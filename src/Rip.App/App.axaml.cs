using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Rip.App.Composition;
using Rip.App.Presentation;
using AvaloniaApplication = Avalonia.Application;

namespace Rip.App;

public partial class App : AvaloniaApplication
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            void ShowDownloader()
            {
                var previous = desktop.MainWindow;
                var composed = ApplicationComposition.Create(new AvaloniaUiDispatcher(), updateService: new Updates.VelopackUpdateService());
                desktop.MainWindow = new MainWindow(composed);
                desktop.Exit += (_, _) => composed.Dispose();
                if (previous is not null) { desktop.MainWindow.Show(); previous.Close(); }
            }
            if (Setup.ToolBootstrapper.NeedsSetup()) desktop.MainWindow = new Setup.SetupWindow(ShowDownloader);
            else ShowDownloader();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public async Task InvokeAsync(Action action) => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action);
}
