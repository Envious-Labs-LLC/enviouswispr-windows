using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Globalization.NumberFormatting;
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

    /// <summary>
    /// The system's "show animations" setting, read once. A user who turned animations off said so
    /// for every app, and re-reading it on every navigation would cost a COM call per page change.
    /// </summary>
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;

    /// <summary>How far a page rises as it arrives, in device-independent pixels.</summary>
    private const double PageEntranceOffsetPixels = 12;

    /// <summary>Fluent's short-duration band. Long enough to read as motion, short enough to feel instant.</summary>
    private const double PageEntranceMilliseconds = 180;

    /// <summary>The notification bar grows over slightly longer, because it also moves the page.</summary>
    private const double NotificationEntranceMilliseconds = 220;

    /// <summary>
    /// A ceiling far above any real notification. The Auto row stops at the bar's natural height,
    /// so this is not a guess at how tall a bar is - it only has to be larger than one.
    /// </summary>
    private const double NotificationEntranceCeilingPixels = 400;

    private static readonly SelectableChoiceOption[] FinalEngineChoices =
    [
        new("Automatic", "Chooses the best available local engine for this PC."),
        new("Parakeet", "Fast local English transcription with automatic hardware selection."),
        // NOT PRESENTED AS PARAKEET'S EQUAL, BECAUSE IT IS NOT YET. Six of six live takes on this rig
        // pasted sentences nobody said (#101): the engine elaborates room noise into fluent prose,
        // where Parakeet degrades into a fragment a person can see is wrong. Until that closes, the
        // card says so, and the sentence names the condition rather than the mechanism.
        new("Whisper", "Multilingual, but not yet recommended: with room noise it can add words that were never spoken."),
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

    /// <summary>Serialises every settings write this window makes.</summary>
    /// <remarks>
    /// THE RULE LIVES IN Core SO IT CAN BE PROVEN. The defect it prevents only appears while two
    /// saves overlap, which never happens in a test that drives this window one call at a time - so
    /// a gate written here would be a gate nothing could demonstrate. SerialSettingsWriterTests
    /// holds a store open mid-save and shows two changes both surviving.
    /// </remarks>
    private SerialSettingsWriter? _settingsWriter;

    /// <summary>The writer, created once and kept for the life of the window.</summary>
    private SerialSettingsWriter SettingsWriter =>
        _settingsWriter ??= new SerialSettingsWriter(_settingsStore, _settings);
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

    /// <summary>The language the pill is currently offering to pin, or null when it offers none.</summary>
    private WhisperLanguagePreference? _offeredLanguage;

    /// <summary>Notices a language being spoken over and over, and offers to pin it.</summary>
    /// <remarks>
    /// IT LIVES HERE BECAUSE THE ANSWER HAS TO BE WRITTEN DOWN, and this is where the settings and
    /// the one save path are. Owning it from the app meant the count of offers already made could be
    /// read at launch and never updated, so the promise to stop after three lasted only until the
    /// next relaunch.
    /// </remarks>
    private readonly LanguageLockSuggester _languageSuggestions;

    /// <summary>True while a pin started from the pill is still being written.</summary>
    private bool _lockingLanguage;

    // True between asking for a speed check and being handed the answer. Without it, any status
    // change arriving mid-run hands the button back and lets a second check start over the first.
    private bool _speedCheckRunning;

    // The lone modifier a keybind field has seen go down with nothing else after it. Null the
    // moment any ordinary key arrives, because from then on the modifier is qualifying that key.
    private string? _keybindModifierCandidate;
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

        // THE PICKER STARTS ON THE ORDINARY RULE BY NAME, NOT BY POSITION. Starting it on whichever
        // item happens to be listed first is the same coupling this feature just removed, wearing
        // the word "default" as a coincidence rather than as a choice.
        ResetMatchStrictness();

        EngineComboBox.ItemsSource = FinalEngineChoices;
        PolishProviderComboBox.ItemsSource = PolishProviderChoices;
        ThemeComboBox.ItemsSource = ThemeChoices;
        OverlayPositionComboBox.ItemsSource = OverlayPositionChoices;

        // Days are whole days. Without a formatter a NumberBox keeps and DISPLAYS a fraction:
        // measured on the running app, "12.7" was accepted and shown as 12.7, then stored as 12 by
        // the save path's cast. No crash and no bad state - the defect is that the control shows a
        // precision it does not honour, and the user is never told their 12.7 became 12.
        //
        // THE ROUNDER IS THE PART THAT DOES THE WORK. FractionDigits is a MINIMUM number of
        // fraction digits, not a maximum: it stops zero-padding and it does not round anything.
        // A formatter carrying only FractionDigits = 0 was measured on the running app and 12.7
        // survived both the display and the value unchanged - it compiled, it was attached, and
        // it did nothing. Vendor documentation for NumberBox pairs a NumberRounder with the
        // formatter for exactly this reason.
        foreach (var box in new[] { RetentionDaysBox, DiagnosticRetentionDaysBox })
        {
            box.NumberFormatter = new DecimalFormatter
            {
                FractionDigits = 0,
                IsDecimalPointAlwaysDisplayed = false,
                IsGrouped = false,
                NumberRounder = new IncrementNumberRounder
                {
                    Increment = 1,
                    RoundingAlgorithm = RoundingAlgorithm.RoundHalfUp,
                },
            };
        }

        // A keybind field waiting for a keystroke must not let the system-wide hook act on it.
        // Focus is what the hook needs to know, so focus is what is reported - see
        // KeybindCaptureActiveChanged.
        foreach (var box in new[] { HotkeyTextBox, CancelHotkeyTextBox, QuickAddHotkeyTextBox })
        {
            box.GotFocus += (_, _) => KeybindCaptureActiveChanged?.Invoke(true);
            box.LostFocus += (_, _) => KeybindCaptureActiveChanged?.Invoke(false);
        }

        // Subscribed HERE rather than with a KeyDown="" attribute in the markup, and the
        // difference is the whole fix. A XAML attribute subscribes with handledEventsToo:false,
        // so the handler stops being called the moment focus is on one of the cards: a focused
        // RadioButton marks the arrow handled before it reaches the list.
        //
        // The handler's last act is to focus the newly selected card, so it defeated itself -
        // exactly one arrow worked per group, then they went dead. Measured: with focus on the
        // list, Down moved selection and focus correctly; with focus on a card, Down did nothing.
        // Same key, same page, same handler, only the focused element differed.
        foreach (var list in ChoiceLists())
        {
            list.AddHandler(
                UIElement.KeyDownEvent,
                new KeyEventHandler(ChoiceListKeyDown),
                handledEventsToo: true);
        }
        _recordingSoundCoordinator = new RecordingSoundCueCoordinator(
            _recordingSoundPlayer.Play);
        RecordingSoundComboBox.ItemsSource = RecordingSoundCatalog.Choices;
        _overlayWindow = new DictationOverlayWindow();
        _historyAnnounceDebounce.Tick += OnHistoryAnnounceDue;
        _overlayWindow.ActionInvoked += OnPillActionInvoked;
        _overlayWindow.ApplyPreferences(
            settings.Preferences.OverlayPosition,
            settings.Preferences.LivePreviewEnabled,
            settings.Preferences.PillDesignWithoutWords,
            settings.Preferences.PillDesignWithWords);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // The caption glyphs follow the theme from here, and from nowhere else. Subscribing to the
        // resolved theme rather than calling this from each place a theme can change is what makes
        // it correct for the case nobody remembers: the user leaves the app on "Use Windows
        // setting" and Windows itself switches at sunset.
        ApplyCaptionButtonColors();
        WindowRoot.ActualThemeChanged += (_, _) => ApplyCaptionButtonColors();
        TryUseMicaBackdrop();
        ConfigureMinimumWindowWidth();
        ResizeToDefault();
        AppWindow.SetIcon(Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Brand",
            "EnviousWispr.ico"));
        Activated += OnWindowActivated;

        // BUILT FROM WHAT WAS WRITTEN DOWN, so somebody who has already been asked three times about
        // Spanish is not asked three more times because they restarted the app.
        _languageSuggestions = new LanguageLockSuggester(settings.LanguageOfferHistory);
        ApplyTheme(settings.Preferences.Theme);
        ApplySettingsToControls();
        ShowOnboarding(!settings.HasCompletedOnboarding);
        ProductNavigation.SelectedItem = HomeNavItem;
        BuildInfoText.Text = $"{releaseIdentity.DisplayName} {Assembly.GetExecutingAssembly().GetName().Version} · {releaseIdentity.ChannelName}";
        WhatsNewBuildInfoText.Text = BuildInfoText.Text;

        // THE MARK IS ON UNTIL THIS BUILD'S NOTES HAVE BEEN OPENED. Comparing the stored string with
        // the current one is the whole rule: a fresh install has stored nothing, so the notes are
        // new to them, which is true; an update changes the string, so the mark comes back.
        _releaseNotesIdentity = BuildInfoText.Text;
        ShowReleaseNotesMark(
            ReleaseNotesMark.IsUnread(settings.LastSeenReleaseNotes, _releaseNotesIdentity));
        SetLiveText(
            UpdateStatusText,
    updateConfigured
                ? $"Installed {releaseIdentity.ChannelName} version {currentVersion}. Updates are downloaded only while dictation is idle and must pass SHA-256 plus Envious Labs publisher verification before apply."
                : $"This {releaseIdentity.ChannelName} build has no update endpoint configured. It will not contact an update server.");
        CheckForUpdatesButton.IsEnabled = updateConfigured;
        if (settingsLoadStatus is SettingsLoadStatus.Invalid or SettingsLoadStatus.Migrated)
        {
            FoundationInfoBar.Message += " Previous settings were recovered safely.";
        }
    }

    public event Action<AppSettings>? SettingsChanged;

    /// <summary>Raised whenever the app's status changes, for surfaces outside this window.</summary>
    /// <remarks>
    /// CARRIES THE WHOLE STATUS, NOT THE SENTENCE. The tray icon is driven from this, and an icon
    /// that had to work out what was happening by reading the words would be the pill's old defect
    /// rebuilt one surface over.
    /// </remarks>
    public event Action<DictationStatus>? SessionStatusChanged;

    public event Action<AudioDeviceChange>? AudioDevicesChanged;

    public event Action? RecoveryCleared;

    /// <summary>The person asked for the speech model this build pins to be downloaded.</summary>
    public event Action? ModelDownloadRequested;

    /// <summary>The person asked for the running download to stop.</summary>
    public event Action? ModelDownloadCancelRequested;

    public event Action<bool, int>? DiagnosticsExportCompleted;

    public event Action? UpdateCheckRequested;

    public event Action? UpdateApplyRequested;

    /// <summary>Raised when the user asks for a speed check.</summary>
    public event Action? SpeedCheckRequested;

    /// <summary>Asks the app to find out what a word might be misheard as.</summary>
    /// <remarks>
    /// The window does not own the polish provider, so it asks rather than calls, exactly as the
    /// speed check does. The answer comes back through <see cref="SetAliasSuggestions"/>.
    /// </remarks>
    public event Action<string, IReadOnlyList<string>>? MishearingSuggestionsRequested;

    /// <summary>
    /// Raised true while a keybind field is waiting for a keystroke, false the moment it is not.
    /// </summary>
    /// <remarks>
    /// Pressing the recording key inside its own capture field started a real recording, because
    /// the system-wide hook sees the key before this window does and marking the routed event
    /// handled has no bearing on it. So the fix cannot live in the field's key handler at all -
    /// the hook has to be told to stand down, and the only thing this window knows is focus.
    ///
    /// Closing the window raises false as well. A capture field that still held focus when the
    /// window went away would otherwise leave the hook standing down with no field on screen to
    /// clear it, and dictation would stop working with nothing on screen to explain it.
    /// </remarks>
    public event Action<bool>? KeybindCaptureActiveChanged;

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
        // One shortcut phrase, used on screen and in the tray, so the two cannot drift apart.
        var shortcut = recordingMode == DictationRecordingMode.PushToTalk
            ? $"Hold {gesture}"
            : $"Toggle with {gesture}";
        HotkeyStatusText.Text = shortcut;
        OnboardingHotkeyText.Text = $"{instruction} Cancel with {cancelGesture}. Add a selected word with {quickAddGesture}.";
        SetLiveText(SessionStatusText, "Idle");
        // A full stop rather than a middle dot. The tray tooltip is a SENTENCE - it has no
        // layout to separate, and a screen reader either announces a middle dot literally or
        // drops it. Text that is laid out on screen may still use one; this is not.
        SessionStatusChanged?.Invoke(DictationStatus.Quiet($"ready. {shortcut}"));
    }

    public void SetHotkeyUnavailable(string status)
    {
        HotkeyStatusText.Text = status;
        OnboardingHotkeyText.Text = status;
        SetLiveText(SessionStatusText, "Unavailable");
        // AN ERROR FOR THE TRAY, WHICH IS THE ONLY PLACE IT SHOWS. The recording key not being
        // available means the app cannot be used at all, and unlike a pill the icon stays. It
        // raises no pill, because SetSessionStatus is not on this path.
        SessionStatusChanged?.Invoke(DictationStatus.Error("shortcut unavailable"));
    }

    public void SetSessionStatus(DictationStatus status)
    {
        var sentence = status.Text;
        SetLiveText(SessionStatusText, sentence);
        SessionStatusChanged?.Invoke(status);
        var overlayState = status.State;
        HandleRecordingSoundTransition(overlayState);
        if (overlayState == DictationOverlayState.Recording)
        {
            // A DICTATION OUTRANKS A TEST, ALWAYS. Somebody who presses their record key wants to
            // dictate, and a test still holding the device would either fail their recording or be
            // failed by it.
            CancelMicrophoneTest();
        }

        _currentOverlayState = overlayState;
        PreviewRecordingSoundButton.IsEnabled = overlayState != DictationOverlayState.Recording;
        SetSpeedCheckAvailability(overlayState != DictationOverlayState.Recording);
        _overlayWindow.ShowState(status);
        // THE STATUS SAYS WHETHER IT BELONGS HERE; THIS NO LONGER GUESSES FROM THE WORDS. What stood
        // here tested the sentence for "ready", "model is not installed", "transcription is
        // unavailable" and "worker could not start", which is the same mistake `OverlayStateFor` was
        // deleted for one surface over. It let four unrelated messages overwrite the Transcription
        // card - a Windows resume, a finished Escape Recovery, and both Ollama health lines - and it
        // would have dropped any new engine sentence that did not happen to contain one of the four
        // phrases. `AboutTheTranscriptionEngine` carries the answer from the call site instead.
        if (status.DescribesTranscriptionEngine)
        {
            EngineReadinessText.Text = sentence;
            OnboardingModelText.Text = sentence;
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
        SetLiveText(UpdateStatusText, "Checking the isolated signed update channel and staging any newer version…");
    }

    public void SetUpdateStatus(UpdateOperationResult result)
    {
        CheckForUpdatesButton.IsEnabled = result.Status is not UpdateOperationStatus.NotConfigured;
        ApplyUpdateButton.IsEnabled = result.CanApply;
        SetLiveText(
            UpdateStatusText,
    result.Status switch
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
            });
    }

    public void SetCloudPolishNotice(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
        {
            return;
        }

        FoundationInfoBar.Title = "Cloud polish is on, using your own key";
        FoundationInfoBar.Message = notice;
    }

    public void SetOllamaPolishNotice(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
        {
            return;
        }

        FoundationInfoBar.Title = "Ollama polish is on, running on this PC";
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

    /// <summary>
    /// Opens settings at the first section rather than at a page listing every section.
    /// </summary>
    /// <remarks>
    /// There used to be an "All Settings" destination that rendered every section at once. It was
    /// 4076 pixels tall on a 2160-pixel screen, it had no in-page anchors, and every control on it
    /// was one click away in the sidebar - so it duplicated rather than summarised, and it was the
    /// page a measured audit called the clunkiest in the app.
    ///
    /// Removed on PARITY grounds rather than taste: macOS ships fifteen settings sections and no
    /// aggregate page. That was a lookup rather than a judgement call, and it had been sitting in
    /// the founder's queue as a decision until somebody read the other side.
    /// </remarks>
    /// <summary>Answers a press on the pill's button by showing the page that fixes it.</summary>
    /// <remarks>
    /// THE INTENT IS TRANSLATED HERE AND NOWHERE ELSE. The overlay's vocabulary lives in Core and
    /// the pages live in this file's markup, so a page tag travelling through Core would make the
    /// pill depend on the spelling of a navigation row.
    ///
    /// The default arm exists because an enum can hold a value nobody declared, and it lands on
    /// Appearance rather than throwing: a button that opens the wrong settings page is a bad day,
    /// and a button that crashes the app during someone's dictation is a worse one. A gate holds
    /// this switch to naming every declared member, so the default is genuinely unreachable.
    /// </remarks>
    /// <summary>Shows where the words went, and asks about the language if this is the moment to.</summary>
    /// <remarks>
    /// ONE ENTRY POINT SO THE PILL CANNOT BE SET TWICE. The offer takes the pill INSTEAD of the
    /// delivery sentence rather than after it, and it carries that sentence along, so somebody who
    /// asked for the clipboard is still told the text went to the clipboard.
    /// </remarks>
    public void ReportDeliveryAndMaybeOfferLanguage(DictationStatus delivered, string? detectedLanguage)
    {
        var offer = delivered.State == DictationOverlayState.Success
            ? _languageSuggestions.Observe(
                detectedLanguage,
                _settings.Preferences.Dictation.WhisperLanguage)
            : null;
        if (offer is null)
        {
            SetSessionStatus(delivered);
            return;
        }

        ShowLanguageLockOffer(offer, delivered.Text);

        // WRITTEN DOWN THE MOMENT IT IS SHOWN, not when it is answered. Most offers are never
        // answered at all - the pill lapses - and that is exactly the case the count exists to
        // limit, so waiting for an answer would record only the offers that did not need recording.
        _ = RememberLanguageOffersAsync();
    }

    private async Task RememberLanguageOffersAsync()
    {
        var history = _languageSuggestions.OfferHistory;
        if (string.Equals(_settings.LanguageOfferHistory, history, StringComparison.Ordinal))
        {
            return;
        }

        // NO NOTICE ON FAILURE, DELIBERATELY. Nobody asked for this write and nobody is waiting on
        // it; the worst a lost one costs is being asked about a language once more than intended,
        // which is quieter than an error about bookkeeping over somebody's work.
        await UpdateSettingsAsync(current => current with { LanguageOfferHistory = history })
            .ConfigureAwait(true);
    }

    /// <summary>Offers to pin the language the app keeps hearing.</summary>
    /// <remarks>
    /// THE OFFERED LANGUAGE IS REMEMBERED HERE RATHER THAN CARRIED ON THE BUTTON, because the pill's
    /// vocabulary names intents and a language code in it would tie the overlay to the list of
    /// languages the settings page happens to hold.
    /// </remarks>
    private void ShowLanguageLockOffer(LanguageLockOffer offer, string deliverySentence)
    {
        ArgumentNullException.ThrowIfNull(offer);
        _offeredLanguage = offer.Language;
        var (question, action) = offer.Kind == LanguageOfferKind.AskToLock
            ? ($"You keep speaking {offer.DisplayName}. Pin it so recognition stops guessing?",
                new PillAction(
                    $"Use {offer.DisplayName}",
                    PillActionKind.LockDetectedLanguage,
                    $"Always use {offer.DisplayName} for speech recognition"))
            : ($"You keep speaking {offer.DisplayName}. Pin it under Transcription whenever you like.",
                new PillAction(
                    "Open settings",
                    PillActionKind.OpenTranscriptionSettings,
                    "Open transcription settings"));

        // THE OFFER TAKES THE PILL, SO IT HAS TO CARRY WHAT THE PILL WOULD HAVE SAID. Where the
        // words went is not always the same sentence: somebody using the clipboard setting was told
        // "Copied to your clipboard", and replacing that with a question about languages leaves them
        // believing the text was inserted into the window in front of them.
        SetSessionStatus(DictationStatus.Suggestion(
            string.IsNullOrWhiteSpace(deliverySentence) ? question : $"{deliverySentence}. {question}",
            action));
    }

    /// <summary>Does the one thing a pill button promised.</summary>
    /// <remarks>
    /// ONE SWITCH WITH LABELLED CASES, AND THE SHAPE IS WHAT THE GATE CAN READ. An arrangement that
    /// answered one action with an early return and the rest from an expression left the gate reading
    /// the whole method for any mention of a name - so an action named only in a comment counted as
    /// answered while still falling through to the default page. A case label cannot be written by
    /// accident and cannot be a comment.
    ///
    /// Called directly rather than through the dispatcher. The overlay is created in this window's
    /// constructor, so its click handler already runs on this thread, and TryEnqueue returns a bool
    /// nobody reads - a silent way for a button press to go nowhere.
    /// </remarks>
    private void OnPillActionInvoked(PillActionKind kind)
    {
        switch (kind)
        {
            case PillActionKind.OpenPolishSettings:
                OpenPage("settings-ai-polish");
                break;
            case PillActionKind.OpenTranscriptionSettings:
                OpenPage("settings-transcription");
                break;

            // NOT NAVIGATION. Sending it to a page would open the settings and leave the thing the
            // button said it would do undone.
            case PillActionKind.LockDetectedLanguage:
                LockOfferedLanguage();
                break;

            // An enum can hold a value nobody declared. Appearance is the harmless page: it changes
            // nothing and shows the person something they can read.
            default:
                OpenPage("settings-appearance");
                break;
        }
    }

    /// <summary>Saves the language the pill offered, and tells the app it was taken.</summary>
    /// <remarks>
    /// THE OFFER IS HELD UNTIL THE SAVE SUCCEEDS. Clearing it first meant a save that failed left
    /// nothing: the pill was already gone, the setting was unchanged, and the only report was an
    /// InfoBar inside a window that is usually not on screen when somebody dictates. A guard rather
    /// than the cleared field is what stops a second press starting a second save.
    /// </remarks>
    private async void LockOfferedLanguage()
    {
        var language = _offeredLanguage;
        if (language is null || _lockingLanguage)
        {
            return;
        }

        _lockingLanguage = true;
        try
        {
            await LockLanguageAsync(language.Value).ConfigureAwait(true);
        }
        finally
        {
            _lockingLanguage = false;
        }
    }

    private async Task LockLanguageAsync(WhisperLanguagePreference language)
    {
        if (await TrySaveAsync(
            current => current with
            {
                Preferences = current.Preferences with
                {
                    Dictation = current.Preferences.Dictation with { WhisperLanguage = language },
                },
            },
            "Language pinned",
            $"Recognition will use {LanguageLockSuggester.DisplayName(language)}.").ConfigureAwait(true))
        {
            _offeredLanguage = null;
            _languageSuggestions.Accepted(language);
            await RememberLanguageOffersAsync().ConfigureAwait(true);
            return;
        }

        // THE PILL SAYS SO, BECAUSE THE PILL IS WHERE THEY WERE LOOKING. A settings page notice is
        // the right place for a save somebody started on the settings page; this one started on an
        // overlay over their work, and a button that appears to do nothing is worse than no button.
        SetSessionStatus(DictationStatus.Warning(
            $"{LanguageLockSuggester.DisplayName(language)} could not be pinned. Open Transcription "
                + "settings to set it there."));
    }

    /// <summary>Brings the window forward and shows one page by its tag.</summary>
    public void OpenPage(string tag)
    {
        AppWindow.Show();
        Activate();
        ShowOnboarding(show: false);
        var row = NavigationRows()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
        if (row is null)
        {
            // A SILENT RETURN HERE IS A BUTTON THAT DOES NOTHING. The user pressed something,
            // the pill went away, and the window came forward showing whatever it was showing -
            // which reads as the app ignoring them. The page name is deliberately not in the
            // message: it is our vocabulary, not theirs.
            SetReliabilityNotice(
                "That settings page could not be opened",
                "Use the sidebar to find it. Nothing about your dictation was affected.",
                isError: true);
            return;
        }

        // Selecting the row raises SelectionChanged, which is the one path that shows a page.
        // Calling ShowPage as well would leave the sidebar and the content able to disagree.
        ProductNavigation.SelectedItem = row;
    }

    public void OpenSettings()
    {
        ShowOnboarding(show: false);
        ProductNavigation.SelectedItem = AppearanceNavItem;
        ShowPage("settings-appearance");
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
        // DETACHED, NOT JUST STOPPED. A timer left attached can still tick during shutdown and raise
        // an announcement for a window that is going away.
        _historyAnnounceDebounce.Stop();
        _historyAnnounceDebounce.Tick -= OnHistoryAnnounceDue;
        // THE WRITER IS NOT DISPOSED HERE, AND THAT IS DELIBERATE. Shutdown is synchronous and a
        // save may still be inside it; disposing its gate makes the release throw, and clearing the
        // field makes the continuation dereference null. A semaphore that outlives the window costs
        // nothing, and a settings write that completes during shutdown is the outcome we want.
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

    /// <summary>Says a dictation was lost, and only when one actually was.</summary>
    /// <remarks>
    /// THIS REPLACES A BANNER THAT ACCUSED THE PRODUCT ON A NUMBER IT COULD NOT JUSTIFY. Home used
    /// to read "EnviousWispr did not close properly last time" plus "That has now happened N times
    /// in a row" whenever the previous run left no clean-exit flag. Nothing in this app can tell a
    /// fault from a closed laptop, a Restart chosen from the Start menu, a log off or Task Manager,
    /// so the tally was not evidence of anything, and on the test machine it reached nineteen with
    /// almost all of it a build script releasing a file lock.
    ///
    /// AND YET SOMETHING HAD TO STAY, WHICH IS THE HALF THAT NEARLY GOT LOST. Recovery text is
    /// written only after transcription finishes, so a stop DURING a dictation leaves nothing to
    /// restore and reads exactly like an idle restart. That is the one case where a person must be
    /// told, because their words are gone and only they can say them again. StartupNoticeDecision
    /// separates the two, and this is only ever raised for the second.
    ///
    /// NO COUNT, AND NO BLAME. It says what happened to their dictation and what they may have to
    /// do, which is the whole of what they can act on.
    /// </remarks>
    public void SetPossiblyLostDictationNotice()
    {
        const string title = "A dictation may not have finished";
        const string message =
            "EnviousWispr stopped while a dictation was in progress, and there was nothing saved to "
            + "restore. You may need to say it again.";
        FoundationInfoBar.Title = title;
        FoundationInfoBar.Message = message;
        FoundationInfoBar.Severity = InfoBarSeverity.Warning;
        SetOnboardingReliabilityNotice(title, message);
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

        if (await TrySaveAsync(
                current => current with { HasCompletedOnboarding = true },
                "Setup complete",
                "Your choices were saved on this PC.").ConfigureAwait(true))
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

    /// <summary>Fills the recording keybind with the binding that unlocks the four gestures.</summary>
    /// <remarks>
    /// IT FILLS THE FIELD RATHER THAN SAVING. Everything a typed keybind goes through - the parse,
    /// the conflict check against the other two binds, the Save button - happens the same way, so
    /// this cannot be the one route into the setting that skips the checks. It also leaves the person
    /// looking at what they are about to agree to.
    /// </remarks>
    private void HandsFreeGestureButton_Click(object sender, RoutedEventArgs e)
    {
        HotkeyTextBox.Text = HandsFreeRecordBinding.Suggested;
        HotkeyTextBox.Focus(FocusState.Programmatic);
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

        var clashes = HotkeyConflictDetector.Find(KeybindFields()
            .Select(field => (field.Role, field.Box.Text))
            .ToArray());
        if (clashes.Count > 0)
        {
            ShowMessage("Shortcuts overlap", HotkeyConflictDetector.Describe(clashes), InfoBarSeverity.Error);
            RefreshKeybindConflicts();
            return;
        }

        // An empty field means the default rather than zero. A zero here would be clamped up by
        // the policy anyway, but storing it would show the user a threshold they never chose.
        var autoStopSeconds = double.IsNaN(AutoStopSecondsBox.Value)
            ? DictationPreferences.Default.AutoStopSilenceSeconds
            : AutoStopSecondsBox.Value;
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
            parsedQuickAddHotkey.Gesture!.Value.ToString(),
            AutoStopToggle.IsOn,
            autoStopSeconds);
        var polish = new PolishPreferences(
            PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)),
            NullIfBlank(PolishModelTextBox.Text),
            NullIfBlank(OllamaEndpointTextBox.Text));
        var history = new HistoryPreferences(
            HistoryEnabledToggle.IsOn,
            RetentionDays.FromField(
                RetentionDaysBox.Value,
                fallback: 30,
                RetentionDays.HistoryMinimum,
                RetentionDays.HistoryMaximum));
        var theme = ThemeFromIndex(SelectedIndexOf(ThemeChoices));
        var observability = new ObservabilityPreferences(
            LocalDiagnosticsToggle.IsOn,
            RetentionDays.FromField(
                DiagnosticRetentionDaysBox.Value,
                ObservabilityPreferences.Default.DiagnosticRetentionDays,
                RetentionDays.DiagnosticMinimum,
                RetentionDays.DiagnosticMaximum),
            _telemetryAvailable && ShareTelemetryToggle.IsOn);
        var microphoneId = (MicrophoneComboBox.SelectedItem as MicrophoneChoice)?.Id;

        // THE CONTROL VALUES ARE READ HERE, ON THE UI THREAD, AND APPLIED INSIDE THE GATE. Reading
        // them is what has to happen now; building the whole record now is what made a save that
        // waited on another writer overwrite it. The fields above are already captured, so the
        // transform below touches nothing that can have moved.
        var preferences = new UserPreferences(
            dictation,
            polish,
            history,
            theme,
            LivePreviewToggle.IsOn,
            OverlayPositionFromIndex(SelectedIndexOf(OverlayPositionChoices)),
            PillDesignWithoutWordsFromControls(),
            RecordingPillDesign.ReadingWell,
            PlayRecordingSoundsToggle.IsOn,
            SelectedRecordingSoundPairing(),
            CopyInsteadOfPasteToggle.IsOn);

        if (await TrySaveAsync(
                current => current with
                {
                    PreferredMicrophoneId = microphoneId,
                    Preferences = preferences,
                    Observability = observability,
                },
                "Settings saved",
                "Theme, Live Preview, pill design, pill position, recording sounds, and local data choices apply now. Engine, microphone, shortcut, and polish changes apply safely on the next launch.")
            .ConfigureAwait(true))
        {
            ApplyTheme(theme);
        }
    }

    /// <summary>Applies a theme the moment it is chosen, and keeps it.</summary>
    /// <remarks>
    /// THIS USED TO APPLY WITHOUT SAVING, WHICH IS THE WORST OF THE TWO WAYS TO GET IT WRONG. The
    /// window went light instantly, which is the strongest possible signal to a user that their
    /// choice took, and then it came back as System on the next launch. Every other unsaved-settings
    /// bug at least announces itself by doing nothing.
    /// It was invisible to tests because both halves worked: saving a theme round-tripped, and
    /// choosing a theme repainted the window. Nothing connected them, and a gap between two passing
    /// tests is not a failure inside either.
    /// </remarks>
    private async void ThemeCardChecked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        ApplyTheme(ThemeFromIndex(SelectedIndexOf(ThemeChoices)));
        await PersistAppearanceChoicesAsync().ConfigureAwait(true);
    }

    /// <summary>Keeps an Appearance choice that has already taken effect on screen.</summary>
    private async void AppearanceCardChecked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        await PersistAppearanceChoicesAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Writes the Appearance choices, and nothing else.
    /// </summary>
    /// <remarks>
    /// ONLY THESE TWO FIELDS, DELIBERATELY. The Save button builds a whole settings object out of
    /// every control in the window, which is right for a button the user pressed and wrong for a
    /// side effect of clicking a theme card: it would commit half-finished edits sitting on other
    /// pages that the user has not chosen to save yet.
    ///
    /// SAVED QUIETLY, because the user already has their confirmation - the window changed colour.
    /// A success banner for something they can plainly see happened is noise. A FAILURE still
    /// speaks, because that is the case where the screen and the file disagree, which is exactly
    /// the state this whole change exists to prevent.
    ///
    /// Appearance is the only settings page with no Save button, so persisting on selection is what
    /// makes its settings behave like the other ten pages' rather than an exception. The
    /// alternative - adding a Save button - would mean asking a user to confirm a change they can
    /// already see, which reads as the app not trusting its own preview.
    /// </remarks>
    private async Task PersistAppearanceChoicesAsync()
    {
        // DERIVED INSIDE THE GATE, so a save that overlaps another writer builds on what is actually
        // stored rather than on a snapshot taken before the wait.
        await UpdateSettingsAsync(
            current => current with
            {
                Preferences = current.Preferences with
                {
                    Theme = ThemeFromIndex(SelectedIndexOf(ThemeChoices)),
                    OverlayPosition = OverlayPositionFromIndex(SelectedIndexOf(OverlayPositionChoices)),

                    // THE PILL'S LOOK JOINED THIS PAGE AND HAD TO JOIN THIS WRITE. Appearance is the
                    // one settings page with no Save button, so a card that only the Save button
                    // reads is a card that does nothing: somebody picks a design, sees the preview,
                    // walks away, and it is gone. Moving the cards here without moving them into
                    // this list would have been a change that ships and does nothing.
                    PillDesignWithoutWords = PillDesignWithoutWordsFromControls(),
                },
            },
            _ =>
            {
                ShowMessage(
                    "That choice was not kept",
                    "It is applied for now, but the app will start with your previous appearance until "
                        + "settings can be written again.",
                    InfoBarSeverity.Warning);
                return Task.CompletedTask;
            }).ConfigureAwait(true);
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

        // A DISTINCT IN-PROGRESS LINE, SO A RETRY IS AUDIBLE. The setter only announces when the
        // words change, which is right - but a refresh that fails the same way twice then produces
        // the same sentence twice and says nothing the second time, leaving somebody who pressed the
        // button with no confirmation it did anything. Saying "refreshing" first guarantees the
        // result is always a change from what preceded it.
        SetLiveText(ApiKeyStatusText, "Refreshing the list of available models...");
        await RefreshPolishModelChoicesAsync(provider, chooseDefault: false)
            .ConfigureAwait(true);
    }

    /// <summary>How long a microphone test listens for.</summary>
    /// <remarks>
    /// LONG ENOUGH TO SAY SOMETHING AND SHORT ENOUGH THAT NOBODY WAITS. Three seconds is about one
    /// sentence, which is what somebody naturally does when a button says to speak.
    /// </remarks>
    private static readonly TimeSpan MicrophoneTestDuration = TimeSpan.FromSeconds(3);

    private bool _microphoneTestRunning;

    /// <summary>Cancels a microphone test that a recording, a page change or shutdown has overtaken.</summary>
    private CancellationTokenSource? _microphoneTest;

    /// <summary>Which test a queued meter update belongs to.</summary>
    /// <remarks>
    /// AN UPDATE ALREADY ON THE QUEUE OUTLIVES THE UNSUBSCRIBE. Removing the handler stops new ones
    /// being posted and does nothing about the ones already waiting, so a late update could relight
    /// the meter after the test had finished and cleared it, or during the next one. A generation
    /// read inside the callback is the only thing that can refuse a message that is already in
    /// flight.
    /// </remarks>
    private int _microphoneTestGeneration;

    /// <summary>Opens the microphone for a moment and shows what actually arrives.</summary>
    /// <remarks>
    /// THIS IS THE PAGE WHERE SOMEBODY CONFIRMS THEIR MICROPHONE WORKS, AND IT COULD NOT TELL THEM.
    /// It named a device and stopped, so an app receiving pure digital silence looked exactly like
    /// one that was working. That is not hypothetical: it happened on the development machine, the
    /// meter sat at its floor for seventy frames, nothing transcribed, and it took a day of measuring
    /// to find. A person would have seen it here in three seconds.
    ///
    /// IT SAYS WHICH KIND OF NOTHING. A device that could not be opened, a device that opened and
    /// delivered packets Windows marked as deliberately silent, and a device that delivered real
    /// packets of zeroes are three different faults with three different answers, and a bare "no
    /// sound" sends somebody to look in the wrong place for all three.
    /// </remarks>
    private async void MicrophoneTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_microphoneTestRunning)
        {
            return;
        }

        // A TEST AND A DICTATION MUST NOT BOTH OPEN THE MICROPHONE. The button guard only stopped a
        // second press; nothing stopped somebody pressing the record key while a test held the
        // device. Refusing here and cancelling from the other side is the pair that closes it.
        if (_currentOverlayState == DictationOverlayState.Recording)
        {
            SetLiveText(
                MicrophoneTestResultText,
                "A recording is running. Finish it, then test the microphone.");
            return;
        }

        _microphoneTestRunning = true;
        _microphoneTestGeneration++;
        using var cancellation = new CancellationTokenSource();
        _microphoneTest = cancellation;
        MicrophoneTestButton.IsEnabled = false;
        try
        {
            await RunMicrophoneTestAsync(cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            SetLiveText(MicrophoneTestResultText, "Microphone test stopped.");
        }
        finally
        {
            _microphoneTest = null;
            _microphoneTestRunning = false;
            _microphoneTestGeneration++;
            MicrophoneTestButton.IsEnabled = true;
            DrawMicrophoneTestLevel(0f);
        }
    }

    /// <summary>Stops a microphone test, because something with a better claim wants the device.</summary>
    private void CancelMicrophoneTest()
    {
        try
        {
            _microphoneTest?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The test finished between the read and the call, which is the outcome asked for.
        }
    }

    private async Task RunMicrophoneTestAsync(CancellationToken cancellationToken)
    {
        SetLiveText(MicrophoneTestResultText, "Listening. Say a few words.");
        await using var capture = new WasapiAudioCapture();
        var generation = _microphoneTestGeneration;
        // ONE POST PER METER FRAME, NOT ONE PER AUDIO PACKET, AND THAT WAS THE WHOLE BUG.
        // Capture reports a level per audio buffer, about two hundred times a second, and every one
        // of them was posting its own callback to the UI thread. Layout and render run on that same
        // dispatcher queue, so a flood at that rate keeps it permanently busy: measured on this
        // machine as five hundred and ninety-eight draws that each assigned height, opacity and
        // brush to the correct live Border, against a camera that recorded no change in any of the
        // three across the entire test. Nothing was ignored and nothing was reverted. No frame was
        // ever produced. The tell was that the verdict sentence, the one thing on that page written
        // AFTER the flood stops, was also the only thing that ever appeared.
        //
        // FIFTY MILLISECONDS IS THE RATE THE PILL'S RAIL ALREADY USES, for the same reason, and it
        // is already written down as RecordingLevelHistory.SampleInterval. This meter joins that
        // answer rather than inventing a second one.
        //
        // AND IT KEEPS THE LOUDEST OF EACH FRAME RATHER THAN THE FIRST. Taking the first level after
        // each boundary chooses at random with respect to loudness, so the attack of a consonant -
        // which is the thing somebody watches a meter for - disappears whenever it lands mid-frame.
        var meterClock = System.Diagnostics.Stopwatch.StartNew();
        var meterFrames = new MicrophoneMeterFrameSampler();
        capture.LevelChanged += OnLevel;
        try
        {
            // THE TOKEN GOES ALL THE WAY IN. A recording cancels a running test, and a test that
            // does not forward its own cancellation would keep opening a device the app has already
            // decided somebody else should have.
            var started = await capture
                .StartAsync(
                    new AudioCaptureRequest(
                        DictationSessionId.Create(),
                        (MicrophoneComboBox.SelectedItem as MicrophoneChoice)?.Id is { } id
                            ? new AudioDeviceId(id)
                            : null),
                    cancellationToken)
                .ConfigureAwait(true);
            if (!started.Succeeded)
            {
                SetLiveText(
                    MicrophoneTestResultText,
                    "Windows would not open that microphone. Check it is plugged in and that "
                        + "microphone privacy allows desktop apps.");
                return;
            }

            await Task.Delay(MicrophoneTestDuration, cancellationToken).ConfigureAwait(true);
            // STOPPING IS NOT CANCELLED, DELIBERATELY, AND IT IS THE ONE EXCEPTION. A cancelled stop
            // leaves the device open, which is the opposite of what a cancel is for: the whole reason
            // a recording cancels a test is to take the microphone back.
            var captured = await capture.StopAsync(CancellationToken.None).ConfigureAwait(true);

            // WHAT THE STOP SAID, BEFORE WHAT THE PACKETS SAID. A device that vanished after one loud
            // packet leaves counts that read as healthy, so throwing away the outcome let an
            // interrupted test report a working microphone.
            if (captured.Outcome != AudioCaptureOutcome.Completed)
            {
                SetLiveText(
                    MicrophoneTestResultText,
                    "The microphone stopped part way through the test. It may have been unplugged, "
                        + "or taken by another app.");
                return;
            }

            // THE ROOT-MEAN-SQUARE, NOT THE PEAK, because that is the number the recording meter is
            // driven from. A verdict read off the peak could call a microphone healthy while the
            // meter it is meant to explain sits flat.
            SetLiveText(MicrophoneTestResultText, MicrophoneTestVerdict.For(
                capture.LastPacketCount,
                capture.LastSilentPacketCount,
                capture.LastRootMeanSquare));
        }
        finally
        {
            capture.LevelChanged -= OnLevel;
        }

        void OnLevel(object? sender, AudioLevel level)
        {
            if (!meterFrames.TryTakeFrame(level.RootMeanSquare, meterClock.Elapsed, out var loudest))
            {
                return;
            }

            var normalized = RecordingLevelHistory.Normalize(loudest);
            MicrophoneTestBars.DispatcherQueue.TryEnqueue(() =>
            {
                if (generation != _microphoneTestGeneration)
                {
                    return;
                }

                DrawMicrophoneTestLevel(normalized);
            });
        }
    }

    private void DrawMicrophoneTestLevel(float level)
    {
        var lit = (int)Math.Round(level * MicrophoneTestBars.Children.Count);
        for (var index = 0; index < MicrophoneTestBars.Children.Count; index++)
        {
            if (MicrophoneTestBars.Children[index] is not Border bar)
            {
                continue;
            }

            var on = index < lit;
            bar.Height = on ? 22 : 6;
            bar.Opacity = on ? 1 : 0.25;

            // COLOUR AS WELL AS HEIGHT, so the row reads at a glance rather than only under
            // comparison. A lit segment differs from an unlit one in three ways at once - taller,
            // fully opaque, and accent rather than grey - which is what makes a single lit bar
            // legible on a quiet microphone.
            bar.Background = on
                ? (Brush)Application.Current.Resources["BrandAccentSolidBrush"]
                : (Brush)Application.Current.Resources["BrandTextSecondaryBrush"];
        }
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

    /// <summary>
    /// Asks what speech recognition is likely to hear instead of the word being taught.
    /// </summary>
    /// <remarks>
    /// THE WORD IT ASKS ABOUT IS THE ONE ON THE RIGHT, NOT THE LEFT. The left field is what the
    /// recogniser produces and the right one is what should be written instead, so the thing the
    /// model is being asked about is the CORRECT spelling, and every answer is a candidate for the
    /// left field. Getting this the wrong way round would ask a model for mishearings of a
    /// mishearing, which is both useless and completely plausible on screen.
    ///
    /// The aliases already pointing at this same word are sent along, so the model does not spend
    /// its five answers on ones the user already has.
    /// </remarks>
    private void SuggestAliasesButton_Click(object sender, RoutedEventArgs e)
    {
        var term = ReplacementBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            ShowMessage(
                "Type the word first",
                "Put the correct spelling in the Write field, and this will suggest what speech "
                    + "recognition might hear instead.",
                InfoBarSeverity.Informational);
            return;
        }

        var existing = _settings.UserData.CustomWords
            .Where(entry => string.Equals(entry.Replacement, term, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.SpokenForm)
            .ToArray();

        SuggestAliasesButton.IsEnabled = false;
        SuggestedAliasesPanel.Children.Clear();
        SuggestedAliasesScroller.Visibility = Visibility.Collapsed;
        SetLiveRegion(SuggestAliasesStatusText, "Asking...", Visibility.Visible);
        MishearingSuggestionsRequested?.Invoke(term, existing);
    }

    /// <summary>Shows what came back, or says why nothing did.</summary>
    /// <remarks>
    /// EVERY OUTCOME GETS ITS OWN SENTENCE, because the user's next move differs in each. "The
    /// model had no ideas" means try a different word; "it did not answer" means check the
    /// connection and press again; "this option cannot do this" means change the polish choice.
    /// Collapsing them into one empty list is what makes a feature feel broken.
    ///
    /// A SUGGESTION IS A BUTTON, NOT A ROW THAT HAS ALREADY BEEN ADDED. Nothing reaches the user's
    /// word list until they click one. A wrong alias added quietly is a correction that fires on
    /// words they never said, and nothing on screen would connect it back to a suggestion.
    /// </remarks>
    public void SetAliasSuggestions(string term, MishearingAdvice advice)
    {
        ArgumentNullException.ThrowIfNull(advice);
        SuggestAliasesButton.IsEnabled = true;
        SuggestedAliasesPanel.Children.Clear();
        // Shown by the SetLiveRegion call below, which sets text and visibility together.

        if (advice.Status != MishearingAdviceStatus.Suggested)
        {
            SuggestedAliasesScroller.Visibility = Visibility.Collapsed;
            SetLiveText(
                SuggestAliasesStatusText,
    advice.Status switch
                {
                    MishearingAdviceStatus.NothingUsable =>
                        "No likely mishearings came back for that word. Add one yourself when you hear it.",
                    MishearingAdviceStatus.NotSupported =>
                        "The polish option you have chosen cannot answer this. Pick the built-in model, "
                            + "Ollama, or a cloud provider to use suggestions.",
                    _ => "The suggestion did not come back. Check your polish settings and try again.",
            
                });
            return;
        }

        SetLiveText(SuggestAliasesStatusText, "Click one to add it. Nothing is saved until you do.");
        foreach (var suggestion in advice.Suggestions)
        {
            var candidate = suggestion;
            var button = new Button
            {
                Content = candidate,
                Style = (Style)Application.Current.Resources["BrandQuietButtonStyle"],
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button, $"Add {candidate} as a mishearing of {term}");
            button.Click += async (_, _) => await AddSuggestedAliasAsync(candidate, term)
                .ConfigureAwait(true);
            SuggestedAliasesPanel.Children.Add(button);
        }

        SuggestedAliasesScroller.Visibility = Visibility.Visible;
    }

    /// <summary>Saves one accepted suggestion, and takes it off the screen.</summary>
    /// <remarks>
    /// The chip is removed once it is saved, so the panel shows only what is still on offer. Leaving
    /// an accepted one in place invites a second click that would do nothing visible, which reads as
    /// the button being broken.
    /// </remarks>
    private async Task AddSuggestedAliasAsync(string spokenForm, string replacement)
    {
        if (!await SaveCustomWordFromPickerAsync(spokenForm, replacement).ConfigureAwait(true))
        {
            // THE CHIP STAYS WHEN THE SAVE DID NOT. Removing it says the suggestion was taken, and
            // the panel then shows a shorter list of offers than the user actually still has - so
            // the one thing they could do about the failure disappears along with the failure.
            return;
        }

        ResetMatchStrictness();

        var accepted = SuggestedAliasesPanel.Children
            .OfType<Button>()
            .FirstOrDefault(button => Equals(button.Content, spokenForm));
        if (accepted is not null)
        {
            SuggestedAliasesPanel.Children.Remove(accepted);
        }

        if (SuggestedAliasesPanel.Children.Count == 0)
        {
            SuggestedAliasesScroller.Visibility = Visibility.Collapsed;
            SetLiveText(SuggestAliasesStatusText, "All of them added.");
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

        // CLEARED ONLY IF IT WAS SAVED. Emptying the boxes after a refused save throws away what
        // the person typed and leaves them looking at an error with nothing to retry.
        if (!await SaveCustomWordFromPickerAsync(spoken, replacement).ConfigureAwait(true))
        {
            return;
        }

        SpokenFormBox.Text = string.Empty;
        ReplacementBox.Text = string.Empty;
        ResetMatchStrictness();
    }

    /// <summary>Saves one word under whatever the picker currently says, replacing any twin.</summary>
    /// <remarks>
    /// ONE DOOR, BECAUSE THERE ARE TWO WAYS TO ADD A WORD. Suggested mishearings are accepted by
    /// their own button, and building the entry separately there meant a person could set Loose,
    /// press a suggestion, and get a word under the ordinary rule with the picker still saying Loose
    /// in front of them. Reading the picker inside the one method that saves is what makes the
    /// second path unable to forget - and it is what a gate can check, because it is the only place
    /// in the app allowed to build one of these.
    /// </remarks>
    private Task<bool> SaveCustomWordFromPickerAsync(string spokenForm, string replacement)
    {
        return SaveUserDataAsync(
            data => new ReusableUserData(
                data.CustomWords
                    .Where(entry => !string.Equals(entry.SpokenForm, spokenForm, StringComparison.OrdinalIgnoreCase))
                    // THE PICKER CARRIES THE ANSWER ITSELF, not a position that has to be looked up.
                    // A mapping from index to meaning is a contract between this file and the ORDER
                    // of three strings in the markup, and reordering those strings changes what
                    // people choose while every test stays green. Each choice now holds its own
                    // value, so there is nothing left to keep in step.
                    .Append(new CustomWordEntry(
                        spokenForm,
                        replacement,
                        WordStrictnessComboBox.SelectedValue as MatchStrictness? ?? MatchStrictness.Default))
                    .OrderBy(entry => entry.SpokenForm, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray(),
                data.Snippets),
            "Dictionary saved");
    }

    /// <summary>Puts the picker back to the ordinary rule after a word is saved.</summary>
    /// <remarks>
    /// A picker that keeps its last answer means the second word somebody adds is silently strict
    /// because the first one was, and the only sign of it is a column they have no reason to be
    /// watching.
    /// </remarks>
    private void ResetMatchStrictness() =>
        WordStrictnessComboBox.SelectedValue = MatchStrictness.Default;

    /// <summary>Selects every word, or clears the selection when they are all already chosen.</summary>
    /// <remarks>
    /// ONE PRESS FOR THE WHOLE LIST, AND THE SAME PRESS TO UNDO IT. Selecting twenty words by hand
    /// to remove them is the work this button exists to remove; a separate "deselect all" would be a
    /// second control for the same idea, and macOS uses the one toggle for both.
    /// </remarks>
    private void SelectAllWordsButton_Click(object sender, RoutedEventArgs e)
    {
        var words = _settings.UserData.CustomWords;
        if (DictionaryList.SelectedItems.Count == words.Count && words.Count > 0)
        {
            DictionaryList.SelectedItems.Clear();
            return;
        }

        DictionaryList.SelectAll();
    }

    /// <summary>Removes every selected word, asking first when it is more than one.</summary>
    /// <remarks>
    /// ONE AT A TIME WAS THE WHOLE FEATURE, AND IT IS NOT ONE macOS HAS. Clearing a list of twenty
    /// imported words meant twenty selections and twenty presses, and the only thing stopping a
    /// person doing it in one go was the list refusing to hold more than one selection.
    ///
    /// THE CONFIRMATION IS FOR THE PLURAL CASE ONLY. Asking before removing a single word turns the
    /// ordinary action into two steps for no gain; asking before removing fifteen is the difference
    /// between a mistake somebody notices and one they have to rebuild by hand.
    /// </remarks>
    private async void RemoveWordButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = DictionaryList.SelectedItems.OfType<CustomWordEntry>().ToArray();
        if (selected.Length == 0)
        {
            ShowMessage("Select a word first", "Choose the dictionary rows you want to remove.", InfoBarSeverity.Informational);
            return;
        }

        if (selected.Length > 1 && !await ConfirmAsync(
            $"Remove {selected.Length} words?",
            "They are deleted from this PC. Nothing else changes, and you can add them again.",
            "Remove them").ConfigureAwait(true))
        {
            return;
        }

        // THE COUNT IS WHAT WAS REALLY REMOVED, NOT WHAT WAS SELECTED. Removal matches by identity,
        // so a row that another change replaced while this was waiting is no longer the row that was
        // chosen - it is left alone, correctly, and saying "removed" over the top of that tells
        // somebody a word is gone when it is still there.
        var removal = await SaveUserDataAsync(data =>
        {
            var remaining = CustomWordRemoval.Without(data.CustomWords, selected);
            return (
                new ReusableUserData(remaining, data.Snippets),
                data.CustomWords.Count - remaining.Count);
        }).ConfigureAwait(true);

        if (removal.Failure is not null)
        {
            return;
        }

        var removedCount = removal.Value;

        if (removedCount == 0)
        {
            ShowMessage(
                "Nothing was removed",
                "Those words changed while this was saving. Select them again to remove them.",
                InfoBarSeverity.Warning);
            return;
        }

        ShowMessage(
            removedCount == 1 ? "Dictionary entry removed" : $"{removedCount} dictionary entries removed",
            removedCount < selected.Length
                ? $"{selected.Length - removedCount} of them changed while this was saving and were left alone."
                : "The change was saved locally.",
            InfoBarSeverity.Success);
    }

    /// <summary>Asks before something that cannot be undone.</summary>
    private async Task<bool> ConfirmAsync(string title, string body, string confirmLabel)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = body,
            PrimaryButtonText = confirmLabel,
            CloseButtonText = "Keep them",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync().AsTask().ConfigureAwait(true) == ContentDialogResult.Primary;
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

        // Same reason as the word boxes above: a refused save must not eat the snippet someone
        // just wrote.
        if (!await SaveUserDataAsync(
                data => new ReusableUserData(
                    data.CustomWords,
                    data.Snippets
                        .Where(entry => !string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                        .Append(new SnippetEntry(name, body))
                        .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToArray()),
                "Snippet saved").ConfigureAwait(true))
        {
            return;
        }

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

        await SaveUserDataAsync(data => new ReusableUserData(
                data.CustomWords,
                data.Snippets.Where(entry => entry != selected).ToArray()), "Snippet removed").ConfigureAwait(true);
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
            FoundationInfoBar.Message = "The saved copy is gone. EnviousWispr will not paste it or keep it anywhere.";
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

    /// <summary>
    /// Reads a word list and reports what it found, without changing anything the user did not see.
    /// </summary>
    /// <remarks>
    /// The message names every outcome that occurred, including the ones that are NOT failures.
    /// A count of additions alone would let a file that was half unreadable report a clean import,
    /// which is the failure this whole feature is shaped to avoid.
    ///
    /// A conflict adds nothing. Overwriting a correction someone tuned by hand, silently, because
    /// a file they downloaded happened to disagree, is worse than importing nothing at all - so
    /// conflicts are reported and skipped rather than resolved on the user's behalf.
    /// </remarks>
    /// <summary>
    /// Offers the shipped word lists, one menu item each.
    /// </summary>
    /// <remarks>
    /// Built from the catalogue rather than declared in markup, so a pack added later appears
    /// without anyone remembering to add a row - the failure mode of a hand-written menu is that
    /// it is correct until it silently is not.
    ///
    /// Each item carries the pack's DESCRIPTION as its help text rather than its name twice, so a
    /// screen reader user hears what the list is for rather than a label they already heard.
    /// </remarks>
    private void AddPackButton_Click(object sender, RoutedEventArgs e)
    {
        PackFlyout.Items.Clear();
        foreach (var pack in VocabularyPacks.All)
        {
            var item = new MenuFlyoutItem { Text = pack.Name, Tag = pack.Id };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(item, pack.Description);
            item.Click += PackMenuItem_Click;
            PackFlyout.Items.Add(item);
        }
    }

    /// <summary>
    /// Installs one pack through exactly the path an imported file takes.
    /// </summary>
    /// <remarks>
    /// Same reader, same collision rules, same description. A pack that merged by its own route
    /// would be a second implementation of adding words, and the two would drift - so a user who
    /// already corrects one of these words their own way keeps their version and is told, exactly
    /// as they would be for a file they chose themselves.
    /// </remarks>
    private async void PackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string id })
        {
            return;
        }

        var pack = VocabularyPacks.All.FirstOrDefault(candidate => candidate.Id == id);
        if (pack is null)
        {
            return;
        }

        // NO DECISION OUTSIDE THE GATE AT ALL, INCLUDING "THERE IS NOTHING TO ADD". Reading the
        // words first to decide whether to bother meant a removal finishing in between could leave
        // this saying "already set up" about a list that would in fact have gained words. The plan
        // is computed once, where it is applied, and the message is chosen from what came back.
        var outcome = await SaveUserDataAsync(data =>
        {
            var actual = CustomWordImport.Read(pack.Words, data.CustomWords);
            return (
                new ReusableUserData([.. data.CustomWords, .. actual.Additions], data.Snippets),
                actual);
        }).ConfigureAwait(true);

        if (outcome.Failure is not null)
        {
            return;
        }

        var committed = outcome.Value;
        ShowMessage(
            committed.Additions.Count == 0 ? $"{pack.Name} is already set up" : $"{pack.Name} added",
            DescribeImport(committed),
            committed.Additions.Count == 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Success,
            ReplaceConflictsAction(committed));
    }

    private Button BuildOfferButton(ImportConflictOffer offer)
    {
        var button = new Button { Content = offer.Label };
        button.Click += async (_, _) =>
        {
            if (await ReplaceConflictsAsync(offer.Replacements).ConfigureAwait(true))
            {
                return;
            }

            // THE OFFER COMES BACK WHEN THE SAVE DID NOT TAKE. The failure message clears the bar's
            // button along with the old sentence, so without this a refused replacement leaves the
            // user reading an error with the one thing that would retry it gone. An earlier draft
            // cleared the button BEFORE saving, which had the same end state and no way back.
            OperationInfoBar.ActionButton = BuildOfferButton(offer);
        };
        return button;
    }

    private async void ImportWordsButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".txt");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(file.Path).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowMessage(
                "That file could not be read",
                "Windows would not open it. Nothing was changed.",
                InfoBarSeverity.Error);
            return;
        }

        await ApplyWordListAsync(text).ConfigureAwait(true);
    }

    /// <summary>Reads one word list, adds what is new, and offers what it could not decide.</summary>
    /// <remarks>
    /// ONE PATH FOR EVERY WORD LIST, HOWEVER IT ARRIVED. A file and a paste are the same list with
    /// two ways in, and two copies of this would be two places for the conflict offer to be
    /// forgotten from.
    ///
    /// A CONFLICT IS OFFERED RATHER THAN DECIDED, AND THAT IS THE ROW THIS CLOSES. Until now a word
    /// the list corrected differently from the user's own was counted and left alone, which tells
    /// somebody their curated list was ignored and gives them nothing to do about it but retype
    /// words they already have written down. Silently overwriting a correction tuned by hand is
    /// worse, so neither is chosen for them: the message carries a button and they pick.
    /// </remarks>
    private async Task ApplyWordListAsync(string text)
    {
        // NO DECISION OUTSIDE THE GATE AT ALL, INCLUDING "THERE IS NOTHING TO ADD". Reading the
        // words first to decide whether to bother meant a removal finishing in between could leave
        // this saying "no new words" about a list that would in fact have gained some. The plan is
        // computed once, where it is applied, and the message is chosen from what came back.
        //
        // THE ITEMISED DESCRIPTION IS USED ON BOTH OUTCOMES. An earlier version fell back to the
        // generic save message the moment ONE word imported, so a hundred-line file with sixty good
        // rows and forty unreadable ones said "the change was saved locally" and the forty were
        // never mentioned.
        var outcome = await SaveUserDataAsync(data =>
        {
            var actual = CustomWordImport.Read(text, data.CustomWords);
            return (
                new ReusableUserData([.. data.CustomWords, .. actual.Additions], data.Snippets),
                actual);
        }).ConfigureAwait(true);

        if (outcome.Failure is not null)
        {
            // The save refused and has already said why. Speaking again here would paint over that
            // with a success, and offering to replace corrections nothing imported would be worse.
            return;
        }

        var committed = outcome.Value;
        ShowMessage(
            committed.Additions.Count == 0 ? "No new words to add" : "Words imported",
            DescribeImport(committed),
            committed.Additions.Count == 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Success,
            ReplaceConflictsAction(committed));
    }

    /// <summary>Pastes a word list straight out of the clipboard.</summary>
    /// <remarks>
    /// THE SHORTEST ROUTE FROM SOMEBODY ELSE'S LIST INTO THIS ONE. macOS has the same path, and
    /// without it a person with a list in a spreadsheet or an email has to save a file first for no
    /// reason the app can explain.
    ///
    /// It reads the clipboard and does not touch it: the clipboard is somewhere a dictation product
    /// has to be a good citizen, and this app already guards it carefully on the delivery path.
    /// </remarks>
    private async void PasteWordsButton_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            var clipboard = Clipboard.GetContent();
            text = clipboard.Contains(StandardDataFormats.Text)
                ? await clipboard.GetTextAsync()
                : string.Empty;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Another app can hold the clipboard open, and Windows says so by failing the read.
            // UnauthorizedAccessException is the one that mattered: .NET maps E_ACCESSDENIED to it
            // rather than to COMException, so protected clipboard content escaped this handler -
            // and an async void handler that throws takes the process down with it.
            ShowMessage(
                "The clipboard could not be read",
                "Another app may be using it. Copy the list again and retry. Nothing was changed.",
                InfoBarSeverity.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            ShowMessage(
                "There is no text on the clipboard",
                "Copy a list of word pairs first. Each line needs a spoken form and a replacement, "
                    + "separated by a comma, a tab, or an equals sign.",
                InfoBarSeverity.Informational);
            return;
        }

        await ApplyWordListAsync(text).ConfigureAwait(true);
    }

    /// <summary>The offer to take the list's version of the words the user corrects differently.</summary>
    private static ImportConflictOffer? ReplaceConflictsAction(CustomWordImportPlan plan)
    {
        if (plan.Conflicts.Count == 0)
        {
            return null;
        }

        var word = plan.Conflicts.Count == 1 ? "correction" : "corrections";
        return new ImportConflictOffer($"Replace my {plan.Conflicts.Count} {word}", plan.Conflicts);
    }

    private async Task<bool> ReplaceConflictsAsync(IReadOnlyList<CustomWordEntry> replacements)
    {
        var word = replacements.Count == 1 ? "correction" : "corrections";
        // MERGED INSIDE THE GATE, against the words that are there when it happens. Merging outside
        // built a list from a snapshot, and saving it put back whatever had changed since.
        return await SaveUserDataAsync(
            data => new ReusableUserData(
                CustomWordImport.Merge(data.CustomWords, replacements),
                data.Snippets),
            "Corrections replaced",
            $"{replacements.Count} {word} now match the list you imported.").ConfigureAwait(true);
    }

    /// <summary>A button a message can carry, and what pressing it applies.</summary>
    private sealed record ImportConflictOffer(
        string Label, IReadOnlyList<CustomWordEntry> Replacements);

    private async void ExportWordsButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = "EnviousWispr-words",
            DefaultFileExtension = ".csv",
        };
        picker.FileTypeChoices.Add("Word list", [".csv"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(
                file.Path,
                CustomWordImport.Write(_settings.UserData.CustomWords)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowMessage(
                "That file could not be written",
                "Windows would not save it. Your words are unchanged.",
                InfoBarSeverity.Error);
            return;
        }

        ShowMessage(
            "Words exported",
            "Your word list was saved. You can edit it and import it back.",
            InfoBarSeverity.Success);
    }

    /// <summary>
    /// Says what happened to EVERY line, not only the ones that worked.
    /// </summary>
    /// <remarks>
    /// Built by naming each outcome that occurred rather than by reporting a total. A file that is
    /// half unreadable and half duplicates would otherwise produce "0 words added", which reads as
    /// nothing to do rather than as a file that needs fixing.
    /// </remarks>
    private static string DescribeImport(CustomWordImportPlan plan)
    {
        var parts = new List<string>();
        var already = plan.Lines.Count(line => line.Outcome == ImportedWordOutcome.AlreadyPresent);
        if (plan.Additions.Count > 0)
        {
            parts.Add($"{plan.Additions.Count} added");
        }

        if (already > 0)
        {
            parts.Add($"{already} you already had");
        }

        if (plan.ConflictCount > 0)
        {
            parts.Add($"{plan.ConflictCount} left alone because you already correct them differently");
        }

        if (plan.UnreadableCount > 0)
        {
            // No trailing full stop: the join below adds one, and the first version produced
            // "separated by a comma..". And the list of separators is the parser's rather than a
            // remembered one - it accepts a tab and an equals sign too, and a user with a
            // tab-separated file was being told the wrong thing about a file it would have taken.
            // "1 lines could not be read" - the only clause in this message with a noun that has
            // to agree with its count. The others are correct at any number: "1 added", "1 you
            // already had", "1 left alone because you already correct them differently". Checked
            // rather than assumed, so this is one line rather than a pattern to sweep for.
            var lineWord = plan.UnreadableCount == 1 ? "line" : "lines";
            parts.Add(
                $"{plan.UnreadableCount} {lineWord} could not be read. Each needs a spoken form "
                + "and a replacement, separated by a comma, a tab, or an equals sign");
        }

        return parts.Count == 0
            ? "That file had no word pairs in it."
            : string.Join(". ", parts) + ".";
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

    /// <summary>
    /// Asks for a repeatable measurement of the text cleanup, and shows the answer.
    /// </summary>
    /// <remarks>
    /// The window does not own the pipeline, so it asks rather than measures. Building a second
    /// pipeline here to avoid the round trip would measure a DIFFERENT object from the one every
    /// dictation uses, which is the one thing a speed check must not do.
    /// </remarks>
    private void RunSpeedCheckButton_Click(object sender, RoutedEventArgs e)
    {
        _speedCheckRunning = true;
        RunSpeedCheckButton.IsEnabled = false;
        SetLiveRegion(SpeedCheckResultText, "Running...", Visibility.Visible);
        SpeedCheckRequested?.Invoke();
    }

    /// <summary>Shows a finished speed check, or says why it did not run.</summary>
    /// <remarks>
    /// The refusal says WHY, for the same reason Quick Add's does: a check that silently produces
    /// nothing is indistinguishable from a button that does not work.
    /// </remarks>
    /// <summary>
    /// Greys the speed check out while a dictation is running, and says why underneath it.
    /// </summary>
    /// <remarks>
    /// A LIVE BUTTON THAT ANSWERS "NO" IS THE WRONG FEEL ON WINDOWS. The check genuinely cannot run
    /// during a dictation - it would be measuring a machine that is busy doing the thing being
    /// measured - so the honest presentation is a control that is visibly unavailable, not one that
    /// accepts the click and then declines.
    ///
    /// THE REASON GETS ITS OWN LINE RATHER THAN REPLACING THE RESULT. A greyed control with no
    /// explanation is a different bad, and overwriting the last measurement to explain the greying
    /// would destroy the number the user came back to read.
    /// </remarks>
    private void SetSpeedCheckAvailability(bool available)
    {
        RunSpeedCheckButton.IsEnabled = available && !_speedCheckRunning;
        SetLiveVisibility(SpeedCheckUnavailableText, available ? Visibility.Collapsed : Visibility.Visible);
    }

    public void SetSpeedCheckResult(LatencySummary? summary)
    {
        // Not an unconditional re-enable. A dictation can begin between the click and the answer,
        // and handing the button back then would undo the greying the moment it was needed.
        _speedCheckRunning = false;
        SetSpeedCheckAvailability(_currentOverlayState != DictationOverlayState.Recording);
        SetLiveRegion(
            SpeedCheckResultText,
    summary is null || summary.Count == 0
                ? "The speed check did not run. It is skipped while a dictation is in progress."
                : $"{summary.Count} runs. Typical {summary.MedianMilliseconds:0.0}ms, "
                    + $"fastest {summary.MinMilliseconds:0.0}ms, slowest {summary.MaxMilliseconds:0.0}ms. "
                    + (summary.Percentile95IsJustTheMaximum
                        ? "Too few runs to report a slow tail separately."
                        : $"The slowest 5% took {summary.Percentile95Milliseconds:0.0}ms or more."),
            Visibility.Visible);
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

    private void DownloadModelButton_Click(object sender, RoutedEventArgs e) =>
        ModelDownloadRequested?.Invoke();

    private void CancelModelDownloadButton_Click(object sender, RoutedEventArgs e) =>
        ModelDownloadCancelRequested?.Invoke();

    /// <summary>
    /// Shows the speech-model situation on the Transcription card and the onboarding card together.
    /// </summary>
    /// <remarks>
    /// ONE PRESENTATION, TWO SURFACES. Onboarding is where a new install first meets "model is not
    /// installed", and Transcription is where the pill sends them; a person must be able to act
    /// from either without the two disagreeing about what is happening. Ref: #92.
    /// </remarks>
    public void SetModelDelivery(ModelDeliveryPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        SetLiveText(ModelDeliveryStatusText, presentation.Text);
        ModelDeliveryProgress.Visibility = presentation.Percent is null ? Visibility.Collapsed : Visibility.Visible;
        ModelDeliveryProgress.Value = presentation.Percent ?? 0;
        DownloadModelButton.Visibility = presentation.CanDownload ? Visibility.Visible : Visibility.Collapsed;
        CancelModelDownloadButton.Visibility = presentation.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        OnboardingDownloadModelButton.Visibility = DownloadModelButton.Visibility;
        OnboardingModelProgress.Visibility = ModelDeliveryProgress.Visibility;
        OnboardingModelProgress.Value = ModelDeliveryProgress.Value;
        if (presentation.CanDownload || presentation.CanCancel || presentation.Percent is not null)
        {
            OnboardingModelText.Text = presentation.Text;
        }
    }

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

        if (await TrySaveAsync(
                current => current.Apply(imported.Profile),
                "Profile imported",
                "Settings, dictionary entries, and snippets are ready. Machine-local choices and history were preserved.").ConfigureAwait(true))
        {
            ApplySettingsToControls();
        }
    }

    private async Task LoadMicrophonesAsync()
    {
        // DECLARED OUTSIDE THE TRY SO THE ANSWER SURVIVES A FAILURE. A blocked switch is one of the
        // reasons enumeration throws, and the catch is exactly where knowing that matters most.
        var consent = MicrophoneConsent.Unknown;
        try
        {
            if (_deviceCatalog is null)
            {
                _deviceCatalog = new WasapiDeviceCatalog();
                _deviceCatalog.DevicesChanged += OnAudioDevicesChanged;
            }

            // READ BEFORE THE ENUMERATION, because the enumeration is one of the things a blocked
            // switch breaks. Reading it afterwards meant the catch threw the answer away and
            // reported the symptom instead of the cause.
            consent = WindowsMicrophoneConsent.Read();
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
            ApplyMicrophoneReadiness(MicrophoneReadinessReport.For(
                consent,
                defaultDevice?.DisplayName));
        }
        catch
        {
            _microphones = [new MicrophoneChoice(null, "Use the Windows default microphone")];
            MicrophoneComboBox.ItemsSource = _microphones;
            MicrophoneComboBox.SelectedIndex = 0;
            ApplyMicrophoneReadiness(MicrophoneReadinessReport.For(
                consent,
                defaultDeviceName: null,
                enumerationFailed: true));
        }
    }

    /// <summary>Puts one microphone verdict on both places that report it.</summary>
    /// <remarks>
    /// THE BUTTON APPEARS ONLY WHERE IT WOULD HELP. Offering "open microphone privacy settings"
    /// beside a working microphone is noise, and offering it beside a missing one sends somebody to
    /// a page that will tell them everything is fine.
    /// </remarks>
    private void ApplyMicrophoneReadiness(MicrophoneReadiness readiness)
    {
        SetLiveText(MicrophoneReadinessText, readiness.Sentence);
        OnboardingMicrophoneText.Text = readiness.Sentence;
        MicrophonePrivacyFixButton.Visibility = readiness.OffersPrivacySettings
            ? Visibility.Visible
            : Visibility.Collapsed;
        OnboardingMicrophonePrivacyButton.Visibility = MicrophonePrivacyFixButton.Visibility;
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
            || SelectAllWordsButton is null
            || WordSelectionCountText is null
            || RemoveSnippetButton is null)
        {
            return;
        }

        var historySelected = HistoryList.SelectedItem is not null;
        CopyHistoryButton.IsEnabled = historySelected;
        KeepHistoryButton.IsEnabled = historySelected;
        DeleteHistoryButton.IsEnabled = historySelected;
        ClearHistoryButton.IsEnabled = _history.Count > 0;

        var selectedWords = DictionaryList.SelectedItems.Count;
        RemoveWordButton.IsEnabled = selectedWords > 0;
        SelectAllWordsButton.IsEnabled = _settings.UserData.CustomWords.Count > 0;

        // THE COUNT IS SHOWN ONLY WHILE MORE THAN ONE IS CHOSEN. With one row selected the row
        // itself is the answer and a line reading "1 selected" is noise; with fifteen it is the
        // only thing that says how much the next press removes.
        SetLiveRegion(
            WordSelectionCountText,
            selectedWords > 1 ? $"{selectedWords} selected" : string.Empty,
            selectedWords > 1 ? Visibility.Visible : Visibility.Collapsed);

        // Export needs words rather than a selection - it writes the whole list.
        ExportWordsButton.IsEnabled = _settings.UserData.CustomWords.Count > 0;
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
            var row = EyebrowRowOf(candidate);
            if (eyebrow is null || row is null)
            {
                continue;
            }

            var redundant = visibleSections.Length == 1
                && ReferenceEquals(candidate, visibleSections[0])
                && string.Equals(eyebrow.Text, title, StringComparison.OrdinalIgnoreCase);
            row.Visibility = redundant ? Visibility.Collapsed : Visibility.Visible;
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
        EyebrowRowOf(section)?.Children.OfType<TextBlock>().FirstOrDefault();

    /// <summary>
    /// The whole eyebrow row of a section card: its accent glyph and its label together.
    /// </summary>
    /// <remarks>
    /// COLLAPSING THE LABEL ALONE WOULD LEAVE THE GLYPH BEHIND. An eyebrow that repeats the page
    /// title is hidden, and once the eyebrow became two things rather than one, hiding the text
    /// would have left a small accent mark floating above the card with nothing beside it.
    /// </remarks>
    private static StackPanel? EyebrowRowOf(Border section) =>
        section.Child is StackPanel panel
            ? panel.Children.OfType<StackPanel>().FirstOrDefault(row =>
                row.Orientation == Orientation.Horizontal &&
                row.Children.OfType<TextBlock>().Any())
            : null;

    /// <summary>
    /// Moves the choice with the arrow keys, the way a Windows radio group does.
    /// </summary>
    /// <remarks>
    /// Written by hand because the framework does not supply it here. Replacing RadioButtons with
    /// an ItemsControl fixed card widths and took the group's keyboard behaviour with it, and two
    /// attempts to get it back from a property failed: TabFocusNavigation="Once" gave the single
    /// tab stop and nothing to move within the group, then XYFocusKeyboardNavigation="Enabled"
    /// changed nothing at all - it governs directional/gamepad navigation, not arrow keys inside a
    /// radio group, which RadioButtons supplies through its own key handling.
    ///
    /// Both were verified INERT on the running app rather than assumed: with focus resting on a
    /// non-selected card, Down and Up moved nothing, while the same synthetic arrow injection
    /// moved the navigation list in the same binary. So focus can sit on these cards; the keys
    /// were simply never routed between them.
    ///
    /// Arrowing CHANGES the selection, which is what a Windows radio group does and what
    /// RadioButtons did here before. Left and Right are included because a user reaching for
    /// either is reaching for the same thing.
    /// </remarks>
    /// <summary>The four card-based choice lists.</summary>
    private ItemsControl[] ChoiceLists() =>
    [
        EngineComboBox,
        PolishProviderComboBox,
        ThemeComboBox,
        OverlayPositionComboBox,
    ];

    /// <summary>
    /// Captures a pressed key combination into a keybind field.
    /// </summary>
    /// <remarks>
    /// These were plain text boxes. A field labelled "Recording keybind" showing "F8" reads
    /// unmistakably as a capture control, so the overwhelmingly likely user action is to click it
    /// and press the key they want. Measured on the running app: pressing F9 did nothing at all -
    /// no value change, no feedback - and pressing Q produced "qF8", because the character was
    /// inserted at a caret sitting at position 0. Silent in both directions, and the corrupted
    /// value still LOOKS like a keybind.
    ///
    /// Only the combinations HotkeyGestureParser can express are accepted, so anything captured
    /// here round-trips: letters, digits, F1-F24 and the named keys it normalises. An unsupported
    /// key leaves the field untouched rather than writing something that cannot be parsed.
    /// </remarks>
    /// <summary>
    /// The threshold field is only useful when the switch above it is on, and only in Toggle mode.
    /// </summary>
    /// <remarks>
    /// Disabled rather than hidden. A control that vanishes leaves the user wondering whether they
    /// imagined it; a disabled one with the switch beside it says what turns it back on.
    /// </remarks>
    private void AutoStopToggle_Toggled(object sender, RoutedEventArgs e) =>
        UpdateAutoStopAvailability();

    private void UpdateAutoStopAvailability() =>
        AutoStopSecondsBox.IsEnabled = AutoStopToggle.IsOn;

    /// <summary>The three keybind fields, each with the name a person would call it.</summary>
    private (TextBox Box, string Role)[] KeybindFields() =>
    [
        (HotkeyTextBox, "Recording"),
        (CancelHotkeyTextBox, "Cancel"),
        (QuickAddHotkeyTextBox, "Add-a-word"),
    ];

    private void HotkeyBoxTextChanged(object sender, TextChangedEventArgs e) => RefreshKeybindConflicts();

    /// <summary>Names a clash while it is being made, not when Save refuses it.</summary>
    /// <remarks>
    /// SAVE ALREADY REFUSED A CLASH, so nothing broken could reach the settings file, and that is
    /// why this reads as polish rather than a fix. It is not: a rule enforced only at the last
    /// possible moment lets someone set a shortcut, watch two fields sit there agreeing with each
    /// other, and be told no after they commit. Both paths now ask the same detector, so the
    /// warning and the refusal cannot come to different conclusions.
    /// </remarks>
    private void RefreshKeybindConflicts()
    {
        // No existence guard: every field here is built by InitializeComponent, and the only two
        // callers are a change event and the Save button, both of which run after it. Settings
        // load fills the three fields one at a time, which is harmless - a field still holding
        // its empty starting value does not collide with anything.
        var fields = KeybindFields();
        var clashes = HotkeyConflictDetector.Find(fields.Select(field => (field.Role, field.Box.Text)).ToArray());
        var guilty = clashes
            .SelectMany(clash => new[] { clash.FirstRole, clash.SecondRole })
            .ToHashSet(StringComparer.Ordinal);

        foreach (var field in fields)
        {
            if (guilty.Contains(field.Role))
            {
                // Read off a probe element rather than Application.Current.Resources, because the
                // error brush lives in a theme dictionary: indexing the app resources for one
                // throws, and a brush captured once would keep the old theme's colour after a
                // switch. The probe carries a ThemeResource, so it follows the theme for free.
                field.Box.BorderBrush = KeybindErrorProbe.Background;
            }
            else
            {
                // ClearValue rather than null: a null brush is an invisible border, not the
                // theme's border, so the field would silently lose its outline once fixed.
                field.Box.ClearValue(Control.BorderBrushProperty);
            }
        }

        SetLiveRegion(
            KeybindConflictText,
            HotkeyConflictDetector.Describe(clashes),
            clashes.Count == 0 ? Visibility.Collapsed : Visibility.Visible);
    }

    private void HotkeyBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        // A MODIFIER IS NOT A GESTURE YET, WHICH IS NOT THE SAME AS NOT BEING ONE. On the way down
        // it is indistinguishable from the start of a combination, so it is still swallowed here -
        // it must neither edit the field nor move focus while the user is mid-chord. What decides
        // it is the RELEASE: coming back up with nothing else pressed is a deliberate tap, and that
        // is handled in HotkeyBoxKeyUp.
        if (e.Key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            _keybindModifierCandidate ??= SidedModifierName();
            e.Handled = true;
            return;
        }

        // Any ordinary key means the modifier was qualifying it. The candidate is dropped here
        // rather than on the modifier's release, because by then the field already holds the
        // combination and overwriting it would undo what the user just chose.
        _keybindModifierCandidate = null;

        // Handled regardless of whether the key is usable: the field is capture-driven, so a
        // keystroke must never fall through and be inserted as text. That fall-through is the
        // defect being fixed.
        e.Handled = true;

        if (!TryDescribeKey(e.Key, out var key))
        {
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (IsHeld(VirtualKey.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsHeld(VirtualKey.Menu))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsHeld(VirtualKey.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (IsHeld(VirtualKey.LeftWindows) || IsHeld(VirtualKey.RightWindows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        box.Text = new HotkeyGesture(modifiers, key).ToString();
        box.SelectionStart = box.Text.Length;
    }

    /// <summary>Offers a lone modifier as a binding, once it comes back up untouched.</summary>
    /// <remarks>
    /// THE RELEASE IS WHERE A TAP BECOMES DISTINGUISHABLE from the start of a shortcut, which is the
    /// same reason the running app decides it there. Deciding on the way down would put a binding in
    /// the field the moment a user reached for Control, before they had pressed the letter they were
    /// aiming at.
    ///
    /// ALT IS NOT OFFERED, matching the engine and the parser. A lone Alt tap already opens a
    /// window's menu bar, so a user who set it would have a binding that reads correctly, saves
    /// correctly, and loses every race with the shell.
    ///
    /// WITHOUT THIS THE WHOLE MODIFIER BINDING IS UNREACHABLE. The engine accepts one and the
    /// settings file can carry one; if nothing on screen can produce one, that is a working feature
    /// wired to nothing - which had already happened once on this project and was caught by reading
    /// rather than by shipping.
    /// </remarks>
    private void HotkeyBoxKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        var candidate = _keybindModifierCandidate;
        _keybindModifierCandidate = null;

        if (candidate is null ||
            e.Key is not (VirtualKey.Control or VirtualKey.Shift
                or VirtualKey.LeftWindows or VirtualKey.RightWindows))
        {
            return;
        }

        e.Handled = true;
        box.Text = new HotkeyGesture(HotkeyModifiers.None, candidate).ToString();
        box.SelectionStart = box.Text.Length;
    }

    /// <summary>Which physical modifier is down, by side.</summary>
    /// <remarks>
    /// A key event reports "Control", not "the right one". A binding has to name ONE PHYSICAL KEY,
    /// so the side is read from the keyboard state instead. Left is checked first only to make the
    /// answer deterministic when someone is holding both; either answer would be defensible and an
    /// undefined one would not.
    /// </remarks>
    private static string? SidedModifierName()
    {
        if (IsHeld(VirtualKey.LeftControl))
        {
            return "LeftCtrl";
        }

        if (IsHeld(VirtualKey.RightControl))
        {
            return "RightCtrl";
        }

        if (IsHeld(VirtualKey.LeftShift))
        {
            return "LeftShift";
        }

        if (IsHeld(VirtualKey.RightShift))
        {
            return "RightShift";
        }

        if (IsHeld(VirtualKey.LeftWindows))
        {
            return "LeftWin";
        }

        return IsHeld(VirtualKey.RightWindows) ? "RightWin" : null;
    }

    private static bool IsHeld(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// The parser's own vocabulary, and nothing outside it.
    /// </summary>
    /// <remarks>
    /// Deliberately mirrors HotkeyGestureParser.TryNormalizeKey rather than accepting every key
    /// Windows can report. Capturing something the parser cannot read back would put a value in
    /// the field that looks valid and fails at save - which is the failure this whole change
    /// exists to remove.
    /// </remarks>
    private static bool TryDescribeKey(VirtualKey key, out string described)
    {
        if (key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            described = key.ToString();
            return true;
        }

        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            described = ((int)key - (int)VirtualKey.Number0).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (key is >= VirtualKey.F1 and <= VirtualKey.F24)
        {
            described = $"F{(int)key - (int)VirtualKey.F1 + 1}";
            return true;
        }

        described = key switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.Insert => "Insert",
            VirtualKey.Delete => "Delete",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.Pause => "Pause",
            VirtualKey.Scroll => "ScrollLock",
            VirtualKey.Escape => "Escape",
            _ => string.Empty,
        };
        return described.Length > 0;
    }

    private void ChoiceListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not ItemsControl list
            || list.ItemsSource is not SelectableChoiceOption[] choices
            || choices.Length == 0)
        {
            return;
        }

        var delta = e.Key switch
        {
            VirtualKey.Down or VirtualKey.Right => 1,
            VirtualKey.Up or VirtualKey.Left => -1,
            _ => 0,
        };
        if (delta == 0)
        {
            return;
        }

        // Handled either way once an arrow reaches a choice list: at the ends the key does
        // nothing here, and letting it bubble would move focus out of the group, which is the
        // one thing a radio group's arrow keys must never do.
        e.Handled = true;

        var next = Math.Clamp(SelectedIndexOf(choices) + delta, 0, choices.Length - 1);
        if (choices[next].IsSelected)
        {
            return;
        }

        SelectChoice(choices, next);
        FindDescendant<RadioButton>(list.ContainerFromIndex(next) as DependencyObject)
            ?.Focus(FocusState.Keyboard);
    }

    /// <summary>First descendant of the requested type, or null.</summary>
    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSelectionDependentButtons();

    /// <summary>Saves with the generic message, and says whether it worked.</summary>
    /// <remarks>
    /// IT RETURNS THE OUTCOME FOR THE SAME REASON THE OTHER OVERLOAD DOES. This one used to return a
    /// bare Task, which is not a decision anybody made - it simply had no reason to report until a
    /// caller needed to react. Three did: two cleared the user's typing after a refused save, and
    /// one said "All of them added" and removed the suggestion it had failed to add.
    /// </remarks>
    private async Task<bool> SaveUserDataAsync(
        Func<ReusableUserData, ReusableUserData> change, string title) =>
        await SaveUserDataAsync(change, title, "The change was saved locally.").ConfigureAwait(true);

    /// <summary>Saves the user's words and snippets, and says whether it worked.</summary>
    /// <remarks>
    /// IT RETURNS THE OUTCOME BECAUSE A CALLER THAT SPEAKS AFTER IT NEEDS TO KNOW. TrySaveAsync
    /// already shows an error of its own when the save is refused - the word ceiling, for one - and
    /// a caller that then showed its own success message would paint over that error with the
    /// opposite of the truth. Measured on the import path, where a refused save reported "Words
    /// imported" and offered to replace corrections that had not been imported.
    /// </remarks>
    /// <summary>Saves user data and returns what the change worked out while it held the gate.</summary>
    /// <remarks>
    /// AN IMPORT DECIDES WHAT TO ADD BY LOOKING AT WHAT IS ALREADY THERE, so that decision is a
    /// question about the CURRENT words and has to be answered inside the gate. Answered outside, the
    /// plan describes a list that may have changed - and saving its result then overwrites whatever
    /// changed it. The value comes back so the message describes what was actually stored.
    /// </remarks>
    private async Task<SettingsUpdateOutcome<T>> SaveUserDataAsync<T>(
        Func<ReusableUserData, (ReusableUserData Data, T Value)> change)
    {
        var outcome = await SettingsWriter.UpdateAsync(current =>
        {
            var (data, value) = change(current.UserData);
            return (current with { UserData = data }, value);
        }).ConfigureAwait(true);

        if (outcome.Failure is not null)
        {
            ShowSettingsFailure(outcome.Failure);
            return outcome;
        }

        _settings = SettingsWriter.Current;
        SettingsChanged?.Invoke(_settings);
        ApplySettingsToControls();
        return outcome;
    }

    /// <summary>Says why a settings write did not happen.</summary>
    private void ShowSettingsFailure(Exception exception)
    {
        // THE APP CLOSING IS NOT A STORAGE FAILURE. A click that lands as the window is going away
        // is refused on purpose, and telling somebody their settings storage broke as they quit is
        // both alarming and untrue.
        if (exception is ObjectDisposedException)
        {
            return;
        }

        var (title, body) = exception switch
        {
            ArgumentException => (
                "Settings were not saved",
                "One or more values are invalid. Your previous settings remain active."),
            UnauthorizedAccessException or SecurityException => (
                "Windows blocked settings storage",
                "Your previous settings remain active."),
            _ => (
                "Settings storage is unavailable",
                "Your previous settings remain active."),
        };

        ShowMessage(title, body, InfoBarSeverity.Error);
    }

    private async Task<bool> SaveUserDataAsync(
        Func<ReusableUserData, ReusableUserData> change, string title, string message)
    {
        if (!await TrySaveAsync(current => current with { UserData = change(current.UserData) }, title, message)
            .ConfigureAwait(true))
        {
            return false;
        }

        RefreshReusableUserDataViews();
        return true;
    }

    private void UpdateHistoryListVisibility(string query, int itemCount)
    {
        var hasItems = itemCount > 0;
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var historyUnavailable = _historyLoadStatus is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable;
        ShowCard(HistoryLoadingState, _isHistoryLoading);
        ShowCard(HistoryList, !_isHistoryLoading && hasItems);
        ShowCard(HistoryEmptyState, !_isHistoryLoading && !hasItems && !hasQuery && !historyUnavailable);
        ShowCard(HistorySearchEmptyState, !_isHistoryLoading && !hasItems && hasQuery && !historyUnavailable);

        ShowCard(HistoryUnavailableState, !_isHistoryLoading && !hasItems && historyUnavailable);

        // ANNOUNCED AFTER EVERY CARD HAS ITS VISIBILITY, NOT BEFORE. A collapsed element is not in
        // the layout, so raising while the unavailable card was still hidden was refused by the very
        // ancestor check that stops announcements from pages nobody is looking at - and the one
        // state a person most needs told about was the one that stayed silent.
        //
        // AND ONLY WHEN THE ANSWER CHANGED. This runs on every refresh and every search keystroke,
        // and the card titles are fixed text, so nothing else stops "No matching dictations" being
        // spoken again on each letter typed. The key carries the count too, because a list that grew
        // is news even though it is still a loaded list.
        // THE CARDS GET THEIR WORDS BEFORE ANYTHING IS ANNOUNCED, because the announcement reads what
        // is on screen and a card still carrying the previous search's text would be read out.
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

        // THE QUERY IS PART OF THE ANSWER. "No matching dictations" for one search and for a
        // different search are two different facts, and a key without the query announced only the
        // first of them. The same is true of a count that happens to repeat under a new search.
        var normalisedQuery = query.Trim().ToLowerInvariant();
        var terminal = _isHistoryLoading
            ? null
            : historyUnavailable ? "unavailable"
            : hasItems ? $"loaded:{itemCount}:{normalisedQuery}"
            : hasQuery ? $"no-matches:{normalisedQuery}"
            : "empty";

        // A SEARCH ANNOUNCES WHEN THE TYPING STOPS, NOT PER LETTER. Putting the query in the key
        // makes every keystroke a new answer, which without this would restore exactly the
        // every-keystroke repetition the key was added to remove. A result the user did not type
        // their way to - a finished load, a cleared search - is not delayed.
        _pendingHistoryAnnouncement = terminal;
        if (hasQuery && terminal is not null)
        {
            _historyAnnounceDebounce.Stop();
            _historyAnnounceDebounce.Start();
            return;
        }

        _historyAnnounceDebounce.Stop();
        AnnounceHistoryStateIfChanged(terminal, historyUnavailable, hasItems, hasQuery, itemCount);
    }

    /// <summary>Announces the history outcome, if it is one nobody has been told yet.</summary>
    private void AnnounceHistoryStateIfChanged(
        string? terminal,
        bool historyUnavailable,
        bool hasItems,
        bool hasQuery,
        int itemCount)
    {
        if (terminal is null)
        {
            // CLEARED WHILE LOADING, so an explicit refresh that lands on the same answer still says
            // so. Without this, pressing Refresh and getting the same twelve dictations is silent.
            _lastAnnouncedHistoryState = null;
        }
        else if (!string.Equals(terminal, _lastAnnouncedHistoryState, StringComparison.Ordinal))
        {
            var announced = historyUnavailable
                ? AnnounceLiveRegion(HistoryUnavailableTitle)
                : hasItems
                    ? AnnounceHistoryCount(itemCount)
                    : hasQuery
                        ? AnnounceLiveRegion(HistorySearchEmptyTitle)
                        : AnnounceLiveRegion(HistoryEmptyTitle);

            // RECORDED ONLY IF IT WAS ACTUALLY SPOKEN. Remembering a state the ancestor check
            // refused would swallow the announcement for good once the page is opened.
            if (announced)
            {
                _lastAnnouncedHistoryState = terminal;
            }
        }
    }

    /// <summary>Announces the history outcome when the page becomes visible.</summary>
    /// <remarks>
    /// A RESULT THAT ARRIVED WHILE YOU WERE ELSEWHERE IS STILL NEWS WHEN YOU GET HERE. History loads
    /// at startup, so on any other page the announcement is refused by the ancestor check and, with
    /// nothing to re-run it, never happened at all - the page that most needed a spoken result was
    /// the one that never gave one.
    /// </remarks>
    private void OnHistoryAnnounceDue(object? sender, object e)
    {
        _historyAnnounceDebounce.Stop();
        AnnounceHistoryOnPageShown();
    }

    /// <summary>Waits for any settings write to finish, then stops accepting new ones.</summary>
    /// <remarks>
    /// AWAITED AT EXIT, BECAUSE ABANDONING THE WRITER LETS THE PROCESS END MID-WRITE. Synchronous
    /// teardown cannot wait, so it does not try; this is the asynchronous half that can.
    /// </remarks>
    public Task DrainSettingsAsync() =>
        _settingsWriter?.DrainAsync() ?? Task.CompletedTask;

    /// <summary>Says whatever history result is still waiting, if anything can hear it now.</summary>
    public void AnnouncePendingHistoryState() => AnnounceHistoryOnPageShown();

    private void AnnounceHistoryOnPageShown()
    {
        // A HIDDEN WINDOW IS NOT A VISIBLE PAGE, AND THE XAML TREE CANNOT SEE THE DIFFERENCE. Closing
        // this window hides it to the notification area without collapsing anything inside it, so the
        // ancestor check still reports every parent as showing and a debounce landing afterwards
        // would announce from a window nobody can see. The pending announcement is KEPT, so it
        // arrives when the window is opened again rather than being lost.
        if (_isHistoryLoading || _pendingHistoryAnnouncement is null || !AppWindow.IsVisible)
        {
            return;
        }

        var query = HistorySearchBox.Text.Trim();
        AnnounceHistoryStateIfChanged(
            _pendingHistoryAnnouncement,
            _historyLoadStatus is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable,
            HistoryList.Visibility == Visibility.Visible,
            !string.IsNullOrWhiteSpace(query),
            HistoryList.Items.Count);
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

    /// <summary>One at a time, and each change derived from what is actually stored.</summary>
    /// <remarks>
    /// EVERY WRITER HERE BUILT ITS RECORD FIRST AND SAVED SECOND, so two of them overlapping wrote
    /// two different whole-settings snapshots and whichever finished last won - silently discarding
    /// the other person's change. Saving atomically does not help: each save was atomic and still
    /// complete, so it replaced everything the other had just written.
    ///
    /// THE FIX IS TO DERIVE INSIDE THE GATE, NOT TO PASS A RECORD ACROSS IT. A snapshot built before
    /// the wait is stale by the time the wait ends, and writing it back is exactly the loss this
    /// prevents. Callers hand over a function of the CURRENT settings instead.
    /// </remarks>
    private async Task<bool> UpdateSettingsAsync(
        Func<AppSettings, AppSettings> change,
        Func<Exception, Task>? onFailure = null)
    {
        var writer = SettingsWriter;
        var failure = await writer.UpdateAsync(change).ConfigureAwait(true);
        if (failure is not null)
        {
            if (onFailure is not null)
            {
                await onFailure(failure).ConfigureAwait(true);
            }

            return false;
        }

        _settings = writer.Current;
        SettingsChanged?.Invoke(_settings);
        return true;
    }

    /// <summary>Applies a change to the stored settings and reports how it went.</summary>
    /// <remarks>
    /// A TRANSFORM, NOT A RECORD, AND MY REASON FOR ALLOWING A RECORD WAS SIMPLY WRONG. This took a
    /// prebuilt AppSettings on the grounds that its callers replace the whole thing deliberately.
    /// They do not: a profile import calls Apply, which preserves machine-local choices and app
    /// state, so it is a partial change like every other. A record built before the gate is stale by
    /// the time the gate opens, and writing it back discards whatever ran in between.
    /// </remarks>
    private async Task<bool> TrySaveAsync(
        Func<AppSettings, AppSettings> change,
        string title,
        string message)
    {
        var saved = await UpdateSettingsAsync(
            change,
            exception =>
            {
                // THREE CAUSES, THREE ANSWERS, AND FOLDING THEM COST THE MOST USEFUL ONE. The store
                // reports invalid settings as an ArgumentException; collapsing that into "storage is
                // unavailable" tells somebody their disk is broken when a value they typed is out of
                // range, which sends them looking in exactly the wrong place.
                if (exception is ObjectDisposedException)
                {
                    return Task.CompletedTask;
                }

                var (title, body) = exception switch
                {
                    ArgumentException => (
                        "Settings were not saved",
                        "One or more values are invalid. Your previous settings remain active."),
                    UnauthorizedAccessException or SecurityException => (
                        "Windows blocked settings storage",
                        "Your previous settings remain active."),
                    _ => (
                        "Settings storage is unavailable",
                        "Your previous settings remain active."),
                };

                ShowMessage(title, body, InfoBarSeverity.Error);
                return Task.CompletedTask;
            }).ConfigureAwait(true);

        if (saved)
        {
            ApplySettingsToControls();
            ShowMessage(title, message, InfoBarSeverity.Success);
        }

        return saved;
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
            AutoStopToggle.IsOn = preferences.Dictation.AutoStopEnabled;
            AutoStopSecondsBox.Value = preferences.Dictation.AutoStopSilenceSeconds;
            UpdateAutoStopAvailability();
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
            CopyInsteadOfPasteToggle.IsOn = preferences.CopyInsteadOfPaste;
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
            SetLiveText(
                DiagnosticsStatusText,
    _telemetryAvailable
                    ? "Anonymous sharing is off until you explicitly enable and save it. Local exports and uploads contain only the typed fields listed here."
                    : "No telemetry upload channel is configured in this development build. Local content-free diagnostics can still be retained, exported, or disabled.");
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
        OllamaEndpointTextBoxRow.Visibility = provider == PolishProvider.Ollama
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApiKeyPasswordBoxRow.Visibility = isCloudProvider
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApiKeyButtonPanel.Visibility = isCloudProvider
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshPolishModelsButtonRow.Visibility = provider is PolishProvider.Ollama or
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

        // All three model controls follow the PROVIDER, not just the two that used to. With the
        // provider set to None the picker and the refresh button were correctly disabled while
        // the free-text Model ID stayed live and in the tab order, so a user could type a model
        // id for a provider that does not exist. One of a pair got the guard and its sibling did
        // not, which is the shape that survives review because the page looks mostly right.
        var providerUsesAModel = provider is PolishProvider.Ollama or
            PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini;
        RefreshPolishModelsButton.IsEnabled = providerUsesAModel;
        PolishModelTextBox.IsEnabled = providerUsesAModel;
        PolishModelPicker.ItemsSource = choices;
        PolishModelPicker.IsEnabled = providerUsesAModel && choices.Count > 0;
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
            SetLiveText(ApiKeyStatusText, discoveryNotice);
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
            SetLiveText(
                ApiKeyStatusText,
    provider switch
                {
                    PolishProvider.Ollama => "Ollama runs on this PC and does not use a cloud API key.",
                    PolishProvider.EgOne => "EG-1 runs on this PC and does not use a cloud API key.",
                    _ => "AI polish is off; no provider key is used.",
            
                });
            return;
        }

        SetLiveText(
            ApiKeyStatusText,
    _apiKeyStore.GetStatus(provider) switch
            {
                ApiKeyReadStatus.Found =>
                    $"{CredentialArticle(provider)} {ProviderDisplayName(provider)} key is stored in Windows Credential Manager.",
                // SAY WHAT IT MEANS FOR THE DICTATION, NOT JUST WHAT IS TRUE OF THE MACHINE. "No key is
                // stored" is a fact about storage; the thing a person needs to know is that every
                // dictation will come out unpolished until they add one, and nothing will tell them
                // again at the moment it happens. macOS carries the same warning for the same reason.
                ApiKeyReadStatus.Missing =>
                    $"No {ProviderDisplayName(provider)} key is stored on this PC. Dictation still "
                        + "works, and cleanup falls back to your raw, unedited text every time until "
                        + "you add one.",
                _ => "Windows Credential Manager status is unavailable. No key value was revealed.",
        
            });
    }

    /// <summary>Sets a live region's text and visibility, and announces once if anything changed.</summary>
    /// <remarks>
    /// ONE CALL, BECAUSE TWO CALLS ANNOUNCE TWICE. Separate text and visibility helpers each raised,
    /// so showing a region and then filling it announced the OLD or empty text first and the new text
    /// second - the alias suggestion status and the speed check both did exactly that. Reassigning
    /// the same visibility announced again for no change at all.
    ///
    /// RAISED ONLY WHEN THE FINAL STATE IS VISIBLE AND SOMETHING ACTUALLY MOVED. Announcing text
    /// nobody can see is noise, and announcing an unchanged region is noise that repeats.
    ///
    /// IT HAS TO BE A CONTROL WITH A PEER. WinUI creates no automation peer for a Border or a Panel,
    /// so a live region declared on one has nothing to raise through and the raise silently does
    /// nothing. TextBlock has one.
    /// </remarks>
    private static void SetLiveRegion(TextBlock region, string text, Visibility visibility)
    {
        var textChanged = !string.Equals(region.Text, text, StringComparison.Ordinal);
        var becameVisible = region.Visibility != visibility && visibility == Visibility.Visible;

        region.Text = text;
        region.Visibility = visibility;

        if (visibility == Visibility.Visible && (textChanged || becameVisible) && IsOnAVisiblePage(region))
        {
            var peer = FrameworkElementAutomationPeer.FromElement(region)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(region);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    /// <summary>The last history outcome a screen reader was told about.</summary>
    private string? _lastAnnouncedHistoryState;

    /// <summary>The outcome waiting to be announced, if the page or the typing is not ready.</summary>
    private string? _pendingHistoryAnnouncement;

    /// <summary>Holds a search result back until the typing stops.</summary>
    private readonly DispatcherTimer _historyAnnounceDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(600),
    };

    /// <summary>Shows or hides one of the history cards.</summary>
    /// <remarks>
    /// A HELPER SO THE LIVE REGIONS ARE NOT WRITTEN DIRECTLY. HistoryList is a live region and its
    /// visibility changes on every refresh; assigning it in place forced the gate to grant that
    /// region a blanket exemption, which then excused any other direct write to it anywhere in the
    /// file. Going through here means the gate needs no exemption at all.
    /// </remarks>
    private static void ShowCard(FrameworkElement card, bool show) =>
        card.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Announces how many dictations the list is showing.</summary>
    /// <remarks>
    /// THE COUNT GOES ON THE NAME BEFORE THE RAISE, because a live region announces what it is
    /// called, and a list's own contents are not a sentence anybody wants read out on every refresh.
    /// </remarks>
    private bool AnnounceHistoryCount(int itemCount)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            HistoryList,
            $"Transcript history, {itemCount} dictation{(itemCount == 1 ? string.Empty : "s")}.");
        return AnnounceLiveRegion(HistoryList);
    }

    /// <summary>Announces a region that carries its own words, rather than being assigned them.</summary>
    /// <remarks>
    /// FOR THE CARDS THAT ALREADY SAY IT. A history card's title is fixed in markup - "No dictations
    /// yet" never changes - so there is nothing to assign and nothing for the atomic setter to
    /// compare. What changes is which card is showing, and that is the news.
    /// </remarks>
    /// <returns>True when the announcement was actually raised.</returns>
    private static bool AnnounceLiveRegion(FrameworkElement region)
    {
        if (!IsOnAVisiblePage(region))
        {
            return false;
        }

        var peer = FrameworkElementAutomationPeer.FromElement(region)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(region);
        if (peer is null)
        {
            return false;
        }

        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        return true;
    }

    /// <summary>Whether every ancestor of a region is showing, not just the region itself.</summary>
    /// <remarks>
    /// A VISIBLE CONTROL ON A COLLAPSED PAGE IS NOT VISIBLE. Every settings page in this window is
    /// built once and collapsed until it is chosen, so history finishing its load while the user is
    /// on Home announced "History loaded" from a page nobody was looking at. The control's own
    /// Visibility says nothing about that.
    ///
    /// PARENTS, NOT IsOffscreen. A status scrolled out of its own viewport is still on the page the
    /// user is reading and should still announce; a status on a collapsed page should not. Those are
    /// different questions and only the first one is about ancestry.
    /// </remarks>
    private static bool IsOnAVisiblePage(DependencyObject region)
    {
        for (var node = region; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Sets a live region's text, leaving its visibility alone.</summary>
    private static void SetLiveText(TextBlock region, string text) =>
        SetLiveRegion(region, text, region.Visibility);

    /// <summary>Shows or hides a live region without changing its words.</summary>
    /// <remarks>
    /// A REGION THAT DISAPPEARS IS NOT ANNOUNCED, which is why this defers to the same setter: the
    /// raise happens only when the final state is visible. Hiding something is not news.
    /// </remarks>
    private static void SetLiveVisibility(TextBlock region, Visibility visibility) =>
        SetLiveRegion(region, region.Text, visibility);

    /// <summary>What this build's release notes are called, for the unread mark.</summary>
    private string _releaseNotesIdentity = string.Empty;

    /// <summary>Shows or hides the unread mark, and says so to a screen reader.</summary>
    /// <remarks>
    /// THE ANNOUNCEMENT GOES ON THE NAVIGATION ITEM, NOT ON THE BADGE. An InfoBadge has no standalone
    /// screen-reader presence, so a name set on it is read by nobody - the mark was visible and
    /// silent. Microsoft's own guidance is to put the status on the parent, which is also where it
    /// belongs: what has news is the destination, not the dot.
    /// </remarks>
    private void ShowReleaseNotesMark(bool unread)
    {
        if (WhatsNewBadge.Visibility == (unread ? Visibility.Visible : Visibility.Collapsed))
        {
            return;
        }

        WhatsNewBadge.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(
            WhatsNewNavItem,
            unread ? "New release notes you have not read" : string.Empty);

        // ItemStatus IS READ WHEN SOMEBODY REACHES THE ITEM, WHICH IS NOT THE SAME AS BEING TOLD.
        // It is the right place for the status - Microsoft's own guidance, and what has news is the
        // destination rather than the dot - but on its own the mark is silent to anyone whose focus
        // is elsewhere, which is everyone at the moment it appears. One notification says it once.
        //
        // CurrentThenMostRecent, so a mark that appears and clears quickly does not queue two
        // sentences that arrive after both are out of date.
        var peer = FrameworkElementAutomationPeer.FromElement(WhatsNewNavItem)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(WhatsNewNavItem);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.Other,
            AutomationNotificationProcessing.CurrentThenMostRecent,
            unread
                ? "New release notes you have not read."
                : "Release notes marked as read.",
            "EnviousWisprReleaseNotes");
    }

    /// <summary>Records that these notes have been read, and takes the mark off.</summary>
    /// <remarks>
    /// THE MARK GOES THE MOMENT THE PAGE OPENS, not when the settings write finishes. A dot that
    /// lingers while a disk write completes reads as a page that did not register the visit, and if
    /// the write fails the worst outcome is being shown the notes again after a restart - which is
    /// the harmless direction.
    /// </remarks>
    private async Task MarkReleaseNotesSeenAsync()
    {
        ShowReleaseNotesMark(false);
        if (!ReleaseNotesMark.IsUnread(_settings.LastSeenReleaseNotes, _releaseNotesIdentity))
        {
            return;
        }

        await UpdateSettingsAsync(current => current with
        {
            LastSeenReleaseNotes = _releaseNotesIdentity,
        }).ConfigureAwait(true);
    }

    /// <summary>Commits a setting when the row beside its switch is tapped.</summary>
    /// <remarks>
    /// macOS COMMITS ON THE WHOLE ROW AND WINDOWS COMMITTED ONLY ON THE SWITCH, leaving two thirds
    /// of every settings row dead. Measured by clicking both ends of one on the running app: the
    /// left turned it on, the right did nothing.
    ///
    /// A TRANSPARENT ROW RATHER THAN A REBUILT CONTROL. Stretching the ToggleSwitch was tried and
    /// reverted - it widened the rectangle the accessibility tree reports without widening the area
    /// a pointer can reach, because WinUI does not hit-test space whose background is null and the
    /// stock template does not bind the control's background to anything that does. A Grid WITH a
    /// background is hit-tested, and it needs no copy of a control used on ten pages.
    ///
    /// A TAP ON THE SWITCH ITSELF IS LEFT ALONE. It bubbles up to here after the switch has already
    /// acted, and flipping again would undo it - so a tap inside the switch's own width is ignored
    /// and the control keeps its own behaviour, including the drag its track supports.
    /// </remarks>
    private void SettingRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Panel row)
        {
            return;
        }

        var toggle = row.Children.OfType<ToggleSwitch>().FirstOrDefault();
        if (toggle is null || !toggle.IsEnabled)
        {
            return;
        }

        // THE SWITCH OWNS ITS SWITCH AND NOTHING MORE, AND THE SWITCH IS NOW ALL IT IS. Every one of
        // these toggles carries no Header: its label is a sibling TextBlock in the same row, so the
        // control's own rectangle covers the switch and nothing a person would aim at expecting the
        // label to work. An earlier version resolved the template part named SwitchAreaGrid to draw
        // this line, which read a name Microsoft does not publish as a contract and would have gone
        // quietly back to a dead label the first time a template changed.
        var where = e.GetPosition(toggle);
        if (where.X >= 0 && where.X <= toggle.ActualWidth &&
            where.Y >= 0 && where.Y <= toggle.ActualHeight)
        {
            return;
        }

        toggle.IsOn = !toggle.IsOn;
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
        // A confirmation belongs to the page that produced it. Measured on the running app:
        // "Snippet removed" was still displayed minutes later across Clipboard, History, All
        // Settings and Keybinds, where it reads as a message about the page you are now looking
        // at rather than the one you left.
        OperationInfoBar.IsOpen = false;

        var settingsPage = tag.StartsWith("settings-", StringComparison.Ordinal);
        var helpPage = tag == "help" || tag.StartsWith("help-", StringComparison.Ordinal);
        HomePage.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "history")
        {
            AnnounceHistoryOnPageShown();
        }
        WhatsNewPage.Visibility = tag == "whats-new" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "whats-new")
        {
            _ = MarkReleaseNotesSeenAsync();
        }
        DictionaryPage.Visibility = tag == "dictionary" ? Visibility.Visible : Visibility.Collapsed;
        SnippetsPage.Visibility = tag == "snippets" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = settingsPage ? Visibility.Visible : Visibility.Collapsed;
        // The pinned action bar belongs to the settings page and appears with it. Set on the
        // same line as the page it serves, so the two cannot drift apart.
        SaveSettingsBar.Visibility = SettingsPage.Visibility;
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

        // Whichever page is now visible is the one that arrived. Reading it back from the pages
        // themselves rather than re-deriving it from the tag means a page added later cannot be
        // left out of the animation while still being reachable from the sidebar.
        foreach (var page in Pages())
        {
            if (page.Visibility == Visibility.Visible)
            {
                PlayPageEntrance(page);
                break;
            }
        }
    }

    /// <summary>Every page the sidebar can reach, in the order they are declared.</summary>
    private ScrollViewer[] Pages() =>
    [
        HomePage,
        HistoryPage,
        WhatsNewPage,
        DictionaryPage,
        SnippetsPage,
        SettingsPage,
        HelpPage,
    ];

    /// <summary>The glyph a page header shows, read from that page's own sidebar row.</summary>
    /// <remarks>
    /// ONE ICON PER DESTINATION, AND THE SIDEBAR OWNS IT. Page headers used to carry their own
    /// copy of the glyph, so every icon change was two edits and people remembered one. Five
    /// pages drifted, and two of them drifted onto another page's icon: Backup showed
    /// Clipboard's, and Dictation history showed History's. Reading the header off the row makes
    /// the pair unable to disagree, which is a different thing from making them agree today.
    /// </remarks>
    private string NavigationGlyphFor(string tag)
    {
        foreach (var row in NavigationRows())
        {
            if (row.Tag as string == tag && row.Icon is FontIcon icon)
            {
                return icon.Glyph;
            }
        }

        // A page tag with no sidebar row is an authoring mistake, and the test suite refuses the
        // build before it can ship. The app still navigates rather than throwing in the user's
        // face, and an empty tile is the visible tell.
        return string.Empty;
    }

    /// <summary>Every sidebar row, top list and footer list together.</summary>
    private IEnumerable<NavigationViewItem> NavigationRows() =>
        ProductNavigation.MenuItems
            .Concat(ProductNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>();

    private void ConfigureSettingsPage(string tag)
    {
        var (title, description, section) = tag switch
        {
            "settings-appearance" => (
                "Appearance",
                "The theme, and where the recording pill appears while you dictate.",
                (FrameworkElement?)AppearanceSection),
            "settings-transcription" => (
                "Transcription",
                "The speech engine that turns your voice into text.",
                (FrameworkElement?)TranscriptionEngineSection),
            "settings-live-preview" => (
                "Live Preview",
                "See your words on screen while you are still speaking, and choose how the recording pill looks.",
                (FrameworkElement?)LivePreviewSection),
            "settings-microphone" => (
                "Microphone",
                "Choose your input source and readiness behavior.",
                (FrameworkElement?)MicrophoneSection),
            "settings-sounds" => (
                "Sounds",
                "Play a short sound when recording starts and stops.",
                (FrameworkElement?)SoundSection),
            "settings-keybinds" => (
                "Keybinds",
                "Set the keybinds that start, stop, and cancel dictation.",
                (FrameworkElement?)KeybindsSection),
            "settings-ai-polish" => (
                "AI Polish",
                "Clean up and rewrite your dictation with AI.",
                (FrameworkElement?)AiPolishSection),
            "settings-history" => (
                "Dictation history",
                "Whether your dictations are saved on this PC, and for how long.",
                (FrameworkElement?)HistorySettingsSection),
            "settings-diagnostics" => (
                "Diagnostics",
                "What EnviousWispr records about how it is running, and what it never records.",
                (FrameworkElement?)DiagnosticsSection),
            "settings-profile" => (
                "Backup",
                "Move your settings, words, and snippets to another PC.",
                (FrameworkElement?)PortableProfileSection),
            "settings-clipboard" => (
                "Clipboard",
                "How your transcript reaches the clipboard and the app you're in.",
                (FrameworkElement?)ClipboardSection),
            // An unrecognised tag lands on a REAL section rather than showing all of them. The
            // old default returned null, which the loop below read as "show everything" - so a
            // typo in a tag rendered the aggregate page that no longer exists.
            _ => (
                "Appearance",
                "The theme, and where the recording pill appears while you dictate.",
                (FrameworkElement?)AppearanceSection),
        };

        // THE PAGES WHOSE SETTINGS ARE READ WHEN A RECORDING STARTS, and only those. A banner on a
        // page whose settings take effect immediately would be a lie, and a lie about this is worse
        // than silence: it tells somebody to stop and start a recording for no reason.
        //
        // Keybinds holds auto-stop and Escape Recovery; Live Preview holds the preview switch;
        // Clipboard holds whether the words are pasted or copied. All are read at the press and held
        // for that recording - App.xaml.cs reads them in StartAutoStopWatch, the Started transition,
        // StartLivePreviewAsync, and the delivery options the session controller asks for.
        FrozenPerRecordingBanner.Visibility =
            tag is "settings-keybinds" or "settings-live-preview" or "settings-clipboard"
            ? Visibility.Visible
            : Visibility.Collapsed;
        FrozenPerRecordingText.Text = FrozenPerRecordingCopy;

        SettingsPageTitle.Text = title;
        SettingsPageDescription.Text = description;
        // An unrecognised tag lands on the Appearance page above, so it wears Appearance's icon.
        SettingsPageGlyph.Glyph = NavigationGlyphFor(tag) is { Length: > 0 } settingsGlyph
            ? settingsGlyph
            : NavigationGlyphFor("settings-appearance");

        // No "show everything" branch any more. Every tag resolves to exactly one section, so the
        // aggregate page cannot be reached by any route rather than merely being unlinked.
        var showTranscriptionCompanion = tag == "settings-transcription";
        foreach (var candidate in SettingsSections())
        {
            candidate.Visibility =
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
        var (title, description) = tag switch
        {
            "help-permissions" => (
                "Permissions",
                "The microphone and accessibility access EnviousWispr needs."),
            "help-updates" => (
                "Check for Updates",
                "Whether a newer EnviousWispr is available, and how to install it."),
            "help-licenses" => (
                "Open Source Licenses",
                "EnviousWispr is GPLv3 open source. The license and third-party notices."),
            _ => (
                "Help and privacy",
                "Find keyboard guidance, privacy details, updates, and licenses."),
        };

        HelpPageTitle.Text = title;
        HelpPageDescription.Text = description;
        HelpPageGlyph.Glyph = NavigationGlyphFor(tag) is { Length: > 0 } helpGlyph
            ? helpGlyph
            : NavigationGlyphFor("help");

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

    /// <summary>What every surface says about settings that freeze when a recording starts.</summary>
    /// <remarks>
    /// ONE STRING, LIKE macOS'S OWN `SettingsCopy`, because it appeared in two places and the two
    /// were already different - a banner and an inline helper line saying the same thing in
    /// different words is how a user decides one of them means something else.
    /// </remarks>
    private const string FrozenPerRecordingCopy =
        "Changes made during a recording apply to the next recording.";

    private void ShowMessage(string title, string message, InfoBarSeverity severity) =>
        ShowMessage(title, message, severity, offer: null);

    /// <summary>Shows one message, optionally carrying the one thing the user can do about it.</summary>
    /// <remarks>
    /// THE BUTTON IS CLEARED ON EVERY MESSAGE, INCLUDING THE ONES THAT DO NOT WANT ONE. An action
    /// left on the bar belongs to a message that has gone, and this app has already been bitten by
    /// exactly that shape - a "Snippet removed" confirmation that stayed visible across four other
    /// pages, reading as a message about wherever the user now was. A stale BUTTON is worse than a
    /// stale sentence, because pressing it does something.
    /// </remarks>
    private void ShowMessage(
        string title, string message, InfoBarSeverity severity, ImportConflictOffer? offer)
    {
        OperationInfoBar.Title = title;
        OperationInfoBar.Message = message;
        OperationInfoBar.Severity = severity;
        OperationInfoBar.ActionButton = offer is null ? null : BuildOfferButton(offer);
        var wasAlreadyOpen = OperationInfoBar.IsOpen;
        OperationInfoBar.IsOpen = true;
        if (!wasAlreadyOpen)
        {
            PlayNotificationEntrance();
        }
    }

    /// <summary>
    /// Opens the notification bar by growing it, instead of shoving the page down in one frame.
    /// </summary>
    /// <remarks>
    /// Measured on the running app: the bar arrived in a single frame and the page content below
    /// jumped down 76 pixels with it. The bar lives in an Auto row above the pages - which is
    /// deliberate, and fixed a worse defect where it painted OVER the page title - so its arrival
    /// is a layout change and a layout change is what has to be animated.
    ///
    /// MaxHeight IS THE PROPERTY TO ANIMATE, and the choice is not arbitrary. An Auto row takes the
    /// smaller of its content's natural height and any maximum, so growing the maximum from zero
    /// grows the row smoothly without anyone needing to know how tall the bar will be. Measuring
    /// its natural height first would need a layout pass, and a forced layout pass inside a queued
    /// callback is what crashed this app earlier in this branch.
    ///
    /// AND BECAUSE IT IS A LAYOUT PROPERTY IT NEEDS EnableDependentAnimation. The first version of
    /// this method did not set it, so the grow was silently skipped while everything around it
    /// worked - see the comment at the animation itself. The reasoning above was correct about
    /// WHICH property and silent about what animating it requires, which is the more dangerous
    /// shape: a justification that reads as settled and is only half the story.
    ///
    /// The ceiling is deliberately far above any real bar. The row stops at the content's natural
    /// height and the animation runs on past it with no visible effect, so a long message is not
    /// clipped by a number someone guessed. MaxHeight is released entirely when the animation ends,
    /// so nothing is left capped - a bar whose message grows later must still be able to grow.
    ///
    /// Only the OPEN is animated. Closing is driven by navigation and by the bar's own dismiss
    /// button, and a page that has already changed underneath a shrinking bar reads as a glitch
    /// rather than as motion. Stated rather than left as an omission someone will read as a bug.
    /// </remarks>
    private void PlayNotificationEntrance()
    {
        if (!_animationsEnabled)
        {
            return;
        }

        OperationInfoBar.MaxHeight = 0;
        OperationInfoBar.Opacity = 0;

        var duration = new Duration(TimeSpan.FromMilliseconds(NotificationEntranceMilliseconds));

        var grow = new DoubleAnimation
        {
            To = NotificationEntranceCeilingPixels,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            // WITHOUT THIS THE ANIMATION IS SILENTLY SKIPPED AND THE FIRST VERSION WAS.
            // MaxHeight is a LAYOUT property, which makes this a dependent animation, and WinUI
            // refuses to run those unless asked - with no error, no exception and no log line.
            // Measured on the running app: MaxHeight was set to 0, the grow never ran, the fade
            // animated opacity on an element clamped to zero height, and 220ms later the Completed
            // handler released MaxHeight and the bar appeared in ONE frame. A dead pause followed
            // by the exact snap the animation existed to remove.
            //
            // The same build animated a page entrance correctly through the same guard, the same
            // API and the same file, which is what ruled out every other suspect: that one targets
            // Opacity and a transform, both independent.
            //
            // A ScaleTransform would stay off the layout path and is the usual advice, and it is
            // WRONG HERE: scaling the bar would not move the page below it, because the page's
            // position comes from this row's height. The bar would grow smoothly while the page
            // still jumped - worse than either, and it would pass a check on the bar alone.
            // So the layout animation is the point rather than an oversight, and its cost is one
            // property for 220ms.
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(grow, OperationInfoBar);
        Storyboard.SetTargetProperty(grow, "MaxHeight");

        var fade = new DoubleAnimation { To = 1, Duration = duration };
        Storyboard.SetTarget(fade, OperationInfoBar);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(grow);
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => OperationInfoBar.MaxHeight = double.PositiveInfinity;
        storyboard.Begin();
    }

    /// <summary>
    /// Colours the Minimize, Maximize and Close glyphs for the theme actually on screen.
    /// </summary>
    /// <remarks>
    /// WINDOWS DRAWS THESE, NOT XAML, so setting RequestedTheme on the content does not reach them.
    /// They follow the MACHINE's theme unless told otherwise, which is invisible while the two
    /// agree and produces white glyphs on a near-white titlebar the moment a user picks Light on a
    /// machine set to Dark: 1.11:1, against the 3:1 a control glyph needs. The window offers no
    /// visible way to close it.
    ///
    /// The colour comes from the same token the window's text uses, read off a zero-size swatch
    /// bound to it, so there is exactly one definition of "the primary text colour in this theme".
    ///
    /// Backgrounds stay transparent so the buttons sit on the titlebar rather than on plates of
    /// their own; only hover and pressed take a wash, which is the Windows behaviour. Inactive
    /// glyphs are dimmed rather than recoloured, because a dimmed caption button is how Windows
    /// says the window is not focused, and that signal is worth keeping.
    /// </remarks>
    private void ApplyCaptionButtonColors()
    {
        if (ThemeColorProbe.Background is not SolidColorBrush probe)
        {
            return;
        }

        var glyph = probe.Color;
        var wash = WindowRoot.ActualTheme == ElementTheme.Dark ? (byte)0x20 : (byte)0x14;
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonForegroundColor = glyph;
        titleBar.ButtonHoverForegroundColor = glyph;
        titleBar.ButtonPressedForegroundColor = glyph;
        titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0x9B, glyph.R, glyph.G, glyph.B);
        titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(wash, glyph.R, glyph.G, glyph.B);
        titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(
            (byte)(wash + 0x10),
            glyph.R,
            glyph.G,
            glyph.B);
    }

    /// <summary>
    /// Puts the Windows 11 material behind the two cards, in place of a flat painted canvas.
    /// </summary>
    /// <remarks>
    /// Mica is the single loudest "this is a Windows 11 app" signal there is - Settings, Photos and
    /// Terminal all sit on it - and its absence is most of why this window read as a form.
    ///
    /// THE ORDER IS THE WHOLE THING. The canvas brush paints OVER the material, so it has to be
    /// cleared; but clearing it on a machine where Mica is unavailable leaves a window with no
    /// background at all. So the brush is cleared only AFTER the backdrop is actually assigned, and
    /// the unsupported path changes nothing and needs no fallback of its own.
    ///
    /// Mica follows the window content's theme, and ApplyTheme sets RequestedTheme on exactly that
    /// element, so the light and dark materials follow the user's choice without a second switch.
    /// </remarks>
    private void TryUseMicaBackdrop()
    {
        if (!MicaController.IsSupported())
        {
            return;
        }

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        WindowRoot.Background = null;
    }

    /// <summary>
    /// The short settle a Windows 11 page makes when it arrives, instead of a hard cut.
    /// </summary>
    /// <remarks>
    /// Windows 11 apps MOVE. A page that simply appears is one of the loudest not-native signals
    /// after a missing backdrop, and every page here was a Visibility flip.
    ///
    /// Hand-rolled rather than EntranceThemeTransition, and the reason is not preference: a theme
    /// transition fires when an element is ADDED to the tree, and these pages are always in the
    /// tree and only change Visibility, so the sanctioned transition would attach cleanly and never
    /// play. That is the "ships and does nothing" shape, so it is written down here rather than
    /// discovered by measuring an unchanged screen.
    ///
    /// Honours the system's animation setting. A user who has turned animations off has said so
    /// once, for every app, and Fluent motion is not exempt from that.
    /// </remarks>
    private void PlayPageEntrance(UIElement page)
    {
        if (!_animationsEnabled)
        {
            page.Opacity = 1;
            return;
        }

        var offset = new TranslateTransform { Y = PageEntranceOffsetPixels };
        page.RenderTransform = offset;
        page.Opacity = 0;

        var duration = new Duration(TimeSpan.FromMilliseconds(PageEntranceMilliseconds));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation { To = 1, Duration = duration };
        Storyboard.SetTarget(fade, page);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var settle = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(settle, offset);
        Storyboard.SetTargetProperty(settle, "Y");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(settle);
        storyboard.Begin();
    }

    /// <summary>Applies a theme to everything the app draws, not only this window.</summary>
    /// <remarks>
    /// EVERY TOP-LEVEL SURFACE, NOT JUST THIS ONE. The recording pill is its own window and the
    /// caption buttons are drawn by Windows; neither is inside this window's visual tree, so
    /// neither followed a theme set here. Both were wrong in the same way for the same reason, and
    /// the pill's was worse because this window shows a PREVIEW of it that did follow the theme -
    /// so the preview showed a pill that would never appear.
    ///
    /// The caption buttons are not called from here on purpose: they follow the RESOLVED theme,
    /// which also changes when Windows switches underneath an app left on "Use Windows setting".
    /// They listen for that instead. The pill takes the requested theme directly, because it
    /// inherits the same resolution rules once it has it.
    /// </remarks>
    private void ApplyTheme(AppTheme theme)
    {
        var requested = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        WindowRoot.RequestedTheme = requested;
        _overlayWindow.ApplyTheme(requested);
    }

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
        // The inactive group's cards are disabled, and a disabled card with no stated reason reads
        // as a control that ignores clicks. Measured on the running app: with Live Preview off,
        // Reading Well showed a purple selected-style border and greyed text, and nothing on the
        // page said why it could not be chosen. The heading is the honest place to say it - the
        // cards are remembered choices for a mode that is not currently on, not broken controls.
        WithoutWordsPillHeading.Text = withWords
            ? "Live Preview off · turn Live Preview off to use these"
            : "Live Preview off · In use";
        WithWordsPillHeading.Text = withWords
            ? "Live Preview on · In use"
            : "Live Preview on · turn Live Preview on to use this";
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
            Details = $"{TranscriptionEngineNames.DisplayName(entry.EngineId)} · {(entry.WasPolished ? "AI polished" : "deterministic cleanup")} · {(entry.WasDelivered ? "delivered" : "held safely")}{retention}";
        }

        public Guid Id { get; }

        public string Text { get; }

        public string CreatedDisplay { get; }

        public string Details { get; }

        /// <summary>
        /// What a screen reader announces for this row.
        /// </summary>
        /// <remarks>
        /// Reads as a sentence rather than a field dump: the point is what a person HEARS, so a
        /// visual separator like a middle dot is not good enough - a screen reader either
        /// announces it literally or drops it, and neither is a word.
        ///
        /// Without this a list row falls back to ToString on the bound item, and a plain class
        /// returns its fully-qualified type name - so the row would announce
        /// "EnviousWispr.App.MainWindow+HistoryItemViewModel" before reaching the dictation. The
        /// two other list types had the record version of the same defect, measured on the running
        /// app; this one is fixed from the same reading rather than waiting for history to exist.
        /// </remarks>
        public override string ToString() => $"{Text}, dictated {CreatedDisplay}";
    }
}
