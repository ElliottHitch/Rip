using System.Security.Cryptography;
using Rip.Application;
using Rip.Domain;
using Rip.Infrastructure;

namespace Rip.Infrastructure.Tests;

public sealed class FfmpegProcessAdapterTests
{
    private const string VideoUrl = "https://example.invalid/watch?v=synthetic";
    private const string VideoStreamUrl = "https://cdn.example.invalid/video?signature=synthetic";
    private const string AudioStreamUrl = "https://cdn.example.invalid/audio?token=synthetic";

    [Fact]
    public async Task Connect_enforces_encoding_even_when_provider_claims_compatible_codecs()
    {
        using var fixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var workspace = TestWorkspace.Create();
        var video = workspace.Input("video-SYNTHETIC_PATH_SENTINEL.mp4");
        var audio = workspace.Input("audio-SYNTHETIC_PATH_SENTINEL.m4a");
        var adapter = CreateAdapter(fixture);

        var result = await adapter.ProcessAsync(
            Plan(hasVideo: true, hasAudio: true, target: OutputContainer.UnifiMp4),
            new FfmpegInputSet(video, audio),
            new FfmpegStageTarget(workspace.StageRoot),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.True(result.Value!.Verified);
        Assert.True(result.Value.Artifact.Verified);
        Assert.Equal(OutputContainer.UnifiMp4, result.Value.Artifact.Container);
        Assert.Matches("^stage-[0-9a-f]{32}$", result.Value.Artifact.StagingKey);
        Assert.Equal(result.Value.Artifact.StagingKey + ".mp4", result.Value.Artifact.FileName);
        Assert.Equal(2, result.Value.Artifact.LengthBytes);
        Assert.DoesNotContain("SYNTHETIC_PATH_SENTINEL", result.Value.Artifact.FileName, StringComparison.Ordinal);

        var args = fixture.ReadArguments();
        Assert.Contains("libx264", args);
        Assert.Contains("aac_low", args);
        Assert.Contains("40000000", args);
        Assert.DoesNotContain("copy", args);
        Assert.DoesNotContain(args, static argument => argument.Contains("http://", StringComparison.OrdinalIgnoreCase) || argument.Contains("https://", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(args, static argument => argument.Contains("browser", StringComparison.OrdinalIgnoreCase) || argument.Contains("cookie", StringComparison.OrdinalIgnoreCase) || argument.Contains("session", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(args, static argument => argument == "-y");
    }

    [Fact]
    public async Task Matroska_remux_allocates_mkv_and_preserves_selected_codecs()
    {
        using var fixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var workspace = TestWorkspace.Create();
        var result = await CreateAdapter(fixture).ProcessAsync(
            Plan(hasVideo: true, hasAudio: true, target: OutputContainer.Matroska),
            new FfmpegInputSet(workspace.Input("video.webm"), workspace.Input("audio.opus")),
            new FfmpegStageTarget(workspace.StageRoot),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OutputContainer.Matroska, result.Value!.Artifact.Container);
        Assert.EndsWith(".mkv", result.Value.Artifact.FileName, StringComparison.Ordinal);
        Assert.Equal(
            [
                "-hide_banner", "-nostdin", "-loglevel", "error", "-n",
                "-i", workspace.Input("video.webm"), "-i", workspace.Input("audio.opus"),
                "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "copy",
                "-f", "matroska"
            ],
            fixture.ReadArguments()[..^1]);
    }

    [Fact]
    public async Task Successful_output_is_registered_in_the_shared_staged_registry()
    {
        using var fixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var workspace = TestWorkspace.Create();
        var registry = new StagedArtifactRegistry(workspace.StageRoot);
        var result = await CreateAdapter(fixture, stagedRegistry: registry).ProcessAsync(
            Plan(hasVideo: true, hasAudio: true, target: OutputContainer.UnifiMp4),
            new FfmpegInputSet(workspace.Input("video.mp4"), workspace.Input("audio.m4a")),
            new FfmpegStageTarget(workspace.StageRoot),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var artifact = result.Value!.Artifact;
        Assert.True(registry.Owns(artifact));
        Assert.True(registry.TryResolve(artifact, out var path));
        Assert.Equal(artifact.FileName, System.IO.Path.GetFileName(path));
        Assert.Equal(artifact.LengthBytes, new FileInfo(path).Length);
    }

    [Fact]
    public async Task Transcode_uses_frozen_cpu_x264_and_aac_settings()
    {
        using var fixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var workspace = TestWorkspace.Create();
        var video = workspace.Input("video.mp4");
        var audio = workspace.Input("audio.m4a");
        var adapter = CreateAdapter(fixture);

        var result = await adapter.ProcessAsync(
            Plan(hasVideo: true, hasAudio: true, target: OutputContainer.Mp4, videoCodec: VideoCodec.Av1),
            new FfmpegInputSet(video, audio),
            new FfmpegStageTarget(workspace.StageRoot),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var args = fixture.ReadArguments();
        Assert.Equal(
            [
                "-hide_banner", "-nostdin", "-loglevel", "error", "-n",
                "-i", video, "-i", audio,
                "-map", "0:v:0", "-map", "1:a:0",
                "-c:v", "libx264", "-preset", "medium", "-crf", "18",
                "-b:v", "40000000", "-maxrate", "40000000", "-bufsize", "80000000",
                "-profile:v", "high", "-pix_fmt", "yuv420p",
                "-vf", "scale=w='min(3840,iw)':h='min(2160,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2",
                "-c:a", "aac", "-profile:a", "aac_low", "-b:a", "192000", "-ac", "2",
                "-movflags", "+faststart", "-f", "mp4"
            ],
            args[..^1]);
        Assert.EndsWith(".mp4", args[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(24d)]
    [InlineData(25d)]
    [InlineData(30d)]
    public async Task Requested_frame_rate_promotes_copy_strategy_and_emits_one_bounded_target(double target)
    {
        using var fixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var workspace = TestWorkspace.Create();
        var result = await CreateAdapter(fixture).ProcessAsync(
            Plan(hasVideo: true, hasAudio: true, target: OutputContainer.UnifiMp4, frameRateTarget: target),
            new FfmpegInputSet(workspace.Input("video.mp4"), workspace.Input("audio.m4a")),
            new FfmpegStageTarget(workspace.StageRoot),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var args = fixture.ReadArguments();
        Assert.Equal(1, args.Count(static argument => argument == "-r"));
        var rateIndex = Array.IndexOf(args, "-r");
        Assert.True(rateIndex > 0);
        Assert.Equal(target.ToString("0", System.Globalization.CultureInfo.InvariantCulture), args[rateIndex + 1]);
        Assert.DoesNotContain(args, static argument => argument == "copy");
        Assert.Contains("libx264", args, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Requested_frame_rate_promotes_passthrough_to_transcode()
    {
        using var fixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var workspace = TestWorkspace.Create();
        var result = await CreateAdapter(fixture).ProcessAsync(
            Plan(hasVideo: true, hasAudio: true, target: OutputContainer.Mp4, frameRateTarget: 24),
            new FfmpegInputSet(workspace.Input("video.mp4"), workspace.Input("audio.m4a")),
            new FfmpegStageTarget(workspace.StageRoot),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var args = fixture.ReadArguments();
        Assert.Contains("-r", args);
        Assert.DoesNotContain(args, static argument => argument == "copy");
    }

    [Fact]
    public async Task Connect_preserves_allowed_rate_while_enforcing_other_encoding_properties()
    {
        using var fixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var workspace = TestWorkspace.Create();
        var result = await CreateAdapter(fixture).ProcessAsync(
            Plan(hasVideo: true, hasAudio: true, target: OutputContainer.UnifiMp4, frameRateTarget: 30, sourceFrameRate: 30),
            new FfmpegInputSet(workspace.Input("video.mp4"), workspace.Input("audio.m4a")),
            new FfmpegStageTarget(workspace.StageRoot),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var args = fixture.ReadArguments();
        Assert.Contains("libx264", args);
        Assert.DoesNotContain("copy", args);
        Assert.DoesNotContain(args, static argument => argument == "-r");
    }

    [Fact]
    public async Task Explicit_target_with_unknown_source_rate_transcodes_with_target()
    {
        using var fixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var workspace = TestWorkspace.Create();
        var result = await CreateAdapter(fixture).ProcessAsync(
            Plan(hasVideo: true, hasAudio: true, target: OutputContainer.UnifiMp4, frameRateTarget: 25, sourceFrameRate: null),
            new FfmpegInputSet(workspace.Input("video.mp4"), workspace.Input("audio.m4a")),
            new FfmpegStageTarget(workspace.StageRoot),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var args = fixture.ReadArguments();
        var rateIndex = Array.IndexOf(args, "-r");
        Assert.True(rateIndex > 0);
        Assert.Equal("25", args[rateIndex + 1]);
        Assert.DoesNotContain(args, static argument => argument == "copy");
    }

    [Fact]
    public async Task Audio_only_frame_rate_target_is_rejected_before_process_execution()
    {
        using var fixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var workspace = TestWorkspace.Create();
        var result = await CreateAdapter(fixture).ProcessAsync(
            Plan(hasVideo: false, hasAudio: true, target: OutputContainer.Mp4, frameRateTarget: 24),
            new FfmpegInputSet(AudioPath: workspace.Input("audio.m4a")),
            new FfmpegStageTarget(workspace.StageRoot),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.InvalidRequest, result.Error!.Code);
        Assert.False(File.Exists(fixture.ArgumentsPath));
        Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));
    }

    [Fact]
    public async Task Invalid_frame_rate_targets_are_rejected_before_process_execution()
    {
        using var fixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var workspace = TestWorkspace.Create();
        foreach (var target in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 23d, 24.5d })
        {
            var result = await CreateAdapter(fixture).ProcessAsync(
                Plan(hasVideo: true, hasAudio: false, target: OutputContainer.Mp4, videoCodec: VideoCodec.Av1, frameRateTarget: target),
                new FfmpegInputSet(VideoPath: workspace.Input($"video-{target}.mp4")),
                new FfmpegStageTarget(workspace.StageRoot),
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(DownloadErrorCode.InvalidRequest, result.Error!.Code);
        }

        Assert.False(File.Exists(fixture.ArgumentsPath));
        Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));
    }

    [Fact]
    public async Task Passthrough_is_classified_as_typed_unsupported_format_without_launch()
    {
        using var fixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var workspace = TestWorkspace.Create();
        var plan = Plan(hasVideo: true, hasAudio: true, target: OutputContainer.Mp4) with
        {
            Request = Plan(hasVideo: true, hasAudio: true, target: OutputContainer.Mp4).Request with
            {
                Output = new OutputOptions(
                    Path.Combine(workspace.Root, "final-destination"),
                    "passthrough",
                    OutputContainer.Mp4)
            }
        };
        var result = await CreateAdapter(fixture).ProcessAsync(
            plan,
            new FfmpegInputSet(workspace.Input("video.mp4"), workspace.Input("audio.m4a")),
            new FfmpegStageTarget(workspace.StageRoot),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(DownloadErrorCode.UnsupportedFormat, result.Error!.Code);
        Assert.Equal(DownloadStage.Processing, result.Error.Stage);
        Assert.Equal(RetryAction.UserActionRequired, result.Error.Retry);
        Assert.Equal("The requested media format cannot be processed by the local adapter.", result.Error.UserMessage);
        Assert.Equal("diag-media-processing-format-unsupported", result.Error.Diagnostic.Value);
        Assert.False(File.Exists(fixture.ArgumentsPath));
        Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));
    }

    [Fact]
    public async Task Audio_only_and_video_only_plans_are_mapped_without_inventing_a_second_stream()
    {
        using var audioFixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var audioWorkspace = TestWorkspace.Create();
        var audio = audioWorkspace.Input("audio.m4a");
        var audioResult = await CreateAdapter(audioFixture).ProcessAsync(
            Plan(hasVideo: false, hasAudio: true, target: OutputContainer.Mp4),
            new FfmpegInputSet(AudioPath: audio),
            new FfmpegStageTarget(audioWorkspace.StageRoot),
            CancellationToken.None);

        Assert.True(audioResult.IsSuccess);
        Assert.Equal(
            [
                "-hide_banner", "-nostdin", "-loglevel", "error", "-n", "-i", audio,
                "-map", "0:a:0", "-c:a", "aac", "-profile:a", "aac_low", "-b:a", "192000", "-ac", "2",
                "-movflags", "+faststart", "-f", "mp4"
            ],
            audioFixture.ReadArguments()[..^1]);

        using var videoFixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var videoWorkspace = TestWorkspace.Create();
        var video = videoWorkspace.Input("video.mp4");
        var videoResult = await CreateAdapter(videoFixture).ProcessAsync(
            Plan(hasVideo: true, hasAudio: false, target: OutputContainer.Mp4, videoCodec: VideoCodec.Av1),
            new FfmpegInputSet(VideoPath: video),
            new FfmpegStageTarget(videoWorkspace.StageRoot),
            CancellationToken.None);

        Assert.True(videoResult.IsSuccess);
        Assert.Equal(
            [
                "-hide_banner", "-nostdin", "-loglevel", "error", "-n", "-i", video,
                "-map", "0:v:0", "-c:v", "libx264", "-preset", "medium", "-crf", "18",
                "-b:v", "40000000", "-maxrate", "40000000", "-bufsize", "80000000",
                "-profile:v", "high", "-pix_fmt", "yuv420p",
                "-vf", "scale=w='min(3840,iw)':h='min(2160,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2",
                "-movflags", "+faststart", "-f", "mp4"
            ],
            videoFixture.ReadArguments()[..^1]);
    }

    [Fact]
    public async Task Invalid_remote_relative_missing_directory_and_final_destination_paths_fail_before_launch()
    {
        using var fixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var workspace = TestWorkspace.Create();
        var plan = Plan(hasVideo: true, hasAudio: false, target: OutputContainer.Mp4, videoCodec: VideoCodec.Av1);
        var stage = new FfmpegStageTarget(workspace.StageRoot);
        const string remoteInput = "https://cdn.example.invalid/signed?token=PATH_TOKEN_SENTINEL";
        Assert.Contains("PATH_TOKEN_SENTINEL", remoteInput, StringComparison.Ordinal);

        foreach (var path in new[]
        {
            remoteInput,
            "relative-video.mp4",
            Path.Combine(workspace.Root, "missing.mp4"),
            workspace.StageRoot,
        })
        {
            var result = await CreateAdapter(fixture).ProcessAsync(
                plan,
                new FfmpegInputSet(VideoPath: path),
                stage,
                CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal(DownloadErrorCode.InvalidRequest, result.Error!.Code);
            Assert.DoesNotContain("PATH_TOKEN_SENTINEL", result.Error.UserMessage, StringComparison.Ordinal);
        }

        const string finalDestinationStem = "final-destination-SENTINEL";
        Assert.Contains("final-destination-SENTINEL", finalDestinationStem, StringComparison.Ordinal);
        var finalDestinationPlan = plan with
        {
            Request = plan.Request with { Output = new OutputOptions(workspace.StageRoot, finalDestinationStem, OutputContainer.UnifiMp4) }
        };
        var finalDestination = await CreateAdapter(fixture).ProcessAsync(
            finalDestinationPlan,
            new FfmpegInputSet(VideoPath: workspace.Input("valid.mp4")),
            stage,
            CancellationToken.None);
        Assert.False(finalDestination.IsSuccess);
        Assert.Equal(DownloadErrorCode.InvalidRequest, finalDestination.Error!.Code);
        Assert.DoesNotContain("final-destination-SENTINEL", finalDestination.Error.UserMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ArgumentsPath));
    }

    [Fact]
    public async Task Exit_zero_without_or_with_empty_output_is_not_success_and_failed_output_is_cleaned()
    {
        foreach (var mode in new[] { FixtureMode.NoOutput, FixtureMode.EmptyOutput })
        {
            using var fixture = Fixture.Create(mode);
            using var workspace = TestWorkspace.Create();
            var result = await CreateAdapter(fixture).ProcessAsync(
                Plan(hasVideo: true, hasAudio: false, target: OutputContainer.Mp4, videoCodec: VideoCodec.Av1),
                new FfmpegInputSet(VideoPath: workspace.Input("video.mp4")),
                new FfmpegStageTarget(workspace.StageRoot),
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Null(result.Value);
            Assert.Equal(DownloadErrorCode.MediaProcessingFailed, result.Error!.Code);
            Assert.DoesNotContain(workspace.StageRoot, result.Error.UserMessage, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));
        }
    }

    [Fact]
    public async Task Structured_nonzero_failure_is_typed_and_discards_path_and_child_sentinels()
    {
        const string stderr = "{\"error_code\":\"media-processing-failed\",\"detail\":\"https://cdn.example.invalid/?token=FFMPEG_TOKEN_SENTINEL\"}";
        Assert.Contains("FFMPEG_TOKEN_SENTINEL", stderr, StringComparison.Ordinal);
        using var fixture = Fixture.Create(FixtureMode.NonzeroStructured, stderr);
        using var workspace = TestWorkspace.Create();
        var result = await CreateAdapter(fixture).ProcessAsync(
            Plan(hasVideo: true, hasAudio: false, target: OutputContainer.Mp4, videoCodec: VideoCodec.Av1),
            new FfmpegInputSet(VideoPath: workspace.Input("video.mp4")),
            new FfmpegStageTarget(workspace.StageRoot),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.MediaProcessingFailed, result.Error!.Code);
        Assert.Equal("diag-process-media-failed", result.Error.Diagnostic.Value);
        Assert.DoesNotContain("FFMPEG_TOKEN_SENTINEL", result.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.StageRoot, result.Error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_trusted_ffmpeg_expectation_is_a_safe_non_launchable_failure()
    {
        using var fixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var workspace = TestWorkspace.Create();
        var executor = new BoundedProcessExecutor(
            new Dictionary<string, ToolConfiguration> { [nameof(ToolKey.Ffmpeg)] = fixture.Configuration(), [nameof(ToolKey.Ffprobe)] = fixture.Configuration() with { Key = ToolKey.Ffprobe } },
            allowedRepositories: new HashSet<string> { fixture.Repository },
            executionTargetRid: "linux-arm64");

        var result = await new FfmpegProcessAdapter(executor).ProcessAsync(
            Plan(hasVideo: true, hasAudio: false, target: OutputContainer.Mp4, videoCodec: VideoCodec.Av1),
            new FfmpegInputSet(VideoPath: workspace.Input("video.mp4")),
            new FfmpegStageTarget(workspace.StageRoot),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.MissingTool, result.Error!.Code);
        Assert.DoesNotContain(fixture.Path, result.Error.UserMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ArgumentsPath));
    }

    [Fact]
    public async Task Timeout_and_cancellation_are_typed_and_leave_no_stage_file()
    {
        using var timeoutFixture = Fixture.Create(FixtureMode.Sleep, sleepSeconds: 30);
        using var timeoutWorkspace = TestWorkspace.Create();
        var timeout = await CreateAdapter(timeoutFixture, TimeSpan.FromMilliseconds(100)).ProcessAsync(
            Plan(hasVideo: true, hasAudio: false, target: OutputContainer.Mp4, videoCodec: VideoCodec.Av1),
            new FfmpegInputSet(VideoPath: timeoutWorkspace.Input("video.mp4")),
            new FfmpegStageTarget(timeoutWorkspace.StageRoot),
            CancellationToken.None);
        Assert.False(timeout.IsSuccess);
        Assert.Equal(DownloadErrorCode.Unknown, timeout.Error!.Code);
        Assert.Empty(Directory.EnumerateFiles(timeoutWorkspace.StageRoot));

        using var cancellationFixture = Fixture.Create(FixtureMode.Sleep, sleepSeconds: 30);
        using var cancellationWorkspace = TestWorkspace.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var cancelled = await CreateAdapter(cancellationFixture, TimeSpan.FromSeconds(10)).ProcessAsync(
            Plan(hasVideo: true, hasAudio: false, target: OutputContainer.Mp4, videoCodec: VideoCodec.Av1),
            new FfmpegInputSet(VideoPath: cancellationWorkspace.Input("video.mp4")),
            new FfmpegStageTarget(cancellationWorkspace.StageRoot),
            cancellation.Token);
        Assert.False(cancelled.IsSuccess);
        Assert.Equal(DownloadErrorCode.Cancelled, cancelled.Error!.Code);
        Assert.Empty(Directory.EnumerateFiles(cancellationWorkspace.StageRoot));
    }

    [Fact]
    public async Task Nonfinite_and_nonpositive_source_frame_rate_plans_fail_before_process_execution_even_with_target()
    {
        using var fixture = Fixture.Create(FixtureMode.WriteNonEmptyOutput);
        using var workspace = TestWorkspace.Create();
        foreach (var rate in new[] { double.NaN, double.PositiveInfinity, 0d, -1d })
        {
            var result = await CreateAdapter(fixture).ProcessAsync(
                Plan(hasVideo: true, hasAudio: false, target: OutputContainer.Mp4, videoCodec: VideoCodec.Av1, frameRateTarget: 24) with
                {
                    Characteristics = Plan(true, false, OutputContainer.Mp4, VideoCodec.Av1).Characteristics with { FrameRate = rate }
                },
                new FfmpegInputSet(VideoPath: workspace.Input($"video-{rate}.mp4")),
                new FfmpegStageTarget(workspace.StageRoot),
                CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal(DownloadErrorCode.InvalidRequest, result.Error!.Code);
        }

        Assert.False(File.Exists(fixture.ArgumentsPath));
    }

    private static FfmpegProcessAdapter CreateAdapter(
        Fixture fixture,
        TimeSpan? timeout = null,
        StagedArtifactRegistry? stagedRegistry = null) =>
        new(CreateExecutor(fixture), timeout, stagedRegistry);

    private static BoundedProcessExecutor CreateExecutor(Fixture fixture) => new(
        new Dictionary<string, ToolConfiguration> { [nameof(ToolKey.Ffmpeg)] = fixture.Configuration(), [nameof(ToolKey.Ffprobe)] = fixture.Configuration() with { Key = ToolKey.Ffprobe } },
        new ProcessExecutorOptions(32_768, TimeSpan.FromSeconds(5)),
        new HashSet<string> { fixture.Repository },
        "linux-arm64",
        new Dictionary<ToolKey, ToolExpectation> { [ToolKey.Ffmpeg] = fixture.TrustedExpectation(), [ToolKey.Ffprobe] = fixture.TrustedExpectation() with { Key = ToolKey.Ffprobe } });

    private static MediaPlan Plan(
        bool hasVideo,
        bool hasAudio,
        OutputContainer target,
        VideoCodec videoCodec = VideoCodec.H264,
        AudioCodec audioCodec = AudioCodec.Aac,
        double? frameRateTarget = null,
        double? sourceFrameRate = 29.97) =>
        new(
            new DownloadRequest(
                new VideoReference(new Uri(VideoUrl)),
                hasVideo ? DownloadOperation.Video : DownloadOperation.Audio,
                new OutputOptions("/tmp/synthetic-final-destination", "user-final-SENTINEL", target, FrameRateTarget: frameRateTarget),
                BrowserSessionSelection.Create(BrowserKind.Firefox)),
            new MediaCharacteristics(OutputContainer.Mp4, videoCodec, audioCodec, hasVideo, hasAudio, sourceFrameRate),
            hasVideo ? "video-format" : null,
            hasAudio ? "audio-format" : null);

    private enum FixtureMode
    {
        WriteNonEmptyOutput,
        NoOutput,
        EmptyOutput,
        NonzeroStructured,
        Sleep
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string path, string argumentsPath, string hash, FixtureMode mode, string? stderr, int sleepSeconds)
        {
            Path = path;
            ArgumentsPath = argumentsPath;
            Hash = hash;
            this.mode = mode;
            this.stderr = stderr;
            this.sleepSeconds = sleepSeconds;
        }

        private readonly FixtureMode mode;
        private readonly string? stderr;
        private readonly int sleepSeconds;
        public string Path { get; }
        public string ArgumentsPath { get; }
        public string Hash { get; }
        public string Repository { get; } = "https://example.invalid/ffmpeg-fixture-repository";

        public static Fixture Create(FixtureMode mode, string? stderr = null, int sleepSeconds = 0)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"unifi-ffmpeg-{Guid.NewGuid():N}.sh");
            var argumentsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"unifi-ffmpeg-args-{Guid.NewGuid():N}.txt");
            const string probeScript = """
                if [ "$1" = "-v" ]; then
                  for arg in "$@"; do output="$arg"; done
                  container='mov,mp4'
                  case "$output" in *.mkv) container='matroska';; esac
                  printf '{"format":{"duration":"1","format_name":"%s"},"streams":[{"codec_type":"video","codec_name":"h264","pix_fmt":"yuv420p","width":1280,"height":720,"avg_frame_rate":"30/1","bit_rate":"1000000"},{"codec_type":"audio","codec_name":"aac","profile":"LC","channels":2}]}' "$container"
                  exit 0
                fi
                """;
            var body = "#!/bin/sh\n" + probeScript.Replace("\r\n", "\n", StringComparison.Ordinal) + $"\nprintf '%s\\n' \"$@\" > {ShellQuote(argumentsPath)}\n";
            if (sleepSeconds > 0) body += $"sleep {sleepSeconds}\n";
            if (mode == FixtureMode.NonzeroStructured) body += $"printf '%s' {ShellQuote(stderr!)} >&2\n";
            if (mode is FixtureMode.WriteNonEmptyOutput or FixtureMode.EmptyOutput)
            {
                body += "output=''\nfor arg in \"$@\"; do output=\"$arg\"; done\n";
                body += mode == FixtureMode.WriteNonEmptyOutput ? "printf ok > \"$output\"\n" : ": > \"$output\"\n";
            }
            body += mode == FixtureMode.NonzeroStructured ? "exit 17\n" : "exit 0\n";
            File.WriteAllText(path, body);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            using var stream = File.OpenRead(path);
            return new Fixture(path, argumentsPath, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), mode, stderr, sleepSeconds);
        }

        public ToolConfiguration Configuration() => new(
            ToolKey.Ffmpeg,
            "ffmpeg-fixture",
            Repository,
            "7.0.0-fixture",
            "linux-arm64",
            Hash,
            true,
            Path);

        public ToolExpectation TrustedExpectation() => new(
            ToolKey.Ffmpeg,
            "ffmpeg-fixture",
            Repository,
            "7.0.0-fixture",
            "linux-arm64",
            Hash);

        public string[] ReadArguments() => File.ReadAllLines(ArgumentsPath);

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (File.Exists(ArgumentsPath)) File.Delete(ArgumentsPath);
        }

        private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    private sealed class TestWorkspace : IDisposable
    {
        private TestWorkspace(string root)
        {
            Root = root;
            StageRoot = System.IO.Path.Combine(root, "stage");
            Directory.CreateDirectory(StageRoot);
        }

        public string Root { get; }
        public string StageRoot { get; }

        public static TestWorkspace Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"unifi-ffmpeg-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new TestWorkspace(root);
        }

        public string Input(string fileName)
        {
            var path = System.IO.Path.Combine(Root, fileName);
            if (!File.Exists(path) && !Directory.Exists(path)) File.WriteAllText(path, "input");
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
