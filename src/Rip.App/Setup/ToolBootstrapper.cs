using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Rip.App.Composition;

namespace Rip.App.Setup;

/// <summary>Installs pinned, checksum-verified upstream tools outside the updatable app directory.</summary>
public static class ToolBootstrapper
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(15) };
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RipData");
    public static string ManifestPath => Path.Combine(DataDirectory, "tools", ToolManifestLoader.ManifestFileName);
    private static string CatalogPath => Path.Combine(AppContext.BaseDirectory, "tool-bootstrap.json");
    private static string StampPath => Path.Combine(DataDirectory, "tools", "catalog.sha256");

    public static bool NeedsSetup()
    {
        if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ToolManifestLoader.ManifestPathEnvironmentVariable))) return false;
        return !File.Exists(ManifestPath) || !File.Exists(StampPath) ||
            File.ReadAllText(StampPath) != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(CatalogPath)));
    }

    public static async Task EnsureAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var catalogBytes = await File.ReadAllBytesAsync(CatalogPath, cancellationToken).ConfigureAwait(false);
        var catalogHash = Convert.ToHexString(SHA256.HashData(catalogBytes));
        using var catalog = JsonDocument.Parse(catalogBytes);
        var toolRoot = Path.GetDirectoryName(ManifestPath)!;
        Directory.CreateDirectory(toolRoot);
        var stage = Path.Combine(toolRoot, "setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var tools = new Dictionary<string, object>(StringComparer.Ordinal);
        var expectations = new Dictionary<string, object>(StringComparer.Ordinal);
        var repositories = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var asset in catalog.RootElement.GetProperty("assets").EnumerateArray())
            {
                var url = new Uri(asset.GetProperty("url").GetString()!);
                if (url.Scheme != "https" || url.Host != "github.com") throw new InvalidDataException("Untrusted tool source.");
                var expected = asset.GetProperty("sha256").GetString()!;
                var destination = Path.Combine(toolRoot, expected);
                var assetName = Path.GetFileName(url.AbsolutePath);
                progress.Report($"Setting up {assetName}…");
                if (!Directory.Exists(destination))
                {
                    var download = Path.Combine(stage, assetName);
                    using (var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        await using var output = File.Create(download);
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }
                    await using (var input = File.OpenRead(download))
                    {
                        var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
                        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Tool checksum mismatch.");
                    }
                    var extracted = Path.Combine(stage, expected);
                    Directory.CreateDirectory(extracted);
                    if (asset.GetProperty("archive").GetBoolean()) ZipFile.ExtractToDirectory(download, extracted);
                    else File.Move(download, Path.Combine(extracted, assetName));
                    Directory.Move(extracted, destination);
                }
                var repository = asset.GetProperty("repository").GetString()!;
                repositories.Add(repository);
                foreach (var tool in asset.GetProperty("tools").EnumerateArray())
                {
                    var key = tool.GetProperty("key").GetString()!;
                    var file = tool.GetProperty("file").GetString()!;
                    var version = tool.GetProperty("version").GetString()!;
                    var path = Directory.EnumerateFiles(destination, file, SearchOption.AllDirectories).Single();
                    await using var input = File.OpenRead(path);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
                    if (!hash.Equals(tool.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Executable checksum mismatch.");
                    tools.Add(key, new { key, sourceRepository = repository, targetRid = "win-x64", assetName = file, version,
                        expectedSha256 = hash, executablePath = path, isVerified = true });
                    expectations.Add(key, new { key, sourceRepository = repository, targetRid = "win-x64", assetName = file,
                        version, expectedSha256 = hash, requireVerified = true });
                }
            }
            var manifest = JsonSerializer.Serialize(new { schemaVersion = 1, executionTargetRid = "win-x64",
                allowedRepositories = repositories, tools, trustedExpectations = expectations });
            var temporaryManifest = Path.Combine(stage, "manifest.json");
            await File.WriteAllTextAsync(temporaryManifest, manifest, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryManifest, ManifestPath, true);
            await File.WriteAllTextAsync(StampPath, catalogHash, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(stage, true); }
            catch (IOException) { /* A later startup can retry without publishing a partial manifest. */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
