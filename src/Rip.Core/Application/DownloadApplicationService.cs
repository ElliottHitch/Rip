using Rip.Domain;

namespace Rip.Application;

public enum DownloadRunStatus
{
    MetadataSucceeded,
    Published,
    Cancelled,
    Failed
}

/// <summary>
/// The safe result of one application-service invocation. It never contains staged inputs or a
/// media plan; a local location is present only when publication returned a verified final file.
/// </summary>
public sealed record DownloadRunResult
{
    private DownloadRunResult(
        RunIdentity run,
        DownloadRunStatus status,
        MetadataSnapshot? metadata,
        VerifiedLocalMp4? publishedFile,
        SafeDownloadError? error,
        bool cleanupComplete)
    {
        Run = run;
        Status = status;
        Metadata = metadata;
        PublishedFile = publishedFile;
        Error = error;
        CleanupComplete = cleanupComplete;

        if (status == DownloadRunStatus.MetadataSucceeded && (metadata is null || publishedFile is not null || error is not null))
        {
            throw new ArgumentException("Metadata results contain metadata only.", nameof(metadata));
        }

        if (status == DownloadRunStatus.Published && (publishedFile is null || error is not null))
        {
            throw new ArgumentException("Published results require a verified local file.", nameof(publishedFile));
        }

        if (status is DownloadRunStatus.Cancelled or DownloadRunStatus.Failed && error is null)
        {
            throw new ArgumentException("Non-success results require a safe error.", nameof(error));
        }
    }

    public RunIdentity Run { get; }
    public DownloadRunStatus Status { get; }
    public DownloadRunStatus Outcome => Status;
    public MetadataSnapshot? Metadata { get; }
    public MetadataSnapshot? MetadataSnapshot => Metadata;
    public VerifiedLocalMp4? PublishedFile { get; }
    public VerifiedLocalMp4? VerifiedFile => PublishedFile;
    public SafeDownloadError? Error { get; }
    public SafeDownloadError? Failure => Error;
    public bool CleanupComplete { get; }
    public bool IsMetadataSuccess => Status == DownloadRunStatus.MetadataSucceeded;
    public bool IsPublished => Status == DownloadRunStatus.Published;
    public bool IsCancelled => Status == DownloadRunStatus.Cancelled;
    public bool IsFailed => Status == DownloadRunStatus.Failed;

    internal static DownloadRunResult MetadataSuccess(RunIdentity run, MetadataSnapshot metadata) =>
        new(run, DownloadRunStatus.MetadataSucceeded, metadata, null, null, cleanupComplete: true);

    internal static DownloadRunResult Published(
        RunIdentity run,
        MetadataSnapshot metadata,
        VerifiedLocalMp4 file,
        bool cleanupComplete) =>
        new(run, DownloadRunStatus.Published, metadata, file, null, cleanupComplete);

    internal static DownloadRunResult Cancelled(
        RunIdentity run,
        MetadataSnapshot? metadata,
        SafeDownloadError error,
        bool cleanupComplete) =>
        new(run, DownloadRunStatus.Cancelled, metadata, null, error, cleanupComplete);

    internal static DownloadRunResult Failed(
        RunIdentity run,
        MetadataSnapshot? metadata,
        SafeDownloadError error,
        bool cleanupComplete) =>
        new(run, DownloadRunStatus.Failed, metadata, null, error, cleanupComplete);
}

/// <summary>
/// Coordinates one metadata, video, or audio operation without owning any concrete side effect.
/// </summary>
public sealed class DownloadApplicationService
{
    private static readonly string[] Activities =
    [
        "validating",
        "metadata",
        "resolving",
        "staging",
        "processing",
        "publishing"
    ];

    private readonly IVideoProvider provider;
    private readonly ILocalStreamStager stager;
    private readonly IStagedMediaProcessor processor;
    private readonly IPublicationStore publisher;
    private readonly IDownloadObserver observer;
    private readonly IDiagnostics diagnostics;

    public DownloadApplicationService(
        IVideoProvider provider,
        ILocalStreamStager stager,
        IStagedMediaProcessor processor,
        IPublicationStore publisher,
        IDownloadObserver observer,
        IDiagnostics diagnostics)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.stager = stager ?? throw new ArgumentNullException(nameof(stager));
        this.processor = processor ?? throw new ArgumentNullException(nameof(processor));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public ValueTask<DownloadRunResult> RunAsync(DownloadRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, cancellationToken);

    public async ValueTask<DownloadRunResult> ExecuteAsync(
        DownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        var run = RunIdentity.New();
        var sequence = new SequenceCounter();
        MetadataSnapshot? metadata = null;

        await ProgressAsync(run, DownloadStage.Validating, sequence).ConfigureAwait(false);
        if (!IsValidRequest(request))
        {
            return await FailAsync(
                run,
                DownloadStage.Validating,
                SafeDownloadErrors.InvalidRequest(),
                metadata,
                cleanupComplete: true,
                sequence).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return await CancelAsync(run, DownloadStage.Validating, metadata, cleanupComplete: true, sequence)
                .ConfigureAwait(false);
        }

        await ProgressAsync(run, DownloadStage.Metadata, sequence).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            return await CancelAsync(run, DownloadStage.Metadata, metadata, cleanupComplete: true, sequence)
                .ConfigureAwait(false);
        }

        ProviderResult<MetadataSnapshot> metadataResult;
        try
        {
            metadataResult = await provider.ReadMetadataAsync(
                request.Video,
                request.BrowserSession,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await CancelAsync(run, DownloadStage.Metadata, metadata, cleanupComplete: true, sequence)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await FailAsync(
                run,
                DownloadStage.Metadata,
                SafeDownloadErrors.Unexpected(DownloadStage.Metadata),
                metadata,
                cleanupComplete: true,
                sequence).ConfigureAwait(false);
        }

        if (metadataResult.Error is not null)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return await CancelAsync(run, DownloadStage.Metadata, metadata, cleanupComplete: true, sequence)
                    .ConfigureAwait(false);
            }

            return await CompleteErrorAsync(run, DownloadStage.Metadata, metadataResult.Error, metadata, sequence)
                .ConfigureAwait(false);
        }

        if (metadataResult.Value is null)
        {
            return await FailAsync(
                run,
                DownloadStage.Metadata,
                SafeDownloadErrors.InvalidMetadata(),
                metadata,
                cleanupComplete: true,
                sequence).ConfigureAwait(false);
        }

        metadata = SafeMetadata(metadataResult.Value);
        if (cancellationToken.IsCancellationRequested)
        {
            return await CancelAsync(run, DownloadStage.Metadata, metadata, cleanupComplete: true, sequence)
                .ConfigureAwait(false);
        }

        if (request.Operation == DownloadOperation.Metadata)
        {
            await TerminalAsync(
                new DownloadMetadataCompleted(run, DownloadStage.Metadata, sequence.Next(), metadata)).ConfigureAwait(false);
            return DownloadRunResult.MetadataSuccess(run, metadata);
        }

        await ProgressAsync(run, DownloadStage.Resolving, sequence).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            return await CancelAsync(run, DownloadStage.Resolving, metadata, cleanupComplete: true, sequence)
                .ConfigureAwait(false);
        }

        ProviderResult<MediaPlan> planResult;
        try
        {
            planResult = await provider.ResolveMediaAsync(request, metadata, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await CancelAsync(run, DownloadStage.Resolving, metadata, cleanupComplete: true, sequence)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await FailAsync(
                run,
                DownloadStage.Resolving,
                SafeDownloadErrors.Unexpected(DownloadStage.Resolving),
                metadata,
                cleanupComplete: true,
                sequence).ConfigureAwait(false);
        }

        if (planResult.Error is not null)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return await CancelAsync(run, DownloadStage.Resolving, metadata, cleanupComplete: true, sequence)
                    .ConfigureAwait(false);
            }

            return await CompleteErrorAsync(run, DownloadStage.Resolving, planResult.Error, metadata, sequence)
                .ConfigureAwait(false);
        }

        if (planResult.Value is null)
        {
            return await FailAsync(
                run,
                DownloadStage.Resolving,
                SafeDownloadErrors.Unexpected(DownloadStage.Resolving),
                metadata,
                cleanupComplete: true,
                sequence).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return await CancelAsync(run, DownloadStage.Resolving, metadata, cleanupComplete: true, sequence)
                .ConfigureAwait(false);
        }

        if (planResult.Value.Request is null)
        {
            return await FailAsync(
                run,
                DownloadStage.Resolving,
                SafeDownloadErrors.Unexpected(DownloadStage.Resolving),
                metadata,
                cleanupComplete: true,
                sequence).ConfigureAwait(false);
        }

        // The typed browser selection remains available to the local yt-dlp staging adapter.
        // It contains only a browser kind, never cookies or profile paths.
        var executionPlan = planResult.Value;

        var selectionText = executionPlan.VideoHeight is { } selectedHeight
            ? $"Downloading {selectedHeight}p video and audio" : "Downloading selected media";
        if (executionPlan.IsProgressive) selectionText += " (combined source; separate streams unavailable)";
        await TerminalAsync(new DownloadProgress(run, DownloadStage.Downloading, sequence.Next(), double.NaN, selectionText)).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            return await CancelAsync(run, DownloadStage.Downloading, metadata, cleanupComplete: true, sequence)
                .ConfigureAwait(false);
        }

        ProviderResult<LocalMediaInputs> stageResult;
        var streamRefreshAttempts = 0;
        while (true)
        {
            try
            {
                stageResult = await stager.StageAsync(executionPlan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await CancelAsync(run, DownloadStage.Downloading, metadata, cleanupComplete: true, sequence)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await FailAsync(
                    run,
                    DownloadStage.Downloading,
                    SafeDownloadErrors.Unexpected(DownloadStage.Downloading),
                    metadata,
                    cleanupComplete: true,
                    sequence).ConfigureAwait(false);
            }

            if (stageResult.Error is null ||
                !RetryPolicy.Decide(new(
                    stageResult.Error.Code,
                    DownloadStage.Downloading,
                    streamRefreshAttempts)).ShouldRetry)
            {
                break;
            }

            streamRefreshAttempts++;
            await ProgressAsync(run, DownloadStage.Resolving, sequence).ConfigureAwait(false);
            ProviderResult<MediaPlan> refreshed;
            try
            {
                refreshed = await provider.ResolveMediaAsync(request, metadata, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await CancelAsync(run, DownloadStage.Resolving, metadata, cleanupComplete: true, sequence)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await FailAsync(
                    run,
                    DownloadStage.Resolving,
                    SafeDownloadErrors.ProviderRefreshFailed(),
                    metadata,
                    cleanupComplete: true,
                    sequence).ConfigureAwait(false);
            }
            if (refreshed.Error is not null || refreshed.Value is null)
            {
                stageResult = refreshed.Error is not null
                    ? new ProviderResult<LocalMediaInputs>(null, refreshed.Error)
                    : new ProviderResult<LocalMediaInputs>(null, SafeDownloadErrors.Unexpected(DownloadStage.Resolving));
                break;
            }

            executionPlan = refreshed.Value;
            await ProgressAsync(run, DownloadStage.Downloading, sequence).ConfigureAwait(false);
        }

        if (stageResult.Error is not null)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return await CancelAsync(run, DownloadStage.Downloading, metadata, cleanupComplete: true, sequence)
                    .ConfigureAwait(false);
            }

            return await CompleteErrorAsync(run, DownloadStage.Downloading, stageResult.Error, metadata, sequence)
                .ConfigureAwait(false);
        }

        // Keep the release-finally boundary around every successful stage return, including a
        // malformed/null opaque value. An implementation may have allocated owned state before
        // constructing the returned value, so cleanup must not depend on Core validation passing.
        var inputs = stageResult.Value!;
        var cleanupComplete = true;
        var published = false;
        VerifiedLocalMp4? publishedFile = null;
        StagedArtifact? artifact = null;
        SafeDownloadError? operationError = null;
        var cancelled = false;

        try
        {
            if (!IsValidStagedInputs(executionPlan, inputs))
            {
                operationError = SafeDownloadErrors.InvalidStagedInputs();
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }
            else
            {
                await ProgressAsync(run, DownloadStage.Processing, sequence).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                }
                else
                {
                    ProviderResult<MediaProcessingResult> processingResult;
                    try
                    {
                        processingResult = await processor.ProcessAsync(
                            executionPlan,
                            inputs,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                        processingResult = new ProviderResult<MediaProcessingResult>(
                            null,
                            SafeDownloadErrors.Cancelled(DownloadStage.Processing));
                    }
                    catch (Exception)
                    {
                        operationError = SafeDownloadErrors.Unexpected(DownloadStage.Processing);
                        processingResult = new ProviderResult<MediaProcessingResult>(null, operationError);
                    }

                    if (processingResult.Error is not null)
                    {
                        operationError ??= NormalizeError(processingResult.Error, DownloadStage.Processing);
                        cancelled |= processingResult.Error.Code == DownloadErrorCode.Cancelled;
                    }
                    else if (processingResult.Value is null || !IsValidProcessingResult(processingResult.Value))
                    {
                        operationError = SafeDownloadErrors.InvalidProcessingResult();
                    }
                    else
                    {
                        artifact = SafeArtifact(processingResult.Value.Artifact);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancelled = true;
                        }
                        else
                        {
                            await ProgressAsync(run, DownloadStage.Publishing, sequence).ConfigureAwait(false);
                            if (cancellationToken.IsCancellationRequested)
                            {
                                cancelled = true;
                            }
                            else
                            {
                                ProviderResult<VerifiedLocalMp4> publicationResult;
                                try
                                {
                                    publicationResult = await publisher.PublishAsync(
                                        processingResult.Value.Artifact,
                                        request.Output with
                                        {
                                            FileStem = SafeFileNamePolicy.Normalize(request.Output.FileStem == "download" ? metadata.Title : request.Output.FileStem),
                                            AllowOverwrite = false
                                        },
                                        cancellationToken).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    cancelled = true;
                                    publicationResult = new ProviderResult<VerifiedLocalMp4>(
                                        null,
                                        SafeDownloadErrors.Cancelled(DownloadStage.Publishing));
                                }
                                catch (Exception)
                                {
                                    publicationResult = new ProviderResult<VerifiedLocalMp4>(
                                        null,
                                        SafeDownloadErrors.Unexpected(DownloadStage.Publishing));
                                }

                                if (publicationResult.Error is not null)
                                {
                                    operationError = NormalizeError(publicationResult.Error, DownloadStage.Publishing);
                                    cancelled |= publicationResult.Error.Code == DownloadErrorCode.Cancelled;
                                }
                                else if (publicationResult.Value is null || !IsValidPublishedFile(publicationResult.Value))
                                {
                                    operationError = SafeDownloadErrors.InvalidPublicationResult();
                                }
                                else
                                {
                                    published = true;
                                    publishedFile = publicationResult.Value;
                                }
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ProviderResult<StageReleaseResult> releaseResult;
            try
            {
                releaseResult = await stager.ReleaseAsync(inputs, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                releaseResult = new ProviderResult<StageReleaseResult>(null, SafeDownloadErrors.CleanupIncomplete());
            }

            cleanupComplete = releaseResult.Error is null && releaseResult.Value is { CleanupComplete: true };
            if (!cleanupComplete)
            {
                TryReport(SafeDownloadErrors.CleanupIncomplete());
            }
        }

        if (published)
        {
            var result = DownloadRunResult.Published(run, metadata, publishedFile!, cleanupComplete);
            await TerminalAsync(new DownloadCompleted(run, DownloadStage.Publishing, sequence.Next(), artifact!, cleanupComplete))
                .ConfigureAwait(false);
            return result;
        }

        if (cancelled || cancellationToken.IsCancellationRequested || operationError?.Code == DownloadErrorCode.Cancelled)
        {
            return await CancelAsync(
                run,
                operationError?.Stage ?? DownloadStage.Processing,
                metadata,
                cleanupComplete,
                sequence,
                operationError).ConfigureAwait(false);
        }

        return await FailAsync(
            run,
            operationError?.Stage ?? DownloadStage.Processing,
            operationError ?? SafeDownloadErrors.Unexpected(DownloadStage.Processing),
            metadata,
            cleanupComplete,
            sequence).ConfigureAwait(false);
    }

    private async ValueTask<DownloadRunResult> CompleteErrorAsync(
        RunIdentity run,
        DownloadStage stage,
        SafeDownloadError error,
        MetadataSnapshot? metadata,
        SequenceCounter sequence)
    {
        var safeError = NormalizeError(error, stage);
        TryReport(safeError);
        if (safeError.Code == DownloadErrorCode.Cancelled)
        {
            await TerminalAsync(new DownloadCancelled(run, stage, sequence.Next())).ConfigureAwait(false);
            return DownloadRunResult.Cancelled(run, metadata, safeError, cleanupComplete: true);
        }

        await TerminalAsync(new DownloadFailed(run, stage, sequence.Next(), safeError)).ConfigureAwait(false);
        return DownloadRunResult.Failed(run, metadata, safeError, cleanupComplete: true);
    }

    private async ValueTask<DownloadRunResult> FailAsync(
        RunIdentity run,
        DownloadStage stage,
        SafeDownloadError error,
        MetadataSnapshot? metadata,
        bool cleanupComplete,
        SequenceCounter sequence)
    {
        var safeError = NormalizeError(error, stage);
        TryReport(safeError);
        await TerminalAsync(new DownloadFailed(run, stage, sequence.Next(), safeError)).ConfigureAwait(false);
        return DownloadRunResult.Failed(run, metadata, safeError, cleanupComplete);
    }

    private async ValueTask<DownloadRunResult> CancelAsync(
        RunIdentity run,
        DownloadStage stage,
        MetadataSnapshot? metadata,
        bool cleanupComplete,
        SequenceCounter sequence,
        SafeDownloadError? existingError = null)
    {
        var safeError = existingError is { Code: DownloadErrorCode.Cancelled }
            ? NormalizeError(existingError, stage)
            : SafeDownloadErrors.Cancelled(stage);
        TryReport(safeError);
        await TerminalAsync(new DownloadCancelled(run, stage, sequence.Next())).ConfigureAwait(false);
        return DownloadRunResult.Cancelled(run, metadata, safeError, cleanupComplete);
    }

    private async ValueTask ProgressAsync(RunIdentity run, DownloadStage stage, SequenceCounter sequence)
    {
        var activity = stage switch
        {
            DownloadStage.Validating => Activities[0],
            DownloadStage.Metadata => Activities[1],
            DownloadStage.Resolving => Activities[2],
            DownloadStage.Downloading => Activities[3],
            DownloadStage.Processing => Activities[4],
            DownloadStage.Publishing => Activities[5],
            _ => "working"
        };
        await TerminalAsync(new DownloadProgress(run, stage, sequence.Next(), double.NaN, activity)).ConfigureAwait(false);
    }

    private async ValueTask TerminalAsync(DownloadEvent @event)
    {
        try
        {
            await observer.ObserveAsync(@event, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            TryReport(SafeDownloadErrors.ObserverFailed());
        }
    }

    private void TryReport(SafeDownloadError error)
    {
        try
        {
            diagnostics.Report(error);
        }
        catch (Exception)
        {
            // Diagnostics is deliberately best-effort; its failure cannot change the operation truth.
        }
    }

    private static bool IsValidRequest(DownloadRequest? request)
    {
        if (request is null ||
            request.Video.Address is not { IsAbsoluteUri: true } address ||
            address.Scheme is not ("http" or "https") ||
            !Enum.IsDefined(request.Operation) ||
            request.Output is null ||
            string.IsNullOrWhiteSpace(request.Output.Directory) ||
            string.IsNullOrWhiteSpace(request.Output.FileStem) ||
            !Enum.IsDefined(request.Output.Container) || request.Output.MaximumVideoHeight is <= 0)
        {
            return false;
        }

        try
        {
            // FrameRatePolicy is the sole authority for the bounded, typed target contract.
            _ = FrameRatePolicy.Decide(
                sourceFrameRate: null,
                requestedFrameRate: request.Output.FrameRateTarget,
                unifiCompatible: request.Output.UnifiCompatible);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool IsValidStagedInputs(MediaPlan? plan, LocalMediaInputs? inputs)
    {
        if (inputs is null || plan is null || plan.Request is null || plan.Characteristics is null)
        {
            return false;
        }

        if (plan.IsProgressive)
        {
            return plan.Characteristics.HasVideo && plan.Characteristics.HasAudio &&
                !string.IsNullOrWhiteSpace(plan.VideoFormatId) && plan.AudioFormatId is null &&
                inputs.Video is { Channel: LocalMediaChannel.Video, Verified: true, LengthBytes: > 0 } &&
                inputs.Audio is null;
        }

        var handles = new List<LocalMediaInputHandle>(2);
        if (inputs.Video is not null)
        {
            if (!plan.Characteristics.HasVideo ||
                string.IsNullOrWhiteSpace(plan.VideoFormatId) ||
                !IsValidStagedHandle(inputs.Video, LocalMediaChannel.Video) ||
                !TryAddUniqueHandle(inputs.Video, handles))
            {
                return false;
            }
        }
        else if (plan.Characteristics.HasVideo || plan.VideoFormatId is not null)
        {
            return false;
        }

        if (inputs.Audio is not null)
        {
            if (!plan.Characteristics.HasAudio ||
                string.IsNullOrWhiteSpace(plan.AudioFormatId) ||
                !IsValidStagedHandle(inputs.Audio, LocalMediaChannel.Audio) ||
                !TryAddUniqueHandle(inputs.Audio, handles))
            {
                return false;
            }
        }
        else if (plan.Characteristics.HasAudio || plan.AudioFormatId is not null)
        {
            return false;
        }

        return handles.Count > 0;
    }

    private static bool IsValidStagedHandle(LocalMediaInputHandle handle, LocalMediaChannel expectedChannel) =>
        handle.Channel == expectedChannel &&
        handle.Verified &&
        handle.LengthBytes > 0 &&
        !string.IsNullOrWhiteSpace(handle.InputKey);

    private static bool TryAddUniqueHandle(
        LocalMediaInputHandle handle,
        List<LocalMediaInputHandle> handles)
    {
        if (handles.Any(existing => existing.InputKey == handle.InputKey))
        {
            return false;
        }

        handles.Add(handle);
        return true;
    }

    private static bool IsValidProcessingResult(MediaProcessingResult result) =>
        result.Artifact is not null && result.Verified && result.Artifact.Verified &&
        result.Artifact.LengthBytes > 0 &&
        !string.IsNullOrWhiteSpace(result.Artifact.StagingKey) &&
        !string.IsNullOrWhiteSpace(result.Artifact.FileName);

    private static bool IsValidPublishedFile(VerifiedLocalMp4 file) =>
        !string.IsNullOrWhiteSpace(file.FileName) &&
        !string.IsNullOrWhiteSpace(file.OutputKey) &&
        file.LengthBytes > 0;

    private static MetadataSnapshot SafeMetadata(MetadataSnapshot source) =>
        new(
            SafeText(source.Title, "Untitled", 256),
            source.Duration is { } duration && duration >= TimeSpan.Zero ? duration : null,
            source.Uploader is null ? null : SafeText(source.Uploader, "Unknown uploader", 256),
            source.PublishedAt);

    private static StagedArtifact SafeArtifact(StagedArtifact source) =>
        new(
            SafeOpaqueIdentifier(source.StagingKey, "stage-result"),
            SafeFileName(source.FileName),
            source.Container is OutputContainer.Mp4 or OutputContainer.UnifiMp4 or OutputContainer.Matroska ? source.Container : OutputContainer.Mp4,
            source.LengthBytes > 0 ? source.LengthBytes : 1,
            source.Verified);

    private static string SafeOpaqueIdentifier(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(static character => !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            return fallback;
        }

        return value;
    }

    private static string SafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Contains('/') || value.Contains('\\'))
        {
            return "download.mp4";
        }

        return SafeFileNamePolicy.Normalize(value);
    }

    private static string SafeText(string? value, string fallback, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            var safe = ErrorRedactor.Redact(value);
            return safe.Length <= maximumLength ? safe : safe[..maximumLength];
        }
        catch (ArgumentException)
        {
            return fallback;
        }
    }

    private static SafeDownloadError NormalizeError(SafeDownloadError error, DownloadStage fallbackStage)
    {
        ArgumentNullException.ThrowIfNull(error);
        var message = SafeText(error.UserMessage, "The download operation failed.", 256);
        return SafeDownloadError.Create(
            error.Code,
            Enum.IsDefined(error.Stage) ? error.Stage : fallbackStage,
            message,
            Enum.IsDefined(error.Retry) ? error.Retry : RetryAction.None,
            error.Diagnostic);
    }

    private sealed class SequenceCounter
    {
        private long value;

        public long Next() => ++value;
    }
}
