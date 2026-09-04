namespace UnifiDownloader.Domain;

public enum DownloadOperation
{
    Metadata,
    Video,
    Audio
}

public enum OutputContainer
{
    Mp4,
    UnifiMp4,
    Matroska
}

public enum BrowserKind
{
    Chromium,
    Chrome,
    Edge,
    Firefox
}

public readonly record struct VideoReference
{
    public VideoReference(Uri address)
    {
        if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The video address must be an absolute HTTP(S) URI.", nameof(address));
        }

        Address = address;
    }

    public Uri Address { get; }

    public override string ToString() => "[video-reference]";
}

/// <summary>A consented browser-session choice. It contains no profile path, cookies, or credentials.</summary>
public sealed record BrowserSessionSelection
{
    private BrowserSessionSelection(BrowserKind kind) => Kind = kind;

    public BrowserKind Kind { get; }

    public static BrowserSessionSelection Create(BrowserKind kind) => new(kind);

    public override string ToString() => "[browser-session-selected]";
}

public sealed record OutputOptions(
    string Directory,
    string FileStem,
    OutputContainer Container = OutputContainer.Matroska,
    bool AllowOverwrite = false,
    double? FrameRateTarget = null,
    bool UnifiCompatible = false);

public sealed record DownloadRequest(
    VideoReference Video,
    DownloadOperation Operation,
    OutputOptions Output,
    BrowserSessionSelection? BrowserSession = null);

public sealed record MetadataSnapshot(
    string Title,
    TimeSpan? Duration,
    string? Uploader,
    DateTimeOffset? PublishedAt);

public enum VideoCodec
{
    H264,
    Hevc,
    Av1,
    Vp9,
    Unknown
}

public enum AudioCodec
{
    Aac,
    Opus,
    Vorbis,
    Unknown
}

public sealed record MediaCharacteristics(
    OutputContainer SourceContainer,
    VideoCodec VideoCodec,
    AudioCodec AudioCodec,
    bool HasVideo,
    bool HasAudio,
    double? FrameRate = null);

public sealed record MediaPlan(
    DownloadRequest Request,
    MediaCharacteristics Characteristics,
    string? VideoFormatId = null,
    string? AudioFormatId = null,
    long? VideoLengthBytes = null,
    long? AudioLengthBytes = null,
    bool IsProgressive = false);

public enum LocalMediaChannel
{
    Video,
    Audio
}

/// <summary>
/// An opaque reference to a verified local stream owned by Infrastructure.
/// The key is an identifier only; no filesystem or remote location crosses the Core boundary.
/// </summary>
public sealed record LocalMediaInputHandle
{
    public LocalMediaInputHandle(string inputKey, LocalMediaChannel channel, long lengthBytes, bool verified)
    {
        if (string.IsNullOrWhiteSpace(inputKey) ||
            inputKey.Length > 128 ||
            inputKey.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character) || character is '/' or '\\' or ':' or '?' or '#'))
        {
            throw new ArgumentException("The local media input key must be a bounded opaque identifier.", nameof(inputKey));
        }

        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthBytes);

        InputKey = inputKey;
        Channel = channel;
        LengthBytes = lengthBytes;
        Verified = verified;
    }

    public string InputKey { get; }
    public LocalMediaChannel Channel { get; }
    public long LengthBytes { get; }
    public bool Verified { get; }

    public override string ToString() => "[local-media-input-handle]";
}

/// <summary>
/// The local stream handles for one media plan. Null channels mean that the plan does not
/// contain that stream; the owning Infrastructure implementation validates the binding.
/// </summary>
public sealed record LocalMediaInputs(
    LocalMediaInputHandle? Video = null,
    LocalMediaInputHandle? Audio = null)
{
    public LocalMediaInputHandle? VideoInput => Video;
    public LocalMediaInputHandle? AudioInput => Audio;

    public override string ToString() => "[local-media-inputs]";
}

public sealed record StageReleaseResult
{
    public StageReleaseResult(int releasedCount, bool cleanupComplete)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(releasedCount);
        ReleasedCount = releasedCount;
        CleanupComplete = cleanupComplete;
    }

    public int ReleasedCount { get; }
    public bool CleanupComplete { get; }

    public override string ToString() => "[stage-release-result]";
}

public sealed record StagedArtifact(
    string StagingKey,
    string FileName,
    OutputContainer Container,
    long LengthBytes,
    bool Verified);

/// <summary>
/// A verified published MP4. The output key is an opaque application handle; the
/// Infrastructure-owned filesystem location never crosses this boundary.
/// </summary>
public sealed record VerifiedLocalMp4
{
    public VerifiedLocalMp4(string fileName, string outputKey, long lengthBytes)
    {
        if (string.IsNullOrWhiteSpace(outputKey) ||
            outputKey.Length > 128 ||
            outputKey.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character) || character is '/' or '\\' or ':' or '?' or '#'))
        {
            throw new ArgumentException("The published output key must be a bounded opaque identifier.", nameof(outputKey));
        }

        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Length > 255 ||
            !fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
            fileName is ".mp4" or ".mkv" or "." or ".." ||
            fileName.Any(static character => char.IsControl(character) || character is '/' or '\\' or ':' or '?' or '#'))
        {
            throw new ArgumentException("The published file name must be a safe output basename.", nameof(fileName));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthBytes);
        FileName = fileName;
        OutputKey = outputKey;
        LengthBytes = lengthBytes;
    }

    public string FileName { get; }
    public string OutputKey { get; }
    public long LengthBytes { get; }

    public override string ToString() => "[verified-local-mp4]";
}
