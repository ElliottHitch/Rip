using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnifiDownloader.Application;
using UnifiDownloader.Domain;

namespace UnifiDownloader.App.Presentation;

public enum ScreenState
{
    Idle,
    Validating,
    Resolving,
    Downloading,
    Processing,
    Publishing,
    Completed,
    Cancelled,
    Failed
}

public enum ProgressMode
{
    None,
    Determinate,
    Indeterminate
}

public sealed record ActivityEntry(DownloadStage Stage, string Text);

public sealed record CapabilityStatus(string Category, string Status, string Description);

/// <summary>Safe, bindable presentation state. It contains no adapter handles or locations.</summary>
public sealed class DownloadViewModel : INotifyPropertyChanged
{
    private const int MaximumActivityEntries = 50;
    private const int MaximumActivityCharacters = 160;
    private string videoUrl = string.Empty;
    private DownloadOperation selectedOperation = DownloadOperation.Video;
    private string outputFolder = string.Empty;
    private string fileStem = "download";
    private OutputContainer selectedContainer = OutputContainer.Matroska;
    private double? frameRateTarget;
    private bool makeUnifiCompatible;
    private bool useBrowserSession;
    private BrowserKind? selectedBrowser;
    private bool isBusy;
    private ScreenState screenState = ScreenState.Idle;
    private ProgressMode progressMode = ProgressMode.None;
    private double progressFraction;
    private string stageText = "Ready";
    private string activityText = string.Empty;
    private string statusText = "Ready";
    private string validationMessage = string.Empty;
    private string errorMessage = string.Empty;
    private string metadataSummary = string.Empty;
    private string completionText = string.Empty;
    private string announcement = string.Empty;
    private VerifiedLocalMp4? publishedFile;
    private bool canOpenFolder;
    private string openFolderDisabledReason = "Native folder picking is unavailable until the window is ready; type a destination in the field above.";
    private readonly string browserSessionHelp = "Browser session access is optional and off by default. If enabled, the selected browser session is used only for this run. The app never asks for a password, exports cookies, automates login or CAPTCHA, uploads browser data, uses a remote browser bridge, rotates proxies, spoofs fingerprints or headers, or bypasses service restrictions.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public DownloadViewModel() => canOpenFolder = false;

    public string VideoUrl { get => videoUrl; set => Set(ref videoUrl, value ?? string.Empty); }
    public DownloadOperation SelectedOperation { get => selectedOperation; set { if (Set(ref selectedOperation, value)) NotifyFormChanged(); } }
    public string OutputFolder { get => outputFolder; set { if (Set(ref outputFolder, value ?? string.Empty)) NotifyFormChanged(); } }
    public string FileStem { get => fileStem; set { if (Set(ref fileStem, value ?? string.Empty)) NotifyFormChanged(); } }
    public OutputContainer SelectedContainer { get => selectedContainer; set { if (Set(ref selectedContainer, value)) NotifyFormChanged(); } }
    public double? FrameRateTarget { get => frameRateTarget; set { if (Set(ref frameRateTarget, value)) NotifyFormChanged(); } }
    /// <summary>The only user-facing output policy switch; it is deliberately off by default.</summary>
    public bool MakeUnifiCompatible { get => makeUnifiCompatible; set { if (Set(ref makeUnifiCompatible, value)) NotifyFormChanged(); } }
    public bool UseBrowserSession { get => useBrowserSession; set { if (Set(ref useBrowserSession, value)) { if (!value) SelectedBrowser = null; NotifyFormChanged(); } } }
    public BrowserKind? SelectedBrowser { get => selectedBrowser; set { if (Set(ref selectedBrowser, value)) NotifyFormChanged(); } }

    public IReadOnlyList<DownloadOperation> Operations { get; } = Enum.GetValues<DownloadOperation>();
    public IReadOnlyList<OutputContainer> Containers { get; } = Enum.GetValues<OutputContainer>();
    public IReadOnlyList<double?> FrameRateTargets { get; } = new double?[] { null, 24, 25, 30 };
    public IReadOnlyList<BrowserKind> SupportedBrowsers { get; } = Enum.GetValues<BrowserKind>();
    public ObservableCollection<ActivityEntry> ActivityLog { get; } = [];
    public ObservableCollection<CapabilityStatus> Capabilities { get; } = [];

    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) Notify(nameof(CanStart), nameof(CanCancel)); } }
    public ScreenState ScreenState { get => screenState; private set => Set(ref screenState, value); }
    public ProgressMode ProgressMode { get => progressMode; private set { if (Set(ref progressMode, value)) Notify(nameof(IsProgressIndeterminate), nameof(HasProgress)); } }
    public double ProgressFraction { get => progressFraction; private set => Set(ref progressFraction, value); }
    public bool IsProgressIndeterminate => ProgressMode == ProgressMode.Indeterminate;
    public bool HasProgress => ProgressMode != ProgressMode.None;
    public string StageText { get => stageText; private set => Set(ref stageText, value); }
    public string ActivityText { get => activityText; private set => Set(ref activityText, value); }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public string ValidationMessage { get => validationMessage; private set => Set(ref validationMessage, value); }
    public string ErrorMessage { get => errorMessage; private set => Set(ref errorMessage, value); }
    public string MetadataSummary { get => metadataSummary; private set => Set(ref metadataSummary, value); }
    public string CompletionText { get => completionText; private set => Set(ref completionText, value); }
    public string Announcement { get => announcement; private set => Set(ref announcement, value); }
    public VerifiedLocalMp4? PublishedFile { get => publishedFile; private set => Set(ref publishedFile, value); }

    public bool IsTerminal => ScreenState is ScreenState.Completed or ScreenState.Cancelled or ScreenState.Failed;
    public bool CanStart => !IsBusy && !IsTerminal && !HasMissingCapability && IsFormValid;
    public bool CanCancel => IsBusy;
    public bool OutputControlsEnabled => SelectedOperation != DownloadOperation.Metadata && !IsBusy;
    public bool BrowserSelectionEnabled => UseBrowserSession && !IsBusy;
    public bool CanOpenInBrowser => IsTerminal && PublishedFile is not null && ScreenState == ScreenState.Completed;
    public bool CanOpenFolder => canOpenFolder;
    public bool CanChooseOutputFolder => canOpenFolder && OutputControlsEnabled;
    public string OpenFolderDisabledReason => openFolderDisabledReason;
    public string BrowserSessionHelp => browserSessionHelp;
    public bool HasMissingCapability => Capabilities.Any(status =>
        status.Status is "Missing" or "Unavailable");
    public int FrameRateSelectionIndex
    {
        get => FrameRateTarget switch
        {
            24 => 1,
            25 => 2,
            30 => 3,
            _ => 0
        };
        set => FrameRateTarget = value switch
        {
            0 => null,
            1 => 24,
            2 => 25,
            3 => 30,
            _ => FrameRateTarget
        };
    }

    public string OutputControlsHelpText => SelectedOperation == DownloadOperation.Metadata
        ? "Not used for metadata; output controls are unavailable."
        : IsBusy ? "Unavailable while a run is active."
        : "Output destination and format settings for the selected download.";

    public string FrameRateHelpText => $"{OutputControlsHelpText} Preserve source, or convert to 24, 25, or 30 FPS.";

    public string FolderChooserHelpText => SelectedOperation == DownloadOperation.Metadata
        ? "Not used for metadata; folder selection is unavailable."
        : IsBusy
            ? "Unavailable while a run is active."
            : CanOpenFolder
                ? "Opens the platform-native folder picker."
                : OpenFolderDisabledReason;

    public string FolderPickerFallbackText => SelectedOperation == DownloadOperation.Metadata
        ? "Folder selection is not used for metadata."
        : IsBusy
            ? "Folder selection is unavailable while a run is active."
            : CanOpenFolder
                ? "Choose a folder, or edit the selected destination in the field above."
                : "The native folder picker is unavailable; type a destination in the field above.";

    public string StartHelpText
    {
        get
        {
            if (IsBusy) return "Unavailable while a run is active.";
            if (IsTerminal) return "Unavailable until Start New Run resets the terminal run.";
            if (!Uri.TryCreate(VideoUrl, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            {
                return "Unavailable because the request is invalid. Enter one valid HTTP(S) video address.";
            }

            if (UseBrowserSession && SelectedBrowser is null)
            {
                return "Unavailable because browser-session consent is incomplete. Choose a supported browser or turn consent off.";
            }

            if (HasMissingCapability)
            {
                return "Unavailable because a required local capability is missing. Run Test Environment after correcting it.";
            }

            if (!IsFormValid) return "Unavailable because required request fields are incomplete.";
            return "Starts one validated request.";
        }
    }

    public bool IsFormValid
    {
        get
        {
            if (!Uri.TryCreate(VideoUrl, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")) return false;
            if (!Enum.IsDefined(SelectedOperation) || !Enum.IsDefined(SelectedContainer)) return false;
            if (FrameRateTarget is { } target && target is not (24 or 25 or 30)) return false;
            if (UseBrowserSession && SelectedBrowser is null) return false;
            return SelectedOperation == DownloadOperation.Metadata ||
                !string.IsNullOrWhiteSpace(OutputFolder) && !string.IsNullOrWhiteSpace(FileStem);
        }
    }

    public bool TryBuildRequest(out DownloadRequest? request)
    {
        request = null;
        if (!IsFormValid || !Uri.TryCreate(VideoUrl, UriKind.Absolute, out var address))
        {
            ValidationMessage = "Enter one valid HTTP(S) video URL and complete the required fields.";
            return false;
        }

        try
        {
            var output = SelectedOperation == DownloadOperation.Metadata
                ? new OutputOptions("metadata", "metadata", OutputContainer.Mp4)
                : new OutputOptions(
                    OutputFolder.Trim(),
                    FileStem.Trim(),
                    MakeUnifiCompatible ? OutputContainer.UnifiMp4 : SelectedContainer,
                    false,
                    FrameRateTarget,
                    MakeUnifiCompatible);
            request = new DownloadRequest(
                new VideoReference(address),
                SelectedOperation,
                output,
                UseBrowserSession && SelectedBrowser is { } browser ? BrowserSessionSelection.Create(browser) : null);
            ValidationMessage = string.Empty;
            return true;
        }
        catch (ArgumentException)
        {
            ValidationMessage = "The request could not be validated. Check the fields and try again.";
            return false;
        }
    }

    public void BeginRun()
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        CompletionText = string.Empty;
        MetadataSummary = string.Empty;
        PublishedFile = null;
        ActivityLog.Clear();
        ScreenState = ScreenState.Validating;
        ProgressMode = ProgressMode.None;
        StageText = "Validating";
        ActivityText = string.Empty;
        StatusText = "Request accepted";
        Announcement = $"Request accepted for {SelectedOperation}.";
        NotifyFormChanged();
    }

    internal void ApplyEvent(DownloadEvent downloadEvent)
    {
        if (downloadEvent is DownloadProgress progress)
        {
            ScreenState = MapStage(progress.Stage);
            StageText = StageLabel(progress.Stage);
            ActivityText = SafeText(progress.Activity, "Working");
            ProgressMode = double.IsFinite(progress.Fraction) ? ProgressMode.Determinate : ProgressMode.Indeterminate;
            ProgressFraction = double.IsFinite(progress.Fraction) ? progress.Fraction : 0;
            StatusText = ActivityText;
            AddActivity(progress.Stage, ActivityText);
            Announcement = StageText;
            NotifyFormChanged();
            return;
        }

        if (downloadEvent is DownloadMetadataCompleted metadataCompleted)
        {
            MetadataSummary = FormatMetadata(metadataCompleted.Metadata);
            CompleteTerminal(ScreenState.Completed, "Metadata ready", "Metadata was resolved. No output was published.");
        }
        else if (downloadEvent is DownloadCompleted)
        {
            CompleteTerminal(ScreenState.Completed, "Completed", "The verified output was published.");
        }
        else if (downloadEvent is DownloadCancelled)
        {
            CompleteTerminal(ScreenState.Cancelled, "Cancelled", "Cancelled. No published output was reported.");
        }
        else if (downloadEvent is DownloadFailed failed)
        {
            ErrorMessage = SafeText(failed.Error.UserMessage, "The operation could not be completed.");
            CompleteTerminal(ScreenState.Failed, "Failed", ErrorMessage);
        }
    }

    internal void ApplyResult(DownloadRunResult result)
    {
        if (result.IsPublished && result.PublishedFile is not null)
        {
            PublishedFile = result.PublishedFile;
            CompletionText = $"Published {SafeText(result.PublishedFile.FileName, "download.mp4")}.";
            StatusText = result.CleanupComplete ? "Completed" : "Completed with cleanup warning";
            Announcement = StatusText;
            Notify(nameof(CanOpenInBrowser));
        }
        else if (result.IsMetadataSuccess && result.Metadata is not null)
        {
            MetadataSummary = FormatMetadata(result.Metadata);
        }

        if (result.Error is not null && string.IsNullOrWhiteSpace(ErrorMessage))
        {
            ErrorMessage = SafeText(result.Error.UserMessage, "The operation could not be completed.");
        }

        if (IsTerminal)
        {
            UseBrowserSession = false;
            SelectedBrowser = null;
            IsBusy = false;
            NotifyFormChanged();
        }
    }

    internal void RequestCancellation()
    {
        if (!IsBusy) return;
        StatusText = "Cancellation requested";
        Announcement = StatusText;
        Notify(nameof(StatusText), nameof(Announcement));
    }

    internal void ApplyControllerFailure()
    {
        ErrorMessage = "The operation could not be completed. Check the environment and try again.";
        CompleteTerminal(ScreenState.Failed, "Failed", ErrorMessage);
    }

    internal void SetCapabilities(IEnumerable<CapabilityStatus> statuses)
    {
        Capabilities.Clear();
        foreach (var status in statuses) Capabilities.Add(status);
        StatusText = "Environment checked";
        Announcement = StatusText;
        NotifyFormChanged();
    }

    internal void SetOutputFolderPickerAvailability(bool available, string? unavailableReason = null)
    {
        canOpenFolder = available;
        openFolderDisabledReason = string.IsNullOrWhiteSpace(unavailableReason)
            ? "Native folder picking is unavailable on this platform; type a destination in the field above."
            : SafeText(unavailableReason, "Native folder picking is unavailable; type a destination in the field above.");
        Notify(nameof(CanOpenFolder), nameof(CanChooseOutputFolder), nameof(FolderChooserHelpText), nameof(FolderPickerFallbackText), nameof(OpenFolderDisabledReason));
    }

    internal void ReportFolderPickerFailure()
    {
        StatusText = "Output folder was not changed";
        Announcement = "The native folder picker could not be used safely. Type a destination in the field above.";
        Notify(nameof(StatusText), nameof(Announcement));
    }
    internal void SetOpenResult(OpenResult result)
    {
        StatusText = result.Opened ? "Opened verified local MP4" : "The verified local MP4 could not be opened by the operating system.";
        Announcement = StatusText;
        Notify(nameof(StatusText), nameof(Announcement));
    }

    public void StartNewRun()
    {
        VideoUrl = string.Empty;
        OutputFolder = string.Empty;
        FileStem = "download";
        SelectedOperation = DownloadOperation.Video;
        SelectedContainer = OutputContainer.Matroska;
        FrameRateTarget = null;
        MakeUnifiCompatible = false;
        UseBrowserSession = false;
        SelectedBrowser = null;
        IsBusy = false;
        ScreenState = ScreenState.Idle;
        ProgressMode = ProgressMode.None;
        ProgressFraction = 0;
        StageText = "Ready";
        ActivityText = string.Empty;
        StatusText = "Ready";
        ValidationMessage = string.Empty;
        ErrorMessage = string.Empty;
        MetadataSummary = string.Empty;
        CompletionText = string.Empty;
        Announcement = string.Empty;
        PublishedFile = null;
        ActivityLog.Clear();
        NotifyFormChanged();
        Notify(nameof(IsTerminal), nameof(OutputControlsEnabled), nameof(BrowserSelectionEnabled), nameof(CanOpenInBrowser), nameof(CanChooseOutputFolder), nameof(FrameRateSelectionIndex));
    }

    private void CompleteTerminal(ScreenState state, string status, string announcement)
    {
        IsBusy = false;
        ScreenState = state;
        ProgressMode = ProgressMode.None;
        ProgressFraction = 0;
        StageText = status;
        StatusText = status;
        Announcement = announcement;
        UseBrowserSession = false;
        SelectedBrowser = null;
        NotifyFormChanged();
        Notify(nameof(IsTerminal), nameof(OutputControlsEnabled), nameof(BrowserSelectionEnabled), nameof(CanOpenInBrowser), nameof(CanChooseOutputFolder));
    }

    private void AddActivity(DownloadStage stage, string text)
    {
        ActivityLog.Add(new ActivityEntry(stage, text));
        while (ActivityLog.Count > MaximumActivityEntries) ActivityLog.RemoveAt(0);
    }

    private static ScreenState MapStage(DownloadStage stage) => stage switch
    {
        DownloadStage.Validating => ScreenState.Validating,
        DownloadStage.Metadata or DownloadStage.Resolving => ScreenState.Resolving,
        DownloadStage.Downloading => ScreenState.Downloading,
        DownloadStage.Processing => ScreenState.Processing,
        DownloadStage.Publishing or DownloadStage.Opening => ScreenState.Publishing,
        _ => ScreenState.Resolving
    };

    private static string StageLabel(DownloadStage stage) => stage switch
    {
        DownloadStage.Validating => "Validating",
        DownloadStage.Metadata or DownloadStage.Resolving => "Resolving",
        DownloadStage.Downloading => "Downloading",
        DownloadStage.Processing => "Processing",
        DownloadStage.Publishing or DownloadStage.Opening => "Publishing",
        _ => "Working"
    };

    private static string FormatMetadata(MetadataSnapshot metadata)
    {
        var duration = metadata.Duration is { } value ? $" ({value:g})" : string.Empty;
        return $"{SafeText(metadata.Title, "Untitled")}{duration}";
    }

    private static string SafeText(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var safe = ErrorRedactor.Redact(value);
        return safe.Length <= MaximumActivityCharacters ? safe : safe[..MaximumActivityCharacters];
    }

    private void NotifyFormChanged() => Notify(
        nameof(IsFormValid), nameof(CanStart), nameof(OutputControlsEnabled), nameof(BrowserSelectionEnabled),
        nameof(FrameRateSelectionIndex), nameof(OutputControlsHelpText), nameof(FrameRateHelpText),
        nameof(FolderChooserHelpText), nameof(FolderPickerFallbackText), nameof(CanChooseOutputFolder), nameof(StartHelpText));
    private void Notify(params string[] names) { foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
