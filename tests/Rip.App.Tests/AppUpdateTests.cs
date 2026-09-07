using Rip.App.Updates;
using Xunit;

namespace Rip.App.Tests;

public sealed class AppUpdateTests
{
    [Fact]
    public async Task Checking_offers_update_without_installing_it()
    {
        var service = new FakeUpdater();
        var model = new AppUpdateViewModel(service, () => false);
        await model.CheckAsync();
        Assert.True(model.HasUpdate);
        Assert.True(model.CanInstall);
        Assert.Equal(0, service.Downloads);
        Assert.Equal(0, service.Restarts);
    }

    [Fact]
    public async Task Active_download_prevents_update_and_restart()
    {
        var service = new FakeUpdater();
        var model = new AppUpdateViewModel(service, () => true);
        await model.CheckAsync();
        await model.InstallAsync();
        Assert.False(model.CanInstall);
        Assert.Equal(0, service.Downloads);
        Assert.Equal(0, service.Restarts);
    }

    [Fact]
    public async Task Successful_download_precedes_restart()
    {
        var service = new FakeUpdater();
        var model = new AppUpdateViewModel(service, () => false);
        await model.CheckAsync();
        await model.InstallAsync();
        Assert.Equal(1, service.Downloads);
        Assert.Equal(1, service.Restarts);
    }

    [Fact]
    public async Task Failed_update_keeps_current_app_and_allows_retry()
    {
        var service = new FakeUpdater { FailDownload = true };
        var model = new AppUpdateViewModel(service, () => false);
        await model.CheckAsync();
        await model.InstallAsync();
        Assert.Equal(0, service.Restarts);
        Assert.True(model.CanInstall);
        Assert.Contains("current version is unchanged", model.Status, StringComparison.Ordinal);
        service.FailDownload = false;
        await model.InstallAsync();
        Assert.Equal(1, service.Restarts);
    }

    [Fact]
    public async Task Development_build_never_contacts_release_service()
    {
        var service = new FakeUpdater { IsInstalled = false };
        var model = new AppUpdateViewModel(service, () => false);
        await model.CheckAsync();
        Assert.Equal(0, service.Checks);
        Assert.False(model.CanCheck);
    }

    [Fact]
    public async Task Offline_check_is_recoverable()
    {
        var service = new FakeUpdater { FailCheck = true };
        var model = new AppUpdateViewModel(service, () => false);
        await model.CheckAsync();
        Assert.True(model.CanCheck);
        Assert.Contains("try again later", model.Status, StringComparison.Ordinal);
        Assert.False(model.HasUpdate);
    }

    private sealed class FakeUpdater : IAppUpdateService
    {
        public bool IsInstalled { get; set; } = true;
        public string CurrentVersion => "1.0.0";
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public int Restarts { get; private set; }
        public bool FailDownload { get; set; }
        public bool FailCheck { get; set; }
        public Task<string?> CheckAsync()
        {
            Checks++;
            return FailCheck ? Task.FromException<string?>(new HttpRequestException()) : Task.FromResult<string?>("1.0.1");
        }
        public Task DownloadAsync(IProgress<int> progress)
        {
            Downloads++;
            return FailDownload ? Task.FromException(new IOException()) : Task.CompletedTask;
        }
        public void ApplyAndRestart()
        {
            Assert.True(Downloads > 0);
            Restarts++;
        }
    }
}
