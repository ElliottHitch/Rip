using System.Security.Cryptography;
using UnifiDownloader.Application;
using UnifiDownloader.Domain;
using UnifiDownloader.Infrastructure;

namespace UnifiDownloader.Infrastructure.Tests;

public sealed class ToolingTests
{
    private static readonly string[] PolicyOperationArguments = ["--print", "title", "https://example.invalid/watch?v=fixture"];
    private static readonly string[] ExpectedPolicyArguments =
    [
        "--ignore-config", "--no-js-runtimes", "--js-runtimes", "deno:/app/tools/deno",
        "--no-remote-components", "--print", "title", "https://example.invalid/watch?v=fixture"
    ];

    [Fact]
    public void Verified_manifest_and_matching_digests_are_accepted()
    {
        using var tool = TemporaryTool.Create("exit 0");
        var configuration = tool.Configuration(manifestSha256: tool.Hash, apiSha256: tool.Hash);

        var result = ToolManifestValidator.Validate(
            configuration,
            tool.TrustedExpectation(),
            new HashSet<string> { tool.Repository });

        Assert.True(result.IsValid);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Candidate_metadata_cannot_define_the_trusted_expectation()
    {
        using var tool = TemporaryTool.Create("exit 0");
        var trusted = tool.TrustedExpectation();
        var candidate = tool.Configuration() with
        {
            AssetName = "arbitrary-asset",
            Version = "0.0.0",
            TargetRid = "win-x64",
            ExpectedSha256 = new string('a', 64)
        };

        var result = ToolManifestValidator.Validate(candidate, trusted, new HashSet<string> { tool.Repository });

        Assert.False(result.IsValid);
        Assert.Contains(ToolValidationFailure.WrongAsset, result.Failures);
        Assert.Contains(ToolValidationFailure.WrongVersion, result.Failures);
        Assert.Contains(ToolValidationFailure.WrongTargetRid, result.Failures);
        Assert.Contains(ToolValidationFailure.HashMismatch, result.Failures);
    }

    [Fact]
    public void Provenance_validation_fails_closed_for_unverified_wrong_and_conflicting_inputs()
    {
        using var tool = TemporaryTool.Create("exit 0");
        var configuration = tool.Configuration(
            expectedSha256: new string('0', 64),
            manifestSha256: new string('1', 64),
            apiSha256: new string('2', 64),
            verified: false) with
        {
            SourceRepository = "https://example.invalid/untrusted-repository"
        };

        var result = ToolManifestValidator.Validate(
            configuration,
            new ToolExpectation(ToolKey.Ffprobe, "wrong", "https://example.invalid/trusted", "1.0.0", "win-x64", new string('3', 64)),
            new HashSet<string> { tool.Repository });

        Assert.False(result.IsValid);
        Assert.Contains(ToolValidationFailure.Unverified, result.Failures);
        Assert.Contains(ToolValidationFailure.WrongAsset, result.Failures);
        Assert.Contains(ToolValidationFailure.WrongRepository, result.Failures);
        Assert.Contains(ToolValidationFailure.WrongTargetRid, result.Failures);
        Assert.Contains(ToolValidationFailure.ConflictingDigest, result.Failures);
        Assert.Contains(ToolValidationFailure.HashMismatch, result.Failures);
    }

    [Fact]
    public async Task Executor_requires_a_trusted_expectation_and_matching_candidate()
    {
        using var tool = TemporaryTool.Create("exit 0");
        var candidate = tool.Configuration() with
        {
            AssetName = "arbitrary-asset",
            Version = "0.0.0",
            TargetRid = "linux-arm64",
            ExpectedSha256 = tool.Hash
        };
        var executor = new BoundedProcessExecutor(
            new Dictionary<string, ToolConfiguration> { ["Deno"] = candidate },
            allowedRepositories: new HashSet<string> { tool.Repository },
            executionTargetRid: "linux-arm64",
            trustedToolExpectations: new Dictionary<ToolKey, ToolExpectation> { [ToolKey.Deno] = tool.TrustedExpectation() });

        var result = await executor.ExecuteAsync(
            new ProcessSpec("Deno", Array.Empty<string>(), TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.MissingTool, result.Error!.Code);
    }

    [Fact]
    public async Task Missing_trusted_expectation_is_not_launchable()
    {
        using var tool = TemporaryTool.Create("exit 0");
        var executor = new BoundedProcessExecutor(
            new Dictionary<string, ToolConfiguration> { ["Deno"] = tool.Configuration() },
            allowedRepositories: new HashSet<string> { tool.Repository },
            executionTargetRid: "linux-arm64");

        var result = await executor.ExecuteAsync(
            new ProcessSpec("Deno", Array.Empty<string>(), TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.MissingTool, result.Error!.Code);
    }

    [Fact]
    public async Task Missing_or_replaced_tool_is_not_launchable()
    {
        using var tool = TemporaryTool.Create("exit 0");
        var executor = new BoundedProcessExecutor(
            new Dictionary<string, ToolConfiguration> { ["Deno"] = tool.Configuration() },
            allowedRepositories: new HashSet<string> { tool.Repository },
            executionTargetRid: "linux-arm64",
            trustedToolExpectations: new Dictionary<ToolKey, ToolExpectation> { [ToolKey.Deno] = tool.TrustedExpectation() });
        File.Delete(tool.Path);

        var result = await executor.ExecuteAsync(
            new ProcessSpec("Deno", Array.Empty<string>(), TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.MissingTool, result.Error!.Code);
        Assert.DoesNotContain(tool.Path, result.Error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Official_yt_dlp_fixtures_are_exactly_represented()
    {
        Assert.Equal("2026.08.19", OfficialToolFixtures.YtDlpVersion);
        Assert.Equal("yt-dlp.exe", OfficialToolFixtures.WindowsX64YtDlpAsset);
        Assert.Equal("66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a", OfficialToolFixtures.WindowsX64YtDlpSha256);
        Assert.Equal("yt-dlp_linux", OfficialToolFixtures.LinuxX64YtDlpAsset);
        Assert.Equal("58162f9bfdc27458ea47bfcb311cf47028f17d8154a8bf7d689861d46399230a", OfficialToolFixtures.LinuxX64YtDlpSha256);
    }

    [Fact]
    public void Yt_dlp_policy_is_fixed_and_rejects_remote_reenablement()
    {
        var args = YtDlpInvocationPolicy.Build(
            "/app/tools/deno",
            PolicyOperationArguments);

        Assert.Equal(ExpectedPolicyArguments, args);
        Assert.DoesNotContain(args, static argument => argument.Contains("ejs:npm", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(args, static argument => argument.Contains("ejs:github", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<ArgumentException>(() => YtDlpInvocationPolicy.Build("/app/tools/deno", ["--remote-components=custom"]));
        Assert.Throws<ArgumentException>(() => YtDlpInvocationPolicy.Build("/app/tools/deno", ["--REMOTE-COMPONENTS", "custom"]));
        Assert.Throws<ArgumentException>(() => YtDlpInvocationPolicy.Build("/app/tools/deno", ["--config-location=/secret/config"]));
        Assert.Throws<ArgumentException>(() => YtDlpInvocationPolicy.Build("/app/tools/deno", ["--CONFIG-LOCATIONS", "/secret/config"]));
        Assert.Throws<ArgumentException>(() => YtDlpInvocationPolicy.Build("/app/tools/deno", ["--plugin-dirs=/secret/plugins"]));
        Assert.Throws<ArgumentException>(() => YtDlpInvocationPolicy.Build("/app/tools/deno", ["--LOAD-PLUGINS", "ejs:npm"]));
        Assert.Throws<ArgumentException>(() => YtDlpInvocationPolicy.Build("/app/tools/deno", ["--js-runtimes", "deno:/override"]));
        Assert.Throws<ArgumentException>(() => YtDlpInvocationPolicy.Build("/app/tools/deno", ["--print", "ejs:github"]));
        Assert.Throws<ArgumentException>(() => YtDlpInvocationPolicy.Build("relative-deno"));
    }

    private sealed class TemporaryTool : IDisposable
    {
        private TemporaryTool(string path, string hash, string repository)
        {
            Path = path;
            Hash = hash;
            Repository = repository;
        }

        public string Path { get; }
        public string Hash { get; }
        public string Repository { get; }

        public static TemporaryTool Create(string body)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"unifi-fixture-{Guid.NewGuid():N}.sh");
            File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return new TemporaryTool(path, hash, "https://example.invalid/fixture-repository");
        }

        public ToolConfiguration Configuration(
            string? expectedSha256 = null,
            string? manifestSha256 = null,
            string? apiSha256 = null,
            bool verified = true) => new(
                ToolKey.Deno,
                "deno-fixture",
                Repository,
                "2.9.6",
                "linux-arm64",
                expectedSha256 ?? Hash,
                verified,
                Path,
                manifestSha256,
                apiSha256);

        public ToolExpectation TrustedExpectation() => new(
            ToolKey.Deno,
            "deno-fixture",
            Repository,
            "2.9.6",
            "linux-arm64",
            Hash);

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
