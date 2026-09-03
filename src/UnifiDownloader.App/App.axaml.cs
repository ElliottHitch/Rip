using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UnifiDownloader.App.Composition;
using UnifiDownloader.App.Presentation;
using AvaloniaApplication = Avalonia.Application;

namespace UnifiDownloader.App;

public partial class App : AvaloniaApplication
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var composed = ApplicationComposition.Create(new AvaloniaUiDispatcher());
            desktop.MainWindow = new MainWindow(composed);
            desktop.Exit += (_, _) => composed.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public async Task InvokeAsync(Action action) => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action);
}
