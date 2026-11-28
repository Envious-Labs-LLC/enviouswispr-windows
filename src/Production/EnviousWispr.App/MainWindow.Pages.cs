using EnviousWispr.App.Pages;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Globalization.NumberFormatting;

namespace EnviousWispr.App;

/// <summary>Load-on-demand page registration, deferred control materialization, and snapshot replay for MainWindow.</summary>
public sealed partial class MainWindow
{
    private readonly WindowPageRegistry _pageRegistry = new();

    private bool _onboardingLoaded;
    private bool _whatsNewLoaded;
    private bool _dictionaryLoaded;
    private bool _snippetsLoaded;
    private bool _settingsLoaded;

    private string _buildInfoText = string.Empty;
    private string _installedVersionDisplay = string.Empty;
    private bool _updateCheckInProgress;
    private bool _updateInstallInProgress;
    private UpdateOperationResult? _latestUpdateResult;
    private StartupRegistrationResult? _latestStartupRegistration;
    private bool _hasGraphicsRuntimeSnapshot;
    private ModelDeliveryPresentation? _latestGraphicsRuntimeDelivery;
    private bool _hasSpeedCheckResult;
    private LatencySummary? _latestSpeedCheckSummary;
    private MicrophoneChoice? _selectedMicrophoneChoice;
    private MicrophoneReadiness? _latestMicrophoneReadiness;
    private MicrophoneConsent _latestMicrophoneConsent = MicrophoneConsent.Unknown;
    private string? _latestSpeechEngineNotice;
    private string? _latestEgOneNotice;
    private (string Title, string Message)? _pendingOnboardingReliabilityNotice;

    internal bool IsOnboardingLoaded => _onboardingLoaded;

    internal bool IsWhatsNewPageLoaded => _whatsNewLoaded;

    internal bool IsDictionaryPageLoaded => _dictionaryLoaded;

    internal bool IsSnippetsPageLoaded => _snippetsLoaded;

    internal bool IsSettingsPageLoaded => _settingsLoaded;

    private void InitializePageRegistry()
    {
        _pageRegistry.Register(new WindowPageRegistration(
            WindowPageKey.History,
            isContentPage: true,
            isLoaded: () => HistoryPage is not null,
            ensureLoaded: () => { },
            getElement: () => HistoryPage,
            onThemeChanged: UpdateHistoryFilterSegmentButtons,
            onShutdown: ShutdownHistoryUi));

        _pageRegistry.Register(new WindowPageRegistration(
            WindowPageKey.WhatsNew,
            isContentPage: true,
            isLoaded: () => _whatsNewLoaded,
            ensureLoaded: EnsureWhatsNewPageLoaded,
            getElement: () => WhatsNewPage,
            onShutdown: () =>
            {
                if (_whatsNewPageView is { } view)
                {
                    view.Detach();
                    MainContentGrid.Children.Remove(view);
                    _whatsNewPageView = null;
                }
                _whatsNewLoaded = false;
            }));

        _pageRegistry.Register(new WindowPageRegistration(
            WindowPageKey.Dictionary,
            isContentPage: true,
            isLoaded: () => _dictionaryLoaded,
            ensureLoaded: EnsureDictionaryPageLoaded,
            getElement: () => DictionaryPage,
            onShutdown: () =>
            {
                InvalidateAliasSuggestions(clearSurface: false);
                if (_dictionaryPageView is { } view)
                {
                    view.Detach();
                    MainContentGrid.Children.Remove(view);
                    _dictionaryPageView = null;
                }
                _dictionaryLoaded = false;
            }));

        _pageRegistry.Register(new WindowPageRegistration(
            WindowPageKey.Snippets,
            isContentPage: true,
            isLoaded: () => _snippetsLoaded,
            ensureLoaded: EnsureSnippetsPageLoaded,
            getElement: () => SnippetsPage,
            onShutdown: () =>
            {
                _snippetRefreshCts?.Cancel();
                _snippetRefreshCts?.Dispose();
                _snippetRefreshCts = null;
                if (_snippetsPageView is { } view)
                {
                    view.Detach();
                    MainContentGrid.Children.Remove(view);
                    _snippetsPageView = null;
                }
                _snippetsLoaded = false;
            }));

        _pageRegistry.Register(new WindowPageRegistration(
            WindowPageKey.Settings,
            isContentPage: true,
            isLoaded: () => _settingsLoaded,
            ensureLoaded: EnsureSettingsPageLoaded,
            getElement: () => SettingsPage,
            onThemeChanged: OnSettingsPageThemeChanged,
            onShutdown: () =>
            {
                CancelKeybindCapture();
                StopOllamaLivePollTimer();
                StopEgOneMemoryTimer();
                if (_settingsPageView is { } view)
                {
                    view.Detach();
                    MainContentGrid.Children.Remove(view);
                    _settingsPageView = null;
                }
                _settingsLoaded = false;
            }));

        _pageRegistry.Register(new WindowPageRegistration(
            WindowPageKey.Onboarding,
            isContentPage: false,
            isLoaded: () => _onboardingLoaded,
            ensureLoaded: EnsureOnboardingViewLoaded,
            getElement: () => OnboardingView,
            onShutdown: () =>
            {
                StopOnboardingTimers();
                if (_onboardingPageView is { } view)
                {
                    view.OnboardingView.LayoutUpdated -= FitWindowToOnboardingOnce;
                    view.Detach();
                    ShellContentGrid.Children.Remove(view);
                    _onboardingPageView = null;
                }
                _onboardingLoaded = false;
            }));
    }

    private void OnSettingsPageThemeChanged()
    {
        UpdateChimeCardsVisuals();
        UpdateRecordingModeButtons();
        UpdateThemeSegmentButtons();
        RenderSpeechEngineCards();
        UpdateEgOneCards();
        RefreshLivePreviewSectionPresentation();
        UpdateOverlayPositionSegmentButtons();
    }

    private static WindowPageKey PageKeyForTag(string resolvedTag) => resolvedTag switch
    {
        "whats-new" => WindowPageKey.WhatsNew,
        "dictionary" => WindowPageKey.Dictionary,
        "snippets" => WindowPageKey.Snippets,
        _ when resolvedTag.StartsWith("settings-", StringComparison.Ordinal) => WindowPageKey.Settings,
        _ => WindowPageKey.History,
    };

    private void EnsureOnboardingViewLoaded()
    {
        if (_onboardingLoaded)
        {
            return;
        }

        _onboardingPageView = new OnboardingPageView(this);
        ShellContentGrid.Children.Insert(0, _onboardingPageView);
        _onboardingLoaded = OnboardingView is not null;
        if (!_onboardingLoaded)
        {
            return;
        }

        if (_pendingOnboardingReliabilityNotice is { } notice && OnboardingReliabilityInfoBar is not null)
        {
            OnboardingReliabilityInfoBar.Title = notice.Title;
            OnboardingReliabilityInfoBar.Message = notice.Message;
            OnboardingReliabilityInfoBar.IsOpen = true;
        }

        RenderOnboarding();
    }

    private void EnsureWhatsNewPageLoaded()
    {
        if (_whatsNewLoaded)
        {
            return;
        }

        _whatsNewPageView = new WhatsNewPageView(this);
        Grid.SetRow(_whatsNewPageView, 1);
        MainContentGrid.Children.Add(_whatsNewPageView);
        _whatsNewLoaded = WhatsNewPage is not null;
        if (!_whatsNewLoaded)
        {
            return;
        }

        if (WhatsNewBuildInfoText is not null)
        {
            WhatsNewBuildInfoText.Text = _buildInfoText;
        }
    }

    private void EnsureDictionaryPageLoaded()
    {
        if (_dictionaryLoaded)
        {
            return;
        }

        _dictionaryPageView = new DictionaryPageView(this);
        Grid.SetRow(_dictionaryPageView, 1);
        MainContentGrid.Children.Add(_dictionaryPageView);
        _dictionaryLoaded = DictionaryPage is not null;
        if (!_dictionaryLoaded)
        {
            return;
        }

        ResetMatchStrictness();
        WireDictionaryPageAutoSave();
        ApplySettingsToDictionaryPageControls();
        RefreshVocabularyPacksPanel();
    }

    private void EnsureSnippetsPageLoaded()
    {
        if (_snippetsLoaded)
        {
            return;
        }

        _snippetsPageView = new SnippetsPageView(this);
        Grid.SetRow(_snippetsPageView, 1);
        MainContentGrid.Children.Add(_snippetsPageView);
        _snippetsLoaded = SnippetsPage is not null;
        if (!_snippetsLoaded)
        {
            return;
        }

        if (SnippetList is not null)
        {
            SnippetList.ItemsSource = _snippetRows;
        }

        RefreshSnippetKeywordViews(force: true);
        UpdateSelectionDependentButtons();
        _snippetsViewDirty = true;
    }

    private void EnsureSettingsPageLoaded()
    {
        if (_settingsLoaded)
        {
            return;
        }

        _settingsPageView = new SettingsPageView(this);
        Grid.SetRow(_settingsPageView, 1);
        MainContentGrid.Children.Add(_settingsPageView);
        _settingsLoaded = SettingsPage is not null;
        if (!_settingsLoaded)
        {
            return;
        }

        UppercaseSectionEyebrows();

        if (PolishProviderComboBox is not null)
        {
            PolishProviderComboBox.ItemsSource = PolishProviderChoices;
        }

        if (DiagnosticRetentionDaysBox is not null)
        {
            // Whole days only: FractionDigits without IncrementNumberRounder leaves fractional displays intact.
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
        }

        // A keybind field waiting for a keystroke must notify the global hook on focus changes.
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

        foreach (var list in ChoiceLists())
        {
            if (list is not null)
            {
                list.AddHandler(
                    UIElement.KeyDownEvent,
                    new KeyEventHandler(ChoiceListKeyDown),
                    handledEventsToo: true);
            }
        }

        InitializeChimeCards();
        SetTranscribeFileStep(1);
        InitializePillPreview();
        WireSettingsPageAutoSave();

        if (BuildInfoText is not null)
        {
            BuildInfoText.Text = _buildInfoText;
        }

        ApplySettingsToSettingsPageControls();
        ReplayCachedSettingsPageSnapshots();
    }

    private void ReplayCachedSettingsPageSnapshots()
    {
        if (RunSetupAgainButton is not null)
        {
            RunSetupAgainButton.IsEnabled = !_dictationActive;
        }

        foreach (var button in _chimePlayButtons.Values)
        {
            button.IsEnabled = _currentOverlayState != DictationOverlayState.Recording;
        }

        if (_microphones.Length > 0 && MicrophoneComboBox is not null)
        {
            _isRefreshingMicrophones = true;
            try
            {
                MicrophoneComboBox.ItemsSource = _microphones;
                MicrophoneComboBox.SelectedItem = _selectedMicrophoneChoice ?? _microphones[0];
            }
            finally
            {
                _isRefreshingMicrophones = false;
            }
        }

        if (_latestMicrophoneReadiness is { } readiness)
        {
            ApplyMicrophoneReadinessToControls(readiness, _latestMicrophoneConsent);
        }

        if (_hasSpeedCheckResult)
        {
            SetSpeedCheckResult(_latestSpeedCheckSummary);
        }
        else
        {
            SetSpeedCheckAvailability(_currentOverlayState != DictationOverlayState.Recording);
        }

        if (_updateInstallInProgress)
        {
            SetUpdateInstallInProgress();
        }
        else if (_updateCheckInProgress)
        {
            SetUpdateCheckInProgress();
        }
        else if (_latestUpdateResult is { } updateResult)
        {
            SetUpdateStatus(updateResult);
        }
        else
        {
            if (UpdateStatusText is not null)
            {
                SetLiveText(
                    UpdateStatusText,
                    _storeInstalled
                        ? $"Version {_installedVersionDisplay} is installed from the Microsoft Store."
                        : NotFromStoreSentence);
            }

            if (CheckForUpdatesButton is not null)
            {
                CheckForUpdatesButton.IsEnabled = _storeInstalled;
            }
        }

        if (_startupOperationInProgress)
        {
            SetStartupRegistrationInProgress();
        }
        else if (_latestStartupRegistration is { } startupResult)
        {
            SetStartupRegistration(startupResult);
        }

        RenderSpeechEngineCards();
        UpdateActiveEngineDisplay();
        RefreshLivePreviewSectionPresentation();
        UpdateActivePolishProviderDisplay();
        UpdateEgOneCards();

        if (_hasGraphicsRuntimeSnapshot)
        {
            SetGraphicsRuntimeDelivery(_latestGraphicsRuntimeDelivery);
        }

        if (!string.IsNullOrEmpty(_latestSpeechEngineNotice))
        {
            ShowSpeechEngineNotice(_latestSpeechEngineNotice);
        }

        if (!string.IsNullOrEmpty(_latestEgOneNotice))
        {
            ShowEgOneNotice(_latestEgOneNotice);
        }

        if (OllamaNoticeText is not null)
        {
            var effectiveNotice = HistoryPlaybackPresentation.ResolveOllamaNoticeDisplay(
                _ollamaPolishNotice,
                _lastOllamaModelsNotice);
            SetLiveRegion(
                OllamaNoticeText,
                effectiveNotice ?? string.Empty,
                effectiveNotice is null ? Visibility.Collapsed : Visibility.Visible);
        }

        if (ApiKeyPasswordBox is not null)
        {
            ApiKeyPasswordBox.Password = string.Empty;
        }

        RefreshApiKeyStatus();
        _ = RefreshPolishModelChoicesAsync(
            _settings.Preferences.Polish.Provider,
            chooseDefault: false);
    }

    private void ClearOnboardingReliabilityNotice()
    {
        _pendingOnboardingReliabilityNotice = null;
        if (OnboardingReliabilityInfoBar is not null)
        {
            OnboardingReliabilityInfoBar.IsOpen = false;
        }
    }
}
