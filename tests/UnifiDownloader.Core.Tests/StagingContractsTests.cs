using UnifiDownloader.Domain;

namespace UnifiDownloader.Core.Tests;

public sealed class StagingContractsTests
{
    [Fact]
    public void Local_input_contracts_are_opaque_and_do_not_render_values()
    {
        var handle = new LocalMediaInputHandle("input-0123456789abcdef0123456789abcdef", LocalMediaChannel.Audio, 42, true);
        var inputs = new LocalMediaInputs(Audio: handle);
        var release = new StageReleaseResult(1, cleanupComplete: true);

        Assert.Equal("input-0123456789abcdef0123456789abcdef", handle.InputKey);
        Assert.Equal(LocalMediaChannel.Audio, handle.Channel);
        Assert.Equal(42, handle.LengthBytes);
        Assert.True(handle.Verified);
        Assert.Equal(handle, inputs.AudioInput);
        Assert.Equal("[local-media-input-handle]", handle.ToString());
        Assert.Equal("[local-media-inputs]", inputs.ToString());
        Assert.Equal("[stage-release-result]", release.ToString());
        Assert.DoesNotContain("0123456789", handle.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("42", release.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("input with spaces")]
    [InlineData("/tmp/local-stream")]
    [InlineData("https://remote.example/stream")]
    public void Local_input_handle_rejects_non_opaque_keys(string key)
    {
        Assert.Throws<ArgumentException>(() => new LocalMediaInputHandle(key, LocalMediaChannel.Video, 1, true));
    }

    [Fact]
    public void Local_input_handle_rejects_invalid_channel_and_length()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalMediaInputHandle("input-valid", (LocalMediaChannel)99, 1, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalMediaInputHandle("input-valid", LocalMediaChannel.Video, 0, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StageReleaseResult(-1, cleanupComplete: false));
    }
}
