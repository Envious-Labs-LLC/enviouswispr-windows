using System.Diagnostics;
using System.Runtime.InteropServices;
using EnviousWispr.App.Services;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace EnviousWispr.App;

public sealed partial class MainWindow
{
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

    private bool _isOnboardingWindowGeometryActive;

    private void ShowOnboarding(bool show)
    {
        if (show)
        {
            EnsureOnboardingViewLoaded();
            ProductNavigation.Visibility = Visibility.Collapsed;
            if (AppTitleBarRightActions is not null)
            {
                AppTitleBarRightActions.Visibility = Visibility.Collapsed;
            }
            if (TitleBarVersionBadge is not null)
            {
                TitleBarVersionBadge.Visibility = Visibility.Collapsed;
            }
            if (AppTitleBarTitleText is not null)
            {
                AppTitleBarTitleText.Text = "EnviousWispr Setup";
            }

            if (OnboardingView is not null)
            {
                OnboardingView.Visibility = Visibility.Visible;
                ApplyOnboardingWindowGeometry(onboarding: true);
                // EVERY SHOWING IS A NEW VISIT FROM THE WELCOME: a first run, or "Run setup again" on Diagnostics.
                BeginOnboardingVisit();
            }
        }
        else
        {
            if (OnboardingView is not null)
            {
                OnboardingView.Visibility = Visibility.Collapsed;
            }

            ProductNavigation.Visibility = Visibility.Visible;
            if (AppTitleBarRightActions is not null)
            {
                AppTitleBarRightActions.Visibility = Visibility.Visible;
            }
            if (TitleBarVersionBadge is not null)
            {
                TitleBarVersionBadge.Visibility = Visibility.Visible;
            }
            if (AppTitleBarTitleText is not null)
            {
                AppTitleBarTitleText.Text = "EnviousWispr";
            }

            StopOnboardingTimers();
            ApplyOnboardingWindowGeometry(onboarding: false);
        }

        UpdateEgOneMemoryTimer(tickImmediately: !show);
    }

    /// <summary>
    /// Switches between the focused solo setup window geometry (580x780 DIP, 480 DIP minimum)
    /// and the full two-pane shell geometry, clamped strictly to 94% of the active WorkArea and centered.
    /// </summary>
    private void ApplyOnboardingWindowGeometry(bool onboarding)
    {
        var scale = DisplayScale();
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;

        if (onboarding)
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = (int)Math.Ceiling(
                    LayoutDouble("BrandOnboardingWindowMinWidth") * scale);
            }

            var width = LayoutDouble("BrandOnboardingWindowWidth") * scale;
            var height = LayoutDouble("BrandOnboardingWindowHeight") * scale;
            if (workArea is { } area && area.Width > 0 && area.Height > 0)
            {
                const double screenMargin = 0.94;
                width = Math.Min(width, area.Width * screenMargin);
                height = Math.Min(height, area.Height * screenMargin);
                var targetWidth = (int)Math.Round(width);
                var targetHeight = (int)Math.Round(height);
                var x = area.X + Math.Max(0, (area.Width - targetWidth) / 2);
                var y = area.Y + Math.Max(0, (area.Height - targetHeight) / 2);
                AppWindow.MoveAndResize(new RectInt32(x, y, targetWidth, targetHeight));
            }
            else
            {
                AppWindow.Resize(new SizeInt32((int)Math.Round(width), (int)Math.Round(height)));
            }

            _isOnboardingWindowGeometryActive = true;
            return;
        }

        ConfigureMinimumWindowWidth();
        if (!_isOnboardingWindowGeometryActive)
        {
            return;
        }

        _isOnboardingWindowGeometryActive = false;
        ResizeToDefault();
        if (workArea is { } restoredArea && restoredArea.Width > 0 && restoredArea.Height > 0)
        {
            var currentSize = AppWindow.Size;
            var x = restoredArea.X + Math.Max(0, (restoredArea.Width - currentSize.Width) / 2);
            var y = restoredArea.Y + Math.Max(0, (restoredArea.Height - currentSize.Height) / 2);
            AppWindow.Move(new PointInt32(x, y));
        }
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

        var pageKey = PageKeyForTag(resolvedTag);
        var activePage = _pageRegistry.ActivateContentPage(pageKey);

        if (pageKey == Pages.WindowPageKey.History)
        {
            AnnounceHistoryOnPageShown();
        }
        else if (pageKey == Pages.WindowPageKey.WhatsNew)
        {
            _ = MarkReleaseNotesSeenAsync();
        }
        else if (pageKey == Pages.WindowPageKey.Dictionary)
        {
            if (!string.IsNullOrWhiteSpace(targetTab))
            {
                SelectDictionaryTab(targetTab);
            }

            if (DictionaryTabStrip is not null)
            {
                ApplyResponsiveTabStripLayout(DictionaryTabStrip);
            }

            _dictionaryPageView?.FlushPendingRefresh(CurrentCustomWords);
        }
        else if (pageKey == Pages.WindowPageKey.Snippets)
        {
            FlushPendingSnippetRefresh();
        }
        else if (pageKey == Pages.WindowPageKey.Settings)
        {
            ConfigureSettingsPage(resolvedTag, targetTab);
        }

        if (!IsSettingsPageLoaded || SettingsPage?.Visibility != Visibility.Visible || KeybindsSection?.Visibility != Visibility.Visible)
        {
            CancelKeybindCapture();
        }

        if (activePage is not null)
        {
            PlayPageEntrance(activePage);
        }

        UpdateOllamaLivePollTimer();
        UpdateEgOneMemoryTimer(tickImmediately: resolvedTag == "settings-ai-polish");
        if (resolvedTag == "settings-ai-polish" &&
            PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) == PolishProvider.Ollama)
        {
            _ = PollOllamaModelsSilentlyAsync();
        }
    }

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

    internal void DictationTab_Click(object sender, RoutedEventArgs e)
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

    internal void AppTab_Click(object sender, RoutedEventArgs e)
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
}
