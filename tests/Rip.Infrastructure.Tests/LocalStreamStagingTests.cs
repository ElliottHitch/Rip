using System.Security.Cryptography;
using Rip.Application;
using Rip.Domain;

namespace Rip.Infrastructure.Tests;

public sealed class LocalStreamStagingTests
{
    [Fact]
    public async Task Progressive_format_18_is_staged_once_as_one_muxed_input()
    {
        using var fixture = Fixture.Create("progressive");
        using var workspace = Workspace.Create();
        var stager = new LocalStreamStager(CreateExecutor(fixture), "/verified/deno", workspace.StageRoot);
        var plan = Plan(progressive: true);

        var result = await stager.StageAsync(plan, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value!.Video);
        Assert.Null(result.Value.Audio);
        Assert.Single(Directory.EnumerateFiles(workspace.StageRoot));
        Assert.Equal(1, fixture.Invocations);
        var formatIndex = Array.IndexOf(fixture.Arguments, "--format");
        Assert.True(formatIndex >= 0 && formatIndex + 1 < fixture.Arguments.Length);
        Assert.Equal("18", fixture.Arguments[formatIndex + 1]);
        Assert.Contains("--max-filesize", fixture.Arguments);
        Assert.Contains(LocalStreamStager.DefaultMaximumResponseBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), fixture.Arguments);
        Assert.Contains("https://video.example.test/watch", fixture.Arguments);
        Assert.DoesNotContain(fixture.Arguments, argument => argument.Contains("audio", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("signature=", plan.ToString(), StringComparison.OrdinalIgnoreCase);

        var released = await stager.ReleaseAsync(result.Value, CancellationToken.None);
        Assert.True(released.IsSuccess);
        Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));
    }

    [Fact]
    public async Task Dedicated_video_and_audio_formats_are_each_staged_once()
    {
        using var fixture = Fixture.Create("pair");
        using var workspace = Workspace.Create();
        var stager = new LocalStreamStager(CreateExecutor(fixture), "/verified/deno", workspace.StageRoot);

        var result = await stager.StageAsync(Plan(progressive: false), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value!.Video);
        Assert.NotNull(result.Value.Audio);
        Assert.Equal(2, fixture.Invocations);
        Assert.Equal(2, Directory.EnumerateFiles(workspace.StageRoot).Count());
        Assert.Contains("video-format", fixture.AllArguments);
        Assert.Contains("audio-format", fixture.AllArguments);
        Assert.Equal(2, fixture.AllArguments.Count(argument => argument == "--format"));
        Assert.True((await stager.ReleaseAsync(result.Value, CancellationToken.None)).IsSuccess);
        Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));
    }

    [Fact]
    public async Task Exact_five_gib_bound_is_accepted_and_post_check_cleans_too_large_output()
    {
        using var fixture = Fixture.Create("exact");
        using var workspace = Workspace.Create();
        var stager = new LocalStreamStager(CreateExecutor(fixture), "/verified/deno", workspace.StageRoot, maximumResponseBytes: 5);

        var exact = await stager.StageAsync(Plan(progressive: true), CancellationToken.None);
        Assert.True(exact.IsSuccess);
        Assert.Equal(5, exact.Value!.Video!.LengthBytes);
        Assert.True((await stager.ReleaseAsync(exact.Value, CancellationToken.None)).IsSuccess);

        using var oversizedFixture = Fixture.Create("oversize");
        var oversizedStager = new LocalStreamStager(CreateExecutor(oversizedFixture), "/verified/deno", workspace.StageRoot, maximumResponseBytes: 5);
        var oversized = await oversizedStager.StageAsync(Plan(progressive: true), CancellationToken.None);
        Assert.False(oversized.IsSuccess);
        Assert.Equal(DownloadErrorCode.TooLarge, oversized.Error!.Code);
        Assert.Equal("diag-local-stream-too-large", oversized.Error.Diagnostic.Value);
        Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));
    }

    [Fact]
    public async Task Process_stager_passes_only_public_reference_format_and_explicit_browser_kind()
    {
        const string expansionSentinel = "/tmp/stager-shell-expansion-sentinel";
        if (File.Exists(expansionSentinel)) File.Delete(expansionSentinel);
        using var fixture = Fixture.Create("progressive");
        using var workspace = Workspace.Create();
        var stager = new LocalStreamStager(CreateExecutor(fixture), "/verified/deno", workspace.StageRoot);
        var publicReference = $"https://video.example.test/watch?v=$(touch {expansionSentinel})";
        var plan = Plan(progressive: true) with
        {
            Request = Plan(progressive: true).Request with
            {
                Video = new VideoReference(new Uri(publicReference)),
                BrowserSession = BrowserSessionSelection.Create(BrowserKind.Chrome)
            }
        };

        var result = await stager.StageAsync(plan, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(new Uri(publicReference).AbsoluteUri, fixture.AllArguments);
        Assert.Contains("--cookies-from-browser", fixture.AllArguments);
        Assert.Contains("chrome", fixture.AllArguments);
        Assert.DoesNotContain(fixture.AllArguments, argument => argument.Contains("profile", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fixture.AllArguments, argument => argument.Contains("signature=", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(expansionSentinel));
        await stager.ReleaseAsync(result.Value!, CancellationToken.None);
    }

    [Fact]
    public async Task Missing_output_is_empty_error_and_is_cleaned()
    {
        using var fixture = Fixture.Create("empty");
        using var workspace = Workspace.Create();
        var stager = new LocalStreamStager(CreateExecutor(fixture), "/verified/deno", workspace.StageRoot);

        var result = await stager.StageAsync(Plan(progressive: true), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("diag-local-stream-empty", result.Error!.Diagnostic.Value);
        Assert.Empty(Directory.EnumerateFiles(workspace.StageRoot));
    }

    private static MediaPlan Plan(bool progressive) => new(
        new DownloadRequest(
            new VideoReference(new Uri("https://video.example.test/watch")),
            DownloadOperation.Video,
            new OutputOptions("/tmp/final-destination", "fixture"),
            null),
        new MediaCharacteristics(OutputContainer.Matroska, VideoCodec.H264, AudioCodec.Aac, true, true, 30),
        progressive ? "18" : "video-format",
        progressive ? null : "audio-format",
        IsProgressive: progressive);

    private static BoundedProcessExecutor CreateExecutor(Fixture fixture) => new(
        new Dictionary<string, ToolConfiguration> { [ToolKey.YtDlp.ToString()] = fixture.Configuration() },
        new ProcessExecutorOptions(32_768, TimeSpan.FromSeconds(5)),
        new HashSet<string> { fixture.Repository },
        "linux-arm64",
        new Dictionary<ToolKey, ToolExpectation> { [ToolKey.YtDlp] = fixture.TrustedExpectation() });

    private sealed class Fixture : IDisposable
    {
        private Fixture(string path, string argumentsPath, string hash, string mode)
        {
            Path = path;
            ArgumentsPath = argumentsPath;
            Hash = hash;
            Mode = mode;
        }

        public string Path { get; }
        public string ArgumentsPath { get; }
        public string Hash { get; }
        public string Mode { get; set; }
        public string Repository { get; } = "https://example.invalid/yt-dlp-fixture";
        public string[] Arguments => File.Exists(ArgumentsPath) ? File.ReadAllLines(ArgumentsPath) : [];
        public string[] AllArguments => Arguments;
        public int Invocations => Arguments.Length == 0 ? 0 : Arguments.Count(argument => argument == "--format");

        public static Fixture Create(string mode)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stager-{Guid.NewGuid():N}.sh");
            var argumentsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stager-args-{Guid.NewGuid():N}.txt");
            var body = $"#!/bin/sh\nprintf '%s\\n' \"$@\" >> {ShellQuote(argumentsPath)}\noutput=''\nformat=''\nwhile [ \"$#\" -gt 0 ]; do if [ \"$1\" = '--output' ]; then output=\"$2\"; shift 2; continue; fi; if [ \"$1\" = '--format' ]; then format=\"$2\"; fi; shift; done\n";
            body += $"case \"$format\" in 18|video-format|audio-format) ;; *) exit 23;; esac\n";
            body += $"if [ \"{mode}\" = 'empty' ]; then exit 0; fi\n";
            body += "if [ \"$format\" = 'audio-format' ]; then printf audio > \"$output\"; else ";
            body += mode == "oversize" ? "printf 123456" : "printf 12345";
            body += " > \"$output\"; fi\nexit 0\n";
            File.WriteAllText(path, body);
            File.WriteAllText(argumentsPath, string.Empty);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var stream = File.OpenRead(path);
            return new Fixture(path, argumentsPath, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), mode);
        }

        public ToolConfiguration Configuration() => new(ToolKey.YtDlp, "yt-dlp-fixture", Repository, "2026.08.19", "linux-arm64", Hash, true, Path);
        public ToolExpectation TrustedExpectation() => new(ToolKey.YtDlp, "yt-dlp-fixture", Repository, "2026.08.19", "linux-arm64", Hash);
        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (File.Exists(ArgumentsPath)) File.Delete(ArgumentsPath);
        }

        private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private sealed class Workspace : IDisposable
    {
        private Workspace(string root) => StageRoot = root;
        public string StageRoot { get; }
        public static Workspace Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stager-root-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new Workspace(root);
        }
        public void Dispose() { if (Directory.Exists(StageRoot)) Directory.Delete(StageRoot, recursive: true); }
    }
}
