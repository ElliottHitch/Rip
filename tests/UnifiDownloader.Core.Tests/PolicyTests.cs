using UnifiDownloader.Domain;

namespace UnifiDownloader.Core.Tests;

public sealed class PolicyTests
{
    private static readonly MediaCharacteristics Compatible = new(
        OutputContainer.Mp4, VideoCodec.H264, AudioCodec.Aac, HasVideo: true, HasAudio: true, FrameRate: 30);

    [Fact]
    public void Format_policy_passthroughs_compatible_mp4()
    {
        var decision = FormatPolicy.Decide(OutputContainer.Mp4, Compatible);

        Assert.Equal(EncodingStrategy.Passthrough, decision.Strategy);
        Assert.False(decision.RequiresMediaProcessor);
    }

    [Fact]
    public void Format_policy_remuxes_compatible_streams_in_other_container()
    {
        var decision = FormatPolicy.Decide(OutputContainer.UnifiMp4, Compatible);

        Assert.Equal(EncodingStrategy.Remux, decision.Strategy);
        Assert.True(decision.RequiresMediaProcessor);
    }

    [Fact]
    public void Format_policy_transcodes_incompatible_codecs()
    {
        var decision = FormatPolicy.Decide(
            OutputContainer.Mp4,
            Compatible with { VideoCodec = VideoCodec.Av1 });

        Assert.Equal(EncodingStrategy.Transcode, decision.Strategy);
        Assert.True(decision.RequiresMediaProcessor);
    }

    [Fact]
    public void Frame_rate_policy_preserves_unspecified_rate_and_converts_when_requested()
    {
        var preserve = FrameRatePolicy.Decide(29.97, null);
        var convert = FrameRatePolicy.Decide(29.97, 30);

        Assert.Equal(29.97, preserve.EffectiveFrameRate);
        Assert.False(preserve.RequiresConversion);
        Assert.Equal(30, convert.EffectiveFrameRate);
        Assert.True(convert.RequiresConversion);
    }

    [Fact]
    public void Frame_rate_policy_does_not_convert_when_known_source_matches_target()
    {
        var decision = FrameRatePolicy.Decide(30, 30);

        Assert.Equal(30, decision.EffectiveFrameRate);
        Assert.False(decision.RequiresConversion);
    }

    [Theory]
    [InlineData(24d)]
    [InlineData(25d)]
    [InlineData(30d)]
    public void Unifi_frame_rate_policy_preserves_allowed_source_rates(double sourceFrameRate)
    {
        var decision = FrameRatePolicy.Decide(sourceFrameRate, null, unifiCompatible: true);

        Assert.Equal(sourceFrameRate, decision.EffectiveFrameRate);
        Assert.False(decision.RequiresConversion);
    }

    [Theory]
    [InlineData(60d)]
    [InlineData(null)]
    public void Unifi_frame_rate_policy_converts_unsupported_or_unknown_source_to_30(double? sourceFrameRate)
    {
        var decision = FrameRatePolicy.Decide(sourceFrameRate, null, unifiCompatible: true);

        Assert.Equal(30d, decision.EffectiveFrameRate);
        Assert.True(decision.RequiresConversion);
    }

    [Fact]
    public void Frame_rate_policy_converts_to_explicit_target_when_source_is_unknown()
    {
        var decision = FrameRatePolicy.Decide(null, 25);

        Assert.Equal(25, decision.EffectiveFrameRate);
        Assert.True(decision.RequiresConversion);
    }

    [Theory]
    [InlineData(24d)]
    [InlineData(25d)]
    [InlineData(30d)]
    public void Frame_rate_policy_accepts_the_contract_targets(double requestedFrameRate)
    {
        var decision = FrameRatePolicy.Decide(29.97, requestedFrameRate);

        Assert.Equal(requestedFrameRate, decision.EffectiveFrameRate);
        Assert.True(decision.RequiresConversion);
    }

    [Fact]
    public void Frame_rate_policy_rejects_non_finite_source_and_requested_values()
    {
        foreach (var nonFiniteRate in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FrameRatePolicy.Decide(nonFiniteRate, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => FrameRatePolicy.Decide(30, nonFiniteRate));
        }
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void Frame_rate_policy_rejects_nonpositive_finite_source_even_with_explicit_target(double sourceFrameRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameRatePolicy.Decide(sourceFrameRate, 24d));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameRatePolicy.Decide(sourceFrameRate, null));
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(23d)]
    [InlineData(26d)]
    [InlineData(60d)]
    [InlineData(120d)]
    public void Frame_rate_policy_rejects_non_contract_targets(double requestedFrameRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameRatePolicy.Decide(30, requestedFrameRate));
    }

    [Fact]
    public void Retry_policy_allows_one_stream_403_refresh_but_never_429()
    {
        var first = RetryPolicy.Decide(new(DownloadErrorCode.AccessDenied, DownloadStage.Downloading, 0));
        var exhausted = RetryPolicy.Decide(new(DownloadErrorCode.AccessDenied, DownloadStage.Downloading, 1));
        var rateLimited = RetryPolicy.Decide(new(DownloadErrorCode.RateLimited, DownloadStage.Downloading, 0));

        Assert.Equal(RetryPolicy.MaxStreamRefreshAttempts, 1);
        Assert.True(first.ShouldRetry);
        Assert.Equal(RetryAction.RefreshStream, first.Action);
        Assert.False(exhausted.ShouldRetry);
        Assert.False(rateLimited.ShouldRetry);
        Assert.Equal(RetryAction.UserActionRequired, rateLimited.Action);
    }

    [Fact]
    public void Publication_policy_requires_verified_staging_and_never_overwrites()
    {
        Assert.Equal(PublicationDecisionKind.RejectNotStaged,
            PublicationPolicy.Decide(new(false, true, false)).Kind);
        Assert.Equal(PublicationDecisionKind.RejectUnverified,
            PublicationPolicy.Decide(new(true, false, false)).Kind);
        Assert.Equal(PublicationDecisionKind.RejectExisting,
            PublicationPolicy.Decide(new(true, true, true, AllowOverwrite: true)).Kind);
        Assert.Equal(PublicationDecisionKind.Publish,
            PublicationPolicy.Decide(new(true, true, false)).Kind);
    }

    [Fact]
    public void Filename_policy_removes_unsafe_characters_and_has_fallback()
    {
        Assert.Equal("safe_name_.mp4", SafeFileNamePolicy.Normalize("safe/name?.mp4"));
        Assert.Equal("download", SafeFileNamePolicy.Normalize("..."));
    }
}
