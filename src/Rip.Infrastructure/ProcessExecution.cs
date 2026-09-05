using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rip.Application;
using Rip.Domain;

namespace Rip.Infrastructure;

public sealed record ProcessExecutorOptions(
    int MaximumCapturedCharacters = 16 * 1024 * 1024,
    TimeSpan MaximumTimeout = default)
{
    public TimeSpan EffectiveMaximumTimeout => MaximumTimeout == default ? TimeSpan.FromHours(12) : MaximumTimeout;
}

internal sealed record CapturedProcessResult(
    int ExitCode,
    bool TimedOut,
    bool DirectProcessExited,
    bool DescendantCleanupCertain,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated);

public sealed class BoundedProcessExecutor : IProcessExecutor
{
    private readonly IReadOnlyDictionary<string, ToolConfiguration> tools;
    private readonly ProcessExecutorOptions options;
    private readonly IReadOnlySet<string> allowedRepositories;
    private readonly string executionTargetRid;
    private readonly IReadOnlyDictionary<ToolKey, ToolExpectation> trustedToolExpectations;

    public BoundedProcessExecutor(
        IReadOnlyDictionary<string, ToolConfiguration> tools,
        ProcessExecutorOptions? options = null,
        IReadOnlySet<string>? allowedRepositories = null,
        string? executionTargetRid = null,
        IReadOnlyDictionary<ToolKey, ToolExpectation>? trustedToolExpectations = null)
    {
        this.tools = tools ?? throw new ArgumentNullException(nameof(tools));
        this.options = options ?? new ProcessExecutorOptions();
        if (this.options.MaximumCapturedCharacters < 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (this.options.EffectiveMaximumTimeout <= TimeSpan.Zero && this.options.EffectiveMaximumTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        this.allowedRepositories = allowedRepositories ?? new HashSet<string>(StringComparer.Ordinal)
        {
            OfficialToolFixtures.YtDlpRepository
        };
        this.executionTargetRid = executionTargetRid ?? RuntimeInformation.RuntimeIdentifier;
        this.trustedToolExpectations = trustedToolExpectations ?? new Dictionary<ToolKey, ToolExpectation>();
    }

    public async ValueTask<ProviderResult<ProcessResult>> ExecuteAsync(
        ProcessSpec specification,
        CancellationToken cancellationToken)
    {
        var captured = await ExecuteCapturedAsync(specification, cancellationToken).ConfigureAwait(false);
        if (captured.Error is not null) return new ProviderResult<ProcessResult>(null, captured.Error);

        var result = captured.Value!;
        if (result.ExitCode != 0)
        {
            return new ProviderResult<ProcessResult>(null, ClassifyNonzeroExit(result));
        }

        var diagnostic = result.TimedOut
            ? "local-process-timeout-cleanup-uncertain"
            : result.OutputTruncated ? "local-process-succeeded-output-truncated" : "local-process-succeeded";
        return new ProviderResult<ProcessResult>(
            new ProcessResult(result.ExitCode, result.TimedOut, diagnostic),
            null);
    }

    internal async ValueTask<ProviderResult<CapturedProcessResult>> ExecuteCapturedAsync(
        ProcessSpec specification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (cancellationToken.IsCancellationRequested)
        {
            return new ProviderResult<CapturedProcessResult>(null, SafeInfrastructureErrors.Cancelled(cleanupCertain: false));
        }
        if (specification.Timeout <= TimeSpan.Zero && specification.Timeout != Timeout.InfiniteTimeSpan)
        {
            return new ProviderResult<CapturedProcessResult>(null, SafeInfrastructureErrors.ProviderUnavailable());
        }

        if (string.IsNullOrWhiteSpace(specification.ExecutableKey) || specification.Arguments is null ||
            !tools.TryGetValue(specification.ExecutableKey, out var tool))
        {
            return new ProviderResult<CapturedProcessResult>(null, SafeInfrastructureErrors.MissingTool(ToolKey.YtDlp));
        }

        if (!trustedToolExpectations.TryGetValue(tool.Key, out var expectation))
        {
            return new ProviderResult<CapturedProcessResult>(null, SafeInfrastructureErrors.MissingTool(tool.Key));
        }

        var validation = ToolManifestValidator.Validate(tool, expectation, allowedRepositories);
        if (!validation.IsValid || !string.Equals(tool.TargetRid, executionTargetRid, StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderResult<CapturedProcessResult>(null, SafeInfrastructureErrors.MissingTool(tool.Key));
        }

        if (specification.Arguments.Any(static argument => argument is null))
        {
            return new ProviderResult<CapturedProcessResult>(null, SafeInfrastructureErrors.ProviderUnavailable());
        }

        var timeout = specification.Timeout == Timeout.InfiniteTimeSpan
            ? options.EffectiveMaximumTimeout
            : Min(specification.Timeout, options.EffectiveMaximumTimeout);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(tool.ExecutablePath, specification.Arguments)
        };

        try
        {
            if (!process.Start())
            {
                return new ProviderResult<CapturedProcessResult>(null, SafeInfrastructureErrors.MissingTool(tool.Key));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return new ProviderResult<CapturedProcessResult>(null, SafeInfrastructureErrors.MissingTool(tool.Key));
        }

        using var drainCancellation = new CancellationTokenSource();
        var standardOutput = CaptureAsync(process.StandardOutput, options.MaximumCapturedCharacters, drainCancellation.Token);
        var standardError = CaptureAsync(process.StandardError, options.MaximumCapturedCharacters, drainCancellation.Token);
        var directProcessExited = false;
        var timedOut = false;
        var descendantCleanupCertain = true;

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan) operationCancellation.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(operationCancellation.Token).ConfigureAwait(false);
            directProcessExited = true;
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            await TerminateAsync(process).ConfigureAwait(false);
            descendantCleanupCertain = false;
            if (!directProcessExited)
            {
                directProcessExited = await WaitForDirectProcessAsync(process).ConfigureAwait(false);
            }
        }

        var drained = await DrainWithinBoundAsync(standardOutput, standardError, drainCancellation).ConfigureAwait(false);
        if (!drained)
        {
            descendantCleanupCertain = false;
            drainCancellation.Cancel();
            await IgnoreCaptureFailuresAsync(standardOutput, standardError).ConfigureAwait(false);
        }

        var output = await IgnoreCaptureFailuresAsync(standardOutput, standardError).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested && !timedOut)
        {
            return new ProviderResult<CapturedProcessResult>(null, SafeInfrastructureErrors.Cancelled(descendantCleanupCertain));
        }
        if (timedOut)
        {
            return new ProviderResult<CapturedProcessResult>(null, SafeInfrastructureErrors.TimedOut(descendantCleanupCertain));
        }

        return new ProviderResult<CapturedProcessResult>(
            new CapturedProcessResult(
                process.ExitCode,
                false,
                directProcessExited,
                descendantCleanupCertain,
                output.StandardOutput,
                output.StandardError,
                output.OutputTruncated),
            null);
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static async Task<CapturedText> CaptureAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[4096];
        var truncated = false;
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                var remaining = maximumCharacters - builder.Length;
                if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, count));
                if (count > Math.Max(remaining, 0)) truncated = true;
            }
        }
        catch (OperationCanceledException)
        {
            truncated = true;
        }
        catch (IOException)
        {
            truncated = true;
        }
        return new CapturedText(builder.ToString(), truncated);
    }

    private static async Task<bool> DrainWithinBoundAsync(
        Task<CapturedText> standardOutput,
        Task<CapturedText> standardError,
        CancellationTokenSource drainCancellation)
    {
        var all = Task.WhenAll(standardOutput, standardError);
        var completed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        if (completed == all)
        {
            await all.ConfigureAwait(false);
            return true;
        }

        drainCancellation.Cancel();
        return false;
    }

    private static async Task<CapturedOutputs> IgnoreCaptureFailuresAsync(Task<CapturedText> output, Task<CapturedText> error)
    {
        try
        {
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Child output is deliberately not carried across this boundary.
        }

        var outputResult = output.IsCompletedSuccessfully ? output.Result : new CapturedText(string.Empty, true);
        var errorResult = error.IsCompletedSuccessfully ? error.Result : new CapturedText(string.Empty, true);
        return new CapturedOutputs(
            outputResult.Text,
            outputResult.Truncated || errorResult.Truncated,
            errorResult.Text);
    }

    private static Task<bool> TerminateAsync(Process process)
    {
        if (process.HasExited) return Task.FromResult(true);
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(process.HasExited);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(false);
        }
        catch (NotSupportedException)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    private static async Task<bool> WaitForDirectProcessAsync(Process process)
    {
        try
        {
            using var waitCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(waitCancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return process.HasExited;
        }
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    internal static SafeDownloadError ClassifyNonzeroExit(CapturedProcessResult result)
    {
        if (TryReadStructuredFailure(result.StandardError, out var structured) ||
            TryReadStructuredFailure(result.StandardOutput, out structured))
        {
            return SafeInfrastructureErrors.StructuredProcessFailure(structured);
        }

        // yt-dlp's normal human-readable stderr is untrusted but status codes are stable.
        // Classify them here so the application can apply its single stream-refresh policy.
        if (ContainsHttpStatus(result.StandardError, 403) || ContainsHttpStatus(result.StandardOutput, 403))
            return SafeInfrastructureErrors.StructuredProcessFailure("access-denied");
        if (ContainsHttpStatus(result.StandardError, 429) || ContainsHttpStatus(result.StandardOutput, 429))
            return SafeInfrastructureErrors.StructuredProcessFailure("rate-limited");

        return SafeInfrastructureErrors.ProcessExitedNonzero();
    }

    private static bool ContainsHttpStatus(string output, int statusCode) =>
        Regex.IsMatch(output, $"\\bHTTP(?:\\s+Error)?\\s+{statusCode}\\b|\\bError\\s+{statusCode}\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool TryReadStructuredFailure(string output, out string code)
    {
        code = string.Empty;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("error_code", out var value) ||
                    value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var candidate = value.GetString();
                if (candidate is not null && candidate is "access-denied" or "rate-limited" or "provider-unavailable" or "media-processing-failed")
                {
                    code = candidate;
                    return true;
                }
            }
            catch (JsonException)
            {
                // Untrusted child output is never carried beyond this boundary.
            }
        }

        return false;
    }

    private readonly record struct CapturedText(string Text, bool Truncated, string Error = "");

    private readonly record struct CapturedOutputs(string StandardOutput, bool OutputTruncated, string StandardError);
}
