using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace EnviousWispr.App;

/// <summary>
/// One card in a choice list. Carries its own selection, because the list that shows it does not.
/// </summary>
/// <remarks>
/// The choice lists were <c>RadioButtons</c> controls and are now plain <c>ItemsControl</c>s. That
/// change was forced by layout: RadioButtons arranges each item at the item's own desired size and
/// never consults the item's alignment, so six provider cards rendered at six different widths
/// tracking six description lengths. Three attempts to make the container hand its width down -
/// stretching the control, stretching the items, a minimum width bound to the list - all failed,
/// the last one inertly, producing byte-identical measurements across two builds.
///
/// An ItemsControl over a StackPanel gives each item the panel's full width, which is all that was
/// ever wanted. The items are still RadioButtons, so the card style and its template are untouched.
/// What an ItemsControl does not have is a selection, so selection moved here, onto the data, where
/// two-way binding keeps it and the card in step without anyone reaching into a container.
/// </remarks>
public sealed class SelectableChoiceOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public SelectableChoiceOption(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string Description { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public override string ToString() => Name;
}

public sealed partial class MainWindow : Window, IDisposable
{
    private const int WindowFrameInsetCount = 3;

    /// <summary>
    /// The index of the chosen card, or 0 when nothing is chosen yet.
    /// </summary>
    /// <remarks>
    /// Falls back to 0 rather than -1 deliberately: every caller feeds this straight into an enum
    /// or a clamp, and a -1 would either throw or quietly become a different setting. The first
    /// option in each of these lists is the safe default - Automatic, None, System, Top.
    /// </remarks>
    private static int SelectedIndexOf(SelectableChoiceOption[] choices)
    {
        var index = Array.FindIndex(choices, choice => choice.IsSelected);
        return index >= 0 ? index : 0;
    }

    private static void SelectChoice(SelectableChoiceOption[] choices, int index)
    {
        for (var i = 0; i < choices.Length; i++)
        {
            choices[i].IsSelected = i == index;
        }
    }

    private static readonly SelectableChoiceOption[] FinalEngineChoices =
    [
        new("Automatic", "Chooses the best available local engine for this PC."),
        new("Parakeet", "Fast local English transcription with automatic hardware selection."),
        new("Whisper", "Local multilingual transcription using your chosen language."),
    ];

    private static readonly SelectableChoiceOption[] PolishProviderChoices =
    [
        new("None", "Leaves the deterministic transcript unchanged."),
        new("EG-1", "Polishes locally with the bundled Envious Grammar model."),
        new("Ollama", "Polishes locally with an Ollama model on this PC."),
        new("OpenAI", "Sends transcript text directly to your OpenAI account."),
        new("Anthropic", "Sends transcript text directly to your Anthropic account."),
        new("Gemini", "Sends transcript text directly to your Google Gemini account."),
    ];

    private static readonly SelectableChoiceOption[] ThemeChoices =
    [
        new("Use Windows setting", "Follows the current Windows light or dark setting."),
        new("Light", "Uses the light EnviousWispr palette."),
        new("Dark", "Uses the dark EnviousWispr palette."),
    ];

    private static readonly SelectableChoiceOption[] OverlayPositionChoices =
    [
        new("Top", "Shows the recording pill at the top of the active display."),
        new("Bottom", "Shows the recording pill at the bottom of the active display."),
    ];

    private readonly ISettingsStore _settingsStore;
    private readonly IPortableProfileService _profileService;
    private readonly IHistoryStore _historyStore;
    private readonly IApiKeyStore _apiKeyStore;
    private readonly CloudPolishModelCatalog _cloudModelCatalog;
    private readonly IRecoveryTextStore _recoveryTextStore;
    private readonly IDiagnosticExportService _diagnosticExportService;
    private readonly bool _telemetryAvailable;
    private readonly DictationOverlayWindow _overlayWindow;
    private readonly RecordingSoundCuePlayer _recordingSoundPlayer = new();
    private readonly RecordingSoundCueCoordinator _recordingSoundCoordinator;
    private readonly List<HistoryItemViewModel> _history = [];
    private IReadOnlyList<MicrophoneChoice> _microphones = [];
    private WasapiDeviceCatalog? _deviceCatalog;
    private AppSettings _settings;
    private HistoryLoadStatus _historyLoadStatus = HistoryLoadStatus.Missing;
    private bool _isHistoryLoading = true;
    private bool _isApplyingSettings;
    private bool _initialFocusAssigned;
    private int _polishModelDiscoveryVersion;
    private DictationOverlayState _currentOverlayState = DictationOverlayState.Hidden;
    private CancellationTokenSource? _soundPreviewCancellation;

    public MainWindow(
        AppSettings settings,
        SettingsLoadStatus settingsLoadStatus,
        ISettingsStore settingsStore,
        IPortableProfileService profileService,
        IHistoryStore historyStore,
        IApiKeyStore apiKeyStore,
        IRecoveryTextStore recoveryTextStore,
        IDiagnosticExportService diagnosticExportService,
        bool telemetryAvailable,
        ReleaseIdentity releaseIdentity,
        bool updateConfigured,
        string currentVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(profileService);
        ArgumentNullException.ThrowIfNull(historyStore);
        ArgumentNullException.ThrowIfNull(apiKeyStore);
        ArgumentNullException.ThrowIfNull(recoveryTextStore);
        ArgumentNullException.ThrowIfNull(diagnosticExportService);
        ArgumentNullException.ThrowIfNull(releaseIdentity);

        _settings = settings;
        _settingsStore = settingsStore;
        _profileService = profileService;
        _historyStore = historyStore;
        _apiKeyStore = apiKeyStore;
        _cloudModelCatalog = new CloudPolishModelCatalog(apiKeyStore);
        _recoveryTextStore = recoveryTextStore;
        _diagnosticExportService = diagnosticExportService;
        _telemetryAvailable = telemetryAvailable;

        InitializeComponent();
        EngineComboBox.ItemsSource = FinalEngineChoices;
        PolishProviderComboBox.ItemsSource = PolishProviderChoices;
        ThemeComboBox.ItemsSource = ThemeChoices;
        OverlayPositionComboBox.ItemsSource = OverlayPositionChoices;
        _recordingSoundCoordinator = new RecordingSoundCueCoordinator(
            _recordingSoundPlayer.Play);
        RecordingSoundComboBox.ItemsSource = RecordingSoundCatalog.Choices;
        _overlayWindow = new DictationOverlayWindow();
        _overlayWindow.ApplyPreferences(
            settings.Preferences.OverlayPosition,
            settings.Preferences.LivePreviewEnabled,
            settings.Preferences.PillDesignWithoutWords,
            settings.Preferences.PillDesignWithWords);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureMinimumWindowWidth();
        ResizeToDefault();
        AppWindow.SetIcon(Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Brand",
            "EnviousWispr.ico"));
        Activated += OnWindowActivated;

        ApplyTheme(settings.Preferences.Theme);
        ApplySettingsToControls();
        ShowOnboarding(!settings.HasCompletedOnboarding);
        ProductNavigation.SelectedItem = HomeNavItem;
        BuildInfoText.Text = $"{releaseIdentity.DisplayName} {Assembly.GetExecutingAssembly().GetName().Version} · {releaseIdentity.ChannelName}";
        WhatsNewBuildInfoText.Text = BuildInfoText.Text;
        UpdateStatusText.Text = updateConfigured
            ? $"Installed {releaseIdentity.ChannelName} version {currentVersion}. Updates are downloaded only while dictation is idle and must pass SHA-256 plus Envious Labs publisher verification before apply."
            : $"This {releaseIdentity.ChannelName} build has no update endpoint configured. It will not contact an update server.";
        CheckForUpdatesButton.IsEnabled = updateConfigured;
        if (settingsLoadStatus is SettingsLoadStatus.Invalid or SettingsLoadStatus.Migrated)
        {
            FoundationInfoBar.Message += " Previous settings were recovered safely.";
        }
    }

    public event Action<AppSettings>? SettingsChanged;

    public event Action<string>? SessionStatusChanged;

    public event Action<AudioDeviceChange>? AudioDevicesChanged;

    public event Action? RecoveryCleared;

    public event Action<bool, int>? DiagnosticsExportCompleted;

    public event Action? UpdateCheckRequested;

    public event Action? UpdateApplyRequested;

    /// <summary>
    /// The window's opening size, in the same units the rest of the layout is expressed in.
    /// </summary>
    /// <remarks>
    /// <c>AppWindow.Resize</c> takes PHYSICAL pixels, while every size in the XAML - the sidebar,
    /// the frame inset, the content measure, the minimum width - is in device-independent units.
    /// Passing a DIP figure straight to Resize therefore shrinks the window by the display's
    /// scale factor, and does it silently: it looks right on the machine it was written on and
    /// gets worse the higher the user's scaling.
    ///
    /// Measured on a 150% display: the intended 1120x760 opened at 747x507 effective, small
    /// enough that the navigation list clipped on first run and a user had to scroll to reach
    /// half the app. At 200% the same call would open the window NARROWER than the minimum width
    /// this class enforces a few lines above, so the app would fight its own floor.
    /// </remarks>
    private void ResizeToDefault()
    {
        const int preferredWidthDips = 1120;

        // Tall enough to show the whole navigation list without scrolling. MEASURED on the
        // running app, not derived from the row arithmetic: the list is 832 DIP at this density
        // and the chrome above and below it - title bar, brand header, pinned footer, padding -
        // takes a further 219 that the list can never use. 1051 is the sum; the rest is slack.
        //
        // Every previous value here came from multiplying a row count by a row height, and every
        // one of them was wrong - 754 against a real 896, then 731 against a real 832. The row
        // arithmetic consistently under-counts because it does not know what the group headings
        // cost, and the headings are the expensive part.
        const int preferredHeightDips = 1060;

        var scale = DisplayScale();
        var width = preferredWidthDips * scale;
        var height = preferredHeightDips * scale;

        // Never open larger than the screen. A settings window that wants more height than the
        // display has is not a window the user can use, and on a 1080p laptop the preferred
        // height exceeds the work area outright. Clamping is what makes one preferred size safe
        // everywhere instead of correct on the machine it was chosen on - the same class of
        // mistake as writing the size in the wrong units, one level up.
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        if (workArea is { } area && area.Width > 0 && area.Height > 0)
        {
            const double screenMargin = 0.94;
            width = Math.Min(width, area.Width * screenMargin);
            height = Math.Min(height, area.Height * screenMargin);
        }

        AppWindow.Resize(new SizeInt32((int)Math.Round(width), (int)Math.Round(height)));
    }

    /// <summary>
    /// Physical pixels per layout unit for the display this window is on.
    /// </summary>
    /// <remarks>
    /// One function, because both callers convert the same layout units into the same window
    /// units and two answers to that would drift apart. An unreadable DPI falls back to 1.0
    /// rather than to a guess: that is the value the size constants are already written in, so
    /// the fallback degrades to the pre-scaling behaviour instead of inventing a new one.
    /// </remarks>
    private double DisplayScale()
    {
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void ConfigureMinimumWindowWidth()
    {
        var frameInset = ((Thickness)Application.Current.Resources["BrandWindowFrameInset"]).Left;
        var contentCardMinimumWidth = (double)Application.Current.Resources["BrandContentCardMinimumWidth"];

        // NavigationView's content margin is left-only. Build it from the same frame token
        // before the control template is applied so all three visible gutters stay in sync.
        ProductNavigation.Resources["NavigationViewContentMargin"] = new Thickness(
            frameInset,
            default,
            default,
            default);

        // Microsoft.UI.Xaml.Window.MinWidth does not exist in the Windows App SDK this project
        // builds against, so the minimum is set on the window's presenter instead. The
        // presenter is only an OverlappedPresenter for a normal window; if the app is ever
        // shown full-screen or compact-overlay there is nothing to constrain and nothing to do.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Scaled for the same reason ResizeToDefault is: the three inputs are layout units
            // and the presenter's floor is in physical pixels. Unscaled, the minimum shrinks by
            // the display's scale factor - on a 150% display the window could be dragged to two
            // thirds of the width the frame arithmetic says it needs, which is precisely the
            // case this floor exists to prevent.
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(
                (ProductNavigation.OpenPaneLength
                    + (WindowFrameInsetCount * frameInset)
                    + contentCardMinimumWidth)
                * DisplayScale());
        }
    }

    public AppSettings CurrentSettings => _settings;

    public void FocusInitialControl()
    {
        if (_settings.HasCompletedOnboarding)
        {
            HomeNavItem.Focus(FocusState.Programmatic);
        }
        else
        {
            FinishOnboardingButton.Focus(FocusState.Programmatic);
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialFocusAssigned || args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        _initialFocusAssigned = true;
        DispatcherQueue.TryEnqueue(FocusInitialControl);
    }

    public async Task InitializeProductDataAsync()
    {
        await LoadMicrophonesAsync().ConfigureAwait(true);
        await ReloadHistoryAsync().ConfigureAwait(true);
    }

    public void SetHotkeyReady(
        string gesture,
        DictationRecordingMode recordingMode,
        string cancelGesture,
        string quickAddGesture)
    {
        var instruction = recordingMode == DictationRecordingMode.PushToTalk
            ? $"Hold {gesture} while speaking; release to finish."
            : $"Press {gesture} to start; press it again to finish.";
        HotkeyStatusText.Text = recordingMode == DictationRecordingMode.PushToTalk
            ? $"Hold {gesture}"
            : $"Toggle with {gesture}";
        OnboardingHotkeyText.Text = $"{instruction} Cancel with {cancelGesture}. Add a selected word with {quickAddGesture}.";
        SessionStatusText.Text = "Idle";
        SessionStatusChanged?.Invoke($"ready · {gesture}");
    }

    public void SetHotkeyUnavailable(string status)
    {
        HotkeyStatusText.Text = status;
        OnboardingHotkeyText.Text = status;
        SessionStatusText.Text = "Unavailable";
        SessionStatusChanged?.Invoke("shortcut unavailable");
    }

    public void SetSessionStatus(string status)
    {
        SessionStatusText.Text = status;
        SessionStatusChanged?.Invoke(status);
        var overlayState = OverlayStateFor(status);
        HandleRecordingSoundTransition(overlayState);
        _currentOverlayState = overlayState;
        PreviewRecordingSoundButton.IsEnabled = overlayState != DictationOverlayState.Recording;
        _overlayWindow.ShowState(overlayState, status);
        if (status.Contains("ready", StringComparison.OrdinalIgnoreCase))
        {
            EngineReadinessText.Text = status;
            OnboardingModelText.Text = status;
        }
        else if (status.Contains("model is not installed", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("transcription is unavailable", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("worker could not start", StringComparison.OrdinalIgnoreCase))
        {
            EngineReadinessText.Text = status;
            OnboardingModelText.Text = status;
        }
    }

    private void HandleRecordingSoundTransition(DictationOverlayState nextState)
    {
        _recordingSoundCoordinator.Handle(
            isRecording: nextState == DictationOverlayState.Recording,
            _settings.Preferences.PlayRecordingSounds,
            _settings.Preferences.RecordingSoundPairing);
    }

    public void SetUpdateCheckInProgress()
    {
        CheckForUpdatesButton.IsEnabled = false;
        ApplyUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking the isolated signed update channel and staging any newer version…";
    }

    public void SetUpdateStatus(UpdateOperationResult result)
    {
        CheckForUpdatesButton.IsEnabled = result.Status is not UpdateOperationStatus.NotConfigured;
        ApplyUpdateButton.IsEnabled = result.CanApply;
        UpdateStatusText.Text = result.Status switch
        {
            UpdateOperationStatus.BusyDictating =>
                "Finish or cancel the active dictation before checking or applying an update.",
            UpdateOperationStatus.NoUpdate =>
                $"Version {result.Version} is current on this isolated channel.",
            UpdateOperationStatus.DownloadedAndVerified =>
                $"Version {result.Version} is staged and verified. Apply it when you are ready to restart.",
            UpdateOperationStatus.DevelopmentBuild =>
                "Updates can only be checked from a Velopack-installed build.",
            UpdateOperationStatus.NotConfigured =>
                "No update endpoint is configured; no network request was made.",
            UpdateOperationStatus.RejectedHash =>
                "The update hash did not match. It was rejected and will not run.",
            UpdateOperationStatus.RejectedSignature or UpdateOperationStatus.RejectedPublisher =>
                "The update did not pass trusted Envious Labs publisher verification. It was rejected.",
            UpdateOperationStatus.RejectedChannel =>
                "The update identity did not match this release channel. It was rejected.",
            _ => "The update could not be prepared safely. The installed version is unchanged.",
        };
    }

    public void SetCloudPolishNotice(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
        {
            return;
        }

        FoundationInfoBar.Title = "Direct BYOK cloud polish enabled";
        FoundationInfoBar.Message = notice;
    }

    public void SetOllamaPolishNotice(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
        {
            return;
        }

        FoundationInfoBar.Title = "Local Ollama polish enabled";
        FoundationInfoBar.Message = notice;
    }

    public void SetLivePreview(string? text)
    {
        LivePreviewText.Text = text ?? string.Empty;
        LivePreviewText.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _overlayWindow.SetPreview(text);
    }

    public void SetAudioLevel(AudioLevel level) =>
        _overlayWindow.SetAudioLevel(level.RootMeanSquare);

    public async Task NotifyHistoryChangedAsync() => await ReloadHistoryAsync().ConfigureAwait(true);

    public void OpenSettings()
    {
        ShowOnboarding(show: false);
        ProductNavigation.SelectedItem = SettingsNavItem;
        ShowPage("settings");
    }

    public void OpenQuickAdd(string? selection, string? message)
    {
        ShowOnboarding(show: false);
        ProductNavigation.SelectedItem = DictionaryNavItem;
        ShowPage("dictionary");
        if (!string.IsNullOrWhiteSpace(selection))
        {
            SpokenFormBox.Text = selection;
            ReplacementBox.Text = selection;
            ReplacementBox.Focus(FocusState.Programmatic);
            ReplacementBox.SelectAll();
        }
        else
        {
            SpokenFormBox.Focus(FocusState.Programmatic);
            ShowMessage("Add a word", message ?? "Enter the spoken and written forms.", InfoBarSeverity.Warning);
        }
    }

    public void ShutdownProductWindows()
    {
        _soundPreviewCancellation?.Cancel();
        _soundPreviewCancellation?.Dispose();
        _soundPreviewCancellation = null;
        _recordingSoundPlayer.Dispose();
        _cloudModelCatalog.Dispose();
        if (_deviceCatalog is not null)
        {
            _deviceCatalog.DevicesChanged -= OnAudioDevicesChanged;
            _deviceCatalog.Dispose();
            _deviceCatalog = null;
        }

        _overlayWindow.Shutdown();
    }

    public void Dispose() => ShutdownProductWindows();

    public void SetRecoveredText(RecoveryTextLoadResult result)
    {
        if (result.Status == RecoveryTextLoadStatus.Found && result.Record is not null)
        {
            RecoveryTextBox.Text = result.Record.Text;
            RecoveryCard.Visibility = Visibility.Visible;
            FoundationInfoBar.Title = "Interrupted dictation recovered";
            FoundationInfoBar.Message = "Review or copy the private recovery text on Home. It was not pasted automatically.";
            FoundationInfoBar.Severity = InfoBarSeverity.Warning;
            SetOnboardingReliabilityNotice(
                "Interrupted dictation recovered",
                "Select Get started to review or copy the private recovery text. It will not be pasted automatically.");
            return;
        }

        ClearRecoveredText();
        if (result.Status == RecoveryTextLoadStatus.Invalid)
        {
            SetOnboardingReliabilityNotice(
                "Recovery data needs attention",
                "The encrypted recovery file is invalid. It was preserved and no text was exposed.");
            ShowMessage(
                "Recovery data needs attention",
                "The encrypted recovery file is invalid. It was preserved and no text was exposed.",
                InfoBarSeverity.Warning);
        }
        else if (result.Status == RecoveryTextLoadStatus.Unavailable)
        {
            SetOnboardingReliabilityNotice(
                "Recovery storage is unavailable",
                "Windows could not open the encrypted recovery file. Dictation remains available.");
            ShowMessage(
                "Recovery storage is unavailable",
                "Windows could not open the encrypted recovery file. Dictation remains available.",
                InfoBarSeverity.Warning);
        }
    }

    public void SetRunRecoveryNotice(int consecutiveInterruptedRuns)
    {
        FoundationInfoBar.Title = "Recovered after an interrupted run";
        FoundationInfoBar.Message = consecutiveInterruptedRuns > 1
            ? $"EnviousWispr detected {consecutiveInterruptedRuns.ToString(CultureInfo.CurrentCulture)} interrupted starts in a row. Global input and owned runtimes were reinitialized; unfinished text is never pasted automatically."
            : "Global input and owned runtimes were reinitialized. Unfinished text is never pasted automatically.";
        FoundationInfoBar.Severity = InfoBarSeverity.Warning;
        SetOnboardingReliabilityNotice(
            "Recovered after an interrupted run",
            FoundationInfoBar.Message);
    }

    private void SetOnboardingReliabilityNotice(string title, string message)
    {
        OnboardingReliabilityInfoBar.Title = title;
        OnboardingReliabilityInfoBar.Message = message;
        OnboardingReliabilityInfoBar.IsOpen = true;
    }

    public void SetReliabilityNotice(string title, string message, bool isError = false) =>
        ShowMessage(
            title,
            message,
            isError ? InfoBarSeverity.Error : InfoBarSeverity.Warning);

    public void ClearRecoveredText()
    {
        RecoveryTextBox.Text = string.Empty;
        RecoveryCard.Visibility = Visibility.Collapsed;
    }

    private async void FinishOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.HasCompletedOnboarding)
        {
            ShowOnboarding(show: false);
            return;
        }

        var next = _settings with { HasCompletedOnboarding = true };
        if (await TrySaveAsync(next, "Setup complete", "Your choices were saved on this PC.").ConfigureAwait(true))
        {
            ShowOnboarding(show: false);
            ProductNavigation.SelectedItem = HomeNavItem;
            HomeNavItem.Focus(FocusState.Programmatic);
        }
    }

    private void ProductNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag?.ToString() ?? "home";
        ShowPage(tag);
    }

    private void OpenHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var historyItem = ProductNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .First(item => string.Equals(item.Tag?.ToString(), "history", StringComparison.Ordinal));
        ProductNavigation.SelectedItem = historyItem;
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var parsedHotkey = HotkeyGestureParser.Parse(HotkeyTextBox.Text);
        var parsedCancelHotkey = HotkeyGestureParser.Parse(CancelHotkeyTextBox.Text);
        var parsedQuickAddHotkey = HotkeyGestureParser.Parse(QuickAddHotkeyTextBox.Text);
        if (!parsedHotkey.Succeeded || !parsedCancelHotkey.Succeeded || !parsedQuickAddHotkey.Succeeded)
        {
            ShowMessage("Shortcut needs attention", "Use supported keys such as F8, Escape, or Ctrl+Alt+W.", InfoBarSeverity.Error);
            (parsedHotkey.Succeeded
                ? parsedCancelHotkey.Succeeded ? QuickAddHotkeyTextBox : CancelHotkeyTextBox
                : HotkeyTextBox).Focus(FocusState.Programmatic);
            return;
        }

        if (parsedHotkey.Gesture == parsedCancelHotkey.Gesture ||
            parsedHotkey.Gesture == parsedQuickAddHotkey.Gesture ||
            parsedCancelHotkey.Gesture == parsedQuickAddHotkey.Gesture)
        {
            ShowMessage("Shortcuts overlap", "Recording, cancel, and Add-a-word must use three different shortcuts.", InfoBarSeverity.Error);
            return;
        }

        var dictation = new DictationPreferences(
            (FinalAsrEngine)Math.Clamp(SelectedIndexOf(FinalEngineChoices), 0, 2),
            parsedHotkey.Gesture!.Value.ToString(),
            WordCorrectionToggle.IsOn,
            FillerRemovalToggle.IsOn,
            EmojiFormatterToggle.IsOn,
            SpokenPunctuationToggle.IsOn,
            (WhisperLanguagePreference)Math.Clamp(WhisperLanguageComboBox.SelectedIndex, 0, 4),
            (DictationRecordingMode)Math.Clamp(RecordingModeComboBox.SelectedIndex, 0, 1),
            parsedCancelHotkey.Gesture!.Value.ToString(),
            EscapeRecoveryToggle.IsOn,
            parsedQuickAddHotkey.Gesture!.Value.ToString());
        var polish = new PolishPreferences(
            PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)),
            NullIfBlank(PolishModelTextBox.Text),
            NullIfBlank(OllamaEndpointTextBox.Text));
        var history = new HistoryPreferences(
            HistoryEnabledToggle.IsOn,
            (int)Math.Clamp(double.IsNaN(RetentionDaysBox.Value) ? 30 : RetentionDaysBox.Value, 0, 3650));
        var theme = ThemeFromIndex(SelectedIndexOf(ThemeChoices));
        var observability = new ObservabilityPreferences(
            LocalDiagnosticsToggle.IsOn,
            (int)Math.Clamp(
                double.IsNaN(DiagnosticRetentionDaysBox.Value)
                    ? ObservabilityPreferences.Default.DiagnosticRetentionDays
                    : DiagnosticRetentionDaysBox.Value,
                1,
                90),
            _telemetryAvailable && ShareTelemetryToggle.IsOn);
        var microphoneId = (MicrophoneComboBox.SelectedItem as MicrophoneChoice)?.Id;
        var next = _settings with
        {
            PreferredMicrophoneId = microphoneId,
            Preferences = new UserPreferences(
                dictation,
                polish,
                history,
                theme,
                LivePreviewToggle.IsOn,
                OverlayPositionFromIndex(SelectedIndexOf(OverlayPositionChoices)),
                PillDesignWithoutWordsFromControls(),
                RecordingPillDesign.ReadingWell,
                PlayRecordingSoundsToggle.IsOn,
                SelectedRecordingSoundPairing()),
            Observability = observability,
        };

        if (await TrySaveAsync(
                next,
                "Settings saved",
                "Theme, Live Preview, pill design, pill position, recording sounds, and local data choices apply now. Engine, microphone, shortcut, and polish changes apply safely on the next launch.")
            .ConfigureAwait(true))
        {
            ApplyTheme(theme);
        }
    }

    private void ThemeCardChecked(object sender, RoutedEventArgs e)
    {
        if (!_isApplyingSettings)
        {
            ApplyTheme(ThemeFromIndex(SelectedIndexOf(ThemeChoices)));
        }
    }

    private void LivePreviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isApplyingSettings)
        {
            UpdatePillDesignControls();
        }
    }

    private void RecordingSoundComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var choice = RecordingSoundComboBox.SelectedItem as RecordingSoundChoice;
        RecordingSoundDescriptionText.Text = choice?.Description ?? string.Empty;
    }

    private async void PreviewRecordingSoundButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentOverlayState == DictationOverlayState.Recording)
        {
            return;
        }

        _soundPreviewCancellation?.Cancel();
        _soundPreviewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _soundPreviewCancellation = cancellation;
        PreviewRecordingSoundButton.IsEnabled = false;
        var pairing = SelectedRecordingSoundPairing();
        try
        {
            if (!_recordingSoundPlayer.Play(pairing, RecordingSoundMoment.Start))
            {
                ShowMessage(
                    "Sound preview unavailable",
                    "Windows could not open the current audio output device.",
                    InfoBarSeverity.Warning);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(550), cancellation.Token).ConfigureAwait(true);
            if (_currentOverlayState != DictationOverlayState.Recording)
            {
                _recordingSoundPlayer.Play(pairing, RecordingSoundMoment.Stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_soundPreviewCancellation, cancellation))
            {
                _soundPreviewCancellation.Dispose();
                _soundPreviewCancellation = null;
                PreviewRecordingSoundButton.IsEnabled =
                    _currentOverlayState != DictationOverlayState.Recording;
            }
        }
    }

    private async void PolishProviderCardChecked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        ApiKeyPasswordBox.Password = string.Empty;
        RefreshApiKeyStatus();
        await RefreshPolishModelChoicesAsync(
            PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)),
            chooseDefault: true).ConfigureAwait(true);
    }

    private void PolishModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isApplyingSettings && PolishModelPicker.SelectedItem is string modelId)
        {
            PolishModelTextBox.Text = modelId;
        }
    }

    private async void SaveApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        if (!IsCloudProvider(provider))
        {
            ShowMessage(
                "No cloud key needed",
                "Choose OpenAI, Anthropic, or Gemini before storing a provider key.",
                InfoBarSeverity.Informational);
            return;
        }

        var value = ApiKeyPasswordBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            ShowMessage("Enter an API key", "The key field is empty.", InfoBarSeverity.Warning);
            ApiKeyPasswordBox.Focus(FocusState.Programmatic);
            return;
        }

        try
        {
            _apiKeyStore.Store(provider, value);
            ApiKeyPasswordBox.Password = string.Empty;
            RefreshApiKeyStatus();
            ShowMessage(
                $"{ProviderDisplayName(provider)} key saved",
                "The key is stored in Windows Credential Manager. It is not part of settings, profiles, history, or diagnostics.",
                InfoBarSeverity.Success);
            await RefreshPolishModelChoicesAsync(provider, chooseDefault: true)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (IsCredentialStorageFailure(exception))
        {
            ApiKeyPasswordBox.Password = string.Empty;
            RefreshApiKeyStatus();
            ShowMessage(
                "API key was not saved",
                "Windows Credential Manager is unavailable. No key was written to settings or exports.",
                InfoBarSeverity.Error);
        }
    }

    private async void RemoveApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        if (!IsCloudProvider(provider))
        {
            return;
        }

        if (_apiKeyStore.GetStatus(provider) == ApiKeyReadStatus.Missing)
        {
            RefreshApiKeyStatus();
            ShowMessage(
                "No stored key to remove",
                $"No {ProviderDisplayName(provider)} key is stored for EnviousWispr in Windows Credential Manager.",
                InfoBarSeverity.Informational);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = $"Remove the stored {ProviderDisplayName(provider)} key?",
            Content = "This deletes the EnviousWispr credential from Windows Credential Manager. Settings, history, dictionary entries, and snippets are not affected.",
            PrimaryButtonText = "Remove key",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            _apiKeyStore.Delete(provider);
            ApiKeyPasswordBox.Password = string.Empty;
            RefreshApiKeyStatus();
            await RefreshPolishModelChoicesAsync(provider, chooseDefault: false)
                .ConfigureAwait(true);
            ShowMessage(
                $"{ProviderDisplayName(provider)} key removed",
                "The stored credential was removed from Windows Credential Manager.",
                InfoBarSeverity.Success);
        }
        catch (Exception exception) when (IsCredentialStorageFailure(exception))
        {
            RefreshApiKeyStatus();
            ShowMessage(
                "API key could not be removed",
                "Windows Credential Manager is unavailable. Existing settings were not changed.",
                InfoBarSeverity.Error);
        }
    }

    private async void RefreshPolishModelsButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        if (provider is PolishProvider.None or PolishProvider.EgOne)
        {
            return;
        }

        await RefreshPolishModelChoicesAsync(provider, chooseDefault: false)
            .ConfigureAwait(true);
    }

    private async void OpenMicrophonePrivacyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var opened = await Windows.System.Launcher.LaunchUriAsync(
                new Uri("ms-settings:privacy-microphone"));
            if (!opened)
            {
                ShowMessage(
                    "Microphone privacy settings did not open",
                    "Open Windows Settings, then choose Privacy & security > Microphone.",
                    InfoBarSeverity.Warning);
            }
        }
        catch
        {
            ShowMessage(
                "Microphone privacy settings did not open",
                "Open Windows Settings, then choose Privacy & security > Microphone.",
                InfoBarSeverity.Warning);
        }
    }

    private async void AddWordButton_Click(object sender, RoutedEventArgs e)
    {
        var spoken = SpokenFormBox.Text.Trim();
        var replacement = ReplacementBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(spoken) || string.IsNullOrWhiteSpace(replacement))
        {
            ShowMessage("Both fields are required", "Enter the spoken form and the exact replacement.", InfoBarSeverity.Warning);
            return;
        }

        var words = _settings.UserData.CustomWords
            .Where(entry => !string.Equals(entry.SpokenForm, spoken, StringComparison.OrdinalIgnoreCase))
            .Append(new CustomWordEntry(spoken, replacement))
            .OrderBy(entry => entry.SpokenForm, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        await SaveUserDataAsync(new ReusableUserData(words, _settings.UserData.Snippets), "Dictionary saved").ConfigureAwait(true);
        SpokenFormBox.Text = string.Empty;
        ReplacementBox.Text = string.Empty;
    }

    private async void RemoveWordButton_Click(object sender, RoutedEventArgs e)
    {
        if (DictionaryList.SelectedItem is not CustomWordEntry selected)
        {
            ShowMessage("Select a word first", "Choose the dictionary row you want to remove.", InfoBarSeverity.Informational);
            return;
        }

        var words = _settings.UserData.CustomWords.Where(entry => entry != selected).ToArray();
        await SaveUserDataAsync(new ReusableUserData(words, _settings.UserData.Snippets), "Dictionary entry removed").ConfigureAwait(true);
    }

    private async void AddSnippetButton_Click(object sender, RoutedEventArgs e)
    {
        var name = SnippetNameBox.Text.Trim();
        var body = SnippetBodyBox.Text;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(body))
        {
            ShowMessage("Name and text are required", "Give this snippet a name and some reusable text.", InfoBarSeverity.Warning);
            return;
        }

        var snippets = _settings.UserData.Snippets
            .Where(entry => !string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            .Append(new SnippetEntry(name, body))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        await SaveUserDataAsync(new ReusableUserData(_settings.UserData.CustomWords, snippets), "Snippet saved").ConfigureAwait(true);
        SnippetNameBox.Text = string.Empty;
        SnippetBodyBox.Text = string.Empty;
    }

    private async void RemoveSnippetButton_Click(object sender, RoutedEventArgs e)
    {
        if (SnippetList.SelectedItem is not SnippetEntry selected)
        {
            ShowMessage("Select a snippet first", "Choose the snippet you want to remove.", InfoBarSeverity.Informational);
            return;
        }

        var snippets = _settings.UserData.Snippets.Where(entry => entry != selected).ToArray();
        await SaveUserDataAsync(new ReusableUserData(_settings.UserData.CustomWords, snippets), "Snippet removed").ConfigureAwait(true);
    }

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshHistoryView();

    private void ClearHistorySearchButton_Click(object sender, RoutedEventArgs e) =>
        HistorySearchBox.Text = string.Empty;

    private void FocusAddWordButton_Click(object sender, RoutedEventArgs e) =>
        SpokenFormBox.Focus(FocusState.Programmatic);

    private void FocusAddSnippetButton_Click(object sender, RoutedEventArgs e) =>
        SnippetNameBox.Focus(FocusState.Programmatic);

    private void CopyHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItemViewModel selected)
        {
            ShowMessage("Select a dictation first", "Choose the history entry you want to copy.", InfoBarSeverity.Informational);
            return;
        }

        var package = new DataPackage();
        package.SetText(selected.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        ShowMessage("Copied", "The selected dictation is on your clipboard.", InfoBarSeverity.Success);
    }

    private void CopyRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RecoveryTextBox.Text))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(RecoveryTextBox.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        ShowMessage(
            "Recovered text copied",
            "The private recovery text is on your clipboard. Delete the recovery copy when you no longer need it.",
            InfoBarSeverity.Success);
    }

    private async void DeleteRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RecoveryTextBox.Text))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = "Delete the recovered dictation?",
            Content = "This permanently removes the encrypted recovery copy from this PC. It does not change history or clipboard contents.",
            PrimaryButtonText = "Delete recovery copy",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (await _recoveryTextStore.ClearAsync().ConfigureAwait(true))
        {
            ClearRecoveredText();
            FoundationInfoBar.Title = "No recovered dictation is pending";
            FoundationInfoBar.Message = "The encrypted recovery copy was removed. EnviousWispr will not paste or retain that text.";
            FoundationInfoBar.Severity = InfoBarSeverity.Success;
            OnboardingReliabilityInfoBar.IsOpen = false;
            RecoveryCleared?.Invoke();
            ShowMessage(
                "Recovery copy deleted",
                "The encrypted local recovery copy was removed.",
                InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage(
                "Recovery copy could not be deleted",
                "Windows left the encrypted recovery file untouched.",
                InfoBarSeverity.Error);
        }
    }

    private async void DeleteHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItemViewModel selected)
        {
            ShowMessage("Select a dictation first", "Choose the history entry you want to delete.", InfoBarSeverity.Informational);
            return;
        }

        var result = await _historyStore.DeleteAsync(selected.Id).ConfigureAwait(true);
        if (result.Succeeded)
        {
            await ReloadHistoryAsync().ConfigureAwait(true);
            ShowMessage("History entry deleted", "The local copy was removed.", InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage("History could not be changed", "The private history file was left untouched.", InfoBarSeverity.Error);
        }
    }

    private async void KeepHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItemViewModel selected)
        {
            ShowMessage("Select a dictation first", "Choose the temporary recovery entry you want to keep.", InfoBarSeverity.Informational);
            return;
        }

        var result = await _historyStore.KeepAsync(selected.Id).ConfigureAwait(true);
        if (result.Succeeded)
        {
            await ReloadHistoryAsync().ConfigureAwait(true);
            ShowMessage("History entry kept", "Its 24-hour Escape Recovery expiry was removed.", InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage("History could not be changed", "The private history file was left untouched.", InfoBarSeverity.Error);
        }
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = "Delete all dictation history?",
            Content = "This permanently removes every locally saved transcript. Settings, dictionary entries, and snippets are not affected.",
            PrimaryButtonText = "Delete all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await _historyStore.ClearAsync().ConfigureAwait(true);
        if (result.Succeeded)
        {
            await ReloadHistoryAsync().ConfigureAwait(true);
            ShowMessage("History cleared", "All locally saved dictations were removed.", InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage("History could not be cleared", "The private history file was left untouched.", InfoBarSeverity.Error);
        }
    }

    private async void ExportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = "EnviousWispr-profile",
            DefaultFileExtension = ".json",
        };
        picker.FileTypeChoices.Add("EnviousWispr profile", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var result = await _profileService.ExportAsync(_settings.ToPortableProfile(), file.Path).ConfigureAwait(true);
        ShowMessage(
            result.Succeeded ? "Profile exported" : "Profile export failed safely",
            result.Succeeded ? "Settings, dictionary entries, and snippets were written without private machine data." : "No existing destination data was intentionally replaced with an invalid profile.",
            result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = "EnviousWispr-diagnostics",
            DefaultFileExtension = ".jsonl",
        };
        picker.FileTypeChoices.Add("Privacy-safe diagnostics", [".jsonl"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var retentionDays = _settings.Observability?.DiagnosticRetentionDays ??
            ObservabilityPreferences.Default.DiagnosticRetentionDays;
        var result = await _diagnosticExportService.ExportAsync(
            file.Path,
            retentionDays,
            DateTimeOffset.UtcNow).ConfigureAwait(true);
        DiagnosticsExportCompleted?.Invoke(result.Succeeded, result.ExportedRecordCount);
        ShowMessage(
            result.Succeeded ? "Diagnostics exported" : "Diagnostics export failed safely",
            result.Succeeded
                ? $"{result.ExportedRecordCount.ToString(CultureInfo.CurrentCulture)} content-free operational record{(result.ExportedRecordCount == 1 ? string.Empty : "s")} exported. No dictated text, audio, keys, clipboard, surrounding context, or stable device identifier is included."
                : "The destination was left untouched or replaced only with a valid content-free export.",
            result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e) =>
        UpdateCheckRequested?.Invoke();

    private void ApplyUpdateButton_Click(object sender, RoutedEventArgs e) =>
        UpdateApplyRequested?.Invoke();

    private async void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        var imported = await _profileService.ImportAsync(file.Path).ConfigureAwait(true);
        if (imported.Status != PortableProfileImportStatus.Imported || imported.Profile is null)
        {
            ShowMessage("Profile not imported", ImportFailureMessage(imported.Status), InfoBarSeverity.Error);
            return;
        }

        var next = _settings.Apply(imported.Profile);
        if (await TrySaveAsync(next, "Profile imported", "Settings, dictionary entries, and snippets are ready. Machine-local choices and history were preserved.").ConfigureAwait(true))
        {
            ApplySettingsToControls();
        }
    }

    private async Task LoadMicrophonesAsync()
    {
        try
        {
            if (_deviceCatalog is null)
            {
                _deviceCatalog = new WasapiDeviceCatalog();
                _deviceCatalog.DevicesChanged += OnAudioDevicesChanged;
            }

            var devices = await _deviceCatalog.GetCaptureDevicesAsync().ConfigureAwait(true);
            _microphones = [
                new MicrophoneChoice(null, "Use the Windows default microphone"),
                .. devices.Select(device => new MicrophoneChoice(
                    device.Id.Value,
                    device.IsDefault ? $"{device.DisplayName} (Windows default)" : device.DisplayName)),
            ];
            MicrophoneComboBox.ItemsSource = _microphones;
            var selected = _microphones.FirstOrDefault(choice =>
                string.Equals(choice.Id, _settings.PreferredMicrophoneId, StringComparison.Ordinal)) ?? _microphones[0];
            MicrophoneComboBox.SelectedItem = selected;
            var defaultDevice = devices.FirstOrDefault(device => device.IsDefault) ??
                (devices.Count == 0 ? null : devices[0]);
            var status = defaultDevice is null
                ? "No active recording device found. Windows microphone privacy or device settings may need attention."
                : $"{defaultDevice.DisplayName} is available.";
            MicrophoneReadinessText.Text = status;
            OnboardingMicrophoneText.Text = status;
        }
        catch
        {
            _microphones = [new MicrophoneChoice(null, "Use the Windows default microphone")];
            MicrophoneComboBox.ItemsSource = _microphones;
            MicrophoneComboBox.SelectedIndex = 0;
            const string status = "Windows could not enumerate microphones. Settings remain available and dictation will fail safely.";
            MicrophoneReadinessText.Text = status;
            OnboardingMicrophoneText.Text = status;
        }
    }

    private void OnAudioDevicesChanged(object? sender, AudioDeviceChange change)
    {
        if (!change.AffectsCapture)
        {
            return;
        }

        AudioDevicesChanged?.Invoke(change);
        // An async callback handed to TryEnqueue is an async void: a throw inside it is
        // UNOBSERVED and takes the process down. This one fires on a device change - a headset
        // plugged in, a dock disconnected - which is an ordinary thing to do while the app is
        // sitting in the tray, and both halves can fail (device enumeration, and touching UI on a
        // window that may be closing). Same shape as the navigation crash: a callback that runs
        // later, against a world that has moved on.
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await LoadMicrophonesAsync().ConfigureAwait(true);
                ShowMessage(
                    "Microphone devices updated",
                    "EnviousWispr refreshed the active recording-device list. A missing preferred microphone falls back to the Windows default.",
                    InfoBarSeverity.Informational);
            }
            catch (Exception)
            {
                // Failing to REFRESH a device list, or to announce that it changed, is a missed
                // convenience. Letting it escape is a dead app and a lost dictation. The user's
                // next recording re-reads the device list anyway, so there is nothing here worth
                // the process.
            }
        });
    }

    private async Task ReloadHistoryAsync()
    {
        _isHistoryLoading = true;
        UpdateHistoryListVisibility(HistorySearchBox.Text.Trim(), itemCount: 0);
        var result = await _historyStore.LoadAsync(
            _settings.Preferences.History.RetentionDays,
            DateTimeOffset.UtcNow).ConfigureAwait(true);
        _historyLoadStatus = result.Status;
        _history.Clear();
        _history.AddRange(result.Entries.Select(entry => new HistoryItemViewModel(entry)));
        _isHistoryLoading = false;
        RefreshHistoryView();
        HistorySummaryText.Text = result.Status switch
        {
            HistoryLoadStatus.Invalid => "History is unavailable because its local file is invalid; the source was preserved for recovery.",
            HistoryLoadStatus.Unavailable => "Windows could not open the private history file.",
            _ when !_settings.Preferences.History.IsEnabled => "History is off. New dictations will not be saved.",
            _ when _history.Count == 0 => "No dictations saved yet.",
            _ => $"{_history.Count.ToString(CultureInfo.CurrentCulture)} local dictation{(_history.Count == 1 ? string.Empty : "s")} saved.",
        };
    }

    private void RefreshHistoryView()
    {
        var query = HistorySearchBox.Text.Trim();
        var visibleHistory = string.IsNullOrWhiteSpace(query)
            ? _history.ToArray()
            : _history.Where(item => item.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        HistoryList.ItemsSource = visibleHistory;
        UpdateHistoryListVisibility(query, visibleHistory.Length);
        UpdateSelectionDependentButtons();
    }

    /// <summary>
    /// Keeps every button that acts on a list selection in step with that selection.
    /// </summary>
    /// <remarks>
    /// These buttons were live with nothing selected. Clicking one was not destructive - each
    /// handler guards and shows "Select a dictation first" - but offering an action and then
    /// telling the user off for taking it is the difference between working and finished. The
    /// button should not be there to click.
    ///
    /// "Delete all history" tracks the LIST rather than the selection: with no history there is
    /// nothing to delete, and a live button promising otherwise is the same defect one step over.
    /// </remarks>
    private void UpdateSelectionDependentButtons()
    {
        // The lists appear BEFORE some of these buttons in the markup, so a selection event
        // raised while the page is still being built would reach a field that is not assigned
        // yet. An empty ListView does not raise one, but "does not" and "cannot" are different
        // claims and the cost of being wrong here is an app that will not start. Every caller
        // that populates a list calls this again afterwards, so a skipped early pass corrects
        // itself rather than leaving a button stale.
        //
        // Every button is named, not just the last-declared ones. Guarding three and then
        // dereferencing six would be correct only because of the order they happen to sit in the
        // markup, which is a premise nothing states and any reorder silently breaks.
        if (CopyHistoryButton is null
            || KeepHistoryButton is null
            || DeleteHistoryButton is null
            || ClearHistoryButton is null
            || RemoveWordButton is null
            || RemoveSnippetButton is null)
        {
            return;
        }

        var historySelected = HistoryList.SelectedItem is not null;
        CopyHistoryButton.IsEnabled = historySelected;
        KeepHistoryButton.IsEnabled = historySelected;
        DeleteHistoryButton.IsEnabled = historySelected;
        ClearHistoryButton.IsEnabled = _history.Count > 0;

        RemoveWordButton.IsEnabled = DictionaryList.SelectedItem is not null;
        RemoveSnippetButton.IsEnabled = SnippetList.SelectedItem is not null;
    }

    /// <summary>
    /// Hides a section's eyebrow when it merely repeats the page title above it.
    /// </summary>
    /// <remarks>
    /// On a page showing ONE section the small-caps eyebrow sits a few pixels under the page
    /// title saying the same word: "Sounds" over "SOUNDS". Splitting the pages made this worse,
    /// because most pages now show exactly one section. Where the eyebrow does real work -
    /// Transcription shows an engine section and a cleanup section - it stays.
    ///
    /// Shared by BOTH pages. The first version lived inside the settings page only, so Open Source
    /// Licenses kept showing "OPEN SOURCE LICENSES" under "Open Source Licenses" - the exact case
    /// the rule was written for, on the one page the rule never ran. Only an EXACT match collapses:
    /// Permissions shows "PERMISSIONS AND PRIVACY" and Updates shows "UPDATES" under "Check for
    /// Updates", and neither is a repeat.
    /// </remarks>
    private static void CollapseEyebrowThatRepeatsTheTitle(
        Border[] allSections, Border[] visibleSections, string title)
    {
        foreach (var candidate in allSections)
        {
            var eyebrow = EyebrowOf(candidate);
            if (eyebrow is null)
            {
                continue;
            }

            var redundant = visibleSections.Length == 1
                && ReferenceEquals(candidate, visibleSections[0])
                && string.Equals(eyebrow.Text, title, StringComparison.OrdinalIgnoreCase);
            eyebrow.Visibility = redundant ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// The eyebrow of a section card: by construction the first TextBlock inside it.
    /// </summary>
    /// <remarks>
    /// Read from the STRUCTURE rather than from a name on each of the fifteen eyebrows, so a
    /// section added later behaves the same without anyone wiring it up. Returns null rather than
    /// throwing if a card is ever built differently - a missing eyebrow is not worth a crash.
    /// </remarks>
    private static TextBlock? EyebrowOf(Border section) =>
        section.Child is StackPanel panel
            ? panel.Children.OfType<TextBlock>().FirstOrDefault()
            : null;

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSelectionDependentButtons();

    private async Task SaveUserDataAsync(ReusableUserData userData, string title)
    {
        if (await TrySaveAsync(_settings with { UserData = userData }, title, "The change was saved locally.").ConfigureAwait(true))
        {
            RefreshReusableUserDataViews();
        }
    }

    private void UpdateHistoryListVisibility(string query, int itemCount)
    {
        var hasItems = itemCount > 0;
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var historyUnavailable = _historyLoadStatus is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable;
        HistoryLoadingState.Visibility = _isHistoryLoading ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.Visibility = !_isHistoryLoading && hasItems ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmptyState.Visibility = !_isHistoryLoading && !hasItems && !hasQuery && !historyUnavailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        HistorySearchEmptyState.Visibility = !_isHistoryLoading && !hasItems && hasQuery && !historyUnavailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        HistoryUnavailableState.Visibility = !_isHistoryLoading && !hasItems && historyUnavailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!hasItems && hasQuery && !historyUnavailable)
        {
            HistorySearchEmptyDescription.Text = $"No saved dictations match “{query}”. Try another search or clear it.";
        }

        if (!hasItems && historyUnavailable)
        {
            HistoryUnavailableDescription.Text = _historyLoadStatus == HistoryLoadStatus.Invalid
                ? "The local history file is invalid. It was left untouched for recovery."
                : "Windows could not open the private history file. It was left untouched.";
        }
    }

    private void RefreshReusableUserDataViews()
    {
        var customWords = _settings.UserData.CustomWords;
        var snippets = _settings.UserData.Snippets;
        DictionaryList.ItemsSource = customWords;
        SnippetList.ItemsSource = snippets;
        UpdateListAndEmptyStateVisibility(DictionaryList, DictionaryEmptyState, customWords.Count);
        UpdateListAndEmptyStateVisibility(SnippetList, SnippetEmptyState, snippets.Count);
        UpdateSelectionDependentButtons();
    }

    private static void UpdateListAndEmptyStateVisibility(
        ListView list,
        FrameworkElement emptyState,
        int itemCount)
    {
        list.Visibility = itemCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        emptyState.Visibility = itemCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task<bool> TrySaveAsync(AppSettings next, string title, string message)
    {
        try
        {
            await _settingsStore.SaveAsync(next).ConfigureAwait(true);
            _settings = next;
            SettingsChanged?.Invoke(next);
            ApplySettingsToControls();
            ShowMessage(title, message, InfoBarSeverity.Success);
            return true;
        }
        catch (ArgumentException)
        {
            ShowMessage("Settings were not saved", "One or more values are invalid. Your previous settings remain active.", InfoBarSeverity.Error);
        }
        catch (IOException)
        {
            ShowMessage("Settings storage is unavailable", "Your previous settings remain active.", InfoBarSeverity.Error);
        }
        catch (UnauthorizedAccessException)
        {
            ShowMessage("Windows blocked settings storage", "Your previous settings remain active.", InfoBarSeverity.Error);
        }

        return false;
    }

    private void ApplySettingsToControls()
    {
        _isApplyingSettings = true;
        try
        {
            var preferences = _settings.Preferences;
            SelectChoice(FinalEngineChoices, (int)preferences.Dictation.FinalEngine);
            WhisperLanguageComboBox.SelectedIndex = (int)preferences.Dictation.WhisperLanguage;
            HotkeyTextBox.Text = preferences.Dictation.PushToTalkGesture;
            RecordingModeComboBox.SelectedIndex = (int)preferences.Dictation.RecordingMode;
            CancelHotkeyTextBox.Text = preferences.Dictation.CancelGesture;
            EscapeRecoveryToggle.IsOn = preferences.Dictation.EscapeRecoveryEnabled;
            QuickAddHotkeyTextBox.Text = preferences.Dictation.QuickAddGesture;
            WordCorrectionToggle.IsOn = preferences.Dictation.WordCorrectionEnabled;
            FillerRemovalToggle.IsOn = preferences.Dictation.FillerRemovalEnabled;
            EmojiFormatterToggle.IsOn = preferences.Dictation.EmojiFormatterEnabled;
            SpokenPunctuationToggle.IsOn = preferences.Dictation.SpokenPunctuationEnabled;
            SelectChoice(PolishProviderChoices, PolishProviderIndex(preferences.Polish.Provider));
            PolishModelTextBox.Text = preferences.Polish.ModelId ?? string.Empty;
            OllamaEndpointTextBox.Text = preferences.Polish.OllamaEndpoint ?? string.Empty;
            HistoryEnabledToggle.IsOn = preferences.History.IsEnabled;
            RetentionDaysBox.Value = preferences.History.RetentionDays;
            SelectChoice(ThemeChoices, ThemeIndex(preferences.Theme));
            LivePreviewToggle.IsOn = preferences.LivePreviewEnabled;
            SelectChoice(OverlayPositionChoices, OverlayPositionIndex(preferences.OverlayPosition));
            CapsulePillButton.IsChecked =
                preferences.PillDesignWithoutWords == RecordingPillDesign.Classic;
            LevelRailPillButton.IsChecked =
                preferences.PillDesignWithoutWords == RecordingPillDesign.LevelRail;
            ReadingWellPillButton.IsChecked = true;
            PlayRecordingSoundsToggle.IsOn = preferences.PlayRecordingSounds;
            RecordingSoundComboBox.SelectedItem = RecordingSoundCatalog.Find(
                preferences.RecordingSoundPairing);
            RecordingSoundDescriptionText.Text = RecordingSoundCatalog.Find(
                preferences.RecordingSoundPairing).Description;
            UpdatePillDesignControls();
            _overlayWindow.ApplyPreferences(
                preferences.OverlayPosition,
                preferences.LivePreviewEnabled,
                preferences.PillDesignWithoutWords,
                preferences.PillDesignWithWords);
            var observability = _settings.Observability ?? ObservabilityPreferences.Default;
            LocalDiagnosticsToggle.IsOn = observability.LocalDiagnosticsEnabled;
            DiagnosticRetentionDaysBox.Value = observability.DiagnosticRetentionDays;
            ShareTelemetryToggle.IsEnabled = _telemetryAvailable;
            ShareTelemetryToggle.IsOn = _telemetryAvailable && observability.ShareAnonymousTelemetry;
            DiagnosticsStatusText.Text = _telemetryAvailable
                ? "Anonymous sharing is off until you explicitly enable and save it. Local exports and uploads contain only the typed fields listed here."
                : "No telemetry upload channel is configured in this development build. Local content-free diagnostics can still be retained, exported, or disabled.";
            RefreshReusableUserDataViews();
        }
        finally
        {
            _isApplyingSettings = false;
        }

        ApiKeyPasswordBox.Password = string.Empty;
        RefreshApiKeyStatus();
        _ = RefreshPolishModelChoicesAsync(
            _settings.Preferences.Polish.Provider,
            chooseDefault: false);
    }

    private async Task RefreshPolishModelChoicesAsync(
        PolishProvider provider,
        bool chooseDefault)
    {
        var discoveryVersion = Interlocked.Increment(ref _polishModelDiscoveryVersion);
        var isCloudProvider = IsCloudProvider(provider);
        OllamaEndpointTextBox.Visibility = provider == PolishProvider.Ollama
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApiKeyPasswordBox.Visibility = isCloudProvider
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApiKeyButtonPanel.Visibility = isCloudProvider
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshPolishModelsButton.Visibility = provider is PolishProvider.Ollama or
            PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini
                ? Visibility.Visible
                : Visibility.Collapsed;
        RefreshPolishModelsButton.IsEnabled = false;

        IReadOnlyList<string> choices = provider switch
        {
            PolishProvider.EgOne => ["eg-1"],
            PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini =>
                [CloudPolishOptions.DefaultModel(provider)],
            _ => [],
        };
        string? discoveryNotice = null;
        if (provider == PolishProvider.Ollama)
        {
            await using var catalog = new OllamaApiClient(NullIfBlank(OllamaEndpointTextBox.Text));
            var discovery = await catalog.DiscoverAsync().ConfigureAwait(true);
            choices = discovery.LocalModels.Select(model => model.Id).ToArray();
            discoveryNotice = discovery.Health == OllamaHealth.Ready
                ? $"{choices.Count.ToString(CultureInfo.CurrentCulture)} local Ollama model{(choices.Count == 1 ? string.Empty : "s")} available on this PC."
                : "Ollama is not ready at this endpoint. Start Ollama or enter another loopback endpoint.";
        }
        else if (isCloudProvider)
        {
            var discovery = await _cloudModelCatalog.DiscoverAsync(provider).ConfigureAwait(true);
            if (discovery.Status == CloudModelCatalogStatus.Ready)
            {
                if (discovery.ModelIds.Count > 0)
                {
                    choices = discovery.ModelIds;
                }

                discoveryNotice = discovery.ModelIds.Count > 0
                    ? $"{discovery.ModelIds.Count.ToString(CultureInfo.CurrentCulture)} compatible {ProviderDisplayName(provider)} model{(discovery.ModelIds.Count == 1 ? string.Empty : "s")} available to the stored key. No transcript or generation request was sent."
                    : $"{ProviderDisplayName(provider)} returned no compatible transcript-polish models. The recommended model and custom-ID field remain available.";
            }
            else
            {
                discoveryNotice = CloudModelDiscoveryNotice(provider, discovery.Status);
            }
        }

        if (discoveryVersion != Volatile.Read(ref _polishModelDiscoveryVersion))
        {
            return;
        }

        RefreshPolishModelsButton.IsEnabled = provider is PolishProvider.Ollama or
            PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini;
        PolishModelPicker.ItemsSource = choices;
        PolishModelPicker.IsEnabled = choices.Count > 0;
        var current = PolishModelTextBox.Text.Trim();
        var selectedIndex = choices
            .Select((model, index) => new { model, index })
            .Where(item => string.Equals(item.model, current, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();

        if (chooseDefault && selectedIndex < 0 && choices.Count > 0)
        {
            var shouldChoose = provider switch
            {
                PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini =>
                    !CloudPolishOptions.ModelIdLooksLikeProvider(current, provider),
                _ => string.IsNullOrWhiteSpace(current) ||
                    !choices.Contains(current, StringComparer.OrdinalIgnoreCase),
            };
            if (shouldChoose)
            {
                PolishModelTextBox.Text = choices[0];
                selectedIndex = 0;
            }
        }

        PolishModelPicker.SelectedIndex = selectedIndex;
        if (discoveryNotice is not null)
        {
            ApiKeyStatusText.Text = discoveryNotice;
        }
    }

    private static string CloudModelDiscoveryNotice(
        PolishProvider provider,
        CloudModelCatalogStatus status) => status switch
    {
        CloudModelCatalogStatus.MissingCredential =>
            $"Save a {ProviderDisplayName(provider)} API key to discover the compatible models available to that account. The recommended model and custom-ID field remain available.",
        CloudModelCatalogStatus.CredentialUnavailable =>
            "Windows Credential Manager could not provide the stored key. No provider request was sent.",
        CloudModelCatalogStatus.KeyRejected =>
            $"{ProviderDisplayName(provider)} rejected the stored key while listing models. Replace the key and try again.",
        CloudModelCatalogStatus.ProviderUnavailable =>
            $"{ProviderDisplayName(provider)} model discovery is temporarily unavailable. The recommended model and custom-ID field remain available.",
        CloudModelCatalogStatus.InvalidResponse =>
            $"{ProviderDisplayName(provider)} returned an unrecognized model catalog. The recommended model and custom-ID field remain available.",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private void RefreshApiKeyStatus()
    {
        var provider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        var isCloudProvider = IsCloudProvider(provider);
        ApiKeyPasswordBox.IsEnabled = isCloudProvider;
        SaveApiKeyButton.IsEnabled = isCloudProvider;
        RemoveApiKeyButton.IsEnabled = isCloudProvider;
        ApiKeyPasswordBox.PlaceholderText = isCloudProvider
            ? $"Enter {ProviderDisplayName(provider)} API key"
            : "Choose a direct cloud provider to manage its key";

        if (!isCloudProvider)
        {
            ApiKeyStatusText.Text = provider switch
            {
                PolishProvider.Ollama => "Ollama runs on this PC and does not use a cloud API key.",
                PolishProvider.EgOne => "EG-1 runs on this PC and does not use a cloud API key.",
                _ => "AI polish is off; no provider key is used.",
            };
            return;
        }

        ApiKeyStatusText.Text = _apiKeyStore.GetStatus(provider) switch
        {
            ApiKeyReadStatus.Found =>
                $"{CredentialArticle(provider)} {ProviderDisplayName(provider)} key is stored in Windows Credential Manager.",
            ApiKeyReadStatus.Missing =>
                $"No {ProviderDisplayName(provider)} key is stored on this PC.",
            _ => "Windows Credential Manager status is unavailable. No key value was revealed.",
        };
    }

    private void ShowOnboarding(bool show)
    {
        OnboardingView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ProductNavigation.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show)
        {
            FinishOnboardingButton.Focus(FocusState.Programmatic);
        }
    }

    private void ShowPage(string tag)
    {
        var settingsPage = tag == "settings" || tag.StartsWith("settings-", StringComparison.Ordinal);
        var helpPage = tag == "help" || tag.StartsWith("help-", StringComparison.Ordinal);
        HomePage.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;
        WhatsNewPage.Visibility = tag == "whats-new" ? Visibility.Visible : Visibility.Collapsed;
        DictionaryPage.Visibility = tag == "dictionary" ? Visibility.Visible : Visibility.Collapsed;
        SnippetsPage.Visibility = tag == "snippets" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = settingsPage ? Visibility.Visible : Visibility.Collapsed;
        HelpPage.Visibility = helpPage ? Visibility.Visible : Visibility.Collapsed;

        if (settingsPage)
        {
            ConfigureSettingsPage(tag);
        }
        else if (helpPage)
        {
            ConfigureHelpPage(tag);
            ScrollPageToTop(HelpPage);
        }
    }

    private void ConfigureSettingsPage(string tag)
    {
        var (title, description, glyph, section) = tag switch
        {
            "settings-appearance" => (
                "Appearance",
                "The theme, and where the recording pill appears while you dictate.",
                "\uE771",
                (FrameworkElement?)AppearanceSection),
            "settings-transcription" => (
                "Transcription",
                "The speech engine that turns your voice into text.",
                "\uE8C1",
                (FrameworkElement?)TranscriptionEngineSection),
            "settings-live-preview" => (
                "Live Preview",
                "See your words on screen while you are still speaking.",
                "\uE890",
                (FrameworkElement?)LivePreviewSection),
            "settings-microphone" => (
                "Microphone",
                "Choose your input source and readiness behavior.",
                "\uE720",
                (FrameworkElement?)MicrophoneSection),
            "settings-sounds" => (
                "Sounds",
                "Play a short sound when recording starts and stops.",
                "\uE767",
                (FrameworkElement?)SoundSection),
            "settings-keybinds" => (
                "Keybinds",
                "Set the keybinds that start, stop, and cancel dictation.",
                "\uE765",
                (FrameworkElement?)KeybindsSection),
            "settings-ai-polish" => (
                "AI Polish",
                "Clean up and rewrite your dictation with AI.",
                "\uE70F",
                (FrameworkElement?)AiPolishSection),
            "settings-clipboard" => (
                "Clipboard",
                "How your transcript reaches the clipboard and the app you're in.",
                "\uE8C8",
                (FrameworkElement?)ClipboardSection),
            _ => (
                "All Settings",
                "Choose how EnviousWispr records, transcribes, cleans, and stores your dictation.",
                "\uE713",
                (FrameworkElement?)null),
        };

        SettingsPageTitle.Text = title;
        SettingsPageDescription.Text = description;
        SettingsPageGlyph.Glyph = glyph;

        var showAll = section is null;
        var showTranscriptionCompanion = tag == "settings-transcription";
        foreach (var candidate in SettingsSections())
        {
            candidate.Visibility = showAll ||
                ReferenceEquals(candidate, section) ||
                (showTranscriptionCompanion && ReferenceEquals(candidate, DeterministicCleanupSection))
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        var visible = SettingsSections()
            .Where(candidate => candidate.Visibility == Visibility.Visible)
            .ToArray();
        CollapseEyebrowThatRepeatsTheTitle(SettingsSections(), visible, title);

        // A page with nothing to change should not offer to save it. Clipboard is one paragraph
        // explaining fixed behaviour - there are no clipboard preferences in AppSettings at all -
        // so the button sat under it promising an action it could not perform on anything visible.
        // Asked of the SECTIONS rather than of a list of tags kept here, so a prose-only section
        // added later is handled without anyone remembering this rule.
        SaveSettingsButton.Visibility = visible.Any(ContainsAnEditableControl)
            ? Visibility.Visible
            : Visibility.Collapsed;

        ScrollPageToTop(SettingsPage);
    }

    /// <summary>
    /// True when <paramref name="element"/> contains a control the user can actually change.
    /// </summary>
    /// <remarks>
    /// Walks the LOGICAL tree - panels, borders, content controls - rather than the visual tree,
    /// because the visual tree of a collapsed section is not realised and would report every
    /// hidden section as empty, which is the answer that happens to look right.
    /// </remarks>
    private static bool ContainsAnEditableControl(DependencyObject element)
    {
        if (element is ToggleSwitch or ComboBox or RadioButtons or RadioButton
            or TextBox or PasswordBox or NumberBox or CheckBox or Slider)
        {
            return true;
        }

        return element switch
        {
            Panel panel => panel.Children.Any(ContainsAnEditableControl),
            Border border => border.Child is not null && ContainsAnEditableControl(border.Child),
            ContentControl content => content.Content is DependencyObject child
                && ContainsAnEditableControl(child),
            _ => false,
        };
    }

    private void ConfigureHelpPage(string tag)
    {
        var (title, description, glyph) = tag switch
        {
            "help-permissions" => (
                "Permissions",
                "The microphone and accessibility access EnviousWispr needs.",
                "\uE72E"),
            "help-updates" => (
                "Check for Updates",
                string.Empty,
                "\uE895"),
            "help-licenses" => (
                "Open Source Licenses",
                "EnviousWispr is GPLv3 open source. The license and third-party notices.",
                "\uE8A5"),
            _ => (
                "Help and privacy",
                "Find keyboard guidance, privacy details, updates, and licenses.",
                "\uE897"),
        };

        HelpPageTitle.Text = title;
        HelpPageDescription.Text = description;
        HelpPageGlyph.Glyph = glyph;

        // Show only the section the user asked for. Setting the header alone left three sidebar
        // rows rendering all five sections, so "Permissions" opened onto a keyboard guide and the
        // sidebar promised three destinations while delivering one.
        var section = HelpSectionFor(tag);
        var showAll = section is null;
        foreach (var candidate in HelpSections())
        {
            candidate.Visibility = showAll || ReferenceEquals(candidate, section)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        var visible = HelpSections()
            .Where(candidate => candidate.Visibility == Visibility.Visible)
            .ToArray();
        CollapseEyebrowThatRepeatsTheTitle(HelpSections(), visible, title);
    }

    private Border[] HelpSections() =>
    [
        KeyboardGuideSection,
        PermissionsSection,
        LanguageSupportSection,
        UpdatesSection,
        LicensesSection,
    ];

    private Border[] SettingsSections() =>
    [
        MicrophoneSection,
        TranscriptionEngineSection,
        KeybindsSection,
        SoundSection,
        DeterministicCleanupSection,
        AiPolishSection,
        HistorySettingsSection,
        AppearanceSection,
        LivePreviewSection,
        ClipboardSection,
        DiagnosticsSection,
        PortableProfileSection,
    ];

    private Border? HelpSectionFor(string tag) => tag switch
    {
        "help-permissions" => PermissionsSection,
        "help-updates" => UpdatesSection,
        "help-licenses" => LicensesSection,
        _ => null,
    };

    /// <summary>
    /// Returns a page to the top after navigation. Best effort: never worth a crash.
    /// </summary>
    /// <remarks>
    /// This CRASHED THE APP, and the crash was mine. It used to compute a section's offset and
    /// call <c>UpdateLayout()</c> first to make that offset valid. Filtering the Help page removed
    /// the last caller that passed a section, so the offset branch became dead - and I left the
    /// <c>UpdateLayout()</c> behind serving nothing but the dead code.
    ///
    /// The callback is queued, so navigation can move on before it runs. When it did,
    /// <c>UpdateLayout()</c> threw <c>E_UNEXPECTED</c> on a page no longer in the visual tree,
    /// nothing caught it inside a DispatcherQueue callback, and the process died. Found by driving
    /// rapid page changes; at human clicking speed it would present as "the app randomly closed".
    ///
    /// So the fix is a deletion rather than a guard: nothing here needs a forced layout pass any
    /// more. The remaining guard and catch cover the same window for <c>ChangeView</c>, which can
    /// reach a torn-down page for exactly the same reason. Failing to scroll is invisible; failing
    /// loudly costs the user their session.
    /// </remarks>
    private static void ScrollPageToTop(ScrollViewer page)
    {
        page.DispatcherQueue.TryEnqueue(() =>
        {
            if (page.XamlRoot is null)
            {
                return;
            }

            try
            {
                page.ChangeView(null, 0, null, disableAnimation: true);
            }
            catch (COMException failure)
            {
                // The page went away between the check above and this call. Scrolling a page the
                // user has already navigated off is a no-op worth exactly nothing, so swallowing
                // is the correct outcome and not a hidden failure.
                //
                // Stated plainly because the next reader will otherwise assume a trace exists:
                // Debug.WriteLine is compiled OUT of release builds, so this leaves a breadcrumb
                // for someone reproducing the crash under a debugger and NOTHING in a shipped
                // app. There is no logger on this window to route it to. If this ever needs to be
                // visible in the field it belongs in the privacy-safe diagnostics record - the
                // event is content-free, so that route is open - not in a wider catch here.
                Debug.WriteLine($"ScrollPageToTop: page went away before ChangeView ({failure.HResult:X8}).");
            }
        });
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        OperationInfoBar.Title = title;
        OperationInfoBar.Message = message;
        OperationInfoBar.Severity = severity;
        OperationInfoBar.IsOpen = true;
    }

    private void ApplyTheme(AppTheme theme) => WindowRoot.RequestedTheme = theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private static AppTheme ThemeFromIndex(int index) => index switch
    {
        1 => AppTheme.Light,
        2 => AppTheme.Dark,
        _ => AppTheme.System,
    };

    private static int ThemeIndex(AppTheme theme) => theme switch
    {
        AppTheme.Light => 1,
        AppTheme.Dark => 2,
        _ => 0,
    };

    private static OverlayPillPosition OverlayPositionFromIndex(int index) =>
        index == 1 ? OverlayPillPosition.Bottom : OverlayPillPosition.Top;

    private static int OverlayPositionIndex(OverlayPillPosition position) =>
        position == OverlayPillPosition.Bottom ? 1 : 0;

    private RecordingPillDesign PillDesignWithoutWordsFromControls() =>
        LevelRailPillButton.IsChecked == true
            ? RecordingPillDesign.LevelRail
            : RecordingPillDesign.Classic;

    private RecordingSoundPairing SelectedRecordingSoundPairing() =>
        (RecordingSoundComboBox.SelectedItem as RecordingSoundChoice)?.Pairing ??
        RecordingSoundPairing.WhisperTick;

    private void UpdatePillDesignControls()
    {
        var withWords = LivePreviewToggle.IsOn;
        CapsulePillButton.IsEnabled = !withWords;
        LevelRailPillButton.IsEnabled = !withWords;
        ReadingWellPillButton.IsEnabled = withWords;
        WithoutWordsPillHeading.Text = withWords
            ? "Live Preview off"
            : "Live Preview off · In use";
        WithWordsPillHeading.Text = withWords
            ? "Live Preview on · In use"
            : "Live Preview on";
    }

    private static PolishProvider PolishProviderFromIndex(int index) => index switch
    {
        1 => PolishProvider.EgOne,
        2 => PolishProvider.Ollama,
        3 => PolishProvider.OpenAI,
        4 => PolishProvider.Anthropic,
        5 => PolishProvider.Gemini,
        _ => PolishProvider.None,
    };

    private static bool IsCloudProvider(PolishProvider provider) =>
        provider is PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini;

    private static string ProviderDisplayName(PolishProvider provider) => provider switch
    {
        PolishProvider.OpenAI => "OpenAI",
        PolishProvider.Anthropic => "Anthropic",
        PolishProvider.Gemini => "Gemini",
        _ => provider.ToString(),
    };

    private static string CredentialArticle(PolishProvider provider) => provider is
        PolishProvider.OpenAI or PolishProvider.Anthropic
            ? "An"
            : "A";

    private static bool IsCredentialStorageFailure(Exception exception) => exception is
        Win32Exception or
        UnauthorizedAccessException or
        SecurityException or
        ArgumentException;

    private static int PolishProviderIndex(PolishProvider provider) => provider switch
    {
        PolishProvider.EgOne => 1,
        PolishProvider.Ollama => 2,
        PolishProvider.OpenAI => 3,
        PolishProvider.Anthropic => 4,
        PolishProvider.Gemini => 5,
        _ => 0,
    };

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string ImportFailureMessage(PortableProfileImportStatus status) => status switch
    {
        PortableProfileImportStatus.NewerVersion => "This profile was created by a newer EnviousWispr version. The file was left untouched.",
        PortableProfileImportStatus.Invalid => "This is not a valid EnviousWispr portable profile. The file and current settings were left untouched.",
        _ => "Windows could not read this profile. The file and current settings were left untouched.",
    };

    private static DictationOverlayState OverlayStateFor(string status)
    {
        if (status.StartsWith("Recording", StringComparison.OrdinalIgnoreCase))
        {
            return DictationOverlayState.Recording;
        }

        if (status.StartsWith("Transcribing", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Delivering", StringComparison.OrdinalIgnoreCase))
        {
            return DictationOverlayState.Processing;
        }

        if (status.Contains("copied only", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Copied", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("held safely", StringComparison.OrdinalIgnoreCase))
        {
            return DictationOverlayState.Warning;
        }

        if (status.StartsWith("Local transcription failed", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Session failed", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Text delivery stopped", StringComparison.OrdinalIgnoreCase))
        {
            return DictationOverlayState.Error;
        }

        if (status.StartsWith("Inserted", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Pasted", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Transcribed", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Cleaned", StringComparison.OrdinalIgnoreCase))
        {
            return DictationOverlayState.Success;
        }

        return DictationOverlayState.Hidden;
    }

    private sealed record MicrophoneChoice(string? Id, string DisplayName);

    private sealed class HistoryItemViewModel
    {
        public HistoryItemViewModel(DictationHistoryEntry entry)
        {
            Id = entry.Id;
            Text = entry.Text;
            CreatedDisplay = entry.CreatedAt.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
            var retention = entry.ExpiresAt is null
                ? string.Empty
                : $" · Escape Recovery expires {entry.ExpiresAt.Value.ToLocalTime():g} unless kept";
            Details = $"{entry.EngineId} · {(entry.WasPolished ? "AI polished" : "deterministic cleanup")} · {(entry.WasDelivered ? "delivered" : "held safely")}{retention}";
        }

        public Guid Id { get; }

        public string Text { get; }

        public string CreatedDisplay { get; }

        public string Details { get; }
    }
}
