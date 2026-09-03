using Avalonia;
using UnifiDownloader.App.Composition;

namespace UnifiDownloader.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--deterministic-smoke", StringComparer.Ordinal))
        {
            // This gate deliberately does not compose Infrastructure. Composition creates the
            // run-owned staging root and is therefore reserved for ordinary startup/real runs.
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
