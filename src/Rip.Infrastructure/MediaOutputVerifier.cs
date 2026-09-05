using System.Globalization;
using System.Text.Json;
using Rip.Application;
using Rip.Domain;

namespace Rip.Infrastructure;

/// <summary>Inspects the actual muxed media before it can be published.</summary>
public static class MediaOutputVerifier
{
    private static readonly double[] AllowedFrameRates = [24d, 25d, 30d];
    public static async ValueTask<bool> VerifyAsync(
        BoundedProcessExecutor executor, string path, MediaPlan plan, CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteCapturedAsync(new ProcessSpec(nameof(ToolKey.Ffprobe),
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], TimeSpan.FromSeconds(30)),
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value!.ExitCode == 0 && !result.Value.OutputTruncated &&
            ValidateProbe(result.Value.StandardOutput, plan);
    }

    public static bool ValidateProbe(string json, MediaPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var format = root.GetProperty("format");
            if (!double.TryParse(Text(format, "duration"), CultureInfo.InvariantCulture, out var duration) ||
                !double.IsFinite(duration) || duration <= 0) return false;
            var container = Text(format, "format_name");
            if (plan.Request.Output.Container == OutputContainer.Matroska
                ? !container.Contains("matroska", StringComparison.Ordinal)
                : !container.Contains("mp4", StringComparison.Ordinal)) return false;
            var streams = root.GetProperty("streams").EnumerateArray().ToArray();
            var video = streams.FirstOrDefault(stream => Text(stream, "codec_type") == "video");
            var audio = streams.FirstOrDefault(stream => Text(stream, "codec_type") == "audio");
            if (plan.Characteristics.HasVideo && video.ValueKind != JsonValueKind.Object ||
                plan.Characteristics.HasAudio && audio.ValueKind != JsonValueKind.Object) return false;
            if (plan.Request.Output.MaximumVideoHeight is { } height && plan.Characteristics.HasVideo &&
                video.GetProperty("height").GetInt32() > height) return false;
            if (!plan.Request.Output.UnifiCompatible && plan.Request.Output.Container != OutputContainer.UnifiMp4)
                return true;

            if (plan.Characteristics.HasVideo)
            {
                var width = video.GetProperty("width").GetInt32();
                var videoHeight = video.GetProperty("height").GetInt32();
                if (Text(video, "codec_name") != "h264" || Text(video, "pix_fmt") != "yuv420p" ||
                    width is <= 0 or > 3840 || videoHeight is <= 0 or > 2160) return false;
                var rate = Text(video, "avg_frame_rate").Split('/');
                if (rate.Length != 2 || !double.TryParse(rate[0], CultureInfo.InvariantCulture, out var numerator) ||
                    !double.TryParse(rate[1], CultureInfo.InvariantCulture, out var denominator) || denominator <= 0)
                    return false;
                var fps = numerator / denominator;
                if (!double.IsFinite(fps) || !AllowedFrameRates.Any(allowed => Math.Abs(fps - allowed) < 0.01)) return false;
                if (!long.TryParse(Text(video, "bit_rate"), out var bitrate) || bitrate is <= 0 or > 40_000_000)
                    return false;
            }
            return !plan.Characteristics.HasAudio ||
                Text(audio, "codec_name") == "aac" && Text(audio, "profile") == "LC" &&
                audio.GetProperty("channels").GetInt32() is > 0 and <= 2;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
}
