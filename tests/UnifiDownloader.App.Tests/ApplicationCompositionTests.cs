using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnifiDownloader.App.Composition;
using UnifiDownloader.App.Presentation;
using UnifiDownloader.Infrastructure;
using UnifiDownloader.Domain;
using Xunit;

namespace UnifiDownloader.App.Tests;

public sealed class ApplicationCompositionTests
{

    [Fact]
    public void Missing_or_mismatched_manifest_is_an_empty_safe_configuration()
    {
        using var workspace = TestWorkspace.Create();
        var missing = ToolManifestLoader.Load(Path.Combine(workspace.Root, "missing.json"));
        Assert.Empty(missing.Tools);
        Assert.Empty(missing.TrustedToolExpectations!);

        var manifest = workspace.WriteManifest(executionTargetRid: "win-x64");
        var mismatched = ToolManifestLoader.Load(manifest);
        Assert.Empty(mismatched.Tools);
        Assert.Empty(mismatched.TrustedToolExpectations!);
    }

    [Fact]
    public async Task Missing_manifest_composes_safe_non_startable_capabilities()
    {
        using var workspace = TestWorkspace.Create();
        var composed = ApplicationComposition.Create(
            new InlineDispatcher(),
            Path.Combine(workspace.Root, "missing.json"));
        var stagingRoot = composed.StagingRoot;
        var leftover = Path.Combine(stagingRoot, "leftover.tmp");
        File.WriteAllText(leftover, "composition-owned leftover");
        Assert.True(File.Exists(leftover));
        try
        {
            composed.ViewModel.VideoUrl = "https://fixture.invalid/watch?v=one";
            composed.ViewModel.OutputFolder = workspace.OutputRoot;
            composed.ViewModel.FileStem = "not-started";
            Assert.False(composed.ViewModel.CanStart);
            Assert.All(composed.ViewModel.Capabilities, status => Assert.Equal("Unavailable", status.Status));
            Assert.False(await composed.Controller.StartAsync());
            Assert.False(File.Exists(Path.Combine(workspace.OutputRoot, "not-started.mp4")));
        }
        finally
        {
            composed.Dispose();
        }
        Assert.False(Directory.Exists(stagingRoot));
    }

    [Fact]
    public void Valid_manifest_resolves_relative_executables_and_keeps_expectations_separate()
    {
        using var workspace = TestWorkspace.Create();
        var manifest = workspace.WriteManifest();
        var configuration = ToolManifestLoader.Load(manifest);

        Assert.Equal(4, configuration.Tools.Count);
        Assert.Equal(4, configuration.TrustedToolExpectations!.Count);
        Assert.All(configuration.Tools.Values, tool =>
        {
            Assert.True(Path.IsPathFullyQualified(tool.ExecutablePath));
            Assert.True(File.Exists(tool.ExecutablePath));
            Assert.True(tool.IsVerified);
            Assert.Contains(tool.SourceRepository, configuration.AllowedToolRepositories!);
        });
        Assert.Equal(
            configuration.Tools[ToolKey.YtDlp.ToString()].ExpectedSha256,
            configuration.TrustedToolExpectations[ToolKey.YtDlp].ExpectedSha256);
    }

    [Fact]
    public async Task Valid_manifest_composes_environment_and_publishes_a_verified_mp4()
    {
        if (OperatingSystem.IsWindows()) return;

        using var workspace = TestWorkspace.Create();
        var manifest = workspace.WriteManifest();
        var composed = ApplicationComposition.Create(new InlineDispatcher(), manifest);
        var stagingRoot = composed.StagingRoot;
        try
        {
            await composed.Controller.TestEnvironmentAsync();
            Assert.Equal(5, composed.ViewModel.Capabilities.Count);
            Assert.All(composed.ViewModel.Capabilities.Take(4), status => Assert.Equal("Available", status.Status));
            Assert.Equal("Available", composed.ViewModel.Capabilities[4].Status);

            composed.ViewModel.VideoUrl = "https://fixture.invalid/watch?v=one";
            composed.ViewModel.OutputFolder = workspace.OutputRoot;
            composed.ViewModel.FileStem = "published-fixture";
            composed.ViewModel.SelectedContainer = OutputContainer.UnifiMp4;
            Assert.True(composed.ViewModel.CanStart);

            Assert.True(await composed.Controller.StartAsync());
            Assert.Equal(ScreenState.Completed, composed.ViewModel.ScreenState);
            Assert.NotNull(composed.ViewModel.PublishedFile);
            var publishedPath = Path.Combine(workspace.OutputRoot, "published-fixture.mp4");
            Assert.True(File.Exists(publishedPath));
            Assert.Equal("fixture-mp4", await File.ReadAllTextAsync(publishedPath, TestContext.Current.CancellationToken));
            Assert.Equal(new FileInfo(publishedPath).Length, composed.ViewModel.PublishedFile!.LengthBytes);
        }
        finally
        {
            composed.Dispose();
        }
        Assert.False(Directory.Exists(stagingRoot));
        Assert.True(File.Exists(Path.Combine(workspace.OutputRoot, "published-fixture.mp4")));
    }

    private sealed class FixtureHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("fixture.invalid", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("fixture-input", Encoding.UTF8, "application/octet-stream")
            });
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        private TestWorkspace(string root)
        {
            Root = root;
            OutputRoot = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        }

        public string Root { get; }
        public string OutputRoot { get; }

        public static TestWorkspace Create() => new(Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"unifi-composition-{Guid.NewGuid():N}")).FullName);

        public string WriteManifest(string? executionTargetRid = null)
        {
            var repositories = new[] { "https://example.invalid/yt-dlp", "https://example.invalid/deno", "https://example.invalid/ffmpeg", "https://example.invalid/ffprobe" };
            var tools = new Dictionary<string, object>(StringComparer.Ordinal);
            var expectations = new Dictionary<string, object>(StringComparer.Ordinal);
            AddTool("YtDlp", "yt-dlp.sh", repositories[0], "2026.08.19", YtDlpScript());
            AddTool("Deno", "deno.sh", repositories[1], "2.3.0", "#!/bin/sh\nprintf '2.3.0\\n'");
            AddTool("Ffmpeg", "ffmpeg.sh", repositories[2], "7.0", "#!/bin/sh\nif [ \"$1\" = \"-version\" ]; then printf 'ffmpeg version 7.0\\n'; exit 0; fi\nfor argument do output=\"$argument\"; done\nprintf 'fixture-mp4' > \"$output\"");
            AddTool("Ffprobe", "ffprobe.sh", repositories[3], "7.0", "#!/bin/sh\nprintf 'ffprobe version 7.0\\n'");

            var path = Path.Combine(Root, ToolManifestLoader.ManifestFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schemaVersion = ToolManifestLoader.CurrentSchemaVersion,
                executionTargetRid = executionTargetRid ?? RuntimeInformation.RuntimeIdentifier,
                allowedRepositories = repositories,
                tools,
                trustedExpectations = expectations
            }, ManifestJsonOptions));
            return path;

            void AddTool(string key, string fileName, string repository, string version, string script)
            {
                var executable = Path.Combine(Root, fileName);
                File.WriteAllText(executable, script + "\n");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                var hash = Hash(executable);
                tools.Add(key, new
                {
                    key,
                    assetName = fileName,
                    sourceRepository = repository,
                    version,
                    targetRid = RuntimeInformation.RuntimeIdentifier,
                    expectedSha256 = hash,
                    isVerified = true,
                    executablePath = fileName,
                    manifestSha256 = hash,
                    apiSha256 = hash
                });
                expectations.Add(key, new
                {
                    key,
                    assetName = fileName,
                    sourceRepository = repository,
                    version,
                    targetRid = RuntimeInformation.RuntimeIdentifier,
                    expectedSha256 = hash,
                    requireVerified = true
                });
            }
        }

        private static string YtDlpScript() =>
            $"#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then printf '2026.08.19\\n'; exit 0; fi\nformat=''\noutput=''\nwhile [ \"$#\" -gt 0 ]; do if [ \"$1\" = \"--format\" ]; then format=\"$2\"; shift 2; continue; fi; if [ \"$1\" = \"--output\" ]; then output=\"$2\"; shift 2; continue; fi; shift; done\nif [ -n \"$format\" ]; then printf 'fixture-input' > \"$output\"; exit 0; fi\nprintf '%s' '{{\"title\":\"Fixture title\",\"duration\":1,\"formats\":[{{\"format_id\":\"video\",\"url\":\"https://fixture.invalid/video.mp4\",\"ext\":\"mp4\",\"vcodec\":\"avc1\",\"acodec\":\"none\",\"height\":720,\"width\":1280,\"fps\":30,\"tbr\":500}}]}}'";

        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }
}
