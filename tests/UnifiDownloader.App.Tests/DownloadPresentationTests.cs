using UnifiDownloader.App.Presentation;
using UnifiDownloader.Application;
using UnifiDownloader.Domain;
using Xunit;

namespace UnifiDownloader.App.Tests;

public sealed class DownloadPresentationTests
{
    private static readonly RunIdentity Run = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 0);
    private static readonly RunIdentity OtherRun = new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 0);

    [Fact]
    public void Request_mapping_preserves_operation_fps_container_and_browser_choice()
    {
        var model = ValidVideoModel();
        model.SelectedOperation = DownloadOperation.Audio;
        model.SelectedContainer = OutputContainer.Mp4;
        model.FrameRateTarget = 25;
        model.UseBrowserSession = true;
        model.SelectedBrowser = BrowserKind.Firefox;

        Assert.True(model.TryBuildRequest(out var request));
        Assert.NotNull(request);
        Assert.Equal(DownloadOperation.Audio, request!.Operation);
        Assert.Equal(OutputContainer.Mp4, request.Output.Container);
        Assert.Equal(25, request.Output.FrameRateTarget);
        Assert.Equal(BrowserKind.Firefox, request.BrowserSession!.Kind);
        Assert.Equal("[video-reference]", request.Video.ToString());
    }

    [Fact]
    public void Consent_is_off_by_default_and_cleared_by_terminal_event()
    {
        var model = ValidVideoModel();
        Assert.False(model.UseBrowserSession);
        model.UseBrowserSession = true;
        model.SelectedBrowser = BrowserKind.Chrome;
        var controller = Controller(model);
        model.BeginRun();
        Assert.True(controller.ApplyEvent(new DownloadCancelled(Run, DownloadStage.Downloading, 1)));
        Assert.False(model.UseBrowserSession);
        Assert.Null(model.SelectedBrowser);
        Assert.False(model.CanCancel);
    }

    [Fact]
    public async Task Try_again_is_exposed_only_for_retryable_failures_and_preserves_request_fields()
    {
        var model = ValidVideoModel();
        var controller = Controller(model);
        model.BeginRun();
        var error = SafeDownloadError.Create(
            DownloadErrorCode.AccessDenied,
            DownloadStage.Downloading,
            "The stream could not be accessed.",
            RetryAction.RefreshStream,
            new RedactedDiagnosticToken("diag-test-retry"));

        Assert.True(controller.ApplyEvent(new DownloadFailed(Run, DownloadStage.Downloading, 1, error)));
        Assert.True(model.CanRetry);
        Assert.False(model.CanStart);
        Assert.False(await controller.RetryAsync());
        Assert.False(model.CanRetry);
        Assert.False(model.CanStart);
        Assert.Equal("https://example.invalid/watch?v=redacted", model.VideoUrl);
        Assert.Equal("/safe/destination", model.OutputFolder);
        Assert.Equal("video", model.FileStem);
    }

    [Fact]
    public void User_action_required_failure_does_not_offer_try_again()
    {
        var model = ValidVideoModel();
        var controller = Controller(model);
        model.BeginRun();
        var error = SafeDownloadError.Create(
            DownloadErrorCode.TooLarge,
            DownloadStage.Downloading,
            "The selected stream is too large.",
            RetryAction.UserActionRequired,
            new RedactedDiagnosticToken("diag-test-too-large"));

        Assert.True(controller.ApplyEvent(new DownloadFailed(Run, DownloadStage.Downloading, 1, error)));
        Assert.False(model.CanRetry);
    }

    [Fact]
    public void UniFi_compatibility_is_not_applied_to_audio_requests()
    {
        var model = ValidVideoModel();
        model.SelectedOperation = DownloadOperation.Audio;
        model.MakeUnifiCompatible = true;

        Assert.True(model.TryBuildRequest(out var request));
        Assert.False(request!.Output.UnifiCompatible);
        Assert.Equal(OutputContainer.Matroska, request.Output.Container);
        Assert.False(model.IsVideoOperation);
    }

    [Fact]
    public void Metadata_uses_safe_compatibility_output_and_disables_output_controls()
    {
        var model = ValidVideoModel();
        model.SelectedOperation = DownloadOperation.Metadata;
        model.OutputFolder = string.Empty;
        model.FileStem = string.Empty;

        Assert.True(model.IsFormValid);
        Assert.True(model.TryBuildRequest(out var request));
        Assert.Equal(DownloadOperation.Metadata, request!.Operation);
        Assert.Equal("metadata", request.Output.Directory);
        Assert.False(model.OutputControlsEnabled);
        Assert.False(model.CanChooseOutputFolder);
        Assert.Contains("Not used for metadata", model.OutputControlsHelpText, StringComparison.Ordinal);
        Assert.Contains("Not used for metadata", model.FolderChooserHelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void Frame_rate_selection_maps_exact_targets_and_reset_notifies_the_bound_selector()
    {
        var model = ValidVideoModel();
        var changes = new List<string>();
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName!);

        Assert.Equal(0, model.FrameRateSelectionIndex);
        Assert.Null(model.FrameRateTarget);
        foreach (var expected in new (int Index, double? Target)[] { (1, 24), (2, 25), (3, 30), (0, null) })
        {
            model.FrameRateSelectionIndex = expected.Index;
            Assert.Equal(expected.Target, model.FrameRateTarget);
            Assert.Equal(expected.Index, model.FrameRateSelectionIndex);
        }

        model.FrameRateSelectionIndex = 3;
        model.StartNewRun();
        Assert.Null(model.FrameRateTarget);
        Assert.Equal(0, model.FrameRateSelectionIndex);
        Assert.Contains(nameof(model.FrameRateSelectionIndex), changes);
    }

    [Fact]
    public async Task Start_description_reports_safe_state_specific_disabled_reasons()
    {
        var model = new DownloadViewModel();
        var controller = new PresentationController(new IdleRunner(), new RecordingOpener(), new MissingEnvironment(), model, new InlineDispatcher());
        Assert.Contains("request is invalid", model.StartHelpText, StringComparison.Ordinal);
        model.VideoUrl = "https://example.invalid/video";
        model.OutputFolder = "/safe/destination";
        model.FileStem = "video";
        Assert.Contains("Starts one validated request", model.StartHelpText, StringComparison.Ordinal);
        await controller.TestEnvironmentAsync();
        Assert.Contains("required local capability is missing", model.StartHelpText, StringComparison.Ordinal);
        model.UseBrowserSession = true;
        Assert.Contains("browser-session consent is incomplete", model.StartHelpText, StringComparison.Ordinal);
        model.SelectedBrowser = BrowserKind.Chrome;
        model.BeginRun();
        Assert.Contains("active", model.StartHelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_and_cancel_are_gated_by_form_and_active_run()
    {
        var model = new DownloadViewModel();
        Assert.False(model.CanStart);
        var controller = Controller(model);
        model.VideoUrl = "https://example.invalid/video";
        model.OutputFolder = "/safe/destination";
        model.FileStem = "video";
        Assert.True(model.CanStart);
        model.BeginRun();
        Assert.False(model.CanStart);
        Assert.True(model.CanCancel);
        controller.Cancel();
        Assert.Contains("Cancellation requested", model.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Reducer_projection_rejects_stale_duplicate_and_post_terminal_events()
    {
        var model = ValidVideoModel();
        var controller = Controller(model);
        model.BeginRun();
        Assert.True(controller.ApplyEvent(new DownloadProgress(Run, DownloadStage.Downloading, 1, .2, "safe activity")));
        Assert.False(controller.ApplyEvent(new DownloadProgress(Run, DownloadStage.Downloading, 1, .8, "obsolete")));
        Assert.False(controller.ApplyEvent(new DownloadProgress(OtherRun, DownloadStage.Downloading, 2, .9, "stale")));
        Assert.True(controller.ApplyEvent(new DownloadCancelled(Run, DownloadStage.Downloading, 2)));
        Assert.False(controller.ApplyEvent(new DownloadProgress(Run, DownloadStage.Downloading, 3, .9, "late")));
        Assert.Equal(ScreenState.Cancelled, model.ScreenState);
        Assert.Equal("safe activity", model.ActivityText);
    }

    [Fact]
    public void Invalid_fraction_is_not_clamped_and_nan_is_presented_as_indeterminate()
    {
        var model = ValidVideoModel();
        var controller = Controller(model);
        model.BeginRun();
        Assert.True(controller.ApplyEvent(new DownloadProgress(Run, DownloadStage.Downloading, 1, double.NaN, "working")));
        Assert.Equal(ProgressMode.Indeterminate, model.ProgressMode);
        Assert.Equal(0, model.ProgressFraction);
    }

    [Fact]
    public void Error_activity_and_accessible_names_do_not_echo_sensitive_values()
    {
        var model = ValidVideoModel();
        var controller = Controller(model);
        model.BeginRun();
        Assert.True(controller.ApplyEvent(new DownloadProgress(Run, DownloadStage.Downloading, 1, .1, "token=secret")));
        Assert.DoesNotContain("secret", model.ActivityText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(model.VideoUrl, model.BrowserSessionHelp, StringComparison.Ordinal);
        Assert.DoesNotContain("path", model.BrowserSessionHelp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Native_folder_picker_selection_updates_destination_without_opening_anything_else()
    {
        var model = ValidVideoModel();
        var picker = new RecordingFolderPicker("/safe/selected");
        var controller = new PresentationController(new IdleRunner(), new RecordingOpener(), new EmptyEnvironment(), model, new InlineDispatcher(), picker);

        Assert.True(model.CanChooseOutputFolder);
        Assert.True(await controller.ChooseOutputFolderAsync());
        Assert.Equal("/safe/selected", model.OutputFolder);
        Assert.Equal("/safe/destination", picker.SuggestedFolder);
    }

    [Fact]
    public async Task Folder_picker_is_gated_for_metadata_and_active_runs_with_accessible_reasons()
    {
        var model = ValidVideoModel();
        var picker = new RecordingFolderPicker("/safe/selected");
        var controller = new PresentationController(new IdleRunner(), new RecordingOpener(), new EmptyEnvironment(), model, new InlineDispatcher(), picker);

        model.SelectedOperation = DownloadOperation.Metadata;
        Assert.False(model.CanChooseOutputFolder);
        Assert.Contains("Not used for metadata", model.FolderChooserHelpText, StringComparison.Ordinal);
        Assert.False(await controller.ChooseOutputFolderAsync());

        model.SelectedOperation = DownloadOperation.Video;
        model.BeginRun();
        Assert.False(model.CanChooseOutputFolder);
        Assert.Contains("active", model.FolderChooserHelpText, StringComparison.Ordinal);
        Assert.False(await controller.ChooseOutputFolderAsync());
        Assert.Equal(0, picker.Calls);
    }

    [Fact]
    public async Task Unavailable_or_failed_folder_picker_never_replaces_the_existing_destination()
    {
        var model = ValidVideoModel();
        var picker = new RecordingFolderPicker(null, false, "Native folder picker is unavailable on this platform; type a destination in the field above.");
        var controller = new PresentationController(new IdleRunner(), new RecordingOpener(), new EmptyEnvironment(), model, new InlineDispatcher(), picker);

        Assert.False(model.CanChooseOutputFolder);
        Assert.Contains("unavailable", model.FolderChooserHelpText, StringComparison.OrdinalIgnoreCase);
        Assert.False(await controller.ChooseOutputFolderAsync());
        Assert.Equal("/safe/destination", model.OutputFolder);

        controller.ConfigureOutputFolderPicker(new RecordingFolderPicker(null, true, "Unavailable" ) { Throws = true });
        Assert.False(await controller.ChooseOutputFolderAsync());
        Assert.Equal("/safe/destination", model.OutputFolder);
        Assert.Contains("could not be used safely", model.Announcement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_in_browser_is_disabled_without_a_published_verified_result()
    {
        var model = ValidVideoModel();
        var opener = new RecordingOpener();
        var controller = new PresentationController(new IdleRunner(), opener, new EmptyEnvironment(), model, new InlineDispatcher());
        Assert.False(model.CanOpenInBrowser);
        Assert.False(await controller.OpenInBrowserAsync());
        Assert.Equal(0, opener.Calls);
    }

    [Fact]
    public async Task Published_result_enables_only_the_verified_local_opener()
    {
        var model = ValidVideoModel();
        var observer = new PresentationObserver();
        var service = new DownloadApplicationService(
            new FakeProvider(),
            new FakeStager(),
            new FakeProcessor(),
            new FakePublisher(),
            observer,
            new NoopDiagnostics());
        var opener = new RecordingOpener();
        var controller = new PresentationController(
            new ApplicationServiceRunner(service),
            opener,
            new EmptyEnvironment(),
            model,
            new InlineDispatcher());
        observer.Attach(controller);

        Assert.True(await controller.StartAsync());
        Assert.True(model.CanOpenInBrowser);
        Assert.True(await controller.OpenInBrowserAsync());
        Assert.Equal(1, opener.Calls);
    }

    private sealed class FakeProvider : IVideoProvider
    {
        public ValueTask<ProviderResult<MetadataSnapshot>> ReadMetadataAsync(VideoReference video, BrowserSessionSelection? browserSession, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProviderResult<MetadataSnapshot>(new MetadataSnapshot("safe title", null, null, null), null));

        public ValueTask<ProviderResult<MediaPlan>> ResolveMediaAsync(DownloadRequest request, MetadataSnapshot metadata, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProviderResult<MediaPlan>(new MediaPlan(
                request,
                new MediaCharacteristics(OutputContainer.Mp4, VideoCodec.H264, AudioCodec.Unknown, true, false, 30),
                "video-format",
                null,
                1), null));
    }

    private sealed class FakeStager : ILocalStreamStager
    {
        public ValueTask<ProviderResult<LocalMediaInputs>> StageAsync(MediaPlan plan, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProviderResult<LocalMediaInputs>(new LocalMediaInputs(
                new LocalMediaInputHandle("video-handle", LocalMediaChannel.Video, 1, true), null), null));

        public ValueTask<ProviderResult<StageReleaseResult>> ReleaseAsync(LocalMediaInputs inputs, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProviderResult<StageReleaseResult>(new StageReleaseResult(1, true), null));
    }

    private sealed class FakeProcessor : IStagedMediaProcessor
    {
        public ValueTask<ProviderResult<MediaProcessingResult>> ProcessAsync(MediaPlan plan, LocalMediaInputs inputs, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProviderResult<MediaProcessingResult>(new MediaProcessingResult(
                new StagedArtifact("stage-key", "video.mp4", OutputContainer.Mp4, 1, true), true), null));
    }

    private sealed class FakePublisher : IPublicationStore
    {
        public ValueTask<ProviderResult<VerifiedLocalMp4>> PublishAsync(StagedArtifact artifact, OutputOptions output, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProviderResult<VerifiedLocalMp4>(new VerifiedLocalMp4("video.mp4", "output-key", 1), null));
    }

    private sealed class NoopDiagnostics : IDiagnostics
    {
        public void Report(SafeDownloadError downloadError)
        {
        }
    }

    private static DownloadViewModel ValidVideoModel()
    {
        var model = new DownloadViewModel
        {
            VideoUrl = "https://example.invalid/watch?v=redacted",
            OutputFolder = "/safe/destination",
            FileStem = "video"
        };
        return model;
    }

    private static PresentationController Controller(DownloadViewModel model) =>
        new(new IdleRunner(), new RecordingOpener(), new EmptyEnvironment(), model, new InlineDispatcher());

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }

    private sealed class EmptyEnvironment : IEnvironmentProbe
    {
        public ValueTask<IReadOnlyList<CapabilityStatus>> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<CapabilityStatus>>([]);
    }

    private sealed class MissingEnvironment : IEnvironmentProbe
    {
        public ValueTask<IReadOnlyList<CapabilityStatus>> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<CapabilityStatus>>([new CapabilityStatus("Downloader", "Missing", "Not configured.")]);
    }

    private sealed class IdleRunner : IDownloadRunner
    {
        public ValueTask<DownloadRunResult> RunAsync(DownloadRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The deterministic presentation tests never run an external operation.");
    }

    private sealed class RecordingFolderPicker : IOutputFolderPicker
    {
        private readonly string? selectedFolder;
        public RecordingFolderPicker(string? selectedFolder, bool isAvailable = true, string unavailableReason = "Unavailable")
        {
            this.selectedFolder = selectedFolder;
            IsAvailable = isAvailable;
            UnavailableReason = unavailableReason;
        }

        public bool IsAvailable { get; }
        public string UnavailableReason { get; }
        public string? SuggestedFolder { get; private set; }
        public int Calls { get; private set; }
        public bool Throws { get; init; }

        public ValueTask<string?> PickAsync(string? currentFolder, CancellationToken cancellationToken)
        {
            Calls++;
            SuggestedFolder = currentFolder;
            if (Throws) throw new InvalidOperationException("picker failure");
            return ValueTask.FromResult(selectedFolder);
        }
    }

    private sealed class RecordingOpener : ILocalFileOpener
    {
        public int Calls { get; private set; }
        public ValueTask<OpenResult> OpenAsync(VerifiedLocalMp4 file, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new OpenResult(true, null));
        }
    }
}
