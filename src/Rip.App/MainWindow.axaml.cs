using Avalonia.Controls;
using Avalonia.Interactivity;
using Rip.App.Composition;
using Rip.App.Presentation;

namespace Rip.App;

public partial class MainWindow : Window
{
    private readonly PresentationController controller;
    private readonly IDisposable lifetime;

    public MainWindow() : this(ApplicationComposition.Create(new AvaloniaUiDispatcher()))
    {
    }

    public MainWindow(ComposedApplication composed)
    {
        ArgumentNullException.ThrowIfNull(composed);
        controller = composed.Controller;
        lifetime = composed;
        DataContext = composed.ViewModel;
        InitializeWindow();
    }

    public MainWindow(PresentationController controller, DownloadViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(viewModel);
        this.controller = controller;
        lifetime = controller;
        DataContext = viewModel;
        InitializeWindow();
    }

    private void InitializeWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            ConfigureOutputFolderPicker();
            await controller.TestEnvironmentAsync();
            if (DataContext is DownloadViewModel model) await model.Updates.CheckAsync();
        };
        Closed += (_, _) => lifetime.Dispose();
    }

    private void ConfigureOutputFolderPicker()
    {
        try
        {
            controller.ConfigureOutputFolderPicker(new AvaloniaStorageFolderPicker(StorageProvider));
        }
        catch (Exception)
        {
            controller.ConfigureOutputFolderPicker(null);
        }
    }


    private async void StartClicked(object? sender, RoutedEventArgs e) => await controller.StartAsync();
    private async void RetryClicked(object? sender, RoutedEventArgs e) => await controller.RetryAsync();
    private async void ChooseOutputFolderClicked(object? sender, RoutedEventArgs e) => await controller.ChooseOutputFolderAsync();
    private void CancelClicked(object? sender, RoutedEventArgs e) => controller.Cancel();
    private async void TestEnvironmentClicked(object? sender, RoutedEventArgs e) => await controller.TestEnvironmentAsync();
    private async void OpenInBrowserClicked(object? sender, RoutedEventArgs e) => await controller.OpenInBrowserAsync();
    private void StartNewRunClicked(object? sender, RoutedEventArgs e) => controller.StartNewRun();
    private async void CheckUpdatesClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DownloadViewModel model) await model.Updates.CheckAsync();
    }
    private async void InstallUpdateClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DownloadViewModel model) await model.Updates.InstallAsync();
    }
}
