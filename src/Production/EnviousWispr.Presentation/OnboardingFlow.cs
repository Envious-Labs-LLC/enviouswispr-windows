using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>The five first-run screens, in the order macOS shows them.</summary>
/// <remarks>
/// THE macOS ORDER AND COUNT, BECAUSE A PERSON WHO KNOWS THE MAC APP MUST RECOGNISE THIS ONE. macOS ships
/// welcome, setting up (a checklist phase, then a permissions phase), ready, warming up and try it out
/// (<c>OnboardingV2View.swift</c>, <c>OnboardingV2ViewModel.Screen</c>). Where Windows has no counterpart
/// for a macOS concern the screen stays and its content changes; the differences are written where they
/// are decided, in <see cref="OnboardingFlow"/>.
/// </remarks>
public enum OnboardingScreen
{
    Welcome,
    SettingUp,
    Ready,
    WarmingUp,
    Practice,
}

/// <summary>The two halves of the setting-up screen.</summary>
public enum OnboardingSetupPhase
{
    /// <summary>The speech model and the recording key.</summary>
    Checklist,

    /// <summary>The Windows microphone switch, the one permission Windows has for this app.</summary>
    Permission,
}

/// <summary>Whether local transcription can take a dictation right now.</summary>
/// <remarks>
/// SAID BY THE CODE THAT STARTS THE ENGINE, NOT READ OUT OF A SENTENCE. The window already shows the engine's
/// sentence on the Transcription card; a gate that matched those words would change behaviour the first time
/// somebody reworded one.
/// </remarks>
public enum SpeechEngineReadiness
{
    /// <summary>Not answered yet: the app is still starting the engine.</summary>
    Preparing,

    /// <summary>The engine is running and will take the next dictation.</summary>
    Ready,

    /// <summary>The speech model this configuration needs is not on this PC.</summary>
    NotInstalled,

    /// <summary>The model is here and the engine could not start.</summary>
    Unavailable,
}

/// <summary>Where the speech model download stands, as the delivery code reports it.</summary>
public enum SpeechModelDelivery
{
    /// <summary>Nothing reported yet.</summary>
    Unknown,

    /// <summary>Every model this configuration needs is installed and verified.</summary>
    Installed,

    /// <summary>A model is missing and can be downloaded.</summary>
    Missing,

    /// <summary>A download is running.</summary>
    Downloading,

    /// <summary>The download finished and the engine is being started with it.</summary>
    Activating,

    /// <summary>The download stopped on a failure the person can retry.</summary>
    Failed,

    /// <summary>The person cancelled the download; it resumes when they start it again.</summary>
    Cancelled,

    /// <summary>This build cannot download what it needs; retrying will not help.</summary>
    CannotDownload,
}

/// <summary>One row of the setting-up checklist.</summary>
public enum OnboardingChecklistStatus
{
    Pending,
    InProgress,
    Completed,
    Error,
}

/// <summary>What the warm-up screen has learned about the speech engine.</summary>
public enum OnboardingWarmingOutcome
{
    Waiting,
    Ready,
    Failed,
}

/// <summary>What the practice screen is showing.</summary>
/// <remarks>The macOS <c>PracticeState</c>, member for member (<c>OnboardingV2View.swift</c>).</remarks>
public enum OnboardingPracticeState
{
    /// <summary>Before the first try, or after a success.</summary>
    Waiting,

    /// <summary>A take is in flight: recording, transcribing or delivering.</summary>
    Listening,

    /// <summary>The take put words in the box.</summary>
    Worked,

    /// <summary>The take finished and produced no words. A normal state with a way forward, never an error.</summary>
    SaidNothing,

    /// <summary>The take failed on our side: transcription, capture or delivery.</summary>
    SomethingBroke,

    /// <summary>The take produced words, and they went somewhere other than the box.</summary>
    MissedTheBox,

    /// <summary>Windows is not letting the app hear anything.</summary>
    CannotHear,
}

/// <summary>The ways out of setup that finish it.</summary>
public enum OnboardingExit
{
    /// <summary>FINISH SETUP on the practice screen, after a take worked.</summary>
    FinishSetup,

    /// <summary>"Skip this step" on the practice screen.</summary>
    SkipPractice,

    /// <summary>"In a hurry? Skip ahead" or "Skip and finish setup" on the warm-up screen.</summary>
    SkipWarming,

    /// <summary>"Finish setup without it" on the checklist, offered only when this build cannot get the model at all.</summary>
    SkipUnavailableModel,
}

/// <summary>How a take's words were reported delivered, for telling a missed box from silence.</summary>
public enum OnboardingTakeDelivery
{
    /// <summary>No delivery was reported: the take produced no words, or it was cancelled.</summary>
    None,

    /// <summary>The words were delivered into a window.</summary>
    Delivered,

    /// <summary>The words were refused a window and left on the clipboard.</summary>
    Clipboard,

    /// <summary>The delivery itself failed.</summary>
    Failed,
}

/// <summary>
/// The first-run flow's decisions, without the window: which screen, what may move it on, and what the
/// practice box is saying.
/// </summary>
/// <remarks>
/// PORTED FROM <c>OnboardingV2ViewModel</c>, DECISION BY DECISION, AND THE WINDOWS DIFFERENCES ARE DECIDED HERE:
///
/// - THE SPEECH MODEL IS A GATE, AS ON macOS: the checklist moves on only when the engine answers Ready, and a
///   failed or cancelled download stops on the checklist with a way to start it again. The download starts by
///   itself when the checklist first appears, as macOS's does.
/// - macOS's "Configuring on-device AI" beat is not here. It is a visual beat that writes nothing, because Apple
///   Intelligence is already the default; Windows starts with AI polish off, so there is nothing to configure
///   and a beat claiming otherwise would be untrue. The keybind row stays, and reports the key the app is
///   actually listening for rather than a timed tick.
/// - ONE PERMISSION, NOT TWO. macOS gates on Microphone and Accessibility. Windows lets every desktop app paste
///   without a grant, so there is no Accessibility counterpart; the microphone counterpart is the Windows
///   privacy switch, which an app cannot ask for, so the row opens the page where it is turned on. A missing
///   DEVICE is reported and not gated, exactly as macOS gates the permission and not the hardware.
/// - The keybind on the Ready screen is shown, not edited: Windows applies a changed shortcut at the next launch
///   (<c>GeneralSaveOutcome.SavedMessage</c>), so a key changed here would make the practice screen teach a key
///   the app is not yet listening for.
/// - Progress is not persisted between launches. <c>HasCompletedOnboarding</c> is the one stored fact, so a
///   person who leaves part way starts at the welcome again; every later screen re-reads live readiness, and
///   the model download itself resumes where it stopped.
/// </remarks>
public sealed class OnboardingFlow
{
    /// <summary>How many screens the flow has, for the "step N of M" line.</summary>
    public const int ScreenCount = 5;

    /// <summary>How long the warm-up screen stays up once readiness has landed, so an already-warm engine reads as a beat rather than a flash.</summary>
    /// <remarks>macOS's <c>warmingDisplayFloor</c>. A display floor applied after the real signal, never a substitute for one.</remarks>
    public static readonly TimeSpan WarmingDisplayFloor = TimeSpan.FromMilliseconds(600);

    private bool _downloadStartedThisVisit;
    private string _practiceTextAtTakeStart = string.Empty;
    private OnboardingTakeDelivery _takeDelivery;
    private bool _takeFailed;

    public OnboardingScreen Screen { get; private set; } = OnboardingScreen.Welcome;

    public OnboardingSetupPhase SetupPhase { get; private set; } = OnboardingSetupPhase.Checklist;

    public SpeechEngineReadiness Engine { get; private set; } = SpeechEngineReadiness.Preparing;

    public SpeechModelDelivery Model { get; private set; } = SpeechModelDelivery.Unknown;

    public MicrophoneConsent MicrophoneConsent { get; private set; } = MicrophoneConsent.Unknown;

    /// <summary>The recording key the app is listening for, or null while it is not listening.</summary>
    public string? RecordingKey { get; private set; }

    public DictationRecordingMode RecordingMode { get; private set; } = DictationRecordingMode.PushToTalk;

    /// <summary>Why the recording key is not listening, when it is not.</summary>
    public string? RecordingKeyProblem { get; private set; }

    /// <summary>True between the person pressing Cancel on the download and the download reporting it stopped.</summary>
    public bool CancelRequested { get; private set; }

    public OnboardingPracticeState PracticeState { get; private set; } = OnboardingPracticeState.Waiting;

    /// <summary>Where a missed take's words went, for the practice copy.</summary>
    public OnboardingTakeDelivery MissedTo { get; private set; }

    /// <summary>True once the practice box has ever held words in this visit. Sticky, as on macOS.</summary>
    /// <remarks>
    /// A person who dictates and then selects all and deletes has still seen the product work, and taking
    /// FINISH SETUP away from them for tidying up would punish them for it.
    /// </remarks>
    public bool PracticeSucceeded { get; private set; }

    /// <summary>The one-based position of the current screen.</summary>
    public int StepNumber => (int)Screen + 1;

    /// <summary>True while a practice take is recording, transcribing or delivering.</summary>
    public bool TakeInFlight => PracticeState == OnboardingPracticeState.Listening;

    /// <summary>The speech model's checklist row.</summary>
    public OnboardingChecklistStatus ModelItem => Engine == SpeechEngineReadiness.Ready
        ? OnboardingChecklistStatus.Completed
        : Model switch
        {
            SpeechModelDelivery.Failed or SpeechModelDelivery.CannotDownload => OnboardingChecklistStatus.Error,
            SpeechModelDelivery.Downloading or SpeechModelDelivery.Activating => OnboardingChecklistStatus.InProgress,
            SpeechModelDelivery.Cancelled or SpeechModelDelivery.Missing => OnboardingChecklistStatus.Pending,
            // THE MODEL IS HERE AND THE ENGINE HAS NOT SAID YES. Still starting is progress; refusing to start
            // is an error with a retry, because nothing else on this screen would ever change it.
            _ => Engine == SpeechEngineReadiness.Unavailable
                ? OnboardingChecklistStatus.Error
                : OnboardingChecklistStatus.InProgress,
        };

    /// <summary>The recording key's checklist row. Reported, never a gate.</summary>
    public OnboardingChecklistStatus KeyItem => RecordingKey is not null
        ? OnboardingChecklistStatus.Completed
        : RecordingKeyProblem is not null
            ? OnboardingChecklistStatus.Error
            : OnboardingChecklistStatus.InProgress;

    /// <summary>True when the download was cancelled and waits for the person to start it again.</summary>
    public bool SetupPaused => Model == SpeechModelDelivery.Cancelled && Engine != SpeechEngineReadiness.Ready;

    /// <summary>True when the checklist has something the person can retry.</summary>
    public bool CanRetrySetup => ModelItem == OnboardingChecklistStatus.Error && Model != SpeechModelDelivery.CannotDownload;

    /// <summary>
    /// The one way past the model gate, and a Windows addition: offered only when this build says it cannot download
    /// the model it needs, so no retry can ever open the gate.
    /// </summary>
    /// <remarks>
    /// NOT A macOS BACKDOOR. macOS's checklist always has Retry, and every Windows state but this one has Retry or
    /// "Try setup again" too. Here there is nothing to retry, and the flow hides the whole app - its settings, its
    /// history, the Transcription page the error points at - so without this a person who reinstalled wrongly, or who
    /// chose "Run setup again", would be shut out of everything behind a screen that can never move on.
    /// </remarks>
    public bool CanFinishWithoutModel =>
        Screen == OnboardingScreen.SettingUp &&
        SetupPhase == OnboardingSetupPhase.Checklist &&
        Model == SpeechModelDelivery.CannotDownload &&
        Engine != SpeechEngineReadiness.Ready;

    /// <summary>True while the download can be cancelled from the checklist.</summary>
    public bool CanCancelSetup => Model == SpeechModelDelivery.Downloading && !CancelRequested;

    /// <summary>The checklist's gate: the speech engine will take a dictation.</summary>
    public bool ChecklistComplete => Engine == SpeechEngineReadiness.Ready;

    /// <summary>The permission's gate: Windows is not blocking the microphone.</summary>
    /// <remarks>
    /// UNKNOWN PASSES. A switch that could not be read says nothing either way, and refusing on it would trap a
    /// person behind a setting that may well be on - the same reason the microphone card says nothing about it.
    /// </remarks>
    public bool MicrophoneAllowed => MicrophoneConsent != MicrophoneConsent.Blocked;

    /// <summary>What the warm-up screen has learned, derived from the engine rather than waited for.</summary>
    public OnboardingWarmingOutcome WarmingOutcome => Engine switch
    {
        SpeechEngineReadiness.Ready => OnboardingWarmingOutcome.Ready,
        SpeechEngineReadiness.Preparing => OnboardingWarmingOutcome.Waiting,
        _ => OnboardingWarmingOutcome.Failed,
    };

    /// <summary>FINISH SETUP: only after a take worked, and never while one is in flight.</summary>
    public bool CanFinishPractice => PracticeSucceeded && !TakeInFlight;

    /// <summary>Skip this step: always, except while a take is in flight and the box is its target.</summary>
    public bool CanSkipPractice => !TakeInFlight;

    /// <summary>Whether the bound key has the hands-free gestures, for the Ready screen's second line.</summary>
    public bool RecordingKeyHasGestures =>
        RecordingKey is { } key &&
        HotkeyGestureParser.Parse(key) is { Succeeded: true, Gesture: { } gesture } &&
        HotkeyGestureParser.UnlocksGestures(gesture);

    /// <summary>Whether this way out may finish setup now.</summary>
    /// <remarks>
    /// THREE WAYS OUT, AS ON macOS, AND ALL THREE FINISH. The warm-up's skip is there from its first frame, never
    /// behind a delay: a gate nobody can leave is worse than the cold press it prevents. The practice's skip and
    /// FINISH SETUP are both held while a take is in flight, because leaving would take away the box the take is
    /// about to type into.
    /// </remarks>
    public bool MayFinish(OnboardingExit exit) => exit switch
    {
        OnboardingExit.SkipWarming => Screen == OnboardingScreen.WarmingUp,
        OnboardingExit.SkipUnavailableModel => CanFinishWithoutModel,
        OnboardingExit.SkipPractice => Screen == OnboardingScreen.Practice && CanSkipPractice,
        OnboardingExit.FinishSetup => Screen == OnboardingScreen.Practice && CanFinishPractice,
        _ => false,
    };

    /// <summary>The settings a finished setup stores: the one fact that is persisted, and nothing else.</summary>
    public static AppSettings Completed(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with { HasCompletedOnboarding = true };
    }

    /// <summary>The settings "Run setup again" stores, as macOS stores its restart: setup not done, everything else kept.</summary>
    public static AppSettings Restarted(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with { HasCompletedOnboarding = false };
    }

    /// <summary>Starts a visit at the welcome screen, forgetting everything a previous visit learned about the person.</summary>
    /// <remarks>What the app has learned about the machine - engine, model, microphone, key - is kept: it is still true.</remarks>
    public void Begin()
    {
        Screen = OnboardingScreen.Welcome;
        SetupPhase = OnboardingSetupPhase.Checklist;
        _downloadStartedThisVisit = false;
        CancelRequested = false;
        ResetPractice();
    }

    /// <summary>Get Started on the welcome screen.</summary>
    public void GetStarted()
    {
        if (Screen != OnboardingScreen.Welcome)
        {
            return;
        }

        Screen = OnboardingScreen.SettingUp;
        SetupPhase = OnboardingSetupPhase.Checklist;
    }

    /// <summary>
    /// True once, when the checklist is showing and the model is missing: macOS starts the download the moment
    /// the checklist appears, and so does this. The caller starts it and reports <see cref="DownloadStarted"/>.
    /// </summary>
    /// <remarks>
    /// ONCE PER VISIT. A failure or a cancel after that waits for the person's own Retry or "Try setup again";
    /// starting again by itself would turn a person's Cancel into a pause of a few milliseconds.
    /// </remarks>
    public bool ShouldStartDownload =>
        Screen == OnboardingScreen.SettingUp &&
        SetupPhase == OnboardingSetupPhase.Checklist &&
        !_downloadStartedThisVisit &&
        Model == SpeechModelDelivery.Missing;

    public void DownloadStarted() => _downloadStartedThisVisit = true;

    /// <summary>The person pressed Cancel on the download; "Cancelling…" shows until the download says it stopped.</summary>
    public void RequestCancel()
    {
        if (CanCancelSetup)
        {
            CancelRequested = true;
        }
    }

    /// <summary>True when the checklist is complete and showing, so the flow should move to the permission.</summary>
    public bool ShouldAdvanceFromChecklist =>
        Screen == OnboardingScreen.SettingUp &&
        SetupPhase == OnboardingSetupPhase.Checklist &&
        ChecklistComplete;

    /// <summary>Moves from the checklist to the permission, only through the checklist's gate.</summary>
    public bool AdvanceToPermission()
    {
        if (!ShouldAdvanceFromChecklist)
        {
            return false;
        }

        SetupPhase = OnboardingSetupPhase.Permission;
        return true;
    }

    /// <summary>Continue on the permission, with the switch read again at the moment of the press.</summary>
    /// <remarks>
    /// macOS rechecks Accessibility synchronously at Continue because a poll can be two seconds stale; the
    /// Windows switch can be turned off the same way between polls, so the press reads it again.
    /// </remarks>
    public bool ContinueFromPermission(MicrophoneConsent liveConsent)
    {
        MicrophoneConsent = liveConsent;
        if (Screen != OnboardingScreen.SettingUp || SetupPhase != OnboardingSetupPhase.Permission || !MicrophoneAllowed)
        {
            return false;
        }

        Screen = OnboardingScreen.Ready;
        return true;
    }

    /// <summary>GET STARTED! on the Ready screen: the warm-up screen, which opens only on a ready engine.</summary>
    public void BeginWarming()
    {
        if (Screen == OnboardingScreen.Ready)
        {
            Screen = OnboardingScreen.WarmingUp;
        }
    }

    /// <summary>The warm-up has shown readiness for its floor: on to the practice box.</summary>
    public bool BeginPractice()
    {
        if (Screen != OnboardingScreen.WarmingUp || WarmingOutcome != OnboardingWarmingOutcome.Ready)
        {
            return false;
        }

        Screen = OnboardingScreen.Practice;
        ResetPractice();
        return true;
    }

    public void ReportEngine(SpeechEngineReadiness readiness) => Engine = readiness;

    public void ReportModel(SpeechModelDelivery delivery)
    {
        Model = delivery;
        if (delivery != SpeechModelDelivery.Downloading)
        {
            CancelRequested = false;
        }
    }

    /// <summary>What the Windows switch says now. On the practice screen it also decides whether anything can be heard.</summary>
    public void ReportMicrophone(MicrophoneConsent consent)
    {
        MicrophoneConsent = consent;
        if (Screen != OnboardingScreen.Practice)
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

    public void ReportRecordingKey(string key, DictationRecordingMode mode)
    {
        RecordingKey = key;
        RecordingMode = mode;
        RecordingKeyProblem = null;
    }

    public void ReportRecordingKeyUnavailable(string problem)
    {
        RecordingKey = null;
        RecordingKeyProblem = problem;
    }

    /// <summary>A dictation began while the practice screen is up.</summary>
    /// <param name="boxText">The box's words at the start, so the take is judged against its own start.</param>
    public void TakeStarted(string boxText)
    {
        if (Screen != OnboardingScreen.Practice || PracticeState == OnboardingPracticeState.CannotHear)
        {
            return;
        }

        _practiceTextAtTakeStart = boxText;
        _takeDelivery = OnboardingTakeDelivery.None;
        _takeFailed = false;
        PracticeState = OnboardingPracticeState.Listening;
    }

    /// <summary>The take's delivery was reported. The last report of a take wins.</summary>
    public void TakeDelivered(OnboardingTakeDelivery delivery)
    {
        if (PracticeState == OnboardingPracticeState.Listening)
        {
            _takeDelivery = delivery;
        }
    }

    /// <summary>Something on our side failed during the take.</summary>
    public void TakeFailed()
    {
        if (PracticeState == OnboardingPracticeState.Listening)
        {
            _takeFailed = true;
        }
    }

    /// <summary>The dictation is over. Whether it produced anything is decided by the box, never by a clock.</summary>
    public void TakeEnded(string boxText)
    {
        if (PracticeState != OnboardingPracticeState.Listening)
        {
            return;
        }

        var grew = !string.Equals(boxText, _practiceTextAtTakeStart, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(boxText);
        if (grew)
        {
            PracticeSucceeded = true;
            PracticeState = OnboardingPracticeState.Worked;
            return;
        }

        // A FAILURE OUTRANKS EVERY OTHER READING: no text is a symptom of it, and describing the symptom would
        // send the person to try harder at a thing that is broken.
        if (_takeFailed || _takeDelivery == OnboardingTakeDelivery.Failed)
        {
            PracticeState = OnboardingPracticeState.SomethingBroke;
            return;
        }

        // WORDS EXISTED AND DID NOT REACH THE BOX. A delivery report is the only evidence words existed: without
        // one the take was silent, and telling a silent take "we heard you" is the defect macOS's own screen
        // was reviewed for. macOS also requires the box to have been unfocused; here the report alone decides,
        // because words delivered anywhere but the box are a miss whatever held focus, and "we did not hear
        // anything" would be false about words that were plainly heard.
        if (_takeDelivery is OnboardingTakeDelivery.Delivered or OnboardingTakeDelivery.Clipboard)
        {
            MissedTo = _takeDelivery;
            PracticeState = OnboardingPracticeState.MissedTheBox;
            return;
        }

        PracticeState = OnboardingPracticeState.SaidNothing;
    }

    /// <summary>The box's words changed, by dictation or by hand.</summary>
    /// <remarks>
    /// A PASTE CAN LAND AFTER THE TAKE IS REPORTED OVER. The synthesised paste goes through the input queue and
    /// the take's end through the window's own queue, and nothing orders the two; so words arriving just after
    /// a take was read as silent or missed are the take working, and the screen says so.
    /// </remarks>
    public void PracticeTextChanged(string boxText)
    {
        if (Screen != OnboardingScreen.Practice || string.IsNullOrWhiteSpace(boxText))
        {
            return;
        }

        PracticeSucceeded = true;
        if ((PracticeState is OnboardingPracticeState.SaidNothing or OnboardingPracticeState.MissedTheBox) &&
            !string.Equals(boxText, _practiceTextAtTakeStart, StringComparison.Ordinal))
        {
            PracticeState = OnboardingPracticeState.Worked;
        }
    }

    private void ResetPractice()
    {
        PracticeState = MicrophoneConsent == MicrophoneConsent.Blocked && Screen == OnboardingScreen.Practice
            ? OnboardingPracticeState.CannotHear
            : OnboardingPracticeState.Waiting;
        PracticeSucceeded = false;
        MissedTo = OnboardingTakeDelivery.None;
        _practiceTextAtTakeStart = string.Empty;
        _takeDelivery = OnboardingTakeDelivery.None;
        _takeFailed = false;
    }
}
