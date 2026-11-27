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
    private void RenderOnboarding()
    {
        if (!IsOnboardingLoaded || OnboardingView is not { Visibility: Visibility.Visible })
        {
            StopOnboardingTimers();
            OnboardingKeyRehearsalActiveChanged?.Invoke(false);
            return;
        }

        EnsureOnboardingChromeBuilt();
        var step = _onboarding.Step;
        var screenChanged = _drawnStep != step;
        if (screenChanged && _drawnStep == OnboardingStep.Key)
        {
            CancelOnboardingKeyCapture();
            _onboardingRehearsalKeyDown = false;
            _onboardingKeyHoldTimer.Stop();
        }

        OnboardingKeyRehearsalActiveChanged?.Invoke(
            step == OnboardingStep.Key && _onboardingKeyCaptureSession is null);

        if (screenChanged && step == OnboardingStep.Practice && _dictationActive && !_onboarding.TakeInFlight)
        {
            _onboarding.TakeStarted(OnboardingPracticeBox.Text);
        }

        if (screenChanged && step == OnboardingStep.Closing)
        {
            ArmClosingBeatTimer();
        }
        else if (step != OnboardingStep.Closing)
        {
            _onboardingBeat.Stop();
        }

        ArmMicrophonePoll();
        DrawOnboardingChrome(step);
        DrawOnboardingStepHeader(step);
        SetVisibleOnboardingPanels(step);

        var focusedBefore = FocusedOnboardingControl();
        switch (step)
        {
            case OnboardingStep.Welcome:
                UpdateOnboardingLipsMotion();
                break;
            case OnboardingStep.Language:
                DrawOnboardingLanguage();
                break;
            case OnboardingStep.SpeechPick:
                DrawOnboardingSpeechPick();
                break;
            case OnboardingStep.SpeechCompare:
                DrawOnboardingSpeechCompare();
                break;
            case OnboardingStep.PolishPick:
                DrawOnboardingPolishPick();
                break;
            case OnboardingStep.PolishSeeAll:
                DrawOnboardingPolishSeeAll();
                break;
            case OnboardingStep.Downloads:
                DrawOnboardingDownloads();
                break;
            case OnboardingStep.Permissions:
                DrawOnboardingPermissions();
                break;
            case OnboardingStep.Key:
                DrawOnboardingKey();
                break;
            case OnboardingStep.GettingReady:
                DrawOnboardingGettingReady();
                break;
            case OnboardingStep.Practice:
                DrawOnboardingPractice();
                break;
            case OnboardingStep.Closing:
                DrawOnboardingClosing();
                break;
        }

        DrawOnboardingFooter(step);

        if (!screenChanged)
        {
            RescueOnboardingFocus(focusedBefore);
        }

        if (screenChanged)
        {
            _drawnStep = step;
            _drawnPractice = _onboarding.PracticeState;
            _drawnGate = _onboarding.EvaluateGate();
            OnboardingScroll.ChangeView(null, 0, null, disableAnimation: true);
            FocusOnboardingScreen(step);
        }
        else if (step == OnboardingStep.Practice && _drawnPractice != _onboarding.PracticeState)
        {
            _drawnPractice = _onboarding.PracticeState;
            Announce($"{OnboardingTitle.Text}. {OnboardingSubtitle.Text}");
        }
        else if (step == OnboardingStep.GettingReady && _drawnGate != _onboarding.EvaluateGate())
        {
            _drawnGate = _onboarding.EvaluateGate();
        }
    }

    private void DrawOnboardingChrome(OnboardingStep step)
    {
        var showBack = _onboarding.CanGoBack;
        var showProgress = step is not (OnboardingStep.Welcome or OnboardingStep.Closing or OnboardingStep.Done);
        var pillSummary = _onboardingDownloadSnapshot is null
            ? null
            : OnboardingDownloadCoordinator.FormatBackgroundPillSummary(_onboardingDownloadSnapshot);
        var showDownloadPill = !string.IsNullOrWhiteSpace(pillSummary) &&
            step is not (OnboardingStep.Welcome or OnboardingStep.Downloads);

        OnboardingBackButton.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
        OnboardingProgressGrid.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        OnboardingDownloadPillButton.Visibility = showDownloadPill ? Visibility.Visible : Visibility.Collapsed;
        OnboardingChromeBar.Visibility = showBack || showProgress || showDownloadPill
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (showProgress)
        {
            var currentNum = OnboardingFlow.PrimaryStepNumberFor(step);
            var activeBrush = Application.Current.Resources["BrandAccentSolidBrush"] as Brush;
            var idleBrush = Application.Current.Resources["BrandDividerBrush"] as Brush;
            for (var i = 0; i < _onboardingProgressSegments.Length; i++)
            {
                _onboardingProgressSegments[i].Background = i < currentNum ? activeBrush : idleBrush;
            }
        }

        if (showDownloadPill && pillSummary is not null)
        {
            OnboardingDownloadPillText.Text = pillSummary;
            ToolTipService.SetToolTip(OnboardingDownloadPillButton, pillSummary);
            var isDone = _onboardingDownloadSnapshot is { IsSpeechReady: true, IsPolishReady: true } snap &&
                string.IsNullOrWhiteSpace(snap.FailureMessage);
            OnboardingDownloadPillDot.Fill = Application.Current.Resources[
                isDone ? "BrandSuccessBrush" : "BrandAccentBrush"] as Brush;
        }
    }

    private void DrawOnboardingStepHeader(OnboardingStep step)
    {
        if (step is OnboardingStep.Welcome or OnboardingStep.Closing or OnboardingStep.Done)
        {
            OnboardingStepHeader.Visibility = Visibility.Collapsed;
            return;
        }

        OnboardingStepHeader.Visibility = Visibility.Visible;
        OnboardingStepText.Text = OnboardingCopy.StepOf(step).ToUpperInvariant();
        var (title, subtitle) = step switch
        {
            OnboardingStep.Language => (
                "Which language do you dictate in?",
                "Pick automatic detection or lock a language for higher recognition accuracy."),
            OnboardingStep.SpeechPick => (
                "Choose your speech engine",
                "EnviousWispr tuned this recommendation to your language and PC hardware."),
            OnboardingStep.SpeechCompare => (
                "Compare speech models",
                "Both models run privately on your PC with zero audio leaving this device."),
            OnboardingStep.PolishIntro => (
                "Cleaner writing that still sounds like you",
                "Optional AI polish cleans grammar, filler words, and formatting automatically."),
            OnboardingStep.PolishPick => (
                "Meet EG-1",
                "Our local AI polish model, picked to match your PC memory and graphics."),
            OnboardingStep.PolishSeeAll => (
                "Choose your AI polish option",
                "Pick any local EG-1 size or connect your own OpenAI, Gemini, or Claude key."),
            OnboardingStep.Downloads => (
                "Downloading your models",
                "Your downloads run in the background so you can keep moving through setup."),
            OnboardingStep.Permissions => (
                "Allow microphone access",
                "Windows only asks for your microphone so dictation stays private and ready."),
            OnboardingStep.Key => (
                "Meet your dictation key",
                "Pick the shortcut you will use anywhere on Windows and test it below."),
            OnboardingStep.GettingReady => (
                OnboardingCopy.GateTitle,
                "Waiting for your chosen downloads or microphone check to finish."),
            OnboardingStep.Practice => (
                OnboardingCopy.PracticeHeadline(_onboarding.PracticeState, _onboarding.PracticeSucceeded),
                OnboardingCopy.PracticeSubhead(
                    _onboarding.PracticeState,
                    _onboarding.PracticeSucceeded,
                    _onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? "Ctrl+Win",
                    _onboarding.Choices.RecordingMode,
                    _onboarding.MissedTo)),
            _ => ("Setup", string.Empty),
        };
        OnboardingTitle.Text = title;
        OnboardingSubtitle.Text = subtitle;
    }

    private void SetVisibleOnboardingPanels(OnboardingStep step)
    {
        OnboardingWelcomePanel.Visibility = step == OnboardingStep.Welcome ? Visibility.Visible : Visibility.Collapsed;
        OnboardingLanguagePanel.Visibility = step == OnboardingStep.Language ? Visibility.Visible : Visibility.Collapsed;
        OnboardingSpeechPickPanel.Visibility = step == OnboardingStep.SpeechPick ? Visibility.Visible : Visibility.Collapsed;
        OnboardingSpeechComparePanel.Visibility = step == OnboardingStep.SpeechCompare ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPolishIntroPanel.Visibility = step == OnboardingStep.PolishIntro ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPolishPickPanel.Visibility = step == OnboardingStep.PolishPick ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPolishSeeAllPanel.Visibility = step == OnboardingStep.PolishSeeAll ? Visibility.Visible : Visibility.Collapsed;
        OnboardingDownloadsPanel.Visibility = step == OnboardingStep.Downloads ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPermissionsPanel.Visibility = step == OnboardingStep.Permissions ? Visibility.Visible : Visibility.Collapsed;
        OnboardingKeyPanel.Visibility = step == OnboardingStep.Key ? Visibility.Visible : Visibility.Collapsed;
        OnboardingGettingReadyPanel.Visibility = step == OnboardingStep.GettingReady ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPracticePanel.Visibility = step == OnboardingStep.Practice ? Visibility.Visible : Visibility.Collapsed;
        OnboardingClosingPanel.Visibility = step == OnboardingStep.Closing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateOnboardingLipsMotion()
    {
        var animate = ShouldAnimateMotion();
        if (animate && !_onboardingLipsAnimating)
        {
            _onboardingLipsAnimating = true;
            OnboardingLipsBreathStoryboard.Begin();
        }
        else if (!animate && _onboardingLipsAnimating)
        {
            _onboardingLipsAnimating = false;
            OnboardingLipsBreathStoryboard.Stop();
            OnboardingRainbowLipsContainer.Opacity = 1.0;
        }
    }

    private void DrawOnboardingLanguage()
    {
        var code = _onboarding.Choices.LanguageCode;
        var isAuto = string.IsNullOrWhiteSpace(code) ||
            string.Equals(code, LanguageCatalog.AutomaticCode, StringComparison.OrdinalIgnoreCase);

        _onboardingIsUpdatingLanguageUi = true;
        try
        {
            OnboardingDetectAutoCard.IsChecked = isAuto;
        }
        finally
        {
            _onboardingIsUpdatingLanguageUi = false;
        }

        OnboardingLanguageGreetingText.Text = SetupLanguageMetadata.GetGreeting(code, CultureInfo.CurrentUICulture);
        var displayName = isAuto
            ? LanguageCatalog.AutomaticTitle
            : LanguageCatalog.TryGetEntry(code, out var selectedEntry)
                ? selectedEntry.DisplayName
                : code;
        OnboardingLanguageSelectedTitleText.Text = displayName;

        var parakeetSupports = isAuto || LanguageCatalog.IsSupported(FinalAsrEngine.Parakeet, code);
        OnboardingLanguageEngineBadgeText.Text = parakeetSupports
            ? "Parakeet \u00B7 25 languages"
            : "Whisper \u00B7 99+ languages";
        if (Application.Current.Resources[parakeetSupports ? "BrandAccentChipStyle" : "BrandNeutralChipStyle"] is Style chipStyle)
        {
            OnboardingLanguageEngineBadge.Style = chipStyle;
        }
        if (Application.Current.Resources[parakeetSupports ? "BrandAccentChipTextStyle" : "BrandNeutralChipTextStyle"] is Style chipTextStyle)
        {
            OnboardingLanguageEngineBadgeText.Style = chipTextStyle;
        }

        OnboardingLanguageEngineDot.Fill = Application.Current.Resources[
            parakeetSupports ? "BrandAccentBrush" : "BrandTextSecondaryBrush"] as Brush;

        if (!string.IsNullOrWhiteSpace(_suggestedSystemLanguageCode) &&
            LanguageCatalog.TryGetEntry(_suggestedSystemLanguageCode, out var suggestedEntry) &&
            !string.Equals(code, suggestedEntry.Code, StringComparison.OrdinalIgnoreCase))
        {
            OnboardingSuggestedLanguageButton.Visibility = Visibility.Visible;
            OnboardingSuggestedLanguageText.Text = $"Suggested from Windows: {suggestedEntry.DisplayName}";
        }
        else
        {
            OnboardingSuggestedLanguageButton.Visibility = Visibility.Collapsed;
        }

        RefreshOnboardingLanguageList();
    }

    private void RefreshOnboardingLanguageList()
    {
        EnsureOnboardingChromeBuilt();
        var query = OnboardingLanguageSearchBox.Text ?? string.Empty;
        var selectedCode = _onboarding.Choices.LanguageCode;
        if (OnboardingLanguageListView.ItemsSource is not null &&
            string.Equals(_lastLanguageQuery, query, StringComparison.Ordinal) &&
            string.Equals(_lastSelectedLanguageCode, selectedCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastLanguageQuery = query;
        _lastSelectedLanguageCode = selectedCode;
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _onboardingAllLanguageRows
            : SetupLanguageMetadata.SearchPickerEntries(query)
                .Select(entry => new OnboardingLanguageRow(
                    entry.Code,
                    entry.DisplayName,
                    entry.PickerSubtitle,
                    LanguageCatalog.IsSupported(FinalAsrEngine.Parakeet, entry.Code)
                        ? "Parakeet \u00B7 Whisper"
                        : "Whisper"))
                .ToArray();

        for (var i = 0; i < filtered.Length; i++)
        {
            filtered[i] = filtered[i] with
            {
                IsSelected = string.Equals(filtered[i].Code, selectedCode, StringComparison.OrdinalIgnoreCase),
            };
        }

        OnboardingLanguageListView.ItemsSource = filtered;
        OnboardingLanguageEmptyText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DrawOnboardingSpeechPick()
    {
        var rec = _onboardingRecommendation ?? ComputeOnboardingRecommendation(_onboarding.Choices.LanguageCode);
        _onboardingRecommendation = rec;
        var selectedEngine = _onboarding.Choices.SpeechEngine;
        var isWhisper = selectedEngine == FinalAsrEngine.Whisper;
        var isInstalled = (isWhisper ? _whisperModelState : _parakeetModelState).IsInstalled;

        OnboardingSpeechPickTitle.Text = isWhisper ? "Meet Whisper" : "Meet Parakeet";
        OnboardingSpeechPickDescription.Text = isWhisper
            ? "Multilingual accuracy covering 99+ languages and mixed-script dictation."
            : "Ultra-fast local speech recognition tuned for 25 European languages.";
        var sizeLabel = isWhisper
            ? EnginePolishPresentation.WhisperBundleSizeLabel
            : EnginePolishPresentation.ParakeetBundleSizeLabel;
        OnboardingSpeechInstallBadgeText.Text = isInstalled ? "Already installed" : $"{sizeLabel} download";
        if (Application.Current.Resources[isInstalled ? "BrandSuccessChipStyle" : "BrandNeutralChipStyle"] is Style chipStyle)
        {
            OnboardingSpeechInstallBadge.Style = chipStyle;
        }
        if (Application.Current.Resources[isInstalled ? "BrandSuccessChipTextStyle" : "BrandNeutralChipTextStyle"] is Style chipTextStyle)
        {
            OnboardingSpeechInstallBadgeText.Style = chipTextStyle;
        }

        OnboardingSpeechPickSizeText.Text = isInstalled
            ? "Already downloaded on this PC (0 MB extra needed)."
            : $"One-time download: {sizeLabel}.";
        OnboardingSpeechPickSpeedText.Text = isWhisper ? rec.Speech.WhisperSpeedNote : rec.Speech.ParakeetSpeedNote;
        OnboardingSpeechPickLanguageNoteText.Text = rec.EffectiveLanguageEntry is { } entry
            ? $"Tailored for {entry.DisplayName}."
            : "Configured for Automatic language detection.";
    }

    private void DrawOnboardingSpeechCompare()
    {
        var rec = _onboardingRecommendation ?? ComputeOnboardingRecommendation(_onboarding.Choices.LanguageCode);
        _onboardingRecommendation = rec;
        var selectedEngine = _onboarding.Choices.SpeechEngine;
        var parakeetAllowed = rec.Speech.IsParakeetAvailable;

        _onboardingIsUpdatingCards = true;
        try
        {
            OnboardingParakeetCompareCard.IsEnabled = parakeetAllowed;
            OnboardingWhisperCompareCard.IsEnabled = true;
            OnboardingParakeetCompareCard.IsChecked = selectedEngine == FinalAsrEngine.Parakeet && parakeetAllowed;
            OnboardingWhisperCompareCard.IsChecked = selectedEngine == FinalAsrEngine.Whisper || !parakeetAllowed;
        }
        finally
        {
            _onboardingIsUpdatingCards = false;
        }

        OnboardingParakeetCompareRecommendedBadge.Visibility = rec.Speech.PrimaryEngine == FinalAsrEngine.Parakeet
            ? Visibility.Visible
            : Visibility.Collapsed;
        OnboardingWhisperCompareRecommendedBadge.Visibility = rec.Speech.PrimaryEngine == FinalAsrEngine.Whisper
            ? Visibility.Visible
            : Visibility.Collapsed;

        var parakeetStatusText = !parakeetAllowed
            ? (rec.Speech.ParakeetUnavailableReason ?? $"Doesn't support {rec.EffectiveLanguageEntry?.DisplayName ?? "this language"}")
            : _parakeetModelState.IsInstalled
                ? "Installed · 25 European languages"
                : "25 European languages";
        OnboardingParakeetCompareStatusBadgeText.Text = parakeetStatusText;
        if (Application.Current.Resources[!parakeetAllowed
            ? "BrandWarningChipStyle"
            : _parakeetModelState.IsInstalled
                ? "BrandSuccessChipStyle"
                : "BrandNeutralChipStyle"] is Style parakeetBadgeStyle)
        {
            OnboardingParakeetCompareStatusBadge.Style = parakeetBadgeStyle;
        }
        if (Application.Current.Resources[!parakeetAllowed
            ? "BrandWarningChipTextStyle"
            : _parakeetModelState.IsInstalled
                ? "BrandSuccessChipTextStyle"
                : "BrandNeutralChipTextStyle"] is Style parakeetTextStyle)
        {
            OnboardingParakeetCompareStatusBadgeText.Style = parakeetTextStyle;
        }

        OnboardingParakeetCompareSizeText.Text = _parakeetModelState.IsInstalled
            ? "Ready on this PC"
            : $"Download: {EnginePolishPresentation.ParakeetBundleSizeLabel}";
        OnboardingParakeetCompareDetailText.Text = rec.Speech.ParakeetSpeedNote;

        OnboardingWhisperCompareStatusBadgeText.Text = _whisperModelState.IsInstalled
            ? "Installed · 99+ languages"
            : "99+ languages";
        if (Application.Current.Resources[_whisperModelState.IsInstalled
            ? "BrandSuccessChipStyle"
            : "BrandAccentChipStyle"] is Style whisperBadgeStyle)
        {
            OnboardingWhisperCompareStatusBadge.Style = whisperBadgeStyle;
        }
        if (Application.Current.Resources[_whisperModelState.IsInstalled
            ? "BrandSuccessChipTextStyle"
            : "BrandAccentChipTextStyle"] is Style whisperTextStyle)
        {
            OnboardingWhisperCompareStatusBadgeText.Style = whisperTextStyle;
        }

        OnboardingWhisperCompareSizeText.Text = _whisperModelState.IsInstalled
            ? "Ready on this PC"
            : $"Download: {EnginePolishPresentation.WhisperBundleSizeLabel}";
        OnboardingWhisperCompareDetailText.Text = rec.Speech.WhisperSpeedNote;
    }

    private void DrawOnboardingPolishPick()
    {
        var rec = _onboardingRecommendation ?? ComputeOnboardingRecommendation(_onboarding.Choices.LanguageCode);
        _onboardingRecommendation = rec;
        var profile = _hardwareProfile ?? HardwareProfileSummary.GetCurrent();
        OnboardingPolishHardwareBadgeText.Text = profile.SummaryText;

        OnboardingPolishLowSpecCard.Visibility = rec.Polish.LowSpecPolishAdvisory
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (rec.Polish.LowSpecPolishAdvisory && !string.IsNullOrWhiteSpace(rec.Polish.SecondaryAdvisory))
        {
            OnboardingPolishLowSpecDetailText.Text = rec.Polish.SecondaryAdvisory;
        }

        var primaryId = EgOneModelCatalog.NormalizeOrDefault(rec.Polish.PrimaryModelId);
        var secondaryId = EgOneModelCatalog.NormalizeOrDefault(
            rec.Polish.SecondaryModelId ??
            (primaryId == EgOneModelCatalog.MiniId ? EgOneModelCatalog.MicroId : EgOneModelCatalog.MiniId));

        var selectedEgOne = _onboarding.Choices.PolishChoice == OnboardingPolishChoiceKind.EgOne
            ? EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId)
            : null;
        if (selectedEgOne is null || (selectedEgOne != primaryId && selectedEgOne != secondaryId))
        {
            selectedEgOne = primaryId;
        }

        _onboardingIsUpdatingCards = true;
        try
        {
            OnboardingPolishPrimaryCard.Tag = primaryId;
            OnboardingPolishSecondaryCard.Tag = secondaryId;
            OnboardingPolishPrimaryCard.IsChecked = selectedEgOne == primaryId;
            OnboardingPolishSecondaryCard.IsChecked = selectedEgOne == secondaryId;
        }
        finally
        {
            _onboardingIsUpdatingCards = false;
        }

        ApplyOnboardingEgOneSummary(
            primaryId,
            rec.Polish.PrimaryTag,
            isAccentTag: true,
            OnboardingPolishPrimaryTitleText,
            OnboardingPolishPrimaryTagBadge,
            OnboardingPolishPrimaryTagText,
            OnboardingPolishPrimaryStatusBadge,
            OnboardingPolishPrimaryStatusText,
            OnboardingPolishPrimaryDescText,
            OnboardingPolishPrimarySpecsText);

        ApplyOnboardingEgOneSummary(
            secondaryId,
            string.IsNullOrWhiteSpace(rec.Polish.SecondaryTag) ? "Smaller download" : rec.Polish.SecondaryTag,
            isAccentTag: rec.Polish.SecondaryPromoted,
            OnboardingPolishSecondaryTitleText,
            OnboardingPolishSecondaryTagBadge,
            OnboardingPolishSecondaryTagText,
            OnboardingPolishSecondaryStatusBadge,
            OnboardingPolishSecondaryStatusText,
            OnboardingPolishSecondaryDescText,
            OnboardingPolishSecondarySpecsText);
    }

    private void ApplyOnboardingEgOneSummary(
        string modelId,
        string tagLabel,
        bool isAccentTag,
        TextBlock titleText,
        Border tagBadge,
        TextBlock tagText,
        Border statusBadge,
        TextBlock statusText,
        TextBlock descText,
        TextBlock specsText)
    {
        var facts = EnginePolishPresentation.DescribeEgOneTier(modelId);
        var state = ResolveEgOneDisplayState(modelId);
        var installed = state.IsInstalled;
        var diskLabel = string.IsNullOrWhiteSpace(state.BundleSizeText)
            ? facts.DefaultDiskSizeText
            : state.BundleSizeText;

        titleText.Text = facts.Title;
        tagText.Text = tagLabel;
        if (Application.Current.Resources[isAccentTag ? "BrandAccentChipStyle" : "BrandNeutralChipStyle"] is Style tagStyle)
        {
            tagBadge.Style = tagStyle;
        }
        if (Application.Current.Resources[isAccentTag ? "BrandAccentChipTextStyle" : "BrandNeutralChipTextStyle"] is Style tagTextStyle)
        {
            tagText.Style = tagTextStyle;
        }

        statusText.Text = installed ? "Installed" : $"{diskLabel} download";
        if (Application.Current.Resources[installed ? "BrandSuccessChipStyle" : "BrandNeutralChipStyle"] is Style statusStyle)
        {
            statusBadge.Style = statusStyle;
        }
        if (Application.Current.Resources[installed ? "BrandSuccessChipTextStyle" : "BrandNeutralChipTextStyle"] is Style statusTextStyle)
        {
            statusText.Style = statusTextStyle;
        }

        descText.Text = facts.Description;
        specsText.Text = $"{facts.QualityText} \u00B7 {facts.SpeedText} \u00B7 {facts.MemoryText}";
    }

    private void DrawOnboardingPolishSeeAll()
    {
        var rec = _onboardingRecommendation ?? ComputeOnboardingRecommendation(_onboarding.Choices.LanguageCode);
        _onboardingRecommendation = rec;
        var profile = _hardwareProfile ?? HardwareProfileSummary.GetCurrent();
        OnboardingSeeAllHardwareBadgeText.Text = profile.SummaryText;

        var isCloudTab = _onboardingSeeAllCloudTab || TryGetCloudProvider(_onboarding.Choices.PolishChoice, out _);
        ApplySegmentButtonState(OnboardingPolishTabLocalButton, !isCloudTab);
        ApplySegmentButtonState(OnboardingPolishTabCloudButton, isCloudTab);
        OnboardingSeeAllLocalPanel.Visibility = isCloudTab ? Visibility.Collapsed : Visibility.Visible;
        OnboardingSeeAllCloudPanel.Visibility = isCloudTab ? Visibility.Visible : Visibility.Collapsed;

        var recId = EgOneModelCatalog.NormalizeOrDefault(rec.Polish.PrimaryModelId);
        var selectedEgOne = EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId ?? recId);

        _onboardingIsUpdatingCards = true;
        try
        {
            OnboardingSeeAllEgOneFullCard.IsChecked = !isCloudTab && selectedEgOne == EnginePolishPresentation.EgOneFullModelId;
            OnboardingSeeAllEgOneMiniCard.IsChecked = !isCloudTab && selectedEgOne == EnginePolishPresentation.EgOneMiniModelId;
            OnboardingSeeAllEgOneMicroCard.IsChecked = !isCloudTab && selectedEgOne == EnginePolishPresentation.EgOneMicroModelId;
        }
        finally
        {
            _onboardingIsUpdatingCards = false;
        }

        DrawOnboardingSeeAllEgOneCard(
            EnginePolishPresentation.EgOneFullModelId,
            recId,
            OnboardingSeeAllFullRecBadge,
            OnboardingSeeAllFullStatusBadge,
            OnboardingSeeAllFullStatusText);
        DrawOnboardingSeeAllEgOneCard(
            EnginePolishPresentation.EgOneMiniModelId,
            recId,
            OnboardingSeeAllMiniRecBadge,
            OnboardingSeeAllMiniStatusBadge,
            OnboardingSeeAllMiniStatusText);
        DrawOnboardingSeeAllEgOneCard(
            EnginePolishPresentation.EgOneMicroModelId,
            recId,
            OnboardingSeeAllMicroRecBadge,
            OnboardingSeeAllMicroStatusBadge,
            OnboardingSeeAllMicroStatusText);

        if (isCloudTab)
        {
            ApplySegmentButtonState(OnboardingCloudOpenAiButton, _onboardingCloudProvider == PolishProvider.OpenAI);
            ApplySegmentButtonState(OnboardingCloudGeminiButton, _onboardingCloudProvider == PolishProvider.Gemini);
            ApplySegmentButtonState(OnboardingCloudClaudeButton, _onboardingCloudProvider == PolishProvider.Anthropic);
            var canCheck = !string.IsNullOrWhiteSpace(OnboardingCloudApiKeyBox.Password) ||
                _session.Provider.KeyStatus(_onboardingCloudProvider) == ApiKeyReadStatus.Found;
            OnboardingCloudCheckKeyButton.IsEnabled = canCheck;
            if (OnboardingCloudModelComboBox.ItemsSource is null)
            {
                var defaultModel = _session.Provider.RecommendedModel(_onboardingCloudProvider)
                    ?? GetDefaultCloudModelFallback(_onboardingCloudProvider);
                PopulateOnboardingCloudModels(_onboardingCloudProvider, new[] { defaultModel }, _onboarding.Choices.CloudModelId ?? defaultModel);
                RefreshOnboardingCloudKeyStatusText(_onboardingCloudProvider);
            }
        }
    }

    private void DrawOnboardingSeeAllEgOneCard(
        string modelId,
        string recommendedId,
        Border recBadge,
        Border statusBadge,
        TextBlock statusText)
    {
        var facts = EnginePolishPresentation.DescribeEgOneTier(modelId);
        var state = ResolveEgOneDisplayState(modelId);
        var diskLabel = string.IsNullOrWhiteSpace(state.BundleSizeText)
            ? facts.DefaultDiskSizeText
            : state.BundleSizeText;
        recBadge.Visibility = string.Equals(modelId, recommendedId, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
        statusText.Text = state.IsInstalled ? "Installed" : diskLabel;
        if (Application.Current.Resources[state.IsInstalled ? "BrandSuccessChipStyle" : "BrandNeutralChipStyle"] is Style badgeStyle)
        {
            statusBadge.Style = badgeStyle;
        }
        if (Application.Current.Resources[state.IsInstalled ? "BrandSuccessChipTextStyle" : "BrandNeutralChipTextStyle"] is Style textStyle)
        {
            statusText.Style = textStyle;
        }
    }

    private void DrawOnboardingDownloads()
    {
        var snapshot = _onboardingDownloadSnapshot;
        var speechName = _onboarding.Choices.SpeechEngine == FinalAsrEngine.Whisper
            ? "Whisper speech model"
            : "Parakeet speech model";
        OnboardingSpeechDownloadNameText.Text = speechName;

        var speechStage = snapshot?.Speech.Stage ?? (_onboarding.Model == SpeechModelDelivery.Installed
            ? OnboardingDownloadStage.Installed
            : OnboardingDownloadStage.Pending);
        var speechPercent = snapshot?.Speech.Percent ?? (speechStage == OnboardingDownloadStage.Installed ? 100 : 0);
        OnboardingSpeechDownloadProgress.Value = Math.Clamp(speechPercent, 0, 100);
        OnboardingSpeechDownloadProgress.IsIndeterminate = speechStage == OnboardingDownloadStage.Activating;
        OnboardingSpeechDownloadBadgeText.Text = FormatDownloadStageBadge(speechStage, speechPercent);
        SetLiveText(
            OnboardingSpeechDownloadDetailText,
            snapshot?.Speech.StatusDetail ?? (speechStage == OnboardingDownloadStage.Installed
                ? "Verified and ready on this PC."
                : "Preparing speech model download..."));

        var showPolishCard = _onboarding.RequiresLocalEgOne;
        OnboardingPolishDownloadCard.Visibility = showPolishCard ? Visibility.Visible : Visibility.Collapsed;
        if (showPolishCard)
        {
            var polishName = EnginePolishPresentation.DescribeEgOneTier(
                EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId)).Title;
            OnboardingPolishDownloadNameText.Text = $"{polishName} AI polish";

            var polishSnapshot = snapshot?.Polish;
            var polishStage = polishSnapshot?.Stage ?? (_onboarding.PolishModel == SpeechModelDelivery.Installed
                ? OnboardingDownloadStage.Installed
                : OnboardingDownloadStage.Pending);
            var polishPercent = polishSnapshot?.Percent ?? (polishStage == OnboardingDownloadStage.Installed ? 100 : 0);
            OnboardingPolishDownloadProgress.Value = Math.Clamp(polishPercent, 0, 100);
            OnboardingPolishDownloadProgress.IsIndeterminate = polishStage == OnboardingDownloadStage.Activating;
            OnboardingPolishDownloadBadgeText.Text = FormatDownloadStageBadge(polishStage, polishPercent);
            SetLiveText(
                OnboardingPolishDownloadDetailText,
                polishSnapshot?.StatusDetail ?? (polishStage == OnboardingDownloadStage.Installed
                    ? "Verified and ready on this PC."
                    : "Waiting to download local AI polish..."));
        }

        var canCancel = snapshot?.Speech.CanCancel == true || snapshot?.Polish?.CanCancel == true;
        var canRetry = snapshot?.Speech.CanRetry == true || snapshot?.Polish?.CanRetry == true;
        var polishFailed = snapshot?.Polish is { Stage: OnboardingDownloadStage.Failed or OnboardingDownloadStage.Cancelled or OnboardingDownloadStage.CannotDownload };
        OnboardingDownloadCancelButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        OnboardingDownloadRetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        OnboardingDownloadSkipPolishButton.Visibility = showPolishCard && polishFailed
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string FormatDownloadStageBadge(OnboardingDownloadStage stage, double percent) => stage switch
    {
        OnboardingDownloadStage.Installed => "Ready",
        OnboardingDownloadStage.Downloading => $"{Math.Round(percent):0}%",
        OnboardingDownloadStage.Activating => "Starting",
        OnboardingDownloadStage.Cancelled => "Paused",
        OnboardingDownloadStage.Failed => "Failed",
        OnboardingDownloadStage.CannotDownload => "Unavailable",
        _ => "Queued",
    };

    private void DrawOnboardingPermissions()
    {
        var consent = WindowsMicrophoneConsent.Read();
        _onboardingMicConsent = consent;
        var allowed = consent != MicrophoneConsent.Blocked;
        OnboardingMicAllowedBadge.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        OnboardingMicNeedsAttentionBadge.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        var detail = _onboardingMicrophone?.Sentence ?? (allowed
            ? "Windows microphone access is allowed and ready for dictation."
            : "Microphone access is turned off for desktop apps in Windows Privacy Settings.");
        SetLiveText(OnboardingMicStatusText, detail);
    }

    private void DrawOnboardingKey()
    {
        var currentKey = _onboardingKeyCaptureSession is not null
            ? "Press key..."
            : (_onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? DictationPreferences.Default.PushToTalkGesture);
        OnboardingKeycapText.Text = currentKey;
        OnboardingKeycapHintText.Text = _onboardingKeyCaptureSession is not null
            ? "Listening for your shortcut. Press Escape to cancel."
            : "Click the keycap to capture a custom key, or pick a preset below.";

        ApplyPresetButtonStyle(OnboardingPresetCtrlWinButton, currentKey);
        ApplyPresetButtonStyle(OnboardingPresetRightCtrlButton, currentKey);
        ApplyPresetButtonStyle(OnboardingPresetF8Button, currentKey);
        ApplyPresetButtonStyle(OnboardingPresetAltSpaceButton, currentKey);

        var activeBrush = Application.Current.Resources["BrandAccentBrush"] as Brush;
        var dividerBrush = Application.Current.Resources["BrandDividerBrush"] as Brush;
        var successStyle = Application.Current.Resources["BrandSuccessChipStyle"] as Style;
        var successTextStyle = Application.Current.Resources["BrandSuccessChipTextStyle"] as Style;
        var neutralStyle = Application.Current.Resources["BrandNeutralChipStyle"] as Style;
        var neutralTextStyle = Application.Current.Resources["BrandNeutralChipTextStyle"] as Style;

        OnboardingRehearsalHoldCard.BorderBrush = _onboardingRehearsalKeyDown || _onboardingHoldRehearsed
            ? activeBrush
            : dividerBrush;
        OnboardingRehearsalHoldBadge.Style = _onboardingHoldRehearsed ? successStyle : neutralStyle;
        OnboardingRehearsalHoldBadgeText.Style = _onboardingHoldRehearsed ? successTextStyle : neutralTextStyle;
        OnboardingRehearsalHoldBadgeText.Text = _onboardingHoldRehearsed ? "Tried" : "Try holding";

        OnboardingRehearsalDoubleTapCard.BorderBrush = _onboardingDoubleTapRehearsed
            ? activeBrush
            : dividerBrush;
        OnboardingRehearsalDoubleTapBadge.Style = _onboardingDoubleTapRehearsed ? successStyle : neutralStyle;
        OnboardingRehearsalDoubleTapBadgeText.Style = _onboardingDoubleTapRehearsed ? successTextStyle : neutralTextStyle;
        OnboardingRehearsalDoubleTapBadgeText.Text = _onboardingDoubleTapRehearsed ? "Tried" : "Tap twice";
    }

    private static void ApplyPresetButtonStyle(Button button, string activeKey)
    {
        var isMatch = button.Tag is string tag && string.Equals(tag, activeKey, StringComparison.OrdinalIgnoreCase);
        if (Application.Current.Resources[isMatch ? "BrandQuietActiveButtonStyle" : "BrandQuietButtonStyle"] is Style style)
        {
            button.Style = style;
        }
    }

    private void DrawOnboardingGettingReady()
    {
        var speechReady = _onboarding.IsSpeechReady;
        var speechStage = _onboardingDownloadSnapshot?.Speech.Stage;
        var speechPercent = _onboardingDownloadSnapshot?.Speech.Percent ?? 0;
        ApplyGateRowStyle(
            OnboardingGateSpeechBadge,
            OnboardingGateSpeechBadgeText,
            speechReady ? "Ready" : speechStage == OnboardingDownloadStage.Failed ? "Failed" : "Setting up",
            speechReady ? "BrandSuccessChipStyle" : speechStage == OnboardingDownloadStage.Failed ? "BrandErrorChipStyle" : "BrandAccentChipStyle",
            speechReady ? "BrandSuccessChipTextStyle" : speechStage == OnboardingDownloadStage.Failed ? "BrandErrorChipTextStyle" : "BrandAccentChipTextStyle");
        OnboardingGateSpeechDetailText.Text = speechReady
            ? $"{_onboarding.Choices.SpeechEngine} is ready for dictation."
            : (_onboardingDownloadSnapshot?.Speech.StatusDetail ?? "Preparing speech model...");
        OnboardingGateSpeechProgress.Visibility = !speechReady && speechStage == OnboardingDownloadStage.Downloading
            ? Visibility.Visible
            : Visibility.Collapsed;
        OnboardingGateSpeechProgress.Value = Math.Clamp(speechPercent, 0, 100);

        OnboardingGatePolishRow.Visibility = _onboarding.RequiresLocalEgOne ? Visibility.Visible : Visibility.Collapsed;
        if (_onboarding.RequiresLocalEgOne)
        {
            var polishReady = _onboarding.IsPolishReadyForGate;
            var polishStage = _onboardingDownloadSnapshot?.Polish?.Stage;
            var polishPercent = _onboardingDownloadSnapshot?.Polish?.Percent ?? 0;
            ApplyGateRowStyle(
                OnboardingGatePolishBadge,
                OnboardingGatePolishBadgeText,
                polishReady ? "Ready" : polishStage is OnboardingDownloadStage.Failed or OnboardingDownloadStage.Cancelled ? "Needs attention" : "Downloading",
                polishReady ? "BrandSuccessChipStyle" : polishStage is OnboardingDownloadStage.Failed or OnboardingDownloadStage.Cancelled ? "BrandWarningChipStyle" : "BrandAccentChipStyle",
                polishReady ? "BrandSuccessChipTextStyle" : polishStage is OnboardingDownloadStage.Failed or OnboardingDownloadStage.Cancelled ? "BrandWarningChipTextStyle" : "BrandAccentChipTextStyle");
            OnboardingGatePolishDetailText.Text = polishReady
                ? "Local AI polish is ready."
                : (_onboardingDownloadSnapshot?.Polish?.StatusDetail ?? "Finishing EG-1 download...");
            OnboardingGatePolishProgress.Visibility = !polishReady && polishStage == OnboardingDownloadStage.Downloading
                ? Visibility.Visible
                : Visibility.Collapsed;
            OnboardingGatePolishProgress.Value = Math.Clamp(polishPercent, 0, 100);
        }

        var micAllowed = _onboarding.MicrophoneAllowed;
        ApplyGateRowStyle(
            OnboardingGateMicBadge,
            OnboardingGateMicBadgeText,
            micAllowed ? "Allowed" : "Needs attention",
            micAllowed ? "BrandSuccessChipStyle" : "BrandWarningChipStyle",
            micAllowed ? "BrandSuccessChipTextStyle" : "BrandWarningChipTextStyle");
        OnboardingGateMicDetailText.Text = micAllowed
            ? "Microphone access verified."
            : "Turn on desktop microphone access in Windows Privacy Settings.";
        OnboardingGateMicSettingsButton.Visibility = micAllowed ? Visibility.Collapsed : Visibility.Visible;

        var decision = _onboarding.EvaluateGate();
        OnboardingGateRetryButton.Visibility = decision == OnboardingGateDecision.PolishFailedContinueWithout ||
            _onboarding.Model is SpeechModelDelivery.Failed or SpeechModelDelivery.Cancelled
                ? Visibility.Visible
                : Visibility.Collapsed;
        OnboardingGateSkipPolishButton.Visibility = decision is OnboardingGateDecision.WaitingWithPolishDownloading
            or OnboardingGateDecision.PolishFailedContinueWithout
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private static void ApplyGateRowStyle(
        Border badge,
        TextBlock text,
        string label,
        string badgeStyleKey,
        string textStyleKey)
    {
        text.Text = label;
        if (Application.Current.Resources[badgeStyleKey] is Style badgeStyle)
        {
            badge.Style = badgeStyle;
        }
        if (Application.Current.Resources[textStyleKey] is Style textStyle)
        {
            text.Style = textStyle;
        }
    }

    private void DrawOnboardingPractice()
    {
        var activeKey = _onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? "Ctrl+Win";
        OnboardingPracticeKeyChipText.Text = _onboarding.Choices.RecordingMode == DictationRecordingMode.Toggle
            ? $"Toggle: {activeKey}"
            : $"Hold: {activeKey} (or double-press for hands-free)";
        OnboardingPracticeFootnote.Text = _onboarding.TakeInFlight
            ? OnboardingCopy.PracticeBusy
            : OnboardingCopy.PracticeFootnote;
        OnboardingPracticeMicrophoneButton.Visibility = _onboarding.PracticeState == OnboardingPracticeState.CannotHear
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DrawOnboardingClosing()
    {
        var beat = _onboarding.CurrentClosingBeat;
        OnboardingClosingBeatStepText.Text = $"{beat.Index + 1} OF {OnboardingFlow.ClosingBeatCount}";
        OnboardingClosingBeatTitle.Text = beat.Title;
        OnboardingClosingBeatSubtitle.Text = beat.Subtitle;
        OnboardingClosingBeatIcon.Glyph = beat.Kind switch
        {
            OnboardingClosingBeatKind.Apps => "\uE71D",
            OnboardingClosingBeatKind.Land => "\uE8A7",
            OnboardingClosingBeatKind.Copied => "\uE8C8",
            OnboardingClosingBeatKind.Learn => "\uE82D",
            OnboardingClosingBeatKind.More => "\uE713",
            OnboardingClosingBeatKind.Yours => "\uE720",
            _ => "\uE73E",
        };

        var speechName = _onboarding.Choices.SpeechEngine == FinalAsrEngine.Whisper ? "Whisper" : "Parakeet";
        var polishName = _onboarding.Choices.PolishChoice switch
        {
            OnboardingPolishChoiceKind.EgOne => EnginePolishPresentation.DescribeEgOneTier(
                EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId)).Title,
            OnboardingPolishChoiceKind.OpenAI => "OpenAI",
            OnboardingPolishChoiceKind.Claude => "Claude",
            OnboardingPolishChoiceKind.Gemini => "Gemini",
            OnboardingPolishChoiceKind.Ollama => "Ollama",
            _ => "Off",
        };
        var keyName = _onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? "Ctrl+Win";

        OnboardingClosingSummaryText.Text = OnboardingCopy.FormatClosingSummary(beat, speechName, polishName, keyName);

        var languageLabel = string.Equals(_onboarding.Choices.LanguageCode, LanguageCatalog.AutomaticCode, StringComparison.OrdinalIgnoreCase)
            ? LanguageCatalog.AutomaticTitle
            : LanguageCatalog.TryGetEntry(_onboarding.Choices.LanguageCode, out var entry)
                ? entry.DisplayName
                : _onboarding.Choices.LanguageCode;
        OnboardingClosingLanguageSummaryText.Text = languageLabel;
        OnboardingClosingSpeechSummaryText.Text = speechName;
        OnboardingClosingPolishSummaryText.Text = polishName;
        OnboardingClosingKeySummaryText.Text = keyName;

        var activeBrush = Application.Current.Resources["BrandAccentSolidBrush"] as Brush;
        var idleBrush = Application.Current.Resources["BrandDividerBrush"] as Brush;
        for (var i = 0; i < _onboardingClosingDotButtons.Length; i++)
        {
            if (_onboardingClosingDotButtons[i].Content is Border dot)
            {
                dot.Background = i == beat.Index ? activeBrush : idleBrush;
            }
        }
    }

    private void DrawOnboardingFooter(OnboardingStep step)
    {
        var (ctaText, ctaEnabled, skipText, showSkip) = step switch
        {
            OnboardingStep.Welcome => ("Set up dictation", true, string.Empty, false),
            OnboardingStep.PolishIntro => ("Set up AI polish", true, "Skip AI polish", true),
            OnboardingStep.Permissions => ("Continue", _onboarding.MicrophoneAllowed, string.Empty, false),
            OnboardingStep.GettingReady when _onboarding.CanSkipAheadDuringGate => ("Skip ahead", true, "Continue without AI polish", true),
            OnboardingStep.GettingReady when _onboarding.CanContinueWithoutAiPolishOnGate => ("Continue without AI polish", true, string.Empty, false),
            OnboardingStep.GettingReady => ("Continue", _onboarding.EvaluateGate() == OnboardingGateDecision.ReadyAutoSkip, string.Empty, false),
            OnboardingStep.Practice => ("Continue", _onboarding.CanFinishPractice, "Skip practice", _onboarding.CanSkipPractice),
            OnboardingStep.Closing => ("Start using EnviousWispr", true, string.Empty, false),
            _ => ("Continue", true, string.Empty, false),
        };

        FinishOnboardingButton.Content = ctaText;
        FinishOnboardingButton.IsEnabled = ctaEnabled;
        OnboardingSkipButton.Content = skipText;
        OnboardingSkipButton.Visibility = showSkip ? Visibility.Visible : Visibility.Collapsed;
        OnboardingFooterText.Visibility = step is OnboardingStep.Welcome or OnboardingStep.Closing
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public sealed record OnboardingLanguageRow(
        string Code,
        string DisplayTitle,
        string Subtitle,
        string EngineLabel,
        bool IsSelected = false)
    {
        public Visibility SelectedVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    }
}
