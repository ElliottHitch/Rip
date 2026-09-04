using System.Net;
using System.Security;
using UnifiDownloader.Application;
using UnifiDownloader.Domain;

namespace UnifiDownloader.Infrastructure;

/// <summary>
/// Downloads the HTTP(S) streams in a resolved plan into private Infrastructure-owned files.
/// Core receives only opaque handles; the key-to-path map never leaves this assembly.
/// </summary>
public class LocalStreamStager : ILocalStreamStager
{
    public const long DefaultMaximumResponseBytes = 5L * 1024 * 1024 * 1024;
    private const int CopyBufferSize = 64 * 1024;
    private const int MaximumAllocationAttempts = 8;

    private readonly HttpClient httpClient;
    private readonly BoundedProcessExecutor? ytDlpExecutor;
    private readonly string? knownLocalDenoPath;
    private readonly string stageRootCandidate;
    private readonly long maximumResponseBytes;
    private readonly object gate = new();
    private readonly Dictionary<string, StagedInput> stagedInputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReleasedInput> releasedInputs = new(StringComparer.Ordinal);

    public LocalStreamStager(
        HttpClient httpClient,
        string stageRoot,
        long maximumResponseBytes = DefaultMaximumResponseBytes)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        stageRootCandidate = stageRoot ?? throw new ArgumentNullException(nameof(stageRoot));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResponseBytes);

        this.maximumResponseBytes = maximumResponseBytes;
    }

    /// <summary>Production path: let pinned yt-dlp own signed-stream requests.</summary>
    public LocalStreamStager(
        BoundedProcessExecutor executor,
        string knownLocalDenoPath,
        string stageRoot,
        long maximumResponseBytes = DefaultMaximumResponseBytes)
    {
        ytDlpExecutor = executor ?? throw new ArgumentNullException(nameof(executor));
        if (string.IsNullOrWhiteSpace(knownLocalDenoPath) || !Path.IsPathFullyQualified(knownLocalDenoPath))
            throw new ArgumentException("The JavaScript runtime path must be an absolute local path.", nameof(knownLocalDenoPath));
        this.knownLocalDenoPath = knownLocalDenoPath;
        httpClient = null!;
        stageRootCandidate = stageRoot ?? throw new ArgumentNullException(nameof(stageRoot));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResponseBytes);
        this.maximumResponseBytes = maximumResponseBytes;
    }

    public LocalStreamStager(
        string stageRoot,
        HttpClient httpClient,
        long maximumResponseBytes = DefaultMaximumResponseBytes)
        : this(httpClient, stageRoot, maximumResponseBytes)
    {
    }

    public async ValueTask<ProviderResult<LocalMediaInputs>> StageAsync(
        MediaPlan plan,
        CancellationToken cancellationToken)
    {
        var stageRoot = GetValidatedStageRoot();
        if (stageRoot is null || plan is null)
        {
            return Failure<LocalMediaInputs>(SafeInfrastructureErrors.InvalidLocalStreamRequest());
        }

        var sources = new List<(LocalMediaChannel Channel, MediaSource Source)>();
        if (plan.IsProgressive)
        {
            if (!plan.Characteristics.HasVideo || !plan.Characteristics.HasAudio || plan.VideoSource is null || plan.AudioSource is not null ||
                !IsHttpSource(plan.VideoSource))
            {
                return Failure<LocalMediaInputs>(SafeInfrastructureErrors.InvalidLocalStreamRequest());
            }
            sources.Add((LocalMediaChannel.Video, plan.VideoSource));
        }
        else if (!TryValidatePlan(plan, out sources))
        {
            return Failure<LocalMediaInputs>(SafeInfrastructureErrors.InvalidLocalStreamRequest());
        }

        var createdPaths = new List<string>(sources.Count);
        try
        {
            LocalMediaInputHandle? video = null;
            LocalMediaInputHandle? audio = null;
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var staged = await StageOneAsync(plan, source.Channel, source.Source, stageRoot, cancellationToken)
                    .ConfigureAwait(false);
                if (!staged.IsSuccess)
                {
                    CleanupFilesAndForget(createdPaths);
                    return Failure<LocalMediaInputs>(staged.Error!);
                }

                createdPaths.Add(staged.Path!);
                var handle = staged.Handle!;
                lock (gate)
                {
                    stagedInputs.Add(handle.InputKey, new StagedInput(plan, handle.Channel, staged.Path!));
                }

                if (source.Channel == LocalMediaChannel.Video)
                {
                    video = handle;
                }
                else
                {
                    audio = handle;
                }
            }

            return new ProviderResult<LocalMediaInputs>(new LocalMediaInputs(video, audio), null);
        }
        catch (OperationCanceledException)
        {
            var cleanupCertain = CleanupFilesAndForget(createdPaths);
            return Failure<LocalMediaInputs>(SafeInfrastructureErrors.LocalStreamCancelled(cleanupCertain));
        }
        catch (HttpRequestException)
        {
            _ = CleanupFilesAndForget(createdPaths);
            return Failure<LocalMediaInputs>(SafeInfrastructureErrors.LocalStreamUnavailable());
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            _ = CleanupFilesAndForget(createdPaths);
            return Failure<LocalMediaInputs>(SafeInfrastructureErrors.InvalidLocalStreamRequest());
        }
    }

    public ValueTask<ProviderResult<StageReleaseResult>> ReleaseAsync(
        LocalMediaInputs inputs,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(Failure<StageReleaseResult>(
                SafeInfrastructureErrors.LocalStreamCancelled(cleanupCertain: false)));
        }

        if (!TryGetHandles(inputs, out var handles) || handles.Count == 0)
        {
            return ValueTask.FromResult(Failure<StageReleaseResult>(SafeInfrastructureErrors.StageReleaseFailed()));
        }

        lock (gate)
        {
            var entries = new List<(LocalMediaInputHandle Handle, StagedInput Input)>(handles.Count);
            foreach (var handle in handles)
            {
                if (stagedInputs.TryGetValue(handle.InputKey, out var input))
                {
                    if (!IsMatchingHandle(handle, input))
                    {
                        return ValueTask.FromResult(Failure<StageReleaseResult>(SafeInfrastructureErrors.StageReleaseFailed()));
                    }

                    entries.Add((handle, input));
                    continue;
                }

                if (releasedInputs.TryGetValue(handle.InputKey, out var released) &&
                    IsMatchingReleasedHandle(handle, released))
                {
                    continue;
                }

                return ValueTask.FromResult(Failure<StageReleaseResult>(SafeInfrastructureErrors.StageReleaseFailed()));
            }

            // A previously successful release is deliberately a no-op. The private history
            // is scoped to this stager instance, and the full handle identity is revalidated.
            if (entries.Count == 0)
            {
                return ValueTask.FromResult(new ProviderResult<StageReleaseResult>(
                    new StageReleaseResult(0, cleanupComplete: true), null));
            }

            var cleanupComplete = true;
            var releasedCount = 0;
            foreach (var (handle, input) in entries)
            {
                var deleted = TryDeleteFile(input.Path);
                if (!deleted)
                {
                    cleanupComplete = false;
                }

                if (deleted || !File.Exists(input.Path))
                {
                    stagedInputs.Remove(handle.InputKey);
                    releasedInputs[handle.InputKey] = new ReleasedInput(
                        handle.Channel,
                        handle.LengthBytes,
                        handle.Verified);
                    if (deleted)
                    {
                        releasedCount++;
                    }
                }
            }

            if (!cleanupComplete)
            {
                return ValueTask.FromResult(Failure<StageReleaseResult>(SafeInfrastructureErrors.StageReleaseFailed()));
            }

            return ValueTask.FromResult(new ProviderResult<StageReleaseResult>(
                new StageReleaseResult(releasedCount, cleanupComplete: true), null));
        }
    }

    // This is intentionally internal: only the Infrastructure bridge can turn an issued
    // handle into a path, and it must validate the plan and channel on every lookup.
    internal bool TryResolve(
        MediaPlan plan,
        LocalMediaInputHandle handle,
        out string path)
    {
        path = string.Empty;
        if (plan is null || handle is null || !handle.Verified || handle.LengthBytes <= 0)
        {
            return false;
        }

        lock (gate)
        {
            if (!stagedInputs.TryGetValue(handle.InputKey, out var input) ||
                !IsMatchingHandle(handle, input) ||
                !input.Plan.Equals(plan) ||
                !IsExpectedSource(plan, handle.Channel))
            {
                return false;
            }

            if (!IsVerifiedRegularFileBelowRoot(input.Path, GetValidatedStageRoot()))
            {
                return false;
            }

            path = input.Path;
            return true;
        }
    }

    internal bool Owns(LocalMediaInputHandle handle)
    {
        if (handle is null) return false;
        lock (gate) return stagedInputs.ContainsKey(handle.InputKey);
    }

    private async ValueTask<StageOneResult> StageOneAsync(
        MediaPlan plan,
        LocalMediaChannel channel,
        MediaSource source,
        string stageRoot,
        CancellationToken cancellationToken)
    {
        if (!TryAllocatePath(stageRoot, out var path))
        {
            return StageOneResult.Failure(SafeInfrastructureErrors.InvalidLocalStreamRequest());
        }

        try
        {
            if (ytDlpExecutor is not null)
            {
                return await StageWithYtDlpAsync(plan, channel, path, stageRoot, cancellationToken).ConfigureAwait(false);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, source.Address);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode) ||
                (response.RequestMessage?.RequestUri is { } actualUri && !UriEquals(actualUri, source.Address)))
            {
                TryDeleteFile(path);
                return StageOneResult.Failure(SafeInfrastructureErrors.LocalStreamUnavailable());
            }

            if (!response.IsSuccessStatusCode)
            {
                TryDeleteFile(path);
                return StageOneResult.Failure(SafeInfrastructureErrors.LocalStreamUnavailable());
            }

            if (response.Content.Headers.ContentLength is > 0 and var declaredLength &&
                declaredLength > maximumResponseBytes)
            {
                TryDeleteFile(path);
                return StageOneResult.Failure(SafeInfrastructureErrors.LocalStreamTooLarge());
            }

            if (response.Content.Headers.ContentLength == 0)
            {
                TryDeleteFile(path);
                return StageOneResult.Failure(SafeInfrastructureErrors.LocalStreamEmpty());
            }

            await using (var output = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                var buffer = new byte[CopyBufferSize];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (read > maximumResponseBytes - total)
                    {
                        TryDeleteFile(path);
                        return StageOneResult.Failure(SafeInfrastructureErrors.LocalStreamTooLarge());
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!IsVerifiedRegularFileBelowRoot(path, stageRoot, out var lengthBytes))
            {
                TryDeleteFile(path);
                return StageOneResult.Failure(SafeInfrastructureErrors.LocalStreamEmpty());
            }

            var key = "input-" + Guid.NewGuid().ToString("N");
            var handle = new LocalMediaInputHandle(key, channel, lengthBytes, verified: true);
            return StageOneResult.Success(handle, path);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(path);
            throw;
        }
        catch (HttpRequestException)
        {
            TryDeleteFile(path);
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            TryDeleteFile(path);
            return StageOneResult.Failure(SafeInfrastructureErrors.InvalidLocalStreamRequest());
        }
    }

    private async ValueTask<StageOneResult> StageWithYtDlpAsync(
        MediaPlan plan,
        LocalMediaChannel channel,
        string path,
        string stageRoot,
        CancellationToken cancellationToken)
    {
        var formatId = plan.IsProgressive || channel == LocalMediaChannel.Video
            ? plan.VideoFormatId
            : plan.AudioFormatId;
        if (string.IsNullOrWhiteSpace(formatId))
        {
            TryDeleteFile(path);
            return StageOneResult.Failure(SafeInfrastructureErrors.InvalidLocalStreamRequest());
        }

        var operation = new List<string>
        {
            "--format", formatId,
            "--output", path,
            "--no-playlist",
            "--no-part",
            "--retries", "1",
            "--fragment-retries", "1",
            "--socket-timeout", "30"
        };
        if (plan.Request.BrowserSession is { } browser)
        {
            operation.Add("--cookies-from-browser");
            operation.Add(browser.Kind switch
            {
                BrowserKind.Chromium => "chromium",
                BrowserKind.Chrome => "chrome",
                BrowserKind.Edge => "edge",
                BrowserKind.Firefox => "firefox",
                _ => throw new ArgumentOutOfRangeException(nameof(plan))
            });
        }
        operation.Add(plan.Request.Video.Address.AbsoluteUri);
        var arguments = YtDlpInvocationPolicy.Build(knownLocalDenoPath!, operation);
        var result = await ytDlpExecutor!.ExecuteCapturedAsync(
            new ProcessSpec(ToolKey.YtDlp.ToString(), arguments, TimeSpan.FromMinutes(2)),
            cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            TryDeleteFile(path);
            return StageOneResult.Failure(result.Error);
        }
        if (result.Value!.ExitCode != 0)
        {
            TryDeleteFile(path);
            return StageOneResult.Failure(BoundedProcessExecutor.ClassifyNonzeroExit(result.Value));
        }
        if (!IsVerifiedRegularFileBelowRoot(path, stageRoot, out var lengthBytes) || lengthBytes > maximumResponseBytes)
        {
            TryDeleteFile(path);
            return StageOneResult.Failure(SafeInfrastructureErrors.LocalStreamEmpty());
        }
        return StageOneResult.Success(
            new LocalMediaInputHandle("input-" + Guid.NewGuid().ToString("N"), channel, lengthBytes, verified: true),
            path);
    }

    private string? GetValidatedStageRoot()
    {
        if (string.IsNullOrWhiteSpace(stageRootCandidate) || !Path.IsPathFullyQualified(stageRootCandidate))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(stageRootCandidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root)) return null;
            var attributes = File.GetAttributes(root);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory
                ? root
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            return null;
        }
    }

    private bool TryValidatePlan(MediaPlan plan, out List<(LocalMediaChannel Channel, MediaSource Source)> sources)
    {
        sources = new();
        if (plan is null || plan.Request is null || plan.Characteristics is null ||
            !Enum.IsDefined(plan.Request.Operation) || plan.Request.Operation == DownloadOperation.Metadata)
        {
            return false;
        }

        var characteristics = plan.Characteristics;
        if (!characteristics.HasVideo && !characteristics.HasAudio) return false;
        if (!TryAddSource(plan.VideoSource, characteristics.HasVideo, LocalMediaChannel.Video, sources) ||
            !TryAddSource(plan.AudioSource, characteristics.HasAudio, LocalMediaChannel.Audio, sources))
        {
            return false;
        }

        if (sources.Count == 2 && UriEquals(sources[0].Source.Address, sources[1].Source.Address))
        {
            return false;
        }

        return true;
    }

    private bool TryAddSource(
        MediaSource? source,
        bool expected,
        LocalMediaChannel channel,
        List<(LocalMediaChannel Channel, MediaSource Source)> sources)
    {
        if (expected != (source is not null) || source is null) return !expected;
        if (!IsHttpSource(source) ||
            (source.LengthBytes is { } declaredLength &&
                (declaredLength <= 0 || declaredLength > maximumResponseBytes)))
        {
            return false;
        }

        sources.Add((channel, source));
        return true;
    }

    private static bool IsHttpSource(MediaSource source) =>
        source.Address is not null && source.Address.IsAbsoluteUri &&
        source.Address.Scheme is "http" or "https";

    private static bool IsExpectedSource(MediaPlan plan, LocalMediaChannel channel) => channel switch
    {
        LocalMediaChannel.Video => plan.Characteristics.HasVideo && plan.VideoSource is not null,
        LocalMediaChannel.Audio => plan.Characteristics.HasAudio && plan.AudioSource is not null,
        _ => false
    };

    private static bool TryGetHandles(LocalMediaInputs inputs, out List<LocalMediaInputHandle> handles)
    {
        handles = new();
        if (inputs is null) return false;
        if (inputs.Video is not null) handles.Add(inputs.Video);
        if (inputs.Audio is not null)
        {
            if (handles.Any(handle => handle.InputKey == inputs.Audio.InputKey)) return false;
            handles.Add(inputs.Audio);
        }

        return handles.All(static handle => handle is not null && handle.LengthBytes > 0 && handle.Verified);
    }

    private static bool IsMatchingHandle(LocalMediaInputHandle handle, StagedInput input) =>
        handle.Channel == input.Channel &&
        handle.LengthBytes > 0 &&
        handle.LengthBytes == GetLength(input.Path) &&
        handle.Verified;

    private static bool IsMatchingReleasedHandle(LocalMediaInputHandle handle, ReleasedInput released) =>
        handle.Channel == released.Channel &&
        handle.LengthBytes == released.LengthBytes &&
        handle.Verified == released.Verified;

    private static long GetLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : -1;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            return -1;
        }
    }

    private static bool TryAllocatePath(string stageRoot, out string path)
    {
        path = string.Empty;
        for (var attempt = 0; attempt < MaximumAllocationAttempts; attempt++)
        {
            var name = "input-" + Guid.NewGuid().ToString("N") + ".stream";
            var candidate = Path.Combine(stageRoot, name);
            if (IsWithin(stageRoot, candidate) && !PathExists(candidate))
            {
                path = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsVerifiedRegularFileBelowRoot(string path, string? root) =>
        root is not null && IsVerifiedRegularFileBelowRoot(path, root, out _);

    private static bool IsVerifiedRegularFileBelowRoot(string path, string root, out long lengthBytes)
    {
        lengthBytes = 0;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!IsWithin(root, fullPath) || !File.Exists(fullPath) || Directory.Exists(fullPath)) return false;
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
            lengthBytes = new FileInfo(fullPath).Length;
            return lengthBytes > 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(normalizedRoot, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    private static bool PathExists(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) return true;
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private bool CleanupFilesAndForget(IEnumerable<string> paths)
    {
        var pathList = paths.ToArray();
        var cleanupComplete = pathList.All(TryDeleteFile);
        var pathSet = pathList.ToHashSet(StringComparer.Ordinal);
        lock (gate)
        {
            foreach (var pair in stagedInputs.Where(pair => pathSet.Contains(pair.Value.Path)).ToArray())
            {
                stagedInputs.Remove(pair.Key);
            }
        }

        return cleanupComplete;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        (int)statusCode is >= 300 and <= 399;

    private static bool UriEquals(Uri first, Uri second) =>
        Uri.Compare(first, second, UriComponents.AbsoluteUri, UriFormat.UriEscaped, StringComparison.Ordinal) == 0;

    private static ProviderResult<T> Failure<T>(SafeDownloadError error) => new(default, error);

    private sealed record StagedInput(MediaPlan Plan, LocalMediaChannel Channel, string Path);

    private sealed record ReleasedInput(LocalMediaChannel Channel, long LengthBytes, bool Verified);

    private sealed record StageOneResult(LocalMediaInputHandle? Handle, string? Path, SafeDownloadError? Error)
    {
        public bool IsSuccess => Error is null;
        public static StageOneResult Success(LocalMediaInputHandle handle, string path) => new(handle, path, null);
        public static StageOneResult Failure(SafeDownloadError error) => new(null, null, error);
    }
}

/// <summary>Descriptive alias for callers that prefer the transport-specific name.</summary>
public sealed class HttpLocalStreamStager : LocalStreamStager
{
    public HttpLocalStreamStager(HttpClient httpClient, string stageRoot, long maximumResponseBytes = DefaultMaximumResponseBytes)
        : base(httpClient, stageRoot, maximumResponseBytes)
    {
    }

    public HttpLocalStreamStager(string stageRoot, HttpClient httpClient, long maximumResponseBytes = DefaultMaximumResponseBytes)
        : base(httpClient, stageRoot, maximumResponseBytes)
    {
    }
}
