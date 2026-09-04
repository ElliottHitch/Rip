using System.Globalization;
using System.Security;
using UnifiDownloader.Application;
using UnifiDownloader.Domain;

namespace UnifiDownloader.Infrastructure;

/// <summary>
/// A path-only input set for the external FFmpeg boundary. The paths are intentionally
/// not part of Core contracts and are validated again by <see cref="FfmpegProcessAdapter"/>.
/// </summary>
public sealed record FfmpegInputSet(string? VideoPath = null, string? AudioPath = null);

/// <summary>
/// An application-owned staging root. The adapter allocates a fresh opaque MP4 name below it;
/// callers cannot provide an output path or reuse the final destination as the process target.
/// </summary>
public sealed record FfmpegStageTarget(string StageRoot);

/// <summary>
/// Runs the explicitly external, trusted FFmpeg executable against already-staged local streams.
/// This is an Infrastructure seam and deliberately does not implement Core's current
/// <c>IMediaProcessor</c> port: that port has no approved local-input/stage allocation contract.
/// </summary>
public sealed class FfmpegProcessAdapter
{
    private static readonly string FfmpegExecutableKey = ToolKey.Ffmpeg.ToString();

    private const int MaximumStageAllocationAttempts = 8;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly BoundedProcessExecutor executor;
    private readonly TimeSpan timeout;
    private readonly StagedArtifactRegistry? stagedRegistry;

    public FfmpegProcessAdapter(
        BoundedProcessExecutor executor,
        TimeSpan? timeout = null,
        StagedArtifactRegistry? stagedRegistry = null)
    {
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.timeout = timeout ?? DefaultTimeout;
        this.stagedRegistry = stagedRegistry;
        if (this.timeout <= TimeSpan.Zero && this.timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    /// <summary>
    /// Processes local input handles into a new MP4 below the supplied staging root.
    /// No address, browser session, final destination, or raw child output is used here.
    /// </summary>
    public async ValueTask<ProviderResult<MediaProcessingResult>> ProcessAsync(
        MediaPlan plan,
        FfmpegInputSet inputs,
        FfmpegStageTarget stage,
        CancellationToken cancellationToken)
    {
        string? outputPath = null;
        var succeeded = false;
        try
        {
            if (plan is null || inputs is null || stage is null)
            {
                return Failure<MediaProcessingResult>(SafeInfrastructureErrors.InvalidMediaProcessingRequest());
            }

            if (!TryValidatePlan(plan, inputs, stage, out var characteristics, out var decision, out var frameRate))
            {
                return Failure<MediaProcessingResult>(SafeInfrastructureErrors.InvalidMediaProcessingRequest());
            }

            var strategy = decision.Strategy;
            if (frameRate.RequiresConversion && strategy is EncodingStrategy.Passthrough or EncodingStrategy.Remux)
            {
                strategy = EncodingStrategy.Transcode;
            }

            if (strategy == EncodingStrategy.Passthrough)
            {
                // A caller must not route a no-op through an adapter whose responsibility is
                // process execution. The current Core policy says no media processor is needed.
                return Failure<MediaProcessingResult>(SafeInfrastructureErrors.UnsupportedMediaProcessingFormat());
            }

            if (!TryAllocateOutput(stage.StageRoot, plan.Request.Output.Container, out var stagingKey, out var fileName, out outputPath))
            {
                return Failure<MediaProcessingResult>(SafeInfrastructureErrors.InvalidMediaProcessingRequest());
            }

            var arguments = FfmpegCommandBuilder.Build(
                inputs,
                characteristics,
                strategy,
                frameRate,
                outputPath);
            var process = await executor.ExecuteCapturedAsync(
                new ProcessSpec(FfmpegExecutableKey, arguments, timeout),
                cancellationToken).ConfigureAwait(false);

            if (process.Error is not null)
            {
                return Failure<MediaProcessingResult>(process.Error);
            }

            var captured = process.Value!;
            if (captured.ExitCode != 0)
            {
                return Failure<MediaProcessingResult>(BoundedProcessExecutor.ClassifyNonzeroExit(captured));
            }

            if (!IsVerifiedOutput(outputPath, out var lengthBytes))
            {
                return Failure<MediaProcessingResult>(SafeInfrastructureErrors.OutputVerificationFailed());
            }

            var artifact = new StagedArtifact(
                stagingKey,
                fileName,
                plan.Request.Output.Container,
                lengthBytes,
                Verified: true);
            if (stagedRegistry is not null && !stagedRegistry.Register(artifact, outputPath))
            {
                return Failure<MediaProcessingResult>(SafeInfrastructureErrors.OutputVerificationFailed());
            }
            succeeded = true;
            return new ProviderResult<MediaProcessingResult>(
                new MediaProcessingResult(artifact, Verified: true),
                null);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            return Failure<MediaProcessingResult>(SafeInfrastructureErrors.InvalidMediaProcessingRequest());
        }
        finally
        {
            // A failed process or failed verification never leaves an unpublishable stage file.
            if (outputPath is not null && !succeeded)
            {
                TryDelete(outputPath);
            }
        }
    }

    private static bool TryValidatePlan(
        MediaPlan plan,
        FfmpegInputSet inputs,
        FfmpegStageTarget stage,
        out MediaCharacteristics characteristics,
        out FormatDecision decision,
        out FrameRateDecision frameRate)
    {
        characteristics = plan.Characteristics;
        decision = default!;
        frameRate = default!;

        if (plan.Request is null || plan.Request.Output is null || characteristics is null ||
            !Enum.IsDefined(plan.Request.Output.Container) ||
            !Enum.IsDefined(plan.Request.Operation) ||
            plan.Request.Operation == DownloadOperation.Metadata)
        {
            return false;
        }

        if (!TryGetAbsoluteDirectory(stage.StageRoot, out var stageRoot) ||
            !IsSafeStageRoot(stageRoot, plan.Request.Output.Directory))
        {
            return false;
        }

        string? videoPath = null;
        string? audioPath = null;
        if (plan.IsProgressive)
        {
            if (!characteristics.HasVideo || !characteristics.HasAudio ||
                plan.VideoSource is null || plan.AudioSource is not null ||
                !TryValidateRegularFile(inputs.VideoPath, out var progressiveVideo) ||
                inputs.AudioPath is not null)
            {
                return false;
            }

            videoPath = progressiveVideo;
            audioPath = null;
        }
        else if (!TryValidateChannel(
                characteristics.HasVideo,
                plan.VideoSource is not null,
                inputs.VideoPath,
                out videoPath) ||
            !TryValidateChannel(
                characteristics.HasAudio,
                plan.AudioSource is not null,
                inputs.AudioPath,
                out audioPath))
        {
            return false;
        }

        if (!characteristics.HasVideo && !characteristics.HasAudio)
        {
            return false;
        }

        // Frame rates are a video-only concern. Reject the request here, before any process
        // allocation or execution, rather than silently applying a video option to audio.
        if (!characteristics.HasVideo && plan.Request.Output.FrameRateTarget is not null)
        {
            return false;
        }

        if (!plan.IsProgressive && videoPath is not null && audioPath is not null && PathsEqual(videoPath, audioPath))
        {
            return false;
        }

        try
        {
            decision = FormatPolicy.Decide(plan.Request.Output.Container, characteristics);
            frameRate = FrameRatePolicy.Decide(
                characteristics.FrameRate,
                plan.Request.Output.FrameRateTarget,
                plan.Request.Output.UnifiCompatible);
        }
        catch (ArgumentException)
        {
            return false;
        }

        // A zero/negative source value is not a usable media plan.
        if (frameRate.EffectiveFrameRate is { } effective && effective <= 0)
        {
            return false;
        }

        return decision.Strategy switch
        {
            EncodingStrategy.Passthrough => characteristics.HasVideo && characteristics.HasAudio,
            EncodingStrategy.Remux => characteristics.HasVideo || characteristics.HasAudio,
            EncodingStrategy.Transcode => true,
            _ => false
        };
    }

    private static bool TryValidateChannel(
        bool expected,
        bool sourcePresent,
        string? path,
        out string? validatedPath)
    {
        validatedPath = null;
        if (expected != sourcePresent)
        {
            return false;
        }

        if (!expected)
        {
            return path is null;
        }

        if (!TryValidateRegularFile(path, out var fullPath))
        {
            return false;
        }

        validatedPath = fullPath;
        return true;
    }

    private static bool TryValidateRegularFile(string? candidate, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
        {
            return false;
        }

        // URI-looking values are rejected independently of filesystem existence checks.
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(candidate);
            if (!File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                return false;
            }

            var attributes = File.GetAttributes(fullPath);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool TryGetAbsoluteDirectory(string? candidate, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(fullPath))
            {
                return false;
            }

            var attributes = File.GetAttributes(fullPath);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool IsSafeStageRoot(string stageRoot, string? finalDirectory)
    {
        if (string.IsNullOrWhiteSpace(finalDirectory) || !Path.IsPathFullyQualified(finalDirectory))
        {
            // The final destination is intentionally not used, but a malformed output request
            // is still not a valid processing plan.
            return false;
        }

        try
        {
            var destination = Path.GetFullPath(finalDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return !PathsEqual(stageRoot, destination) &&
                   !IsWithin(stageRoot, destination) &&
                   !IsWithin(destination, stageRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryAllocateOutput(
        string stageRootCandidate,
        OutputContainer container,
        out string stagingKey,
        out string fileName,
        out string outputPath)
    {
        stagingKey = string.Empty;
        fileName = string.Empty;
        outputPath = string.Empty;
        if (!TryGetAbsoluteDirectory(stageRootCandidate, out var stageRoot))
        {
            return false;
        }

        for (var attempt = 0; attempt < MaximumStageAllocationAttempts; attempt++)
        {
            stagingKey = $"stage-{Guid.NewGuid():N}";
            fileName = stagingKey + (container == OutputContainer.Matroska ? ".mkv" : ".mp4");
            outputPath = Path.Combine(stageRoot, fileName);
            if (PathsEqual(outputPath, stageRoot) || PathExists(outputPath))
            {
                continue;
            }

            // The path is deliberately only checked, never created here. FFmpeg's -n option
            // and post-exit verification remain responsible for the actual output.
            return IsWithin(stageRoot, outputPath) && !PathExists(outputPath);
        }

        stagingKey = string.Empty;
        fileName = string.Empty;
        outputPath = string.Empty;
        return false;
    }

    private static bool IsVerifiedOutput(string path, out long lengthBytes)
    {
        lengthBytes = 0;
        try
        {
            if (!File.Exists(path) || Directory.Exists(path))
            {
                return false;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            var info = new FileInfo(path);
            lengthBytes = info.Length;
            return lengthBytes > 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            return false;
        }
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

    private static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(normalizedRoot, PathComparison);
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Cleanup cannot safely be reported with a path; leave the process result typed.
        }
    }

    private static ProviderResult<T> Failure<T>(SafeDownloadError error) => new(default, error);

    private static class FfmpegCommandBuilder
    {
        public static List<string> Build(
            FfmpegInputSet inputs,
            MediaCharacteristics characteristics,
            EncodingStrategy strategy,
            FrameRateDecision frameRate,
            string outputPath)
        {
            var arguments = new List<string>(32)
            {
                "-hide_banner",
                "-nostdin",
                "-loglevel", "error",
                "-n"
            };

            if (inputs.VideoPath is not null)
            {
                arguments.Add("-i");
                arguments.Add(inputs.VideoPath);
            }

            if (inputs.AudioPath is not null &&
                (inputs.VideoPath is null || !PathsEqual(inputs.VideoPath, inputs.AudioPath)))
            {
                arguments.Add("-i");
                arguments.Add(inputs.AudioPath);
            }

            if (inputs.AudioPath is null && characteristics.HasVideo && characteristics.HasAudio)
            {
                arguments.AddRange(["-map", "0:v:0", "-map", "0:a:0"]);
            }
            else if (characteristics.HasVideo && characteristics.HasAudio)
            {
                arguments.AddRange(["-map", "0:v:0", "-map", "1:a:0"]);
            }
            else if (characteristics.HasVideo)
            {
                arguments.AddRange(["-map", "0:v:0"]);
            }
            else
            {
                arguments.AddRange(["-map", "0:a:0"]);
            }

            if (strategy == EncodingStrategy.Remux)
            {
                if (characteristics.HasVideo) arguments.AddRange(["-c:v", "copy"]);
                if (characteristics.HasAudio) arguments.AddRange(["-c:a", "copy"]);
            }
            else
            {
                if (characteristics.HasVideo)
                {
                    arguments.AddRange([
                        "-c:v", "libx264",
                        "-preset", "medium",
                        "-crf", "18",
                        "-b:v", "40000000",
                        "-maxrate", "46000000",
                        "-bufsize", "80000000",
                        "-profile:v", "high",
                        "-pix_fmt", "yuv420p"
                    ]);

                    if (frameRate.RequiresConversion)
                    {
                        var target = frameRate.EffectiveFrameRate;
                        if (target is not (24d or 25d or 30d))
                        {
                            throw new ArgumentOutOfRangeException(nameof(frameRate));
                        }

                        arguments.Add("-r");
                        arguments.Add(target.Value.ToString("0", CultureInfo.InvariantCulture));
                    }
                }

                if (characteristics.HasAudio)
                {
                    arguments.AddRange(["-c:a", "aac", "-b:a", "192000", "-ac", "2"]);
                }
            }

            if (OutputContainerFromPath(outputPath) == "mp4")
            {
                arguments.AddRange(["-movflags", "+faststart", "-f", "mp4", outputPath]);
            }
            else
            {
                arguments.AddRange(["-f", "matroska", outputPath]);
            }
            return arguments;
        }

        private static string OutputContainerFromPath(string outputPath) =>
            Path.GetExtension(outputPath).Equals(".mkv", StringComparison.OrdinalIgnoreCase) ? "matroska" : "mp4";
    }
}
