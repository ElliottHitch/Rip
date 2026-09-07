using System.Security.Cryptography;
using Rip.Application;
using Rip.Domain;
using Rip.Infrastructure;

namespace Rip.Infrastructure.Tests;

public sealed class ProcessExecutionTests
{
    [Fact]
    public async Task Argument_list_preserves_adversarial_values_without_shell_evaluation()
    {
        using var tool = FixtureTool.Create(
            "if [ \"$#\" -eq 7 ] && [ \"$1\" = 'space value' ] && [ \"$2\" = 'quote\"value' ] && [ \"$3\" = 'amp&percent%' ] && [ \"$4\" = '--leading-dash' ] && [ -n \"$5\" ] && [ \"$6\" = '$(touch /tmp/unifi-should-not-exist)' ] && [ \"$7\" = '; exit 99' ]; then exit 0; fi; exit 23");
        var executor = CreateExecutor(tool);
        var arguments = new[] { "space value", "quote\"value", "amp&percent%", "--leading-dash", "line one\nline two", "$(touch /tmp/unifi-should-not-exist)", "; exit 99" };

        var result = await executor.ExecuteAsync(new ProcessSpec("fixture", arguments, TimeSpan.FromSeconds(2)), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value!.ExitCode);
        Assert.False(File.Exists("/tmp/unifi-should-not-exist"));
    }

    [Fact]
    public async Task Generic_nonzero_exit_is_typed_and_does_not_cross_raw_child_output()
    {
        using var tool = FixtureTool.Create(
            "printf '%s' 'https://example.invalid/signed?signature=URL_SENTINEL cookie=COOKIE_SENTINEL token=TOKEN_SENTINEL password=PASSWORD_SENTINEL Authorization: Bearer AUTH_SENTINEL Proxy-Authorization: Bearer PROXY_SENTINEL path=/secret/profile' >&2; exit 17");
        var result = await CreateExecutor(tool).ExecuteAsync(
            new ProcessSpec("fixture", Array.Empty<string>(), TimeSpan.FromSeconds(2)), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(DownloadErrorCode.ProviderUnavailable, result.Error!.Code);
        Assert.Equal(DownloadStage.Processing, result.Error.Stage);
        Assert.Equal(RetryAction.UserActionRequired, result.Error.Retry);
        Assert.Equal("diag-process-failed", result.Error.Diagnostic.Value);
        Assert.DoesNotContain("URL_SENTINEL", result.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("COOKIE_SENTINEL", result.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN_SENTINEL", result.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("PASSWORD_SENTINEL", result.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTH_SENTINEL", result.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("PROXY_SENTINEL", result.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("/secret/profile", result.Error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allowlisted_structured_nonzero_exit_is_mapped_without_child_output()
    {
        using var tool = FixtureTool.Create(
            "printf '%s' '{\"error_code\":\"access-denied\",\"detail\":\"STRUCTURED_OUTPUT_SENTINEL https://example.invalid/?token=STRUCTURED_TOKEN\"}' >&2; exit 17");
        var result = await CreateExecutor(tool).ExecuteAsync(
            new ProcessSpec("fixture", Array.Empty<string>(), TimeSpan.FromSeconds(2)), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.AccessDenied, result.Error!.Code);
        Assert.Equal(DownloadStage.Downloading, result.Error.Stage);
        Assert.Equal(RetryAction.RefreshStream, result.Error.Retry);
        Assert.Equal("diag-process-access-denied", result.Error.Diagnostic.Value);
        Assert.DoesNotContain("STRUCTURED_OUTPUT_SENTINEL", result.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("STRUCTURED_TOKEN", result.Error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Human_readable_ansi_403_is_classified_as_access_denied_without_control_output()
    {
        const string signedDiagnosticSentinel = "https://googlevideo.example.invalid/videoplayback?signature=ANSI_SIGNATURE_SENTINEL";
        const string humanError = "\u001b[31mERROR: HTTP Error 403: Forbidden\u001b[0m " + signedDiagnosticSentinel;
        using var tool = FixtureTool.Create($"printf '%s' {ShellQuote(humanError)} >&2; exit 17");

        var result = await CreateExecutor(tool).ExecuteAsync(
            new ProcessSpec("fixture", Array.Empty<string>(), TimeSpan.FromSeconds(2)), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.AccessDenied, result.Error!.Code);
        Assert.Equal(RetryAction.RefreshStream, result.Error.Retry);
        Assert.DoesNotContain("ANSI_SIGNATURE_SENTINEL", result.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", result.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("googlevideo", result.Error.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_structured_nonzero_exit_remains_generic_and_safe()
    {
        using var tool = FixtureTool.Create("printf '%s' '{\"error_code\":\"not-allowlisted\",\"detail\":\"UNKNOWN_STRUCTURED_SENTINEL\"}' >&2; exit 17");
        var result = await CreateExecutor(tool).ExecuteAsync(
            new ProcessSpec("fixture", Array.Empty<string>(), TimeSpan.FromSeconds(2)), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.ProviderUnavailable, result.Error!.Code);
        Assert.Equal("diag-process-failed", result.Error.Diagnostic.Value);
        Assert.DoesNotContain("UNKNOWN_STRUCTURED_SENTINEL", result.Error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Large_stdout_and_stderr_are_drained_but_only_bounded_status_is_retained()
    {
        using var tool = FixtureTool.Create("i=0; while [ $i -lt 100000 ]; do printf x; printf y >&2; i=$((i+1)); done");
        var executor = CreateExecutor(tool, new ProcessExecutorOptions(128, TimeSpan.FromSeconds(5)));

        var result = await executor.ExecuteAsync(
            new ProcessSpec("fixture", Array.Empty<string>(), TimeSpan.FromSeconds(2)), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("local-process-succeeded-output-truncated", result.Value!.SafeDiagnosticMessage);
    }

    [Fact]
    public async Task Cancellation_returns_cancelled_and_terminates_direct_process()
    {
        using var tool = FixtureTool.Create("sleep 30");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await CreateExecutor(tool).ExecuteAsync(
            new ProcessSpec("fixture", Array.Empty<string>(), TimeSpan.FromSeconds(10)), cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.Cancelled, result.Error!.Code);
        Assert.DoesNotContain(tool.Path, result.Error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_returns_unknown_with_bounded_retry_classification()
    {
        using var tool = FixtureTool.Create("sleep 30");

        var result = await CreateExecutor(tool).ExecuteAsync(
            new ProcessSpec("fixture", Array.Empty<string>(), TimeSpan.FromMilliseconds(100)), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.Unknown, result.Error!.Code);
        Assert.Equal(RetryAction.RetryAfterDelay, result.Error.Retry);
    }

    [Fact]
    public async Task Missing_tool_is_a_typed_start_failure()
    {
        using var tool = FixtureTool.Create("exit 0");
        var executor = CreateExecutor(tool);
        File.Delete(tool.Path);

        var result = await executor.ExecuteAsync(
            new ProcessSpec("fixture", Array.Empty<string>(), TimeSpan.FromSeconds(1)), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.MissingTool, result.Error!.Code);
        Assert.DoesNotContain(tool.Path, result.Error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capability_probe_reports_version_floor_without_returning_child_output()
    {
        using var supported = FixtureTool.Create("printf '%s' 'deno 2.9.6'");
        using var unsupported = FixtureTool.Create("printf '%s' 'deno 2.2.0'");

        var good = await new LocalCapabilityProbe(CreateExecutor(supported)).ProbeAsync(ToolKey.Deno, TestContext.Current.CancellationToken);
        var low = await new LocalCapabilityProbe(CreateExecutor(unsupported)).ProbeAsync(ToolKey.Deno, TestContext.Current.CancellationToken);

        Assert.True(good.IsSuccess);
        Assert.Equal("2.9.6", good.Value!.Version);
        Assert.True(good.Value.MeetsVersionFloor);
        Assert.False(low.IsSuccess);
        Assert.Equal("2.2.0", low.Value!.Version);
        Assert.False(low.Value.MeetsVersionFloor);
        Assert.DoesNotContain("deno 2.9.6", good.Value.SafeStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Composition_wires_explicit_ports_and_bounded_diagnostics()
    {
        using var tool = FixtureTool.Create("exit 0");
        var services = InfrastructureComposition.Create(new InfrastructureConfiguration(
            new Dictionary<string, ToolConfiguration> { ["Deno"] = tool.Configuration() },
            new ProcessExecutorOptions(128, TimeSpan.FromSeconds(2)),
            new HashSet<string> { tool.Repository },
            new Dictionary<ToolKey, ToolExpectation> { [ToolKey.Deno] = tool.TrustedExpectation() },
            "linux-arm64"));

        Assert.IsType<BoundedProcessExecutor>(services.ProcessExecutor);
        Assert.IsType<BoundedDiagnostics>(services.Diagnostics);
        Assert.IsType<SystemClock>(services.Clock);
        Assert.IsType<LocalCapabilityProbe>(services.Capabilities);
        var diagnostics = (BoundedDiagnostics)services.Diagnostics;
        var error = SafeDownloadError.Create(
            DownloadErrorCode.Unknown,
            DownloadStage.Processing,
            "failed /secret/profile destination=/home/sentinel/download temp=C:\\Users\\sentinel\\temp UNC=\\\\server\\share\\sentinel",
            RetryAction.None,
            new RedactedDiagnosticToken("diag-bounded"));
        diagnostics.Report(error);
        Assert.Single(diagnostics.Errors);
        Assert.DoesNotContain("/secret/profile", diagnostics.Errors[0].UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/sentinel", diagnostics.Errors[0].UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\sentinel", diagnostics.Errors[0].UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\server\\share\\sentinel", diagnostics.Errors[0].UserMessage, StringComparison.Ordinal);
    }

    private static BoundedProcessExecutor CreateExecutor(FixtureTool tool, ProcessExecutorOptions? options = null) =>
        new(
            new Dictionary<string, ToolConfiguration>
            {
                ["fixture"] = tool.Configuration(),
                ["Deno"] = tool.Configuration()
            },
            options,
            new HashSet<string> { tool.Repository },
            "linux-arm64",
            new Dictionary<ToolKey, ToolExpectation> { [ToolKey.Deno] = tool.TrustedExpectation() });

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private sealed class FixtureTool : IDisposable
    {
        private FixtureTool(string path, string hash)
        {
            Path = path;
            Hash = hash;
        }

        public string Path { get; }
        public string Hash { get; }
        public string Repository { get; } = "https://example.invalid/fixture-repository";

        public static FixtureTool Create(string body)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"unifi-process-{Guid.NewGuid():N}.sh");
            File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            using var stream = File.OpenRead(path);
            return new FixtureTool(path, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }

        public ToolConfiguration Configuration() => new(
            ToolKey.Deno,
            "fixture",
            Repository,
            "2.9.6",
            "linux-arm64",
            Hash,
            true,
            Path);

        public ToolExpectation TrustedExpectation() => new(
            ToolKey.Deno,
            "fixture",
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
