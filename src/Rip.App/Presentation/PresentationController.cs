using Rip.Application;
using Rip.Domain;

namespace Rip.App.Presentation;

public interface IUiDispatcher
{
    Task InvokeAsync(Action action);
}

public interface IDownloadRunner
{
    ValueTask<DownloadRunResult> RunAsync(DownloadRequest request, CancellationToken cancellationToken);
}

public sealed class ApplicationServiceRunner : IDownloadRunner
{
    private readonly DownloadApplicationService service;

    public ApplicationServiceRunner(DownloadApplicationService service) => this.service = service ?? throw new ArgumentNullException(nameof(service));

    public ValueTask<DownloadRunResult> RunAsync(DownloadRequest request, CancellationToken cancellationToken) =>
        service.RunAsync(request, cancellationToken);
}

public interface IEnvironmentProbe
{
    ValueTask<IReadOnlyList<CapabilityStatus>> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>Projects Core events through the Core reducer and owns command orchestration.</summary>
public sealed class PresentationController : IDisposable
{
    private readonly IDownloadRunner runner;
    private readonly ILocalFileOpener opener;
    private readonly IEnvironmentProbe environment;
    private readonly IUiDispatcher dispatcher;
    private IOutputFolderPicker? outputFolderPicker;
    private CancellationTokenSource? cancellation;
    private RunIdentity? currentRun;
    private RunIdentity? previousRun;
    private LifecycleSnapshot? lifecycle;

    public PresentationController(
        IDownloadRunner runner,
        ILocalFileOpener opener,
        IEnvironmentProbe environment,
        DownloadViewModel viewModel,
        IUiDispatcher dispatcher,
        IOutputFolderPicker? outputFolderPicker = null)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.opener = opener ?? throw new ArgumentNullException(nameof(opener));
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ConfigureOutputFolderPicker(outputFolderPicker);
    }

    public DownloadViewModel ViewModel { get; }
    public bool HasActiveRun => lifecycle is { IsTerminal: false };

    public void ConfigureOutputFolderPicker(IOutputFolderPicker? picker)
    {
        outputFolderPicker = picker;
        ViewModel.SetOutputFolderPickerAvailability(
            picker?.IsAvailable == true,
            picker?.UnavailableReason);
    }

    public async Task<bool> ChooseOutputFolderAsync()
    {
        var picker = outputFolderPicker;
        if (ViewModel.IsBusy || ViewModel.SelectedOperation == DownloadOperation.Metadata || picker?.IsAvailable != true)
        {
            return false;
        }

        try
        {
            var selectedFolder = await picker.PickAsync(ViewModel.OutputFolder, CancellationToken.None).ConfigureAwait(false);
            if (selectedFolder is null) return false;

            await dispatcher.InvokeAsync(() => ViewModel.OutputFolder = selectedFolder).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            await dispatcher.InvokeAsync(ViewModel.ReportFolderPickerFailure).ConfigureAwait(false);
            return false;
        }
    }

    public async Task<bool> StartAsync()
    {
        if (!ViewModel.CanStart || !ViewModel.TryBuildRequest(out var request) || request is null) return false;

        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        previousRun = currentRun;
        currentRun = null;
        lifecycle = null;
        ViewModel.BeginRun();
        try
        {
            var result = await runner.RunAsync(request, cancellation.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (currentRun == result.Run && lifecycle is { IsTerminal: true }) ViewModel.ApplyResult(result);
            }).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            await dispatcher.InvokeAsync(ViewModel.ApplyControllerFailure).ConfigureAwait(false);
            return false;
        }
        catch (Exception)
        {
            await dispatcher.InvokeAsync(ViewModel.ApplyControllerFailure).ConfigureAwait(false);
            return false;
        }
        finally
        {
            cancellation?.Dispose();
            cancellation = null;
        }
    }

    public async Task<bool> RetryAsync()
    {
        if (!ViewModel.CanRetry) return false;
        ViewModel.PrepareRetry();
        return await StartAsync().ConfigureAwait(false);
    }

    public void Cancel()
    {
        if (!ViewModel.IsBusy) return;
        if (lifecycle is { IsTerminal: false } state) lifecycle = LifecycleReducer.RequestCancellation(state);
        ViewModel.RequestCancellation();
        cancellation?.Cancel();
    }

    public async Task TestEnvironmentAsync()
    {
        try
        {
            var statuses = await environment.ProbeAsync(CancellationToken.None).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => ViewModel.SetCapabilities(statuses)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await dispatcher.InvokeAsync(() => ViewModel.SetCapabilities(
                [new CapabilityStatus("Environment", "Unavailable", "The environment could not be checked safely.")])).ConfigureAwait(false);
        }
    }

    public async Task<bool> OpenInBrowserAsync()
    {
        var file = ViewModel.PublishedFile;
        if (!ViewModel.CanOpenInBrowser || file is null) return false;

        try
        {
            var result = await opener.OpenAsync(file, CancellationToken.None).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => ViewModel.SetOpenResult(result)).ConfigureAwait(false);
            return result.Opened;
        }
        catch (Exception)
        {
            await dispatcher.InvokeAsync(() => ViewModel.SetOpenResult(new OpenResult(false, null))).ConfigureAwait(false);
            return false;
        }
    }

    public void StartNewRun()
    {
        if (ViewModel.IsBusy) return;
        previousRun = currentRun;
        currentRun = null;
        lifecycle = null;
        ViewModel.StartNewRun();
    }

    /// <summary>Called by the observer; returns false for stale, duplicate, invalid, or terminal events.</summary>
    public bool ApplyEvent(DownloadEvent downloadEvent)
    {
        ArgumentNullException.ThrowIfNull(downloadEvent);
        if (currentRun is null)
        {
            if (previousRun == downloadEvent.Run) return false;
            currentRun = downloadEvent.Run;
            previousRun = null;
            lifecycle = new LifecycleSnapshot(downloadEvent.Run);
        }

        if (currentRun != downloadEvent.Run || lifecycle is null) return false;
        var application = LifecycleReducer.Apply(lifecycle, downloadEvent);
        if (!application.Accepted) return false;
        lifecycle = application.State;
        ViewModel.ApplyEvent(downloadEvent);
        return true;
    }

    public ValueTask ObserveAsync(DownloadEvent downloadEvent, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return ValueTask.CompletedTask;
        return new ValueTask(dispatcher.InvokeAsync(() => ApplyEvent(downloadEvent)));
    }

    public void Dispose()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }
}

/// <summary>Late-bound observer keeps manual composition explicit without a service locator.</summary>
public sealed class PresentationObserver : IDownloadObserver
{
    private PresentationController? controller;

    public void Attach(PresentationController presentationController) => controller = presentationController ?? throw new ArgumentNullException(nameof(presentationController));

    public ValueTask ObserveAsync(DownloadEvent downloadEvent, CancellationToken cancellationToken) =>
        controller?.ObserveAsync(downloadEvent, cancellationToken) ?? ValueTask.CompletedTask;
}
