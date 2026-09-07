using Rip.Domain;
using Rip.Infrastructure;

namespace Rip.Infrastructure.Tests;

public sealed class MediaOutputVerifierTests
{
    private const string Probe = """
        {"format":{"duration":"2","format_name":"mov,mp4"},"streams":[
        {"codec_type":"video","codec_name":"h264","pix_fmt":"yuv420p","width":3840,"height":2160,"avg_frame_rate":"30/1","bit_rate":"10000000"},
        {"codec_type":"audio","codec_name":"aac","profile":"LC","channels":2}]}
        """;

    [Fact]
    public void Connect_accepts_actual_compliant_tracks() => Assert.True(MediaOutputVerifier.ValidateProbe(Probe, Plan()));

    [Theory]
    [InlineData("h264", "vp9")]
    [InlineData("yuv420p", "yuv420p10le")]
    [InlineData("2160", "4320")]
    [InlineData("30/1", "60/1")]
    [InlineData("10000000", "46000000")]
    [InlineData("LC", "HE-AAC")]
    [InlineData("\"audio\"", "\"data\"")]
    public void Connect_rejects_incompatible_actual_tracks(string before, string after) =>
        Assert.False(MediaOutputVerifier.ValidateProbe(Probe.Replace(before, after, StringComparison.Ordinal), Plan()));

    [Fact]
    public void Selected_resolution_is_checked_after_processing() =>
        Assert.False(MediaOutputVerifier.ValidateProbe(Probe, Plan() with
        { Request = Plan().Request with { Output = Plan().Request.Output with { MaximumVideoHeight = 1080 } } }));

    private static MediaPlan Plan() => new(
        new DownloadRequest(new VideoReference(new Uri("https://example.invalid/video")), DownloadOperation.Video,
            new OutputOptions("/output", "test", OutputContainer.UnifiMp4)),
        new MediaCharacteristics(OutputContainer.Mp4, VideoCodec.H264, AudioCodec.Aac, true, true));
}
