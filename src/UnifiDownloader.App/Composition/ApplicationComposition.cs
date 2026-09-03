using System.Runtime.InteropServices;
using System.Text.Json;
using UnifiDownloader.App.Presentation;
using UnifiDownloader.Application;
using UnifiDownloader.Domain;
using UnifiDownloader.Infrastructure;

namespace UnifiDownloader.App.Composition;

public sealed class InlineUiDispatcher : IUiDispatcher
{
    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}

public sealed class InfrastructureEnvironmentProbe : IEnvironmentProbe
{
    private readonly LocalCapabilityProbe probe;

    public InfrastructureEnvironmentProbe(LocalCapabilityProbe probe) => this.probe = probe ?? throw new ArgumentNullException(nameof(probe));

    public async ValueTask<IReadOnlyList<CapabilityStatus>> ProbeAsync(CancellationToken cancellationToken)
    {
        var statuses = new List<CapabilityStatus>(5);
        foreach (var tool in Enum.GetValues<ToolKey>())
        {
            var result = await probe.ProbeAsync(tool, cancellationToken).ConfigureAwait(false);
            statuses.Add(result.Value is { IsAvailable: true, MeetsVersionFloor: true }
                ? new CapabilityStatus(ToolLabel(tool), "Available", "The approved local capability is available.")
                : new CapabilityStatus(ToolLabel(tool), "Missing", "The approved local capability is not configured or does not meet its required floor."));
        }

        statuses.Add(new CapabilityStatus("Runtime", "Available", $".NET {Environment.Version.Major} runtime ({RuntimeInformation.ProcessArchitecture})."));
        return statuses;
    }

    private static string ToolLabel(ToolKey tool) => tool switch
    {
        ToolKey.YtDlp => "yt-dlp",
        ToolKey.Deno => "Deno",
        ToolKey.Ffmpeg => "FFmpeg",
        ToolKey.Ffprobe => "FFprobe",
        _ => "Tool"
    };
}

/// <summary>Loads only an explicit, local, operator-approved tool manifest.</summary>
public static class ToolManifestLoader
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "unifi-downloader.tools.json";
    public const string ManifestPathEnvironmentVariable = "UNIFI_DOWNLOADER_TOOL_MANIFEST";

    public static string DefaultManifestPath => Path.Combine(AppContext.BaseDirectory, ManifestFileName);

    public static InfrastructureConfiguration Load(string? manifestPath = null)
    {
        var path = ResolveManifestPath(manifestPath);
        if (path is null) return EmptyConfiguration();

        try
        {
            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<ManifestDocument>(stream, JsonOptions);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion ||
                !TryGetExecutionTarget(document.ExecutionTargetRid, out var executionTargetRid) ||
                !TryGetAllowedRepositories(document.AllowedRepositories, out var allowedRepositories) ||
                document.Tools is null || document.TrustedExpectations is null)
            {
                return EmptyConfiguration();
            }

            var baseDirectory = Path.GetDirectoryName(path);
            if (baseDirectory is null || !TryBuildConfiguration(
                    document,
                    baseDirectory,
                    executionTargetRid,
                    allowedRepositories,
                    out var configuration))
            {
                return EmptyConfiguration();
            }

            return configuration;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return EmptyConfiguration();
        }
    }

    private static bool TryBuildConfiguration(
        ManifestDocument document,
        string baseDirectory,
        string executionTargetRid,
        IReadOnlySet<string> allowedRepositories,
        out InfrastructureConfiguration configuration)
    {
        configuration = default!;
        var tools = new Dictionary<string, ToolConfiguration>(StringComparer.Ordinal);
        var expectations = new Dictionary<ToolKey, ToolExpectation>();
        var seenKeys = new HashSet<ToolKey>();

        foreach (var (name, manifestTool) in document.Tools!)
        {
            if (!TryParseToolKey(name, out var key) || manifestTool is null || !seenKeys.Add(key) ||
                !TryResolveExecutable(manifestTool.ExecutablePath, baseDirectory, out var executablePath) ||
                !TryBuildToolConfiguration(key, manifestTool, executablePath, out var tool))
            {
                return false;
            }

            if (document.TrustedExpectations!.TryGetValue(name, out var expectation) is false ||
                expectation is null || !TryBuildExpectation(key, expectation, out var trustedExpectation))
            {
                return false;
            }

            tools.Add(key.ToString(), tool);
            expectations.Add(key, trustedExpectation);
        }

        // A manifest that mentions an expectation without a matching candidate is not an
        // approved configuration. Rejecting it also prevents stale artifacts being mistaken for
        // a complete tool set.
        if (document.TrustedExpectations!.Keys.Any(name => !TryParseToolKey(name, out var key) || !seenKeys.Contains(key)))
        {
            return false;
        }

        configuration = new InfrastructureConfiguration(
            tools,
            AllowedToolRepositories: allowedRepositories,
            TrustedToolExpectations: expectations,
            ExecutionTargetRid: executionTargetRid);
        return true;
    }

    private static bool TryBuildToolConfiguration(
        ToolKey key,
        ManifestTool tool,
        string executablePath,
        out ToolConfiguration configuration)
    {
        configuration = default!;
        if (!string.Equals(tool.Key, key.ToString(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(tool.AssetName) ||
            string.IsNullOrWhiteSpace(tool.SourceRepository) ||
            string.IsNullOrWhiteSpace(tool.Version) ||
            string.IsNullOrWhiteSpace(tool.TargetRid) ||
            string.IsNullOrWhiteSpace(tool.ExpectedSha256) ||
            !tool.IsVerified)
        {
            return false;
        }

        configuration = new ToolConfiguration(
            key,
            tool.AssetName,
            tool.SourceRepository,
            tool.Version,
            tool.TargetRid,
            tool.ExpectedSha256,
            tool.IsVerified,
            executablePath,
            tool.ManifestSha256,
            tool.ApiSha256);
        return true;
    }

    private static bool TryBuildExpectation(
        ToolKey key,
        ManifestExpectation expectation,
        out ToolExpectation trustedExpectation)
    {
        trustedExpectation = default!;
        if (!string.Equals(expectation.Key, key.ToString(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(expectation.AssetName) ||
            string.IsNullOrWhiteSpace(expectation.SourceRepository) ||
            string.IsNullOrWhiteSpace(expectation.Version) ||
            string.IsNullOrWhiteSpace(expectation.TargetRid) ||
            string.IsNullOrWhiteSpace(expectation.ExpectedSha256) ||
            !expectation.RequireVerified)
        {
            return false;
        }

        trustedExpectation = new ToolExpectation(
            key,
            expectation.AssetName,
            expectation.SourceRepository,
            expectation.Version,
            expectation.TargetRid,
            expectation.ExpectedSha256,
            RequireVerified: true);
        return true;
    }

    private static bool TryGetExecutionTarget(string? declaredRid, out string rid)
    {
        rid = RuntimeInformation.RuntimeIdentifier;
        return string.IsNullOrWhiteSpace(declaredRid) || string.Equals(declaredRid, rid, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetAllowedRepositories(
        List<string>? repositories,
        out IReadOnlySet<string> allowedRepositories)
    {
        allowedRepositories = new HashSet<string>(StringComparer.Ordinal);
        if (repositories is null || repositories.Count == 0) return false;
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repository in repositories)
        {
            if (string.IsNullOrWhiteSpace(repository) || !Uri.TryCreate(repository, UriKind.Absolute, out var address) ||
                address.Scheme is not "https" || !values.Add(repository))
            {
                return false;
            }
        }

        allowedRepositories = values;
        return true;
    }

    private static bool TryParseToolKey(string? name, out ToolKey key) =>
        Enum.TryParse(name, ignoreCase: false, out key) && Enum.IsDefined(key);

    private static bool TryResolveExecutable(string? candidate, string baseDirectory, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            path = Path.GetFullPath(Path.IsPathFullyQualified(candidate)
                ? candidate
                : Path.Combine(baseDirectory, candidate));
            return Path.IsPathFullyQualified(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? ResolveManifestPath(string? manifestPath)
    {
        var candidate = manifestPath;
        if (string.IsNullOrWhiteSpace(candidate)) candidate = Environment.GetEnvironmentVariable(ManifestPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(candidate)) candidate = DefaultManifestPath;
        try
        {
            return Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static InfrastructureConfiguration EmptyConfiguration() => new(
        new Dictionary<string, ToolConfiguration>(StringComparer.Ordinal),
        AllowedToolRepositories: new HashSet<string>(StringComparer.Ordinal),
        TrustedToolExpectations: new Dictionary<ToolKey, ToolExpectation>(),
        ExecutionTargetRid: RuntimeInformation.RuntimeIdentifier);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private sealed class ManifestDocument
    {
        public int SchemaVersion { get; set; }
        public string? ExecutionTargetRid { get; set; }
        public List<string>? AllowedRepositories { get; set; }
        public Dictionary<string, ManifestTool?>? Tools { get; set; }
        public Dictionary<string, ManifestExpectation?>? TrustedExpectations { get; set; }
    }

    private sealed class ManifestTool
    {
        public string? Key { get; set; }
        public string? AssetName { get; set; }
        public string? SourceRepository { get; set; }
        public string? Version { get; set; }
        public string? TargetRid { get; set; }
        public string? ExpectedSha256 { get; set; }
        public bool IsVerified { get; set; }
        public string? ExecutablePath { get; set; }
        public string? ManifestSha256 { get; set; }
        public string? ApiSha256 { get; set; }
    }

    private sealed class ManifestExpectation
    {
        public string? Key { get; set; }
        public string? AssetName { get; set; }
        public string? SourceRepository { get; set; }
        public string? Version { get; set; }
        public string? TargetRid { get; set; }
        public string? ExpectedSha256 { get; set; }
        public bool RequireVerified { get; set; }
    }
}

/// <summary>
/// Manual production composition root. Missing or invalid local configuration remains a safe,
/// non-runnable composition; no tool is downloaded or discovered remotely.
/// </summary>
public static class ApplicationComposition
{
    public static ComposedApplication Create(
        IUiDispatcher? dispatcher = null,
        string? toolManifestPath = null,
        HttpClient? httpClient = null,
        Func<string>? stagingRootFactory = null)
    {
        var stagingRoot = CompositionStagingRoot.Create(stagingRootFactory);
        try
        {
            var root = stagingRoot.RootPath;
            var configuration = ToolManifestLoader.Load(toolManifestPath);
            var infrastructure = InfrastructureComposition.Create(configuration);
            var publication = InfrastructureComposition.CreateLocalPublication(
                (BoundedProcessExecutor)infrastructure.ProcessExecutor,
                root,
                infrastructure.Diagnostics);
            var client = httpClient ?? new HttpClient();
            var stager = new LocalStreamStager(client, root);
            var denoPath = configuration.Tools.TryGetValue(ToolKey.Deno.ToString(), out var deno)
                ? deno.ExecutablePath
                : Path.Combine(AppContext.BaseDirectory, "deno-unconfigured");
            var provider = new YtDlpVideoProvider((BoundedProcessExecutor)infrastructure.ProcessExecutor, denoPath);
            var processor = new FfmpegStagedMediaProcessor(
                publication.Ffmpeg,
                stager,
                new FfmpegStageTarget(root));
            var observer = new PresentationObserver();
            var service = new DownloadApplicationService(
                provider,
                stager,
                processor,
                publication.Publisher,
                observer,
                infrastructure.Diagnostics);
            var opener = new SystemLocalFileOpener(publication.PublishedOutputs, new SystemLocalFileLauncher());
            var viewModel = new DownloadViewModel();
            foreach (var tool in Enum.GetValues<ToolKey>())
            {
                viewModel.Capabilities.Add(new CapabilityStatus(
                    InitialToolLabel(tool),
                    "Unavailable",
                    "Run Test Environment to verify the approved local capability before starting."));
            }
            var controller = new PresentationController(
                new ApplicationServiceRunner(service),
                opener,
                new InfrastructureEnvironmentProbe(infrastructure.Capabilities),
                viewModel,
                dispatcher ?? new InlineUiDispatcher());
            observer.Attach(controller);
            return new ComposedApplication(controller, viewModel, stagingRoot);
        }
        catch
        {
            stagingRoot.Dispose();
            throw;
        }
    }

    private static string InitialToolLabel(ToolKey tool) => tool switch
    {
        ToolKey.YtDlp => "yt-dlp",
        ToolKey.Deno => "Deno",
        ToolKey.Ffmpeg => "FFmpeg",
        ToolKey.Ffprobe => "FFprobe",
        _ => "Tool"
    };
}

public sealed class ComposedApplication : IDisposable
{
    private readonly CompositionStagingRoot stagingRoot;
    private int disposed;

    public ComposedApplication(
        PresentationController controller,
        DownloadViewModel viewModel,
        CompositionStagingRoot stagingRoot)
    {
        Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.stagingRoot = stagingRoot ?? throw new ArgumentNullException(nameof(stagingRoot));
    }

    public PresentationController Controller { get; }
    public DownloadViewModel ViewModel { get; }
    public string StagingRoot => stagingRoot.RootPath;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        try
        {
            Controller.Dispose();
        }
        finally
        {
            stagingRoot.Dispose();
        }
    }
}
