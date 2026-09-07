using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Velopack;
using Velopack.Sources;

namespace Rip.App.Updates;

public interface IAppUpdateService
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    Task<string?> CheckAsync();
    Task DownloadAsync(IProgress<int> progress);
    void ApplyAndRestart();
}

internal sealed class DevelopmentUpdateService : IAppUpdateService
{
    public bool IsInstalled => false;
    public string CurrentVersion => "development";
    public Task<string?> CheckAsync() => Task.FromResult<string?>(null);
    public Task DownloadAsync(IProgress<int> progress) => throw new InvalidOperationException("Install Rip to receive updates.");
    public void ApplyAndRestart() => throw new InvalidOperationException("Install Rip to receive updates.");
}

public sealed class VelopackUpdateService : IAppUpdateService
{
    public static string RepositoryUrl => typeof(VelopackUpdateService).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "UpdateRepository").Value!;
    private readonly UpdateManager manager = new(new GithubSource(RepositoryUrl, null, false));
    private UpdateInfo? update;
    public bool IsInstalled => manager.IsInstalled;
    public string CurrentVersion => manager.CurrentVersion?.ToString() ?? "development";
    public async Task<string?> CheckAsync()
    {
        update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return update?.TargetFullRelease.Version.ToString();
    }
    public Task DownloadAsync(IProgress<int> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return manager.DownloadUpdatesAsync(update ?? throw new InvalidOperationException("No update selected."), progress.Report);
    }
    public void ApplyAndRestart() => manager.ApplyUpdatesAndRestart(
        update?.TargetFullRelease ?? throw new InvalidOperationException("No update selected."));
}

/// <summary>Updates are offered automatically, but installation always requires a user action.</summary>
public sealed class AppUpdateViewModel : INotifyPropertyChanged
{
    private readonly IAppUpdateService service;
    private readonly Func<bool> downloadIsBusy;
    private bool isChecking;
    private bool isInstalling;
    private string? availableVersion;
    private string status = string.Empty;

    public AppUpdateViewModel(IAppUpdateService service, Func<bool> downloadIsBusy)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.downloadIsBusy = downloadIsBusy ?? throw new ArgumentNullException(nameof(downloadIsBusy));
        status = service.IsInstalled ? $"Rip {service.CurrentVersion}" : "Development build · install Rip to receive updates.";
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Status { get => status; private set { status = value; Notify(); } }
    public bool IsInstalling { get => isInstalling; private set { isInstalling = value; Notify(); RefreshAvailability(); } }
    public bool HasUpdate => availableVersion is not null;
    public bool CanCheck => service.IsInstalled && !isChecking && !IsInstalling;
    public bool CanInstall => HasUpdate && !isChecking && !IsInstalling && !downloadIsBusy();
    public void RefreshAvailability() { Notify(nameof(CanCheck)); Notify(nameof(CanInstall)); Notify(nameof(HasUpdate)); }

    public async Task CheckAsync()
    {
        if (!CanCheck) return;
        isChecking = true;
        RefreshAvailability();
        Status = "Checking for updates…";
        try
        {
            availableVersion = await service.CheckAsync();
            Status = availableVersion is null ? $"Rip {service.CurrentVersion} is up to date." : $"Rip {availableVersion} is available. Update when you’re ready.";
        }
        catch (Exception)
        {
            Status = "Couldn’t check for updates. You can keep using Rip and try again later.";
        }
        finally { isChecking = false; RefreshAvailability(); }
    }

    public async Task InstallAsync()
    {
        if (!CanInstall) return;
        IsInstalling = true;
        Status = "Downloading the update…";
        try
        {
            await service.DownloadAsync(new Progress<int>(percent =>
            {
                if (IsInstalling) Status = $"Downloading update · {Math.Clamp(percent, 0, 100)}%";
            }));
            Status = "Installing update and restarting Rip…";
            service.ApplyAndRestart();
        }
        catch (Exception)
        {
            Status = "Update couldn’t be installed. Your current version is unchanged. Try again when ready.";
        }
        finally { IsInstalling = false; }
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
