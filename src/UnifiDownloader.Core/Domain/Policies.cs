namespace UnifiDownloader.Domain;

public sealed record OneVideoValidation(bool IsValid, string? Reason)
{
    public static OneVideoValidation Valid { get; } = new(true, null);
}

public static class OneVideoPolicy
{
    public static OneVideoValidation Validate(IReadOnlyCollection<DownloadRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return requests.Count == 1
            ? OneVideoValidation.Valid
            : new(false, "Exactly one video request is required.");
    }
}

public enum EncodingStrategy
{
    Passthrough,
    Remux,
    Transcode
}

public sealed record FormatDecision(
    OutputContainer Target,
    EncodingStrategy Strategy,
    bool RequiresMediaProcessor,
    string Reason);

public static class FormatPolicy
{
    public static FormatDecision Decide(OutputContainer target, MediaCharacteristics source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var compatibleMp4 = source.HasVideo && source.HasAudio
            && source.VideoCodec == VideoCodec.H264
            && source.AudioCodec == AudioCodec.Aac;

        // Matroska is the lossless general-purpose target. It is deliberately used for
        // arbitrary source codecs rather than silently converting them to the compatibility
        // profile. A mux is still required to combine separate streams.
        if (target == OutputContainer.Matroska)
        {
            return new(target, EncodingStrategy.Remux, source.HasVideo || source.HasAudio,
                "Combine the selected streams losslessly in a codec-neutral Matroska container.");
        }

        if (source.SourceContainer == target && compatibleMp4)
        {
            return new(target, EncodingStrategy.Passthrough, false, "The source already satisfies the target MP4 contract.");
        }

        return compatibleMp4
            ? new(target, EncodingStrategy.Remux, true, "The streams are compatible but the container must be changed.")
            : new(target, EncodingStrategy.Transcode, true, "The source codecs require conversion for the target MP4 contract.");
    }
}

public sealed record FrameRateDecision(double? EffectiveFrameRate, bool RequiresConversion, string Reason);

public static class FrameRatePolicy
{
    public static FrameRateDecision Decide(
        double? sourceFrameRate,
        double? requestedFrameRate,
        bool unifiCompatible = false)
    {
        if (sourceFrameRate is { } source)
        {
            if (!double.IsFinite(source))
            {
                throw new ArgumentOutOfRangeException(nameof(sourceFrameRate), "Frame rate must be finite.");
            }

            if (source <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceFrameRate), "Frame rate must be greater than zero.");
            }
        }

        if (requestedFrameRate is { } requested && !double.IsFinite(requested))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedFrameRate), "Frame rate must be finite.");
        }

        if (requestedFrameRate is null)
        {
            if (unifiCompatible)
            {
                if (sourceFrameRate is 24d or 25d or 30d)
                {
                    return new(sourceFrameRate, false, "The source frame rate is already in the UniFi compatibility set.");
                }

                return new(30d, true, "Use the safe UniFi compatibility frame rate of 30 FPS.");
            }

            return new(sourceFrameRate, false, "Preserve the source frame rate.");
        }

        if (requestedFrameRate.Value != 24d
            && requestedFrameRate.Value != 25d
            && requestedFrameRate.Value != 30d)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedFrameRate), "Frame rate targets must be 24, 25, or 30 FPS.");
        }

        return sourceFrameRate == requestedFrameRate
            ? new(requestedFrameRate, false, "The requested frame rate is already satisfied.")
            : new(requestedFrameRate, true, "Convert to the requested frame rate.");
    }
}

public sealed record RetryContext(DownloadErrorCode Failure, DownloadStage Stage, int StreamRefreshAttempts);
public sealed record RetryDecision(bool ShouldRetry, RetryAction Action, string Reason);

public static class RetryPolicy
{
    public const int MaxStreamRefreshAttempts = 1;

    public static RetryDecision Decide(RetryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Failure == DownloadErrorCode.AccessDenied
            && context.Stage == DownloadStage.Downloading
            && context.StreamRefreshAttempts < MaxStreamRefreshAttempts)
        {
            return new(true, RetryAction.RefreshStream, "Refresh a stale stream URL once.");
        }

        if (context.Failure == DownloadErrorCode.RateLimited)
        {
            return new(false, RetryAction.UserActionRequired, "Rate limiting is not retried automatically.");
        }

        return new(false, RetryAction.None, "The failure is not eligible for an automatic retry.");
    }
}

public sealed record PublicationRequest(
    bool Staged,
    bool Verified,
    bool DestinationExists,
    bool AllowOverwrite = false);

public enum PublicationDecisionKind
{
    Publish,
    RejectUnverified,
    RejectExisting,
    RejectNotStaged
}

public sealed record PublicationDecision(PublicationDecisionKind Kind, string Reason);

public static class PublicationPolicy
{
    public static PublicationDecision Decide(PublicationRequest request)
    {
        if (!request.Staged)
        {
            return new(PublicationDecisionKind.RejectNotStaged, "Only staged artifacts may be published.");
        }

        if (!request.Verified)
        {
            return new(PublicationDecisionKind.RejectUnverified, "Only verified artifacts may be published.");
        }

        if (request.DestinationExists)
        {
            return new(PublicationDecisionKind.RejectExisting, "Publication never overwrites an existing destination.");
        }

        return new(PublicationDecisionKind.Publish, "Publish the verified staged artifact without overwrite.");
    }
}

public static class SafeFileNamePolicy
{
    public static string Normalize(string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        var chars = candidate.Trim().Select(static c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '_').ToArray();
        var value = new string(chars).Trim(' ', '.');
        return string.IsNullOrEmpty(value) ? "download" : value;
    }
}
