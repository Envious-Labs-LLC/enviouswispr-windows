using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Onboarding;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>Live state of local transcription for onboarding checklist, gate, and retry logic.</summary>
public enum SpeechEngineReadiness
{
    Preparing,
    Ready,
    NotInstalled,
    Unavailable,
}

/// <summary>Live delivery state for a model needed by onboarding.</summary>
public enum SpeechModelDelivery
{
    Unknown,
    Missing,
    Downloading,
    Activating,
    Failed,
    CannotDownload,
    Cancelled,
    Installed,
}

public enum OnboardingChecklistStatus
{
    Pending,
    InProgress,
    Completed,
    Error,
}

public enum OnboardingWarmingOutcome
{
    Waiting,
    Ready,
    Failed,
}

/// <summary>Result of evaluating the GettingReady gate step before Practice.</summary>
public enum OnboardingGateDecision
{
    /// <summary>Speech model + engine, selected polish model (if any), and microphone are all ready; auto-skip Gate.</summary>
    ReadyAutoSkip,

    /// <summary>Speech and microphone are ready while local EG-1 is still downloading; user can wait or Skip ahead.</summary>
    WaitingWithPolishDownloading,

    /// <summary>Speech and microphone are ready while local EG-1 download failed; user can retry or Continue without AI polish.</summary>
    PolishFailedContinueWithout,

    /// <summary>Speech model/engine or microphone permission is still pending or failed.</summary>
    PendingOrBlocked,
}

public enum OnboardingPracticeState
{
    Waiting,
    Listening,
    Worked,
    SaidNothing,
    MissedTheBox,
    SomethingBroke,
    CannotHear,
}

public enum OnboardingExit
{
    SkipPractice,
    SkipWarming,
    SkipUnavailableModel,
    SkipAheadWithoutPolish,
    ContinueWithoutPolish,
    FinishSetup,
}

public enum OnboardingTakeDelivery
{
    None,
    Delivered,
    Clipboard,
    Failed,
}

public static class OnboardingTakeDeliveries
{
    public static OnboardingTakeDelivery For(DeliveryLanding landing) => landing switch
    {
        DeliveryLanding.Landed => OnboardingTakeDelivery.Delivered,
        DeliveryLanding.Clipboard => OnboardingTakeDelivery.Clipboard,
        _ => OnboardingTakeDelivery.Failed,
    };
}

/// <summary>
/// Full Windows first-run onboarding state machine aligned with macOS #4528:
/// 14 steps with Back stack, durable checkpoint export/restore, hardware/language recommendation choices,
/// GettingReady gate recovery, key rehearsal, Practice loop, and 7-beat Closing walkthrough.
/// </summary>
public sealed class OnboardingFlow
{
    /// <summary>Number of steps in the legacy 5-panel shell projection until Wave C replaces MainWindow.xaml.</summary>
    public const int LegacyShellStepCount = 5;

    /// <summary>Total numbered milestones along the full #4528 setup wizard (excluding terminal Done).</summary>
    public const int PrimaryStepCount = 11;

    public static readonly TimeSpan WarmingDisplayFloor = TimeSpan.FromMilliseconds(600);

    private readonly List<OnboardingStep> _backStack = [];
    private string _practiceBoxAtStart = string.Empty;
    private bool _takeFailed;
    private OnboardingTakeDelivery _takeDelivery;
    private bool _startedDownloadThisVisit;
    private bool _legacyChecklistMode;

    public OnboardingStep Step { get; private set; } = OnboardingStep.Welcome;

    public IReadOnlyList<OnboardingStep> BackStack => _backStack;

    public bool CanGoBack =>
        _backStack.Count > 0 && Step is not (OnboardingStep.Welcome or OnboardingStep.Done);

    public OnboardingChoices Choices { get; private set; } = new();

    public WindowsSetupRecommendation? Recommendation { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; private set; } = DateTimeOffset.UtcNow;

    public SpeechEngineReadiness Engine { get; private set; } = SpeechEngineReadiness.Preparing;

    public SpeechModelDelivery Model { get; private set; } = SpeechModelDelivery.Unknown;

    public SpeechModelDelivery PolishModel { get; private set; } = SpeechModelDelivery.Unknown;

    public bool CancelRequested { get; private set; }

    public MicrophoneConsent MicrophoneConsent { get; private set; } = MicrophoneConsent.Unknown;

    public bool MicrophoneAllowed => MicrophoneConsent != MicrophoneConsent.Blocked;

    public string? RecordingKey { get; private set; }

    public string? RecordingKeyProblem { get; private set; }

    public DictationRecordingMode RecordingMode { get; private set; } = DictationRecordingMode.PushToTalk;

    public bool RecordingKeyHasGestures =>
        RecordingKey is { } key &&
        HotkeyGestureParser.Parse(key) is { Succeeded: true, Gesture: { } gesture } &&
        HotkeyGestureParser.UnlocksGestures(gesture);

    public bool KeyRehearsalCompleted { get; private set; }

    public OnboardingPracticeState PracticeState { get; private set; } = OnboardingPracticeState.Waiting;

    public bool PracticeSucceeded { get; private set; }

    public OnboardingPracticeOutcome PracticeOutcome { get; private set; } = OnboardingPracticeOutcome.None;

    public OnboardingTakeDelivery MissedTo { get; private set; }

    public int ClosingBeatIndex { get; private set; }

    public static int ClosingBeatCount => OnboardingCopy.ClosingBeats.Count;

    public OnboardingClosingBeat CurrentClosingBeat =>
        OnboardingCopy.ClosingBeats[Math.Clamp(ClosingBeatIndex, 0, OnboardingCopy.ClosingBeats.Count - 1)];

    public bool TakeInFlight => PracticeState == OnboardingPracticeState.Listening;

    public bool RequiresLocalEgOne =>
        Choices.PolishChoice == OnboardingPolishChoiceKind.EgOne;

    public bool IsSpeechReady => Engine == SpeechEngineReadiness.Ready;

    public bool IsPolishReadyForGate =>
        !RequiresLocalEgOne || PolishModel == SpeechModelDelivery.Installed;

    public OnboardingChecklistStatus ModelItem => IsSpeechReady
        ? OnboardingChecklistStatus.Completed
        : Model switch
        {
            SpeechModelDelivery.Installed => Engine == SpeechEngineReadiness.Unavailable
                ? OnboardingChecklistStatus.Error
                : OnboardingChecklistStatus.InProgress,
            SpeechModelDelivery.Failed or SpeechModelDelivery.CannotDownload => OnboardingChecklistStatus.Error,
            SpeechModelDelivery.Downloading or SpeechModelDelivery.Activating => OnboardingChecklistStatus.InProgress,
            SpeechModelDelivery.Cancelled or SpeechModelDelivery.Missing => OnboardingChecklistStatus.Pending,
            _ => Engine == SpeechEngineReadiness.Unavailable
                ? OnboardingChecklistStatus.Error
                : OnboardingChecklistStatus.InProgress,
        };

    public OnboardingChecklistStatus KeyItem => RecordingKey is not null
        ? OnboardingChecklistStatus.Completed
        : RecordingKeyProblem is not null
            ? OnboardingChecklistStatus.Error
            : OnboardingChecklistStatus.InProgress;

    public bool SetupPaused => Model == SpeechModelDelivery.Cancelled && Engine != SpeechEngineReadiness.Ready;

    public bool CanRetrySetup =>
        ModelItem == OnboardingChecklistStatus.Error && Model != SpeechModelDelivery.CannotDownload;

    public bool CanFinishWithoutModel =>
        Step is OnboardingStep.Downloads or OnboardingStep.GettingReady &&
        Model == SpeechModelDelivery.CannotDownload &&
        Engine != SpeechEngineReadiness.Ready;

    public bool CanCancelSetup => Model == SpeechModelDelivery.Downloading && !CancelRequested;

    public bool ChecklistComplete => Engine == SpeechEngineReadiness.Ready;

    public bool ShouldAdvanceFromChecklist =>
        _legacyChecklistMode &&
        Step == OnboardingStep.Downloads &&
        ChecklistComplete;

    public OnboardingWarmingOutcome WarmingOutcome => Engine switch
    {
        SpeechEngineReadiness.Ready => OnboardingWarmingOutcome.Ready,
        SpeechEngineReadiness.Preparing => OnboardingWarmingOutcome.Waiting,
        _ => OnboardingWarmingOutcome.Failed,
    };

    public bool CanSkipPractice => !TakeInFlight;

    public bool CanFinishPractice => PracticeSucceeded && !TakeInFlight;

    public bool ShouldStartDownload
    {
        get
        {
            if (Step != OnboardingStep.Downloads || _startedDownloadThisVisit)
            {
                return false;
            }

            if (Engine != SpeechEngineReadiness.NotInstalled || Model != SpeechModelDelivery.Missing)
            {
                return false;
            }

            return true;
        }
    }

    public int LegacyStepNumber => Step switch
    {
        OnboardingStep.Welcome => 1,
        OnboardingStep.Downloads or OnboardingStep.Permissions => 2,
        OnboardingStep.Key => 3,
        OnboardingStep.GettingReady => 4,
        _ => 5,
    };

    public static int PrimaryStepNumberFor(OnboardingStep step) => step switch
    {
        OnboardingStep.Welcome => 1,
        OnboardingStep.Language => 2,
        OnboardingStep.SpeechPick or OnboardingStep.SpeechCompare => 3,
        OnboardingStep.PolishIntro => 4,
        OnboardingStep.PolishPick or OnboardingStep.PolishSeeAll => 5,
        OnboardingStep.Downloads => 6,
        OnboardingStep.Permissions => 7,
        OnboardingStep.Key => 8,
        OnboardingStep.GettingReady => 9,
        OnboardingStep.Practice => 10,
        OnboardingStep.Closing or OnboardingStep.Done => 11,
        _ => 1,
    };

    public OnboardingGateDecision EvaluateGate()
    {
        if (!IsSpeechReady || !MicrophoneAllowed)
        {
            return OnboardingGateDecision.PendingOrBlocked;
        }

        if (!RequiresLocalEgOne || PolishModel == SpeechModelDelivery.Installed)
        {
            return OnboardingGateDecision.ReadyAutoSkip;
        }

        if (PolishModel is SpeechModelDelivery.Failed or SpeechModelDelivery.CannotDownload)
        {
            return OnboardingGateDecision.PolishFailedContinueWithout;
        }

        return OnboardingGateDecision.WaitingWithPolishDownloading;
    }

    public bool CanSkipAheadDuringGate =>
        Step == OnboardingStep.GettingReady &&
        EvaluateGate() == OnboardingGateDecision.WaitingWithPolishDownloading;

    public bool CanContinueWithoutAiPolishOnGate =>
        Step == OnboardingStep.GettingReady &&
        EvaluateGate() == OnboardingGateDecision.PolishFailedContinueWithout;

    public bool MayFinish(OnboardingExit exit) => exit switch
    {
        OnboardingExit.SkipWarming => Step == OnboardingStep.GettingReady,
        OnboardingExit.SkipUnavailableModel => CanFinishWithoutModel,
        OnboardingExit.SkipAheadWithoutPolish => CanSkipAheadDuringGate,
        OnboardingExit.ContinueWithoutPolish => CanContinueWithoutAiPolishOnGate,
        OnboardingExit.SkipPractice => Step == OnboardingStep.Practice && CanSkipPractice,
        OnboardingExit.FinishSetup => Step is OnboardingStep.Practice or OnboardingStep.Closing && CanFinishPractice,
        _ => false,
    };

    public static AppSettings Completed(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with { HasCompletedOnboarding = true };
    }

    public static AppSettings Restarted(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with { HasCompletedOnboarding = false };
    }

    public void Begin(DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        Step = OnboardingStep.Welcome;
        _backStack.Clear();
        Choices = new OnboardingChoices(RecordingMode: RecordingMode, HotkeyGestureText: RecordingKey);
        Recommendation = null;
        StartedAtUtc = now;
        UpdatedAtUtc = now;
        _startedDownloadThisVisit = false;
        _legacyChecklistMode = false;
        CancelRequested = false;
        KeyRehearsalCompleted = false;
        ClosingBeatIndex = 0;
        ResetPractice();
    }

    public OnboardingRestoreDecision Restore(
        OnboardingCheckpoint? checkpoint,
        bool hasCompletedOnboarding,
        OnboardingStep? resumeStepOverride = null)
    {
        var decision = OnboardingRestore.Resolve(checkpoint, hasCompletedOnboarding, resumeStepOverride);
        if (!decision.ShouldResume)
        {
            Begin();
            return decision;
        }

        Step = decision.Step;
        _backStack.Clear();
        _backStack.AddRange(decision.BackStack);
        Choices = decision.Choices;
        RecordingMode = decision.Choices.RecordingMode;
        if (!string.IsNullOrWhiteSpace(decision.Choices.HotkeyGestureText))
        {
            RecordingKey = decision.Choices.HotkeyGestureText;
        }

        PracticeSucceeded = decision.PracticeSucceeded;
        PracticeOutcome = decision.PracticeOutcome;
        PracticeState = decision.Step == OnboardingStep.Practice && MicrophoneConsent == MicrophoneConsent.Blocked
            ? OnboardingPracticeState.CannotHear
            : OnboardingPracticeState.Waiting;
        MissedTo = OnboardingTakeDelivery.None;
        _takeDelivery = OnboardingTakeDelivery.None;
        _takeFailed = false;
        _legacyChecklistMode = false;
        ClosingBeatIndex = 0;
        StartedAtUtc = checkpoint!.StartedAtUtc;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        return decision;
    }

    public OnboardingCheckpoint ToCheckpoint(DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        UpdatedAtUtc = now;
        var safeChoices = Choices with
        {
            RecordingMode = RecordingMode,
            HotkeyGestureText = RecordingKey ?? Choices.HotkeyGestureText,
        };
        return new OnboardingCheckpoint(
            SchemaVersion: OnboardingCheckpoint.CurrentSchemaVersion,
            Step: Step,
            BackStack: [.. _backStack],
            Choices: safeChoices,
            PracticeSucceeded: PracticeSucceeded,
            PracticeOutcome: PracticeOutcome,
            UpdatedAtUtc: now,
            StartedAtUtc: StartedAtUtc);
    }

    public void ApplyRecommendation(WindowsSetupRecommendation recommendation, bool applyPrimaryChoices = true)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        Recommendation = recommendation;
        if (!applyPrimaryChoices)
        {
            return;
        }

        ApplySelectedChoices(
            speechEngine: recommendation.Speech.PrimaryEngine,
            speechModelId: recommendation.Speech.PrimaryModelId,
            polishChoice: OnboardingPolishChoiceKind.EgOne,
            egOneModelId: recommendation.Polish.PrimaryModelId,
            cloudModelId: Choices.CloudModelId,
            updatePolish: true);
        Touch();
    }

    public bool GoBack()
    {
        if (!CanGoBack)
        {
            return false;
        }

        if (Step == OnboardingStep.SpeechCompare)
        {
            Step = OnboardingStep.SpeechPick;
            if (_backStack.Count > 0 && _backStack[^1] == OnboardingStep.SpeechPick)
            {
                _backStack.RemoveAt(_backStack.Count - 1);
            }

            Touch();
            return true;
        }

        if (Step == OnboardingStep.PolishSeeAll)
        {
            Step = OnboardingStep.PolishPick;
            if (_backStack.Count > 0 && _backStack[^1] == OnboardingStep.PolishPick)
            {
                _backStack.RemoveAt(_backStack.Count - 1);
            }

            Touch();
            return true;
        }

        var target = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        if (target == OnboardingStep.GettingReady &&
            EvaluateGate() == OnboardingGateDecision.ReadyAutoSkip &&
            _backStack.Count > 0)
        {
            target = _backStack[^1];
            _backStack.RemoveAt(_backStack.Count - 1);
        }

        Step = target;
        Touch();
        return true;
    }

    public void AdvanceFromWelcome()
    {
        if (Step != OnboardingStep.Welcome)
        {
            return;
        }

        NavigateForwardTo(OnboardingStep.Language);
    }

    /// <summary>Minimal bridge for the untouched 5-panel MainWindow.xaml until Wave C adds full XAML steps.</summary>
    public void StartLegacyShellFromWelcome()
    {
        if (Step != OnboardingStep.Welcome)
        {
            return;
        }

        _legacyChecklistMode = true;
        NavigateForwardTo(OnboardingStep.Downloads);
    }

    public void SelectLanguage(string? languageCode, WindowsSetupRecommendation? recommendation = null)
    {
        var normalized = string.IsNullOrWhiteSpace(languageCode) ||
            string.Equals(languageCode.Trim(), LanguageCatalog.AutomaticCode, StringComparison.OrdinalIgnoreCase)
                ? LanguageCatalog.AutomaticCode
                : LanguageCatalog.TryGetEntry(languageCode, out var entry)
                    ? entry.Code
                    : LanguageCatalog.AutomaticCode;

        Choices = Choices with { LanguageCode = normalized };
        if (recommendation is not null)
        {
            ApplyRecommendation(recommendation, applyPrimaryChoices: true);
        }

        Touch();
    }

    public void ContinueFromLanguage()
    {
        if (Step != OnboardingStep.Language)
        {
            return;
        }

        NavigateForwardTo(OnboardingStep.SpeechPick);
    }

    public void SelectSpeech(FinalAsrEngine engine, string? modelId = null)
    {
        var normalizedModel = engine == FinalAsrEngine.Whisper
            ? WhisperModelIds.Final
            : ParakeetModelIds.Final;
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            normalizedModel = modelId.Trim();
        }

        ApplySelectedChoices(
            speechEngine: engine,
            speechModelId: normalizedModel);
        Touch();
    }

    public void OpenSpeechCompare()
    {
        if (Step != OnboardingStep.SpeechPick)
        {
            return;
        }

        NavigateForwardTo(OnboardingStep.SpeechCompare);
    }

    public void CloseSpeechCompare()
    {
        if (Step == OnboardingStep.SpeechCompare)
        {
            _ = GoBack();
        }
    }

    public void ContinueFromSpeechPick()
    {
        if (Step is not (OnboardingStep.SpeechPick or OnboardingStep.SpeechCompare))
        {
            return;
        }

        if (Step == OnboardingStep.SpeechCompare)
        {
            if (_backStack.Count > 0 && _backStack[^1] == OnboardingStep.SpeechPick)
            {
                _backStack.RemoveAt(_backStack.Count - 1);
            }

            _backStack.Add(OnboardingStep.SpeechPick);
            Step = OnboardingStep.PolishIntro;
            Touch();
            return;
        }

        NavigateForwardTo(OnboardingStep.PolishIntro);
    }

    public void ContinueFromPolishIntro()
    {
        if (Step != OnboardingStep.PolishIntro)
        {
            return;
        }

        NavigateForwardTo(OnboardingStep.PolishPick);
    }

    public void SelectPolish(
        OnboardingPolishChoiceKind choiceKind,
        string? egOneModelId = null,
        string? cloudModelId = null)
    {
        var normalizedEgOne = choiceKind == OnboardingPolishChoiceKind.EgOne
            ? EgOneModelCatalog.NormalizeOrDefault(egOneModelId ?? Choices.EgOneModelId)
            : null;
        var normalizedCloud = string.IsNullOrWhiteSpace(cloudModelId) ? null : cloudModelId.Trim();
        ApplySelectedChoices(
            polishChoice: choiceKind,
            egOneModelId: normalizedEgOne,
            cloudModelId: normalizedCloud,
            updatePolish: true);
        Touch();
    }

    public void OpenPolishSeeAll()
    {
        if (Step != OnboardingStep.PolishPick)
        {
            return;
        }

        NavigateForwardTo(OnboardingStep.PolishSeeAll);
    }

    public void ClosePolishSeeAll()
    {
        if (Step == OnboardingStep.PolishSeeAll)
        {
            _ = GoBack();
        }
    }

    public void ContinueFromPolishPick(bool? downloadsNeededOverride = null)
    {
        if (Step is not (OnboardingStep.PolishPick or OnboardingStep.PolishSeeAll))
        {
            return;
        }

        if (Step == OnboardingStep.PolishSeeAll)
        {
            if (_backStack.Count > 0 && _backStack[^1] == OnboardingStep.PolishPick)
            {
                _backStack.RemoveAt(_backStack.Count - 1);
            }

            _backStack.Add(OnboardingStep.PolishPick);
        }
        else
        {
            _backStack.Add(OnboardingStep.PolishPick);
        }

        var needsDownloads = downloadsNeededOverride ??
            (Model != SpeechModelDelivery.Installed || (RequiresLocalEgOne && PolishModel != SpeechModelDelivery.Installed));

        Step = needsDownloads ? OnboardingStep.Downloads : OnboardingStep.Permissions;
        Touch();
    }

    public void ContinueFromDownloads()
    {
        if (Step != OnboardingStep.Downloads)
        {
            return;
        }

        NavigateForwardTo(OnboardingStep.Permissions);
    }

    public bool AdvanceLegacyChecklistToPermissions()
    {
        if (Step != OnboardingStep.Downloads || !ChecklistComplete)
        {
            return false;
        }

        NavigateForwardTo(OnboardingStep.Permissions);
        return true;
    }

    public bool ContinueFromPermissions(MicrophoneConsent liveConsent)
    {
        MicrophoneConsent = liveConsent;
        if (Step != OnboardingStep.Permissions || !MicrophoneAllowed)
        {
            return false;
        }

        NavigateForwardTo(OnboardingStep.Key);
        return true;
    }

    public void MarkKeyRehearsalCompleted()
    {
        KeyRehearsalCompleted = true;
        Touch();
    }

    public void ContinueFromKey()
    {
        if (Step != OnboardingStep.Key)
        {
            return;
        }

        var gate = EvaluateGate();
        if (gate == OnboardingGateDecision.ReadyAutoSkip)
        {
            NavigateForwardTo(OnboardingStep.Practice);
            ResetPractice();
            return;
        }

        NavigateForwardTo(OnboardingStep.GettingReady);
    }

    public void BeginLegacyWarming()
    {
        if (Step == OnboardingStep.Key)
        {
            NavigateForwardTo(OnboardingStep.GettingReady);
        }
    }

    public bool TryAdvanceFromGate()
    {
        if (Step != OnboardingStep.GettingReady || EvaluateGate() != OnboardingGateDecision.ReadyAutoSkip)
        {
            return false;
        }

        NavigateForwardTo(OnboardingStep.Practice);
        ResetPractice();
        return true;
    }

    public bool BeginPractice()
    {
        if (Step != OnboardingStep.GettingReady || WarmingOutcome != OnboardingWarmingOutcome.Ready)
        {
            return false;
        }

        NavigateForwardTo(OnboardingStep.Practice);
        ResetPractice();
        return true;
    }

    public bool SkipAheadWithoutPolish()
    {
        if (!CanSkipAheadDuringGate)
        {
            return false;
        }

        NavigateForwardTo(OnboardingStep.Practice);
        ResetPractice();
        return true;
    }

    public bool ContinueWithoutAiPolish()
    {
        if (Step != OnboardingStep.GettingReady ||
            EvaluateGate() != OnboardingGateDecision.PolishFailedContinueWithout)
        {
            return false;
        }

        Choices = Choices with
        {
            PolishChoice = OnboardingPolishChoiceKind.None,
            EgOneModelId = null,
        };
        NavigateForwardTo(OnboardingStep.Practice);
        ResetPractice();
        return true;
    }

    public bool SkipPractice()
    {
        if (Step != OnboardingStep.Practice || !CanSkipPractice)
        {
            return false;
        }

        if (!PracticeSucceeded)
        {
            PracticeOutcome = OnboardingPracticeOutcome.Skipped;
        }

        ClosingBeatIndex = 0;
        NavigateForwardTo(OnboardingStep.Closing);
        return true;
    }

    public bool ContinueFromPractice()
    {
        if (Step != OnboardingStep.Practice || !CanFinishPractice)
        {
            return false;
        }

        ClosingBeatIndex = 0;
        NavigateForwardTo(OnboardingStep.Closing);
        return true;
    }

    public bool AdvanceClosingBeat()
    {
        if (Step != OnboardingStep.Closing)
        {
            return false;
        }

        if (ClosingBeatIndex + 1 < OnboardingCopy.ClosingBeats.Count)
        {
            ClosingBeatIndex++;
            Touch();
            return false;
        }

        CompleteClosing();
        return true;
    }

    public void CompleteClosing()
    {
        if (Step != OnboardingStep.Closing)
        {
            return;
        }

        Step = OnboardingStep.Done;
        _backStack.Clear();
        Touch();
    }

    public void DownloadStarted() => _startedDownloadThisVisit = true;

    public void RetrySetup()
    {
        CancelRequested = false;
        _startedDownloadThisVisit = false;
    }

    public void RequestCancel() => CancelRequested = true;

    public void ReportEngine(SpeechEngineReadiness readiness) => Engine = readiness;

    public void ReportModel(SpeechModelDelivery delivery)
    {
        Model = delivery;
        if (delivery != SpeechModelDelivery.Downloading)
        {
            CancelRequested = false;
        }
    }

    public void ReportPolishModel(SpeechModelDelivery delivery) => PolishModel = delivery;

    public void ReportMicrophone(MicrophoneConsent consent)
    {
        MicrophoneConsent = consent;
        if (Step != OnboardingStep.Practice)
        {
            return;
        }

        if (consent == MicrophoneConsent.Blocked)
        {
            if (!TakeInFlight)
            {
                PracticeState = OnboardingPracticeState.CannotHear;
            }
        }
        else if (PracticeState == OnboardingPracticeState.CannotHear)
        {
            PracticeState = OnboardingPracticeState.Waiting;
        }
    }

    public void ReportRecordingKey(string gesture, DictationRecordingMode mode)
    {
        RecordingKey = string.IsNullOrWhiteSpace(gesture) ? null : gesture;
        RecordingKeyProblem = null;
        RecordingMode = mode;
        Choices = Choices with
        {
            RecordingMode = mode,
            HotkeyGestureText = RecordingKey,
        };
    }

    public void ReportRecordingKeyUnavailable(string problem)
    {
        RecordingKey = null;
        RecordingKeyProblem = string.IsNullOrWhiteSpace(problem) ? null : problem;
    }

    public void TakeStarted(string boxText)
    {
        if (Step != OnboardingStep.Practice || PracticeState == OnboardingPracticeState.CannotHear)
        {
            return;
        }

        _practiceBoxAtStart = boxText ?? string.Empty;
        _takeFailed = false;
        _takeDelivery = OnboardingTakeDelivery.None;
        PracticeState = OnboardingPracticeState.Listening;
    }

    public void TakeDelivered(OnboardingTakeDelivery delivery)
    {
        if (PracticeState == OnboardingPracticeState.Listening)
        {
            _takeDelivery = delivery;
        }
    }

    public void TakeFailed()
    {
        if (PracticeState == OnboardingPracticeState.Listening)
        {
            _takeFailed = true;
        }
    }

    public void TakeEnded(string boxText)
    {
        if (PracticeState != OnboardingPracticeState.Listening)
        {
            return;
        }

        var current = boxText ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(current) &&
            !string.Equals(current, _practiceBoxAtStart, StringComparison.Ordinal))
        {
            PracticeSucceeded = true;
            PracticeOutcome = OnboardingPracticeOutcome.Worked;
            PracticeState = OnboardingPracticeState.Worked;
            return;
        }

        if (_takeFailed || _takeDelivery == OnboardingTakeDelivery.Failed)
        {
            PracticeState = OnboardingPracticeState.SomethingBroke;
            return;
        }

        if (_takeDelivery is OnboardingTakeDelivery.Delivered or OnboardingTakeDelivery.Clipboard)
        {
            MissedTo = _takeDelivery;
            PracticeState = OnboardingPracticeState.MissedTheBox;
            return;
        }

        PracticeState = OnboardingPracticeState.SaidNothing;
    }

    public void PracticeTextChanged(string boxText)
    {
        if (Step != OnboardingStep.Practice || string.IsNullOrWhiteSpace(boxText))
        {
            return;
        }

        if ((PracticeState is OnboardingPracticeState.SaidNothing or OnboardingPracticeState.MissedTheBox) &&
            !string.Equals(boxText, _practiceBoxAtStart, StringComparison.Ordinal))
        {
            PracticeSucceeded = true;
            PracticeOutcome = OnboardingPracticeOutcome.Worked;
            PracticeState = OnboardingPracticeState.Worked;
        }
    }

    private void NavigateForwardTo(OnboardingStep target)
    {
        if (Step != target)
        {
            _backStack.Add(Step);
            Step = target;
            Touch();
        }
    }

    private void ResetPractice()
    {
        PracticeState = MicrophoneConsent == MicrophoneConsent.Blocked && Step == OnboardingStep.Practice
            ? OnboardingPracticeState.CannotHear
            : OnboardingPracticeState.Waiting;
        PracticeSucceeded = false;
        PracticeOutcome = OnboardingPracticeOutcome.None;
        MissedTo = OnboardingTakeDelivery.None;
        _practiceBoxAtStart = string.Empty;
        _takeDelivery = OnboardingTakeDelivery.None;
        _takeFailed = false;
    }

    private void ApplySelectedChoices(
        FinalAsrEngine? speechEngine = null,
        string? speechModelId = null,
        OnboardingPolishChoiceKind? polishChoice = null,
        string? egOneModelId = null,
        string? cloudModelId = null,
        bool updatePolish = false)
    {
        var targetSpeechEngine = speechEngine ?? Choices.SpeechEngine;
        var targetSpeechModelId = speechModelId ?? Choices.SpeechModelId;
        var speechChanged =
            targetSpeechEngine != Choices.SpeechEngine ||
            !string.Equals(targetSpeechModelId, Choices.SpeechModelId, StringComparison.Ordinal);

        var targetPolishChoice = updatePolish ? polishChoice!.Value : Choices.PolishChoice;
        var targetEgOneModelId = updatePolish ? egOneModelId : Choices.EgOneModelId;
        var targetCloudModelId = updatePolish ? cloudModelId : Choices.CloudModelId;
        var polishChanged = updatePolish && (
            targetPolishChoice != Choices.PolishChoice ||
            !string.Equals(targetEgOneModelId, Choices.EgOneModelId, StringComparison.Ordinal));

        Choices = Choices with
        {
            SpeechEngine = targetSpeechEngine,
            SpeechModelId = targetSpeechModelId,
            PolishChoice = targetPolishChoice,
            EgOneModelId = targetEgOneModelId,
            CloudModelId = targetCloudModelId,
        };

        if (speechChanged)
        {
            Engine = SpeechEngineReadiness.Preparing;
            Model = SpeechModelDelivery.Unknown;
            _startedDownloadThisVisit = false;
            CancelRequested = false;
        }

        if (polishChanged)
        {
            PolishModel = SpeechModelDelivery.Unknown;
            _startedDownloadThisVisit = false;
            CancelRequested = false;
        }
    }

    private void Touch() => UpdatedAtUtc = DateTimeOffset.UtcNow;
}
