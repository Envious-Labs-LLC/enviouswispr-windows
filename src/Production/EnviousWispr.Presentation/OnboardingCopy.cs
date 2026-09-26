using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>Every sentence the first-run flow chooses from its state, in one place.</summary>
/// <remarks>
/// macOS's WORDS WHERE macOS HAS THEM. The practice sentences are <c>PracticeScreenCopy</c> in
/// <c>PracticeScreen.swift</c>; the warm-up sentences are <c>WarmingScreenV2</c>; the setup sentences are
/// <c>OnboardingV2View.swift</c>. A sentence differs only where the Mac one would be untrue here, and each such
/// place says why. Sentences fixed in the window's markup are not repeated here; these are the ones chosen
/// from state, which is what makes them worth testing without a window.
/// </remarks>
public static class OnboardingCopy
{
    public const string SetupPaused = "Setup paused. Start it again whenever you're ready.";

    public const string TrySetupAgain = "Try setup again";

    public const string Retry = "Retry";

    public const string Cancelling = "Cancelling…";

    /// <summary>The speech model row's line while nothing more specific is known.</summary>
    public const string OneTimeSetup = "One-time setup";

    /// <summary>The engine is here and would not start: said without the mechanism, with the way on.</summary>
    public const string EngineUnavailable =
        "Local transcription could not start on this PC. Try again, or check the Transcription page later.";

    public const string StartingEngine = "Starting local transcription…";

    public const string WarmingTitle = "Warming up";

    public const string WarmingFailedTitle = "Nearly there";

    public const string WarmingSubtitle = "Getting the speech engine ready\nso your first go is instant.";

    public const string WarmingFailedSubtitle = "The speech engine did not finish waking up.";

    /// <summary>
    /// NOT macOS's WORDS, BECAUSE macOS's PROMISE IS NOT TRUE HERE. macOS says the first press may just wake the
    /// engine and the next will dictate; on Windows an engine that failed to start stays down until it is
    /// started again, so a press would not wake it.
    /// </summary>
    public const string WarmingFailedMessage =
        "You can try again, or skip ahead and fix it later on the Transcription page.";

    public const string WarmingCaption = "This usually takes a moment. Nothing else is needed from you.";

    public const string WarmingSkip = "In a hurry? Skip ahead";

    public const string WarmingSkipAfterFailure = "Skip and finish setup";

    public const string PracticeFootnote = "Your recording indicator appears while you hold the key.";

    public const string PracticeSkip = "Skip this step";

    public const string PracticeBusy = "One moment, still working on that";

    public const string TurnOnMicrophone = "Turn on the microphone";

    public const string FinishWithoutModel = "Finish setup without it";

    /// <summary>"Step 2 of 5", said the same way on screen and to a screen reader.</summary>
    public static string StepOf(int step) => $"Step {step} of {OnboardingFlow.ScreenCount}";

    /// <summary>The recording key's checklist line.</summary>
    public static string KeyLine(OnboardingFlow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.RecordingKey is { } key
            ? $"Your key: {key}"
            : flow.RecordingKeyProblem ?? "Getting your keybind ready…";
    }

    /// <summary>What the Ready screen says the key does.</summary>
    public static string KeyUsage(DictationRecordingMode mode) => mode == DictationRecordingMode.Toggle
        ? "Press to dictate. Press again to transcribe."
        : "Hold to dictate. Release to transcribe.";

    public static string PracticeHeadline(OnboardingPracticeState state, bool succeeded)
    {
        const string youAreSet = "That is it. You are set.";
        return state switch
        {
            OnboardingPracticeState.CannotHear => "We cannot hear you",
            OnboardingPracticeState.Listening => "Listening…",
            OnboardingPracticeState.SomethingBroke => "That did not work",
            OnboardingPracticeState.MissedTheBox => "Click the box first",
            OnboardingPracticeState.SaidNothing => "All quiet",
            OnboardingPracticeState.Worked => youAreSet,
            _ => succeeded ? youAreSet : "Time for your first dictation!",
        };
    }

    /// <param name="shortcut">The recording key as the Ready screen showed it, so the two never disagree about its name.</param>
    public static string PracticeSubhead(
        OnboardingPracticeState state,
        bool succeeded,
        string shortcut,
        DictationRecordingMode mode,
        OnboardingTakeDelivery missedTo)
    {
        const string worked = "Those are your words, typed for you.\nIn any other app, click into a text box first.";
        var toggle = mode == DictationRecordingMode.Toggle;
        return state switch
        {
            OnboardingPracticeState.CannotHear =>
                "EnviousWispr needs permission to use your microphone.\nYou can turn it on and come back, or skip ahead.",
            OnboardingPracticeState.Listening => toggle
                ? $"Go ahead. Press {shortcut} again when you are done."
                : $"Go ahead. Let go of {shortcut} when you are done.",
            OnboardingPracticeState.SomethingBroke =>
                "Something went wrong on our side, not yours.\nTry once more, or skip ahead and dictate anywhere.",
            // WHERE THE WORDS WENT IS SAID AS IT HAPPENED. macOS always says the clipboard, because a missed box
            // there always falls back to it; on Windows a take can also be delivered into another window.
            OnboardingPracticeState.MissedTheBox => missedTo == OnboardingTakeDelivery.Delivered
                ? $"We heard you. The box was not selected, so your words went to another window.\nClick inside the box, then {(toggle ? "press" : "hold")} {shortcut} again."
                : $"We heard you. The box was not selected, so your words went to the clipboard.\nClick inside the box, then {(toggle ? "press" : "hold")} {shortcut} again.",
            OnboardingPracticeState.SaidNothing =>
                $"Your microphone is working. We just did not hear anything.\nTry {(toggle ? "pressing" : "holding")} {shortcut} and saying: tell grandma I will call Sunday.",
            OnboardingPracticeState.Worked => worked,
            _ => succeeded
                ? worked
                : toggle
                    ? $"Press {shortcut} and say something.\nPress it again when you are done."
                    : $"Hold {shortcut} and say something.\nLet go when you are done.",
        };
    }
}
