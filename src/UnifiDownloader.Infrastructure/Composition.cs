using UnifiDownloader.Application;
using UnifiDownloader.Domain;

namespace UnifiDownloader.Infrastructure;

public sealed record InfrastructureConfiguration(
    IReadOnlyDictionary<string, ToolConfiguration> Tools,
    ProcessExecutorOptions? ProcessOptions = null,
    IReadOnlySet<string>? AllowedToolRepositories = null,
    IReadOnlyDictionary<ToolKey, ToolExpectation>? TrustedToolExpectations = null,
    string? ExecutionTargetRid = null);

public sealed record InfrastructureServices(
    IProcessExecutor ProcessExecutor,
    IDiagnostics Diagnostics,
    IClock Clock,
    LocalCapabilityProbe Capabilities);

/// <summary>
/// Explicit local publication wiring. The same ownership registries are deliberately shared by
/// the FFmpeg adapter and publisher; no container or implicit directory migration is involved.
/// </summary>
public sealed record LocalPublicationServices(
    StagedArtifactRegistry StagedArtifacts,
    PublishedOutputRegistry PublishedOutputs,
    FfmpegProcessAdapter Ffmpeg,
    LocalPublicationStore Publisher);

public static class InfrastructureComposition
{
    public static InfrastructureServices Create(
        InfrastructureConfiguration configuration,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var executor = new BoundedProcessExecutor(
            configuration.Tools,
            configuration.ProcessOptions,
            configuration.AllowedToolRepositories,
            configuration.ExecutionTargetRid,
            configuration.TrustedToolExpectations);
        return new InfrastructureServices(
            executor,
            new BoundedDiagnostics(),
            clock ?? new SystemClock(),
            new LocalCapabilityProbe(executor));
    }

    public static LocalPublicationServices CreateLocalPublication(
        BoundedProcessExecutor executor,
        string stagingRoot,
        IDiagnostics diagnostics,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var stagedArtifacts = new StagedArtifactRegistry(stagingRoot);
        var publishedOutputs = new PublishedOutputRegistry();
        var ffmpeg = new FfmpegProcessAdapter(executor, timeout, stagedArtifacts);
        var publisher = new LocalPublicationStore(stagedArtifacts, publishedOutputs, diagnostics);
        return new LocalPublicationServices(stagedArtifacts, publishedOutputs, ffmpeg, publisher);
    }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class BoundedDiagnostics : IDiagnostics
{
    private const int MaximumEntries = 64;
    private const int MaximumMessageCharacters = 512;
    private readonly Queue<SafeDownloadError> errors = new();

    public IReadOnlyList<SafeDownloadError> Errors => errors.ToArray();

    public void Report(SafeDownloadError downloadError)
    {
        ArgumentNullException.ThrowIfNull(downloadError);
        var message = downloadError.UserMessage.Length <= MaximumMessageCharacters
            ? downloadError.UserMessage
            : downloadError.UserMessage[..MaximumMessageCharacters];
        var safe = SafeDownloadError.Create(
            downloadError.Code,
            downloadError.Stage,
            message,
            downloadError.Retry,
            downloadError.Diagnostic);
        if (errors.Count == MaximumEntries) errors.Dequeue();
        errors.Enqueue(safe);
    }
}
