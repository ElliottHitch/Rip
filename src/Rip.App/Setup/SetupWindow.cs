using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Rip.App.Setup;

public sealed class SetupWindow : Window, IDisposable
{
    private readonly TextBlock status = new() { Text = "Preparing video and audio tools…", TextWrapping = TextWrapping.Wrap };
    private readonly Button retry = new() { Content = "Retry setup", IsVisible = false };
    private readonly CancellationTokenSource cancellation = new();
    private readonly Action completed;
    private bool disposed;
    public SetupWindow(Action completed)
    {
        this.completed = completed ?? throw new ArgumentNullException(nameof(completed));
        Title = "Welcome to Rip";
        Width = 480; Height = 310; CanResize = false;
        Content = new StackPanel
        {
            Margin = new Thickness(32), Spacing = 18,
            Children =
            {
                new TextBlock { Text = "Welcome to Rip", FontSize = 28, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "A one-time setup downloads verified yt-dlp, Deno and FFmpeg tools. Your downloads stay separate from the app.", TextWrapping = TextWrapping.Wrap },
                status, retry,
            },
        };
        Opened += async (_, _) => await SetupAsync();
        retry.Click += async (_, _) => await SetupAsync();
        Closed += (_, _) => Dispose();
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }
    private async Task SetupAsync()
    {
        retry.IsVisible = false;
        try
        {
            await ToolBootstrapper.EnsureAsync(new Progress<string>(message => status.Text = message), cancellation.Token);
            if (!cancellation.IsCancellationRequested) completed();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            status.Text = "Setup couldn’t finish. Check your connection and free disk space, then retry. No unverified tools were enabled.";
            retry.IsVisible = true;
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel();
        cancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}
