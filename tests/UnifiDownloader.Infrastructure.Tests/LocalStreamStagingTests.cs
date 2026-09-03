using System.Net;
using System.Security.Cryptography;
using System.Text;
using UnifiDownloader.Application;
using UnifiDownloader.Domain;
using UnifiDownloader.Infrastructure;

namespace UnifiDownloader.Infrastructure.Tests;

public sealed class LocalStreamStagingTests
{
    [Fact]
    public async Task Stages_video_as_opaque_verified_handle_and_release_removes_owned_file()
    {
        using var workspace = Workspace.Create();
        using var client = new HttpClient(new ResponseHandler(_ => Response(HttpStatusCode.OK, "video-bytes")));
        var stager = new LocalStreamStager(client, workspace.StageRoot);
        var plan = Plan(video: true, audio: false);
        const string videoUrlSentinel = "https://video.example.test/watch";
        Assert.Contains(videoUrlSentinel, plan.Request.Video.Address.AbsoluteUri, StringComparison.Ordinal);

        var result = await stager.StageAsync(plan, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var inputs = Assert.IsType<LocalMediaInputs>(result.Value);
        var handle = Assert.IsType<LocalMediaInputHandle>(inputs.Video);
        Assert.Equal(LocalMediaChannel.Video, handle.Channel);
        Assert.Equal(11, handle.LengthBytes);
        Assert.True(handle.Verified);
        Assert.Matches("^input-[0-9a-f]{32}$", handle.InputKey);
        Assert.Equal("[local-media-input-handle]", handle.ToString());
        Assert.Equal("[local-media-inputs]", inputs.ToString());
        Assert.DoesNotContain(videoUrlSentinel, inputs.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("https://", inputs.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.EnumerateFiles(workspace.StageRoot));

        var release = await stager.ReleaseAsync(inputs, CancellationToken.None);

        Assert.True(release.IsSuccess);
        Assert.Equal(new StageReleaseResult(1, true), release.Value);
        Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));

        var repeatedRelease = await stager.ReleaseAsync(inputs, CancellationToken.None);
        Assert.True(repeatedRelease.IsSuccess);
        Assert.Equal(new StageReleaseResult(0, true), repeatedRelease.Value);
        Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));
    }

    [Fact]
    public async Task Stages_video_and_audio_without_combining_sources()
    {
        using var workspace = Workspace.Create();
        using var client = new HttpClient(new ResponseHandler(request =>
            Response(HttpStatusCode.OK, request.RequestUri!.AbsolutePath.Contains("audio", StringComparison.Ordinal)
                ? "audio"
                : "video")));
        var stager = new LocalStreamStager(client, workspace.StageRoot);
        var result = await stager.StageAsync(Plan(video: true, audio: true), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value!.Video);
        Assert.NotNull(result.Value.Audio);
        Assert.NotEqual(result.Value.Video!.InputKey, result.Value.Audio!.InputKey);
        Assert.Equal(5, result.Value.Video.LengthBytes);
        Assert.Equal(5, result.Value.Audio.LengthBytes);
        Assert.Equal(2, Directory.EnumerateFiles(workspace.StageRoot).Count());

        var released = await stager.ReleaseAsync(result.Value, CancellationToken.None);
        Assert.True(released.IsSuccess);
        Assert.Equal(2, released.Value!.ReleasedCount);
        Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Rejects_redirect_and_unsuccessful_status_without_leaking_response_details(HttpStatusCode status)
    {
        using var workspace = Workspace.Create();
        const string responseBodySentinel = "body-token-value";
        var responseBody = "prefix-" + responseBodySentinel + "-suffix";
        Assert.Contains(responseBodySentinel, responseBody, StringComparison.Ordinal);
        using var client = new HttpClient(new ResponseHandler(_ => Response(status, responseBody)));
        var stager = new LocalStreamStager(client, workspace.StageRoot);

        var result = await stager.StageAsync(Plan(video: true, audio: false), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.ProviderUnavailable, result.Error!.Code);
        Assert.Equal(DownloadStage.Downloading, result.Error.Stage);
        Assert.DoesNotContain("body-token-value", result.Error.UserMessage, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));
    }

    [Fact]
    public async Task Rejects_non_http_empty_and_oversized_sources_before_or_during_copy()
    {
        using var workspace = Workspace.Create();
        using var client = new HttpClient(new ResponseHandler(_ => Response(HttpStatusCode.OK, "123456")));
        var stager = new LocalStreamStager(client, workspace.StageRoot, maximumResponseBytes: 5);

        var tooLarge = await stager.StageAsync(Plan(video: true, audio: false), CancellationToken.None);
        Assert.False(tooLarge.IsSuccess);
        Assert.Equal(DownloadErrorCode.InvalidRequest, tooLarge.Error!.Code);
        Assert.Equal("diag-local-stream-too-large", tooLarge.Error.Diagnostic.Value);
        Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));

        var emptyClient = new HttpClient(new ResponseHandler(_ => Response(HttpStatusCode.OK, string.Empty)));
        var empty = await new LocalStreamStager(emptyClient, workspace.StageRoot)
            .StageAsync(Plan(video: true, audio: false), CancellationToken.None);
        Assert.False(empty.IsSuccess);
        Assert.Equal("diag-local-stream-empty", empty.Error!.Diagnostic.Value);

        var invalidPlan = Plan(video: true, audio: false) with
        {
            VideoSource = new MediaSource(new Uri("file:///not-a-stream"))
        };
        var invalid = await stager.StageAsync(invalidPlan, CancellationToken.None);
        Assert.False(invalid.IsSuccess);
        Assert.Equal("diag-local-stream-request-invalid", invalid.Error!.Diagnostic.Value);
        Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));
    }

    [Fact]
    public async Task Cancellation_is_typed_and_partial_files_are_cleaned()
    {
        using var workspace = Workspace.Create();
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new ResponseHandler(_ =>
        {
            cancellation.Cancel();
            return Response(HttpStatusCode.OK, "cancelled");
        }));
        var stager = new LocalStreamStager(client, workspace.StageRoot);

        var result = await stager.StageAsync(Plan(video: true, audio: false), cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(DownloadStage.Downloading, result.Error.Stage);
        Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));
    }

    [Fact]
    public async Task Fabricated_duplicate_and_mismatched_handles_are_rejected_without_adapter_invocation()
    {
        using var workspace = Workspace.Create();
        using var client = new HttpClient(new ResponseHandler(_ => Response(HttpStatusCode.OK, "video")));
        var stager = new LocalStreamStager(client, workspace.StageRoot);
        var plan = Plan(video: true, audio: false);
        var staged = await stager.StageAsync(plan, CancellationToken.None);
        var issued = staged.Value!.Video!;
        var fabricated = new LocalMediaInputHandle("input-00000000000000000000000000000000", LocalMediaChannel.Video, issued.LengthBytes, true);
        var adapter = new FfmpegProcessAdapter(new BoundedProcessExecutor(new Dictionary<string, ToolConfiguration>()));
        var bridge = new StagedFfmpegProcessor(adapter, stager, new FfmpegStageTarget(workspace.StageRoot));

        var unknown = await bridge.ProcessAsync(plan, new LocalMediaInputs(fabricated), CancellationToken.None);
        Assert.False(unknown.IsSuccess);
        Assert.Equal("diag-local-stream-request-invalid", unknown.Error!.Diagnostic.Value);

        var mismatchedPlan = Plan(video: true, audio: false) with
        {
            VideoSource = new MediaSource(new Uri("https://media.example.test/other"))
        };
        var mismatched = await bridge.ProcessAsync(mismatchedPlan, new LocalMediaInputs(issued), CancellationToken.None);
        Assert.False(mismatched.IsSuccess);

        var duplicate = await bridge.ProcessAsync(
            Plan(video: true, audio: true),
            new LocalMediaInputs(issued, issued),
            CancellationToken.None);
        Assert.False(duplicate.IsSuccess);

        var release = await stager.ReleaseAsync(staged.Value, CancellationToken.None);
        Assert.True(release.IsSuccess);
    }

    [Fact]
    public async Task Bridge_maps_issued_path_privately_and_uses_injected_stage_target()
    {
        using var workspace = Workspace.Create();
        using var client = new HttpClient(new ResponseHandler(_ => Response(HttpStatusCode.OK, "video")));
        var stager = new LocalStreamStager(client, workspace.StageRoot);
        var plan = Plan(video: true, audio: false);
        const string remoteSourceSentinel = "https://media.example.test/video";
        Assert.Contains(remoteSourceSentinel, plan.VideoSource!.Address.AbsoluteUri, StringComparison.Ordinal);
        const string finalDestinationSentinel = "/tmp/final-destination";
        Assert.Contains(finalDestinationSentinel, plan.Request.Output.Directory, StringComparison.Ordinal);
        var staged = await stager.StageAsync(plan, CancellationToken.None);
        using var fixture = FfmpegFixture.Create();
        var adapter = new FfmpegProcessAdapter(fixture.Executor());
        var bridge = new StagedFfmpegProcessor(adapter, stager, new FfmpegStageTarget(workspace.StageRoot));

        var result = await bridge.ProcessAsync(plan, staged.Value!, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var arguments = fixture.ReadArguments();
        Assert.DoesNotContain(arguments, argument => argument.Contains(plan.VideoSource!.Address.AbsoluteUri, StringComparison.Ordinal));
        Assert.DoesNotContain(finalDestinationSentinel, arguments[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("final", arguments[^1], StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(workspace.StageRoot), Path.GetFullPath(arguments[^1]), StringComparison.Ordinal);
        Assert.True(File.Exists(arguments[^1]));
        await stager.ReleaseAsync(Assert.IsType<LocalMediaInputs>(staged.Value), CancellationToken.None);
    }

    [Fact]
    public async Task Release_is_idempotent_for_issued_handles_and_rejects_mixed_unknown_handles()
    {
        using var workspace = Workspace.Create();
        using var client = new HttpClient(new ResponseHandler(request =>
            Response(HttpStatusCode.OK, request.RequestUri!.AbsolutePath.Contains("audio", StringComparison.Ordinal)
                ? "audio"
                : "video")));
        var stager = new LocalStreamStager(client, workspace.StageRoot);
        var staged = await stager.StageAsync(Plan(video: true, audio: true), CancellationToken.None);
        Assert.True(staged.IsSuccess);
        var inputs = staged.Value!;
        var video = inputs.Video!;
        var audio = inputs.Audio!;
        var unrelatedPath = Path.Combine(workspace.StageRoot, "unrelated.stream");
        File.WriteAllText(unrelatedPath, "keep");

        var firstRelease = await stager.ReleaseAsync(inputs, CancellationToken.None);
        Assert.True(firstRelease.IsSuccess);
        Assert.Equal(new StageReleaseResult(2, true), firstRelease.Value);

        var repeatedRelease = await stager.ReleaseAsync(inputs, CancellationToken.None);
        Assert.True(repeatedRelease.IsSuccess);
        Assert.Equal(new StageReleaseResult(0, true), repeatedRelease.Value);

        var repeatedSubset = await stager.ReleaseAsync(new LocalMediaInputs(video), CancellationToken.None);
        Assert.True(repeatedSubset.IsSuccess);
        Assert.Equal(new StageReleaseResult(0, true), repeatedSubset.Value);

        var fabricated = new LocalMediaInputHandle(
            "input-00000000000000000000000000000000",
            LocalMediaChannel.Video,
            video.LengthBytes,
            verified: true);
        var mixedUnknown = await stager.ReleaseAsync(new LocalMediaInputs(fabricated, audio), CancellationToken.None);
        Assert.False(mixedUnknown.IsSuccess);
        Assert.Equal("diag-local-stream-cleanup-failed", mixedUnknown.Error!.Diagnostic.Value);
        Assert.True(File.Exists(unrelatedPath));
        Assert.Single(Directory.EnumerateFiles(workspace.StageRoot));
    }

    private static MediaPlan Plan(bool video, bool audio) => new(
        new DownloadRequest(
            new VideoReference(new Uri("https://video.example.test/watch")),
            video ? DownloadOperation.Video : DownloadOperation.Audio,
            new OutputOptions("/tmp/final-destination", "user-file"),
            BrowserSessionSelection.Create(BrowserKind.Chrome)),
        new MediaCharacteristics(OutputContainer.Mp4, VideoCodec.Av1, AudioCodec.Aac, video, audio, 30),
        video ? new MediaSource(new Uri("https://media.example.test/video")) : null,
        audio ? new MediaSource(new Uri("https://media.example.test/audio")) : null);

    private static HttpResponseMessage Response(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/octet-stream")
        };
        if (status == HttpStatusCode.Redirect)
        {
            response.Headers.Location = new Uri("https://redirect.example.test/secret");
        }

        return response;
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }

    private sealed class Workspace : IDisposable
    {
        private Workspace(string root)
        {
            Root = root;
            StageRoot = Path.Combine(root, "stage");
            Directory.CreateDirectory(StageRoot);
        }

        public string Root { get; }
        public string StageRoot { get; }
        public static Workspace Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "unifi-staging-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Workspace(root);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FfmpegFixture : IDisposable
    {
        private FfmpegFixture(string path, string argumentsPath, string hash)
        {
            Path = path;
            ArgumentsPath = argumentsPath;
            Hash = hash;
        }

        public string Path { get; }
        public string ArgumentsPath { get; }
        public string Hash { get; }
        public static string Repository => "https://fixture.example.test/repository";

        public static FfmpegFixture Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unifi-stager-ffmpeg-" + Guid.NewGuid().ToString("N") + ".sh");
            var argumentsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unifi-stager-args-" + Guid.NewGuid().ToString("N") + ".txt");
            var script = "#!/bin/sh\nprintf '%s\\n' \"$@\" > '" + argumentsPath.Replace("'", "'\\''", StringComparison.Ordinal) + "'\noutput=''\nfor arg in \"$@\"; do output=\"$arg\"; done\nprintf ok > \"$output\"\n";
            File.WriteAllText(path, script);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            using var stream = File.OpenRead(path);
            return new FfmpegFixture(path, argumentsPath, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }

        public BoundedProcessExecutor Executor() => new(
            new Dictionary<string, ToolConfiguration> { [nameof(ToolKey.Ffmpeg)] = new ToolConfiguration(ToolKey.Ffmpeg, "fixture", Repository, "1.0", "linux-arm64", Hash, true, Path) },
            new ProcessExecutorOptions(32_768, TimeSpan.FromSeconds(5)),
            new HashSet<string> { Repository },
            "linux-arm64",
            new Dictionary<ToolKey, ToolExpectation> { [ToolKey.Ffmpeg] = new ToolExpectation(ToolKey.Ffmpeg, "fixture", Repository, "1.0", "linux-arm64", Hash) });

        public string[] ReadArguments() => File.ReadAllLines(ArgumentsPath);

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (File.Exists(ArgumentsPath)) File.Delete(ArgumentsPath);
        }
    }
}
