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

    private readonly IMishearingAdvisor _mishearingAdvisor;

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
        WindowLaunch launch,
        IMishearingAdvisor mishearingAdvisor)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(mishearingAdvisor);

        _settings = settings;
        _session = session;
        _mishearingAdvisor = mishearingAdvisor;
        _telemetryAvailable = launch.TelemetryAvailable;
        _launchedCrashReports = launch.LaunchedCrashReports;
        var settingsLoadStatus = launch.SettingsLoadStatus;
        var releaseIdentity = launch.ReleaseIdentity;
        _storeInstalled = launch.StoreInstalled;
        var installedVersion = launch.InstalledVersion;
        _dataDirectory = launch.DataDirectory;

        InitializeComponent();
        InitializePageRegistry();
        PlaceSidebarResizeHandle();

        _recordingSoundPlayer = new RecordingSoundCuePlayer(
            onPlaybackFailed: OnRecordingSoundCueFailed);
        _recordingSoundCoordinator = new RecordingSoundCueCoordinator(PlayRecordingSoundCue);
        PreloadRecordingSoundsInBackground(_selectedRecordingSoundPairing);
        _overlayWindow = new DictationOverlayWindow();
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

        // Caption glyphs follow the resolved theme automatically, while loaded pages refresh via the registry.
        ApplyCaptionButtonColors();
        WindowRoot.ActualThemeChanged += (_, _) =>
        {
            ApplyCaptionButtonColors();
            _pageRegistry.NotifyThemeChanged();
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
        AppWindow.Changed += OnMainAppWindowChanged;
        Closed += (_, _) => CancelKeybindCapture();

        // Preserve persisted language offer history across restarts.
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
        _installedVersionDisplay = installedVersion ?? string.Empty;
        TitleBarVersionText.Text = FormatTitleBarVersion(installedVersion, assemblyVersion);
        _buildInfoText = $"{releaseIdentity.DisplayName} {assemblyVersion} · {releaseIdentity.ChannelName}";
        _releaseNotesIdentity = _buildInfoText;

        _languageSuggestions = new LanguageLockSuggester(settings.LanguageOfferHistory);
        ApplyTheme(settings.Preferences.Theme);
        ApplySettingsToControls();
        ShowOnboarding(!settings.HasCompletedOnboarding);
        ProductNavigation.SelectedItem = HistoryNavItem;

        // Keep release notes unread mark active until What's New is opened on this build.
        ShowReleaseNotesMark(
            ReleaseNotesMark.IsUnread(settings.LastSeenReleaseNotes, _releaseNotesIdentity));
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
            EnsureOnboardingViewLoaded();
        }
    }

    private void OnMainAppWindowChanged(
        AppWindow sender,
        AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange || args.DidPresenterChange)
        {
            UpdateEgOneMemoryTimer(tickImmediately: true);
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
        await InitializeOnboardingStateAsync().ConfigureAwait(true);
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

    public void ShowApproachingCapNotice()
    {
        if (!_overlayWindow.IsPreviewVisible)
        {
            _overlayWindow.ShowApproachingCapNotice();
        }
    }

    public void SetAudioLevel(AudioLevel level)
    {
        _overlayWindow.SetAudioLevel(level.RootMeanSquare);
        ObserveAudioLevelForOnboarding(level.RootMeanSquare);
    }

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


    public void ShutdownProductWindows()
    {
        _shuttingDownProductWindows = true;
        // Tear down loaded pages and detach any queued timers or CTS instances before shutdown.
        _pageRegistry.ShutdownLoadedPages();
        ShutdownHistoryUi();
        ShutdownUserDataUi();
        _audioPlayback.StateChanged -= OnAudioPlaybackStateChanged;
        _audioPlayback.PlaybackFailed -= OnAudioPlaybackFailed;
        _audioPlayback.Dispose();
        AppWindow.Changed -= OnMainAppWindowChanged;
        StopOllamaLivePollTimer();
        StopEgOneMemoryTimer();
        StopOnboardingTimers();
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

        _overlayWindow.PreviewActionPressed -= OnOverlayPreviewActionPressed;
        _overlayWindow.Shutdown();
    }

    public void Dispose() => ShutdownProductWindows();
}
