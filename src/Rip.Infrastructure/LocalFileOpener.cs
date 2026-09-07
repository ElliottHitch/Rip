using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using Rip.Application;
using Rip.Domain;

namespace Rip.Infrastructure;

/// <summary>Launches an already-validated local file URI through the operating system handler.</summary>
public interface ILocalFileLauncher
{
    ValueTask<bool> LaunchAsync(Uri localFileUri, CancellationToken cancellationToken);
}

/// <summary>Uses the operating system's default handler without selecting a browser or shell command.</summary>
public sealed class SystemLocalFileLauncher : ILocalFileLauncher
{
    public ValueTask<bool> LaunchAsync(Uri localFileUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localFileUri);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsEncodedLocalFileUri(localFileUri))
        {
            return ValueTask.FromResult(false);
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = localFileUri.AbsoluteUri,
                UseShellExecute = true
            });
            return ValueTask.FromResult(process is not null);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or SecurityException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return ValueTask.FromResult(false);
        }
    }

    private static bool IsEncodedLocalFileUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.IsFile &&
        string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.Host) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);
}

/// <summary>
/// Resolves an opaque published output through the ownership registry and opens only its
/// freshly revalidated local file URI.
/// </summary>
public sealed class SystemLocalFileOpener : ILocalFileOpener
{
    private readonly PublishedOutputRegistry publishedOutputs;
    private readonly ILocalFileLauncher launcher;

    public SystemLocalFileOpener(PublishedOutputRegistry publishedOutputs, ILocalFileLauncher launcher)
    {
        this.publishedOutputs = publishedOutputs ?? throw new ArgumentNullException(nameof(publishedOutputs));
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    public async ValueTask<OpenResult> OpenAsync(
        VerifiedLocalMp4 file,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file is null || !publishedOutputs.TryResolve(file, out var path))
            {
                return Failure();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var localFileUri = CreateLocalFileUri(path);
            cancellationToken.ThrowIfCancellationRequested();

            var opened = await launcher.LaunchAsync(localFileUri, cancellationToken).ConfigureAwait(false);
            return opened ? new OpenResult(true, null) : Failure();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or SecurityException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Failure();
        }
    }

    private static Uri CreateLocalFileUri(string path)
    {
        var uri = new Uri(path, UriKind.Absolute);
        if (!uri.IsFile || !string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("The verified output did not produce a local file URI.", nameof(path));
        }

        return uri;
    }

    private static OpenResult Failure() => new(false, SafeInfrastructureErrors.LocalFileOpenFailed());

    private static OpenResult Cancelled() => new(false, SafeInfrastructureErrors.LocalFileOpenCancelled());
}
