using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using EnviousWispr.App.Services;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Globalization.NumberFormatting;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;

namespace EnviousWispr.App;

public sealed partial class MainWindow : Window, IDisposable
{
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
        new("Learn From", "dictionary", "learn-from", "self learning automatic edits memory import analyze documents"),
        new("Quick Add Words", "dictionary", "quick-add", "shortcut add word"),
        new("Snippets", "snippets", null, "text expansion templates boilerplates phrases"),
        new("Appearance & Theme", "settings-app", "appearance", "light dark system theme high contrast"),
        new("Permissions", "settings-app", "permissions", "microphone privacy"),
        new("Privacy & History", "settings-app", "privacy", "retention days safe held local dictations delete"),
        new("Backup & Profile", "settings-app", "backup", "export import portable profile reset data your data"),
        new("Updates & Licenses", "settings-app", "updates-licenses", "version release notes third party licenses"),
        new("Diagnostics", "settings-diagnostics", null, "logs telemetry troubleshooting reports"),
        new("History", "history", null, "diary transcripts search dictations"),
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

    private readonly DictationOverlayWindow _overlayWindow;

    private bool _shuttingDownProductWindows;

    private AppSettings _settings;

    private bool _isApplyingSettings;
    private bool _initialFocusAssigned;
    private DictationOverlayState _currentOverlayState = DictationOverlayState.Hidden;

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
        DiagnosticRetentionDaysBox.NumberFormatter = new DecimalFormatter
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
        InitializePillPreview();
        _audioPlayback = new WindowsAudioPlaybackService(DispatcherQueue);
        _audioPlayback.StateChanged += OnAudioPlaybackStateChanged;
        _audioPlayback.PlaybackFailed += OnAudioPlaybackFailed;
        InitializeHistoryUi();
        _overlayWindow.ActionInvoked += OnPillActionInvoked;
        _overlayWindow.LearnedUndoRequested += pillId => LearnedPillUndoRequested?.Invoke(pillId);
        _overlayWindow.LearnedEnded += pillId => LearnedPillEnded?.Invoke(pillId);
        _overlayWindow.LearnedDismissed += pillId => LearnedPillEnded?.Invoke(pillId);
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
            RefreshLivePreviewSectionPresentation();
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
        TitleBarVersionText.Text = FormatTitleBarVersion(installedVersion, assemblyVersion);
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

    private static string FormatTitleBarVersion(string? installedVersion, Version? assemblyVersion)
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

    public void SetSessionStatus(DictationStatus status)
    {
        var sentence = status.Text;
        var overlayState = status.State;
        if (!PillPreviewCatalog.ShouldSuppressStatusForPreview(_overlayWindow.IsPreviewVisible, overlayState))
        {
            _overlayWindow.ShowState(status);
        }
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
        if (RecordToolbarButton is not null)
        {
            var isRecording = overlayState == DictationOverlayState.Recording;
            RecordToolbarButton.Style = (Style)Application.Current.Resources[
                isRecording ? "BrandStopToolbarButtonStyle" : "BrandRecordToolbarButtonStyle"];
        }
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

    public void SetLivePreview(string? text) =>
        _overlayWindow.SetPreview(text);

    public void SetAudioLevel(AudioLevel level) =>
        _overlayWindow.SetAudioLevel(level.RootMeanSquare);

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
            case PillActionKind.OpenMicrophoneSettings:
                OpenMicrophoneSettings();
                break;
            case PillActionKind.OpenMicrophonePrivacySettings:
                OpenMicrophonePrivacySettings();
                break;
            case PillActionKind.OpenFileTranscription:
                OpenPage("settings-transcribe-file");
                break;

            // NOT NAVIGATION. Sending it to a page would open the settings and leave the thing the
            // button said it would do undone.
            case PillActionKind.LockDetectedLanguage:
                LockOfferedLanguage();
                break;

            case PillActionKind.UndoEscapeRecovery:
                InvokeEscapeRecoveryUndo();
                break;

            case PillActionKind.UndoLearnedCorrection:
                InvokeLearnedCorrectionUndo();
                break;

            // An enum can hold a value nobody declared. Appearance is the harmless page: it changes
            // nothing and shows the person something they can read.
            default:
                OpenPage("settings-appearance");
                break;
        }
    }

    private void OpenMicrophoneSettings() => OpenPage("settings-dictation", "microphone");

    private async void OpenMicrophonePrivacySettings()
    {
        try
        {
            var opened = await Windows.System.Launcher.LaunchUriAsync(
                new Uri("ms-settings:privacy-microphone"));
            if (!opened)
            {
                OpenMicrophoneSettings();
            }
        }
        catch
        {
            OpenMicrophoneSettings();
        }
    }

    private void InvokeEscapeRecoveryUndo() => RecoveryUndoRequested?.Invoke();

    private void InvokeLearnedCorrectionUndo() => LearnedPillUndoActiveRequested?.Invoke();

    public bool TryShowBluetoothAwarenessCard()
    {
        if (_overlayWindow.IsActiveNonPreview || _overlayWindow.IsPreviewVisible)
        {
            return false;
        }

        _overlayWindow.ShowState(DictationStatus.BluetoothCard());
        return true;
    }

    public bool TryShowPolishSetupCard(string title, string buttonLabel)
    {
        if (_overlayWindow.IsActiveNonPreview || _overlayWindow.IsPreviewVisible)
        {
            return false;
        }

        _overlayWindow.ShowState(DictationStatus.PolishSetupCard(title, buttonLabel));
        return true;
    }

    public Func<(bool HasStoredMemory, int BlockedCount)>? QueryLearnMemoryState { get; set; }

    public Func<bool>? ForgetLearnMemory { get; set; }

    public Action<IReadOnlyList<CustomWordEntry>>? WordsRemovedFromDictionary { get; set; }

    public Action<string, IReadOnlyList<string>>? LearnedAliasesRemovedFromDictionary { get; set; }

    public Func<IReadOnlyDictionary<string, EnviousWispr.Core.Learning.LearnMemoryEntry>?>? ExportLearnMemory { get; set; }

    public Action<IReadOnlyDictionary<string, EnviousWispr.Core.Learning.LearnMemoryEntry>>? ImportLearnMemory { get; set; }

    internal PortableProfile BuildExportablePortableProfile()
    {
        var baseProfile = _settings.ToPortableProfile();
        var learnMemory = ExportLearnMemory?.Invoke();
        return learnMemory is { Count: > 0 }
            ? baseProfile with { LearnMemory = learnMemory }
            : baseProfile;
    }

    public event Action<Guid>? LearnedPillUndoRequested;

    public event Action? LearnedPillUndoActiveRequested;

    public event Action<Guid>? LearnedPillEnded;

    public bool ShowLearnedCorrectionPill(EnviousWispr.Core.Learning.LearnedCorrectionPillModel model) =>
        _overlayWindow.ShowLearnedCorrection(model);

    public void UpdateLearnedCorrectionPill(EnviousWispr.Core.Learning.LearnedCorrectionPillModel model) =>
        _overlayWindow.UpdateLearnedCorrection(model);

    public void CloseLearnedCorrectionPill(Guid pillId) =>
        _overlayWindow.CloseLearnedCorrection(pillId);

    public void ShowLearnedCorrectionSaveErrorPill(string canonical) =>
        _overlayWindow.ShowLearnedSaveError(canonical);

    public event Action? ManualDictationToggleRequested;

    private void RecordToolbarButton_Click(object sender, RoutedEventArgs e) =>
        ManualDictationToggleRequested?.Invoke();

    private void WhatsNewToolbarButton_Click(object sender, RoutedEventArgs e) =>
        OpenPage("whats-new");

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
        var wasOnboardingVisible = OnboardingView?.Visibility == Visibility.Visible;
        AppWindow.Show();
        Activate();
        ShowOnboarding(show: false);

        var (resolvedTag, resolvedTab) = ResolvePageAndTab(tag);
        var targetTab = tabKey ?? resolvedTab;

        if (resolvedTag != "whats-new")
        {
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
        }

        var targetRoute = SettingsRoute.Normalize(resolvedTag, targetTab);
        if (!wasOnboardingVisible && TryBeginGuardedLeave(targetRoute, revertSidebarSelection: false))
        {
            return;
        }

        NavigateCommittedRoute(targetRoute);
    }

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
        ShutdownHistoryUi();
        _audioPlayback.StateChanged -= OnAudioPlaybackStateChanged;
        _audioPlayback.PlaybackFailed -= OnAudioPlaybackFailed;
        _audioPlayback.Dispose();
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

    private string? _pendingTabKey;

    private void ProductNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_isHandlingGuardedNavigation || (args.SelectedItem is null && args.SelectedItemContainer is null))
        {
            return;
        }

        var item = args.SelectedItemContainer as NavigationViewItem ?? args.SelectedItem as NavigationViewItem;
        var tag = item?.Tag?.ToString() ?? "history";
        var tabKey = _pendingTabKey;
        _pendingTabKey = null;

        var targetRoute = SettingsRoute.Normalize(tag, tabKey);
        if (TryBeginGuardedLeave(targetRoute, revertSidebarSelection: true))
        {
            return;
        }

        CommitNavigationRouteState(targetRoute);
        ShowPage(targetRoute.PageTag, targetRoute.TabKey);
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

    /// <summary>The vertical card-based choice lists.</summary>
    private ItemsControl[] ChoiceLists() =>
    [
        PolishProviderComboBox,
    ];

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

    private static void UpdateListAndEmptyStateVisibility(
        ListView list,
        FrameworkElement emptyState,
        int itemCount)
    {
        list.Visibility = itemCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        emptyState.Visibility = itemCount == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>Shows or hides one of the history cards.</summary>
    /// <remarks>
    /// A HELPER SO THE LIVE REGIONS ARE NOT WRITTEN DIRECTLY. HistoryList is a live region and its
    /// visibility changes on every refresh; assigning it in place forced the gate to grant that
    /// region a blanket exemption, which then excused any other direct write to it anywhere in the
    /// file. Going through here means the gate needs no exemption at all.
    /// </remarks>
    private static void ShowCard(FrameworkElement card, bool show) =>
        card.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

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
        if (resolvedTag == "dictionary")
        {
            if (!string.IsNullOrWhiteSpace(targetTab))
            {
                SelectDictionaryTab(targetTab);
            }

            if (DictionaryTabStrip is not null)
            {
                ApplyResponsiveTabStripLayout(DictionaryTabStrip);
            }
        }
        SnippetsPage.Visibility = resolvedTag == "snippets" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = settingsPage ? Visibility.Visible : Visibility.Collapsed;

        if (settingsPage)
        {
            ConfigureSettingsPage(resolvedTag, targetTab);
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
            SettingsTabStrip.Visibility = Visibility.Visible;
            DictationTabStrip.Visibility = Visibility.Visible;
            AppTabStrip.Visibility = Visibility.Collapsed;
            ApplyResponsiveTabStripLayout(DictationTabStrip);
            SelectDictationTab(tabKey ?? "engine");
            return;
        }

        if (tag == "settings-app")
        {
            SettingsTabStrip.Visibility = Visibility.Visible;
            DictationTabStrip.Visibility = Visibility.Collapsed;
            AppTabStrip.Visibility = Visibility.Visible;
            ApplyResponsiveTabStripLayout(AppTabStrip);
            SelectAppTab(tabKey ?? "appearance");
            return;
        }

        SettingsTabStrip.Visibility = Visibility.Collapsed;
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
            NavigateToDictationTab(tag);
        }
    }

    private void SelectDictationTab(string tabKey)
    {
        CommitNavigationRouteState(SettingsRoute.Normalize("settings-dictation", tabKey));
        var active = (Style)Application.Current.Resources["BrandTopTabActiveButtonStyle"];
        var normal = (Style)Application.Current.Resources["BrandTopTabButtonStyle"];
        DictationTabEngine.Style = tabKey == "engine" ? active : normal;
        DictationTabMicrophone.Style = tabKey == "microphone" ? active : normal;
        DictationTabLivePreview.Style = tabKey == "live-preview" ? active : normal;
        DictationTabPill.Style = tabKey == "pill" ? active : normal;
        DictationTabSounds.Style = tabKey == "sounds" ? active : normal;
        DictationTabClipboard.Style = tabKey == "clipboard" ? active : normal;

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
        var active = (Style)Application.Current.Resources["BrandTopTabActiveButtonStyle"];
        var normal = (Style)Application.Current.Resources["BrandTopTabButtonStyle"];
        AppTabAppearance.Style = tabKey == "appearance" ? active : normal;
        AppTabPermissions.Style = tabKey == "permissions" ? active : normal;
        AppTabPrivacy.Style = tabKey == "privacy" ? active : normal;
        AppTabBackup.Style = tabKey == "backup" ? active : normal;
        AppTabUpdatesLicenses.Style = tabKey == "updates-licenses" ? active : normal;

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

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
