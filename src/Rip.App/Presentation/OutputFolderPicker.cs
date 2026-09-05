using Avalonia.Platform.Storage;

namespace Rip.App.Presentation;

/// <summary>App-level boundary for choosing one user-approved local output directory.</summary>
public interface IOutputFolderPicker
{
    bool IsAvailable { get; }
    string UnavailableReason { get; }
    ValueTask<string?> PickAsync(string? currentFolder, CancellationToken cancellationToken);
}

/// <summary>Adapts Avalonia's platform-native folder picker without exposing storage handles to the app state.</summary>
public sealed class AvaloniaStorageFolderPicker : IOutputFolderPicker
{
    private const string DefaultUnavailableReason =
        "Native folder picking is unavailable on this platform; type a destination in the field above.";
    private readonly IStorageProvider storageProvider;

    public AvaloniaStorageFolderPicker(IStorageProvider storageProvider)
    {
        this.storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
    }

    public bool IsAvailable => storageProvider.CanPickFolder;
    public string UnavailableReason => DefaultUnavailableReason;

    public async ValueTask<string?> PickAsync(string? currentFolder, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return null;

        var options = new FolderPickerOpenOptions
        {
            Title = "Choose output folder",
            AllowMultiple = false
        };

        if (TryCreateLocalPathUri(currentFolder, out var currentPath))
        {
            try
            {
                options.SuggestedStartLocation = await storageProvider
                    .TryGetFolderFromPathAsync(currentPath)
                    .ConfigureAwait(true);
            }
            catch (Exception)
            {
                // A stale or inaccessible typed path must not prevent the native picker from opening.
            }
        }

        var folders = await storageProvider
            .OpenFolderPickerAsync(options)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        var selected = folders.Count > 0 ? folders[0] : null;
        if (selected is null) return null;

        try
        {
            return TryGetSafeLocalPath(selected.Path, out var path) ? path : null;
        }
        finally
        {
            selected.Dispose();
        }
    }

    private static bool TryCreateLocalPathUri(string? value, out Uri path)
    {
        path = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0')) return false;

        try
        {
            var fullPath = Path.GetFullPath(value.Trim());
            if (!Path.IsPathRooted(fullPath) || !Uri.TryCreate(fullPath, UriKind.Absolute, out var created)) return false;
            path = created;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGetSafeLocalPath(Uri? value, out string path)
    {
        path = string.Empty;
        if (value is null || !value.IsAbsoluteUri || !value.IsFile || !string.IsNullOrEmpty(value.UserInfo)) return false;

        try
        {
            var localPath = value.LocalPath;
            if (string.IsNullOrWhiteSpace(localPath) || localPath.Contains('\0') || !Path.IsPathRooted(localPath))
            {
                return false;
            }

            path = Path.GetFullPath(localPath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
