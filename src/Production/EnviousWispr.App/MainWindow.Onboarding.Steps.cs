using System.Globalization;
using System.Runtime.InteropServices;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Onboarding;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI.ViewManagement;

namespace EnviousWispr.App;

/// <summary>13-screen Fluent 2 onboarding walkthrough, checkpoint resume, key rehearsal, and dual model delivery.</summary>
public sealed partial class MainWindow
{
    internal void OnboardingSuggestedLanguageButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_suggestedSystemLanguageCode))
        {
            return;
        }

        SelectOnboardingLanguage(_suggestedSystemLanguageCode);
    }

    internal void OnboardingDetectAutoCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_onboardingIsUpdatingLanguageUi)
        {
            return;
        }

        SelectOnboardingLanguage(LanguageCatalog.AutomaticCode);
    }

    internal void OnboardingLanguageSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshOnboardingLanguageList();

    internal void OnboardingLanguageSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        if (OnboardingLanguageListView.ItemsSource is IReadOnlyList<OnboardingLanguageRow> visible && visible.Count == 1)
        {
            SelectOnboardingLanguage(visible[0].Code);
            e.Handled = true;
        }
    }

    internal void OnboardingLanguageListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OnboardingLanguageRow row)
        {
            SelectOnboardingLanguage(row.Code);
        }
    }

    private void SelectOnboardingLanguage(string? languageCode)
    {
        var rec = ComputeOnboardingRecommendation(languageCode);
        _onboardingRecommendation = rec;
        _onboarding.SelectLanguage(languageCode, rec);
        SyncLiveInventoryToOnboarding();
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    internal void OnboardingOpenSpeechCompareButton_Click(object sender, RoutedEventArgs e)
    {
        _onboarding.OpenSpeechCompare();
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    internal void OnboardingSpeechCompareCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_onboardingIsUpdatingCards || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        var engine = string.Equals(tag, "Whisper", StringComparison.Ordinal)
            ? FinalAsrEngine.Whisper
            : FinalAsrEngine.Parakeet;
        var modelId = engine == FinalAsrEngine.Whisper ? WhisperModelIds.Final : ParakeetModelIds.Final;
        _onboarding.SelectSpeech(engine, modelId);
        SyncLiveInventoryToOnboarding();
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    internal void OnboardingSpeechCompareGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveTwoColumnGrid(
            OnboardingSpeechCompareGrid,
            e.NewSize.Width,
            600,
            OnboardingSpeechCompareCol1,
            OnboardingWhisperCompareCard);

    internal void OnboardingPolishPickGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveTwoColumnGrid(
            OnboardingPolishPickGrid,
            e.NewSize.Width,
            600,
            OnboardingPolishPickCol1,
            OnboardingPolishSecondaryCard);

    internal void OnboardingSeeAllEgOneGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveThreeCardGridLayout(
            OnboardingSeeAllEgOneGrid,
            e.NewSize.Width,
            stackThreshold: 640,
            [OnboardingSeeAllEgOneFullCard, OnboardingSeeAllEgOneMiniCard, OnboardingSeeAllEgOneMicroCard]);

    private static void ApplyResponsiveTwoColumnGrid(
        Grid grid,
        double width,
        double stackThreshold,
        ColumnDefinition secondColumn,
        FrameworkElement secondCard)
    {
        if (grid is null || secondColumn is null || secondCard is null)
        {
            return;
        }

        if (width > 0 && width < stackThreshold)
        {
            secondColumn.Width = new GridLength(0);
            Grid.SetRow(secondCard, 1);
            Grid.SetColumn(secondCard, 0);
        }
        else
        {
            secondColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(secondCard, 0);
            Grid.SetColumn(secondCard, 1);
        }
    }

    internal void OnboardingPolishPrimaryCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_onboardingIsUpdatingCards || sender is not RadioButton { Tag: string modelId })
        {
            return;
        }

        _onboarding.SelectPolish(OnboardingPolishChoiceKind.EgOne, EgOneModelCatalog.NormalizeOrDefault(modelId));
        SyncLiveInventoryToOnboarding();
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    internal void OnboardingOpenPolishSeeAllButton_Click(object sender, RoutedEventArgs e)
    {
        _onboardingSeeAllCloudTab = TryGetCloudProvider(_onboarding.Choices.PolishChoice, out _);
        _onboarding.OpenPolishSeeAll();
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    internal async void OnboardingSkipPolish_Click(object sender, RoutedEventArgs e)
    {
        _onboarding.SelectPolish(OnboardingPolishChoiceKind.None);
        SyncLiveInventoryToOnboarding();
        await AdvanceFromPolishChoiceAsync().ConfigureAwait(true);
    }

    internal void OnboardingPolishLowSpecCloudButton_Click(object sender, RoutedEventArgs e)
    {
        _onboardingSeeAllCloudTab = true;
        _onboarding.OpenPolishSeeAll();
        SelectOnboardingCloudProvider(_onboardingCloudProvider);
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    internal void OnboardingPolishTabLocalButton_Click(object sender, RoutedEventArgs e)
    {
        _onboardingCloudCheckGeneration++;
        OnboardingCloudApiKeyBox.Password = string.Empty;
        _onboardingSeeAllCloudTab = false;
        if (_onboarding.Choices.PolishChoice != OnboardingPolishChoiceKind.EgOne)
        {
            var defaultEgOne = _onboardingRecommendation?.Polish.PrimaryModelId ?? EgOneModelCatalog.MiniId;
            _onboarding.SelectPolish(OnboardingPolishChoiceKind.EgOne, defaultEgOne);
            SyncLiveInventoryToOnboarding();
            QueueOnboardingCheckpointSave();
        }

        RenderOnboarding();
    }

    internal void OnboardingPolishTabCloudButton_Click(object sender, RoutedEventArgs e)
    {
        _onboardingSeeAllCloudTab = true;
        SelectOnboardingCloudProvider(_onboardingCloudProvider);
        RenderOnboarding();
    }

    internal void OnboardingSeeAllEgOneCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_onboardingIsUpdatingCards || sender is not RadioButton { Tag: string modelId })
        {
            return;
        }

        _onboardingCloudCheckGeneration++;
        _onboardingSeeAllCloudTab = false;
        _onboarding.SelectPolish(OnboardingPolishChoiceKind.EgOne, EgOneModelCatalog.NormalizeOrDefault(modelId));
        SyncLiveInventoryToOnboarding();
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    internal void OnboardingCloudProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var provider = tag switch
        {
            "Gemini" => PolishProvider.Gemini,
            "Claude" => PolishProvider.Anthropic,
            _ => PolishProvider.OpenAI,
        };
        SelectOnboardingCloudProvider(provider);
        RenderOnboarding();
    }

    private void SelectOnboardingCloudProvider(PolishProvider provider)
    {
        if (_onboardingCloudProvider != provider)
        {
            OnboardingCloudApiKeyBox.Password = string.Empty;
        }

        _onboardingCloudCheckGeneration++;
        _onboardingCloudProvider = provider;
        var kind = ToOnboardingPolishKind(provider);
        var defaultModel = _session.Provider.RecommendedModel(provider) ?? GetDefaultCloudModelFallback(provider);
        var currentModel = _onboarding.Choices.PolishChoice == kind &&
            !string.IsNullOrWhiteSpace(_onboarding.Choices.CloudModelId)
            ? _onboarding.Choices.CloudModelId
            : defaultModel;
        _onboarding.SelectPolish(kind, cloudModelId: currentModel);
        SyncLiveInventoryToOnboarding();
        QueueOnboardingCheckpointSave();
        PopulateOnboardingCloudModels(provider, new[] { defaultModel }, currentModel);
        RefreshOnboardingCloudKeyStatusText(provider);
        OnboardingCloudCheckKeyButton.IsEnabled = !string.IsNullOrWhiteSpace(OnboardingCloudApiKeyBox.Password) ||
            _session.Provider.KeyStatus(provider) == ApiKeyReadStatus.Found;
    }

    private static OnboardingPolishChoiceKind ToOnboardingPolishKind(PolishProvider provider) => provider switch
    {
        PolishProvider.Gemini => OnboardingPolishChoiceKind.Gemini,
        PolishProvider.Anthropic => OnboardingPolishChoiceKind.Claude,
        _ => OnboardingPolishChoiceKind.OpenAI,
    };

    private static bool TryGetCloudProvider(OnboardingPolishChoiceKind kind, out PolishProvider provider)
    {
        switch (kind)
        {
            case OnboardingPolishChoiceKind.OpenAI:
                provider = PolishProvider.OpenAI;
                return true;
            case OnboardingPolishChoiceKind.Claude:
                provider = PolishProvider.Anthropic;
                return true;
            case OnboardingPolishChoiceKind.Gemini:
                provider = PolishProvider.Gemini;
                return true;
            default:
                provider = PolishProvider.None;
                return false;
        }
    }

    private static string GetDefaultCloudModelFallback(PolishProvider provider) => provider switch
    {
        PolishProvider.Anthropic => "claude-haiku-4-5",
        PolishProvider.Gemini => "gemini-2.5-flash-lite",
        _ => "gpt-4o-mini",
    };

    private void PopulateOnboardingCloudModels(
        PolishProvider provider,
        IReadOnlyList<string> discoveredModels,
        string? preferredModelId)
    {
        var items = new List<string>();
        foreach (var candidate in discoveredModels)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !items.Contains(candidate, StringComparer.Ordinal))
            {
                items.Add(candidate);
            }
        }

        var recommended = _session.Provider.RecommendedModel(provider) ?? GetDefaultCloudModelFallback(provider);
        if (!items.Contains(recommended, StringComparer.Ordinal))
        {
            items.Add(recommended);
        }

        if (!string.IsNullOrWhiteSpace(preferredModelId) && !items.Contains(preferredModelId, StringComparer.Ordinal))
        {
            items.Insert(0, preferredModelId);
        }

        _onboardingIsUpdatingCloudModels = true;
        try
        {
            OnboardingCloudModelComboBox.ItemsSource = items;
            var target = !string.IsNullOrWhiteSpace(preferredModelId) ? preferredModelId : recommended;
            OnboardingCloudModelComboBox.SelectedIndex = Math.Max(0, items.IndexOf(target));
        }
        finally
        {
            _onboardingIsUpdatingCloudModels = false;
        }
    }

    private void RefreshOnboardingCloudKeyStatusText(PolishProvider provider)
    {
        var status = _session.Provider.KeyStatus(provider);
        var message = status switch
        {
            ApiKeyReadStatus.Found => $"{ProviderDisplayName(provider)} API key is saved in Windows Credential Manager.",
            ApiKeyReadStatus.Unavailable => "Windows Credential Manager is unavailable right now.",
            _ => $"Paste your {ProviderDisplayName(provider)} API key and click Check my key, or choose a model below.",
        };
        SetLiveText(OnboardingCloudKeyStatusText, message);
    }

    internal void OnboardingCloudApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var hasText = !string.IsNullOrWhiteSpace(OnboardingCloudApiKeyBox.Password);
        var hasStored = _session.Provider.KeyStatus(_onboardingCloudProvider) == ApiKeyReadStatus.Found;
        OnboardingCloudCheckKeyButton.IsEnabled = hasText || hasStored;
    }

    internal async void OnboardingCloudCheckKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = _onboardingCloudProvider;
        var enteredKey = OnboardingCloudApiKeyBox.Password;
        if (!string.IsNullOrWhiteSpace(enteredKey))
        {
            var saved = _session.Provider.SaveKey(provider, enteredKey);
            if (saved == ApiKeySaveOutcome.Saved)
            {
                OnboardingCloudApiKeyBox.Password = string.Empty;
            }
            else if (saved == ApiKeySaveOutcome.StorageUnavailable)
            {
                SetLiveText(OnboardingCloudKeyStatusText, "Windows Credential Manager could not store the key.");
                return;
            }
        }

        if (_session.Provider.KeyStatus(provider) != ApiKeyReadStatus.Found)
        {
            SetLiveText(OnboardingCloudKeyStatusText, $"Enter a {ProviderDisplayName(provider)} API key first.");
            OnboardingCloudApiKeyBox.Focus(FocusState.Programmatic);
            return;
        }

        var generation = ++_onboardingCloudCheckGeneration;
        SetLiveText(OnboardingCloudKeyStatusText, $"Checking {ProviderDisplayName(provider)} key...");
        OnboardingCloudCheckKeyButton.IsEnabled = false;
        try
        {
            var listing = await _session.Provider.ListModelsAsync(provider, null).ConfigureAwait(true);
            if (listing is null ||
                _session.Closing ||
                _onboarding.Step != OnboardingStep.PolishSeeAll ||
                !_onboardingSeeAllCloudTab ||
                _onboardingCloudProvider != provider ||
                generation != _onboardingCloudCheckGeneration)
            {
                return;
            }

            if (listing.Models.Count > 0)
            {
                var selected = OnboardingCloudModelComboBox.SelectedItem as string
                    ?? _onboarding.Choices.CloudModelId
                    ?? listing.Models[0];
                PopulateOnboardingCloudModels(provider, listing.Models, selected);
                _onboarding.SelectPolish(ToOnboardingPolishKind(provider), cloudModelId: selected);
                QueueOnboardingCheckpointSave();
            }

            if (listing.Discovery is { } discovery)
            {
                SetLiveText(OnboardingCloudKeyStatusText, ModelDiscoveryNotice(provider, discovery));
            }
        }
        finally
        {
            var canCheck = !string.IsNullOrWhiteSpace(OnboardingCloudApiKeyBox.Password) ||
                _session.Provider.KeyStatus(_onboardingCloudProvider) == ApiKeyReadStatus.Found;
            OnboardingCloudCheckKeyButton.IsEnabled = canCheck;
        }
    }

    internal async void OnboardingCloudClearKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = _onboardingCloudProvider;
        if (!string.IsNullOrEmpty(OnboardingCloudApiKeyBox.Password))
        {
            OnboardingCloudApiKeyBox.Password = string.Empty;
            RefreshOnboardingCloudKeyStatusText(provider);
            OnboardingCloudCheckKeyButton.IsEnabled = _session.Provider.KeyStatus(provider) == ApiKeyReadStatus.Found;
            return;
        }

        if (_session.Provider.CheckKeyRemoval(provider) == ApiKeyRemovalCheck.Confirm)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = WindowRoot.XamlRoot,
                Title = $"Remove the stored {ProviderDisplayName(provider)} key?",
                Content = "This deletes the EnviousWispr credential from Windows Credential Manager. You can add one again anytime.",
                PrimaryButtonText = "Remove key",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
                _session.Closing ||
                _onboardingCloudProvider != provider)
            {
                return;
            }

            _ = _session.Provider.RemoveKey(provider);
        }

        RefreshOnboardingCloudKeyStatusText(provider);
        OnboardingCloudCheckKeyButton.IsEnabled = _session.Provider.KeyStatus(provider) == ApiKeyReadStatus.Found;
    }

    internal void OnboardingCloudModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_onboardingIsUpdatingCloudModels || OnboardingCloudModelComboBox.SelectedItem is not string modelId)
        {
            return;
        }

        _onboarding.SelectPolish(ToOnboardingPolishKind(_onboardingCloudProvider), cloudModelId: modelId);
        QueueOnboardingCheckpointSave();
    }

    private async Task AdvanceFromPolishChoiceAsync()
    {
        if (ProbeOnboardingModelsInstalled is { } probeModels)
        {
            var egOneTarget = _onboarding.RequiresLocalEgOne
                ? EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId)
                : null;
            _ = await probeModels(_onboarding.Choices.SpeechEngine, egOneTarget).ConfigureAwait(true);
        }

        SyncLiveInventoryToOnboarding();
        var speechInstalled = _onboarding.Choices.SpeechEngine == FinalAsrEngine.Whisper
            ? _whisperModelState.IsInstalled
            : _parakeetModelState.IsInstalled;
        var polishInstalled = !_onboarding.RequiresLocalEgOne ||
            ResolveEgOneDisplayState(EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId)).IsInstalled;
        var needsDownload = !speechInstalled || !polishInstalled;

        _onboarding.ContinueFromPolishPick(downloadsNeededOverride: needsDownload);
        QueueOnboardingCheckpointSave();

        if (_onboarding.Step == OnboardingStep.Downloads)
        {
            StartOnboardingDownloadsIfNeeded();
        }
        else if (ApplyOnboardingEnginesChoice is { } applyEngines)
        {
            await applyEngines(
                _onboarding.Choices.SpeechEngine,
                _onboarding.Choices.PolishChoice,
                _onboarding.Choices.EgOneModelId,
                _onboarding.Choices.CloudModelId).ConfigureAwait(true);
        }

        RenderOnboarding();
    }

    private void StartOnboardingDownloadsIfNeeded()
    {
        var speechInstalled = _onboarding.Choices.SpeechEngine == FinalAsrEngine.Whisper
            ? _whisperModelState.IsInstalled
            : _parakeetModelState.IsInstalled;
        var polishInstalled = !_onboarding.RequiresLocalEgOne ||
            ResolveEgOneDisplayState(EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId)).IsInstalled;
        if (speechInstalled && polishInstalled)
        {
            return;
        }

        var speechModelId = _onboarding.Choices.SpeechModelId;
        var polishModelId = _onboarding.RequiresLocalEgOne
            ? EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId)
            : null;
        var requestedPair = (speechModelId, polishModelId);
        if (!_onboarding.NeedsDownloadStart &&
            _onboardingDownloadSnapshot?.IsRunning == true &&
            _activeRequestedDownloadPair == requestedPair)
        {
            return;
        }

        _onboarding.DownloadStarted();
        _activeRequestedDownloadPair = requestedPair;
        var rec = _onboardingRecommendation ?? ComputeOnboardingRecommendation(_onboarding.Choices.LanguageCode);
        if (OnboardingDualDownloadRequested is not null)
        {
            RequestOnboardingDualDownload(speechModelId, polishModelId, rec.Polish.ParallelDownloads);
        }
        else
        {
            ModelDownloadRequested?.Invoke();
        }
    }

    internal void OnboardingDownloadCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _onboarding.RequestCancel();
        CancelOnboardingDualDownload();
        RenderOnboarding();
    }

    internal void OnboardingDownloadRetryButton_Click(object sender, RoutedEventArgs e)
    {
        _onboarding.RetrySetup();
        if (_onboarding.Model == SpeechModelDelivery.Installed &&
            _onboarding.Engine != SpeechEngineReadiness.Ready &&
            _settings.Preferences.Dictation.FinalEngine != _onboarding.Choices.SpeechEngine &&
            _onboardingDownloadSnapshot?.IsRunning != true &&
            ApplyOnboardingEnginesChoice is { } applySelected)
        {
            _ = applySelected(
                _onboarding.Choices.SpeechEngine,
                _onboarding.Choices.PolishChoice,
                _onboarding.Choices.EgOneModelId,
                _onboarding.Choices.CloudModelId);
        }
        else if (_onboarding.Engine == SpeechEngineReadiness.Unavailable && _onboarding.Model == SpeechModelDelivery.Installed)
        {
            TranscriptionRetryRequested?.Invoke();
        }
        else if (_onboardingDownloadSnapshot is null)
        {
            StartOnboardingDownloadsIfNeeded();
        }
        else
        {
            RetryOnboardingDualDownload();
        }

        RenderOnboarding();
    }

    internal void OnboardingSkipPolishAndAdvance_Click(object sender, RoutedEventArgs e)
    {
        SkipOnboardingPolishDownload();
        _onboarding.SelectPolish(OnboardingPolishChoiceKind.None);
        SyncLiveInventoryToOnboarding();
        if (_onboarding.Step == OnboardingStep.Downloads)
        {
            _onboarding.ContinueFromDownloads();
        }
        else if (_onboarding.Step == OnboardingStep.GettingReady)
        {
            _ = _onboarding.TryAdvanceFromGate();
        }

        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    private bool TrySkipPolishFromGettingReady()
    {
        if (_onboarding.MayFinish(OnboardingExit.SkipAheadWithoutPolish))
        {
            SkipOnboardingPolishDownload();
            if (_onboarding.SkipAheadWithoutPolish())
            {
                _onboarding.SelectPolish(OnboardingPolishChoiceKind.None);
                SyncLiveInventoryToOnboarding();
                return true;
            }
        }
        else if (_onboarding.MayFinish(OnboardingExit.ContinueWithoutPolish))
        {
            SkipOnboardingPolishDownload();
            if (_onboarding.ContinueWithoutAiPolish())
            {
                _onboarding.SelectPolish(OnboardingPolishChoiceKind.None);
                SyncLiveInventoryToOnboarding();
                return true;
            }
        }

        return false;
    }

    internal void OnboardingGateSkipPolishButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySkipPolishFromGettingReady())
        {
            OnboardingSkipPolishAndAdvance_Click(sender, e);
            return;
        }

        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    internal async void OnboardingRefreshMicButton_Click(object sender, RoutedEventArgs e) =>
        await LoadMicrophonesAsync().ConfigureAwait(true);

    internal void OnboardingKeycapButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingKeyCaptureSession is not null)
        {
            CancelOnboardingKeyCapture();
            RenderOnboarding();
            return;
        }

        StartOnboardingKeyCapture();
    }

    internal async void OnboardingPresetHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string preset })
        {
            return;
        }

        CancelOnboardingKeyCapture();
        await ApplyOnboardingHotkeySelectionAsync(preset).ConfigureAwait(true);
    }

    private void StartOnboardingKeyCapture()
    {
        CancelOnboardingKeyCapture();
        KeybindCaptureActiveChanged?.Invoke(true);
        OnboardingKeyRehearsalActiveChanged?.Invoke(false);
        SetLiveText(OnboardingKeyStatusText, "Press your new dictation shortcut (press Esc to cancel)...");

        try
        {
            _onboardingKeyCaptureSession = new WindowsKeybindCaptureSession(
                allowModifierOnly: true,
                allowEscapeAsBinding: false,
                allowClear: false,
                onCommitted: async gestureText =>
                {
                    StopOnboardingKeyCaptureSession();
                    KeybindCaptureActiveChanged?.Invoke(false);
                    await ApplyOnboardingHotkeySelectionAsync(gestureText).ConfigureAwait(true);
                },
                onCancelled: () =>
                {
                    CancelOnboardingKeyCapture();
                    SetLiveText(OnboardingKeyStatusText, string.Empty);
                    RenderOnboarding();
                },
                onInvalidAttempt: hint => SetLiveText(OnboardingKeyStatusText, hint),
                dispatchToUi: action => DispatcherQueue.TryEnqueue(() => action()));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            CancelOnboardingKeyCapture();
            SetLiveText(OnboardingKeyStatusText, "Could not start shortcut capture. Pick a preset or try again.");
        }

        RenderOnboarding();
    }

    private void CancelOnboardingKeyCapture()
    {
        if (_onboardingKeyCaptureSession is null)
        {
            return;
        }

        StopOnboardingKeyCaptureSession();
        KeybindCaptureActiveChanged?.Invoke(false);
    }

    private void StopOnboardingKeyCaptureSession()
    {
        _onboardingKeyCaptureSession?.Dispose();
        _onboardingKeyCaptureSession = null;
    }

    private async Task ApplyOnboardingHotkeySelectionAsync(string gestureText)
    {
        var parsed = HotkeyGestureParser.Parse(gestureText);
        if (!parsed.Succeeded || parsed.Gesture is not { } parsedGesture)
        {
            SetLiveText(OnboardingKeyStatusText, "Use a supported shortcut such as Ctrl+Win, RightCtrl, or F8.");
            return;
        }

        var canonical = parsedGesture.ToString()!;
        var mode = DictationRecordingMode.PushToTalk;

        if (ApplyOnboardingRecordingKey is not null)
        {
            var (applied, error) = await ApplyOnboardingRecordingKey(canonical, mode).ConfigureAwait(true);
            if (!applied)
            {
                SetLiveText(OnboardingKeyStatusText, error ?? "Could not switch shortcut right now.");
                return;
            }
        }

        _onboardingHoldRehearsed = false;
        _onboardingDoubleTapRehearsed = false;
        _onboardingRehearsalKeyDown = false;
        _onboardingLastTapUpUtc = null;
        _onboarding.ReportRecordingKey(canonical, mode);
        SetLiveText(OnboardingKeyStatusText, $"Shortcut set to {canonical}. Test holding or double-pressing it below.");
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    internal void OnboardingView_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (OnboardingView.Visibility != Visibility.Visible ||
            _onboarding.Step != OnboardingStep.Key ||
            _onboardingKeyCaptureSession is not null)
        {
            return;
        }

        if (!MatchesCurrentOnboardingHotkey(e.Key))
        {
            return;
        }

        if (!_onboardingRehearsalKeyDown)
        {
            _onboardingRehearsalKeyDown = true;
            _onboardingHoldRehearsed = true;
            _onboardingKeyHoldTimer.Tick -= OnOnboardingKeyHoldTimerTick;
            _onboardingKeyHoldTimer.Tick += OnOnboardingKeyHoldTimerTick;
            _onboardingKeyHoldTimer.Start();

            if (_onboardingLastTapUpUtc is { } lastTap &&
                DateTimeOffset.UtcNow - lastTap <= RehearsalDoubleTapWindow)
            {
                _onboardingDoubleTapRehearsed = true;
            }

            _onboarding.MarkKeyRehearsalCompleted();
            RenderOnboarding();
        }
    }

    internal void OnboardingView_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (OnboardingView.Visibility != Visibility.Visible ||
            _onboarding.Step != OnboardingStep.Key ||
            _onboardingKeyCaptureSession is not null ||
            !_onboardingRehearsalKeyDown)
        {
            return;
        }

        if (MatchesCurrentOnboardingHotkeyKeyUp(e.Key))
        {
            _onboardingRehearsalKeyDown = false;
            _onboardingKeyHoldTimer.Stop();
            _onboardingLastTapUpUtc = DateTimeOffset.UtcNow;
            RenderOnboarding();
        }
    }

    private void OnOnboardingKeyHoldTimerTick(object? sender, object e)
    {
        _onboardingKeyHoldTimer.Stop();
        if (_onboardingRehearsalKeyDown && _onboarding.Step == OnboardingStep.Key)
        {
            _onboardingHoldRehearsed = true;
            _onboarding.MarkKeyRehearsalCompleted();
            RenderOnboarding();
        }
    }

    private bool MatchesCurrentOnboardingHotkey(VirtualKey key)
    {
        var text = _onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? DictationPreferences.Default.PushToTalkGesture;
        var parsed = HotkeyGestureParser.Parse(text);
        if (!parsed.Succeeded || parsed.Gesture is not { } gesture)
        {
            return false;
        }

        var currentModifiers = HeldModifierSet() | ModifierFlagOf(key);
        if (string.IsNullOrEmpty(gesture.Key))
        {
            return currentModifiers.HasFlag(gesture.Modifiers);
        }

        if (gesture.Modifiers == HotkeyModifiers.None)
        {
            return MatchesNamedGestureKey(key, gesture.Key);
        }

        return (currentModifiers & gesture.Modifiers) == gesture.Modifiers &&
            MatchesNamedGestureKey(key, gesture.Key);
    }

    private bool MatchesCurrentOnboardingHotkeyKeyUp(VirtualKey key)
    {
        var text = _onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? DictationPreferences.Default.PushToTalkGesture;
        var parsed = HotkeyGestureParser.Parse(text);
        if (!parsed.Succeeded || parsed.Gesture is not { } gesture)
        {
            return true;
        }

        if (string.IsNullOrEmpty(gesture.Key))
        {
            return ModifierFlagOf(key) != HotkeyModifiers.None;
        }

        return MatchesNamedGestureKey(key, gesture.Key);
    }

    private static bool MatchesNamedGestureKey(VirtualKey key, string gestureKey) => gestureKey switch
    {
        "LeftCtrl" or "RightCtrl" => key == VirtualKey.Control || key == VirtualKey.LeftControl || key == VirtualKey.RightControl,
        "LeftShift" or "RightShift" => key == VirtualKey.Shift || key == VirtualKey.LeftShift || key == VirtualKey.RightShift,
        "LeftAlt" or "RightAlt" => key == VirtualKey.Menu || key == VirtualKey.LeftMenu || key == VirtualKey.RightMenu,
        "LeftWin" => key == VirtualKey.LeftWindows,
        "RightWin" => key == VirtualKey.RightWindows,
        "Space" => key == VirtualKey.Space,
        "Escape" => key == VirtualKey.Escape,
        _ => Enum.TryParse<VirtualKey>(gestureKey, ignoreCase: true, out var mapped) && mapped == key,
    };

    internal void OnboardingPracticeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _onboarding.PracticeTextChanged(OnboardingPracticeBox.Text);
        if (_onboarding.PracticeSucceeded)
        {
            QueueOnboardingCheckpointSave();
        }

        RenderOnboarding();
    }

    internal void OnboardingClosingCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _onboardingClosingPaused = true;
        _onboardingBeat.Stop();
    }

    internal void OnboardingClosingCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _onboardingClosingPaused = false;
        ArmClosingBeatTimer();
    }

    private void OnboardingClosingDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int beatIndex })
        {
            return;
        }

        _onboardingClosingPaused = true;
        _onboardingBeat.Stop();
        _onboarding.SetClosingBeat(beatIndex);
        RenderOnboarding();
    }

    internal void OnboardingClosingWatchAgainButton_Click(object sender, RoutedEventArgs e)
    {
        _onboardingClosingPaused = false;
        _onboarding.RestartClosing();
        RenderOnboarding();
        ArmClosingBeatTimer();
    }
}
