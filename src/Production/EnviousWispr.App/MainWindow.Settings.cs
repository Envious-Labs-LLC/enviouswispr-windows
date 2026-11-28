using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EnviousWispr.App;

/// <summary>Auto-save pipeline, settings application and publication, and theme/overlay appearance controls.</summary>
public sealed partial class MainWindow
{
    private int _selectedThemeIndex;
    private int _selectedOverlayPositionIndex;
    private int _warmMicrophonePolicyIndex = SettingsPresenter.WarmMicrophonePolicyIndex(DictationPreferences.Default.WarmMicrophonePolicy);
    private RecordingPillDesign _lastWithoutWordsPillDesign = RecordingPillDesign.Classic;

    /// <summary>Auto-saves settings changes quietly whenever a control is updated.</summary>
    private async Task TriggerAutoSaveAsync()
    {
        if (_isApplyingSettings || _isRefreshingMicrophones || _keybindCaptureSession is not null)
        {
            return;
        }

        // RAW, INCLUDING "NOTHING SELECTED": the indices go across as -1 when no choice is selected
        // and the sound pairing as null when none is, so what a missing choice means is the
        // presenter's decision too, not this handler's.
        var dictation = _settings.Preferences.Dictation;
        var prefs = _settings.Preferences;
        var obs = _settings.Observability ?? ObservabilityPreferences.Default;
        var selectedProviderIndex = RawSelectedIndexOf(PolishProviderChoices);
        var selectedProvider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        var selectedPolishModelForSave = selectedProvider == PolishProvider.EgOne
            ? EnginePolishPresentation.NormalizeEgOneModelId(_selectedEgOneModelId)
            : _selectedPolishModelId;
        var wordCorrectionEnabled = IsSettingsPageLoaded && WordCorrectionToggle is not null
            ? WordCorrectionToggle.IsOn
            : DictionaryEnabledToggle?.IsOn ?? dictation.WordCorrectionEnabled;
        var input = new GeneralSettingsInput(
            HotkeyTextBox?.Text ?? dictation.PushToTalkGesture,
            CancelHotkeyTextBox?.Text ?? dictation.CancelGesture,
            QuickAddHotkeyTextBox?.Text ?? dictation.QuickAddGesture,
            IsSettingsPageLoaded ? SelectedFinalEngineRawIndex() : (int)dictation.FinalEngine,
            wordCorrectionEnabled,
            FillerRemovalToggle?.IsOn ?? dictation.FillerRemovalEnabled,
            EmojiFormatterToggle?.IsOn ?? dictation.EmojiFormatterEnabled,
            SpokenPunctuationToggle?.IsOn ?? dictation.SpokenPunctuationEnabled,
            _selectedWhisperLanguage,
            IsSettingsPageLoaded && EnglishSpellingComboBox is not null
                ? EnglishSpellingComboBox.SelectedIndex
                : (int)dictation.EnglishSpelling,
            _recordingModeIndex,
            EscapeRecoveryToggle?.IsOn ?? dictation.EscapeRecoveryEnabled,
            AutoStopToggle?.IsOn ?? dictation.AutoStopEnabled,
            AutoStopSecondsBox?.Value ?? dictation.AutoStopSilenceSeconds,
            selectedProviderIndex,
            selectedPolishModelForSave,
            OllamaEndpointTextBox?.Text ?? (prefs.Polish.OllamaEndpoint ?? string.Empty),
            HistoryEnabledToggle?.IsOn ?? prefs.History.IsEnabled,
            IsSettingsPageLoaded && RetentionDaysComboBox is not null
                ? SelectedRetentionDays()
                : prefs.History.RetentionDays,
            _selectedThemeIndex,
            LivePreviewToggle?.IsOn ?? prefs.LivePreviewEnabled,
            _selectedOverlayPositionIndex,
            IsSettingsPageLoaded
                ? PillDesignWithoutWordsFromControls() == RecordingPillDesign.LevelRail
                : _lastWithoutWordsPillDesign == RecordingPillDesign.LevelRail,
            PlayRecordingSoundsToggle?.IsOn ?? prefs.PlayRecordingSounds,
            SelectedRecordingSoundPairing(),
            CopyInsteadOfPasteToggle?.IsOn ?? prefs.CopyInsteadOfPaste,
            LocalDiagnosticsToggle?.IsOn ?? obs.LocalDiagnosticsEnabled,
            DiagnosticRetentionDaysBox?.Value ?? obs.DiagnosticRetentionDays,
            ShareUsageMetricsToggle?.IsOn ?? obs.ShareAnonymousTelemetry,
            TelemetryAvailable: true,
            IsSettingsPageLoaded && MicrophoneComboBox is not null
                ? (MicrophoneComboBox.SelectedItem as MicrophoneChoice)?.Id
                : _settings.PreferredMicrophoneId,
            PasteLastHotkeyTextBox?.Text ?? dictation.PasteLastGesture,
            CopyLastHotkeyTextBox?.Text ?? dictation.CopyLastGesture,
            CasualModeToggle?.IsOn ?? dictation.CasualModeEnabled,
            IsSettingsPageLoaded ? SelectedLivePreviewEngineIndex() : (int)prefs.LivePreviewEngine,
            SendCrashReportsToggle?.IsOn ?? obs.SendCrashReports,
            EgOneModelId: _selectedEgOneModelId,
            PolishUnloadPolicyIndex: EgOneUnloadPolicyComboBox?.SelectedIndex
                ?? SettingsPresenter.PolishUnloadPolicyIndex(prefs.Polish.UnloadPolicy),
            ReturnToStartWindow: ReturnToStartWindowToggle?.IsOn
                ?? (prefs.PasteTargetMode == PasteTargetMode.WhereDictationStarted),
            LearnFromEdits: LearnFromEditsToggle?.IsOn ?? dictation.LearnFromEditsEnabled,
            FixLearnedWords: FixLearnedWordsToggle?.IsOn ?? dictation.FixLearnedWordsEnabled,
            SmartInsertionEnabled: SmartInsertionToggle?.IsOn ?? prefs.SmartInsertionEnabled,
            WarmMicrophonePolicyIndex: _warmMicrophonePolicyIndex,
            SilentMicWarningsEnabled: SilentMicWarningsToggle?.IsOn ?? dictation.SilentMicWarningsEnabled);

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
                })?.Focus(FocusState.Programmatic);
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

    private bool _isSyncingWordCorrectionToggles;

    private void WireDictionaryPageAutoSave()
    {
        if (LearnFromEditsToggle is not null)
        {
            LearnFromEditsToggle.Toggled += (_, _) =>
            {
                RefreshLearnMemoryUi();
                _ = TriggerAutoSaveAsync();
            };
        }

        if (FixLearnedWordsToggle is not null)
        {
            FixLearnedWordsToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        }
    }

    private void WireSettingsPageAutoSave()
    {
        WordCorrectionToggle.Toggled += (_, _) => HandleWordCorrectionToggleChanged(WordCorrectionToggle);
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
        CopyInsteadOfPasteToggle.Toggled += (_, _) =>
        {
            if (ReturnToStartWindowToggle is not null)
            {
                ReturnToStartWindowToggle.IsEnabled = !CopyInsteadOfPasteToggle.IsOn;
            }
            _ = TriggerAutoSaveAsync();
        };
        if (ReturnToStartWindowToggle is not null)
        {
            ReturnToStartWindowToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        }
        if (SmartInsertionToggle is not null)
        {
            SmartInsertionToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        }
        LocalDiagnosticsToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        ShareUsageMetricsToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();
        SendCrashReportsToggle.Toggled += (_, _) =>
        {
            UpdateDiagnosticsStatusText(SendCrashReportsToggle.IsOn);
            _ = TriggerAutoSaveAsync();
        };

        EnglishSpellingComboBox.SelectionChanged += (_, _) =>
        {
            RefreshLanguageControls();
            _ = TriggerAutoSaveAsync();
        };
        MicrophoneComboBox.SelectionChanged += (_, _) => _ = TriggerAutoSaveAsync();
        MicReadinessOffSegmentButton.Click += (_, _) => SetWarmMicrophonePolicySelection(0, triggerSave: true);
        MicReadiness10sSegmentButton.Click += (_, _) => SetWarmMicrophonePolicySelection(1, triggerSave: true);
        MicReadiness30sSegmentButton.Click += (_, _) => SetWarmMicrophonePolicySelection(2, triggerSave: true);
        MicReadiness60sSegmentButton.Click += (_, _) => SetWarmMicrophonePolicySelection(3, triggerSave: true);
        MicReadinessAlwaysSegmentButton.Click += (_, _) => SetWarmMicrophonePolicySelection(4, triggerSave: true);
        SilentMicWarningsToggle.Toggled += (_, _) => _ = TriggerAutoSaveAsync();

        AutoStopSecondsBox.ValueChanged += (_, _) => _ = TriggerAutoSaveAsync();
        RetentionDaysComboBox.SelectionChanged += (_, _) => _ = TriggerAutoSaveAsync();
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
    internal void SystemThemeSegmentButton_Click(object sender, RoutedEventArgs e) =>
        SetThemeSelection(0, triggerSave: true);

    internal void LightThemeSegmentButton_Click(object sender, RoutedEventArgs e) =>
        SetThemeSelection(1, triggerSave: true);

    internal void DarkThemeSegmentButton_Click(object sender, RoutedEventArgs e) =>
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

    private void SetWarmMicrophonePolicySelection(int index, bool triggerSave)
    {
        _warmMicrophonePolicyIndex = Math.Clamp(index, 0, 4);
        UpdateMicReadinessSegmentButtons();

        if (triggerSave && !_isApplyingSettings && _settings is not null)
        {
            _ = TriggerAutoSaveAsync();
        }
    }

    private void UpdateMicReadinessSegmentButtons()
    {
        if (MicReadinessOffSegmentButton is null)
        {
            return;
        }

        ApplySegmentButtonState(MicReadinessOffSegmentButton, _warmMicrophonePolicyIndex == 0);
        ApplySegmentButtonState(MicReadiness10sSegmentButton, _warmMicrophonePolicyIndex == 1);
        ApplySegmentButtonState(MicReadiness30sSegmentButton, _warmMicrophonePolicyIndex == 2);
        ApplySegmentButtonState(MicReadiness60sSegmentButton, _warmMicrophonePolicyIndex == 3);
        ApplySegmentButtonState(MicReadinessAlwaysSegmentButton, _warmMicrophonePolicyIndex == 4);
        if (MicReadinessAlwaysNotice is not null)
        {
            MicReadinessAlwaysNotice.Visibility = _warmMicrophonePolicyIndex == 4
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    internal void TopPositionSegmentButton_Click(object sender, RoutedEventArgs e) =>
        SetOverlayPositionSelection(0, triggerSave: true);

    internal void BottomPositionSegmentButton_Click(object sender, RoutedEventArgs e) =>
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

    /// <summary>Applies and persists a single-selection Recording Pill card choice immediately.</summary>
    internal async void PillStyleCardChecked(object sender, RoutedEventArgs e)
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
        UpdateHistorySelectionDependentButtons();

        if (IsDictionaryPageLoaded)
        {
            _dictionaryPageView?.UpdateSelectionUi(CurrentCustomWords.Count);
        }

        if (IsSnippetsPageLoaded && ExportSnippetsButton is not null)
        {
            ExportSnippetsButton.IsEnabled = (_settings?.UserData.Snippets.Count ?? 0) > 0;
        }
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
        SyncAppliedWhisperLanguageStatus(
            _settings.Preferences.Dictation.WhisperLanguage,
            _settings.Preferences.Dictation.EnglishSpelling);
    }

    [Flags]
    private enum UserDataRefreshScope
    {
        None = 0,
        Dictionary = 1,
        VocabularyPacks = 2,
        Snippets = 4,
        All = Dictionary | VocabularyPacks | Snippets,
    }

    private void RefreshReusableUserDataViews() =>
        ApplyUserDataRefresh(UserDataRefreshScope.All);

    private void ApplyUserDataRefresh(UserDataRefreshScope scope)
    {
        if (scope.HasFlag(UserDataRefreshScope.Dictionary) && IsDictionaryPageLoaded)
        {
            _dictionaryPageView?.ApplyWords(
                CurrentCustomWords,
                isPageVisible: DictionaryPage is { Visibility: Visibility.Visible });
        }

        if (scope.HasFlag(UserDataRefreshScope.VocabularyPacks) && IsDictionaryPageLoaded)
        {
            RefreshVocabularyPacksPanel();
        }

        if (scope.HasFlag(UserDataRefreshScope.Snippets))
        {
            if (IsSnippetsPageLoaded)
            {
                RefreshSnippetKeywordViews(force: false);
            }

            _snippetsViewDirty = true;
            if (IsSnippetsPageLoaded && SnippetsPage is { Visibility: Visibility.Visible })
            {
                ScheduleSnippetRefresh(debounce: false);
            }
            else if (IsSnippetsPageLoaded)
            {
                var snippets = _settings?.UserData.Snippets ?? (IReadOnlyList<SnippetEntry>)Array.Empty<SnippetEntry>();
                if (SnippetCountText is not null)
                {
                    SnippetCountText.Text = snippets.Count == 0
                        ? string.Empty
                        : SnippetImportCopy.CountLabel(snippets.Count, snippets.Count, searching: false);
                }

                UpdateSelectionDependentButtons();
            }
        }
    }

    public void SyncExternalSettingsUpdate()
    {
        _settings = _session.Settings.Current;
        ApplyUserDataRefresh(UserDataRefreshScope.Dictionary | UserDataRefreshScope.VocabularyPacks);
        RefreshLearnMemoryUi();
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
            _selectedWhisperLanguage = LanguageCatalog.NormalizeOrDefault(preferences.Dictation.WhisperLanguage);
            _recordingModeIndex = (int)preferences.Dictation.RecordingMode;
            _selectedThemeIndex = ThemeIndex(preferences.Theme);
            _selectedOverlayPositionIndex = OverlayPositionIndex(preferences.OverlayPosition);
            _selectedRecordingSoundPairing = preferences.RecordingSoundPairing;
            _lastWithoutWordsPillDesign = preferences.PillDesignWithoutWords == RecordingPillDesign.LevelRail
                ? RecordingPillDesign.LevelRail
                : RecordingPillDesign.Classic;

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

            ApplyTheme(preferences.Theme);
            _overlayWindow.ApplyPreferences(
                preferences.OverlayPosition,
                preferences.LivePreviewEnabled,
                preferences.PillDesignWithoutWords,
                preferences.PillDesignWithWords);

            if (IsDictionaryPageLoaded)
            {
                ApplySettingsToDictionaryPageControlsCore();
            }

            if (IsSettingsPageLoaded)
            {
                ApplySettingsToSettingsPageControlsCore();
            }

            RefreshReusableUserDataViews();
            UpdateHistoryPolishState();
        }
        finally
        {
            _isApplyingSettings = false;
        }

        if (IsSettingsPageLoaded)
        {
            if (ApiKeyPasswordBox is not null)
            {
                ApiKeyPasswordBox.Password = string.Empty;
            }

            RefreshApiKeyStatus();
            _ = RefreshPolishModelChoicesAsync(
                _settings.Preferences.Polish.Provider,
                chooseDefault: false);
        }
    }

    private void ApplySettingsToDictionaryPageControls()
    {
        var previous = _isApplyingSettings;
        _isApplyingSettings = true;
        try
        {
            ApplySettingsToDictionaryPageControlsCore();
        }
        finally
        {
            _isApplyingSettings = previous;
        }
    }

    private void ApplySettingsToDictionaryPageControlsCore()
    {
        var dictation = _settings.Preferences.Dictation;
        if (DictionaryEnabledToggle is not null)
        {
            DictionaryEnabledToggle.IsOn = dictation.WordCorrectionEnabled;
        }

        if (LearnFromEditsToggle is not null)
        {
            LearnFromEditsToggle.IsOn = dictation.LearnFromEditsEnabled;
        }

        if (FixLearnedWordsToggle is not null)
        {
            FixLearnedWordsToggle.IsOn = dictation.FixLearnedWordsEnabled;
        }

        UpdateQuickAddShortcutBadge();
        RefreshLearnMemoryUi();
    }

    private void ApplySettingsToSettingsPageControls()
    {
        var previous = _isApplyingSettings;
        _isApplyingSettings = true;
        try
        {
            ApplySettingsToSettingsPageControlsCore();
        }
        finally
        {
            _isApplyingSettings = previous;
        }
    }

    private void ApplySettingsToSettingsPageControlsCore()
    {
        var preferences = _settings.Preferences;
        SetSelectedFinalEngineCards(preferences.Dictation.FinalEngine);
        EnglishSpellingComboBox.SelectedIndex = (int)preferences.Dictation.EnglishSpelling;
        UpdateActiveEngineDisplay(preferences.Dictation.FinalEngine);
        SyncAppliedWhisperLanguageStatus(
            preferences.Dictation.WhisperLanguage,
            preferences.Dictation.EnglishSpelling);
        HotkeyTextBox.Text = preferences.Dictation.PushToTalkGesture;
        SetRecordingMode((int)preferences.Dictation.RecordingMode, triggerSave: false);
        CancelHotkeyTextBox.Text = preferences.Dictation.CancelGesture;
        EscapeRecoveryToggle.IsOn = preferences.Dictation.EscapeRecoveryEnabled;
        QuickAddHotkeyTextBox.Text = preferences.Dictation.QuickAddGesture;
        PasteLastHotkeyTextBox.Text = preferences.Dictation.PasteLastGesture;
        CopyLastHotkeyTextBox.Text = preferences.Dictation.CopyLastGesture;
        WordCorrectionToggle.IsOn = preferences.Dictation.WordCorrectionEnabled;
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
        SetWarmMicrophonePolicySelection(
            SettingsPresenter.WarmMicrophonePolicyIndex(preferences.Dictation.WarmMicrophonePolicy),
            triggerSave: false);
        if (SilentMicWarningsToggle is not null)
        {
            SilentMicWarningsToggle.IsOn = preferences.Dictation.SilentMicWarningsEnabled;
        }

        if (EgOneUnloadPolicyComboBox is not null)
        {
            EgOneUnloadPolicyComboBox.SelectedIndex = SettingsPresenter.PolishUnloadPolicyIndex(
                preferences.Polish.UnloadPolicy);
        }
        UpdateEgOneUnloadImmediatelyNotice();
        UpdateActivePolishProviderDisplay();
        OllamaEndpointTextBox.Text = preferences.Polish.OllamaEndpoint ?? string.Empty;
        HistoryEnabledToggle.IsOn = preferences.History.IsEnabled;
        ApplyHistoryRetentionChoice(preferences.History.RetentionDays);
        SetThemeSelection(ThemeIndex(preferences.Theme), triggerSave: false);
        LivePreviewToggle.IsOn = preferences.LivePreviewEnabled;
        LivePreviewParakeetEngineCard.IsChecked = preferences.LivePreviewEngine == LivePreviewEngineChoice.Parakeet;
        LivePreviewWhisperEngineCard.IsChecked = preferences.LivePreviewEngine == LivePreviewEngineChoice.Whisper;
        RefreshLivePreviewSectionPresentation();
        SetOverlayPositionSelection(OverlayPositionIndex(preferences.OverlayPosition), triggerSave: false);
        PlayRecordingSoundsToggle.IsOn = preferences.PlayRecordingSounds;
        CopyInsteadOfPasteToggle.IsOn = preferences.CopyInsteadOfPaste;
        if (ReturnToStartWindowToggle is not null)
        {
            ReturnToStartWindowToggle.IsOn = preferences.PasteTargetMode == PasteTargetMode.WhereDictationStarted;
            ReturnToStartWindowToggle.IsEnabled = !preferences.CopyInsteadOfPaste;
        }
        if (SmartInsertionToggle is not null)
        {
            SmartInsertionToggle.IsOn = preferences.SmartInsertionEnabled;
        }
        SelectChimePairing(preferences.RecordingSoundPairing, triggerSave: false);
        UpdatePillDesignControls();

        var observability = _settings.Observability ?? ObservabilityPreferences.Default;
        LocalDiagnosticsToggle.IsOn = observability.LocalDiagnosticsEnabled;
        DiagnosticRetentionDaysBox.Value = observability.DiagnosticRetentionDays;
        ShareUsageMetricsToggle.IsOn = observability.ShareAnonymousTelemetry;
        SendCrashReportsToggle.IsOn = observability.SendCrashReports;
        UpdateDiagnosticsStatusText(observability.SendCrashReports);
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
    internal void SettingRow_Tapped(object sender, TappedRoutedEventArgs e)
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

        if (IsDescendantOf(e.OriginalSource as DependencyObject, toggle) ||
            IsDescendantOfButtonWithinRow(e.OriginalSource as DependencyObject, row))
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

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDescendantOfButtonWithinRow(DependencyObject? source, DependencyObject row)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, row); current = VisualTreeHelper.GetParent(current))
        {
            if (current is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private void HandleWordCorrectionToggleChanged(ToggleSwitch source)
    {
        if (_isApplyingSettings || _isSyncingWordCorrectionToggles || _settings is null)
        {
            return;
        }

        var mirror = ReferenceEquals(source, WordCorrectionToggle)
            ? DictionaryEnabledToggle
            : WordCorrectionToggle;
        var isEnabled = source.IsOn;

        _isSyncingWordCorrectionToggles = true;
        try
        {
            if (mirror is not null && mirror.IsOn != isEnabled)
            {
                mirror.IsOn = isEnabled;
            }
        }
        finally
        {
            _isSyncingWordCorrectionToggles = false;
        }

        _ = TriggerAutoSaveAsync();
    }

    private void ApplyHistoryRetentionChoice(int retentionDays)
    {
        var choiceState = HistoryRetentionChoices.Resolve(retentionDays);
        RetentionDaysComboBox.ItemsSource = choiceState.Options;
        RetentionDaysComboBox.SelectedIndex = choiceState.SelectedIndex;
    }

    private double SelectedRetentionDays() =>
        HistoryRetentionChoices.SelectedDaysOrFallback(
            RetentionDaysComboBox.SelectedItem,
            _settings.Preferences.History.RetentionDays);

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

    internal void RecordingPillCardsGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveThreeCardGridLayout(
            RecordingPillCardsGrid,
            e.NewSize.Width,
            stackThreshold: 620,
            [CapsulePillButton, LevelRailPillButton, ReadingWellPillButton]);

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

    private void UpdatePillDesignControls()
    {
        if (CapsulePillButton is null || LevelRailPillButton is null || ReadingWellPillButton is null || LivePreviewToggle is null)
        {
            return;
        }

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
}
