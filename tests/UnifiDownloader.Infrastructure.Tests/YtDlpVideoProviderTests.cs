using System.Security.Cryptography;
using UnifiDownloader.Application;
using UnifiDownloader.Domain;
using UnifiDownloader.Infrastructure;

namespace UnifiDownloader.Infrastructure.Tests;

public sealed class YtDlpVideoProviderTests
{
    private const string VideoUrl = "https://example.invalid/watch?v=fixture";
    private const string VideoSource = "https://cdn.example.invalid/video.mp4?signature=source-sentinel";
    private const string AudioSource = "https://cdn.example.invalid/audio.m4a?token=source-token-sentinel";

    [Fact]
    public async Task Metadata_uses_the_pinned_policy_and_keeps_url_as_one_final_argument()
    {
        using var tool = FixtureTool.Create(ValidJson());
        var provider = CreateProvider(tool);

        var result = await provider.ReadMetadataAsync(new VideoReference(new Uri(VideoUrl)), null, CancellationToken.None);
        var arguments = tool.ReadArguments();

        Assert.True(result.IsSuccess);
        Assert.Equal("Fixture title", result.Value!.Title);
        Assert.Equal(TimeSpan.FromSeconds(125), result.Value.Duration);
        Assert.Equal("Fixture uploader", result.Value.Uploader);
        Assert.Equal(new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero), result.Value.PublishedAt);
        Assert.Equal(
            [
                "--ignore-config", "--no-js-runtimes", "--js-runtimes", "deno:/verified/deno",
                "--no-remote-components", "--dump-single-json", "--no-warnings", "--no-progress",
                "--skip-download", "--no-playlist", VideoUrl
            ],
            arguments);
    }

    [Fact]
    public async Task Selected_browser_is_mapped_without_accepting_profile_or_cookie_data()
    {
        using var tool = FixtureTool.Create(ValidJson());
        var provider = CreateProvider(tool);

        var result = await provider.ReadMetadataAsync(
            new VideoReference(new Uri(VideoUrl)),
            BrowserSessionSelection.Create(BrowserKind.Firefox),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [
                "--ignore-config", "--no-js-runtimes", "--js-runtimes", "deno:/verified/deno",
                "--no-remote-components", "--dump-single-json", "--no-warnings", "--no-progress",
                "--skip-download", "--no-playlist", "--cookies-from-browser", "firefox", VideoUrl
            ],
            tool.ReadArguments());
        Assert.DoesNotContain(tool.ReadArguments(), static argument => argument.Contains("profile", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tool.ReadArguments(), static argument => argument.Contains("cookie=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Video_and_audio_resolution_are_deterministic_and_prefer_dedicated_formats()
    {
        var json = """
        {"title":"Fixture","formats":[
          {"format_id":"mixed-high","url":"https://cdn.example.invalid/mixed.mp4","ext":"mp4","vcodec":"avc1","acodec":"mp4a.40.2","height":2160,"width":3840,"fps":60,"tbr":2000},
          {"format_id":"video-low","url":"https://cdn.example.invalid/video-low.mp4","ext":"mp4","vcodec":"avc1","acodec":"none","height":720,"width":1280,"fps":30,"tbr":1200},
          {"format_id":"video-high","url":"https://cdn.example.invalid/video-high.mp4","ext":"mp4","vcodec":"avc1","acodec":"none","height":1080,"width":1920,"fps":30,"tbr":1500},
          {"format_id":"audio-low","url":"https://cdn.example.invalid/audio-low.m4a","ext":"m4a","vcodec":"none","acodec":"mp4a.40.2","abr":96},
          {"format_id":"audio-high","url":"https://cdn.example.invalid/audio-high.m4a","ext":"m4a","vcodec":"none","acodec":"mp4a.40.2","abr":160}
        ]}
        """;
        using var tool = FixtureTool.Create(json);
        var provider = CreateProvider(tool);
        var videoRequest = Request(DownloadOperation.Video);
        var audioRequest = Request(DownloadOperation.Audio);

        var video = await provider.ResolveMediaAsync(videoRequest, new MetadataSnapshot("Fixture", null, null, null), CancellationToken.None);
        var audio = await provider.ResolveMediaAsync(audioRequest, new MetadataSnapshot("Fixture", null, null, null), CancellationToken.None);

        Assert.True(video.IsSuccess);
        Assert.Equal("https://cdn.example.invalid/video-high.mp4", video.Value!.VideoSource!.Address.AbsoluteUri);
        Assert.Equal("https://cdn.example.invalid/audio-high.m4a", video.Value.AudioSource!.Address.AbsoluteUri);
        Assert.True(video.Value.Characteristics.HasVideo);
        Assert.True(video.Value.Characteristics.HasAudio);
        Assert.Equal(VideoCodec.H264, video.Value.Characteristics.VideoCodec);
        Assert.True(audio.IsSuccess);
        Assert.Equal("https://cdn.example.invalid/audio-high.m4a", audio.Value!.AudioSource!.Address.AbsoluteUri);
        Assert.Null(audio.Value.VideoSource);
        Assert.False(audio.Value.Characteristics.HasVideo);
        Assert.True(audio.Value.Characteristics.HasAudio);
        Assert.Equal(AudioCodec.Aac, audio.Value.Characteristics.AudioCodec);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"_type\":\"playlist\",\"entries\":[]}")]
    [InlineData("{\"title\":\"missing formats\"}")]
    [InlineData("{\"title\":123,\"formats\":[]}")]
    [InlineData("{\"title\":\"bad number\",\"formats\":[{\"format_id\":\"1\",\"url\":\"https://cdn.example.invalid/v.mp4\",\"ext\":\"mp4\",\"vcodec\":\"avc1\",\"acodec\":\"none\",\"height\":-1}]}")]
    public async Task Invalid_provider_shapes_fail_closed_without_raw_child_data(string output)
    {
        using var tool = FixtureTool.Create(output);
        var provider = CreateProvider(tool);

        var result = await provider.ReadMetadataAsync(new VideoReference(new Uri(VideoUrl)), null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Equal(DownloadStage.Metadata, result.Error.Stage);
        Assert.Equal("diag-provider-response-invalid", result.Error.Diagnostic.Value);
    }

    [Fact]
    public async Task Invalid_provider_response_redacts_fixture_sentinels()
    {
        var fixture = InvalidProviderResponseFixture();
        using var tool = FixtureTool.Create(fixture);
        var provider = CreateProvider(tool);

        Assert.Contains("secret", fixture, StringComparison.Ordinal);
        Assert.Contains("file:", fixture, StringComparison.Ordinal);
        Assert.Contains(VideoUrl, fixture, StringComparison.Ordinal);

        var result = await provider.ReadMetadataAsync(new VideoReference(new Uri(VideoUrl)), null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Equal(DownloadStage.Metadata, result.Error.Stage);
        Assert.Equal("diag-provider-response-invalid", result.Error.Diagnostic.Value);
        Assert.DoesNotContain("secret", result.Error.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file:", result.Error.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(VideoUrl, result.Error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_many_formats_are_rejected_before_selection()
    {
        var formats = string.Join(',', Enumerable.Range(0, 129).Select(index =>
            $"{{\"format_id\":\"{index}\",\"url\":\"https://cdn.example.invalid/{index}.mp4\",\"ext\":\"mp4\",\"vcodec\":\"avc1\",\"acodec\":\"none\"}}"));
        using var tool = FixtureTool.Create($"{{\"title\":\"Fixture\",\"formats\":[{formats}]}}");
        var provider = CreateProvider(tool);

        var result = await provider.ReadMetadataAsync(new VideoReference(new Uri(VideoUrl)), null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.InvalidRequest, result.Error!.Code);
    }

    [Fact]
    public async Task Unsupported_operation_is_typed_and_does_not_launch_the_tool()
    {
        using var tool = FixtureTool.Create(ValidJson());
        var provider = CreateProvider(tool);

        var result = await provider.ResolveMediaAsync(
            Request(DownloadOperation.Metadata),
            new MetadataSnapshot("Fixture", null, null, null),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Equal("diag-provider-operation-unsupported", result.Error.Diagnostic.Value);
        Assert.False(File.Exists(tool.ArgumentsPath));
    }

    [Fact]
    public async Task Structured_nonzero_exit_is_propagated_without_child_diagnostics()
    {
        const string standardError = "{\"error_code\":\"access-denied\",\"detail\":\"https://cdn.example.invalid/signed?token=secret\"}";
        using var tool = FixtureTool.Create("ignored", standardError, 17);
        var provider = CreateProvider(tool);

        Assert.Contains("secret", standardError, StringComparison.Ordinal);
        Assert.Contains("cdn.example", standardError, StringComparison.Ordinal);

        var result = await provider.ReadMetadataAsync(new VideoReference(new Uri(VideoUrl)), null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.AccessDenied, result.Error!.Code);
        Assert.Equal("diag-process-access-denied", result.Error.Diagnostic.Value);
        Assert.DoesNotContain("secret", result.Error.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cdn.example", result.Error.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Timeout_and_cancellation_remain_typed_failures()
    {
        using var timeoutTool = FixtureTool.Create("ignored", sleepSeconds: 30);
        var timeoutProvider = CreateProvider(timeoutTool, TimeSpan.FromMilliseconds(100));
        var timeout = await timeoutProvider.ReadMetadataAsync(new VideoReference(new Uri(VideoUrl)), null, CancellationToken.None);

        using var cancellationTool = FixtureTool.Create("ignored", sleepSeconds: 30);
        var cancellationProvider = CreateProvider(cancellationTool, TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var cancelled = await cancellationProvider.ReadMetadataAsync(new VideoReference(new Uri(VideoUrl)), null, cancellation.Token);

        Assert.False(timeout.IsSuccess);
        Assert.Equal(DownloadErrorCode.Unknown, timeout.Error!.Code);
        Assert.False(cancelled.IsSuccess);
        Assert.Equal(DownloadErrorCode.Cancelled, cancelled.Error!.Code);
    }

    private static string InvalidProviderResponseFixture() => $$"""
    {"title":"invalid provider response","webpage_url":"{{VideoUrl}}","description":"secret","formats":[{"format_id":"unsafe","url":"file:///synthetic-secret","ext":"mp4","vcodec":"avc1","acodec":"none"}]}
    """;

    private static string ValidJson() => $"{{\"title\":\"Fixture title\",\"duration\":125,\"uploader\":\"Fixture uploader\",\"upload_date\":\"20260819\",\"formats\":[{{\"format_id\":\"video\",\"url\":\"{VideoSource}\",\"ext\":\"mp4\",\"vcodec\":\"avc1.64001f\",\"acodec\":\"none\",\"height\":1080,\"width\":1920,\"fps\":30,\"tbr\":1500,\"filesize\":1000}},{{\"format_id\":\"audio\",\"url\":\"{AudioSource}\",\"ext\":\"m4a\",\"vcodec\":\"none\",\"acodec\":\"mp4a.40.2\",\"abr\":160,\"filesize_approx\":500}}]}}";

    private static DownloadRequest Request(DownloadOperation operation) => new(
        new VideoReference(new Uri(VideoUrl)),
        operation,
        new OutputOptions("/tmp/unifi-output", "fixture"));

    private static YtDlpVideoProvider CreateProvider(FixtureTool tool, TimeSpan? timeout = null) =>
        new(CreateExecutor(tool), "/verified/deno", timeout);

    private static BoundedProcessExecutor CreateExecutor(FixtureTool tool) => new(
        new Dictionary<string, ToolConfiguration> { [ToolKey.YtDlp.ToString()] = tool.Configuration() },
        new ProcessExecutorOptions(32_768, TimeSpan.FromSeconds(5)),
        new HashSet<string> { tool.Repository },
        "linux-arm64",
        new Dictionary<ToolKey, ToolExpectation> { [ToolKey.YtDlp] = tool.TrustedExpectation() });

    private sealed class FixtureTool : IDisposable
    {
        private FixtureTool(string path, string argumentsPath, string hash)
        {
            Path = path;
            ArgumentsPath = argumentsPath;
            Hash = hash;
        }

        public string Path { get; }
        public string ArgumentsPath { get; }
        public string Hash { get; }
        public string Repository { get; } = "https://example.invalid/fixture-repository";

        public static FixtureTool Create(string output, string? standardError = null, int exitCode = 0, int sleepSeconds = 0)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"unifi-yt-dlp-{Guid.NewGuid():N}.sh");
            var argumentsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"unifi-yt-dlp-args-{Guid.NewGuid():N}.txt");
            var script = $"#!/bin/sh\nprintf '%s\\n' \"$@\" > {ShellQuote(argumentsPath)}\n" +
                (sleepSeconds > 0 ? $"sleep {sleepSeconds}\n" : string.Empty) +
                (standardError is null ? string.Empty : $"printf '%s' {ShellQuote(standardError)} >&2\n") +
                $"printf '%s' {ShellQuote(output)}\nexit {exitCode}\n";
            File.WriteAllText(path, script);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            using var stream = File.OpenRead(path);
            return new FixtureTool(path, argumentsPath, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }

        public ToolConfiguration Configuration() => new(
            ToolKey.YtDlp,
            "yt-dlp-fixture",
            Repository,
            OfficialToolFixtures.YtDlpVersion,
            "linux-arm64",
            Hash,
            true,
            Path);

        public ToolExpectation TrustedExpectation() => new(
            ToolKey.YtDlp,
            "yt-dlp-fixture",
            Repository,
            OfficialToolFixtures.YtDlpVersion,
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
}
