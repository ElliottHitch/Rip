using System.Globalization;
using System.Text.Json;
using UnifiDownloader.Application;
using UnifiDownloader.Domain;

namespace UnifiDownloader.Infrastructure;

/// <summary>
/// Runs the pinned yt-dlp executable for one-video metadata and media resolution.
/// Child output is parsed and discarded inside Infrastructure; only typed Core values cross the boundary.
/// </summary>
public sealed class YtDlpVideoProvider : IVideoProvider
{
    private const int MaximumFormats = 128;
    private const int MaximumTextCharacters = 4096;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private static readonly string[] OperationArguments =
    [
        "--dump-single-json",
        "--no-warnings",
        "--no-progress",
        "--skip-download",
        "--no-playlist"
    ];

    private readonly BoundedProcessExecutor executor;
    private readonly string knownLocalDenoPath;
    private readonly TimeSpan timeout;

    public YtDlpVideoProvider(
        BoundedProcessExecutor executor,
        string knownLocalDenoPath,
        TimeSpan? timeout = null)
    {
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        if (string.IsNullOrWhiteSpace(knownLocalDenoPath) || !Path.IsPathFullyQualified(knownLocalDenoPath))
        {
            throw new ArgumentException("The JavaScript runtime path must be an absolute local path.", nameof(knownLocalDenoPath));
        }

        this.knownLocalDenoPath = knownLocalDenoPath;
        this.timeout = timeout ?? DefaultTimeout;
        if (this.timeout <= TimeSpan.Zero && this.timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public async ValueTask<ProviderResult<MetadataSnapshot>> ReadMetadataAsync(
        VideoReference video,
        BrowserSessionSelection? browserSession,
        CancellationToken cancellationToken)
    {
        if (!IsHttpAddress(video.Address))
        {
            return Failure<MetadataSnapshot>(SafeInfrastructureErrors.InvalidProviderResponse(DownloadStage.Metadata));
        }

        var process = await RunAsync(video.Address, browserSession, DownloadStage.Metadata, cancellationToken).ConfigureAwait(false);
        if (process.Error is not null)
        {
            return Failure<MetadataSnapshot>(process.Error);
        }

        var parsed = ProviderJsonParser.Parse(process.Value!.StandardOutput);
        if (parsed.Error)
        {
            return Failure<MetadataSnapshot>(SafeInfrastructureErrors.InvalidProviderResponse(DownloadStage.Metadata));
        }

        var description = parsed.Value!;
        return new ProviderResult<MetadataSnapshot>(
            new MetadataSnapshot(description.Title, description.Duration, description.Uploader, description.PublishedAt),
            null);
    }

    public async ValueTask<ProviderResult<MediaPlan>> ResolveMediaAsync(
        DownloadRequest request,
        MetadataSnapshot metadata,
        CancellationToken cancellationToken)
    {
        if (request is null || metadata is null || !IsHttpAddress(request.Video.Address))
        {
            return Failure<MediaPlan>(SafeInfrastructureErrors.InvalidProviderResponse(DownloadStage.Resolving));
        }

        var selection = request.Operation switch
        {
            DownloadOperation.Video => FormatSelection.Video,
            DownloadOperation.Audio => FormatSelection.Audio,
            _ => FormatSelection.Unsupported
        };
        if (selection == FormatSelection.Unsupported)
        {
            return Failure<MediaPlan>(SafeInfrastructureErrors.UnsupportedProviderOperation());
        }

        var process = await RunAsync(request.Video.Address, request.BrowserSession, DownloadStage.Resolving, cancellationToken).ConfigureAwait(false);
        if (process.Error is not null)
        {
            return Failure<MediaPlan>(process.Error);
        }

        var parsed = ProviderJsonParser.Parse(process.Value!.StandardOutput);
        if (parsed.Error)
        {
            return Failure<MediaPlan>(SafeInfrastructureErrors.InvalidProviderResponse(DownloadStage.Resolving));
        }

        var formats = parsed.Value!.Formats;
        if (selection == FormatSelection.Video)
        {
            var video = SelectDedicatedVideo(formats);
            var audio = SelectDedicatedAudio(formats);
            if (video is not null && audio is not null)
            {
                return BuildVideoPlan(request, video, audio);
            }

            var progressive = SelectProgressive(formats);
            if (progressive is not null)
            {
                return BuildProgressivePlan(request, progressive);
            }

            // Preserve the single-stream fallback when no complete dedicated pair exists.
            // This also supports video-only and audio-only providers that expose no muxed
            // progressive format.
        }

        var candidate = Select(formats, selection);
        if (candidate is null) return Failure<MediaPlan>(SafeInfrastructureErrors.UnsupportedProviderFormat(DownloadStage.Resolving));

        if (!TryMapContainer(candidate.Extension, out var sourceContainer))
        {
            return Failure<MediaPlan>(SafeInfrastructureErrors.UnsupportedProviderFormat(DownloadStage.Resolving));
        }

        var hasVideo = candidate.HasVideo;
        var hasAudio = candidate.HasAudio;
        var characteristics = new MediaCharacteristics(
            sourceContainer,
            MapVideoCodec(candidate.VideoCodec),
            MapAudioCodec(candidate.AudioCodec),
            hasVideo,
            hasAudio,
            hasVideo ? candidate.FrameRate : null);
        return new ProviderResult<MediaPlan>(
            new MediaPlan(
                request,
                characteristics,
                selection == FormatSelection.Video ? candidate.FormatId : null,
                selection == FormatSelection.Audio ? candidate.FormatId : null,
                selection == FormatSelection.Video ? candidate.LengthBytes : null,
                selection == FormatSelection.Audio ? candidate.LengthBytes : null),
            null);
    }

    private static ProviderResult<MediaPlan> BuildVideoPlan(DownloadRequest request, FormatDescriptor video, FormatDescriptor audio)
    {
        if (!TryMapContainer(video.Extension, out var container))
            return Failure<MediaPlan>(SafeInfrastructureErrors.UnsupportedProviderFormat(DownloadStage.Resolving));
        var characteristics = new MediaCharacteristics(
            container,
            MapVideoCodec(video.VideoCodec),
            MapAudioCodec(audio.AudioCodec),
            HasVideo: true,
            HasAudio: true,
            video.FrameRate);
        return new ProviderResult<MediaPlan>(new MediaPlan(
            request,
            characteristics,
            video.FormatId,
            audio.FormatId,
            video.LengthBytes,
            audio.LengthBytes), null);
    }

    private static ProviderResult<MediaPlan> BuildProgressivePlan(DownloadRequest request, FormatDescriptor progressive)
    {
        if (!TryMapContainer(progressive.Extension, out var container))
            return Failure<MediaPlan>(SafeInfrastructureErrors.UnsupportedProviderFormat(DownloadStage.Resolving));
        var characteristics = new MediaCharacteristics(
            container,
            MapVideoCodec(progressive.VideoCodec),
            MapAudioCodec(progressive.AudioCodec),
            HasVideo: true,
            HasAudio: true,
            progressive.FrameRate);
        // Only the video format identity is populated: one input is staged and mapped to both tracks.
        return new ProviderResult<MediaPlan>(new MediaPlan(
            request,
            characteristics,
            progressive.FormatId,
            null,
            progressive.LengthBytes,
            null,
            IsProgressive: true), null);
    }

    private async ValueTask<ProviderResult<CapturedProcessResult>> RunAsync(
        Uri address,
        BrowserSessionSelection? browserSession,
        DownloadStage stage,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> arguments;
        try
        {
            arguments = BuildArguments(address, browserSession);
        }
        catch (ArgumentException)
        {
            return Failure<CapturedProcessResult>(SafeInfrastructureErrors.InvalidProviderResponse(stage));
        }

        var process = await executor.ExecuteCapturedAsync(
            new ProcessSpec(ToolKey.YtDlp.ToString(), arguments, timeout),
            cancellationToken).ConfigureAwait(false);
        if (process.Error is not null)
        {
            return Failure<CapturedProcessResult>(process.Error);
        }

        var result = process.Value!;
        if (result.ExitCode != 0)
        {
            return Failure<CapturedProcessResult>(BoundedProcessExecutor.ClassifyNonzeroExit(result));
        }
        if (result.OutputTruncated)
        {
            return Failure<CapturedProcessResult>(SafeInfrastructureErrors.ProviderUnavailable());
        }

        return new ProviderResult<CapturedProcessResult>(result, null);
    }

    private IReadOnlyList<string> BuildArguments(Uri address, BrowserSessionSelection? browserSession)
    {
        var operation = new List<string>(OperationArguments.Length + 3);
        operation.AddRange(OperationArguments);
        if (browserSession is not null)
        {
            operation.Add("--cookies-from-browser");
            operation.Add(BrowserSelector(browserSession.Kind));
        }

        operation.Add(address.AbsoluteUri);
        return YtDlpInvocationPolicy.Build(knownLocalDenoPath, operation);
    }

    private static FormatDescriptor? Select(IReadOnlyList<FormatDescriptor> formats, FormatSelection selection)
    {
        var candidates = formats
            .Where(format => selection == FormatSelection.Video ? format.HasVideo : format.HasAudio)
            .OrderByDescending(format => selection == FormatSelection.Video
                ? format.HasVideo && !format.HasAudio
                : format.HasAudio && !format.HasVideo)
            .ThenByDescending(format => selection == FormatSelection.Video ? format.Height ?? 0 : format.AudioBitrate ?? 0)
            .ThenByDescending(format => selection == FormatSelection.Video ? format.Width ?? 0 : format.SampleRate ?? 0)
            .ThenByDescending(format => selection == FormatSelection.Video ? format.FrameRate ?? 0 : format.Bitrate ?? 0)
            .ThenByDescending(format => format.Bitrate ?? 0)
            .ThenBy(format => format.LengthBytes ?? long.MaxValue)
            .ThenBy(format => format.FormatId, StringComparer.Ordinal)
            .ThenBy(format => format.Extension, StringComparer.Ordinal)
            .ThenBy(format => format.Source.AbsoluteUri, StringComparer.Ordinal)
            .ToArray();
        return candidates.FirstOrDefault(format => TryMapContainer(format.Extension, out _));
    }

    private static FormatDescriptor? SelectDedicatedVideo(IReadOnlyList<FormatDescriptor> formats) =>
        Select(formats.Where(static format => format.HasVideo && !format.HasAudio).ToArray(), FormatSelection.Video);

    private static FormatDescriptor? SelectDedicatedAudio(IReadOnlyList<FormatDescriptor> formats) =>
        Select(formats.Where(static format => format.HasAudio && !format.HasVideo).ToArray(), FormatSelection.Audio);

    private static FormatDescriptor? SelectProgressive(IReadOnlyList<FormatDescriptor> formats) =>
        Select(formats.Where(static format => format.HasVideo && format.HasAudio).ToArray(), FormatSelection.Video);

    private static string BrowserSelector(BrowserKind kind) => kind switch
    {
        BrowserKind.Chromium => "chromium",
        BrowserKind.Chrome => "chrome",
        BrowserKind.Edge => "edge",
        BrowserKind.Firefox => "firefox",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static bool TryMapContainer(string extension, out OutputContainer container)
    {
        if (extension is "mp4" or "m4a" or "m4v" or "mov")
        {
            container = OutputContainer.Mp4;
            return true;
        }

        if (extension is "webm" or "mkv" or "opus" or "ogg")
        {
            container = OutputContainer.Matroska;
            return true;
        }

        container = default;
        return false;
    }

    private static VideoCodec MapVideoCodec(string codec)
    {
        if (codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("h264", StringComparison.OrdinalIgnoreCase)) return VideoCodec.H264;
        if (codec.StartsWith("hev", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("hvc", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("hevc", StringComparison.OrdinalIgnoreCase)) return VideoCodec.Hevc;
        if (codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("av1", StringComparison.OrdinalIgnoreCase)) return VideoCodec.Av1;
        if (codec.StartsWith("vp09", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase)) return VideoCodec.Vp9;
        return VideoCodec.Unknown;
    }

    private static AudioCodec MapAudioCodec(string codec)
    {
        if (codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("aac", StringComparison.OrdinalIgnoreCase)) return AudioCodec.Aac;
        if (codec.StartsWith("opus", StringComparison.OrdinalIgnoreCase)) return AudioCodec.Opus;
        if (codec.StartsWith("vorbis", StringComparison.OrdinalIgnoreCase)) return AudioCodec.Vorbis;
        return AudioCodec.Unknown;
    }

    private static bool IsHttpAddress(Uri? address) =>
        address is { IsAbsoluteUri: true } &&
        address.Scheme is "http" or "https";

    private static ProviderResult<T> Failure<T>(SafeDownloadError error) => new(default, error);

    private enum FormatSelection
    {
        Unsupported,
        Video,
        Audio
    }

    private sealed record FormatDescriptor(
        string FormatId,
        Uri Source,
        string Extension,
        string VideoCodec,
        string AudioCodec,
        double? Height,
        double? Width,
        double? FrameRate,
        double? Bitrate,
        double? AudioBitrate,
        double? SampleRate,
        bool HasVideo,
        bool HasAudio,
        long? LengthBytes);

    private sealed record ProviderDescription(
        string Title,
        TimeSpan? Duration,
        string? Uploader,
        DateTimeOffset? PublishedAt,
        IReadOnlyList<FormatDescriptor> Formats);

    private sealed record ParseResult<T>(T? Value, bool Error) where T : class
    {
        public static ParseResult<T> Invalid => new(null, true);
    }

    private static class ProviderJsonParser
    {
        private static readonly string[] NumericFormatFields =
        [
            "height", "width", "fps", "tbr", "vbr", "abr", "asr", "audio_channels",
            "filesize", "filesize_approx", "quality", "preference"
        ];

        public static ParseResult<ProviderDescription> Parse(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || IsPlaylist(root)) return ParseResult<ProviderDescription>.Invalid;

                var title = RequiredText(root, "title");
                if (title is null || title.Length > MaximumTextCharacters) return ParseResult<ProviderDescription>.Invalid;
                if (!TryOptionalNonNegativeDouble(root, "duration", out var durationSeconds)) return ParseResult<ProviderDescription>.Invalid;
                if (!TryOptionalText(root, "uploader", out var uploader) || uploader is { Length: > MaximumTextCharacters }) return ParseResult<ProviderDescription>.Invalid;
                if (!TryPublishedAt(root, out var publishedAt)) return ParseResult<ProviderDescription>.Invalid;
                if (!root.TryGetProperty("formats", out var formatsElement) || formatsElement.ValueKind != JsonValueKind.Array) return ParseResult<ProviderDescription>.Invalid;

                var formats = new List<FormatDescriptor>();
                foreach (var element in formatsElement.EnumerateArray())
                {
                    if (formats.Count >= MaximumFormats || !TryFormat(element, out var format)) return ParseResult<ProviderDescription>.Invalid;
                    formats.Add(format!);
                }

                if (formats.Count == 0) return ParseResult<ProviderDescription>.Invalid;
                if (durationSeconds is { } seconds && seconds > TimeSpan.MaxValue.TotalSeconds) return ParseResult<ProviderDescription>.Invalid;
                var duration = durationSeconds is { } durationValue ? (TimeSpan?)TimeSpan.FromSeconds(durationValue) : null;
                return new ParseResult<ProviderDescription>(new ProviderDescription(title, duration, uploader, publishedAt, formats), false);
            }
            catch (JsonException)
            {
                return ParseResult<ProviderDescription>.Invalid;
            }
            catch (ArgumentException)
            {
                return ParseResult<ProviderDescription>.Invalid;
            }
            catch (FormatException)
            {
                return ParseResult<ProviderDescription>.Invalid;
            }
            catch (OverflowException)
            {
                return ParseResult<ProviderDescription>.Invalid;
            }
        }

        private static bool IsPlaylist(JsonElement root)
        {
            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind != JsonValueKind.Null) return true;
            if (!root.TryGetProperty("_type", out var type) || type.ValueKind != JsonValueKind.String) return false;
            var value = type.GetString();
            return value is "playlist" or "multi_video" or "url";
        }

        private static bool TryFormat(JsonElement element, out FormatDescriptor? format)
        {
            format = null;
            if (element.ValueKind != JsonValueKind.Object) return false;
            var formatId = RequiredText(element, "format_id");
            var sourceText = RequiredText(element, "url");
            var extension = RequiredText(element, "ext");
            var videoCodec = RequiredText(element, "vcodec");
            var audioCodec = RequiredText(element, "acodec");
            if (formatId is null || sourceText is null || extension is null || videoCodec is null || audioCodec is null ||
                formatId.Length > MaximumTextCharacters || extension.Length > 32 || videoCodec.Length > 128 || audioCodec.Length > 128 ||
                !Uri.TryCreate(sourceText, UriKind.Absolute, out var source) || !IsHttpAddress(source)) return false;

            foreach (var field in NumericFormatFields)
            {
                if (!TryOptionalNonNegativeDouble(element, field, out _)) return false;
            }

            if (!TryOptionalNonNegativeDouble(element, "height", out var height) ||
            !TryOptionalNonNegativeDouble(element, "width", out var width) ||
            !TryOptionalNonNegativeDouble(element, "fps", out var frameRate) ||
            !TryOptionalNonNegativeDouble(element, "tbr", out var bitrate) ||
            !TryOptionalNonNegativeDouble(element, "abr", out var audioBitrate) ||
            !TryOptionalNonNegativeDouble(element, "asr", out var sampleRate) ||
            !TryLength(element, out var lengthBytes)) return false;

            var hasVideo = !string.Equals(videoCodec, "none", StringComparison.OrdinalIgnoreCase);
            var hasAudio = !string.Equals(audioCodec, "none", StringComparison.OrdinalIgnoreCase);
            if (!hasVideo && !hasAudio) return false;
            format = new FormatDescriptor(
                formatId,
                source,
                extension.ToLowerInvariant(),
                videoCodec,
                audioCodec,
                height,
                width,
                frameRate,
                bitrate,
                audioBitrate,
                sampleRate,
                hasVideo,
                hasAudio,
                lengthBytes);
            return true;
        }

        private static bool TryLength(JsonElement element, out long? length)
        {
            length = null;
            foreach (var name in new[] { "filesize", "filesize_approx" })
            {
                if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var candidate) || candidate < 0) return false;
                length ??= candidate;
            }

            return true;
        }

        private static string? RequiredText(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static bool TryOptionalText(JsonElement element, string name, out string? text)
        {
            text = null;
            if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.String) return false;
            text = value.GetString();
            return text is not null;
        }

        private static bool TryOptionalNonNegativeDouble(JsonElement element, string name, out double? number)
        {
            number = null;
            if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var candidate) || !double.IsFinite(candidate) || candidate < 0) return false;
            number = candidate;
            return true;
        }

        private static bool TryPublishedAt(JsonElement root, out DateTimeOffset? publishedAt)
        {
            publishedAt = null;
            if (!root.TryGetProperty("upload_date", out var value) || value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.String) return false;
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
            publishedAt = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc));
            return true;
        }
    }
}
