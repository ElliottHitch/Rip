using UnifiDownloader.Application;
using UnifiDownloader.Domain;

namespace UnifiDownloader.Core.Tests;

public sealed class ContractsTests
{
    private static DownloadRequest Request(string name = "one")
        => new(
            new VideoReference(new Uri("https://example.test/watch?v=sentinel")),
            DownloadOperation.Video,
            new OutputOptions("output", name));

    [Fact]
    public void Records_are_immutable_value_contracts()
    {
        var first = Request();
        var second = first with { Operation = DownloadOperation.Audio };

        Assert.Equal(DownloadOperation.Video, first.Operation);
        Assert.Equal(DownloadOperation.Audio, second.Operation);
        Assert.NotEqual(first, second);
        Assert.Equal("[video-reference]", first.Video.ToString());
    }

    [Fact]
    public void Output_options_frame_rate_target_is_optional_and_typed()
    {
        var preserve = new OutputOptions("output", "one");
        var target = new OutputOptions("output", "one", FrameRateTarget: 24d);

        Assert.Null(preserve.FrameRateTarget);
        Assert.Equal(24d, target.FrameRateTarget);
    }

    [Fact]
    public void One_video_policy_accepts_exactly_one_and_rejects_zero_or_many()
    {
        Assert.True(OneVideoPolicy.Validate(new[] { Request() }).IsValid);
        Assert.False(OneVideoPolicy.Validate(Array.Empty<DownloadRequest>()).IsValid);
        Assert.False(OneVideoPolicy.Validate(new[] { Request("one"), Request("two") }).IsValid);
    }

    [Fact]
    public void Browser_session_is_opaque_and_not_a_profile_value()
    {
        var selection = BrowserSessionSelection.Create(BrowserKind.Chrome);

        Assert.Equal(BrowserKind.Chrome, selection.Kind);
        Assert.Equal("[browser-session-selected]", selection.ToString());
        Assert.DoesNotContain("profile", selection.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_video_reference_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new VideoReference(new Uri("file:///local/sentinel")));
    }

    [Fact]
    public void Published_mp4_is_opaque_bounded_and_positive_length()
    {
        var file = new VerifiedLocalMp4("safe.mp4", "output-opaque", 1);
        Assert.Equal("output-opaque", file.OutputKey);
        Assert.Equal("safe.mp4", file.FileName);
        Assert.Throws<ArgumentException>(() => new VerifiedLocalMp4("safe.mp4", "/published", 1));
        Assert.Throws<ArgumentException>(() => new VerifiedLocalMp4("../unsafe.mp4", "output-key", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VerifiedLocalMp4("safe.mp4", "output-key", 0));
    }
}
