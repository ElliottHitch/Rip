using System.Text.RegularExpressions;
using Rip.Application;
using Rip.Domain;

namespace Rip.Infrastructure;

public sealed record ToolCapability(
    ToolKey Tool,
    bool IsAvailable,
    string? Version,
    bool MeetsVersionFloor,
    string SafeStatus);

public sealed class LocalCapabilityProbe
{
    private static readonly Version DenoMinimum = new(2, 3, 0);
    private readonly BoundedProcessExecutor executor;

    public LocalCapabilityProbe(BoundedProcessExecutor executor)
    {
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async ValueTask<ProviderResult<ToolCapability>> ProbeAsync(
        ToolKey tool,
        CancellationToken cancellationToken = default)
    {
        var specification = new ProcessSpec(tool.ToString(), VersionArguments(tool), TimeSpan.FromSeconds(15));
        var process = await executor.ExecuteCapturedAsync(specification, cancellationToken).ConfigureAwait(false);
        if (process.Error is not null)
        {
            return new ProviderResult<ToolCapability>(null, process.Error);
        }

        var result = process.Value!;
        if (result.ExitCode != 0)
        {
            return new ProviderResult<ToolCapability>(null, SafeInfrastructureErrors.ProviderUnavailable());
        }

        var version = ParseVersion(tool, result.StandardOutput, result.StandardError);
        if (version is null)
        {
            return new ProviderResult<ToolCapability>(null, SafeInfrastructureErrors.ProviderUnavailable());
        }

        var meetsFloor = tool != ToolKey.Deno || Version.TryParse(version, out var parsed) && parsed >= DenoMinimum;
        if (!meetsFloor)
        {
            return new ProviderResult<ToolCapability>(
                new ToolCapability(tool, true, version, false, "version-below-required-floor"),
                SafeInfrastructureErrors.MissingTool(tool));
        }

        return new ProviderResult<ToolCapability>(
            new ToolCapability(tool, true, version, true, "capability-available"),
            null);
    }

    private static string[] VersionArguments(ToolKey tool) => tool switch
    {
        ToolKey.YtDlp or ToolKey.Deno => new[] { "--version" },
        ToolKey.Ffmpeg or ToolKey.Ffprobe => new[] { "-version" },
        _ => throw new ArgumentOutOfRangeException(nameof(tool))
    };

    private static string? ParseVersion(ToolKey tool, string standardOutput, string standardError)
    {
        var text = string.Concat(standardOutput, "\n", standardError);
        var pattern = tool switch
        {
            ToolKey.YtDlp => @"(?<![0-9])20[0-9]{2}\.[0-9]{2}\.[0-9]{2}(?![0-9])",
            ToolKey.Deno => @"\b[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?\b",
            ToolKey.Ffmpeg or ToolKey.Ffprobe => @"version\s+([0-9][A-Za-z0-9.+_-]*)",
            _ => throw new ArgumentOutOfRangeException(nameof(tool))
        };
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var candidate = tool is ToolKey.Ffmpeg or ToolKey.Ffprobe ? match.Groups[1].Value : match.Value;
        return candidate.Trim();
    }
}

public static class YtDlpInvocationPolicy
{
    public static IReadOnlyList<string> Build(
        string knownLocalDenoPath,
        IEnumerable<string>? operationArguments = null)
    {
        if (!Path.IsPathFullyQualified(knownLocalDenoPath))
        {
            throw new ArgumentException("The JavaScript runtime path must be absolute.", nameof(knownLocalDenoPath));
        }

        var operation = operationArguments?.ToArray() ?? Array.Empty<string>();
        if (ContainsForbiddenArgument(operation))
        {
            throw new ArgumentException("The invocation contains a forbidden remote-component or configuration option.", nameof(operationArguments));
        }

        var arguments = new List<string>(7 + operation.Length)
        {
            "--ignore-config",
            "--no-js-runtimes",
            "--js-runtimes",
            $"deno:{knownLocalDenoPath}",
            "--no-remote-components"
        };
        arguments.AddRange(operation);
        return arguments;
    }

    private static bool ContainsForbiddenArgument(string[] arguments)
    {
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument is null || ContainsForbiddenComponent(argument)) return true;

            var separator = argument.IndexOf('=');
            var optionName = separator < 0 ? argument : argument[..separator];
            if (IsForbiddenOptionName(optionName.Trim().ToLowerInvariant())) return true;
        }

        return false;
    }

    private static bool ContainsForbiddenComponent(string argument) =>
        argument.Contains("ejs:npm", StringComparison.OrdinalIgnoreCase) ||
        argument.Contains("ejs:github", StringComparison.OrdinalIgnoreCase);

    private static bool IsForbiddenOptionName(string optionName) =>
        ForbiddenOptionNames.Contains(optionName) ||
        optionName.StartsWith("--config", StringComparison.Ordinal) ||
        optionName.Contains("plugin", StringComparison.Ordinal) ||
        optionName.Contains("remote-components", StringComparison.Ordinal);

    private static readonly HashSet<string> ForbiddenOptionNames = new(StringComparer.Ordinal)
    {
        "--remote-components",
        "--config",
        "--config-file",
        "--config-location",
        "--config-locations",
        "--enable-plugins",
        "--disable-plugins",
        "--load-plugins",
        "--plugin",
        "--plugin-dir",
        "--plugin-dirs",
        "--plugin-directory",
        "--plugin-directories",
        "--ignore-config",
        "--no-js-runtimes",
        "--js-runtimes",
        "--no-remote-components"
    };
}
