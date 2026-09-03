using System.Security;
using UnifiDownloader.Application;
using UnifiDownloader.Domain;

namespace UnifiDownloader.Infrastructure;

/// <summary>
/// Infrastructure-only ownership registry for verified FFmpeg outputs. The Core artifact is
/// merely an identity; this registry is the only component that can resolve it to a path.
/// </summary>
public sealed class StagedArtifactRegistry
{
    private const int MaximumKeyLength = 128;
    private readonly string root;
    private readonly Func<string, bool> deleteFile;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public StagedArtifactRegistry(string stageRoot, Func<string, bool>? deleteFile = null)
    {
        if (!TryGetRegularDirectory(stageRoot, out var normalizedRoot))
        {
            throw new ArgumentException("The staging root must be an absolute regular directory.", nameof(stageRoot));
        }

        root = normalizedRoot;
        this.deleteFile = deleteFile ?? DeleteFile;
    }

    public bool Register(StagedArtifact artifact, string path)
    {
        if (!TryValidateArtifact(artifact) || !TryGetVerifiedFile(path, root, artifact.LengthBytes, out var normalizedPath))
        {
            return false;
        }
        if (!string.Equals(System.IO.Path.GetFileName(normalizedPath), artifact.FileName, StringComparison.Ordinal))
        {
            return false;
        }

        lock (gate)
        {
            if (entries.ContainsKey(artifact.StagingKey)) return false;
            entries.Add(artifact.StagingKey, new Entry(artifact, normalizedPath, root));
            return true;
        }
    }

    public bool TryResolve(StagedArtifact artifact, out string path)
    {
        path = string.Empty;
        if (!TryValidateArtifact(artifact)) return false;

        lock (gate)
        {
            if (!entries.TryGetValue(artifact.StagingKey, out var entry) || !entry.Artifact.Equals(artifact))
            {
                return false;
            }

            if (!TryGetVerifiedFile(entry.Path, entry.Root, artifact.LengthBytes, out var normalizedPath))
            {
                return false;
            }

            path = normalizedPath;
            return true;
        }
    }

    public bool Owns(StagedArtifact artifact)
    {
        if (!TryValidateArtifact(artifact)) return false;
        lock (gate) return entries.TryGetValue(artifact.StagingKey, out var entry) && entry.Artifact.Equals(artifact);
    }

    /// <summary>Deletes and unregisters an owned source. Unregistration is final even if deletion fails.</summary>
    public bool Consume(StagedArtifact artifact)
    {
        if (!TryValidateArtifact(artifact)) return false;

        lock (gate)
        {
            if (!entries.TryGetValue(artifact.StagingKey, out var entry) || !entry.Artifact.Equals(artifact))
            {
                return false;
            }

            // Validate the registration-time root and the complete path before invoking the
            // deleter. A replaced root must never turn cleanup into deletion outside our tree.
            if (!TryGetVerifiedFile(entry.Path, entry.Root, artifact.LengthBytes, out var normalizedPath))
            {
                // A missing file under an intact, verified directory chain is already consumed.
                // Any unverifiable root or parent remains a failed cleanup and is never deleted.
                var missingFromVerifiedTree = TryGetVerifiedParent(entry.Path, entry.Root) && !File.Exists(entry.Path);
                entries.Remove(artifact.StagingKey);
                return missingFromVerifiedTree;
            }

            var deleted = false;
            try
            {
                deleted = deleteFile(normalizedPath) || !File.Exists(normalizedPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
            {
                deleted = false;
            }

            entries.Remove(artifact.StagingKey);
            return deleted;
        }
    }

    private static bool TryValidateArtifact(StagedArtifact artifact) =>
        artifact is not null &&
        !string.IsNullOrWhiteSpace(artifact.StagingKey) &&
        artifact.StagingKey.Length <= MaximumKeyLength &&
        artifact.StagingKey.All(static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.') &&
        IsSafeMp4Name(artifact.FileName) &&
        Enum.IsDefined(artifact.Container) &&
        artifact.LengthBytes > 0 && artifact.Verified;

    private static bool IsSafeMp4Name(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        fileName.Length <= 255 &&
        fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) &&
        fileName is not ".mp4" and not "." and not ".." &&
        fileName.All(static c => !char.IsControl(c) && c is not '/' and not '\\' and not ':' and not '?' and not '#');

    private static bool TryGetRegularDirectory(string? candidate, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) return false;
        try
        {
            normalized = NormalizeFullPath(candidate);
            return IsRegularDirectory(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    internal static bool TryGetVerifiedFile(string? candidate, string root, long expectedLength, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate) || expectedLength <= 0 || !Path.IsPathFullyQualified(candidate)) return false;
        try
        {
            if (!TryGetRegularDirectory(root, out var normalizedRoot)) return false;
            normalized = NormalizeFullPath(candidate);
            if (!IsWithin(normalizedRoot, normalized) || !TryGetVerifiedParent(normalized, normalizedRoot)) return false;
            var attributes = File.GetAttributes(normalized);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
            return new FileInfo(normalized).Length == expectedLength;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = NormalizeFullPath(root);
        var normalizedCandidate = NormalizeFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(normalizedRoot, normalizedCandidate, comparison)) return false;

        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) || normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, comparison);
    }

    private static bool TryGetVerifiedParent(string candidate, string root)
    {
        var parent = Path.GetDirectoryName(candidate);
        var filesystemRoot = Path.GetPathRoot(root);
        return parent is not null && filesystemRoot is not null && ValidateDirectoryChain(filesystemRoot, parent);
    }

    private static bool ValidateDirectoryChain(string root, string directory)
    {
        if (!IsRegularDirectory(root) || !IsWithinOrEqual(root, directory)) return false;

        var relative = Path.GetRelativePath(root, directory);
        if (relative == ".") return true;

        var current = root;
        foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..") return false;
            current = Path.Combine(current, component);
            if (!IsRegularDirectory(current)) return false;
        }

        return true;
    }

    private static bool IsWithinOrEqual(string root, string candidate)
    {
        var normalizedRoot = NormalizeFullPath(root);
        var normalizedCandidate = NormalizeFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(normalizedRoot, normalizedCandidate, comparison)) return true;

        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) || normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, comparison);
    }

    private static string NormalizeFullPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return pathRoot is not null && string.Equals(fullPath, pathRoot, comparison)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsRegularDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
    }

    private static bool DeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed record Entry(StagedArtifact Artifact, string Path, string Root);
}

/// <summary>Infrastructure-only registry for committed local outputs and their opaque handles.</summary>
public sealed class PublishedOutputRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public bool Register(VerifiedLocalMp4 output, string path)
    {
        if (output is null || !TryGetRegistrationRoot(path, out var root) ||
            !StagedArtifactRegistry.TryGetVerifiedFile(path, root, output.LengthBytes, out var normalizedPath)) return false;
        if (!string.Equals(Path.GetFileName(normalizedPath), output.FileName, StringComparison.Ordinal)) return false;
        lock (gate)
        {
            if (entries.ContainsKey(output.OutputKey)) return false;
            entries.Add(output.OutputKey, new Entry(output, normalizedPath, root));
            return true;
        }
    }

    public bool TryResolve(VerifiedLocalMp4 output, out string path)
    {
        path = string.Empty;
        if (output is null) return false;
        lock (gate)
        {
            if (!entries.TryGetValue(output.OutputKey, out var entry) || !entry.Output.Equals(output)) return false;
            if (!StagedArtifactRegistry.TryGetVerifiedFile(entry.Path, entry.Root, output.LengthBytes, out var normalizedPath) ||
                !string.Equals(Path.GetFileName(normalizedPath), output.FileName, StringComparison.Ordinal)) return false;
            path = normalizedPath;
            return true;
        }
    }

    public bool Contains(VerifiedLocalMp4 output) => TryResolve(output, out _);

    private static bool TryGetRegistrationRoot(string path, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            var normalizedPath = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(normalizedPath);
            return parent is not null && TryGetRegularDirectory(parent, out root);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryGetRegularDirectory(string candidate, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            normalized = Path.GetFullPath(candidate);
            var pathRoot = Path.GetPathRoot(normalized);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (pathRoot is null || !string.Equals(normalized, pathRoot, comparison))
            {
                normalized = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            var attributes = File.GetAttributes(normalized);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    private sealed record Entry(VerifiedLocalMp4 Output, string Path, string Root);
}

/// <summary>Copies an owned staged artifact into a new local MP4 without overwrite.</summary>
public sealed class LocalPublicationStore : IPublicationStore
{
    private const int CopyBufferSize = 64 * 1024;
    private readonly StagedArtifactRegistry staged;
    private readonly PublishedOutputRegistry published;
    private readonly IDiagnostics diagnostics;

    public LocalPublicationStore(StagedArtifactRegistry staged, PublishedOutputRegistry published, IDiagnostics diagnostics)
    {
        this.staged = staged ?? throw new ArgumentNullException(nameof(staged));
        this.published = published ?? throw new ArgumentNullException(nameof(published));
        this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public ValueTask<ProviderResult<VerifiedLocalMp4>> PublishAsync(
        StagedArtifact artifact,
        OutputOptions output,
        CancellationToken cancellationToken)
    {
        return PublishCoreAsync(artifact, output, cancellationToken);
    }

    private async ValueTask<ProviderResult<VerifiedLocalMp4>> PublishCoreAsync(
        StagedArtifact artifact,
        OutputOptions output,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(artifact, output, out var destination, out var finalPath))
        {
            return Failure<VerifiedLocalMp4>(SafeInfrastructureErrors.InvalidPublicationRequest());
        }

        if (output.AllowOverwrite || PathExists(finalPath))
        {
            return Failure<VerifiedLocalMp4>(SafeInfrastructureErrors.PublicationConflict());
        }

        string? temporaryPath = null;
        var committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!staged.TryResolve(artifact, out var sourcePath))
            {
                CleanupSource(artifact);
                return Failure<VerifiedLocalMp4>(SafeInfrastructureErrors.InvalidPublicationRequest());
            }

            temporaryPath = AllocateTemporary(destination);
            await CopyExactAsync(sourcePath, temporaryPath, artifact.LengthBytes, cancellationToken).ConfigureAwait(false);
            if (!StagedArtifactRegistry.TryGetVerifiedFile(temporaryPath, destination, artifact.LengthBytes, out _))
            {
                return Failure<VerifiedLocalMp4>(SafeInfrastructureErrors.InvalidPublicationRequest());
            }

            File.Move(temporaryPath, finalPath, overwrite: false);
            temporaryPath = null;
            committed = true;
            if (!StagedArtifactRegistry.TryGetVerifiedFile(finalPath, destination, artifact.LengthBytes, out _))
            {
                CleanupSource(artifact);
                return Failure<VerifiedLocalMp4>(SafeInfrastructureErrors.InvalidPublicationRequest());
            }

            var outputKey = "output-" + Guid.NewGuid().ToString("N");
            var result = new VerifiedLocalMp4(Path.GetFileName(finalPath), outputKey, artifact.LengthBytes);
            if (!published.Register(result, finalPath))
            {
                CleanupSource(artifact);
                return Failure<VerifiedLocalMp4>(SafeInfrastructureErrors.InvalidPublicationRequest());
            }

            if (!staged.Consume(artifact)) ReportCleanupWarning();
            return new ProviderResult<VerifiedLocalMp4>(result, null);
        }
        catch (OperationCanceledException)
        {
            if (!committed) CleanupSource(artifact);
            return Failure<VerifiedLocalMp4>(SafeInfrastructureErrors.PublicationCancelled());
        }
        catch (IOException)
        {
            if (!committed) CleanupSource(artifact);
            return Failure<VerifiedLocalMp4>(PathExists(finalPath)
                ? SafeInfrastructureErrors.PublicationConflict()
                : SafeInfrastructureErrors.InvalidPublicationRequest());
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            if (!committed) CleanupSource(artifact);
            return Failure<VerifiedLocalMp4>(SafeInfrastructureErrors.InvalidPublicationRequest());
        }
        finally
        {
            if (temporaryPath is not null) TryDelete(temporaryPath);
        }
    }

    private bool TryValidateRequest(StagedArtifact artifact, OutputOptions output, out string destination, out string finalPath)
    {
        destination = string.Empty;
        finalPath = string.Empty;
        if (artifact is null || output is null || !staged.Owns(artifact) || !Enum.IsDefined(output.Container)) return false;
        if (!TryGetRegularDirectory(output.Directory, out destination)) return false;
        string stem;
        try { stem = SafeFileNamePolicy.Normalize(output.FileStem); }
        catch (ArgumentException) { return false; }
        finalPath = Path.Combine(destination, stem + ".mp4");
        return IsWithin(destination, finalPath);
    }

    private static async Task CopyExactAsync(string sourcePath, string temporaryPath, long expectedLength, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != expectedLength) throw new IOException();
        await using var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[CopyBufferSize];
        long copied = 0;
        while (copied < expectedLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, expectedLength - copied);
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException();
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
        }
        if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0) throw new IOException();
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        target.Flush(flushToDisk: true);
    }

    private static string AllocateTemporary(string destination)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var path = Path.Combine(destination, ".unifi-publication-" + Guid.NewGuid().ToString("N") + ".tmp");
            if (IsWithin(destination, path) && !PathExists(path)) return path;
        }
        throw new IOException();
    }

    private void CleanupSource(StagedArtifact artifact)
    {
        if (staged.Owns(artifact) && !staged.Consume(artifact)) ReportCleanupWarning();
    }

    private void ReportCleanupWarning()
    {
        try { diagnostics.Report(SafeInfrastructureErrors.PublicationCleanupIncomplete()); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException) { }
    }

    private static bool TryGetRegularDirectory(string? candidate, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) return false;
        try
        {
            normalized = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(normalized)) return false;
            var attributes = File.GetAttributes(normalized);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException) { }
    }

    private static ProviderResult<T> Failure<T>(SafeDownloadError error) => new(default, error);
}