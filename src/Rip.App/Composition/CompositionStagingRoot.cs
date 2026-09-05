using System.Security;

namespace Rip.App.Composition;

/// <summary>
/// Owns exactly one composition-created staging directory. It never removes the shared staging
/// parent or any path supplied as a publication destination.
/// </summary>
public sealed class CompositionStagingRoot : IDisposable
{
    private readonly string rootPath;
    private int disposed;

    private CompositionStagingRoot(string rootPath) => this.rootPath = rootPath;

    public string RootPath => rootPath;

    public static CompositionStagingRoot Create(Func<string>? rootFactory = null)
    {
        var candidate = rootFactory?.Invoke() ?? System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "rip-stage",
            Guid.NewGuid().ToString("N"));
        if (string.IsNullOrWhiteSpace(candidate) || !System.IO.Path.IsPathFullyQualified(candidate))
        {
            throw new ArgumentException("The composition staging root must be a non-root absolute path.", nameof(rootFactory));
        }

        var normalized = System.IO.Path.GetFullPath(candidate);
        if (string.Equals(normalized, System.IO.Path.GetPathRoot(normalized), GetPathComparison()))
        {
            throw new ArgumentException("The composition staging root must be a non-root absolute path.", nameof(rootFactory));
        }

        // A composition owns a fresh directory, rather than a caller-owned existing directory.
        // This makes recursive cleanup safe and keeps the injectable test seam honest.
        if (Directory.Exists(normalized) || File.Exists(normalized))
        {
            throw new IOException("The composition staging root already exists.");
        }

        try
        {
            Directory.CreateDirectory(normalized);
            var attributes = File.GetAttributes(normalized);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            {
                throw new IOException("The composition staging root is not a regular directory.");
            }

            return new CompositionStagingRoot(normalized);
        }
        catch
        {
            TryDeleteFreshRoot(normalized);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        try
        {
            if (!Directory.Exists(rootPath)) return;
            var attributes = File.GetAttributes(rootPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            {
                return;
            }

            Directory.Delete(rootPath, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            // Teardown must not mask the completed/failed/cancelled run. The exact owned root is
            // the only path considered, and a later process may retry cleanup if needed.
        }
    }

    private static void TryDeleteFreshRoot(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
        }
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
