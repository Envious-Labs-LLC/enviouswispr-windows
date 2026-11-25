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
using EnviousWispr.Core.Feedback;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Distribution;
using EnviousWispr.Services.Input;
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
    /// <summary>
    /// The index of the chosen card, or 0 when nothing is chosen yet.
    /// </summary>
    /// <remarks>
    /// Falls back to 0 rather than -1 deliberately: every caller feeds this straight into an enum
    /// or a clamp, and a -1 would either throw or quietly become a different setting. The first
    /// option in each of these lists is the safe default - Parakeet, None, System, Top.
    /// </remarks>
    private static int SelectedIndexOf(SelectableChoiceOption[] choices)
    {
        var index = RawSelectedIndexOf(choices);
        return index >= 0 ? index : 0;
    }

    /// <summary>The selected index as it is, -1 for nothing selected: what the General save hands the presenter, which decides what a missing choice means.</summary>
    private static int RawSelectedIndexOf(SelectableChoiceOption[] choices) =>
        Array.FindIndex(choices, choice => choice.IsSelected);

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

    // INSTANCE-OWNED, NOT STATIC. A selection is state, and state shared between two windows is a
    // choice made in one appearing in the other. There is one window today; the rule costs nothing
    // and the second instance would otherwise be a defect nobody wrote.
    private readonly SelectableChoiceOption[] PolishProviderChoices =
    [
        new("None", "Leaves the deterministic transcript unchanged."),
        new("EG-1", "Polishes locally with the bundled Envious Grammar model."),
        new("Ollama", "Polishes locally with an Ollama model on this PC."),
        new("OpenAI", "Sends transcript text directly to your OpenAI account."),
        new("Anthropic", "Sends transcript text directly to your Anthropic account."),
        new("Gemini", "Sends transcript text directly to your Google Gemini account."),
    ];

    private sealed record SearchableSettingItem(string DisplayName, string PageTag, string? TabKey = null, string? Keywords = null)
    {
        public override string ToString() => DisplayName;
    }

    private static readonly SearchableSettingItem[] SearchableSettings =
    [
        new("Transcription Engine", "settings-dictation", "engine", "model whisper parakeet speed english language spelling"),
        new("Casual mode", "settings-dictation", "engine", "casual mode period full stop punctuation"),
        new("Microphone & Input", "settings-dictation", "microphone", "mic audio device source readiness test"),
        new("Live Preview", "settings-dictation", "live-preview", "overlay text preview words on screen"),
        new("Recording Pill", "settings-dictation", "pill", "position design capsule reading rail overlay"),
        new("Sounds", "settings-dictation", "sounds", "chime audio cue feedback volume"),
        new("Clipboard & Paste", "settings-dictation", "clipboard", "copy paste direct insert"),
        new("Keybinds", "settings-keybinds", null, "shortcut hotkey push to talk toggle cancel hold"),
        new("Transcribe a File", "settings-transcribe-file", null, "audio recording file wav mp3 m4a dropzone"),
        new("AI Polish", "settings-ai-polish", null, "rewrite grammar fix format eg-1 ollama openai llm"),
        new("Dictionary", "dictionary", "your-words", "custom words vocabulary spellings jargon replacements"),
        new("Vocabulary Packs", "dictionary", "packs", "medical legal tech terminology packs"),
        new("Learn From", "dictionary", "learn-from", "import analyze documents"),
        new("Quick Add Words", "dictionary", "quick-add", "shortcut add word"),
        new("Snippets", "snippets", null, "text expansion templates boilerplates phrases"),
        new("Appearance & Theme", "settings-app", "appearance", "light dark system theme high contrast"),
        new("Permissions", "settings-app", "permissions", "microphone privacy"),
        new("Privacy & History", "settings-app", "privacy", "retention days safe held local dictations delete"),
        new("Backup & Profile", "settings-app", "backup", "export import portable profile reset data your data"),
        new("Updates & Licenses", "settings-app", "updates-licenses", "version release notes third party licenses"),
        new("Diagnostics", "settings-diagnostics", null, "logs telemetry troubleshooting reports"),
        new("History", "history", null, "diary transcripts search dictations"),
        new("Help & Documentation", "help", null, "guide support faq readme"),
        new("What's New", "whats-new", null, "release notes updates changelog features"),
    ];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML event handler")]
    private void SidebarSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var query = sender.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                sender.ItemsSource = null;
            }
            else
            {
                var matches = SearchableSettings
                    .Where(item => item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                                   || (item.Keywords?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();
                sender.ItemsSource = matches.Count > 0 ? matches : new List<string> { "No matching settings found" };
            }
        }
    }

    private void SidebarSearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SearchableSettingItem item)
        {
            OpenPage(item.PageTag, item.TabKey);
            sender.Text = string.Empty;
        }
        else if (args.SelectedItem is string)
        {
            sender.Text = string.Empty;
        }
    }

    /// <summary>Every settings write this window makes, and the answer when one is refused.</summary>
    /// <remarks>
    /// THE RULES LIVE OUTSIDE THE WINDOW SO THEY CAN BE PROVEN. Two saves overlapping, a storage
    /// failure, an Appearance click that must write three fields and no more, a write after the
    /// drain - none of those happen in a test that drives this window one call at a time, and every
    /// one of them is a SettingsPresenterTests case. What stays here is reading the controls,
    /// drawing the outcome and applying the theme.
    /// </remarks>
    /// <summary>
    /// The window's presentation for its life: the one settings writer every presenter shares, the
    /// presenters over it, the microphone test and the device catalogue - built by the shell's
    /// composition and owned by the session, not by this window (plan-2 step 12).
    /// </summary>
    private readonly WindowPresentationSession _session;

    private readonly bool _telemetryAvailable;

    private readonly bool _launchedCrashReports;

    // Whether this copy came from the Microsoft Store; an unpackaged copy has nothing to check.
    private readonly bool _storeInstalled;

    // Whether the last Store answer offered an update. A busy refusal leaves it as it was, so the
    // person can install once the dictation or the file has finished without checking again.
    private bool _updateOffered;
    private readonly DictationOverlayWindow _overlayWindow;
    private readonly RecordingSoundCuePlayer _recordingSoundPlayer;
    private readonly RecordingSoundCueCoordinator _recordingSoundCoordinator;
    private long _recordingSoundAttempt;
    private bool _shuttingDownProductWindows;
    private readonly List<DictationHistoryEntry> _history = [];
    private IReadOnlyList<MicrophoneChoice> _microphones = [];
    /// <summary>The session's catalogue, once the microphone list has asked for it; the window subscribes and unsubscribes, the session owns and disposes.</summary>
    private IAudioDeviceCatalog? _deviceCatalog;
    private AppSettings _settings;
    private HistoryLoadStatus _historyLoadStatus = HistoryLoadStatus.Missing;
    private bool _isHistoryLoading = true;
    private bool _isApplyingSettings;
    private bool _initialFocusAssigned;
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

    // Every modifier held together since the candidate began, so two held at once can be offered as a set
    // (Ctrl+Win). Cleared with the candidate. Ref: #66.
    private HotkeyModifiers _keybindModifierSet;
    private WindowsKeybindCaptureSession? _keybindCaptureSession;
    private KeybindCaptureSlot? _capturingSlot;
    private TextBox? _capturingBox;
    private Button? _capturingButton;
    private string _capturingPreviousValue = string.Empty;
    private bool _isUpdatingCaptureDisplay;
    private CancellationTokenSource? _soundPreviewCancellation;

    private enum KeybindCaptureSlot
    {
        Recording,
        Cancel,
        QuickAdd,
        PasteLast,
        CopyLast,
    }
    private int _recordingModeIndex;
    private int _selectedThemeIndex;
    private int _selectedOverlayPositionIndex;
    private RecordingPillDesign _lastWithoutWordsPillDesign = RecordingPillDesign.Classic;
    private RecordingSoundPairing _selectedRecordingSoundPairing = RecordingSoundPairing.WhisperTick;
    private readonly Dictionary<RecordingSoundPairing, Border> _chimeCards = new();
    private readonly Dictionary<RecordingSoundPairing, Button> _chimeSelectButtons = new();
    private readonly Dictionary<RecordingSoundPairing, Border> _chimeBadges = new();
    private readonly Dictionary<RecordingSoundPairing, List<Border>> _chimeWaveformBars = new();
    private readonly Dictionary<RecordingSoundPairing, Button> _chimePlayButtons = new();
    private PolishProvider _lastNonNonePolishProvider = PolishProvider.EgOne;
    private PolishProvider? _lastActivePolishProvider;
    private FinalAsrEngine? _lastActiveEngine;
    private string _selectedPolishModelId = string.Empty;
    private string? _selectedEgOneModelId;
    private bool _isUpdatingPolishModelPicker;
    private bool _isUpdatingEgOneCards;
    private HardwareProfile? _hardwareProfile;
    private bool _isRepolishing;

    public MainWindow(
        AppSettings settings,
        WindowPresentationSession session,
        WindowLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(launch);

        _settings = settings;
        _session = session;
        _telemetryAvailable = launch.TelemetryAvailable;
        _launchedCrashReports = launch.LaunchedCrashReports;
        var settingsLoadStatus = launch.SettingsLoadStatus;
        var releaseIdentity = launch.ReleaseIdentity;
        _storeInstalled = launch.StoreInstalled;
        var installedVersion = launch.InstalledVersion;
        _dataDirectory = launch.DataDirectory;

        InitializeComponent();

        // THE PICKER STARTS ON THE ORDINARY RULE BY NAME, NOT BY POSITION. Starting it on whichever
        // item happens to be listed first is the same coupling this feature just removed, wearing
        // the word "default" as a coincidence rather than as a choice.
        ResetMatchStrictness();
        UppercaseSectionEyebrows();
        PlaceSidebarResizeHandle();

        PolishProviderComboBox.ItemsSource = PolishProviderChoices;

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
        // A CHORD HALF-CAPTURED IN ONE FIELD NEVER FINISHES IN ANOTHER: focus moving either way forgets it, so a set
        // begun here cannot be completed there, nor a release seen elsewhere leave it held. Ref: #66 review.
        foreach (var box in new[] { HotkeyTextBox, CancelHotkeyTextBox, QuickAddHotkeyTextBox, PasteLastHotkeyTextBox, CopyLastHotkeyTextBox })
        {
            box.GotFocus += (_, _) => { ForgetKeybindChord(); KeybindCaptureActiveChanged?.Invoke(true); };
            box.LostFocus += (_, _) =>
            {
                ForgetKeybindChord();
                if (_keybindCaptureSession is not null && ReferenceEquals(box, _capturingBox))
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_keybindCaptureSession is null || _capturingBox is null)
                        {
                            return;
                        }

                        var focused = Content?.XamlRoot is { } root
                            ? FocusManager.GetFocusedElement(root)
                            : null;
                        if (!ReferenceEquals(focused, _capturingBox) && !ReferenceEquals(focused, _capturingButton))
                        {
                            CancelKeybindCapture();
                        }
                    });
                    return;
                }

                KeybindCaptureActiveChanged?.Invoke(false);
                _ = TriggerAutoSaveAsync();
            };
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
        _recordingSoundPlayer = new RecordingSoundCuePlayer(
            onPlaybackFailed: OnRecordingSoundCueFailed);
        _recordingSoundCoordinator = new RecordingSoundCueCoordinator(PlayRecordingSoundCue);
        PreloadRecordingSoundsInBackground(_selectedRecordingSoundPairing);
        InitializeChimeCards();
        SetTranscribeFileStep(1);
        _overlayWindow = new DictationOverlayWindow();
        _historyAnnounceDebounce.Tick += OnHistoryAnnounceDue;
        _overlayWindow.ActionInvoked += OnPillActionInvoked;
        _overlayWindow.ApplyPreferences(
            settings.Preferences.OverlayPosition,
            settings.Preferences.LivePreviewEnabled,
            settings.Preferences.PillDesignWithoutWords,
            settings.Preferences.PillDesignWithWords);
        DispatcherQueue?.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (!_shuttingDownProductWindows)
                {
                    _overlayWindow.PreWarm();
                }
            });
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // The caption glyphs follow the theme from here, and from nowhere else. Subscribing to the
        // resolved theme rather than calling this from each place a theme can change is what makes
        // it correct for the case nobody remembers: the user leaves the app on "Use Windows
        // setting" and Windows itself switches at sunset.
        ApplyCaptionButtonColors();
        WindowRoot.ActualThemeChanged += (_, _) =>
        {
            ApplyCaptionButtonColors();
            UpdateChimeCardsVisuals();
            UpdateRecordingModeButtons();
            UpdateThemeSegmentButtons();
            RenderSpeechEngineCards();
            UpdateOverlayPositionSegmentButtons();
            UpdateHistoryFilterSegmentButtons();
        };
        TryUseMicaBackdrop();
        ConfigureMinimumWindowWidth();
        ResizeToDefault();
        AppWindow.SetIcon(Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Brand",
            "EnviousWispr.ico"));
        Activated += OnWindowActivated;
        Closed += (_, _) => CancelKeybindCapture();

        // BUILT FROM WHAT WAS WRITTEN DOWN, so somebody who has already been asked three times about
        // Spanish is not asked three more times because they restarted the app.
        _languageSuggestions = new LanguageLockSuggester(settings.LanguageOfferHistory);
        ApplyTheme(settings.Preferences.Theme);
        ApplySettingsToControls();
        WireSettingsAutoSave();
        ShowOnboarding(!settings.HasCompletedOnboarding);
        ProductNavigation.SelectedItem = HistoryNavItem;
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
        SidebarVersionText.Text = FormatSidebarVersion(installedVersion, assemblyVersion);
        BuildInfoText.Text = $"{releaseIdentity.DisplayName} {assemblyVersion} · {releaseIdentity.ChannelName}";
        WhatsNewBuildInfoText.Text = BuildInfoText.Text;

        // THE MARK IS ON UNTIL THIS BUILD'S NOTES HAVE BEEN OPENED. Comparing the stored string with
        // the current one is the whole rule: a fresh install has stored nothing, so the notes are
        // new to them, which is true; an update changes the string, so the mark comes back.
        _releaseNotesIdentity = BuildInfoText.Text;
        ShowReleaseNotesMark(
            ReleaseNotesMark.IsUnread(settings.LastSeenReleaseNotes, _releaseNotesIdentity));
        SetLiveText(
            UpdateStatusText,
            _storeInstalled
                ? $"Version {installedVersion} is installed from the Microsoft Store."
                : NotFromStoreSentence);
        CheckForUpdatesButton.IsEnabled = _storeInstalled;
        if (settingsLoadStatus is SettingsLoadStatus.Invalid or SettingsLoadStatus.Migrated)
        {
            ShowMessage(
                "Previous settings recovered",
                "Previous settings were recovered safely.",
                InfoBarSeverity.Informational);
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

    public event Action<RecordingSoundPairing, RecordingSoundMoment>? RecordingSoundPlaybackFailed;

    public event Action<AudioDeviceChange>? AudioDevicesChanged;

    public event Action? RecoveryCleared;

    /// <summary>The person pressed Undo beside an Escape Recovery on Home.</summary>
    public event Action? RecoveryUndoRequested;

    /// <summary>The person asked for the selected History entry to be pasted into the app they were using.</summary>
    public event Action<Guid>? HistoryPasteRequested;

    /// <summary>The person asked for the speech model this build pins to be downloaded.</summary>
    public event Action? ModelDownloadRequested;

    /// <summary>The person asked for the running download to stop.</summary>
    public event Action? ModelDownloadCancelRequested;

    /// <summary>The person asked to download the Parakeet or Whisper final speech model.</summary>
    public event Action<FinalAsrEngine>? SpeechEngineDownloadRequested;

    /// <summary>The person confirmed removing the Parakeet or Whisper final speech model.</summary>
    public event Action<FinalAsrEngine>? SpeechEngineRemoveRequested;

    /// <summary>Returns whether dictation or file transcription currently holds the engine.</summary>
    public Func<bool>? IsSpeechEngineBusy { get; set; }

    /// <summary>The person asked to download an EG-1 model size.</summary>
    public event Action<string>? EgOneModelDownloadRequested;

    /// <summary>The person asked for the running EG-1 model download to stop.</summary>
    public event Action? EgOneModelDownloadCancelRequested;

    /// <summary>The person confirmed removing an installed EG-1 model size.</summary>
    public event Action<string>? EgOneModelRemoveRequested;

    private SpeechEngineModelState _parakeetModelState = SpeechEngineModelState.Unknown(EnginePolishPresentation.ParakeetBundleSizeLabel);
    private SpeechEngineModelState _whisperModelState = SpeechEngineModelState.Unknown(EnginePolishPresentation.WhisperBundleSizeLabel);
    private FinalAsrEngine? _activeSpeechDownloadEngine;
    private readonly Dictionary<string, EgOneModelState> _egOneModelStates = new(StringComparer.Ordinal)
    {
        [EnginePolishPresentation.EgOneFullModelId] = EgOneModelState.Unknown(EnginePolishPresentation.EgOneFullModelId),
        [EnginePolishPresentation.EgOneMiniModelId] = EgOneModelState.Unknown(EnginePolishPresentation.EgOneMiniModelId),
        [EnginePolishPresentation.EgOneMicroModelId] = EgOneModelState.Unknown(EnginePolishPresentation.EgOneMicroModelId),
    };
    private bool _egOneServerExecutableExists;
    private string? _activeEgOneDownloadModelId;
    private bool _hasEgOneInventorySnapshot;

    /// <summary>The person asked for the 190 MB Whisper Live Preview model to be downloaded (#127).</summary>
    public event Action? PreviewModelDownloadRequested;

    /// <summary>The person asked for the downloaded Whisper Live Preview model to be removed (#127).</summary>
    public event Action? PreviewModelRemoveRequested;

    private bool _livePreviewParakeetInstalled = true;
    private bool _livePreviewWhisperInstalled;
    private bool _livePreviewCanRemoveWhisper;
    private SpeechModelDelivery _livePreviewWhisperDeliveryStage = SpeechModelDelivery.Unknown;
    private double? _livePreviewWhisperDownloadPercent;
    private string? _livePreviewWhisperDeliveryDetail;

    /// <summary>The person asked for the NVIDIA graphics runtime to be downloaded.</summary>
    public event Action? GraphicsRuntimeDownloadRequested;

    /// <summary>The person asked for the running graphics runtime download to stop.</summary>
    public event Action? GraphicsRuntimeDownloadCancelRequested;

    public event Action<bool, int>? DiagnosticsExportCompleted;

    public event Action? UpdateCheckRequested;

    public event Action? UpdateApplyRequested;

    public event Action<bool>? StartupToggleRequested;

    public Func<Task<StartupRegistrationResult>>? LoadStartupRegistration { get; set; }

    public Func<FeedbackSubmission, CancellationToken, Task<FeedbackSendResult>>? SubmitFeedback { get; set; }

    public Func<FeedbackDiagnosticsSnapshot>? BuildFeedbackDiagnosticsSnapshot { get; set; }

    public FeedbackDraftStore? FeedbackDraftStore { get; set; }

    public string? FeedbackDataDirectory { get; set; }

    public Func<CancellationToken, Task>? FlushFeedbackOutbox { get; set; }

    public Func<CancellationToken, Task<bool>>? CheckUndeliverableFeedback { get; set; }

    private bool _isFeedbackDialogOpen;

    private bool _isUpdatingStartupRegistration;
    private bool _startupOperationInProgress;
    private int _startupOperationGeneration;

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

    private static string FormatSidebarVersion(string? installedVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(installedVersion) &&
            Version.TryParse(installedVersion, out var packageVersion))
        {
            return $"v{packageVersion.Major}.{packageVersion.Minor}.{Math.Max(0, packageVersion.Build)}";
        }

        if (assemblyVersion is not null)
        {
            return $"v{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(0, assemblyVersion.Build)}";
        }

        return "v0.0.0";
    }

    private void ConfigureMinimumWindowWidth()
    {
        var contentCardMinimumWidth = (double)Application.Current.Resources["BrandContentCardMinimumWidth"];

        // Microsoft.UI.Xaml.Window.MinWidth does not exist in the Windows App SDK this project
        // builds against, so the minimum is set on the window's presenter instead. The
        // presenter is only an OverlappedPresenter for a normal window; if the app is ever
        // shown full-screen or compact-overlay there is nothing to constrain and nothing to do.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Scaled for the same reason ResizeToDefault is: the inputs are layout units
            // and the presenter's floor is in physical pixels. Unscaled, the minimum shrinks by
            // the display's scale factor - on a 150% display the window could be dragged to two
            // thirds of the width the shell arithmetic says it needs, which is precisely the
            // case this floor exists to prevent.
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(
                (ProductNavigation.OpenPaneLength + contentCardMinimumWidth)
                * DisplayScale());
        }
    }

    public AppSettings CurrentSettings => _settings;

    public void FocusInitialControl()
    {
        if (_settings.HasCompletedOnboarding)
        {
            HistoryNavItem.Focus(FocusState.Programmatic);
        }
        else
        {
            FinishOnboardingButton.Focus(FocusState.Programmatic);
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        // The window losing activation keeps its focused field, so a chord held while it did is forgotten here: its
        // release goes to another window and would never reach the field. Ref: #66 review.
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            CancelKeybindCapture();
            ForgetKeybindChord();
            OnWindowActivationChangedForOllama(active: false);
            return;
        }

        OnWindowActivationChangedForOllama(active: true);
        if (_initialFocusAssigned)
        {
            // When the user returns from Windows Settings (ms-settings:startupapps) or Task Manager, re-read the
            // startup registration so a DisabledByUser toggle turned back on outside the app refreshes immediately.
            _ = LoadStartupRegistrationAsync();
            return;
        }

        _initialFocusAssigned = true;
        DispatcherQueue.TryEnqueue(FocusInitialControl);
    }

    public async Task InitializeProductDataAsync()
    {
        await LoadMicrophonesAsync().ConfigureAwait(true);
        await ReloadHistoryAsync().ConfigureAwait(true);
        await LoadStartupRegistrationAsync().ConfigureAwait(true);
    }

    private async Task LoadStartupRegistrationAsync()
    {
        if (_startupOperationInProgress || LoadStartupRegistration is not { } loadStartup)
        {
            return;
        }

        var generation = _startupOperationGeneration;
        var startup = await loadStartup().ConfigureAwait(true);
        if (_session.Closing || _startupOperationInProgress || generation != _startupOperationGeneration)
        {
            return;
        }

        SetStartupRegistration(startup);
    }

    public void SetHotkeyReady(string gesture, DictationRecordingMode recordingMode)
    {
        // One shortcut phrase, used on screen and in the tray, so the two cannot drift apart.
        var shortcut = recordingMode == DictationRecordingMode.PushToTalk
            ? $"Hold {gesture}"
            : $"Toggle with {gesture}";
        ObserveRecordingKeyForOnboarding(gesture, recordingMode);
        // A full stop rather than a middle dot. The tray tooltip is a SENTENCE - it has no
        // layout to separate, and a screen reader either announces a middle dot literally or
        // drops it. Text that is laid out on screen may still use one; this is not.
        SessionStatusChanged?.Invoke(DictationStatus.Quiet($"ready. {shortcut}"));
    }

    public void SetHotkeyUnavailable(string status)
    {
        ObserveRecordingKeyProblemForOnboarding(status);
        // AN ERROR FOR THE TRAY, WHICH IS THE ONLY PLACE IT SHOWS. The recording key not being
        // available means the app cannot be used at all, and unlike a pill the icon stays. It
        // raises no pill, because SetSessionStatus is not on this path.
        SessionStatusChanged?.Invoke(DictationStatus.Error("shortcut unavailable"));
    }

    public void SetSessionStatus(DictationStatus status)
    {
        var sentence = status.Text;
        var overlayState = status.State;
        _overlayWindow.ShowState(status);
        HandleRecordingSoundTransition(overlayState);
        SessionStatusChanged?.Invoke(status);
        if (overlayState is DictationOverlayState.Recording or DictationOverlayState.Processing)
        {
            // A DICTATION OUTRANKS A TEST OR CAPTURE, ALWAYS. Somebody who presses their record key wants to
            // dictate, and a test or capture hook still active would interfere with their recording.
            CancelMicrophoneTest();
            CancelKeybindCapture();
        }

        _currentOverlayState = overlayState;
        if (RecordToolbarButtonText is not null)
        {
            RecordToolbarButtonText.Text = overlayState == DictationOverlayState.Recording ? "Stop" : "Record";
        }
        if (RecordToolbarButtonIcon is not null)
        {
            RecordToolbarButtonIcon.Glyph = overlayState == DictationOverlayState.Recording ? "\uE71A" : "\uE720";
        }
        foreach (var button in _chimePlayButtons.Values)
        {
            button.IsEnabled = overlayState != DictationOverlayState.Recording;
        }
        SetSpeedCheckAvailability(overlayState != DictationOverlayState.Recording);
        // THE STATUS SAYS WHETHER IT BELONGS HERE; THIS NO LONGER GUESSES FROM THE WORDS. What stood
        // here tested the sentence for "ready", "model is not installed", "transcription is
        // unavailable" and "worker could not start", which is the same mistake `OverlayStateFor` was
        // deleted for one surface over. It let four unrelated messages overwrite the Transcription
        // card - a Windows resume, a finished Escape Recovery, and both Ollama health lines - and it
        // would have dropped any new engine sentence that did not happen to contain one of the four
        // phrases. `AboutTheTranscriptionEngine` carries the answer from the call site instead.
        if (status.DescribesTranscriptionEngine)
        {
            _onboardingEngineSentence = sentence;
        }

        ObserveStatusForOnboarding(status);
    }

    public void CancelRecordingSoundCue()
    {
        Interlocked.Increment(ref _recordingSoundAttempt);
        _recordingSoundPlayer.CancelPendingStart();
        _recordingSoundCoordinator.CancelActive();
    }

    private void HandleRecordingSoundTransition(DictationOverlayState nextState)
    {
        if (nextState != DictationOverlayState.Recording)
        {
            Interlocked.Increment(ref _recordingSoundAttempt);
        }

        _recordingSoundCoordinator.Handle(
            isRecording: nextState == DictationOverlayState.Recording,
            _settings.Preferences.PlayRecordingSounds,
            _settings.Preferences.RecordingSoundPairing);
    }

    private bool PlayRecordingSoundCue(RecordingSoundPairing pairing, RecordingSoundMoment moment)
    {
        if (moment == RecordingSoundMoment.Start)
        {
            var attempt = Interlocked.Increment(ref _recordingSoundAttempt);
            return _recordingSoundPlayer.Play(
                pairing,
                moment,
                () =>
                {
                    if (Volatile.Read(ref _recordingSoundAttempt) == attempt)
                    {
                        _recordingSoundCoordinator.CancelActive();
                    }
                });
        }

        return _recordingSoundPlayer.Play(pairing, moment);
    }

    private void OnRecordingSoundCueFailed(
        RecordingSoundPairing pairing,
        RecordingSoundMoment moment,
        Exception exception)
    {
        RecordingSoundPlaybackFailed?.Invoke(pairing, moment);
    }

    private void PreloadRecordingSoundsInBackground(RecordingSoundPairing pairing)
    {
        if (_shuttingDownProductWindows)
        {
            return;
        }

        _ = Task.Run(() => _recordingSoundPlayer.Preload(pairing));
    }

    private const string NotFromStoreSentence =
        "This copy of EnviousWispr was not installed from the Microsoft Store, so it cannot check for updates.";

    public void SetUpdateCheckInProgress()
    {
        CheckForUpdatesButton.IsEnabled = false;
        ApplyUpdateButton.IsEnabled = false;
        SetLiveText(UpdateStatusText, "Checking the Microsoft Store for updates…");
    }

    public void SetUpdateInstallInProgress()
    {
        CheckForUpdatesButton.IsEnabled = false;
        ApplyUpdateButton.IsEnabled = false;
        SetLiveText(UpdateStatusText, "Asking the Microsoft Store to install the update…");
    }

    public void SetUpdateStatus(UpdateOperationResult result)
    {
        // A BUSY REFUSAL CHANGES NOTHING ABOUT WHAT THE STORE OFFERED. Every other answer is the Store's
        // latest word, and only an available update can be installed.
        if (result.Status is not (UpdateOperationStatus.BusyDictating or UpdateOperationStatus.BusyTranscribingFile))
        {
            _updateOffered = result.CanApply;
        }

        // WINDOWS IS ABOUT TO CLOSE THE APP ONCE THE STORE IS INSTALLING, so nothing is offered after it.
        var installing = result.Status == UpdateOperationStatus.Installing;
        CheckForUpdatesButton.IsEnabled = _storeInstalled && !installing;
        ApplyUpdateButton.IsEnabled = _storeInstalled && _updateOffered && !installing;
        SetLiveText(
            UpdateStatusText,
            result.Status switch
            {
                UpdateOperationStatus.NotPackaged => NotFromStoreSentence,
                UpdateOperationStatus.BusyDictating =>
                    "Finish or cancel your dictation, then choose Install update.",
                UpdateOperationStatus.BusyTranscribingFile =>
                    "A file is being transcribed. Let it finish or stop it, then choose Install update.",
                UpdateOperationStatus.NoUpdate =>
                    $"You have the latest version, {result.Version}.",
                UpdateOperationStatus.UpdateAvailable =>
                    $"Version {result.Version} is available. Choose Install update when you are not dictating.",
                UpdateOperationStatus.Installing =>
                    "The Microsoft Store installed the update. EnviousWispr closes now; open it again from the Start menu.",
                UpdateOperationStatus.Cancelled =>
                    "The update was not installed. Check for updates again when you are ready.",
                _ => "The Microsoft Store could not finish that. Your current version is unchanged. Try again later.",
            });
    }

    public void SetCloudPolishNotice(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice) ||
            (OperationInfoBar.IsOpen && OperationInfoBar.Severity >= InfoBarSeverity.Warning))
        {
            return;
        }

        ShowMessage("Cloud polish is on, using your own key", notice, InfoBarSeverity.Informational);
    }

    public void SetOllamaPolishNotice(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice) ||
            (OperationInfoBar.IsOpen && OperationInfoBar.Severity >= InfoBarSeverity.Warning))
        {
            return;
        }

        ShowMessage("Ollama polish is on, running on this PC", notice, InfoBarSeverity.Informational);
    }

    public void SetLivePreview(string? text) =>
        _overlayWindow.SetPreview(text);

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
    /// delivery sentence rather than after it, and it carries that sentence along when the sentence
    /// had a pill of its own, so somebody who asked for the clipboard is still told the text went to
    /// the clipboard. Onboarding and the offer read where the words went from the delivery, never
    /// from the pill: a landed paste raises no pill at all.
    /// </remarks>
    public void ReportDeliveryAndMaybeOfferLanguage(DeliveryReport delivered, string? detectedLanguage)
    {
        ObserveDeliveryForOnboarding(delivered.Landing);
        var offer = delivered.InvitesLanguageOffer
            ? _languageSuggestions.Observe(
                detectedLanguage,
                _settings.Preferences.Dictation.WhisperLanguage)
            : null;
        if (offer is null)
        {
            SetSessionStatus(delivered.Status());
            return;
        }

        ShowLanguageLockOffer(offer, delivered);

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
    private void ShowLanguageLockOffer(LanguageLockOffer offer, DeliveryReport delivered)
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
        // believing the text was inserted into the window in front of them. A landed paste had no
        // pill, so the question stands alone.
        SetSessionStatus(DictationStatus.Suggestion(delivered.SuggestionText(question), action));
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
            case PillActionKind.OpenFileTranscription:
                OpenPage("settings-transcribe-file");
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

    public event Action? ManualDictationToggleRequested;

    private void RecordToolbarButton_Click(object sender, RoutedEventArgs e) =>
        ManualDictationToggleRequested?.Invoke();

    private void WhatsNewToolbarButton_Click(object sender, RoutedEventArgs e) =>
        OpenPage("whats-new");

    private async void FeedbackToolbarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isFeedbackDialogOpen || WindowRoot.XamlRoot is null)
        {
            return;
        }

        _isFeedbackDialogOpen = true;
        try
        {
            if (FlushFeedbackOutbox is { } flushOutbox)
            {
                _ = FlushFeedbackOutboxBestEffortAsync(flushOutbox);
            }

            var draftStore = ResolveFeedbackDraftStore();
            var snapshotBuilder = BuildFeedbackDiagnosticsSnapshot ?? BuildFallbackFeedbackDiagnosticsSnapshot;
            var submitter = SubmitFeedback ?? ((submission, _) => Task.FromResult(new FeedbackSendResult(
                FeedbackSendOutcome.Unavailable,
                submission.SubmissionId,
                "Feedback delivery is not configured.")));

            var defaultIncludeDiagnostics = _settings.Observability?.ShareAnonymousTelemetry ?? true;
            using var dialog = new FeedbackDialog(
                draftStore,
                snapshotBuilder,
                submitter,
                defaultIncludeDiagnostics,
                CheckUndeliverableFeedback)
            {
                XamlRoot = WindowRoot.XamlRoot,
                RequestedTheme = WindowRoot.RequestedTheme,
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _isFeedbackDialogOpen = false;
        }
    }

    private FeedbackDraftStore? ResolveFeedbackDraftStore()
    {
        if (FeedbackDraftStore is { } existing)
        {
            return existing;
        }

        var directory = !string.IsNullOrWhiteSpace(FeedbackDataDirectory)
            ? FeedbackDataDirectory
            : _dataDirectory;
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : new FeedbackDraftStore(directory);
    }

    private FeedbackDiagnosticsSnapshot BuildFallbackFeedbackDiagnosticsSnapshot()
    {
        var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.19.0";
        var osVersion = $"Windows {Environment.OSVersion.Version}";
        return new FeedbackDiagnosticsSnapshot(
            appVersion: appVersion,
            osVersion: osVersion,
            finalAsrEngine: _settings.Preferences.Dictation.FinalEngine.ToString(),
            polishProvider: _settings.Preferences.Polish.Provider.ToString(),
            livePreviewEnabled: _settings.Preferences.LivePreviewEnabled,
            livePreviewEngine: _settings.Preferences.LivePreviewEngine.ToString(),
            recordingMode: _settings.Preferences.Dictation.RecordingMode.ToString(),
            autoStopEnabled: _settings.Preferences.Dictation.AutoStopEnabled,
            hardwareClass: DiagnosticHardwareClass.Unknown.ToString(),
            recentEventCodes: []);
    }

    private static async Task FlushFeedbackOutboxBestEffortAsync(Func<CancellationToken, Task> flushOutbox)
    {
        try
        {
            await flushOutbox(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Background outbox flush is best-effort when opening Feedback.
        }
    }

    private static (string PageTag, string? TabKey) ResolvePageAndTab(string tag) => tag switch
    {
        "home" => ("history", null),
        "settings-transcription" => ("settings-dictation", "engine"),
        "settings-microphone" => ("settings-dictation", "microphone"),
        "settings-live-preview" => ("settings-dictation", "live-preview"),
        "settings-pill" => ("settings-dictation", "pill"),
        "settings-sounds" => ("settings-dictation", "sounds"),
        "settings-clipboard" => ("settings-dictation", "clipboard"),
        "settings-appearance" => ("settings-app", "appearance"),
        "settings-permissions" or "help-permissions" => ("settings-app", "permissions"),
        "settings-privacy" or "settings-history" => ("settings-app", "privacy"),
        "settings-profile" or "settings-backup" => ("settings-app", "backup"),
        "settings-updates" or "settings-licenses" or "help-updates" or "help-licenses" => ("settings-app", "updates-licenses"),
        _ => (tag, null),
    };

    /// <summary>Brings the window forward and shows one page by its tag.</summary>
    public void OpenPage(string tag) => OpenPage(tag, null);

    public void OpenPage(string tag, string? tabKey)
    {
        AppWindow.Show();
        Activate();
        ShowOnboarding(show: false);

        var (resolvedTag, resolvedTab) = ResolvePageAndTab(tag);
        var targetTab = tabKey ?? resolvedTab;

        if (resolvedTag == "whats-new")
        {
            ProductNavigation.SelectedItem = null;
            ShowPage("whats-new");
            return;
        }

        var row = NavigationRows()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), resolvedTag, StringComparison.Ordinal));
        if (row is null && string.Equals(resolvedTag, "history", StringComparison.Ordinal))
        {
            row = HistoryNavItem;
        }
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

        _pendingTabKey = targetTab;
        if (ReferenceEquals(ProductNavigation.SelectedItem, row))
        {
            ShowPage(resolvedTag, targetTab);
        }
        else
        {
            ProductNavigation.SelectedItem = row;
        }
    }

    public void OpenSettings()
    {
        ShowOnboarding(show: false);
        OpenPage("settings-dictation");
    }

    public void OpenQuickAdd(string? selection, string? message)
    {
        ShowOnboarding(show: false);
        OpenPage("dictionary", "quick-add");
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
        _shuttingDownProductWindows = true;
        // DETACHED, NOT JUST STOPPED. A timer left attached can still tick during shutdown and raise
        // an announcement for a window that is going away.
        _historyAnnounceDebounce.Stop();
        _historyAnnounceDebounce.Tick -= OnHistoryAnnounceDue;
        StopOllamaLivePollTimer();
        // THE WRITER IS NOT DISPOSED HERE, AND THAT IS DELIBERATE. Shutdown is synchronous and a
        // save may still be inside it; disposing its gate makes the release throw. The session that
        // owns the writer is closed by the shell's lifetime after the drain, once the save is over.
        _soundPreviewCancellation?.Cancel();
        _soundPreviewCancellation?.Dispose();
        _soundPreviewCancellation = null;
        _recordingSoundPlayer.Dispose();
        // THE CATALOGUE IS THE SESSION'S TO DISPOSE, with the writer, after the drain; the window
        // only stops listening to it here.
        if (_deviceCatalog is not null)
        {
            _deviceCatalog.DevicesChanged -= OnAudioDevicesChanged;
            _deviceCatalog = null;
        }

        _overlayWindow.Shutdown();
    }

    public void Dispose() => ShutdownProductWindows();

    /// <param name="undoOffered">
    /// The text is an Escape Recovery whose one-shot Undo still stands: the card speaks macOS's words for it,
    /// the pill's title and the announcement that names both ways back, and shows the Undo button.
    /// </param>
    public void SetRecoveredText(RecoveryTextLoadResult result, bool undoOffered = false)
    {
        if (result.Status == RecoveryTextLoadStatus.Found && result.Record is not null)
        {
            RecoveryTextBox.Text = result.Record.Text;
            RecoveryCard.Visibility = Visibility.Visible;
            if (undoOffered)
            {
                RecoveryTitleText.Text = EscapeRecoveryCardTitle;
                RecoveryBodyText.Text = EscapeRecoveryUndoBody;
                UndoRecoveryButton.Visibility = Visibility.Visible;
                UndoRecoveryButton.IsEnabled = true;
                return;
            }

            ResetRecoveryCardWords();
            ShowMessage(
                "Interrupted dictation recovered",
                "Review or copy the private recovery text on History. It was not pasted automatically.",
                InfoBarSeverity.Warning);
            SetOnboardingReliabilityNotice(
                "Interrupted dictation recovered",
                "Finish setup, then review or copy the private recovery text on History. It will not be pasted automatically.");
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
        ShowMessage(title, message, InfoBarSeverity.Warning);
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
        ResetRecoveryCardWords();
    }

    // macOS's words for an Escape Recovery: the pill's title, and its spoken offer after the title.
    private const string EscapeRecoveryCardTitle = "Dictation cancelled";
    private const string EscapeRecoveryUndoBody = "Press Undo to get it back, or find it in History.";

    /// <summary>The card's words for text found after an unfinished run, and no Undo.</summary>
    private void ResetRecoveryCardWords()
    {
        RecoveryTitleText.Text = "Recovered unfinished dictation";
        RecoveryBodyText.Text =
            "EnviousWispr found text that had not reached its destination before the previous run ended. "
            + "Review it here; it will never be pasted automatically.";
        UndoRecoveryButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>What a paste of a saved dictation came to, in this window, and whether Home's Undo still stands.</summary>
    /// <remarks>
    /// ONE-SHOT ON SCREEN AS WELL AS IN THE OWNER. The button goes the moment the offer is spent (used, or its
    /// copy moved on) and is live again only when a refusal left the offer standing. A landed paste says
    /// nothing here: the words arriving in front of the person say it, and Home's copy has already gone.
    /// </remarks>
    public void ShowSavedDictationPasteResult(
        SavedDictationPasteAction action,
        SavedDictationPasteOutcome? outcome,
        SessionHoldAttempt? refusal,
        bool undoStanding,
        bool clipboardNotRestored)
    {
        var undo = action == SavedDictationPasteAction.Undo;
        if (RecoveryCard.Visibility == Visibility.Visible && UndoRecoveryButton.Visibility == Visibility.Visible)
        {
            UndoRecoveryButton.IsEnabled = undoStanding;
            if (!undoStanding)
            {
                UndoRecoveryButton.Visibility = Visibility.Collapsed;
                RecoveryBodyText.Text = "Copy it from here, or find it in History.";
            }
        }

        if (refusal is not null)
        {
            ShowMessage("Not pasted", SavedDictationPasteRefusalSentence(refusal), InfoBarSeverity.Informational);
            return;
        }

        // THE ONE LANDED PASTE THAT SAYS SOMETHING HERE (#242): the paste borrowed the clipboard and could not give
        // it back, so it may now hold these words instead of what the person had copied. A paste that failed with
        // the clipboard in that state says both.
        if (clipboardNotRestored)
        {
            if (outcome == SavedDictationPasteOutcome.Pasted)
            {
                ShowMessage(
                    "Pasted, but your clipboard could not be restored",
                    "It may now hold this dictation instead of what you had copied.",
                    InfoBarSeverity.Warning);
            }
            else
            {
                ShowMessage(
                    "Your dictation could not be pasted",
                    "Nothing was typed, and your clipboard could not be restored. Copy it from Home or History instead.",
                    InfoBarSeverity.Error);
            }

            return;
        }

        switch (outcome)
        {
            case SavedDictationPasteOutcome.KeptOnClipboard:
                ShowMessage(
                    "Your dictation is on the clipboard",
                    undo
                        ? "EnviousWispr could not get back to the place you were dictating into, so nothing was typed there. Paste it where you want it."
                        : "That app refused the paste. Paste it where you want it.",
                    InfoBarSeverity.Warning);
                break;
            case SavedDictationPasteOutcome.MayHavePasted:
                // NOT "NOTHING WAS TYPED", AND NO INVITATION TO PRESS AGAIN: the words may already be there, and a
                // second paste would put them in twice. Only Undo promises a copy: Home keeps it, and a History entry may not.
                ShowMessage(
                    "Your dictation may have been pasted",
                    undo
                        ? "EnviousWispr could not confirm it. Check the app before you try again. The text is still on Home."
                        : "EnviousWispr could not confirm it. Check the app before you try again.",
                    InfoBarSeverity.Warning);
                break;
            case SavedDictationPasteOutcome.NoLongerAvailable:
                ShowMessage(
                    "That dictation is no longer in History",
                    "It was deleted or its 24 hours ran out, so nothing was pasted.",
                    InfoBarSeverity.Informational);
                break;
            case SavedDictationPasteOutcome.DictationInProgress or SavedDictationPasteOutcome.Busy:
                ShowMessage(
                    "Not pasted",
                    "EnviousWispr is busy with a dictation. Try again in a moment.",
                    InfoBarSeverity.Informational);
                break;
            case SavedDictationPasteOutcome.NoTarget:
                ShowMessage(
                    "Not pasted",
                    undo
                        ? "The app you were dictating into has gone. Copy the text from Home instead."
                        : "Click into the app you want it in, then come back and try again.",
                    InfoBarSeverity.Informational);
                break;
            case SavedDictationPasteOutcome.Failed:
                ShowMessage(
                    "Your dictation could not be pasted",
                    "Nothing was typed. Copy it from Home or History instead.",
                    InfoBarSeverity.Error);
                break;
        }
    }

    /// <summary>Why a paste could not start, in the words the other refusals use.</summary>
    private static string SavedDictationPasteRefusalSentence(SessionHoldAttempt refusal) => refusal switch
    {
        { Refusal: SessionHoldRefusal.Dictation } => "A dictation is running. Try again when it finishes.",
        { HeldBy: SessionHolder.UpdateApply } => "EnviousWispr is updating. Try again when it finishes.",
        { HeldBy: SessionHolder.LastDictationReuse } => "EnviousWispr is pasting your last dictation. Try again in a moment.",
        { HeldBy: SessionHolder.FileTranscription } => "A file is being transcribed. Try again when it finishes.",
        { HeldBy: SessionHolder.DataDeletion } => "EnviousWispr is deleting your data and will close.",
        { HeldBy: SessionHolder.GraphicsRuntimeSwitch } => "EnviousWispr is moving dictation to your graphics card. Try again in a moment.",
        { HeldBy: SessionHolder.SpeechEngineSwitch } => "EnviousWispr is updating its speech engine. Try again in a moment.",
        { HeldBy: SessionHolder.SavedDictationPaste } => "EnviousWispr is pasting your dictation. Try again in a moment.",
        _ => "EnviousWispr is closing.",
    };

    private void UndoRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        // ONE PRESS: greyed at once, so a double click asks once. The owner's exchange is the guarantee;
        // this is the picture of it.
        UndoRecoveryButton.IsEnabled = false;
        RecoveryUndoRequested?.Invoke();
    }

    private void PasteHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItemViewModel selected)
        {
            ShowMessage("Select a dictation first", "Choose the history entry you want to paste.", InfoBarSeverity.Informational);
            return;
        }

        HistoryPasteRequested?.Invoke(selected.Id);
    }

    private string? _pendingTabKey;

    private void ProductNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is null && args.SelectedItemContainer is null)
        {
            return;
        }

        var item = args.SelectedItemContainer as NavigationViewItem ?? args.SelectedItem as NavigationViewItem;
        var tag = item?.Tag?.ToString() ?? "history";
        var tabKey = _pendingTabKey;
        _pendingTabKey = null;
        ShowPage(tag, tabKey);
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
        CancelKeybindCapture();
        HotkeyTextBox.Focus(FocusState.Programmatic);
        HotkeyTextBox.Text = HandsFreeRecordBinding.Suggested;
        _ = TriggerAutoSaveAsync();
    }

    /// <summary>Auto-saves settings changes quietly whenever a control is updated.</summary>
    private async Task TriggerAutoSaveAsync()
    {
        if (_isApplyingSettings || _keybindCaptureSession is not null)
        {
            return;
        }

        // RAW, INCLUDING "NOTHING SELECTED": the indices go across as -1 when no choice is selected
        // and the sound pairing as null when none is, so what a missing choice means is the
        // presenter's decision too, not this handler's.
        var selectedProviderIndex = RawSelectedIndexOf(PolishProviderChoices);
        var selectedProvider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        var selectedPolishModelForSave = selectedProvider == PolishProvider.EgOne
            ? EnginePolishPresentation.NormalizeEgOneModelId(_selectedEgOneModelId)
            : _selectedPolishModelId;
        var input = new GeneralSettingsInput(
            HotkeyTextBox.Text,
            CancelHotkeyTextBox.Text,
            QuickAddHotkeyTextBox.Text,
            SelectedFinalEngineRawIndex(),
            WordCorrectionToggle.IsOn,
            FillerRemovalToggle.IsOn,
            EmojiFormatterToggle.IsOn,
            SpokenPunctuationToggle.IsOn,
            WhisperLanguageComboBox.SelectedIndex,
            EnglishSpellingComboBox.SelectedIndex,
            _recordingModeIndex,
            EscapeRecoveryToggle.IsOn,
            AutoStopToggle.IsOn,
            AutoStopSecondsBox.Value,
            selectedProviderIndex,
            selectedPolishModelForSave,
            OllamaEndpointTextBox.Text,
            HistoryEnabledToggle.IsOn,
            RetentionDaysBox.Value,
            _selectedThemeIndex,
            LivePreviewToggle.IsOn,
            _selectedOverlayPositionIndex,
            PillDesignWithoutWordsFromControls() == RecordingPillDesign.LevelRail,
            PlayRecordingSoundsToggle.IsOn,
            SelectedRecordingSoundPairing(),
            CopyInsteadOfPasteToggle.IsOn,
            LocalDiagnosticsToggle.IsOn,
            DiagnosticRetentionDaysBox.Value,
            ShareUsageMetricsToggle.IsOn,
            TelemetryAvailable: true,
            (MicrophoneComboBox.SelectedItem as MicrophoneChoice)?.Id,
            PasteLastHotkeyTextBox.Text,
            CopyLastHotkeyTextBox.Text,
            CasualModeToggle?.IsOn == true,
            SelectedLivePreviewEngineIndex(),
            SendCrashReportsToggle.IsOn,
            EgOneModelId: _selectedEgOneModelId);

        var outcome = await _session.Settings.SaveGeneralAsync(input).ConfigureAwait(true);
        switch (outcome.Status)
        {
            case GeneralSaveStatus.InvalidShortcut:
                ShowMessage(GeneralSaveOutcome.InvalidShortcutTitle, GeneralSaveOutcome.InvalidShortcutMessage, InfoBarSeverity.Error);
                (outcome.InvalidField switch
                {
                    GeneralShortcutField.Cancel => CancelHotkeyTextBox,
                    GeneralShortcutField.QuickAdd => QuickAddHotkeyTextBox,
                    GeneralShortcutField.PasteLast => PasteLastHotkeyTextBox,
                    GeneralShortcutField.CopyLast => CopyLastHotkeyTextBox,
                    _ => HotkeyTextBox,
                }).Focus(FocusState.Programmatic);
                return;
            case GeneralSaveStatus.OverlappingShortcuts:
                ShowMessage(GeneralSaveOutcome.OverlappingShortcutsTitle, outcome.Overlap ?? string.Empty, InfoBarSeverity.Error);
                RefreshKeybindConflicts();
                return;
            case GeneralSaveStatus.NotSaved:
                ShowSettingsRefusal(outcome.Save!.Value.Refusal!.Value);
                return;
        }

        PublishSettings();
        if (outcome.Theme.HasValue)
        {
            ApplyTheme(outcome.Theme.Value);
        }
    }

    private void WireSettingsAutoSave()
    {
        WordCorrectionToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        FillerRemovalToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        EmojiFormatterToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        SpokenPunctuationToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        if (CasualModeToggle != null)
        {
            CasualModeToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        }
        EscapeRecoveryToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        HistoryEnabledToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        LivePreviewToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        PlayRecordingSoundsToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        CopyInsteadOfPasteToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        LocalDiagnosticsToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        ShareUsageMetricsToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        SendCrashReportsToggle.Toggled += (_, _) =>
        {
            UpdateDiagnosticsStatusText(SendCrashReportsToggle.IsOn);
            _ = TriggerAutoSaveAsync();
        };

        WhisperLanguageComboBox.SelectionChanged += (_, _) => _ = TriggerAutoSaveAsync();
        EnglishSpellingComboBox.SelectionChanged += (_, _) => _ = TriggerAutoSaveAsync();
        MicrophoneComboBox.SelectionChanged += (_, _) => _ = TriggerAutoSaveAsync();

        AutoStopSecondsBox.ValueChanged += (_, _) => _ = TriggerAutoSaveAsync();
        RetentionDaysBox.ValueChanged += (_, _) => _ = TriggerAutoSaveAsync();
        DiagnosticRetentionDaysBox.ValueChanged += (_, _) => _ = TriggerAutoSaveAsync();

        foreach (var choice in PolishProviderChoices)
        {
            choice.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SelectableChoiceOption.IsSelected) && ((SelectableChoiceOption)s!).IsSelected)
                {
                    _ = TriggerAutoSaveAsync();
                    UpdateHistoryStatusCards();
                }
            };
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
    private void SystemThemeSegmentButton_Click(object sender, RoutedEventArgs e) =>
        SetThemeSelection(0, triggerSave: true);

    private void LightThemeSegmentButton_Click(object sender, RoutedEventArgs e) =>
        SetThemeSelection(1, triggerSave: true);

    private void DarkThemeSegmentButton_Click(object sender, RoutedEventArgs e) =>
        SetThemeSelection(2, triggerSave: true);

    private void SetThemeSelection(int index, bool triggerSave)
    {
        var normalized = Math.Clamp(index, 0, 2);
        _selectedThemeIndex = normalized;
        UpdateThemeSegmentButtons();
        ApplyTheme(ThemeFromIndex(normalized));

        if (triggerSave && !_isApplyingSettings && _settings is not null)
        {
            _ = PersistAppearanceChoicesAsync();
        }
    }

    private void UpdateThemeSegmentButtons()
    {
        ApplySegmentButtonState(SystemThemeSegmentButton, _selectedThemeIndex == 0);
        ApplySegmentButtonState(LightThemeSegmentButton, _selectedThemeIndex == 1);
        ApplySegmentButtonState(DarkThemeSegmentButton, _selectedThemeIndex == 2);
    }

    private void TopPositionSegmentButton_Click(object sender, RoutedEventArgs e) =>
        SetOverlayPositionSelection(0, triggerSave: true);

    private void BottomPositionSegmentButton_Click(object sender, RoutedEventArgs e) =>
        SetOverlayPositionSelection(1, triggerSave: true);

    private void SetOverlayPositionSelection(int index, bool triggerSave)
    {
        var normalized = index == 1 ? 1 : 0;
        _selectedOverlayPositionIndex = normalized;
        UpdateOverlayPositionSegmentButtons();

        if (triggerSave && !_isApplyingSettings && _settings is not null)
        {
            _ = PersistAppearanceChoicesAsync();
        }
    }

    private void UpdateOverlayPositionSegmentButtons()
    {
        ApplySegmentButtonState(TopPositionSegmentButton, _selectedOverlayPositionIndex == 0);
        ApplySegmentButtonState(BottomPositionSegmentButton, _selectedOverlayPositionIndex == 1);
    }

    private static void ApplySegmentButtonState(Button? button, bool selected)
    {
        if (button is null)
        {
            return;
        }

        button.Style = selected
            ? (Style)Application.Current.Resources["BrandSegmentedActiveButtonStyle"]
            : (Style)Application.Current.Resources["BrandSegmentedButtonStyle"];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(
            button,
            selected ? "Selected" : string.Empty);
    }

    /// <summary>Applies and persists a single-selection Recording Pill card choice immediately.</summary>
    private async void PillStyleCardChecked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        var livePreviewEnabled = ReferenceEquals(sender, ReadingWellPillButton);
        if (ReferenceEquals(sender, LevelRailPillButton))
        {
            _lastWithoutWordsPillDesign = RecordingPillDesign.LevelRail;
        }
        else if (ReferenceEquals(sender, CapsulePillButton))
        {
            _lastWithoutWordsPillDesign = RecordingPillDesign.Classic;
        }

        _isApplyingSettings = true;
        try
        {
            LivePreviewToggle.IsOn = livePreviewEnabled;
            RefreshLivePreviewSectionPresentation();
        }
        finally
        {
            _isApplyingSettings = false;
        }

        await PersistRecordingPillSelectionAsync(livePreviewEnabled, _lastWithoutWordsPillDesign)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Writes the Appearance choices, and nothing else.
    /// </summary>
    /// <remarks>
    /// ONLY THESE FIELDS, DELIBERATELY. The Save button builds a whole settings object out of
    /// every control in the window, which is right for a button the user pressed and wrong for a
    /// side effect of clicking a theme or position segment: it would commit half-finished edits
    /// sitting on other pages that the user has not chosen to save yet.
    /// </remarks>
    private async Task PersistAppearanceChoicesAsync()
    {
        // THE CONTROLS ARE READ HERE, ON THE UI THREAD, AND APPLIED INSIDE THE GATE by the
        // presenter - so a click that overlaps another writer builds on what is actually stored,
        // and no control is read from the thread the wait ends on.
        var choices = new AppearanceChoices(
            ThemeFromIndex(_selectedThemeIndex),
            OverlayPositionFromIndex(_selectedOverlayPositionIndex),
            PillDesignWithoutWordsFromControls());
        var result = await _session.Settings.SaveAppearanceAsync(choices).ConfigureAwait(true);
        HandleAppearanceSaveResult(result);
    }

    private async Task PersistRecordingPillSelectionAsync(
        bool livePreviewEnabled,
        RecordingPillDesign withoutWordsDesign)
    {
        var result = await _session.Settings
            .SaveRecordingPillSelectionAsync(livePreviewEnabled, withoutWordsDesign)
            .ConfigureAwait(true);
        HandleAppearanceSaveResult(result);
    }

    private void HandleAppearanceSaveResult(SettingsSaveResult result)
    {
        if (result.Saved)
        {
            PublishSettings();
        }
        else if (result.Refusal != SettingsSaveRefusal.Closing)
        {
            ShowMessage(
                "That choice was not kept",
                "It is applied for now, but the app will start with your previous appearance until "
                    + "settings can be written again.",
                InfoBarSeverity.Warning);
        }
    }

    private void LivePreviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isApplyingSettings)
        {
            UpdatePillDesignControls();
            RefreshLivePreviewSectionPresentation();
        }
    }

    private async void LivePreviewEngineCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        RefreshLivePreviewSectionPresentation();
        await TriggerAutoSaveAsync().ConfigureAwait(true);
    }

    private void DownloadLivePreviewWhisperButton_Click(object sender, RoutedEventArgs e) =>
        PreviewModelDownloadRequested?.Invoke();

    private void CancelLivePreviewWhisperDownloadButton_Click(object sender, RoutedEventArgs e) =>
        ModelDownloadCancelRequested?.Invoke();

    private void RemoveLivePreviewWhisperButton_Click(object sender, RoutedEventArgs e) =>
        PreviewModelRemoveRequested?.Invoke();

    private int SelectedLivePreviewEngineIndex() =>
        LivePreviewWhisperEngineCard?.IsChecked == true
            ? 1
            : LivePreviewParakeetEngineCard?.IsChecked == true
                ? 0
                : -1;

    public void SetLivePreviewModelInventory(
        bool parakeetInstalled,
        bool whisperPreviewInstalled,
        bool canRemoveWhisperModel)
    {
        _livePreviewParakeetInstalled = parakeetInstalled;
        _livePreviewWhisperInstalled = whisperPreviewInstalled;
        _livePreviewCanRemoveWhisper = canRemoveWhisperModel;
        if (whisperPreviewInstalled)
        {
            _livePreviewWhisperDeliveryStage = SpeechModelDelivery.Installed;
            _livePreviewWhisperDownloadPercent = null;
            _livePreviewWhisperDeliveryDetail = null;
        }
        else if (_livePreviewWhisperDeliveryStage is SpeechModelDelivery.Installed or SpeechModelDelivery.Activating)
        {
            _livePreviewWhisperDeliveryStage = SpeechModelDelivery.Missing;
            _livePreviewWhisperDownloadPercent = null;
            _livePreviewWhisperDeliveryDetail = null;
        }

        RefreshLivePreviewSectionPresentation();
    }

    public void SetLivePreviewWhisperDelivery(ModelDeliveryPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        _livePreviewWhisperDeliveryStage = presentation.Stage;
        _livePreviewWhisperDownloadPercent = presentation.Percent;
        _livePreviewWhisperDeliveryDetail = presentation.Text;
        RefreshLivePreviewSectionPresentation();
    }

    private void RefreshLivePreviewSectionPresentation()
    {
        if (LivePreviewToggle is null || LivePreviewParakeetEngineCard is null || LivePreviewWhisperEngineCard is null)
        {
            return;
        }

        var selectedEngine = LivePreviewWhisperEngineCard.IsChecked == true
            ? LivePreviewEngineChoice.Whisper
            : LivePreviewEngineChoice.Parakeet;
        var presentation = LivePreviewSettingsPresentation.Present(
            LivePreviewToggle.IsOn,
            selectedEngine,
            _livePreviewParakeetInstalled,
            _livePreviewWhisperInstalled,
            _livePreviewWhisperDeliveryStage,
            _livePreviewWhisperDownloadPercent,
            _livePreviewWhisperDeliveryDetail,
            _livePreviewCanRemoveWhisper);

        LivePreviewReadyBadgeText.Text = presentation.StatusBadgeText;
        LivePreviewWarningBadgeText.Text = presentation.StatusBadgeText;
        LivePreviewErrorBadgeText.Text = presentation.StatusBadgeText;
        LivePreviewOffBadgeText.Text = presentation.StatusBadgeText;

        LivePreviewReadyBadge.Visibility = presentation.StatusTone == LivePreviewStatusTone.Ready ? Visibility.Visible : Visibility.Collapsed;
        LivePreviewWarningBadge.Visibility = presentation.StatusTone == LivePreviewStatusTone.Warning ? Visibility.Visible : Visibility.Collapsed;
        LivePreviewErrorBadge.Visibility = presentation.StatusTone == LivePreviewStatusTone.Error ? Visibility.Visible : Visibility.Collapsed;
        LivePreviewOffBadge.Visibility = presentation.StatusTone == LivePreviewStatusTone.Off ? Visibility.Visible : Visibility.Collapsed;

        SetLiveText(LivePreviewStatusDetailText, presentation.StatusDetailText);
        LivePreviewPrivacyText.Text = presentation.PrivacyReassuranceText;
        LivePreviewParakeetStatusText.Text = presentation.ParakeetCard.StatusText;
        LivePreviewWhisperStatusText.Text = presentation.WhisperCard.StatusText;

        LivePreviewWhisperDownloadProgress.Visibility = presentation.WhisperCard.ProgressPercent is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        LivePreviewWhisperDownloadProgress.Value = presentation.WhisperCard.ProgressPercent ?? 0;

        DownloadLivePreviewWhisperButton.Content = presentation.WhisperCard.DownloadButtonText;
        DownloadLivePreviewWhisperButton.Visibility = presentation.WhisperCard.CanDownload
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelLivePreviewWhisperDownloadButton.Visibility = presentation.WhisperCard.CanCancel
            ? Visibility.Visible
            : Visibility.Collapsed;
        RemoveLivePreviewWhisperButton.Visibility = presentation.WhisperCard.CanRemove
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void InitializeChimeCards()
    {
        ChimesGrid.Children.Clear();
        _chimeCards.Clear();
        _chimeSelectButtons.Clear();
        _chimeBadges.Clear();
        _chimeWaveformBars.Clear();
        _chimePlayButtons.Clear();

        var choices = RecordingSoundCatalog.Choices;
        for (var index = 0; index < choices.Count; index++)
        {
            var choice = choices[index];
            var col = index % 2;
            var row = index / 2;

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Background = (Brush)Application.Current.Resources["BrandCardBgBrush"],
                BorderBrush = (Brush)Application.Current.Resources["BrandDividerBrush"],
                BorderThickness = new Thickness(1),
                Tag = choice.Pairing,
                IsTapEnabled = true,
            };
            Grid.SetColumn(card, col);
            Grid.SetRow(card, row);

            card.Tapped += (s, e) =>
            {
                if (s is Border b && b.Tag is RecordingSoundPairing pairing)
                {
                    SelectChimePairing(pairing, triggerSave: true);
                }
            };

            var cardGrid = new Grid
            {
                ColumnSpacing = 8,
            };
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var contentStack = new StackPanel { Spacing = 4 };

            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var titleText = new TextBlock
            {
                Text = choice.DisplayName,
                Style = (Style)Application.Current.Resources["BrandRowLabelStyle"],
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };

            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = (Brush)Application.Current.Resources["BrandAccentBrush"],
                Padding = new Thickness(6, 1, 6, 1),
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "IN USE",
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = (Brush)Application.Current.Resources["BrandOnAccentBrush"],
                },
            };

            headerPanel.Children.Add(titleText);
            headerPanel.Children.Add(badge);
            contentStack.Children.Add(headerPanel);

            var descText = new TextBlock
            {
                Text = choice.Description,
                Style = (Style)Application.Current.Resources["BrandHelperStyle"],
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            };
            contentStack.Children.Add(descText);

            var waveformPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Height = 20,
                Margin = new Thickness(0, 4, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var barHeights = Audio.RecordingChimeWaveform.GetBarHeights(choice.Pairing);
            var bars = new List<Border>();
            foreach (var h in barHeights)
            {
                var barHeight = Math.Max(2.0, h * 18.0);
                var bar = new Border
                {
                    Width = 2,
                    Height = barHeight,
                    CornerRadius = new CornerRadius(1),
                    Background = (Brush)Application.Current.Resources["BrandTextSecondaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                };
                bars.Add(bar);
                waveformPanel.Children.Add(bar);
            }
            contentStack.Children.Add(waveformPanel);

            var selectButton = new Button
            {
                Background = (Brush)Application.Current.Resources["BrandHitTestBrush"],
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Content = contentStack,
                Tag = choice.Pairing,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(selectButton, choice.DisplayName);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(selectButton, choice.Description);
            selectButton.Click += (s, e) =>
            {
                if (s is Button btn && btn.Tag is RecordingSoundPairing pairing)
                {
                    SelectChimePairing(pairing, triggerSave: true);
                }
            };
            selectButton.GotFocus += (s, e) =>
            {
                if (s is Button btn && btn.Tag is RecordingSoundPairing pairing && pairing != _selectedRecordingSoundPairing && _chimeCards.TryGetValue(pairing, out var c))
                {
                    c.BorderBrush = (Brush)Application.Current.Resources["BrandAccentBrush"];
                }
            };
            selectButton.LostFocus += (s, e) =>
            {
                if (s is Button btn && btn.Tag is RecordingSoundPairing pairing && pairing != _selectedRecordingSoundPairing && _chimeCards.TryGetValue(pairing, out var c))
                {
                    c.BorderBrush = (Brush)Application.Current.Resources["BrandDividerBrush"];
                }
            };
            Grid.SetColumn(selectButton, 0);
            cardGrid.Children.Add(selectButton);

            var playButton = new Button
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["BrandQuietButtonStyle"],
                Content = new FontIcon
                {
                    Glyph = "\uE768",
                    FontSize = 12,
                },
                Tag = choice.Pairing,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(playButton, $"Preview {choice.DisplayName} sound");
            ToolTipService.SetToolTip(playButton, $"Play {choice.DisplayName} preview");
            playButton.Click += (s, e) =>
            {
                if (s is Button btn && btn.Tag is RecordingSoundPairing pairing)
                {
                    _ = PlayChimePreviewAsync(pairing, btn);
                }
            };
            Grid.SetColumn(playButton, 1);
            cardGrid.Children.Add(playButton);

            card.Child = cardGrid;
            ChimesGrid.Children.Add(card);

            _chimeCards[choice.Pairing] = card;
            _chimeSelectButtons[choice.Pairing] = selectButton;
            _chimeBadges[choice.Pairing] = badge;
            _chimeWaveformBars[choice.Pairing] = bars;
            _chimePlayButtons[choice.Pairing] = playButton;
        }

        UpdateChimeCardsVisuals();
    }

    private void SelectChimePairing(RecordingSoundPairing pairing, bool triggerSave = true)
    {
        _selectedRecordingSoundPairing = pairing;
        PreloadRecordingSoundsInBackground(pairing);
        UpdateChimeCardsVisuals();
        if (triggerSave && !_isApplyingSettings && _settings is not null && _chimeSelectButtons.TryGetValue(pairing, out var selectBtn))
        {
            var choice = RecordingSoundCatalog.Choices.FirstOrDefault(c => c.Pairing == pairing);
            var name = choice?.DisplayName ?? pairing.ToString();
            var peer = FrameworkElementAutomationPeer.FromElement(selectBtn)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(selectBtn);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.CurrentThenMostRecent,
                $"{name} selected",
                "EnviousWisprChimeSelection");
        }
        if (triggerSave && !_isApplyingSettings && _settings is not null)
        {
            _ = TriggerAutoSaveAsync();
        }
    }

    private void UpdateChimeCardsVisuals()
    {
        var accentBrush = (Brush)Application.Current.Resources["BrandAccentBrush"];
        var dividerBrush = (Brush)Application.Current.Resources["BrandDividerBrush"];
        var surfaceBrush = (Brush)Application.Current.Resources["BrandCardBgBrush"];
        var hoverBrush = (Brush)Application.Current.Resources["BrandAccentLightBrush"];
        var secondaryTextBrush = (Brush)Application.Current.Resources["BrandTextSecondaryBrush"];

        foreach (var (pairing, card) in _chimeCards)
        {
            var isSelected = pairing == _selectedRecordingSoundPairing;
            card.BorderBrush = isSelected ? accentBrush : dividerBrush;
            card.BorderThickness = isSelected ? new Thickness(2) : new Thickness(1);
            card.Background = isSelected ? hoverBrush : surfaceBrush;

            if (_chimeSelectButtons.TryGetValue(pairing, out var selectButton))
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(
                    selectButton,
                    isSelected ? "Selected" : "Not selected");
            }

            if (_chimeBadges.TryGetValue(pairing, out var badge))
            {
                badge.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_chimeWaveformBars.TryGetValue(pairing, out var bars))
            {
                var barBrush = isSelected ? accentBrush : secondaryTextBrush;
                foreach (var bar in bars)
                {
                    bar.Background = barBrush;
                }
            }
        }
    }

    private async Task PlayChimePreviewAsync(RecordingSoundPairing pairing, Button? button = null)
    {
        if (_currentOverlayState == DictationOverlayState.Recording)
        {
            return;
        }

        _soundPreviewCancellation?.Cancel();
        _soundPreviewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _soundPreviewCancellation = cancellation;
        if (button is not null)
        {
            button.IsEnabled = false;
        }

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
                if (button is not null)
                {
                    button.IsEnabled = _currentOverlayState != DictationOverlayState.Recording;
                }
            }
        }
    }

    private void PushToTalkSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        SetRecordingMode(0, triggerSave: true);
    }

    private void ToggleSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        SetRecordingMode(1, triggerSave: true);
    }

    private void SetRecordingMode(int modeIndex, bool triggerSave = true)
    {
        _recordingModeIndex = modeIndex;
        UpdateRecordingModeButtons();

        if (AutoStopToggle is not null)
        {
            UpdateAutoStopAvailability();
        }

        if (triggerSave && !_isApplyingSettings && _settings is not null)
        {
            _ = TriggerAutoSaveAsync();
        }
    }

    private void UpdateRecordingModeButtons()
    {
        var pttSelected = _recordingModeIndex == 0;
        var accentBrush = (Brush)Application.Current.Resources["BrandAccentBrush"];
        var accentFgBrush = (Brush)Application.Current.Resources["BrandOnAccentBrush"];

        if (PushToTalkSegmentButton is not null)
        {
            if (pttSelected)
            {
                PushToTalkSegmentButton.Background = accentBrush;
                PushToTalkSegmentButton.Foreground = accentFgBrush;
                PushToTalkSegmentButton.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            else
            {
                PushToTalkSegmentButton.ClearValue(Button.BackgroundProperty);
                PushToTalkSegmentButton.ClearValue(Button.ForegroundProperty);
                PushToTalkSegmentButton.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
        }

        if (ToggleSegmentButton is not null)
        {
            if (!pttSelected)
            {
                ToggleSegmentButton.Background = accentBrush;
                ToggleSegmentButton.Foreground = accentFgBrush;
                ToggleSegmentButton.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            else
            {
                ToggleSegmentButton.ClearValue(Button.BackgroundProperty);
                ToggleSegmentButton.ClearValue(Button.ForegroundProperty);
                ToggleSegmentButton.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
        }
    }

    private void ChangeHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } ||
            TryResolveCaptureSlot(tag, out var slot, out var box, out var button) is false)
        {
            return;
        }

        if (_capturingSlot == slot)
        {
            CancelKeybindCapture();
            return;
        }

        StartKeybindCapture(slot, box, button);
    }

    private void HotkeyBoxTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not TextBox box ||
            TryResolveCaptureSlot(box, out var slot, out var button) is false)
        {
            return;
        }

        if (_capturingSlot == slot)
        {
            return;
        }

        StartKeybindCapture(slot, box, button);
    }

    private bool TryResolveCaptureSlot(string tag, out KeybindCaptureSlot slot, out TextBox box, out Button button)
    {
        switch (tag)
        {
            case "Hotkey":
                slot = KeybindCaptureSlot.Recording;
                box = HotkeyTextBox;
                button = ChangeHotkeyButton;
                return true;
            case "CancelHotkey":
                slot = KeybindCaptureSlot.Cancel;
                box = CancelHotkeyTextBox;
                button = ChangeCancelHotkeyButton;
                return true;
            case "QuickAdd":
                slot = KeybindCaptureSlot.QuickAdd;
                box = QuickAddHotkeyTextBox;
                button = ChangeQuickAddHotkeyButton;
                return true;
            case "PasteLast":
                slot = KeybindCaptureSlot.PasteLast;
                box = PasteLastHotkeyTextBox;
                button = ChangePasteLastHotkeyButton;
                return true;
            case "CopyLast":
                slot = KeybindCaptureSlot.CopyLast;
                box = CopyLastHotkeyTextBox;
                button = ChangeCopyLastHotkeyButton;
                return true;
            default:
                slot = default;
                box = null!;
                button = null!;
                return false;
        }
    }

    private bool TryResolveCaptureSlot(TextBox box, out KeybindCaptureSlot slot, out Button button)
    {
        if (ReferenceEquals(box, HotkeyTextBox))
        {
            slot = KeybindCaptureSlot.Recording;
            button = ChangeHotkeyButton;
            return true;
        }

        if (ReferenceEquals(box, CancelHotkeyTextBox))
        {
            slot = KeybindCaptureSlot.Cancel;
            button = ChangeCancelHotkeyButton;
            return true;
        }

        if (ReferenceEquals(box, QuickAddHotkeyTextBox))
        {
            slot = KeybindCaptureSlot.QuickAdd;
            button = ChangeQuickAddHotkeyButton;
            return true;
        }

        if (ReferenceEquals(box, PasteLastHotkeyTextBox))
        {
            slot = KeybindCaptureSlot.PasteLast;
            button = ChangePasteLastHotkeyButton;
            return true;
        }

        if (ReferenceEquals(box, CopyLastHotkeyTextBox))
        {
            slot = KeybindCaptureSlot.CopyLast;
            button = ChangeCopyLastHotkeyButton;
            return true;
        }

        slot = default;
        button = null!;
        return false;
    }

    private void StartKeybindCapture(KeybindCaptureSlot slot, TextBox box, Button button)
    {
        if (_currentOverlayState is DictationOverlayState.Recording or DictationOverlayState.Processing)
        {
            ShowKeybindCaptureHint("Stop the current recording before changing shortcuts.");
            return;
        }

        CancelKeybindCapture();

        _capturingSlot = slot;
        _capturingBox = box;
        _capturingButton = button;
        _capturingPreviousValue = box.Text;
        KeybindCaptureActiveChanged?.Invoke(true);

        _isUpdatingCaptureDisplay = true;
        try
        {
            box.Text = "Listening...";
            box.SelectionStart = box.Text.Length;
        }
        finally
        {
            _isUpdatingCaptureDisplay = false;
        }

        box.BorderBrush = KeybindListeningProbe.Background;
        box.Background = KeybindListeningProbe.BorderBrush;
        button.Content = "Cancel";
        SetLiveRegion(KeybindCaptureHintText, string.Empty, Visibility.Collapsed);
        box.Focus(FocusState.Programmatic);

        try
        {
            _keybindCaptureSession = new WindowsKeybindCaptureSession(
                allowModifierOnly: slot == KeybindCaptureSlot.Recording,
                allowEscapeAsBinding: slot == KeybindCaptureSlot.Cancel,
                allowClear: slot is KeybindCaptureSlot.PasteLast or KeybindCaptureSlot.CopyLast,
                onCommitted: gestureText => CompleteKeybindCapture(slot, gestureText),
                onCancelled: CancelKeybindCapture,
                onInvalidAttempt: ShowKeybindCaptureHint,
                dispatchToUi: action => DispatcherQueue.TryEnqueue(() => action()));
        }
        catch (Win32Exception)
        {
            CancelKeybindCapture();
            ShowKeybindCaptureHint("Could not start keyboard capture. Try again.");
        }
    }

    private void CompleteKeybindCapture(KeybindCaptureSlot slot, string gestureText)
    {
        if (_capturingSlot != slot || _capturingBox is not { } box)
        {
            return;
        }

        StopKeybindCaptureSession();
        box.Text = gestureText;
        box.SelectionStart = box.Text.Length;
        RefreshKeybindConflicts();
        KeybindCaptureActiveChanged?.Invoke(false);
        _ = TriggerAutoSaveAsync();
    }

    private void CancelKeybindCapture()
    {
        var box = _capturingBox;
        var hasSession = _keybindCaptureSession is not null || box is not null;
        if (!hasSession)
        {
            return;
        }

        var previous = _capturingPreviousValue;
        StopKeybindCaptureSession();

        if (box is not null)
        {
            _isUpdatingCaptureDisplay = true;
            try
            {
                box.Text = previous;
                box.SelectionStart = box.Text.Length;
            }
            finally
            {
                _isUpdatingCaptureDisplay = false;
            }
        }

        RefreshKeybindConflicts();
        KeybindCaptureActiveChanged?.Invoke(false);
    }

    private void StopKeybindCaptureSession()
    {
        _keybindCaptureSession?.Dispose();
        _keybindCaptureSession = null;

        if (_capturingBox is { } box)
        {
            box.ClearValue(Control.BorderBrushProperty);
            box.Background = KeybindDefaultBgProbe.Background;
        }

        if (_capturingButton is { } button)
        {
            button.Content = "Change";
        }

        _capturingSlot = null;
        _capturingBox = null;
        _capturingButton = null;
        _capturingPreviousValue = string.Empty;
        SetLiveRegion(KeybindCaptureHintText, string.Empty, Visibility.Collapsed);
    }

    private void ShowKeybindCaptureHint(string hint) =>
        SetLiveRegion(KeybindCaptureHintText, hint, Visibility.Visible);

    private async void EnableAiPolishToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _settings is null)
        {
            return;
        }

        var isEnabled = EnableAiPolishToggle.IsOn;
        if (AiPolishEnabledContent is not null)
        {
            AiPolishEnabledContent.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        PolishProvider targetProvider;
        if (isEnabled)
        {
            targetProvider = _lastNonNonePolishProvider == PolishProvider.None
                ? PolishProvider.EgOne
                : _lastNonNonePolishProvider;
        }
        else
        {
            var current = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
            if (current != PolishProvider.None)
            {
                _lastNonNonePolishProvider = current;
            }

            targetProvider = PolishProvider.None;
        }

        _lastActivePolishProvider = targetProvider;
        await ApplySelectedPolishProviderCardAsync(
            PolishProviderIndex(targetProvider),
            targetProvider,
            allowDefaultModel: false).ConfigureAwait(true);
    }

    private void ChangePolishProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (PolishProviderChooserPanel is not null)
        {
            PolishProviderChooserPanel.Visibility =
                PolishProviderChooserPanel.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
        }
    }

    private void UpdateActivePolishProviderDisplay()
    {
        var provider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        if (provider != PolishProvider.None)
        {
            _lastNonNonePolishProvider = provider;
        }

        var isPolishEnabled = provider != PolishProvider.None;
        if (EnableAiPolishToggle is not null && EnableAiPolishToggle.IsOn != isPolishEnabled)
        {
            EnableAiPolishToggle.IsOn = isPolishEnabled;
        }
        if (AiPolishEnabledContent is not null)
        {
            AiPolishEnabledContent.Visibility = isPolishEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        if (ActivePolishProviderNameText is not null)
        {
            ActivePolishProviderNameText.Text = ProviderDisplayName(provider);
        }

        var isCloud = IsCloudProvider(provider);
        var isOllamaHosted = provider == PolishProvider.Ollama && IsSelectedOllamaModelHosted();
        if (ActivePolishProviderBadgeText is not null)
        {
            ActivePolishProviderBadgeText.Text = isCloud
                ? "Cloud"
                : isOllamaHosted
                    ? "Runs on Ollama's servers"
                    : "On this PC";
        }

        if (ActivePolishProviderIcon is not null)
        {
            ActivePolishProviderIcon.Glyph = provider switch
            {
                PolishProvider.EgOne => "\uE70F",
                PolishProvider.Ollama => "\uE968",
                _ => "\uE753",
            };
        }

        if (ActivePolishProviderStatusText is not null)
        {
            ActivePolishProviderStatusText.Text = provider switch
            {
                PolishProvider.EgOne => "Our model for cleaning up dictation on this PC.",
                PolishProvider.Ollama => "Local models on this PC or hosted models on Ollama's servers.",
                PolishProvider.OpenAI => "OpenAI GPT models (requires your API key).",
                PolishProvider.Anthropic => "Anthropic Claude models (requires your API key).",
                PolishProvider.Gemini => "Google Gemini models (requires your API key).",
                _ => "AI Polish is disabled.",
            };
        }

        var outputLanguage = EnginePolishPresentation.DescribeDictationOutputLanguage(provider);
        if (DictationOutputLanguageCard is not null)
        {
            DictationOutputLanguageCard.Visibility = outputLanguage.IsVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (DictationOutputLanguageStatusText is not null && outputLanguage.IsVisible)
        {
            DictationOutputLanguageStatusText.Text = outputLanguage.StatusText;
        }

        if (EgOneSettingsPanel is not null)
        {
            EgOneSettingsPanel.Visibility = provider == PolishProvider.EgOne ? Visibility.Visible : Visibility.Collapsed;
        }
        if (provider == PolishProvider.EgOne)
        {
            UpdateEgOneCards();
        }
        if (OllamaSettingsPanel is not null)
        {
            OllamaSettingsPanel.Visibility = provider == PolishProvider.Ollama ? Visibility.Visible : Visibility.Collapsed;
        }
        if (CloudPolishSettingsPanel is not null)
        {
            CloudPolishSettingsPanel.Visibility = ProviderSettingsPresenter.UsesAModel(provider)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        UpdateOllamaLivePollTimer();
    }

    private int SelectedFinalEngineRawIndex() =>
        ParakeetEngineCard?.IsChecked == true
            ? 0
            : WhisperEngineCard?.IsChecked == true
                ? 1
                : -1;

    private FinalAsrEngine SelectedFinalEngine() =>
        WhisperEngineCard?.IsChecked == true
            ? FinalAsrEngine.Whisper
            : FinalAsrEngine.Parakeet;

    private void SetSelectedFinalEngineCards(FinalAsrEngine selectedEngine)
    {
        if (ParakeetEngineCard is not null)
        {
            ParakeetEngineCard.IsChecked = selectedEngine == FinalAsrEngine.Parakeet;
        }

        if (WhisperEngineCard is not null)
        {
            WhisperEngineCard.IsChecked = selectedEngine == FinalAsrEngine.Whisper;
        }

        RenderSpeechEngineCards();
    }

    private FinalAsrEngine ResolveEngineFromCard(object sender) =>
        sender is RadioButton radio && ReferenceEquals(radio, WhisperEngineCard)
            ? FinalAsrEngine.Whisper
            : ReferenceEquals(sender, ParakeetEngineCard)
                ? FinalAsrEngine.Parakeet
                : SelectedFinalEngine();

    private void ApplySelectedEngineCard(FinalAsrEngine selectedEngine)
    {
        _lastActiveEngine = selectedEngine;
        SetSelectedFinalEngineCards(selectedEngine);
        UpdateActiveEngineDisplay(selectedEngine);
        UpdateHistoryStatusCards();
        ShowSpeechEngineNotice(string.Empty);
        _ = TriggerAutoSaveAsync();
    }

    private void EngineCardChecked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _settings is null)
        {
            return;
        }

        var selectedEngine = ResolveEngineFromCard(sender);
        if (_lastActiveEngine == selectedEngine)
        {
            RenderSpeechEngineCards();
            return;
        }

        ApplySelectedEngineCard(selectedEngine);
    }

    private void EngineCardClicked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _settings is null)
        {
            return;
        }

        var selectedEngine = ResolveEngineFromCard(sender);
        if (_lastActiveEngine == selectedEngine)
        {
            return;
        }

        ApplySelectedEngineCard(selectedEngine);
    }

    private static bool IsTapFromNestedActionButton(object? originalSource)
    {
        var current = originalSource as DependencyObject;
        while (current is not null)
        {
            if (current is Button)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ParakeetEngineCardContainer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsTapFromNestedActionButton(e.OriginalSource) || _settings is null)
        {
            return;
        }

        if (ParakeetEngineCard is not null && ParakeetEngineCard.IsChecked != true)
        {
            ParakeetEngineCard.IsChecked = true;
        }
    }

    private void WhisperEngineCardContainer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsTapFromNestedActionButton(e.OriginalSource) || _settings is null)
        {
            return;
        }

        if (WhisperEngineCard is not null && WhisperEngineCard.IsChecked != true)
        {
            WhisperEngineCard.IsChecked = true;
        }
    }

    private void EngineCardsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (EngineCardsGrid is null || ParakeetEngineCardContainer is null || WhisperEngineCardContainer is null ||
            ParakeetCardColumn is null || WhisperCardColumn is null)
        {
            return;
        }

        var stacked = e.NewSize.Width < 540;
        if (stacked)
        {
            WhisperCardColumn.Width = new GridLength(0);
            Grid.SetRow(WhisperEngineCardContainer, 1);
            Grid.SetColumn(WhisperEngineCardContainer, 0);
        }
        else
        {
            WhisperCardColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(WhisperEngineCardContainer, 0);
            Grid.SetColumn(WhisperEngineCardContainer, 1);
        }
    }

    private void ParakeetDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSpeechEngineNotice(string.Empty);
        SpeechEngineDownloadRequested?.Invoke(FinalAsrEngine.Parakeet);
    }

    private void WhisperDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSpeechEngineNotice(string.Empty);
        SpeechEngineDownloadRequested?.Invoke(FinalAsrEngine.Whisper);
    }

    private async void ParakeetRemoveButton_Click(object sender, RoutedEventArgs e) =>
        await HandleSpeechEngineRemoveClickAsync(FinalAsrEngine.Parakeet).ConfigureAwait(true);

    private async void WhisperRemoveButton_Click(object sender, RoutedEventArgs e) =>
        await HandleSpeechEngineRemoveClickAsync(FinalAsrEngine.Whisper).ConfigureAwait(true);

    private async Task HandleSpeechEngineRemoveClickAsync(FinalAsrEngine engine)
    {
        if (_session.Closing)
        {
            return;
        }

        ShowSpeechEngineNotice(string.Empty);
        if (IsSpeechEngineBusy?.Invoke() == true)
        {
            ShowSpeechEngineNotice(EnginePolishPresentation.RemovalBlockedBusyNotice);
            return;
        }

        var activeEngine = _settings?.Preferences.Dictation.FinalEngine ?? SelectedFinalEngine();
        var isSelectedFinalEngine = engine == activeEngine;
        var livePreviewUsesParakeet = _settings?.Preferences.LivePreviewEnabled == true &&
            (!_livePreviewWhisperInstalled || _settings.Preferences.LivePreviewEngine == LivePreviewEngineChoice.Parakeet);
        var requiresConfirm = isSelectedFinalEngine ||
            (engine == FinalAsrEngine.Parakeet && livePreviewUsesParakeet);
        if (requiresConfirm)
        {
            if (Content?.XamlRoot is not { } xamlRoot)
            {
                return;
            }

            var confirmation = EnginePolishPresentation.DescribeActiveEngineRemovalDialog(
                engine,
                livePreviewUsesParakeet,
                isSelectedFinalEngine);
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                RequestedTheme = (Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
                Title = confirmation.Title,
                Content = new TextBlock
                {
                    Text = confirmation.Message,
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = confirmation.ConfirmButtonText,
                CloseButtonText = confirmation.CancelButtonText,
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _session.Closing)
            {
                return;
            }
        }

        if (IsSpeechEngineBusy?.Invoke() == true)
        {
            ShowSpeechEngineNotice(EnginePolishPresentation.RemovalBlockedBusyNotice);
            return;
        }

        SpeechEngineRemoveRequested?.Invoke(engine);
    }

    public void SetSpeechEngineModels(
        SpeechEngineModelState parakeet,
        SpeechEngineModelState whisper,
        FinalAsrEngine? downloadingEngine)
    {
        ArgumentNullException.ThrowIfNull(parakeet);
        ArgumentNullException.ThrowIfNull(whisper);
        _parakeetModelState = parakeet;
        _whisperModelState = whisper;
        _activeSpeechDownloadEngine = downloadingEngine;
        RenderSpeechEngineCards();
        UpdateActiveEngineDisplay();
    }

    public void SetSpeechEngineDelivery(FinalAsrEngine engine, ModelDeliveryPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        if (engine == FinalAsrEngine.Whisper)
        {
            _whisperModelState = _whisperModelState with
            {
                DeliveryStage = presentation.Stage,
                DownloadPercent = presentation.Percent,
                StatusDetail = presentation.Text,
            };
        }
        else
        {
            _parakeetModelState = _parakeetModelState with
            {
                DeliveryStage = presentation.Stage,
                DownloadPercent = presentation.Percent,
                StatusDetail = presentation.Text,
            };
        }

        _activeSpeechDownloadEngine = presentation.Stage == SpeechModelDelivery.Downloading
            ? engine
            : null;
        RenderSpeechEngineCards();
        UpdateActiveEngineDisplay();
    }

    public void ShowSpeechEngineNotice(string message)
    {
        if (SpeechEngineNoticeText is null || SpeechEngineNoticeBanner is null)
        {
            return;
        }

        var normalized = message ?? string.Empty;
        SetLiveText(SpeechEngineNoticeText, normalized);
        SpeechEngineNoticeBanner.Visibility = string.IsNullOrWhiteSpace(normalized)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void RenderSpeechEngineCards()
    {
        var selectedEngine = SelectedFinalEngine();
        var parakeetOtherInFlight = _activeSpeechDownloadEngine is not null && _activeSpeechDownloadEngine != FinalAsrEngine.Parakeet;
        var whisperOtherInFlight = _activeSpeechDownloadEngine is not null && _activeSpeechDownloadEngine != FinalAsrEngine.Whisper;
        var parakeetCard = EnginePolishPresentation.DescribeEngineCard(
            FinalAsrEngine.Parakeet,
            selectedEngine,
            _parakeetModelState,
            parakeetOtherInFlight);
        var whisperCard = EnginePolishPresentation.DescribeEngineCard(
            FinalAsrEngine.Whisper,
            selectedEngine,
            _whisperModelState,
            whisperOtherInFlight);

        if (ParakeetCardStatusText is not null)
        {
            SetLiveText(ParakeetCardStatusText, parakeetCard.StatusText);
        }

        if (WhisperCardStatusText is not null)
        {
            SetLiveText(WhisperCardStatusText, whisperCard.StatusText);
        }

        ApplySpeechEngineCardPresentation(
            parakeetCard,
            ParakeetEngineCardContainer,
            ParakeetCardSizeText,
            ParakeetCardDetailText,
            ParakeetCardProgress,
            ParakeetDownloadButton,
            ParakeetCancelButton,
            ParakeetRemoveButton);
        ApplySpeechEngineCardPresentation(
            whisperCard,
            WhisperEngineCardContainer,
            WhisperCardSizeText,
            WhisperCardDetailText,
            WhisperCardProgress,
            WhisperDownloadButton,
            WhisperCancelButton,
            WhisperRemoveButton);
    }

    private static void ApplySpeechEngineCardPresentation(
        SpeechEngineCardPresentation card,
        Border? container,
        TextBlock? sizeText,
        TextBlock? detailText,
        ProgressBar? progressBar,
        Button? downloadButton,
        Button? cancelButton,
        Button? removeButton)
    {
        if (container is not null)
        {
            var accentBrush = Application.Current.Resources["BrandAccentBrush"] as Brush;
            var dividerBrush = Application.Current.Resources["BrandDividerBrush"] as Brush;
            container.BorderBrush = card.IsSelected ? accentBrush : dividerBrush;
            container.BorderThickness = new Thickness(card.IsSelected ? 2 : 1);
        }

        if (sizeText is not null)
        {
            sizeText.Text = card.BundleSizeText;
        }

        if (detailText is not null)
        {
            detailText.Text = card.StatusDetail ?? string.Empty;
            detailText.Visibility = string.IsNullOrWhiteSpace(card.StatusDetail)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        if (progressBar is not null)
        {
            progressBar.Visibility = card.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            progressBar.Value = card.DownloadPercent;
        }

        if (downloadButton is not null)
        {
            downloadButton.Content = card.DownloadButtonText;
            downloadButton.IsEnabled = card.DownloadButtonEnabled;
            downloadButton.Visibility = card.ShowDownloadButton ? Visibility.Visible : Visibility.Collapsed;
        }

        if (cancelButton is not null)
        {
            cancelButton.Visibility = card.ShowCancelButton ? Visibility.Visible : Visibility.Collapsed;
        }

        if (removeButton is not null)
        {
            removeButton.IsEnabled = card.RemoveButtonEnabled;
            removeButton.Visibility = card.ShowRemoveButton ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateActiveEngineDisplay(FinalAsrEngine? engineOverride = null)
    {
        var engine = engineOverride ?? SelectedFinalEngine();
        var summary = EnginePolishPresentation.DescribeEngine(engine);

        if (ActiveEngineNameText is not null)
        {
            ActiveEngineNameText.Text = summary.Name;
        }

        if (ActiveEngineBadgeText is not null)
        {
            ActiveEngineBadgeText.Text = summary.BadgeText;
        }

        if (ActiveEngineDescriptionText is not null)
        {
            ActiveEngineDescriptionText.Text = summary.Description;
        }

        if (ActiveEngineIcon is not null)
        {
            ActiveEngineIcon.Glyph = summary.IconGlyph;
        }

        if (ActiveEngineStatusText is not null)
        {
            var activeState = engine == FinalAsrEngine.Whisper ? _whisperModelState : _parakeetModelState;
            var activeCard = EnginePolishPresentation.DescribeEngineCard(
                engine,
                engine,
                activeState,
                otherDownloadInFlight: false);
            ActiveEngineStatusText.Text = activeState.DeliveryStage == SpeechModelDelivery.Unknown
                ? "Checking..."
                : activeCard.StatusText;
        }

        if (WhisperLanguageRow is not null)
        {
            WhisperLanguageRow.Visibility = summary.ShowWhisperLanguage
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    public void SetEgOneModels(
        IReadOnlyDictionary<string, EgOneModelState> states,
        bool serverExecutableExists,
        string? downloadingModelId)
    {
        ArgumentNullException.ThrowIfNull(states);
        foreach (var modelId in EgOneModelCatalog.AllIds)
        {
            if (states.TryGetValue(modelId, out var state))
            {
                _egOneModelStates[modelId] = state;
            }
        }

        _egOneServerExecutableExists = serverExecutableExists;
        _activeEgOneDownloadModelId = downloadingModelId;
        _hasEgOneInventorySnapshot = true;
        UpdateEgOneCards();
    }

    public void SetEgOneDelivery(string modelId, ModelDeliveryPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        var normalizedId = EnginePolishPresentation.NormalizeEgOneModelId(modelId);
        var existing = _egOneModelStates.GetValueOrDefault(
            normalizedId,
            EgOneModelState.Unknown(normalizedId));
        _egOneModelStates[normalizedId] = existing with
        {
            DeliveryStage = presentation.Stage,
            DownloadPercent = presentation.Percent,
            StatusDetail = presentation.Text,
        };
        _activeEgOneDownloadModelId = presentation.Stage == SpeechModelDelivery.Downloading
            ? normalizedId
            : null;
        _hasEgOneInventorySnapshot = true;
        UpdateEgOneCards();
    }

    public void ShowEgOneNotice(string message)
    {
        if (EgOneNoticeText is null || EgOneNoticeBanner is null)
        {
            return;
        }

        var normalized = message ?? string.Empty;
        SetLiveText(EgOneNoticeText, normalized);
        EgOneNoticeBanner.Visibility = string.IsNullOrWhiteSpace(normalized)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private EgOneModelState ResolveEgOneDisplayState(string modelId)
    {
        var normalizedId = EnginePolishPresentation.NormalizeEgOneModelId(modelId);
        if (_hasEgOneInventorySnapshot && _egOneModelStates.TryGetValue(normalizedId, out var state))
        {
            return state;
        }

        var configuredModelId = EnginePolishPresentation.NormalizeEgOneModelId(
            _selectedEgOneModelId ?? (_settings?.Preferences.Polish.Provider == PolishProvider.EgOne
                ? _settings.Preferences.Polish.ModelId
                : null));
        var (serverExists, modelExists) = CheckEgOneLocalAssets(normalizedId, configuredModelId);
        _egOneServerExecutableExists = serverExists;
        var hasBundledManifest = ModelDelivery.BundledModelManifests.TryRead(normalizedId) is not null;
        return new EgOneModelState(
            ModelId: normalizedId,
            IsInstalled: modelExists,
            CanRemove: modelExists &&
                EgOneRuntimeLocator.IsModelFileReady(EgOneRuntimeLocator.ResolveModelFile(_dataDirectory, normalizedId)) &&
                Directory.Exists(Path.Combine(_dataDirectory, "models", normalizedId)),
            HasBundledManifest: hasBundledManifest,
            DeliveryStage: modelExists ? SpeechModelDelivery.Installed : SpeechModelDelivery.Missing);
    }

    private void UpdateEgOneCards()
    {
        if (_isUpdatingEgOneCards
            || EgOneSizeFullCard is null
            || EgOneSizeMiniCard is null
            || EgOneSizeMicroCard is null)
        {
            return;
        }

        var profile = _hardwareProfile ??= HardwareProfileSummary.GetCurrent();
        if (EgOneMemoryText is not null)
        {
            EgOneMemoryText.Text = profile.SummaryText;
        }

        var rawModelId = _selectedEgOneModelId;
        if (string.IsNullOrWhiteSpace(rawModelId) &&
            _settings?.Preferences.Polish.Provider == PolishProvider.EgOne)
        {
            rawModelId = _settings.Preferences.Polish.ModelId;
        }

        var normalizedModelId = EnginePolishPresentation.NormalizeEgOneModelId(rawModelId);

        _isUpdatingEgOneCards = true;
        try
        {
            EgOneSizeFullCard.IsChecked = normalizedModelId == EnginePolishPresentation.EgOneFullModelId;
            EgOneSizeMiniCard.IsChecked = normalizedModelId == EnginePolishPresentation.EgOneMiniModelId;
            EgOneSizeMicroCard.IsChecked = normalizedModelId == EnginePolishPresentation.EgOneMicroModelId;
        }
        finally
        {
            _isUpdatingEgOneCards = false;
        }

        var selectedState = ResolveEgOneDisplayState(normalizedModelId);
        var slots = new[]
        {
            (
                EnginePolishPresentation.EgOneFullModelId,
                EgOneSizeFullCard,
                EgOneFullRecommendedBadge,
                EgOneFullStatusBadge,
                EgOneFullStatusBadgeText,
                EgOneFullTaglineText,
                EgOneFullDescriptionText,
                EgOneFullSpecsText,
                EgOneFullDetailText,
                EgOneFullProgress,
                EgOneFullDownloadButton,
                EgOneFullCancelButton,
                EgOneFullRemoveButton
            ),
            (
                EnginePolishPresentation.EgOneMiniModelId,
                EgOneSizeMiniCard,
                EgOneMiniRecommendedBadge,
                EgOneMiniStatusBadge,
                EgOneMiniStatusBadgeText,
                EgOneMiniTaglineText,
                EgOneMiniDescriptionText,
                EgOneMiniSpecsText,
                EgOneMiniDetailText,
                EgOneMiniProgress,
                EgOneMiniDownloadButton,
                EgOneMiniCancelButton,
                EgOneMiniRemoveButton
            ),
            (
                EnginePolishPresentation.EgOneMicroModelId,
                EgOneSizeMicroCard,
                EgOneMicroRecommendedBadge,
                EgOneMicroStatusBadge,
                EgOneMicroStatusBadgeText,
                EgOneMicroTaglineText,
                EgOneMicroDescriptionText,
                EgOneMicroSpecsText,
                EgOneMicroDetailText,
                EgOneMicroProgress,
                EgOneMicroDownloadButton,
                EgOneMicroCancelButton,
                EgOneMicroRemoveButton
            ),
        };

        foreach (var slot in slots)
        {
            var state = ResolveEgOneDisplayState(slot.Item1);
            var otherInFlight = _activeEgOneDownloadModelId is not null &&
                !string.Equals(_activeEgOneDownloadModelId, slot.Item1, StringComparison.Ordinal);
            var card = EnginePolishPresentation.DescribeEgOneCard(
                slot.Item1,
                normalizedModelId,
                profile.RecommendedEgOneModelId,
                state,
                otherInFlight,
                _egOneServerExecutableExists);
            ApplyEgOneCardPresentation(
                card,
                slot.Item2,
                slot.Item3,
                slot.Item4,
                slot.Item5,
                slot.Item6,
                slot.Item7,
                slot.Item8,
                slot.Item9,
                slot.Item10,
                slot.Item11,
                slot.Item12,
                slot.Item13);
        }

        if (EgOneInstallStatusText is not null)
        {
            EgOneInstallStatusText.Text = EnginePolishPresentation.FormatEgOneInstallStatus(
                _egOneServerExecutableExists,
                selectedState.IsInstalled);
        }
    }

    private static void ApplyEgOneCardPresentation(
        EgOneCardPresentation card,
        RadioButton? cardButton,
        Border? recommendedBadge,
        Border? statusBadge,
        TextBlock? statusBadgeText,
        TextBlock? taglineText,
        TextBlock? descriptionText,
        TextBlock? specsText,
        TextBlock? detailText,
        ProgressBar? progressBar,
        Button? downloadButton,
        Button? cancelButton,
        Button? removeButton)
    {
        if (cardButton is not null)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cardButton, card.AutomationName);
        }

        if (recommendedBadge is not null)
        {
            recommendedBadge.Visibility = card.IsRecommended ? Visibility.Visible : Visibility.Collapsed;
        }

        if (statusBadgeText is not null)
        {
            statusBadgeText.Text = card.StatusText;
            var foregroundKey = card.IsInstalled
                ? "BrandSuccessBrush"
                : card.ShowProgress
                    ? "BrandAccentBrush"
                    : "BrandTextSecondaryBrush";
            if (Application.Current.Resources[foregroundKey] is Brush brush)
            {
                statusBadgeText.Foreground = brush;
            }
        }

        if (statusBadge is not null)
        {
            var backgroundKey = card.IsInstalled
                ? "BrandSuccessSoftBrush"
                : card.ShowProgress
                    ? "BrandAccentLightBrush"
                    : "BrandPageBgBrush";
            if (Application.Current.Resources[backgroundKey] is Brush backgroundBrush)
            {
                statusBadge.Background = backgroundBrush;
            }
        }

        if (taglineText is not null)
        {
            taglineText.Text = card.Tagline;
        }

        if (descriptionText is not null)
        {
            descriptionText.Text = card.Description;
        }

        if (specsText is not null)
        {
            specsText.Text = card.SpecsSummary;
        }

        if (detailText is not null)
        {
            detailText.Text = card.StatusDetail;
        }

        if (progressBar is not null)
        {
            progressBar.Visibility = card.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            progressBar.Value = card.DownloadPercent;
        }

        if (downloadButton is not null)
        {
            downloadButton.Content = card.DownloadButtonText;
            downloadButton.IsEnabled = card.DownloadButtonEnabled;
            downloadButton.Visibility = card.ShowDownloadButton ? Visibility.Visible : Visibility.Collapsed;
        }

        if (cancelButton is not null)
        {
            cancelButton.Visibility = card.ShowCancelButton ? Visibility.Visible : Visibility.Collapsed;
        }

        if (removeButton is not null)
        {
            removeButton.IsEnabled = card.RemoveButtonEnabled;
            removeButton.Visibility = card.ShowRemoveButton ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void EgOneDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string modelId ||
            !EnginePolishPresentation.IsKnownEgOneModelId(modelId))
        {
            return;
        }

        ShowEgOneNotice(string.Empty);
        EgOneModelDownloadRequested?.Invoke(EnginePolishPresentation.NormalizeEgOneModelId(modelId));
    }

    private void CancelEgOneDownloadButton_Click(object sender, RoutedEventArgs e) =>
        EgOneModelDownloadCancelRequested?.Invoke();

    private async void EgOneRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Closing ||
            (sender as FrameworkElement)?.Tag is not string rawModelId ||
            !EnginePolishPresentation.IsKnownEgOneModelId(rawModelId))
        {
            return;
        }

        var normalizedId = EnginePolishPresentation.NormalizeEgOneModelId(rawModelId);
        ShowEgOneNotice(string.Empty);
        if (IsSpeechEngineBusy?.Invoke() == true)
        {
            ShowEgOneNotice(EnginePolishPresentation.EgOneRemovalBlockedBusyNotice);
            return;
        }

        var selectedEgOne = EnginePolishPresentation.NormalizeEgOneModelId(
            _selectedEgOneModelId ?? (_settings?.Preferences.Polish.Provider == PolishProvider.EgOne
                ? _settings.Preferences.Polish.ModelId
                : null));
        var activeProvider = _settings?.Preferences.Polish.Provider
            ?? PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        var isSelected = string.Equals(normalizedId, selectedEgOne, StringComparison.Ordinal);
        if (isSelected)
        {
            if (Content?.XamlRoot is not { } xamlRoot)
            {
                return;
            }

            var confirmation = EnginePolishPresentation.DescribeEgOneRemovalDialog(
                normalizedId,
                isCurrentlySelected: true,
                egOneActiveProvider: activeProvider == PolishProvider.EgOne);
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                RequestedTheme = (Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
                Title = confirmation.Title,
                Content = new TextBlock
                {
                    Text = confirmation.Message,
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = confirmation.ConfirmButtonText,
                PrimaryButtonStyle = Application.Current.Resources["BrandDestructiveButtonStyle"] as Style,
                CloseButtonText = confirmation.CancelButtonText,
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _session.Closing)
            {
                return;
            }
        }

        if (IsSpeechEngineBusy?.Invoke() == true)
        {
            ShowEgOneNotice(EnginePolishPresentation.EgOneRemovalBlockedBusyNotice);
            return;
        }

        EgOneModelRemoveRequested?.Invoke(normalizedId);
    }

    private (bool ServerExists, bool ModelExists) CheckEgOneLocalAssets(string normalizedModelId, string? configuredModelId = null)
    {
        try
        {
            var serverExecutable = EgOneRuntimeLocator.ResolveServerExecutable(
                AppContext.BaseDirectory,
                _dataDirectory,
                _storeInstalled,
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_SERVER_EXE"));
            var serverExists = File.Exists(serverExecutable);

            var envOverride = configuredModelId is not null &&
                string.Equals(normalizedModelId, configuredModelId, StringComparison.Ordinal)
                    ? Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_MODEL_PATH")
                    : null;
            var modelFile = EgOneRuntimeLocator.ResolveModelFile(
                _dataDirectory,
                normalizedModelId,
                envOverride);
            var modelExists = EgOneRuntimeLocator.IsModelFileReady(modelFile);

            return (serverExists, modelExists);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, false);
        }
    }

    private void EgOneSizeCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _isUpdatingEgOneCards || _settings is null)
        {
            return;
        }

        var modelId = (sender as FrameworkElement)?.Tag as string;
        if (!EnginePolishPresentation.IsKnownEgOneModelId(modelId))
        {
            return;
        }

        var normalized = EnginePolishPresentation.NormalizeEgOneModelId(modelId);
        _selectedEgOneModelId = normalized;
        _selectedPolishModelId = _polishModelSetByApp = normalized;

        UpdateEgOneCards();
        _ = TriggerAutoSaveAsync();
    }

    private int ResolvePolishProviderIndexFromCard(object sender) =>
        sender is RadioButton { DataContext: SelectableChoiceOption option }
        && Array.IndexOf(PolishProviderChoices, option) is var idx and >= 0
            ? idx
            : SelectedIndexOf(PolishProviderChoices);

    private async Task ApplySelectedPolishProviderCardAsync(
        int selectedIndex,
        PolishProvider provider,
        bool allowDefaultModel = true)
    {
        SelectChoice(PolishProviderChoices, selectedIndex);
        _lastActivePolishProvider = provider;
        if (provider == PolishProvider.EgOne)
        {
            _selectedEgOneModelId ??= EnginePolishPresentation.EgOneFullModelId;
            _selectedPolishModelId = _polishModelSetByApp = _selectedEgOneModelId;
        }

        UpdateActivePolishProviderDisplay();
        UpdateHistoryStatusCards();
        if (PolishProviderChooserPanel is not null)
        {
            PolishProviderChooserPanel.Visibility = Visibility.Collapsed;
        }

        ApiKeyPasswordBox.Password = string.Empty;
        RefreshApiKeyStatus();
        var chooseDefault = allowDefaultModel && provider != PolishProvider.EgOne;
        await RefreshPolishModelChoicesAsync(
            provider,
            chooseDefault: chooseDefault).ConfigureAwait(true);
        if (provider == PolishProvider.EgOne)
        {
            UpdateEgOneCards();
        }

        _ = TriggerAutoSaveAsync();
    }

    private async void PolishProviderCardChecked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _settings is null)
        {
            return;
        }

        var selectedIndex = ResolvePolishProviderIndexFromCard(sender);
        var provider = PolishProviderFromIndex(selectedIndex);
        if (_lastActivePolishProvider == provider)
        {
            return;
        }

        await ApplySelectedPolishProviderCardAsync(selectedIndex, provider).ConfigureAwait(true);
    }

    private async void PolishProviderCardClicked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _settings is null)
        {
            return;
        }

        var selectedIndex = ResolvePolishProviderIndexFromCard(sender);
        var provider = PolishProviderFromIndex(selectedIndex);
        if (_lastActivePolishProvider == provider)
        {
            if (PolishProviderChooserPanel is not null)
            {
                PolishProviderChooserPanel.Visibility = Visibility.Collapsed;
            }

            return;
        }

        await ApplySelectedPolishProviderCardAsync(selectedIndex, provider).ConfigureAwait(true);
    }

    private void PolishModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingSettings || _isUpdatingPolishModelPicker || _settings is null)
        {
            return;
        }

        if (PolishModelPicker.SelectedItem is string modelId &&
            !string.Equals(_selectedPolishModelId, modelId, StringComparison.Ordinal))
        {
            _selectedPolishModelId = _polishModelSetByApp = modelId;
            UpdateActivePolishProviderDisplay();
            _ = WarmUpSelectedOllamaModelAsync(modelId);
            _ = TriggerAutoSaveAsync();
        }
    }

    private bool IsSelectedOllamaModelHosted()
    {
        var current = _selectedPolishModelId.Trim();
        if (string.IsNullOrEmpty(current))
        {
            current = _settings?.Preferences.Polish.ModelId?.Trim() ?? string.Empty;
        }

        if (string.IsNullOrEmpty(current))
        {
            return false;
        }

        if (OllamaModelCatalog.LooksLikeHostedCloudModel(current))
        {
            return true;
        }

        return OllamaModels?.View.HostedRows.Any(row =>
            row.Installed &&
            OllamaModelCatalog.SameModel(row.Id, current)) == true;
    }

    private async void SaveApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        switch (_session.Provider.SaveKey(provider, ApiKeyPasswordBox.Password))
        {
            case ApiKeySaveOutcome.NotACloudProvider:
                ShowMessage(
                    "No cloud key needed",
                    "Choose OpenAI, Anthropic, or Gemini before storing a provider key.",
                    InfoBarSeverity.Informational);
                return;
            case ApiKeySaveOutcome.EmptyKey:
                ShowMessage("Enter an API key", "The key field is empty.", InfoBarSeverity.Warning);
                ApiKeyPasswordBox.Focus(FocusState.Programmatic);
                return;
            case ApiKeySaveOutcome.Saved:
                ApiKeyPasswordBox.Password = string.Empty;
                RefreshApiKeyStatus();
                ShowMessage(
                    $"{ProviderDisplayName(provider)} key saved",
                    "The key is stored in Windows Credential Manager. It is not part of settings, profiles, history, or diagnostics.",
                    InfoBarSeverity.Success);
                await RefreshPolishModelChoicesAsync(provider, chooseDefault: true)
                    .ConfigureAwait(true);
                UpdateActivePolishProviderDisplay();
                _ = TriggerAutoSaveAsync();
                return;
            default:
                ApiKeyPasswordBox.Password = string.Empty;
                RefreshApiKeyStatus();
                ShowMessage(
                    "API key was not saved",
                    "Windows Credential Manager is unavailable. No key was written to settings or exports.",
                    InfoBarSeverity.Error);
                return;
        }
    }

    private async void RemoveApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        switch (_session.Provider.CheckKeyRemoval(provider))
        {
            case ApiKeyRemovalCheck.NotACloudProvider:
                return;
            case ApiKeyRemovalCheck.NothingStored:
                RefreshApiKeyStatus();
                ShowMessage(
                    "No stored key to remove",
                    $"No {ProviderDisplayName(provider)} key is stored for EnviousWispr in Windows Credential Manager.",
                    InfoBarSeverity.Informational);
                return;
            default:
                break;
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

        if (_session.Provider.RemoveKey(provider) == ApiKeyRemoveOutcome.Removed)
        {
            ApiKeyPasswordBox.Password = string.Empty;
            RefreshApiKeyStatus();
            await RefreshPolishModelChoicesAsync(provider, chooseDefault: false)
                .ConfigureAwait(true);
            ShowMessage(
                $"{ProviderDisplayName(provider)} key removed",
                "The stored credential was removed from Windows Credential Manager.",
                InfoBarSeverity.Success);
        }
        else
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

    /// <summary>The microphone test's lifecycle, without the page: who may open the device, for how long, and what the counts mean.</summary>
    /// <remarks>
    /// THE PAGE KEEPS THE METER, THE WORDS AND THE PRIVACY LINK. The rules - a dictation outranks a
    /// test, one test at a time, the token goes all the way into the capture, the stop is never
    /// cancelled, a frame from a finished test is not drawn - are MicrophoneTestControllerTests.
    /// </remarks>

    /// <summary>Opens the microphone for a moment and shows what actually arrives.</summary>
    /// <remarks>
    /// IT SAYS WHICH KIND OF NOTHING. A device that could not be opened, a device that opened and
    /// delivered packets Windows marked as deliberately silent, and a device that delivered real
    /// packets of zeroes are three different faults with three different answers, and a bare "no
    /// sound" sends somebody to look in the wrong place for all three. The verdict is the
    /// controller's; the sentences for the ways a test does not reach one are here.
    /// </remarks>
    private async void MicrophoneTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.MicrophoneTest.IsRunning)
        {
            return;
        }

        // THE DEVICE IS READ HERE, ON THE UI THREAD, and the test is told whether a recording holds
        // the microphone - the pair that, with the cancel from the other side, keeps a test and a
        // dictation from both opening it.
        AudioDeviceId? device = (MicrophoneComboBox.SelectedItem as MicrophoneChoice)?.Id is { } id
            ? new AudioDeviceId(id)
            : null;
        var recording = _currentOverlayState == DictationOverlayState.Recording;
        if (recording)
        {
            SetLiveText(
                MicrophoneTestResultText,
                "A recording is running. Finish it, then test the microphone.");
            return;
        }

        MicrophoneTestButton.IsEnabled = false;
        SetLiveText(MicrophoneTestResultText, "Listening. Say a few words.");
        _session.MicrophoneTest.Frame += OnMicrophoneTestFrame;
        try
        {
            var result = await _session.MicrophoneTest.RunAsync(device, recording).ConfigureAwait(true);
            if (_session.Closing)
            {
                // The exit stopped the test, or began as it finished; the page is not written to.
                return;
            }

            switch (result.Outcome)
            {
                case MicrophoneTestOutcome.Completed:
                    SetLiveText(MicrophoneTestResultText, result.Verdict!);
                    break;
                case MicrophoneTestOutcome.DeviceWouldNotOpen:
                    SetLiveText(
                        MicrophoneTestResultText,
                        "Windows would not open that microphone. Check it is plugged in and that "
                            + "microphone privacy allows desktop apps.");
                    break;
                case MicrophoneTestOutcome.StoppedPartWay:
                    SetLiveText(
                        MicrophoneTestResultText,
                        "The microphone stopped part way through the test. It may have been unplugged, "
                            + "or taken by another app.");
                    break;
                case MicrophoneTestOutcome.Cancelled:
                    SetLiveText(MicrophoneTestResultText, "Microphone test stopped.");
                    break;
                default:
                    // Already running: the controller refused, and the page has nothing new to say.
                    // (The recording case was answered above; the controller's own refusal of it is
                    // a contract for callers that do not ask first.)
                    break;
            }
        }
        finally
        {
            _session.MicrophoneTest.Frame -= OnMicrophoneTestFrame;
            MicrophoneTestButton.IsEnabled = true;
            DrawMicrophoneTestLevel(0f);
        }
    }

    /// <summary>Stops a microphone test, because something with a better claim wants the device.</summary>
    private void CancelMicrophoneTest() => _session.MicrophoneTest.Cancel();

    /// <summary>One meter frame, posted to the UI thread and refused there if its test has ended.</summary>
    /// <remarks>
    /// AN UPDATE ALREADY ON THE QUEUE OUTLIVES THE UNSUBSCRIBE. Removing the handler stops new ones
    /// being posted and does nothing about the ones already waiting, so a late update could relight
    /// the meter after the test had finished and cleared it, or during the next one. The frame
    /// carries its test, and the controller says whether that test is still the one running.
    /// </remarks>
    private void OnMicrophoneTestFrame(object? sender, MicrophoneTestFrame frame) =>
        MicrophoneTestBars.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_session.MicrophoneTest.IsCurrent(frame.TestId))
            {
                return;
            }

            DrawMicrophoneTestLevel(frame.Level);
        });

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
        _activeWordDialogSuggestionSink?.Invoke(term, advice);
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
        // THE PICKER CARRIES THE ANSWER ITSELF, not a position that has to be looked up. A mapping
        // from index to meaning is a contract between this file and the ORDER of three strings in
        // the markup, and reordering those strings changes what people choose while every test
        // stays green. Each choice now holds its own value, so there is nothing left to keep in step.
        //
        // READ HERE, ON THE UI THREAD, BEFORE THE SAVE. The transform below runs inside the writer's
        // gate, after a wait of unknown length on another save, on whatever thread that wait ends
        // on; a control read in there is a control read off the UI thread. The word is built now
        // and the transform only places it.
        var word = new CustomWordEntry(
            spokenForm,
            replacement,
            WordStrictnessComboBox.SelectedValue as MatchStrictness? ?? MatchStrictness.Default);
        return CommitVocabularyAsync(_session.Vocabulary.AddWordAsync(word), "Dictionary saved");
    }

    /// <summary>Puts the picker back to the ordinary rule after a word is saved.</summary>
    /// <remarks>
    /// A picker that keeps its last answer means the second word somebody adds is silently strict
    /// because the first one was, and the only sign of it is a column they have no reason to be
    /// watching.
    /// </remarks>
    private void ResetMatchStrictness() =>
        WordStrictnessComboBox.SelectedItem = WordStrictnessComboBox.Items
            .OfType<ComboBoxItem>()
            .First(item => item.Tag is MatchStrictness.Default or (int)MatchStrictness.Default);

    /// <summary>Selects every word, or clears the selection when they are all already chosen.</summary>
    /// <remarks>
    /// ONE PRESS FOR THE WHOLE LIST, AND THE SAME PRESS TO UNDO IT. Selecting twenty words by hand
    /// to remove them is the work this button exists to remove; a separate "deselect all" would be a
    /// second control for the same idea, and macOS uses the one toggle for both.
    /// </remarks>
    private void SelectAllWordsButton_Click(object sender, RoutedEventArgs e)
    {
        var shownCount = DictionaryList.Items.Count;
        if (DictionaryList.SelectionMode != ListViewSelectionMode.Multiple)
        {
            MassEditWordsToggle_Click(sender, e);
        }

        if (DictionaryList.SelectedItems.Count == shownCount && shownCount > 0)
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
        var selected = DictionaryList.SelectedItems
            .Select(item => item is DictionaryWordRow row ? row.Entry : item as CustomWordEntry)
            .OfType<CustomWordEntry>()
            .ToArray();
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
        var removal = await CommitVocabularyAsync(_session.Vocabulary.RemoveWordsAsync(selected)).ConfigureAwait(true);
        if (!removal.Saved)
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

    /// <summary>Saves the snippet in the boxes, or says in the person's terms why it was not saved.</summary>
    /// <remarks>
    /// THE TEXT IS NOT TRIMMED. Leading or trailing space or a line break can be deliberate - a snippet
    /// that starts on a new line, or ends with a space before the next word - and "exactly as written"
    /// has to mean exactly (macOS <c>SnippetEditSheet.save</c>). The trigger is trimmed; its spacing
    /// means nothing to the matcher.
    /// </remarks>
    private async void AddSnippetButton_Click(object sender, RoutedEventArgs e)
    {
        var trigger = SnippetNameBox.Text.Trim();
        var body = SnippetBodyBox.Text;

        // Same reason as the word boxes above: a refused save must not eat the snippet someone
        // just wrote.
        var saved = await CommitVocabularyAsync(_session.Vocabulary.AddSnippetAsync(new SnippetEntry(trigger, body))).ConfigureAwait(true);
        if (!saved.Saved)
        {
            return;
        }

        if (saved.Value.Refusal is { } refusal)
        {
            ShowMessage("Snippet not saved", SnippetRefusalMessage(refusal, saved.Value.Clash), InfoBarSeverity.Warning);
            return;
        }

        ShowMessage("Snippet saved", "The change was saved locally.", InfoBarSeverity.Success);
        SnippetNameBox.Text = string.Empty;
        SnippetBodyBox.Text = string.Empty;
    }

    /// <summary>Appends a fill-in's token to the snippet text.</summary>
    /// <remarks>
    /// AT THE END, NOT AT THE CARET, as macOS does: the helper copy under the buttons says so, and the
    /// person can move the token anywhere. The token comes from the one spelling the matcher reads,
    /// never a string written here.
    /// </remarks>
    private void SnippetFillInButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } ||
            !Enum.TryParse<SnippetPlaceholder>(tag, ignoreCase: false, out var placeholder))
        {
            return;
        }

        SnippetBodyBox.Text += SnippetPlaceholders.Token(placeholder);
        SnippetBodyBox.SelectionStart = SnippetBodyBox.Text.Length;
    }

    private void SnippetKeywordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            CommitSnippetKeyword();
        }
    }

    private void SnippetKeywordBox_LostFocus(object sender, RoutedEventArgs e) => CommitSnippetKeyword();

    /// <summary>Stores the keyword in the box when it changed, then shows what was stored.</summary>
    /// <remarks>
    /// READ BACK RATHER THAN KEEPING WHAT WAS TYPED: a cleared box stores the default, so the box must
    /// show what was saved, not what was on screen. A refused keyword puts the stored one back.
    /// </remarks>
    private async void CommitSnippetKeyword()
    {
        var typed = SnippetKeywordBox.Text;
        if (_isApplyingSettings || string.Equals(typed, _settings.UserData.SnippetKeyword, StringComparison.Ordinal))
        {
            return;
        }

        var saved = await CommitVocabularyAsync(_session.Vocabulary.SetSnippetKeywordAsync(typed)).ConfigureAwait(true);
        if (saved.Saved && saved.Value.Refusal is { } refusal)
        {
            ShowMessage("Keyword not saved", SnippetRefusalMessage(refusal, clash: null), InfoBarSeverity.Warning);
        }

        RefreshSnippetKeywordViews(force: true);
    }

    /// <summary>One sentence per refusal, written for the person who typed the thing; the macOS copy.</summary>
    private static string SnippetRefusalMessage(SnippetRefusal refusal, SnippetEntry? clash) => refusal switch
    {
        SnippetRefusal.TriggerEmpty =>
            "Give the snippet something to say. A trigger of only punctuation can never match.",
        SnippetRefusal.ExpansionEmpty =>
            "Add the text this snippet should paste. An empty snippet would delete the words you said.",
        SnippetRefusal.DuplicateTrigger =>
            $"You already have a snippet for those words: \u201C{clash?.Name}\u201D. Change one of them.",
        SnippetRefusal.KeywordNotOneWord =>
            "Your keyword has to be a single word. Pick one you would not say by accident.",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "Not a snippet refusal."),
    };

    /// <summary>The keyword box and the two sentences built from the keyword the person actually has.</summary>
    /// <remarks>
    /// THE EXAMPLE IS BUILT FROM A TRIGGER THE PERSON ACTUALLY HAS, never a literal: a fixed "my email"
    /// becomes a lie the moment that snippet is renamed or deleted, and with no snippets there is no
    /// honest example, so it names none (macOS <c>SnippetsView.keywordExample</c>). The box is left
    /// alone while it has focus unless a commit asks, or a refresh would write the old keyword over
    /// what is being typed.
    /// </remarks>
    private void RefreshSnippetKeywordViews(bool force)
    {
        var keyword = _settings.UserData.SnippetKeyword;
        if (force || SnippetKeywordBox.FocusState == FocusState.Unfocused)
        {
            SnippetKeywordBox.Text = keyword;
        }

        var snippets = _settings.UserData.Snippets;
        SnippetKeywordExample.Text = snippets.Count > 0
            ? $"For example, say \u201C{keyword} {snippets[0].Name}\u201D and that snippet is pasted."
            : "Say your keyword, then the words you saved a snippet under, and that snippet is pasted.";
        SnippetTriggerHelp.Text = $"Matched word for word, and only after you say \u201C{keyword}\u201D.";
    }

    private async void RemoveSnippetButton_Click(object sender, RoutedEventArgs e)
    {
        if (SnippetList.SelectedItem is not SnippetRow { Entry: var selected })
        {
            ShowMessage("Select a snippet first", "Choose the snippet you want to remove.", InfoBarSeverity.Informational);
            return;
        }

        await CommitVocabularyAsync(_session.Vocabulary.RemoveSnippetAsync(selected), "Snippet removed").ConfigureAwait(true);
    }

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshHistoryView();

    private void ClearHistorySearchButton_Click(object sender, RoutedEventArgs e) =>
        HistorySearchBox.Text = string.Empty;

    private async void FocusAddWordButton_Click(object sender, RoutedEventArgs e) =>
        await OpenAddWordDialogAsync().ConfigureAwait(true);

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

    private bool _copyingRecovery;

    /// <remarks>
    /// COPYING IS DEALING WITH IT. Once the recovered words are on the clipboard and the encrypted
    /// copy has been removed, the card closes and the app forgets the pending recovery so no stale
    /// banner stays behind. If deletion fails, keep the card visible and pending so the user can
    /// review or retry removing it.
    /// </remarks>
    private async void CopyRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_copyingRecovery)
        {
            return;
        }

        var recovered = RecoveryTextBox.Text;
        if (string.IsNullOrWhiteSpace(recovered))
        {
            return;
        }

        _copyingRecovery = true;
        try
        {
            var package = new DataPackage();
            package.SetText(recovered);
            Clipboard.SetContent(package);
            Clipboard.Flush();

            var recoveryDeleted = await _session.History.DeleteRecoveryAsync().ConfigureAwait(true);
            if (_session.Closing)
            {
                return;
            }

            if (recoveryDeleted)
            {
                ClearRecoveredText();
                OnboardingReliabilityInfoBar.IsOpen = false;
                RecoveryCleared?.Invoke();
                ShowMessage(
                    "Recovered text copied",
                    "The recovered dictation is on your clipboard, and its private recovery copy was removed from this PC.",
                    InfoBarSeverity.Success);
            }
            else
            {
                ShowMessage(
                    "Recovered text copied",
                    "The recovered dictation is on your clipboard. Its private recovery copy could not be removed, so delete it when you no longer need it.",
                    InfoBarSeverity.Warning);
            }
        }
        finally
        {
            _copyingRecovery = false;
        }
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

        var recoveryDeleted = await _session.History.DeleteRecoveryAsync().ConfigureAwait(true);
        if (_session.Closing)
        {
            return;
        }

        if (recoveryDeleted)
        {
            ClearRecoveredText();
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

    private async void RepolishHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepolishing)
        {
            return;
        }

        if (HistoryList.SelectedItem is not HistoryItemViewModel selected)
        {
            ShowMessage("Select a dictation first", "Choose the history entry you want to re-polish.", InfoBarSeverity.Informational);
            return;
        }

        if (selected.IsTemporary)
        {
            ShowMessage("Keep dictation first", "Escape recovery entries must be kept before re-polishing.", InfoBarSeverity.Informational);
            return;
        }

        if (_settings?.Preferences.Polish.Provider == PolishProvider.None)
        {
            ShowMessage("AI polish is off", "Enable an AI polish provider in settings first.", InfoBarSeverity.Warning);
            return;
        }

        _isRepolishing = true;
        if (RepolishHistoryProgress is not null)
        {
            RepolishHistoryProgress.IsActive = true;
            RepolishHistoryProgress.Visibility = Visibility.Visible;
        }
        UpdateSelectionDependentButtons();

        try
        {
            var result = await _session.History.RepolishAsync(selected.Id).ConfigureAwait(true);
            if (result.Status == HistoryRepolishStatus.Closing || _session.Closing)
            {
                return;
            }

            if (result.Succeeded)
            {
                if (result.View is not null)
                {
                    ShowHistory(result.View);
                    if (HistoryList.ItemsSource is HistoryItemViewModel[] items)
                    {
                        var reselected = items.FirstOrDefault(i => i.Id == selected.Id);
                        if (reselected is not null)
                        {
                            HistoryList.SelectedItem = reselected;
                        }
                    }
                }
                if (result.Entry is not null)
                {
                    HistoryDetailTextBox.Text = result.Entry.Text;
                }
                ShowMessage("Re-polished dictation", "Transcript updated with active polish model.", InfoBarSeverity.Success);
            }
            else
            {
                var (title, message, severity) = result.Status switch
                {
                    HistoryRepolishStatus.NoProviderConfigured => ("AI polish is off", "Enable an AI polish provider in settings first.", InfoBarSeverity.Warning),
                    HistoryRepolishStatus.EscapeRecoveryEntryMustBeKept => ("Keep dictation first", "Escape recovery entries must be kept before re-polishing.", InfoBarSeverity.Informational),
                    HistoryRepolishStatus.PolishRefused => ("Re-polish refused", "The polish provider returned no response.", InfoBarSeverity.Warning),
                    HistoryRepolishStatus.EntryNotFound => ("History entry not found", "The entry may have already been removed.", InfoBarSeverity.Warning),
                    HistoryRepolishStatus.EntryExpired => ("History entry expired", "The entry is past retention.", InfoBarSeverity.Warning),
                    _ => ("Re-polish failed", "The polish service encountered an error.", InfoBarSeverity.Error),
                };
                ShowMessage(title, message, severity);
            }
        }
        finally
        {
            _isRepolishing = false;
            if (RepolishHistoryProgress is not null)
            {
                RepolishHistoryProgress.IsActive = false;
                RepolishHistoryProgress.Visibility = Visibility.Collapsed;
            }
            UpdateSelectionDependentButtons();
        }
    }

    private async void DeleteHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItemViewModel selected)
        {
            ShowMessage("Select a dictation first", "Choose the history entry you want to delete.", InfoBarSeverity.Informational);
            return;
        }

        var result = await _session.History.DeleteAsync(selected.Id).ConfigureAwait(true);
        if (result.Closing || _session.Closing)
        {
            return;
        }

        if (result.Succeeded)
        {
            BeginHistoryReload();
            ShowHistory(result.View!);
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

        var result = await _session.History.KeepAsync(selected.Id).ConfigureAwait(true);
        if (result.Closing || _session.Closing)
        {
            return;
        }

        if (result.Succeeded)
        {
            BeginHistoryReload();
            ShowHistory(result.View!);
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
            PrimaryButtonStyle = Application.Current.Resources["BrandDestructiveButtonStyle"] as Style,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await _session.History.ClearAsync().ConfigureAwait(true);
        if (result.Closing || _session.Closing)
        {
            return;
        }

        if (result.Succeeded)
        {
            BeginHistoryReload();
            ShowHistory(result.View!);
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
    private bool _syncingVocabularyPacks;

    private void RefreshVocabularyPacksPanel()
    {
        if (VocabularyPacksList is null || _settings is null)
        {
            return;
        }

        var enabledIds = new HashSet<string>(
            _settings.UserData.EnabledVocabularyPackIds,
            StringComparer.OrdinalIgnoreCase);

        if (VocabularyPacksList.Children.Count != VocabularyPacks.All.Count)
        {
            VocabularyPacksList.Children.Clear();
            foreach (var pack in VocabularyPacks.All)
            {
                VocabularyPacksList.Children.Add(BuildVocabularyPackCard(pack));
            }
        }

        _syncingVocabularyPacks = true;
        try
        {
            foreach (var child in VocabularyPacksList.Children.OfType<Border>())
            {
                if (child.Tag is ToggleSwitch toggle && toggle.Tag is string packId)
                {
                    toggle.IsOn = enabledIds.Contains(packId);
                }
            }
        }
        finally
        {
            _syncingVocabularyPacks = false;
        }
    }

    private Border BuildVocabularyPackCard(VocabularyPack pack)
    {
        var icon = new FontIcon
        {
            Glyph = pack.IconGlyph,
            FontSize = 16,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BrandAccentBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var iconTile = new Border
        {
            Width = 36,
            Height = 36,
            Style = (Style)Application.Current.Resources["BrandIconTileStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon,
        };

        var titleText = new TextBlock
        {
            Text = pack.Name,
            Style = (Style)Application.Current.Resources["BrandRowLabelStyle"],
        };
        var descText = new TextBlock
        {
            Text = pack.Description,
            Style = (Style)Application.Current.Resources["BrandHelperStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        var summaryText = new TextBlock
        {
            Text = $"{pack.FixCount} fixes \u00B7 e.g. {string.Join(", ", pack.ExampleTerms)}",
            Style = (Style)Application.Current.Resources["BrandHelperStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        var infoStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
            Children = { titleText, descText, summaryText },
        };

        var seeAllButton = new Button
        {
            Content = "See all",
            Style = (Style)Application.Current.Resources["BrandQuietButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(seeAllButton, $"See all words in {pack.Name}");
        seeAllButton.Click += async (_, _) => await ShowPackDetailDialogAsync(pack).ConfigureAwait(true);

        var toggle = new ToggleSwitch
        {
            Style = (Style)Application.Current.Resources["BrandToggleSwitchStyle"],
            MinWidth = 0,
            Tag = pack.Id,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLabeledBy(toggle, titleText);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(toggle, pack.Description);
        toggle.Toggled += async (_, _) =>
        {
            if (_syncingVocabularyPacks || _settings is null)
            {
                return;
            }

            var enabled = toggle.IsOn;
            var saveResult = await _session.Vocabulary.SetVocabularyPackEnabledAsync(pack.Id, enabled)
                .ConfigureAwait(true);
            if (!saveResult.Saved)
            {
                _syncingVocabularyPacks = true;
                toggle.IsOn = !enabled;
                _syncingVocabularyPacks = false;
                if (saveResult.Refusal is { } refusal)
                {
                    ShowSettingsRefusal(refusal);
                }

                return;
            }

            PublishSettings();
            ApplySettingsToControls();
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { seeAllButton, toggle },
        };

        var rowGrid = new Grid { ColumnSpacing = 12 };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.Children.Add(iconTile);
        Grid.SetColumn(infoStack, 1);
        rowGrid.Children.Add(infoStack);
        Grid.SetColumn(actions, 2);
        rowGrid.Children.Add(actions);

        return new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            Style = (Style)Application.Current.Resources["BrandSettingsCardStyle"],
            Child = rowGrid,
            Tag = toggle,
        };
    }

    private Button BuildOfferButton(ImportConflictOffer offer)
    {
        var button = new Button { Content = offer.Label };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, offer.Label);
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
        // THE PLAN IS THE CONTROLLER'S, COMPUTED INSIDE THE GATE, and the message is chosen from
        // what came back.
        //
        // THE ITEMISED DESCRIPTION IS USED ON BOTH OUTCOMES. An earlier version fell back to the
        // generic save message the moment ONE word imported, so a hundred-line file with sixty good
        // rows and forty unreadable ones said "the change was saved locally" and the forty were
        // never mentioned.
        var outcome = await CommitVocabularyAsync(_session.VocabularyImport.ImportAsync(text)).ConfigureAwait(true);
        if (!outcome.Saved)
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

    private Task<bool> ReplaceConflictsAsync(IReadOnlyList<CustomWordEntry> replacements)
    {
        var word = replacements.Count == 1 ? "correction" : "corrections";
        return CommitVocabularyAsync(
            _session.VocabularyImport.ReplaceConflictsAsync(replacements),
            "Corrections replaced",
            $"{replacements.Count} {word} now {(replacements.Count == 1 ? "matches" : "match")} the list you imported.");
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
        if (file is null || RefuseExportIntoDataFolder(file.Path))
        {
            return;
        }

        try
        {
            // A NEW FILE PUT AT THE CHOSEN NAME, never a write through it (ExportFileWriter).
            await EnviousWispr.Services.UserData.ExportFileWriter.WriteReplacingAsync(
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

        var result = await _session.Profiles.ExportAsync(_settings.ToPortableProfile(), file.Path).ConfigureAwait(true);
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
        var result = await _session.Diagnostics.ExportAsync(
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

    public void SetStartupRegistrationInProgress()
    {
        _startupOperationGeneration++;
        _startupOperationInProgress = true;
        StartAtLoginToggle.IsEnabled = false;
        SetLiveText(StartAtLoginStatusText, "Updating Windows startup settings…");
    }

    public void SetStartupRegistration(StartupRegistrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _startupOperationGeneration++;
        _startupOperationInProgress = false;
        _isUpdatingStartupRegistration = true;
        try
        {
            StartAtLoginToggle.IsOn = result.IsEnabled;
        }
        finally
        {
            _isUpdatingStartupRegistration = false;
        }

        StartAtLoginToggle.IsEnabled = result.CanToggle;
        OpenStartupSettingsButton.Visibility = result.NeedsWindowsStartupSettings
            ? Visibility.Visible
            : Visibility.Collapsed;
        SetLiveText(
            StartAtLoginStatusText,
            result.Status switch
            {
                StartupRegistrationStatus.Enabled =>
                    "EnviousWispr starts when you sign in to Windows.",
                StartupRegistrationStatus.Disabled =>
                    "EnviousWispr starts only when you open it.",
                StartupRegistrationStatus.DisabledByUser =>
                    "Turned off in Windows Settings or Task Manager. Turn EnviousWispr on under Startup Apps in Windows Settings to start when you sign in.",
                StartupRegistrationStatus.DisabledByPolicy =>
                    "Turned off by a Windows policy on this PC.",
                StartupRegistrationStatus.EnabledByPolicy =>
                    "Turned on by a Windows policy on this PC.",
                _ => "Windows could not change the startup setting. Try again, or check Startup Apps in Windows Settings.",
            });
    }

    private void StartAtLoginToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _isUpdatingStartupRegistration)
        {
            return;
        }

        StartupToggleRequested?.Invoke(StartAtLoginToggle.IsOn);
    }

    private async void OpenStartupSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var opened = await Windows.System.Launcher.LaunchUriAsync(
                new Uri("ms-settings:startupapps"));
            if (!opened)
            {
                ShowMessage(
                    "Windows startup settings did not open",
                    "Open Windows Settings, then choose Apps > Startup.",
                    InfoBarSeverity.Warning);
            }
        }
        catch
        {
            ShowMessage(
                "Windows startup settings did not open",
                "Open Windows Settings, then choose Apps > Startup.",
                InfoBarSeverity.Warning);
        }
    }

    private void DownloadModelButton_Click(object sender, RoutedEventArgs e) =>
        ModelDownloadRequested?.Invoke();

    private void CancelModelDownloadButton_Click(object sender, RoutedEventArgs e) =>
        ModelDownloadCancelRequested?.Invoke();

    private void DownloadGraphicsRuntimeButton_Click(object sender, RoutedEventArgs e) =>
        GraphicsRuntimeDownloadRequested?.Invoke();

    private void CancelGraphicsRuntimeDownloadButton_Click(object sender, RoutedEventArgs e) =>
        GraphicsRuntimeDownloadCancelRequested?.Invoke();

    /// <summary>
    /// Shows the NVIDIA graphics runtime row on the Transcription card, or hides it when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// HIDDEN UNLESS IT APPLIES. The app decides whether this PC's card would be used; a PC without an NVIDIA
    /// card never sees the row at all, so null is the common case and it collapses the whole row.
    /// </remarks>
    public void SetGraphicsRuntimeDelivery(ModelDeliveryPresentation? presentation)
    {
        if (presentation is null)
        {
            GraphicsRuntimeRow.Visibility = Visibility.Collapsed;
            return;
        }

        GraphicsRuntimeRow.Visibility = Visibility.Visible;
        SetLiveText(GraphicsRuntimeStatusText, presentation.Text);
        GraphicsRuntimeProgress.Visibility = presentation.Percent is null ? Visibility.Collapsed : Visibility.Visible;
        GraphicsRuntimeProgress.Value = presentation.Percent ?? 0;
        DownloadGraphicsRuntimeButton.Visibility = presentation.CanDownload ? Visibility.Visible : Visibility.Collapsed;
        CancelGraphicsRuntimeDownloadButton.Visibility = presentation.CanCancel ? Visibility.Visible : Visibility.Collapsed;
    }

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
        ObserveModelForOnboarding(presentation);
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

        var imported = await _session.Profiles.ImportAsync(file.Path).ConfigureAwait(true);
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
                _deviceCatalog = _session.DeviceCatalog();
                _deviceCatalog.DevicesChanged += OnAudioDevicesChanged;
            }

            // READ BEFORE THE ENUMERATION, because the enumeration is one of the things a blocked
            // switch breaks. Reading it afterwards meant the catch threw the answer away and
            // reported the symptom instead of the cause.
            consent = WindowsMicrophoneConsent.Read();
            var devices = await _deviceCatalog.GetCaptureDevicesAsync().ConfigureAwait(true);
            _microphones = (MicrophoneChoice[])[
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
            ApplyMicrophoneReadiness(
                MicrophoneReadinessReport.For(
                    consent,
                    defaultDevice?.DisplayName),
                consent);
        }
        catch
        {
            _microphones = (MicrophoneChoice[])[new MicrophoneChoice(null, "Use the Windows default microphone")];
            MicrophoneComboBox.ItemsSource = _microphones;
            MicrophoneComboBox.SelectedIndex = 0;
            ApplyMicrophoneReadiness(
                MicrophoneReadinessReport.For(
                    consent,
                    defaultDeviceName: null,
                    enumerationFailed: true),
                consent);
        }
    }

    /// <summary>Puts one microphone verdict on both places that report it.</summary>
    /// <remarks>
    /// THE BUTTON APPEARS ONLY WHERE IT WOULD HELP. Offering "open microphone privacy settings"
    /// beside a working microphone is noise, and offering it beside a missing one sends somebody to
    /// a page that will tell them everything is fine.
    /// </remarks>
    private void ApplyMicrophoneReadiness(MicrophoneReadiness readiness, MicrophoneConsent consent)
    {
        SetLiveText(PermissionsMicrophoneText, readiness.Sentence);
        var allowed = consent == MicrophoneConsent.Allowed;
        PermissionsMicrophoneAllowedBadge.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        PermissionsMicrophoneAttentionBadge.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        ObserveMicrophoneForOnboarding(readiness, consent);
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
        BeginHistoryReload();
        var view = await _session.History.LoadAsync().ConfigureAwait(true);
        ShowHistory(view);
    }

    /// <summary>The loading transition every reload goes through, whether the page or a command asked for it.</summary>
    /// <remarks>
    /// THE SAME TRANSITION FOR A COMMAND'S RELOAD AS FOR THE PAGE'S. The rows go away and the loading
    /// card shows, which is also what resets the announcement bookkeeping - so a command that ends
    /// with the same count as before is still announced, exactly as it was when every command
    /// reloaded through the page's own path.
    /// </remarks>
    private void BeginHistoryReload()
    {
        _isHistoryLoading = true;
        UpdateHistoryListVisibility(HistorySearchBox.Text.Trim(), itemCount: 0);
    }

    /// <summary>Puts what the presenter loaded on the page: the rows, and the one line about them.</summary>
    private void ShowHistory(HistoryView view)
    {
        // A LOAD THE EXIT REFUSED OR STOPPED IS NOT DRAWN, AND NEITHER IS ONE THAT FINISHED AS THE
        // EXIT BEGAN: a load inside a store that ignores its token completes after the drain started
        // and reaches this thread with an honest answer for a page that is on its way out. The
        // loading card it already shows is the last thing drawn on it.
        if (view.Summary == HistorySummary.Closing || _session.Closing)
        {
            return;
        }

        _historyLoadStatus = view.Status;
        _history.Clear();
        _history.AddRange(view.Entries);
        _isHistoryLoading = false;
        RefreshHistoryView();
    }

    private HistoryKindFilter _historyKindFilter = HistoryKindFilter.All;

    private void HistoryFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag)
        {
            return;
        }

        _historyKindFilter = tag switch
        {
            "Dictation" => HistoryKindFilter.Dictation,
            "TranscribeFile" => HistoryKindFilter.TranscribeFile,
            _ => HistoryKindFilter.All,
        };

        UpdateHistoryFilterSegmentButtons();
        RefreshHistoryView();
    }

    private void UpdateHistoryFilterSegmentButtons()
    {
        ApplySegmentButtonState(HistoryFilterAllButton, _historyKindFilter == HistoryKindFilter.All);
        ApplySegmentButtonState(HistoryFilterDictationButton, _historyKindFilter == HistoryKindFilter.Dictation);
        ApplySegmentButtonState(HistoryFilterTranscribeButton, _historyKindFilter == HistoryKindFilter.TranscribeFile);
    }

    private void RefreshHistoryView()
    {
        var previousSelectedId = (HistoryList.SelectedItem as HistoryItemViewModel)?.Id;
        var query = HistorySearchBox.Text.Trim();
        var visibleHistory = HistoryPresenter.Filter(_history, query, _historyKindFilter)
            .Select(entry => new HistoryItemViewModel(entry))
            .ToArray();
        HistoryList.ItemsSource = visibleHistory;
        if (HistoryCountText is not null)
        {
            HistoryCountText.Text = visibleHistory.Length.ToString("N0", CultureInfo.CurrentCulture);
        }
        if (previousSelectedId is { } id)
        {
            HistoryList.SelectedItem = visibleHistory.FirstOrDefault(item => item.Id == id);
        }

        UpdateHistoryListVisibility(query, visibleHistory.Length);
        UpdateSelectionDependentButtons();
        UpdateHistoryStatusCards();
    }

    private void UpdateHistoryStatusCards()
    {
        if (HistoryEngineStatusText is not null && _settings is not null)
        {
            HistoryEngineStatusText.Text = EnginePolishPresentation
                .DescribeEngine(_settings.Preferences.Dictation.FinalEngine)
                .Name;
        }
        if (HistoryPolishStatusText is not null && _settings is not null)
        {
            var providerIndex = PolishProviderIndex(_settings.Preferences.Polish.Provider);
            HistoryPolishStatusText.Text = providerIndex > 0 && providerIndex < PolishProviderChoices.Length
                ? PolishProviderChoices[providerIndex].Name
                : "Off";
        }
        if (HistoryDetailReadyHotkeyText is not null && _settings is not null)
        {
            HistoryDetailReadyHotkeyText.Text = $"Keybind: {_settings.Preferences.Dictation.PushToTalkGesture}";
        }
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
            || PasteHistoryButton is null
            || KeepHistoryButton is null
            || DeleteHistoryButton is null
            || ClearHistoryButton is null
            || RemoveWordButton is null
            || SelectAllWordsButton is null
            || WordSelectionCountText is null
            || RemoveSnippetButton is null
            || ExportSnippetsButton is null)
        {
            return;
        }

        var historySelected = HistoryList.SelectedItem is not null;
        var activeHistoryItem = HistoryList.SelectedItem as HistoryItemViewModel;
        CopyHistoryButton.IsEnabled = historySelected && !_isRepolishing;
        PasteHistoryButton.IsEnabled = historySelected && !_isRepolishing;
        KeepHistoryButton.IsEnabled = historySelected && !_isRepolishing;
        DeleteHistoryButton.IsEnabled = historySelected && !_isRepolishing;
        ClearHistoryButton.IsEnabled = _history.Count > 0 && !_isRepolishing;
        if (RepolishHistoryButton is not null)
        {
            RepolishHistoryButton.IsEnabled = historySelected && !_isRepolishing && (activeHistoryItem is null || !activeHistoryItem.IsTemporary);
        }

        if (HistoryList.SelectedItem is HistoryItemViewModel selectedHistory)
        {
            HistoryDetailEmptyState.Visibility = Visibility.Collapsed;
            HistoryDetailContent.Visibility = Visibility.Visible;
            HistoryDetailTimestampText.Text = selectedHistory.CreatedDisplay;
            HistoryDetailMetadataText.Text = selectedHistory.Details;
            HistoryDetailTextBox.Text = selectedHistory.Text;
            KeepHistoryButton.Visibility = selectedHistory.IsTemporary ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            HistoryDetailEmptyState.Visibility = Visibility.Visible;
            HistoryDetailContent.Visibility = Visibility.Collapsed;
            HistoryDetailTimestampText.Text = string.Empty;
            HistoryDetailMetadataText.Text = string.Empty;
            HistoryDetailTextBox.Text = string.Empty;
            KeepHistoryButton.Visibility = Visibility.Collapsed;
            if (_settings is not null)
            {
                HistoryDetailReadyHotkeyText.Text = $"Keybind: {_settings.Preferences.Dictation.PushToTalkGesture}";
            }
        }

        var selectedWords = DictionaryList.SelectedItems.Count;
        RemoveWordButton.IsEnabled = selectedWords > 0;
        SelectAllWordsButton.IsEnabled = (_settings?.UserData.CustomWords.Count ?? 0) > 0;

        // THE COUNT IS SHOWN ONLY WHILE MORE THAN ONE IS CHOSEN. With one row selected the row
        // itself is the answer and a line reading "1 selected" is noise; with fifteen it is the
        // only thing that says how much the next press removes.
        SetLiveRegion(
            WordSelectionCountText,
            selectedWords > 1 ? $"{selectedWords} selected" : string.Empty,
            selectedWords > 1 ? Visibility.Visible : Visibility.Collapsed);

        // Export needs words rather than a selection - it writes the whole list.
        ExportWordsButton.IsEnabled = (_settings?.UserData.CustomWords.Count ?? 0) > 0;
        RemoveSnippetButton.IsEnabled = SnippetList.SelectedItem is not null;
        ExportSnippetsButton.IsEnabled = (_settings?.UserData.Snippets.Count ?? 0) > 0;
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
    /// <summary>The vertical card-based choice lists.</summary>
    private ItemsControl[] ChoiceLists() =>
    [
        PolishProviderComboBox,
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
    private void AutoStopToggle_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateAutoStopAvailability();
        _ = TriggerAutoSaveAsync();
    }

    private void UpdateAutoStopAvailability()
    {
        var visibility = AutoStopToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (AutoStopSecondsDivider is not null)
        {
            AutoStopSecondsDivider.Visibility = visibility;
        }

        if (AutoStopSecondsRow is not null)
        {
            AutoStopSecondsRow.Visibility = visibility;
        }

        AutoStopSecondsBox.IsEnabled = AutoStopToggle.IsOn;
    }

    /// <summary>The five shortcut fields with the roles the presenter's Save checks them under: the live warning and the refusal share one detector and one set of names.</summary>
    private (TextBox Box, string Role)[] KeybindFields()
    {
        var roles = SettingsPresenter.ShortcutRoles(
            HotkeyTextBox.Text,
            CancelHotkeyTextBox.Text,
            QuickAddHotkeyTextBox.Text,
            PasteLastHotkeyTextBox.Text,
            CopyLastHotkeyTextBox.Text);
        return
        [
            (HotkeyTextBox, roles[0].Role),
            (CancelHotkeyTextBox, roles[1].Role),
            (QuickAddHotkeyTextBox, roles[2].Role),
            (PasteLastHotkeyTextBox, roles[3].Role),
            (CopyLastHotkeyTextBox, roles[4].Role),
        ];
    }

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
        if (_isUpdatingCaptureDisplay)
        {
            return;
        }

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
            if (ReferenceEquals(field.Box, _capturingBox))
            {
                field.Box.BorderBrush = KeybindListeningProbe.Background;
            }
            else if (guilty.Contains(field.Role))
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
        HandsFreeGestureButton.Visibility = string.Equals(
            HotkeyTextBox.Text.Trim(),
            HandsFreeRecordBinding.Suggested,
            StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void HotkeyBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (_keybindCaptureSession is not null)
        {
            e.Handled = true;
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
            _keybindModifierSet |= HeldModifierSet() | ModifierFlagOf(e.Key);
            e.Handled = true;
            return;
        }

        // THE LAST-DICTATION SHORTCUTS CAN BE REMOVED, the others cannot: Backspace with nothing held empties one of
        // those two fields (Delete stays a key that can be bound), which Save stores as "no shortcut". Every other field captures the key as before.
        // Ref: #206.
        if (e.Key == VirtualKey.Back &&
            (ReferenceEquals(box, PasteLastHotkeyTextBox) || ReferenceEquals(box, CopyLastHotkeyTextBox)) &&
            !IsHeld(VirtualKey.Control) && !IsHeld(VirtualKey.Menu) && !IsHeld(VirtualKey.Shift) &&
            !IsHeld(VirtualKey.LeftWindows) && !IsHeld(VirtualKey.RightWindows))
        {
            _keybindModifierCandidate = null;
            _keybindModifierSet = HotkeyModifiers.None;
            e.Handled = true;
            box.Text = string.Empty;
            return;
        }

        // Any ordinary key means the modifier was qualifying it. The candidate is dropped here
        // rather than on the modifier's release, because by then the field already holds the
        // combination and overwriting it would undo what the user just chose.
        _keybindModifierCandidate = null;
        _keybindModifierSet = HotkeyModifiers.None;

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
        var set = _keybindModifierSet | ModifierFlagOf(e.Key);
        _keybindModifierCandidate = null;
        _keybindModifierSet = HotkeyModifiers.None;

        if (candidate is null ||
            e.Key is not (VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
                or VirtualKey.LeftWindows or VirtualKey.RightWindows))
        {
            return;
        }

        // TWO OR MORE MODIFIERS HELD TOGETHER ARE A SET, AND THE SET IS THE BINDING - Ctrl+Win, the default. This field
        // only ever offered the first modifier down, so pressing Ctrl+Win here produced "LeftCtrl": the engine could
        // run the set and nobody could choose it. Offered on the first release, while the set is still whole. Alt is
        // refused in a set as it is alone, matching the parser. Ref: #66.
        var isSet = System.Numerics.BitOperations.PopCount((uint)set) >= 2;
        if (isSet && set.HasFlag(HotkeyModifiers.Alt))
        {
            e.Handled = true;
            return;
        }

        // A LONE MODIFIER IS A RECORDING KEY'S SHAPE, not a last-dictation shortcut's: those fire once per press and
        // need an ordinary key, so the tap is not offered to their fields (Save would refuse it). Ref: #206.
        // ONLY THE RECORDING KEY MAY BE A SET: the cancel and Add-a-word keys need a key of their own, and one without
        // leaves the whole hook refused (HotkeyGestureParser.ParseKeyed). Ref: #66 review.
        if (ReferenceEquals(box, PasteLastHotkeyTextBox) ||
            ReferenceEquals(box, CopyLastHotkeyTextBox) ||
            (isSet && !ReferenceEquals(box, HotkeyTextBox)))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        box.Text = isSet
            ? new HotkeyGesture(set, string.Empty).ToString()
            : new HotkeyGesture(HotkeyModifiers.None, candidate).ToString();
        box.SelectionStart = box.Text.Length;
    }

    private void ForgetKeybindChord()
    {
        _keybindModifierCandidate = null;
        _keybindModifierSet = HotkeyModifiers.None;
    }

    /// <summary>The modifiers the keyboard state says are down right now, sides folded together.</summary>
    private static HotkeyModifiers HeldModifierSet()
    {
        var held = HotkeyModifiers.None;
        if (IsHeld(VirtualKey.Control))
        {
            held |= HotkeyModifiers.Control;
        }

        if (IsHeld(VirtualKey.Menu))
        {
            held |= HotkeyModifiers.Alt;
        }

        if (IsHeld(VirtualKey.Shift))
        {
            held |= HotkeyModifiers.Shift;
        }

        if (IsHeld(VirtualKey.LeftWindows) || IsHeld(VirtualKey.RightWindows))
        {
            held |= HotkeyModifiers.Windows;
        }

        return held;
    }

    /// <summary>The modifier a key is, for the key this event is about, whatever the state has caught up with.</summary>
    private static HotkeyModifiers ModifierFlagOf(VirtualKey key) => key switch
    {
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => HotkeyModifiers.Control,
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => HotkeyModifiers.Alt,
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => HotkeyModifiers.Shift,
        VirtualKey.LeftWindows or VirtualKey.RightWindows => HotkeyModifiers.Windows,
        _ => HotkeyModifiers.None,
    };

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

        if (IsHeld(VirtualKey.LeftMenu))
        {
            return "LeftAlt";
        }

        if (IsHeld(VirtualKey.RightMenu))
        {
            return "RightAlt";
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
    /// <summary>Takes a vocabulary change's answer onto the page: the refusal said, or the settings published and the controls refreshed.</summary>
    private async Task<SettingsSaveResult<T>> CommitVocabularyAsync<T>(Task<SettingsSaveResult<T>> change)
    {
        var result = await change.ConfigureAwait(true);
        if (!result.Saved)
        {
            ShowSettingsRefusal(result.Refusal!.Value);
            return result;
        }

        PublishSettings();
        ApplySettingsToControls();
        return result;
    }

    /// <summary>The same, for a change with nothing to say beyond whether it worked, with the message a person sees.</summary>
    private async Task<bool> CommitVocabularyAsync(
        Task<SettingsSaveResult> change,
        string title,
        string message = "The change was saved locally.")
    {
        var result = await change.ConfigureAwait(true);
        if (!result.Saved)
        {
            ShowSettingsRefusal(result.Refusal!.Value);
            return false;
        }

        PublishSettings();
        ApplySettingsToControls();
        ShowMessage(title, message, InfoBarSeverity.Success);
        RefreshReusableUserDataViews();
        return true;
    }

    /// <summary>Says why a settings write did not happen, in the words for that cause.</summary>
    /// <remarks>
    /// THE CAUSE IS THE PRESENTER'S TO NAME AND THIS WINDOW'S TO SAY. Closing says nothing: a click
    /// that lands as the window is going away is refused on purpose, and telling somebody their
    /// settings storage broke as they quit is both alarming and untrue.
    /// </remarks>
    private void ShowSettingsRefusal(SettingsSaveRefusal refusal)
    {
        switch (refusal)
        {
            case SettingsSaveRefusal.Closing:
                return;
            case SettingsSaveRefusal.InvalidValues:
                ShowMessage(
                    "Settings were not saved",
                    "One or more values are invalid. Your previous settings remain active.",
                    InfoBarSeverity.Error);
                return;
            case SettingsSaveRefusal.StorageBlocked:
                ShowMessage("Windows blocked settings storage", "Your previous settings remain active.", InfoBarSeverity.Error);
                return;
            default:
                ShowMessage("Settings storage is unavailable", "Your previous settings remain active.", InfoBarSeverity.Error);
                return;
        }
    }

    /// <summary>Takes the presenter's settings as this window's, and tells the app.</summary>
    private void PublishSettings()
    {
        _settings = _session.Settings.Current;
        _overlayWindow.ApplyPreferences(
            _settings.Preferences.OverlayPosition,
            _settings.Preferences.LivePreviewEnabled,
            _settings.Preferences.PillDesignWithoutWords,
            _settings.Preferences.PillDesignWithWords);
        SettingsChanged?.Invoke(_settings);
        SyncAppliedWhisperLanguageStatus(_settings.Preferences.Dictation.WhisperLanguage);
    }

    private void SyncAppliedWhisperLanguageStatus(WhisperLanguagePreference language)
    {
        var index = (int)language;
        var appliedLabel = index >= 0 && index < WhisperLanguageComboBox.Items.Count &&
            WhisperLanguageComboBox.Items[index] is string label
                ? label
                : language.ToString();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(WhisperLanguageComboBox, appliedLabel);
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
        var snippets = _settings.UserData.Snippets;
        RefreshDictionaryView();
        RefreshVocabularyPacksPanel();
        RefreshSnippetKeywordViews(force: false);
        RefreshSnippetList(snippets);
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

    /// <summary>A quiet write: applied through the presenter, published on success, silent on refusal.</summary>
    /// <remarks>
    /// THE TRANSACTION IS THE PRESENTER'S. One at a time, each change derived from what is actually
    /// stored, a transform rather than a record - all of that is SettingsPresenter and its tests.
    /// This is the window's side: the settings it holds and the app it tells.
    /// </remarks>
    private async Task<bool> UpdateSettingsAsync(Func<AppSettings, AppSettings> change)
    {
        var result = await _session.Settings.SaveAsync(change).ConfigureAwait(true);
        if (!result.Saved)
        {
            return false;
        }

        PublishSettings();
        return true;
    }

    /// <summary>A write the user asked for: applied through the presenter, and answered either way.</summary>
    private async Task<bool> TrySaveAsync(
        Func<AppSettings, AppSettings> change,
        string title,
        string message)
    {
        var result = await _session.Settings.SaveAsync(change).ConfigureAwait(true);
        if (!result.Saved)
        {
            ShowSettingsRefusal(result.Refusal!.Value);
            return false;
        }

        PublishSettings();
        ApplySettingsToControls();
        ShowMessage(title, message, InfoBarSeverity.Success);
        return true;
    }

    private void ApplySettingsToControls()
    {
        _isApplyingSettings = true;
        try
        {
            var preferences = _settings.Preferences;
            _lastActiveEngine = preferences.Dictation.FinalEngine;
            SetSelectedFinalEngineCards(preferences.Dictation.FinalEngine);
            UpdateActiveEngineDisplay(preferences.Dictation.FinalEngine);
            WhisperLanguageComboBox.SelectedIndex = (int)preferences.Dictation.WhisperLanguage;
            SyncAppliedWhisperLanguageStatus(preferences.Dictation.WhisperLanguage);
            EnglishSpellingComboBox.SelectedIndex = (int)preferences.Dictation.EnglishSpelling;
            HotkeyTextBox.Text = preferences.Dictation.PushToTalkGesture;
            SetRecordingMode((int)preferences.Dictation.RecordingMode, triggerSave: false);
            CancelHotkeyTextBox.Text = preferences.Dictation.CancelGesture;
            EscapeRecoveryToggle.IsOn = preferences.Dictation.EscapeRecoveryEnabled;
            QuickAddHotkeyTextBox.Text = preferences.Dictation.QuickAddGesture;
            UpdateQuickAddShortcutBadge();
            PasteLastHotkeyTextBox.Text = preferences.Dictation.PasteLastGesture;
            CopyLastHotkeyTextBox.Text = preferences.Dictation.CopyLastGesture;
            WordCorrectionToggle.IsOn = preferences.Dictation.WordCorrectionEnabled;
            DictionaryEnabledToggle.IsOn = preferences.Dictation.WordCorrectionEnabled;
            FillerRemovalToggle.IsOn = preferences.Dictation.FillerRemovalEnabled;
            EmojiFormatterToggle.IsOn = preferences.Dictation.EmojiFormatterEnabled;
            SpokenPunctuationToggle.IsOn = preferences.Dictation.SpokenPunctuationEnabled;
            if (CasualModeToggle != null)
            {
                CasualModeToggle.IsOn = preferences.Dictation.CasualModeEnabled;
            }
            AutoStopToggle.IsOn = preferences.Dictation.AutoStopEnabled;
            AutoStopSecondsBox.Value = preferences.Dictation.AutoStopSilenceSeconds;
            UpdateAutoStopAvailability();
            if (preferences.Polish.Provider != PolishProvider.None)
            {
                _lastNonNonePolishProvider = preferences.Polish.Provider;
            }
            var persistedEgOneId = preferences.Polish.EgOneModelId;
            if (!EnginePolishPresentation.IsKnownEgOneModelId(persistedEgOneId) &&
                preferences.Polish.Provider == PolishProvider.EgOne)
            {
                persistedEgOneId = preferences.Polish.ModelId;
            }
            _selectedEgOneModelId = EnginePolishPresentation.NormalizeOptionalEgOneModelId(persistedEgOneId);
            _selectedPolishModelId = _polishModelSetByApp = preferences.Polish.Provider == PolishProvider.EgOne
                ? EnginePolishPresentation.NormalizeEgOneModelId(_selectedEgOneModelId)
                : preferences.Polish.ModelId ?? string.Empty;
            _lastActivePolishProvider = preferences.Polish.Provider;
            SelectChoice(PolishProviderChoices, PolishProviderIndex(preferences.Polish.Provider));
            UpdateActivePolishProviderDisplay();
            OllamaEndpointTextBox.Text = preferences.Polish.OllamaEndpoint ?? string.Empty;
            HistoryEnabledToggle.IsOn = preferences.History.IsEnabled;
            RetentionDaysBox.Value = preferences.History.RetentionDays;
            SetThemeSelection(ThemeIndex(preferences.Theme), triggerSave: false);
            LivePreviewToggle.IsOn = preferences.LivePreviewEnabled;
            LivePreviewParakeetEngineCard.IsChecked = preferences.LivePreviewEngine == LivePreviewEngineChoice.Parakeet;
            LivePreviewWhisperEngineCard.IsChecked = preferences.LivePreviewEngine == LivePreviewEngineChoice.Whisper;
            RefreshLivePreviewSectionPresentation();
            SetOverlayPositionSelection(OverlayPositionIndex(preferences.OverlayPosition), triggerSave: false);
            _lastWithoutWordsPillDesign = preferences.PillDesignWithoutWords == RecordingPillDesign.LevelRail
                ? RecordingPillDesign.LevelRail
                : RecordingPillDesign.Classic;
            PlayRecordingSoundsToggle.IsOn = preferences.PlayRecordingSounds;
            CopyInsteadOfPasteToggle.IsOn = preferences.CopyInsteadOfPaste;
            SelectChimePairing(preferences.RecordingSoundPairing, triggerSave: false);
            UpdatePillDesignControls();
            _overlayWindow.ApplyPreferences(
                preferences.OverlayPosition,
                preferences.LivePreviewEnabled,
                preferences.PillDesignWithoutWords,
                preferences.PillDesignWithWords);
            var observability = _settings.Observability ?? ObservabilityPreferences.Default;
            LocalDiagnosticsToggle.IsOn = observability.LocalDiagnosticsEnabled;
            DiagnosticRetentionDaysBox.Value = observability.DiagnosticRetentionDays;
            ShareUsageMetricsToggle.IsOn = observability.ShareAnonymousTelemetry;
            SendCrashReportsToggle.IsOn = observability.SendCrashReports;
            UpdateDiagnosticsStatusText(observability.SendCrashReports);
            RefreshReusableUserDataViews();
            UpdateHistoryStatusCards();
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

    private void UpdateDiagnosticsStatusText(bool storedSendCrashReports)
    {
        SetLiveText(
            DiagnosticsStatusText,
            storedSendCrashReports != _launchedCrashReports
                ? "Takes effect when EnviousWispr restarts"
                : "We never collect your audio, dictated or transcribed text, history, snippets, dictionary words, API keys, or any text on your screen.");
    }

    /// <summary>
    /// What the app itself last wrote into the model field: loaded, chosen as a default, or repaired. The field still
    /// reading it means the person has not edited it, which is what lets a repair replace it. Ref: #213 review.
    /// </summary>
    private string _polishModelSetByApp = string.Empty;

    /// <param name="ollamaChange">A download or removal just made on the Ollama models block, to repair the field against; null otherwise.</param>
    /// <param name="silentPoll">True when triggered by background live polling or startup detection so the Ollama models block is not re-fetched.</param>
    private async Task RefreshPolishModelChoicesAsync(
        PolishProvider provider,
        bool chooseDefault,
        OllamaModelChange? ollamaChange = null,
        bool silentPoll = false)
    {
        if (ollamaChange is null && !silentPoll)
        {
            ShowOllamaModels(provider);
        }

        var isCloudProvider = IsCloudProvider(provider);
        var hasSavedCloudKey = !isCloudProvider || _session.Provider.KeyStatus(provider) == ApiKeyReadStatus.Found;
        var usesAModel = ProviderSettingsPresenter.UsesAModel(provider);
        var showModelPicker = usesAModel && hasSavedCloudKey;
        PolishModelPickerRow.Visibility = showModelPicker ? Visibility.Visible : Visibility.Collapsed;
        CloudModelsKeyRequiredRow.Visibility = isCloudProvider && !hasSavedCloudKey
            ? Visibility.Visible
            : Visibility.Collapsed;
        OllamaEndpointTextBoxRow.Visibility = provider == PolishProvider.Ollama
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApiKeyPasswordBoxRow.Visibility = isCloudProvider
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApiKeyButtonPanel.Visibility = isCloudProvider
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshPolishModelsButtonRow.Visibility = provider == PolishProvider.Ollama ||
            (isCloudProvider && hasSavedCloudKey)
                ? Visibility.Visible
                : Visibility.Collapsed;
        RefreshPolishModelsButton.IsEnabled = false;

        if (isCloudProvider && !hasSavedCloudKey)
        {
            _isUpdatingPolishModelPicker = true;
            try
            {
                PolishModelPicker.ItemsSource = Array.Empty<string>();
                PolishModelPicker.SelectedIndex = -1;
                PolishModelPicker.PlaceholderText = "No models listed";
                PolishModelPicker.IsEnabled = false;
            }
            finally
            {
                _isUpdatingPolishModelPicker = false;
            }
        }

        // THE ENDPOINT AND SELECTION ARE READ HERE, ON THE UI THREAD, BEFORE THE DISCOVERY;
        // if another refresh overtakes this one, or if the person changes provider/selection meanwhile,
        // no stale listing overwrites what they chose last.
        var endpoint = NullIfBlank(OllamaEndpointTextBox.Text);
        var selectionBefore = _selectedPolishModelId;
        var listing = await _session.Provider.ListModelsAsync(provider, endpoint)
            .ConfigureAwait(true);
        if (listing is null || _session.Closing || !_session.Provider.IsCurrent(listing.Ticket))
        {
            return;
        }

        if (PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) != provider ||
            (provider == PolishProvider.Ollama &&
             !string.Equals(NullIfBlank(OllamaEndpointTextBox.Text), endpoint, StringComparison.Ordinal)))
        {
            return;
        }

        if (isCloudProvider && _session.Provider.KeyStatus(provider) != ApiKeyReadStatus.Found)
        {
            RefreshApiKeyStatus();
            return;
        }

        var selectedMeanwhile = !string.Equals(_selectedPolishModelId, selectionBefore, StringComparison.Ordinal);
        var choices = _session.Provider.Choose(listing, _selectedPolishModelId, chooseDefault && !selectedMeanwhile);

        var providerUsesAModel = ProviderSettingsPresenter.UsesAModel(provider);
        var pickerReady = providerUsesAModel && (!isCloudProvider || _session.Provider.KeyStatus(provider) == ApiKeyReadStatus.Found);
        var displayedChoices = pickerReady
            ? ProviderSettingsPresenter.IncludeActiveSelectionIfMissing(choices, _selectedPolishModelId)
            : choices;
        RefreshPolishModelsButton.IsEnabled = pickerReady;
        _isUpdatingPolishModelPicker = true;
        try
        {
            PolishModelPicker.ItemsSource = displayedChoices.Models;
            PolishModelPicker.PlaceholderText = displayedChoices.Models.Count > 0
                ? "Choose an available model"
                : "No models listed";
            PolishModelPicker.IsEnabled = pickerReady && displayedChoices.Models.Count > 0;
            if (displayedChoices.ModelToApply is { } model)
            {
                _selectedPolishModelId = _polishModelSetByApp = model;
            }

            PolishModelPicker.SelectedIndex = displayedChoices.SelectedIndex;

            if (ollamaChange is not null &&
                !selectedMeanwhile &&
                displayedChoices.Discovery is { Status: PolishModelDiscoveryStatus.Ready } &&
                OllamaModelsPresenter.RepairSelection(
                    displayedChoices.Models,
                    _selectedPolishModelId,
                    _polishModelSetByApp,
                    ollamaChange) is { } repaired)
            {
                _selectedPolishModelId = _polishModelSetByApp = repaired;
                var repairedIndex = -1;
                for (var i = 0; i < displayedChoices.Models.Count && repairedIndex < 0; i++)
                {
                    repairedIndex = OllamaModelCatalog.SameModel(displayedChoices.Models[i], repaired) ? i : -1;
                }

                PolishModelPicker.SelectedIndex = repairedIndex;
            }
        }
        finally
        {
            _isUpdatingPolishModelPicker = false;
        }

        if (choices.Discovery is { } discovery)
        {
            SetLiveText(ApiKeyStatusText, ModelDiscoveryNotice(provider, discovery));
        }
    }

    /// <summary>The words for what a model discovery came back with.</summary>
    private static string ModelDiscoveryNotice(PolishProvider provider, PolishModelDiscovery discovery)
    {
        var count = discovery.ModelIds.Count;
        var plural = count == 1 ? string.Empty : "s";
        return discovery.Status switch
        {
            PolishModelDiscoveryStatus.Ready when provider == PolishProvider.Ollama =>
                $"{count.ToString(CultureInfo.CurrentCulture)} Ollama model{plural} available. Local models stay on this PC; hosted models run on Ollama's servers.",
            PolishModelDiscoveryStatus.OllamaNotReady =>
                "Ollama is not ready at this endpoint. Start Ollama or enter another loopback endpoint.",
            PolishModelDiscoveryStatus.Ready when count > 0 =>
                $"{count.ToString(CultureInfo.CurrentCulture)} compatible {ProviderDisplayName(provider)} model{plural} available to the stored key. No transcript or generation request was sent.",
            PolishModelDiscoveryStatus.Ready =>
                $"{ProviderDisplayName(provider)} returned no compatible transcript-polish models.",
            PolishModelDiscoveryStatus.MissingCredential =>
                "Save your API key to load available models.",
            PolishModelDiscoveryStatus.CredentialUnavailable =>
                "Windows Credential Manager could not provide the stored key. No provider request was sent.",
            PolishModelDiscoveryStatus.KeyRejected =>
                $"{ProviderDisplayName(provider)} rejected the stored key while listing models. Replace the key and try again.",
            PolishModelDiscoveryStatus.ProviderUnavailable =>
                $"{ProviderDisplayName(provider)} model discovery is temporarily unavailable. Check your connection and try again.",
            PolishModelDiscoveryStatus.InvalidResponse =>
                $"{ProviderDisplayName(provider)} returned an unrecognized model catalog. Try refreshing again.",
            _ => throw new ArgumentOutOfRangeException(nameof(discovery), discovery.Status, null),
        };
    }

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
                    PolishProvider.Ollama => "Ollama runs on this PC and does not use a direct cloud API key here. Local models stay on this PC; hosted models run on Ollama's servers.",
                    PolishProvider.EgOne => "EG-1 runs on this PC and does not use a cloud API key.",
                    _ => "AI polish is off; no provider key is used.",
            
                });
            return;
        }

        SetLiveText(
            ApiKeyStatusText,
    _session.Provider.KeyStatus(provider) switch
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
            WhatsNewToolbarButton,
            unread ? "New release notes you have not read" : string.Empty);

        // ItemStatus IS READ WHEN SOMEBODY REACHES THE ITEM, WHICH IS NOT THE SAME AS BEING TOLD.
        // It is the right place for the status - Microsoft's own guidance, and what has news is the
        // destination rather than the dot - but on its own the mark is silent to anyone whose focus
        // is elsewhere, which is everyone at the moment it appears. One notification says it once.
        //
        // CurrentThenMostRecent, so a mark that appears and clears quickly does not queue two
        // sentences that arrive after both are out of date.
        var peer = FrameworkElementAutomationPeer.FromElement(WhatsNewToolbarButton)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(WhatsNewToolbarButton);
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

    private bool _windowFitsOnboarding;

    private void ShowOnboarding(bool show)
    {
        OnboardingView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ProductNavigation.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show)
        {
            // EVERY SHOWING IS A NEW VISIT FROM THE WELCOME: a first run, or "Run setup again" on Diagnostics.
            BeginOnboardingVisit();
            FinishOnboardingButton.Focus(FocusState.Programmatic);
            OnboardingView.LayoutUpdated += FitWindowToOnboardingOnce;
        }
        else
        {
            StopOnboardingTimers();
        }

        if (!show && _windowFitsOnboarding)
        {
            // THE RESIZE IS DELIBERATE AND HAPPENS ONCE, when setup finishes and the navigation
            // list the default height was measured for comes on screen. Leaving the window at
            // the onboarding height would put that list back behind a scrollbar.
            _windowFitsOnboarding = false;
            ResizeToDefault();
        }
    }

    /// <summary>
    /// Fits the first-run window to the onboarding content, measured on the live layout.
    /// </summary>
    /// <remarks>
    /// The default height is measured for the navigation list, and on first run that list is not
    /// on screen - so the window opened 171 layout pixels taller than the only thing it showed,
    /// ending in a blank band below the last card. Ref: #83.
    ///
    /// NOTHING HERE IS DERIVED. The chrome is whatever the client area holds that is not the
    /// onboarding view, read from the live window; the content is the scroll content's own laid-out
    /// height, which a ScrollViewer measures unconstrained, so it is the natural height whatever the
    /// viewport; the footer is its own laid-out height. Every previous number in this file that was
    /// derived from arithmetic was wrong, which is why this one reads the layout instead. It runs
    /// once, on the first layout pass where both parts have a height, and clamps to the work area
    /// the way the default does, so a short display still opens to fit and scrolls.
    /// </remarks>
    private void FitWindowToOnboardingOnce(object? sender, object e)
    {
        if (OnboardingContent.DesiredSize.Height <= 0 || OnboardingFooter.DesiredSize.Height <= 0 || OnboardingView.ActualHeight <= 0)
        {
            return;
        }

        OnboardingView.LayoutUpdated -= FitWindowToOnboardingOnce;
        var scale = DisplayScale();
        var clientDips = AppWindow.ClientSize.Height / scale;
        var chromeDips = Math.Max(0, clientDips - OnboardingView.ActualHeight);
        var wantedClientDips = chromeDips + OnboardingContent.DesiredSize.Height + OnboardingFooter.DesiredSize.Height;
        var nonClientPixels = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        var height = wantedClientDips * scale + nonClientPixels;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        if (workArea is { } area && area.Height > 0)
        {
            height = Math.Min(height, area.Height * 0.94);
        }

        _windowFitsOnboarding = true;
        AppWindow.Resize(new SizeInt32(AppWindow.Size.Width, (int)Math.Round(height)));
    }

    private void ShowPage(string tag) => ShowPage(tag, null);

    private void ShowPage(string tag, string? tabKey)
    {
        OperationInfoBar.IsOpen = false;

        var (resolvedTag, resolvedTab) = ResolvePageAndTab(tag);
        var targetTab = tabKey ?? resolvedTab;

        var isHistoryPage = resolvedTag == "history";
        MainContentGrid.Padding = isHistoryPage
            ? new Thickness(0)
            : new Thickness(28, 18, 28, 32);
        OperationInfoBar.Margin = isHistoryPage
            ? new Thickness(16, 12, 16, 8)
            : new Thickness(0, 0, 0, 12);
        DeletionLeftoverInfoBar.Margin = isHistoryPage
            ? new Thickness(16, 8, 16, 12)
            : new Thickness(0, 12, 0, 0);

        var settingsPage = resolvedTag.StartsWith("settings-", StringComparison.Ordinal);
        var helpPage = resolvedTag == "help" || resolvedTag.StartsWith("help-", StringComparison.Ordinal);
        HistoryPage.Visibility = isHistoryPage ? Visibility.Visible : Visibility.Collapsed;
        if (resolvedTag == "history")
        {
            AnnounceHistoryOnPageShown();
        }
        WhatsNewPage.Visibility = resolvedTag == "whats-new" ? Visibility.Visible : Visibility.Collapsed;
        if (resolvedTag == "whats-new")
        {
            _ = MarkReleaseNotesSeenAsync();
        }
        DictionaryPage.Visibility = resolvedTag == "dictionary" ? Visibility.Visible : Visibility.Collapsed;
        if (resolvedTag == "dictionary" && !string.IsNullOrWhiteSpace(targetTab))
        {
            SelectDictionaryTab(targetTab);
        }
        SnippetsPage.Visibility = resolvedTag == "snippets" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = settingsPage ? Visibility.Visible : Visibility.Collapsed;
        HelpPage.Visibility = helpPage ? Visibility.Visible : Visibility.Collapsed;

        if (settingsPage)
        {
            ConfigureSettingsPage(resolvedTag, targetTab);
        }
        else if (helpPage)
        {
            ConfigureHelpPage(resolvedTag);
            ScrollPageToTop(HelpPage);
        }

        if (SettingsPage.Visibility != Visibility.Visible || KeybindsSection.Visibility != Visibility.Visible)
        {
            CancelKeybindCapture();
        }

        foreach (var page in Pages())
        {
            if (page.Visibility == Visibility.Visible)
            {
                PlayPageEntrance(page);
                break;
            }
        }

        UpdateOllamaLivePollTimer();
        if (resolvedTag == "settings-ai-polish" &&
            PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) == PolishProvider.Ollama)
        {
            _ = PollOllamaModelsSilentlyAsync();
        }
    }

    /// <summary>Every page the sidebar can reach, in the order they are declared.</summary>
    private FrameworkElement[] Pages() =>
    [
        HistoryPage,
        WhatsNewPage,
        DictionaryPage,
        SnippetsPage,
        SettingsPage,
        HelpPage,
    ];

    /// <summary>The glyph a page header shows, read from that page's own sidebar row.</summary>
    private string NavigationGlyphFor(string tag)
    {
        var (pageTag, _) = ResolvePageAndTab(tag);
        foreach (var row in NavigationRows())
        {
            if (string.Equals(row.Tag as string, pageTag, StringComparison.Ordinal) && row.Icon is FontIcon icon)
            {
                return icon.Glyph;
            }
        }

        return string.Empty;
    }

    /// <summary>Every sidebar row, top list and footer list together.</summary>
    private IEnumerable<NavigationViewItem> NavigationRows() =>
        ProductNavigation.MenuItems
            .Concat(ProductNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>();

    private void ConfigureSettingsPage(string tag) => ConfigureSettingsPage(tag, null);

    private void ConfigureSettingsPage(string tag, string? tabKey)
    {
        if (tag == "settings-dictation")
        {
            DictationTabStrip.Visibility = Visibility.Visible;
            AppTabStrip.Visibility = Visibility.Collapsed;
            SelectDictationTab(tabKey ?? "engine");
            return;
        }

        if (tag == "settings-app")
        {
            DictationTabStrip.Visibility = Visibility.Collapsed;
            AppTabStrip.Visibility = Visibility.Visible;
            SelectAppTab(tabKey ?? "appearance");
            return;
        }

        DictationTabStrip.Visibility = Visibility.Collapsed;
        AppTabStrip.Visibility = Visibility.Collapsed;

        var (title, description, section) = tag switch
        {
            "settings-dictation" => (
                "Dictation",
                "The speech engine and input devices that turn your voice into text.",
                (FrameworkElement?)TranscriptionEngineSection),
            "settings-app" => (
                "App Settings",
                "Appearance, theme, permissions, and app preferences.",
                (FrameworkElement?)AppearanceSection),
            "settings-keybinds" => (
                "Keybinds",
                "Set the keybinds that start, stop, and cancel dictation.",
                (FrameworkElement?)KeybindsSection),
            "settings-transcribe-file" => (
                "Transcribe a File",
                "Turn a recording into text, on this PC, with the same engine and clean-up as your dictation.",
                (FrameworkElement?)TranscribeFileSection),
            "settings-ai-polish" => (
                "AI Polish",
                "Clean up and rewrite your dictation with AI.",
                (FrameworkElement?)AiPolishSection),
            "settings-diagnostics" => (
                "Diagnostics",
                "What EnviousWispr records about how it is running, and what it never records.",
                (FrameworkElement?)DiagnosticsSection),
            _ => (
                "App Settings",
                "Appearance, theme, permissions, and app preferences.",
                (FrameworkElement?)AppearanceSection),
        };

        FrozenPerRecordingBanner.Visibility =
            tag is "settings-keybinds" or "settings-live-preview" or "settings-clipboard"
            ? Visibility.Visible
            : Visibility.Collapsed;
        FrozenPerRecordingText.Text = FrozenPerRecordingCopy;

        SettingsPageTitle.Text = title;
        SettingsPageDescription.Text = description;

        var showTranscriptionCompanion = tag == "settings-transcription";
        var showBackupCompanion = tag == "settings-profile";
        foreach (var candidate in SettingsSections())
        {
            candidate.Visibility =
                ReferenceEquals(candidate, section) ||
                (showTranscriptionCompanion && ReferenceEquals(candidate, DeterministicCleanupSection)) ||
                (showBackupCompanion && ReferenceEquals(candidate, YourDataSection))
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        var visible = SettingsSections()
            .Where(candidate => candidate.Visibility == Visibility.Visible)
            .ToArray();
        CollapseEyebrowThatRepeatsTheTitle(SettingsSections(), visible, title);

        ScrollPageToTop(SettingsPage);
    }

    private void DictationTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            SelectDictationTab(tag);
        }
    }

    private void SelectDictationTab(string tabKey)
    {
        DictationTabEngine.Style = (Style)Application.Current.Resources[tabKey == "engine" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        DictationTabMicrophone.Style = (Style)Application.Current.Resources[tabKey == "microphone" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        DictationTabLivePreview.Style = (Style)Application.Current.Resources[tabKey == "live-preview" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        DictationTabPill.Style = (Style)Application.Current.Resources[tabKey == "pill" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        DictationTabSounds.Style = (Style)Application.Current.Resources[tabKey == "sounds" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        DictationTabClipboard.Style = (Style)Application.Current.Resources[tabKey == "clipboard" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];

        ShowSettingsSectionForDictationTab(tabKey);
    }

    private void ShowSettingsSectionForDictationTab(string tabKey)
    {
        var (title, description, section, companion) = tabKey switch
        {
            "microphone" => ("Microphone", "Choose your input source and readiness behavior.", (FrameworkElement?)MicrophoneSection, (FrameworkElement?)null),
            "live-preview" => ("Live Preview", "See your words on screen while you are still speaking, and choose how the recording pill looks.", (FrameworkElement?)LivePreviewSection, (FrameworkElement?)null),
            "pill" => ("Recording Pill", "Choose where and how the recording pill appears while you dictate.", (FrameworkElement?)RecordingPillSection, (FrameworkElement?)null),
            "sounds" => ("Sounds", "Play a short sound when recording starts and stops.", (FrameworkElement?)SoundSection, (FrameworkElement?)null),
            "clipboard" => ("Clipboard", "How your transcript reaches the clipboard and the app you're in.", (FrameworkElement?)ClipboardSection, (FrameworkElement?)null),
            _ => ("Dictation Settings", "The speech engine that turns your voice into text.", (FrameworkElement?)TranscriptionEngineSection, (FrameworkElement?)DeterministicCleanupSection),
        };

        SettingsPageTitle.Text = title;
        SettingsPageDescription.Text = description;

        FrozenPerRecordingBanner.Visibility = tabKey is "live-preview" or "clipboard" ? Visibility.Visible : Visibility.Collapsed;
        FrozenPerRecordingText.Text = FrozenPerRecordingCopy;

        foreach (var candidate in SettingsSections())
        {
            candidate.Visibility = ReferenceEquals(candidate, section) || ReferenceEquals(candidate, companion)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        var visible = SettingsSections().Where(candidate => candidate.Visibility == Visibility.Visible).ToArray();
        CollapseEyebrowThatRepeatsTheTitle(SettingsSections(), visible, title);
        ScrollPageToTop(SettingsPage);
    }

    private void AppTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            SelectAppTab(tag);
        }
    }

    private void SelectAppTab(string tabKey)
    {
        AppTabAppearance.Style = (Style)Application.Current.Resources[tabKey == "appearance" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        AppTabPermissions.Style = (Style)Application.Current.Resources[tabKey == "permissions" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        AppTabPrivacy.Style = (Style)Application.Current.Resources[tabKey == "privacy" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        AppTabBackup.Style = (Style)Application.Current.Resources[tabKey == "backup" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        AppTabUpdatesLicenses.Style = (Style)Application.Current.Resources[tabKey == "updates-licenses" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];

        ShowSettingsSectionForAppTab(tabKey);
    }

    private void ShowSettingsSectionForAppTab(string tabKey)
    {
        var (title, description, section, companion) = tabKey switch
        {
            "permissions" => ("Permissions and privacy", "Audio, local models, dictionaries, snippets, and history stay on this PC.", (FrameworkElement?)PermissionsSection, (FrameworkElement?)null),
            "privacy" => ("Dictation history", "Whether your dictations are saved on this PC, and for how long.", (FrameworkElement?)HistorySettingsSection, (FrameworkElement?)null),
            "backup" => ("Backup", "Move your settings, words, and snippets to another PC, or keep a copy of everything and remove it all.", (FrameworkElement?)PortableProfileSection, (FrameworkElement?)YourDataSection),
            "updates-licenses" => ("Updates & Licenses", "App updates and open source notices.", (FrameworkElement?)UpdatesSection, (FrameworkElement?)LicensesSection),
            _ => ("Appearance", "Choose how EnviousWispr looks and whether it starts when you sign in.", (FrameworkElement?)AppearanceSection, (FrameworkElement?)null),
        };

        SettingsPageTitle.Text = title;
        SettingsPageDescription.Text = description;

        FrozenPerRecordingBanner.Visibility = Visibility.Collapsed;

        foreach (var candidate in SettingsSections())
        {
            candidate.Visibility = ReferenceEquals(candidate, section) || ReferenceEquals(candidate, companion)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        var visible = SettingsSections().Where(candidate => candidate.Visibility == Visibility.Visible).ToArray();
        CollapseEyebrowThatRepeatsTheTitle(SettingsSections(), visible, title);
        ScrollPageToTop(SettingsPage);
    }

    private void DictionaryTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            SelectDictionaryTab(tag);
        }
    }

    private void SelectDictionaryTab(string tabKey)
    {
        DictTabYourWords.Style = (Style)Application.Current.Resources[tabKey == "your-words" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        DictTabPacks.Style = (Style)Application.Current.Resources[tabKey == "packs" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        DictTabLearnFrom.Style = (Style)Application.Current.Resources[tabKey == "learn-from" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        DictTabQuickAdd.Style = (Style)Application.Current.Resources[tabKey == "quick-add" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];

        DictPanelYourWords.Visibility = tabKey == "your-words" ? Visibility.Visible : Visibility.Collapsed;
        DictPanelPacks.Visibility = tabKey == "packs" ? Visibility.Visible : Visibility.Collapsed;
        DictPanelLearnFrom.Visibility = tabKey == "learn-from" ? Visibility.Visible : Visibility.Collapsed;
        DictPanelQuickAdd.Visibility = tabKey == "quick-add" ? Visibility.Visible : Visibility.Collapsed;
    }

    private string _wordFilterCategory = "all";
    private bool _isDictionaryMassEdit;

    private void WordFilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _wordFilterCategory = tag;
            var active = (Style)Application.Current.Resources["BrandTabStripActiveButtonStyle"];
            var normal = (Style)Application.Current.Resources["BrandTabStripButtonStyle"];
            WordFilterAllChip.Style = tag == "all" ? active : normal;
            WordFilterGeneralChip.Style = tag == "general" ? active : normal;
            WordFilterPersonChip.Style = tag == "person" ? active : normal;
            WordFilterBrandChip.Style = tag == "brand" ? active : normal;
            WordFilterAcronymChip.Style = tag == "acronym" ? active : normal;
            WordFilterDomainChip.Style = tag == "domain" ? active : normal;
            RefreshDictionaryView();
        }
    }

    private void MassEditWordsToggle_Click(object sender, RoutedEventArgs e)
    {
        _isDictionaryMassEdit = !_isDictionaryMassEdit;
        DictionaryList.SelectionMode = _isDictionaryMassEdit
            ? ListViewSelectionMode.Multiple
            : ListViewSelectionMode.None;
        DictionaryList.SelectedItems.Clear();
        DictionaryMassEditBar.Visibility = _isDictionaryMassEdit ? Visibility.Visible : Visibility.Collapsed;
        MassEditWordsToggle.Style = (Style)Application.Current.Resources[
            _isDictionaryMassEdit ? "BrandTabStripActiveButtonStyle" : "BrandQuietButtonStyle"];
        UpdateSelectionDependentButtons();
    }

    private void DictionarySearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshDictionaryView();

    private void RefreshDictionaryView()
    {
        var allWords = _settings?.UserData?.CustomWords ?? (IReadOnlyList<CustomWordEntry>)Array.Empty<CustomWordEntry>();
        var query = DictionarySearchBox?.Text?.Trim() ?? string.Empty;
        var list = VocabularyPresenter.FilterWords(allWords, _wordFilterCategory, query)
            .Select(entry => new DictionaryWordRow(entry))
            .ToArray();

        if (DictionaryList is not null)
        {
            DictionaryList.ItemsSource = list;
            UpdateListAndEmptyStateVisibility(DictionaryList, DictionaryEmptyState, list.Length);
        }
        if (WordCountText is not null)
        {
            var total = allWords.Count;
            var wordUnit = total == 1 ? "WORD" : "WORDS";
            WordCountText.Text = list.Length == total
                ? $"{total} {wordUnit}"
                : $"Showing {list.Length} of {total} {wordUnit}";
        }
        UpdateSelectionDependentButtons();
    }

    private async void DictionaryEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _settings is null || WordCorrectionToggle is null)
        {
            return;
        }

        var isEnabled = DictionaryEnabledToggle.IsOn;
        WordCorrectionToggle.IsOn = isEnabled;
        await TriggerAutoSaveAsync().ConfigureAwait(true);
    }

    private void ConfigureHelpPage(string tag)
    {
        var (title, description) = tag switch
        {
            "help" => (
                "Help and privacy",
                "Find keyboard guidance, privacy details, updates, and licenses."),
            _ => (
                "Help and privacy",
                "Find keyboard guidance, privacy details, updates, and licenses."),
        };

        HelpPageTitle.Text = title;
        HelpPageDescription.Text = description;

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
        LanguageSupportSection,
    ];

    private Border[] SettingsSections() =>
    [
        MicrophoneSection,
        TranscriptionEngineSection,
        KeybindsSection,
        SoundSection,
        DeterministicCleanupSection,
        TranscribeFileSection,
        AiPolishSection,
        HistorySettingsSection,
        RecordingPillSection,
        AppearanceSection,
        LivePreviewSection,
        ClipboardSection,
        DiagnosticsSection,
        PortableProfileSection,
        YourDataSection,
        PermissionsSection,
        UpdatesSection,
        LicensesSection,
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
            SystemBackdrop = null;
            return;
        }

        if (SystemBackdrop is not MicaBackdrop)
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }

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
    public void ApplyThemeFromExternal(AppTheme theme)
    {
        var previousApplying = _isApplyingSettings;
        _isApplyingSettings = true;
        try
        {
            SetThemeSelection(ThemeIndex(theme), triggerSave: false);
        }
        finally
        {
            _isApplyingSettings = previousApplying;
        }
    }

    private void ApplyTheme(AppTheme theme)
    {
        var requested = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        WindowRoot.RequestedTheme = requested;
        _overlayWindow?.ApplyTheme(requested);
    }

    // THE MAPS BETWEEN A CHOICE'S INDEX AND WHAT IT MEANS ARE THE PRESENTER'S (plan-2 step 11): the
    // Save reads them there, and the window asks the same ones when it fills its controls or applies
    // a theme the moment it is chosen, so the two directions cannot drift apart.
    private static AppTheme ThemeFromIndex(int index) => SettingsPresenter.ThemeFromIndex(index);

    private static int ThemeIndex(AppTheme theme) => SettingsPresenter.ThemeIndex(theme);

    private static OverlayPillPosition OverlayPositionFromIndex(int index) => SettingsPresenter.OverlayPositionFromIndex(index);

    private static int OverlayPositionIndex(OverlayPillPosition position) => SettingsPresenter.OverlayPositionIndex(position);

    private void RecordingPillCardsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stackVertically = e.NewSize.Width > 0 && e.NewSize.Width < 620;
        RecordingPillCardsGrid.RowSpacing = stackVertically
            ? (double)Application.Current.Resources["BrandSpacingM"]
            : 0;
        var span = stackVertically ? 3 : 1;
        RadioButton[] cards = [CapsulePillButton, LevelRailPillButton, ReadingWellPillButton];
        for (var i = 0; i < cards.Length; i++)
        {
            Grid.SetRow(cards[i], stackVertically ? i : 0);
            Grid.SetColumn(cards[i], stackVertically ? 0 : i);
            Grid.SetColumnSpan(cards[i], span);
        }
    }

    private RecordingPillDesign PillDesignWithoutWordsFromControls()
    {
        if (LevelRailPillButton.IsChecked == true)
        {
            return RecordingPillDesign.LevelRail;
        }

        if (CapsulePillButton.IsChecked == true)
        {
            return RecordingPillDesign.Classic;
        }

        return _lastWithoutWordsPillDesign == RecordingPillDesign.LevelRail
            ? RecordingPillDesign.LevelRail
            : RecordingPillDesign.Classic;
    }

    private RecordingSoundPairing SelectedRecordingSoundPairing() => _selectedRecordingSoundPairing;

    private void UpdatePillDesignControls()
    {
        var previous = _isApplyingSettings;
        _isApplyingSettings = true;
        try
        {
            CapsulePillButton.IsEnabled = true;
            LevelRailPillButton.IsEnabled = true;
            ReadingWellPillButton.IsEnabled = true;

            if (LivePreviewToggle.IsOn)
            {
                ReadingWellPillButton.IsChecked = true;
                CapsulePillButton.IsChecked = false;
                LevelRailPillButton.IsChecked = false;
            }
            else if (_lastWithoutWordsPillDesign == RecordingPillDesign.LevelRail)
            {
                LevelRailPillButton.IsChecked = true;
                CapsulePillButton.IsChecked = false;
                ReadingWellPillButton.IsChecked = false;
            }
            else
            {
                CapsulePillButton.IsChecked = true;
                LevelRailPillButton.IsChecked = false;
                ReadingWellPillButton.IsChecked = false;
            }
        }
        finally
        {
            _isApplyingSettings = previous;
        }
    }

    private static PolishProvider PolishProviderFromIndex(int index) => SettingsPresenter.PolishProviderFromIndex(index);

    private static bool IsCloudProvider(PolishProvider provider) => ProviderSettingsPresenter.IsCloudProvider(provider);

    private static string ProviderDisplayName(PolishProvider provider) => provider switch
    {
        PolishProvider.EgOne => "EG-1",
        PolishProvider.OpenAI => "OpenAI",
        PolishProvider.Anthropic => "Anthropic",
        PolishProvider.Gemini => "Gemini",
        _ => provider.ToString(),
    };

    private static string CredentialArticle(PolishProvider provider) => provider is
        PolishProvider.OpenAI or PolishProvider.Anthropic
            ? "An"
            : "A";

    private static int PolishProviderIndex(PolishProvider provider) => SettingsPresenter.PolishProviderIndex(provider);

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
            IsTemporary = entry.ExpiresAt is not null;
            TagText = IsTemporary
                ? "Recovery"
                : HistoryPresenter.IsFileTranscription(entry)
                    ? "File"
                    : string.Empty;
            TagVisibility = string.IsNullOrEmpty(TagText) ? Visibility.Collapsed : Visibility.Visible;
            var retention = entry.ExpiresAt is null
                ? string.Empty
                : $" · Escape Recovery expires {entry.ExpiresAt.Value.ToLocalTime():g} unless kept";
            Details = $"{TranscriptionEngineNames.DisplayName(entry.EngineId)} · {(entry.WasPolished ? "AI polished" : "deterministic cleanup")} · {(entry.WasDelivered ? "delivered" : "held safely")}{retention}";
        }

        public Guid Id { get; }

        public string Text { get; }

        public string CreatedDisplay { get; }

        public string Details { get; }

        public string TagText { get; }

        public Visibility TagVisibility { get; }

        public bool IsTemporary { get; }

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
