using System.Globalization;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Onboarding;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnviousWispr.App;

/// <summary>13-screen Fluent 2 onboarding walkthrough rendering and theme-aware styling.</summary>
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
        var previousStep = _drawnStep;
        var screenChanged = previousStep != step;

        if (screenChanged && previousStep == OnboardingStep.Key)
        {
            CancelOnboardingKeyCapture();
            _onboardingRehearsalKeyDown = false;
            _onboardingKeyHoldTimer.Stop();
        }

        if (screenChanged && previousStep == OnboardingStep.Language)
        {
            _onboardingLanguageSpeakingActive = false;
        }

        if (screenChanged && step != OnboardingStep.GettingReady)
        {
            _onboardingSlowPolishTimer.Stop();
            _onboardingSlowPolishCardVisible = false;
        }

        OnboardingKeyRehearsalActiveChanged?.Invoke(
            step == OnboardingStep.Key && _onboardingKeyCaptureSession is null);

        if (screenChanged && step == OnboardingStep.Practice && _dictationActive && !_onboarding.TakeInFlight)
        {
            _onboarding.TakeStarted(OnboardingPracticeBox.Text);
        }

        ArmMicrophonePoll();
        ArmSlowPolishGateTimer(step);
        ArmSlowDownloadBannerTimer(step);
        DrawOnboardingChrome(step);
        DrawOnboardingRainbowLips(step, screenChanged);
        DrawOnboardingStepHeader(step);
        SetVisibleOnboardingPanels(step);

        var focusedBefore = FocusedOnboardingControl();
        switch (step)
        {
            case OnboardingStep.Welcome:
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
            case OnboardingStep.PolishIntro:
                DrawOnboardingPolishIntro();
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
                DrawOnboardingClosing(restartFilm: screenChanged);
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
            PlayOnboardingStageEntrance(previousStep, step);
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
        var showBack = _onboarding.CanGoBack && step != OnboardingStep.Closing;
        var showProgress = OnboardingFlow.ShowsProgressStrip(step);
        var pillSummary = _onboardingDownloadSnapshot is null
            ? null
            : OnboardingDownloadCoordinator.FormatBackgroundPillSummary(_onboardingDownloadSnapshot);
        var showDownloadPill = !string.IsNullOrWhiteSpace(pillSummary) &&
            step is not (OnboardingStep.Welcome or OnboardingStep.Downloads or OnboardingStep.Closing);

        OnboardingBackButton.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
        OnboardingProgressGrid.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        OnboardingDownloadPillButton.Visibility = showDownloadPill ? Visibility.Visible : Visibility.Collapsed;
        OnboardingChromeBar.Visibility = showBack || showProgress || showDownloadPill
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (showProgress)
        {
            var currentNum = OnboardingFlow.PrimaryStepNumberFor(step);
            var activeBrush = ResolveWindowThemeBrush("BrandAccentSolidBrush");
            var idleBrush = ResolveWindowThemeBrush("BrandDividerBrush");
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
            OnboardingDownloadPillDot.Fill = ResolveWindowThemeBrush(
                isDone ? "BrandSuccessBrush" : "BrandAccentBrush");
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
        OnboardingStepText.Text = OnboardingCopy.MilestoneLabel(step).ToUpperInvariant();
        var micAllowed = _onboarding.MicrophoneAllowed;
        var polishDeferred = _onboardingPolishDeferredInPractice &&
            _onboarding.PolishModel != SpeechModelDelivery.Installed;

        var (title, subtitle) = step switch
        {
            OnboardingStep.Language => (
                "Which language do you dictate in?",
                "Pick automatic detection or lock a language for higher recognition accuracy."),
            OnboardingStep.SpeechPick => (
                "Meet your speech model",
                "Tuned to your language and PC hardware, running privately offline."),
            OnboardingStep.SpeechCompare => (
                "Compare speech models",
                "Both models run privately on your PC with zero audio leaving this device."),
            OnboardingStep.PolishIntro => (
                "Cleaner writing that still sounds like you",
                "Optional AI polish strips filler words, fixes grammar, and formats cleanly."),
            OnboardingStep.PolishPick => (
                "Meet EG-1",
                "Our local AI polish model, picked to match your PC memory and graphics."),
            OnboardingStep.PolishSeeAll => (
                "Choose your AI polish option",
                "Pick any local EG-1 size or connect your own OpenAI, Gemini, or Claude key."),
            OnboardingStep.Downloads => (
                "Downloading your models",
                "Downloads keep running in the background while you finish setup."),
            OnboardingStep.Permissions => (
                OnboardingCopy.PermissionsHeadline(micAllowed),
                OnboardingCopy.PermissionsSubtitle),
            OnboardingStep.Key => (
                "Meet your dictation key",
                "Pick the shortcut you will use anywhere on Windows and try it below."),
            OnboardingStep.GettingReady => (
                OnboardingCopy.GateTitle,
                "Waiting for your chosen downloads or microphone check to finish."),
            OnboardingStep.Practice => (
                OnboardingCopy.PracticeHeadline(_onboarding.PracticeState, _onboarding.PracticeSucceeded, polishDeferred),
                OnboardingCopy.PracticeSubhead(
                    _onboarding.PracticeState,
                    _onboarding.PracticeSucceeded,
                    _onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? "Ctrl+Win",
                    _onboarding.Choices.RecordingMode,
                    _onboarding.MissedTo,
                    polishDeferred)),
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
        if (OnboardingPolishDemo is not null)
        {
            OnboardingPolishDemo.Visibility = step == OnboardingStep.PolishIntro ? Visibility.Visible : Visibility.Collapsed;
        }
        OnboardingPolishPickPanel.Visibility = step == OnboardingStep.PolishPick ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPolishSeeAllPanel.Visibility = step == OnboardingStep.PolishSeeAll ? Visibility.Visible : Visibility.Collapsed;
        OnboardingDownloadsPanel.Visibility = step == OnboardingStep.Downloads ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPermissionsPanel.Visibility = step == OnboardingStep.Permissions ? Visibility.Visible : Visibility.Collapsed;
        OnboardingKeyPanel.Visibility = step == OnboardingStep.Key ? Visibility.Visible : Visibility.Collapsed;
        OnboardingGettingReadyPanel.Visibility = step == OnboardingStep.GettingReady ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPracticePanel.Visibility = step == OnboardingStep.Practice ? Visibility.Visible : Visibility.Collapsed;
        OnboardingClosingPanel.Visibility = step == OnboardingStep.Closing ? Visibility.Visible : Visibility.Collapsed;
        if (OnboardingClosingFilm is not null)
        {
            OnboardingClosingFilm.Visibility = step == OnboardingStep.Closing ? Visibility.Visible : Visibility.Collapsed;
            if (step != OnboardingStep.Closing)
            {
                OnboardingClosingFilm.Pause();
            }
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

        var greeting = SetupLanguageMetadata.GetGreeting(code, CultureInfo.CurrentUICulture);
        OnboardingLanguageGreetingText.Text = greeting;
        var displayName = isAuto
            ? LanguageCatalog.AutomaticTitle
            : LanguageCatalog.TryGetEntry(code, out var selectedEntry)
                ? selectedEntry.DisplayName
                : code;
        OnboardingLanguageSelectedTitleText.Text = displayName;

        var rec = _onboardingRecommendation ?? ComputeOnboardingRecommendation(code);
        _onboardingRecommendation = rec;
        var parakeetRecommended = isAuto || rec.Speech.PrimaryEngine == FinalAsrEngine.Parakeet;
        OnboardingLanguageEngineBadgeText.Text = parakeetRecommended
            ? "Parakeet \u00B7 25 languages"
            : "Whisper \u00B7 99+ languages";
        ApplyOnboardingStyle(OnboardingLanguageEngineBadge, parakeetRecommended ? "BrandAccentChipStyle" : "BrandNeutralChipStyle");
        ApplyOnboardingStyle(OnboardingLanguageEngineBadgeText, parakeetRecommended ? "BrandAccentChipTextStyle" : "BrandNeutralChipTextStyle");
        OnboardingLanguageEngineDot.Fill = ResolveWindowThemeBrush(
            parakeetRecommended ? "BrandAccentBrush" : "BrandTextSecondaryBrush");

        if (!isAuto && rec.Speech.PrimaryEngine == FinalAsrEngine.Whisper && !string.IsNullOrWhiteSpace(rec.Speech.ParakeetUnavailableReason))
        {
            OnboardingLanguageAdviceCard.Visibility = Visibility.Visible;
            OnboardingLanguageAdviceText.Text = $"{rec.Speech.ParakeetUnavailableReason}. EnviousWispr will recommend Whisper on the next step.";
        }
        else
        {
            OnboardingLanguageAdviceCard.Visibility = Visibility.Collapsed;
        }

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
        var modelState = isWhisper ? _whisperModelState : _parakeetModelState;
        var isInstalled = modelState.IsInstalled;

        OnboardingSpeechHeroTimeText.Text = isWhisper
            ? rec.Speech.WhisperHeroTimeText
            : rec.Speech.ParakeetHeroTimeText;
        OnboardingSpeechHeroSubtext.Text = rec.Speech.HeroSubtext;
        OnboardingSpeechHardwareBadgeText.Text = isWhisper
            ? rec.Speech.WhisperHardwareBadgeText
            : rec.Speech.ParakeetHardwareBadgeText;

        OnboardingSpeechPickTitle.Text = isWhisper ? "Meet Whisper" : "Meet Parakeet";
        OnboardingSpeechPickDescription.Text = isWhisper
            ? "Multilingual accuracy covering 99+ languages and mixed-script dictation."
            : "Ultra-fast local speech recognition tuned for 25 European languages.";

        var sizeLabel = FormatSpeechBundleSize(selectedEngine, modelState.BundleSizeText);
        OnboardingSpeechInstallBadgeText.Text = isInstalled ? "Already installed" : $"{sizeLabel} download";
        ApplyOnboardingStyle(OnboardingSpeechInstallBadge, isInstalled ? "BrandSuccessChipStyle" : "BrandNeutralChipStyle");
        ApplyOnboardingStyle(OnboardingSpeechInstallBadgeText, isInstalled ? "BrandSuccessChipTextStyle" : "BrandNeutralChipTextStyle");

        OnboardingSpeechPickSizeText.Text = isInstalled
            ? "Ready on this PC (0 MB extra needed)."
            : $"One-time download: {sizeLabel}.";
        OnboardingSpeechPickSpeedText.Text = isWhisper ? rec.Speech.WhisperSpeedNote : rec.Speech.ParakeetSpeedNote;
        OnboardingSpeechPickLanguageNoteText.Text = rec.EffectiveLanguageEntry is { } entry
            ? $"Tailored for {entry.DisplayName}."
            : "Automatic language detection.";
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

        OnboardingParakeetCompareTimeText.Text = rec.Speech.ParakeetHeroTimeText;
        OnboardingWhisperCompareTimeText.Text = rec.Speech.WhisperHeroTimeText;

        var hasParakeetAdvisory = !parakeetAllowed || !string.IsNullOrWhiteSpace(rec.Speech.ParakeetUnavailableReason);
        var parakeetStatusText = hasParakeetAdvisory
            ? (rec.Speech.ParakeetUnavailableReason ?? $"Doesn't support {rec.EffectiveLanguageEntry?.DisplayName ?? "this language"}")
            : _parakeetModelState.IsInstalled
                ? "Installed \u00B7 25 languages"
                : "25 European languages";
        OnboardingParakeetCompareStatusBadgeText.Text = parakeetStatusText;
        ApplyOnboardingStyle(
            OnboardingParakeetCompareStatusBadge,
            hasParakeetAdvisory ? "BrandWarningChipStyle" : _parakeetModelState.IsInstalled ? "BrandSuccessChipStyle" : "BrandNeutralChipStyle");
        ApplyOnboardingStyle(
            OnboardingParakeetCompareStatusBadgeText,
            hasParakeetAdvisory ? "BrandWarningChipTextStyle" : _parakeetModelState.IsInstalled ? "BrandSuccessChipTextStyle" : "BrandNeutralChipTextStyle");

        var parakeetSize = FormatSpeechBundleSize(FinalAsrEngine.Parakeet, _parakeetModelState.BundleSizeText);
        OnboardingParakeetCompareSizeText.Text = _parakeetModelState.IsInstalled
            ? "Ready on this PC"
            : $"Download: {parakeetSize}";
        OnboardingParakeetCompareDetailText.Text = rec.Speech.ParakeetSpeedNote;

        OnboardingWhisperCompareStatusBadgeText.Text = _whisperModelState.IsInstalled
            ? "Installed \u00B7 99+ languages"
            : "99+ languages";
        ApplyOnboardingStyle(
            OnboardingWhisperCompareStatusBadge,
            _whisperModelState.IsInstalled ? "BrandSuccessChipStyle" : "BrandAccentChipStyle");
        ApplyOnboardingStyle(
            OnboardingWhisperCompareStatusBadgeText,
            _whisperModelState.IsInstalled ? "BrandSuccessChipTextStyle" : "BrandAccentChipTextStyle");

        var whisperSize = FormatSpeechBundleSize(FinalAsrEngine.Whisper, _whisperModelState.BundleSizeText);
        OnboardingWhisperCompareSizeText.Text = _whisperModelState.IsInstalled
            ? "Ready on this PC"
            : $"Download: {whisperSize}";
        OnboardingWhisperCompareDetailText.Text = rec.Speech.WhisperSpeedNote;
    }

    private void DrawOnboardingPolishIntro()
    {
        if (OnboardingPolishDemo is not null)
        {
            OnboardingPolishDemo.ReduceMotion = !ShouldAnimateMotion();
        }
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
        var diskLabel = FormatEgOneBundleSize(modelId, state.BundleSizeText, facts.DefaultDiskSizeText);

        titleText.Text = facts.Title;
        tagText.Text = tagLabel;
        ApplyOnboardingStyle(tagBadge, isAccentTag ? "BrandAccentChipStyle" : "BrandNeutralChipStyle");
        ApplyOnboardingStyle(tagText, isAccentTag ? "BrandAccentChipTextStyle" : "BrandNeutralChipTextStyle");

        statusText.Text = installed ? "Installed" : $"{diskLabel} download";
        ApplyOnboardingStyle(statusBadge, installed ? "BrandSuccessChipStyle" : "BrandNeutralChipStyle");
        ApplyOnboardingStyle(statusText, installed ? "BrandSuccessChipTextStyle" : "BrandNeutralChipTextStyle");

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
        var diskLabel = FormatEgOneBundleSize(modelId, state.BundleSizeText, facts.DefaultDiskSizeText);
        recBadge.Visibility = string.Equals(modelId, recommendedId, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
        statusText.Text = state.IsInstalled ? "Installed" : diskLabel;
        ApplyOnboardingStyle(statusBadge, state.IsInstalled ? "BrandSuccessChipStyle" : "BrandNeutralChipStyle");
        ApplyOnboardingStyle(statusText, state.IsInstalled ? "BrandSuccessChipTextStyle" : "BrandNeutralChipTextStyle");
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
            FormatDownloadDetailText(snapshot?.Speech, speechStage, isPolish: false));

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
                FormatDownloadDetailText(polishSnapshot, polishStage, isPolish: true));
        }

        OnboardingSlowNetworkBanner.Visibility = _onboardingSlowDownloadBannerVisible &&
            snapshot?.IsRunning == true
                ? Visibility.Visible
                : Visibility.Collapsed;

        var canCancel = snapshot?.Speech.CanCancel == true || snapshot?.Polish?.CanCancel == true;
        var canRetry = snapshot?.Speech.CanRetry == true || snapshot?.Polish?.CanRetry == true;
        var polishFailed = snapshot?.Polish is { Stage: OnboardingDownloadStage.Failed or OnboardingDownloadStage.Cancelled or OnboardingDownloadStage.CannotDownload };
        OnboardingDownloadCancelButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        OnboardingDownloadRetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        OnboardingDownloadSkipPolishButton.Visibility = showPolishCard && polishFailed
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string FormatDownloadDetailText(
        OnboardingModelDownloadState? state,
        OnboardingDownloadStage stage,
        bool isPolish)
    {
        if (state is not null && state.Stage == OnboardingDownloadStage.Downloading && state.TotalBytes > 0)
        {
            var doneLabel = WindowsSetupRules.FormatDownloadLabel(state.CompletedBytes);
            var totalLabel = WindowsSetupRules.FormatDownloadLabel(state.TotalBytes);
            return $"{doneLabel} of {totalLabel} downloaded";
        }

        if (!string.IsNullOrWhiteSpace(state?.StatusDetail))
        {
            return state.StatusDetail!;
        }

        return stage == OnboardingDownloadStage.Installed
            ? "Verified and ready on this PC."
            : isPolish
                ? "Waiting to download local AI polish..."
                : "Preparing speech model download...";
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
        if (allowed && !_drawnMicAllowed)
        {
            OnboardingRainbowLips?.TriggerBurst();
        }

        _drawnMicAllowed = allowed;
        OnboardingMicAllowedBadge.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        OnboardingMicNeedsAttentionBadge.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        var detail = _onboardingMicrophone?.Sentence ?? (allowed
            ? "Windows microphone access is allowed and ready for dictation."
            : "Microphone access is turned off for desktop apps in Windows Privacy Settings.");
        SetLiveText(OnboardingMicStatusText, detail);
    }

    private void DrawOnboardingKey()
    {
        var savedKey = _onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? DictationPreferences.Default.PushToTalkGesture;
        var isCapturing = _onboardingKeyCaptureSession is not null;
        OnboardingKeycapText.Text = isCapturing ? "Press key..." : savedKey;
        OnboardingKeycapHintText.Text = isCapturing
            ? "Listening for your shortcut. Press Escape to cancel."
            : "Click the keycap to capture a custom key, or pick a preset below.";

        OnboardingKeycapButton.BorderBrush = ResolveWindowThemeBrush(
            isCapturing || _onboardingRehearsalKeyDown
                ? "BrandAccentBrush"
                : "BrandOnboardingKeycapBevelBrush");

        if (OnboardingKeyboardVisual is not null)
        {
            OnboardingKeyboardVisual.HotkeyGestureText = savedKey;
            OnboardingKeyboardVisual.IsPressed = _onboardingRehearsalKeyDown;
            OnboardingKeyboardVisual.IsCapturing = isCapturing;
            OnboardingKeyboardVisual.ReduceMotion = !ShouldAnimateMotion();
        }

        ApplyPresetButtonStyle(OnboardingPresetCtrlWinButton, savedKey);
        ApplyPresetButtonStyle(OnboardingPresetRightCtrlButton, savedKey);
        ApplyPresetButtonStyle(OnboardingPresetF8Button, savedKey);
        ApplyPresetButtonStyle(OnboardingPresetAltSpaceButton, savedKey);

        var activeBrush = ResolveWindowThemeBrush("BrandAccentBrush");
        var dividerBrush = ResolveWindowThemeBrush("BrandDividerBrush");
        OnboardingRehearsalHoldCard.BorderBrush = _onboardingRehearsalKeyDown || _onboardingHoldRehearsed
            ? activeBrush
            : dividerBrush;
        ApplyOnboardingStyle(OnboardingRehearsalHoldBadge, _onboardingHoldRehearsed ? "BrandSuccessChipStyle" : "BrandNeutralChipStyle");
        ApplyOnboardingStyle(OnboardingRehearsalHoldBadgeText, _onboardingHoldRehearsed ? "BrandSuccessChipTextStyle" : "BrandNeutralChipTextStyle");
        OnboardingRehearsalHoldBadgeText.Text = _onboardingHoldRehearsed ? "Tried" : "Try holding";

        OnboardingRehearsalDoubleTapCard.BorderBrush = _onboardingDoubleTapRehearsed
            ? activeBrush
            : dividerBrush;
        ApplyOnboardingStyle(OnboardingRehearsalDoubleTapBadge, _onboardingDoubleTapRehearsed ? "BrandSuccessChipStyle" : "BrandNeutralChipStyle");
        ApplyOnboardingStyle(OnboardingRehearsalDoubleTapBadgeText, _onboardingDoubleTapRehearsed ? "BrandSuccessChipTextStyle" : "BrandNeutralChipTextStyle");
        OnboardingRehearsalDoubleTapBadgeText.Text = _onboardingDoubleTapRehearsed ? "Tried" : "Tap twice";
    }

    private void ApplyPresetButtonStyle(Button button, string activeKey)
    {
        var isMatch = button.Tag is string tag && string.Equals(tag, activeKey, StringComparison.OrdinalIgnoreCase);
        ApplyOnboardingStyle(button, isMatch ? "BrandQuietActiveButtonStyle" : "BrandQuietButtonStyle");
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
                : (_onboardingDownloadSnapshot?.Polish?.StatusDetail ?? OnboardingCopy.GatePolishDownloadingNotice);
            OnboardingGatePolishProgress.Visibility = !polishReady && polishStage == OnboardingDownloadStage.Downloading
                ? Visibility.Visible
                : Visibility.Collapsed;
            OnboardingGatePolishProgress.Value = Math.Clamp(polishPercent, 0, 100);
        }

        var micAllowed = _onboarding.MicrophoneAllowed;
        ApplyGateRowStyle(
            OnboardingGateMicBadge,
            OnboardingGateMicBadgeText,
            micAllowed ? "Ready" : "Needs attention",
            micAllowed ? "BrandSuccessChipStyle" : "BrandWarningChipStyle",
            micAllowed ? "BrandSuccessChipTextStyle" : "BrandWarningChipTextStyle");
        OnboardingGateMicDetailText.Text = micAllowed
            ? "Microphone access verified."
            : "Turn on desktop microphone access in Windows Privacy Settings.";
        OnboardingGateMicSettingsButton.Visibility = micAllowed ? Visibility.Collapsed : Visibility.Visible;

        var decision = _onboarding.EvaluateGate();
        var showSlowCard = _onboardingSlowPolishCardVisible && decision == OnboardingGateDecision.WaitingWithPolishDownloading;
        OnboardingGateSlowPolishCard.Visibility = showSlowCard ? Visibility.Visible : Visibility.Collapsed;
        if (showSlowCard)
        {
            OnboardingGateSlowPolishTitleText.Text = OnboardingCopy.GateSlowPolishTitle;
            OnboardingGateSlowPolishBodyText.Text = OnboardingCopy.GateSlowPolishBody;
        }

        OnboardingGateRetryButton.Visibility = decision == OnboardingGateDecision.PolishFailedContinueWithout ||
            _onboarding.Model is SpeechModelDelivery.Failed or SpeechModelDelivery.Cancelled
                ? Visibility.Visible
                : Visibility.Collapsed;
        OnboardingGateSkipPolishButton.Visibility = !showSlowCard &&
            decision is OnboardingGateDecision.WaitingWithPolishDownloading or OnboardingGateDecision.PolishFailedContinueWithout
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void ApplyGateRowStyle(
        Border badge,
        TextBlock text,
        string label,
        string badgeStyleKey,
        string textStyleKey)
    {
        text.Text = label;
        ApplyOnboardingStyle(badge, badgeStyleKey);
        ApplyOnboardingStyle(text, textStyleKey);
    }

    private void DrawOnboardingPractice()
    {
        var activeKey = _onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? "Ctrl+Win";
        OnboardingPracticeKeyChipText.Text = _onboarding.Choices.RecordingMode == DictationRecordingMode.Toggle
            ? $"Press: {activeKey}"
            : $"Hold: {activeKey}";
        OnboardingPracticePromptText.Text = OnboardingCopy.PracticePrompt;

        OnboardingPracticeBoxFrame.BorderBrush = ResolveWindowThemeBrush(
            _onboarding.PracticeSucceeded
                ? "BrandSuccessBrush"
                : _onboarding.PracticeState == OnboardingPracticeState.Listening
                    ? "BrandAccentBrush"
                    : "BrandDividerBrush");

        var showDeferredPolish = _onboardingPolishDeferredInPractice &&
            _onboarding.PolishModel != SpeechModelDelivery.Installed;
        OnboardingPracticeDeferredPolishCard.Visibility = showDeferredPolish ? Visibility.Visible : Visibility.Collapsed;

        var showMicPicker = !_onboarding.PracticeSucceeded && _microphones.Length > 0;
        OnboardingPracticeMicRow.Visibility = showMicPicker ? Visibility.Visible : Visibility.Collapsed;
        if (showMicPicker && OnboardingPracticeMicComboBox is not null)
        {
            _onboardingIsUpdatingPracticeMic = true;
            try
            {
                if (!ReferenceEquals(OnboardingPracticeMicComboBox.ItemsSource, _microphones))
                {
                    OnboardingPracticeMicComboBox.ItemsSource = _microphones;
                }

                if (_selectedMicrophoneChoice is not null &&
                    !ReferenceEquals(OnboardingPracticeMicComboBox.SelectedItem, _selectedMicrophoneChoice))
                {
                    OnboardingPracticeMicComboBox.SelectedItem = _selectedMicrophoneChoice;
                }
            }
            finally
            {
                _onboardingIsUpdatingPracticeMic = false;
            }
        }

        OnboardingPracticeFootnote.Text = _onboarding.TakeInFlight
            ? OnboardingCopy.PracticeBusy
            : OnboardingCopy.PracticeFootnote;
        OnboardingPracticeMicrophoneButton.Visibility = _onboarding.PracticeState == OnboardingPracticeState.CannotHear
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DrawOnboardingClosing(bool restartFilm)
    {
        if (OnboardingClosingFilm is null)
        {
            return;
        }

        var speechName = _onboarding.Choices.SpeechEngine == FinalAsrEngine.Whisper ? "Whisper" : "Parakeet";
        var polishName = _onboarding.Choices.PolishChoice switch
        {
            OnboardingPolishChoiceKind.EgOne => EnginePolishPresentation.DescribeEgOneTier(
                EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId)).Title,
            OnboardingPolishChoiceKind.OpenAI => "OpenAI",
            OnboardingPolishChoiceKind.Claude => "Claude",
            OnboardingPolishChoiceKind.Gemini => "Gemini",
            OnboardingPolishChoiceKind.Ollama => "Ollama",
            _ => "Off for now",
        };
        var keyName = _onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? "Ctrl+Win";

        OnboardingClosingFilm.SpeechModelName = speechName;
        OnboardingClosingFilm.PolishName = polishName;
        OnboardingClosingFilm.KeyLabel = keyName;
        OnboardingClosingFilm.KeyCap = keyName;
        OnboardingClosingFilm.ReduceMotion = !ShouldAnimateMotion();

        if (restartFilm)
        {
            OnboardingClosingFilm.Restart();
        }

        UpdateOnboardingClosingPausePlayButton();
    }

    private void DrawOnboardingFooter(OnboardingStep step)
    {
        var (ctaText, ctaEnabled, skipText, showSkip) = step switch
        {
            OnboardingStep.Welcome => (OnboardingCopy.WelcomePrimaryCta, true, string.Empty, false),
            OnboardingStep.PolishIntro => ("Set up AI polish", true, "Skip AI polish", true),
            OnboardingStep.Permissions => ("Continue", _onboarding.MicrophoneAllowed, string.Empty, false),
            OnboardingStep.GettingReady when _onboarding.CanSkipAheadDuringGate => (OnboardingCopy.GateSkipAhead, true, OnboardingCopy.GateContinueWithoutPolish, true),
            OnboardingStep.GettingReady when _onboarding.CanContinueWithoutAiPolishOnGate => (OnboardingCopy.GateContinueWithoutPolish, true, string.Empty, false),
            OnboardingStep.GettingReady => ("Continue", _onboarding.EvaluateGate() == OnboardingGateDecision.ReadyAutoSkip, string.Empty, false),
            OnboardingStep.Practice => ("Continue", _onboarding.CanFinishPractice, OnboardingCopy.PracticeSkip, _onboarding.CanSkipPractice),
            OnboardingStep.Closing => ("Start using EnviousWispr", true, string.Empty, false),
            _ => ("Continue", true, string.Empty, false),
        };

        FinishOnboardingButton.Content = ctaText;
        FinishOnboardingButton.IsEnabled = ctaEnabled;
        OnboardingSkipButton.Content = skipText;
        OnboardingSkipButton.Visibility = showSkip ? Visibility.Visible : Visibility.Collapsed;
        OnboardingFooterText.Text = OnboardingCopy.WelcomeFooterNote;
        OnboardingFooterText.Visibility = step == OnboardingStep.Welcome
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string FormatSpeechBundleSize(FinalAsrEngine engine, string? probedLabel)
    {
        var rules = WindowsSetupRules.Current;
        var modelId = engine == FinalAsrEngine.Whisper ? WhisperModelIds.Final : ParakeetModelIds.Final;
        var fallbackBytes = rules.SpeechCatalog.TryGetValue(modelId, out var entry) ? entry.SizeBytes : 0L;
        var fallbackLabel = entry?.SizeLabel ?? EnginePolishPresentation.DefaultBundleSizeLabel(engine);
        return WindowsSetupRules.FormatDownloadLabel(
            string.IsNullOrWhiteSpace(probedLabel) ? fallbackLabel : probedLabel,
            fallbackBytes);
    }

    private static string FormatEgOneBundleSize(string modelId, string? probedLabel, string defaultLabel)
    {
        var rules = WindowsSetupRules.Current;
        var fallbackBytes = rules.PolishCatalog.TryGetValue(modelId, out var entry) ? entry.SizeBytes : 0L;
        var raw = !string.IsNullOrWhiteSpace(probedLabel)
            ? probedLabel
            : entry?.SizeLabel ?? defaultLabel;
        return WindowsSetupRules.FormatDownloadLabel(raw, fallbackBytes);
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
