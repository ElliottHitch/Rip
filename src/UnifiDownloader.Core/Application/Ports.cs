using UnifiDownloader.Domain;

namespace UnifiDownloader.Application;

public sealed record ProviderResult<T>(T? Value, SafeDownloadError? Error)
{
    public bool IsSuccess => Error is null;
}

public sealed record ProcessSpec(string ExecutableKey, IReadOnlyList<string> Arguments, TimeSpan Timeout)
{
    public override string ToString() => "[process-spec]";
}

public sealed record ProcessResult
{
    public ProcessResult(int exitCode, bool timedOut, string? safeDiagnosticMessage)
    {
        ExitCode = exitCode;
        TimedOut = timedOut;
        SafeDiagnosticMessage = safeDiagnosticMessage is null ? null : ErrorRedactor.Redact(safeDiagnosticMessage);
    }

    public int ExitCode { get; }
    public bool TimedOut { get; }
    public string? SafeDiagnosticMessage { get; }
}
public sealed record MediaProcessingResult(StagedArtifact Artifact, bool Verified);
public sealed record BrowserSessionLease(BrowserSessionSelection Selection);
public sealed record OpenResult(bool Opened, SafeDownloadError? Error);

public interface IVideoProvider
{
    ValueTask<ProviderResult<MetadataSnapshot>> ReadMetadataAsync(
        VideoReference video,
        BrowserSessionSelection? browserSession,
        CancellationToken cancellationToken);

    ValueTask<ProviderResult<MediaPlan>> ResolveMediaAsync(
        DownloadRequest request,
        MetadataSnapshot metadata,
        CancellationToken cancellationToken);
}

public interface IMediaProcessor
{
    ValueTask<ProviderResult<MediaProcessingResult>> ProcessAsync(
        MediaPlan plan,
        CancellationToken cancellationToken);
}

/// <summary>Obtains local stream files for a resolved media plan without exposing their paths.</summary>
public interface ILocalStreamStager
{
    ValueTask<ProviderResult<LocalMediaInputs>> StageAsync(
        MediaPlan plan,
        CancellationToken cancellationToken);

    ValueTask<ProviderResult<StageReleaseResult>> ReleaseAsync(
        LocalMediaInputs inputs,
        CancellationToken cancellationToken);
}

/// <summary>The additive processing seam between opaque staging and the path-only adapter.</summary>
public interface IStagedMediaProcessor
{
    ValueTask<ProviderResult<MediaProcessingResult>> ProcessAsync(
        MediaPlan plan,
        LocalMediaInputs inputs,
        CancellationToken cancellationToken);
}

public interface IStagingStore
{
    ValueTask<ProviderResult<StagedArtifact>> StageAsync(
        MediaProcessingResult result,
        CancellationToken cancellationToken);
}

public interface IPublicationStore
{
    ValueTask<ProviderResult<VerifiedLocalMp4>> PublishAsync(
        StagedArtifact artifact,
        OutputOptions output,
        CancellationToken cancellationToken);
}

public interface IBrowserSessionSource
{
    ValueTask<ProviderResult<BrowserSessionLease>> AcquireAsync(
        BrowserSessionSelection selection,
        CancellationToken cancellationToken);
}

public interface ILocalFileOpener
{
    ValueTask<OpenResult> OpenAsync(
        VerifiedLocalMp4 file,
        CancellationToken cancellationToken);
}

public interface IProcessExecutor
{
    ValueTask<ProviderResult<ProcessResult>> ExecuteAsync(
        ProcessSpec specification,
        CancellationToken cancellationToken);
}

public interface IDiagnostics
{
    void Report(SafeDownloadError downloadError);
}

public interface IDownloadObserver
{
    ValueTask ObserveAsync(DownloadEvent downloadEvent, CancellationToken cancellationToken);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
