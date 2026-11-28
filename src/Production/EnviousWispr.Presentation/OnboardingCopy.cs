using EnviousWispr.Core.Onboarding;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>Kinds of scenes in the 7-beat closing walkthrough aligned with macOS FilmScript.</summary>
public enum OnboardingClosingBeatKind
{
    Ready,
    Apps,
    Land,
    Copied,
    Learn,
    More,
    Yours,
}

/// <summary>Static title, caption, timing, and screen-reader summary for one Closing walkthrough beat.</summary>
public sealed record OnboardingClosingBeat(
    int Index,
    OnboardingClosingBeatKind Kind,
    double DurationSeconds,
    string Title,
    string Subtitle,
    string SpokenSummaryTemplate);

/// <summary>One value proposition row displayed on the Welcome screen.</summary>
public sealed record OnboardingWelcomeValueRow(
    string Glyph,
    string Title,
    string Body);

/// <summary>Every sentence the first-run flow chooses from its state, in one place.</summary>
/// <remarks>
/// Aligned with macOS #4528 and adapted truthfully for Windows (Microphone-only permissions, Ctrl+V paste,
/// system tray icon, Your Words import, and Windows hardware notes). Must remain free of em-dashes and en-dashes.
/// </remarks>
public static class OnboardingCopy
{
    public const string WelcomeTitle = "Talk naturally.\nCapture perfectly.";

    public const string WelcomeSubtitle = "Turn spoken thoughts into polished text in the apps you use.";

    public const string WelcomePrimaryCta = "Set up dictation";

    public const string WelcomeFooterNote = "Six short steps to your first dictation.";

    public static IReadOnlyList<OnboardingWelcomeValueRow> WelcomeValueRows { get; } =
    [
        new("\uEA18", "Private", "Your voice stays on this PC."),
        new("\uE945", "Fast, even offline", "Runs locally on GPU or CPU. Works offline once models download."),
        new("\uE734", "Polished for you", "Filler words, grammar, and formatting get fixed."),
        new("\uE77B", "Free and open source", "No account. No subscription. Anonymous stats only."),
    ];

    public const string PermissionsTitle = "One quick permission";

    public const string PermissionsReadyTitle = "Microphone is ready";

    public const string PermissionsSubtitle = "Your audio never leaves this PC.";

    public const string PermissionsCardTitle = "Microphone access";

    public const string PermissionsCardBody =
        "EnviousWispr listens only while you hold or toggle your dictation key.";

    public const string SetupPaused = "Setup paused. Start it again whenever you're ready.";

    public const string TrySetupAgain = "Try setup again";

    public const string Retry = "Retry";

    public const string Cancelling = "Cancelling\u2026";

    /// <summary>The speech model row's line while nothing more specific is known.</summary>
    public const string OneTimeSetup = "One-time setup";

    /// <summary>The engine is here and would not start: said without the mechanism, with the way on.</summary>
    public const string EngineUnavailable =
        "Local transcription could not start on this PC. Try again, or check the Transcription page later.";

    public const string StartingEngine = "Starting local transcription\u2026";

    public const string WarmingTitle = "Warming up";

    public const string WarmingFailedTitle = "Nearly there";

    public const string WarmingSubtitle = "Getting the speech engine ready\nso your first go is instant.";

    public const string WarmingFailedSubtitle = "The speech engine did not finish waking up.";

    public const string WarmingFailedMessage =
        "You can try again, or skip ahead and fix it later on the Transcription page.";

    public const string WarmingCaption = "This usually takes a moment. Nothing else is needed from you.";

    public const string WarmingSkip = "In a hurry? Skip ahead";

    public const string WarmingSkipAfterFailure = "Skip and finish setup";

    public const string GateTitle = "Getting ready";

    public const string GateSkipAhead = "Skip ahead";

    public const string GateKeepWaiting = "Keep waiting";

    public const string GateContinueWithoutPolish = "Continue without AI polish";

    public const string GateSlowPolishTitle = "This download is taking longer than expected.";

    public const string GateSlowPolishBody =
        "You can skip ahead and start dictating now. AI polish keeps downloading while EnviousWispr is open and turns on by itself when it is done.";

    public const string GatePolishDownloadingNotice =
        "AI polish keeps downloading while EnviousWispr is open and turns on by itself when it is done.";

    public const string GatePolishFailedNotice =
        "The AI polish model could not be downloaded just now. You can retry, or continue without AI polish for now.";

    public const string PracticePrompt = "Um, I think we should, uh, meet tomorrow morning.";

    public const string PracticeWorkedWithoutPolishHeadline = "Dictation worked";

    public const string PracticeBasicCleanupTitle = "Basic cleanup for now";

    public const string PracticeBasicCleanupBody =
        "AI polish keeps downloading while EnviousWispr is open and turns on by itself when it is done.";

    public const string PracticeFootnote = "Your recording indicator appears while you hold the key.";

    public const string PracticeSkip = "Skip practice";

    public const string PracticeBusy = "One moment, still working on that";

    public const string TurnOnMicrophone = "Turn on the microphone";

    public const string FinishWithoutModel = "Finish setup without it";

    public static IReadOnlyList<OnboardingClosingBeat> ClosingBeats { get; } =
    [
        new(
            Index: 0,
            Kind: OnboardingClosingBeatKind.Ready,
            DurationSeconds: OnboardingClosingFilmEngine.Scenes[0].DurationSeconds,
            Title: "Congratulations! You are all set.",
            Subtitle: "Here is a quick refresher.",
            SpokenSummaryTemplate: "Congratulations! You are all set. Speech model: {speech}. AI polish: {polish}. Dictation key: {key}."),
        new(
            Index: 1,
            Kind: OnboardingClosingBeatKind.Apps,
            DurationSeconds: OnboardingClosingFilmEngine.Scenes[1].DurationSeconds,
            Title: "Works in any app with a text box.",
            Subtitle: "Email, chat, docs, code and more.",
            SpokenSummaryTemplate: "Works in any app with a text box. Email, chat, docs, code and more."),
        new(
            Index: 2,
            Kind: OnboardingClosingBeatKind.Land,
            DurationSeconds: OnboardingClosingFilmEngine.Scenes[2].DurationSeconds,
            Title: "Hold your key and talk.",
            Subtitle: "Click away if you need to. Your words land where you started.",
            SpokenSummaryTemplate: "Hold {key} and talk. Click away if you need to. Your words land where you started."),
        new(
            Index: 3,
            Kind: OnboardingClosingBeatKind.Copied,
            DurationSeconds: OnboardingClosingFilmEngine.Scenes[3].DurationSeconds,
            Title: "No text box selected?",
            Subtitle: "EnviousWispr copies your words. Press Ctrl+V wherever you want them.",
            SpokenSummaryTemplate: "No text box selected? EnviousWispr copies your words. Click any text box and press Ctrl+V to paste them. Your earlier clipboard comes back after."),
        new(
            Index: 4,
            Kind: OnboardingClosingBeatKind.Learn,
            DurationSeconds: OnboardingClosingFilmEngine.Scenes[4].DurationSeconds,
            Title: "Fix a word once.",
            Subtitle: "It heard \"twist\". You type \"Tuist\". EnviousWispr learns it.",
            SpokenSummaryTemplate: "Fix a word once and EnviousWispr learns it. Use Undo if it learned the wrong word."),
        new(
            Index: 5,
            Kind: OnboardingClosingBeatKind.More,
            DurationSeconds: OnboardingClosingFilmEngine.Scenes[5].DurationSeconds,
            Title: "And there is a lot more.",
            Subtitle: "Find all of it in Settings whenever you like.",
            SpokenSummaryTemplate: "And there is a lot more in Settings, including Transcribe a File, Snippets, Your Words, History, and Live preview."),
        new(
            Index: 6,
            Kind: OnboardingClosingBeatKind.Yours,
            DurationSeconds: OnboardingClosingFilmEngine.Scenes[6].DurationSeconds,
            Title: "You're all set.",
            Subtitle: OnboardingClosingFilmEngine.TrayTeachingText,
            SpokenSummaryTemplate: "You're all set. Find EnviousWispr in your system tray. Bring your words with you: open Settings, then Your Words, then Import."),
    ];

    public static string FormatClosingSummary(
        OnboardingClosingBeat beat,
        string speechModelName,
        string? polishName,
        string keyLabel)
    {
        ArgumentNullException.ThrowIfNull(beat);
        var effectiveSpeech = string.IsNullOrWhiteSpace(speechModelName) ? "Parakeet" : speechModelName;
        var effectivePolish = string.IsNullOrWhiteSpace(polishName) ? "Off for now" : polishName;
        var effectiveKey = string.IsNullOrWhiteSpace(keyLabel) ? "your dictation key" : keyLabel;

        return beat.SpokenSummaryTemplate
            .Replace("{speech}", effectiveSpeech, StringComparison.Ordinal)
            .Replace("{polish}", effectivePolish, StringComparison.Ordinal)
            .Replace("{key}", effectiveKey, StringComparison.Ordinal);
    }

    public static string StepOf(int step) => StepOf(step, OnboardingFlow.LegacyShellStepCount);

    public static string StepOf(int step, int totalSteps) => $"Step {step} of {totalSteps}";

    public static string StepOf(OnboardingStep step) =>
        StepOf(OnboardingFlow.PrimaryStepNumberFor(step), OnboardingFlow.PrimaryStepCount);

    /// <summary>Accessible milestone announcement matching macOS ("Step N of 6: Language").</summary>
    public static string MilestoneLabel(OnboardingStep step) =>
        OnboardingFlow.MilestoneNameFor(step) is { } name
            ? $"{StepOf(step)}: {name}"
            : StepOf(step);

    /// <summary>The recording key's checklist line.</summary>
    public static string KeyLine(OnboardingFlow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.RecordingKey is { } key
            ? $"Your key: {key}"
            : flow.RecordingKeyProblem ?? "Getting your keybind ready\u2026";
    }

    /// <summary>What the Ready screen says the key does.</summary>
    public static string KeyUsage(DictationRecordingMode mode) => mode == DictationRecordingMode.Toggle
        ? "Press to dictate. Press again to transcribe."
        : "Hold to dictate. Release to transcribe.";

    public static string PermissionsHeadline(bool microphoneAllowed) =>
        microphoneAllowed ? PermissionsReadyTitle : PermissionsTitle;

    public static string PracticeHeadline(
        OnboardingPracticeState state,
        bool succeeded,
        bool polishSkippedOrFailed = false)
    {
        const string youAreSet = "That is it. You are set.";
        var workedTitle = polishSkippedOrFailed ? PracticeWorkedWithoutPolishHeadline : youAreSet;
        return state switch
        {
            OnboardingPracticeState.CannotHear => "We cannot hear you",
            OnboardingPracticeState.Listening => "Listening\u2026",
            OnboardingPracticeState.SomethingBroke => "That did not work",
            OnboardingPracticeState.MissedTheBox => "Click the box first",
            OnboardingPracticeState.SaidNothing => "All quiet",
            OnboardingPracticeState.Worked => workedTitle,
            _ => succeeded ? workedTitle : "Time for your first dictation!",
        };
    }

    public static string PracticeWorkedWithoutPolishSubhead(string shortcut, DictationRecordingMode mode) =>
        mode == DictationRecordingMode.Toggle
            ? $"AI polish did not run on this one. Press {shortcut} and say the line again to see it."
            : $"AI polish did not run on this one. Hold {shortcut} and say the line again to see it.";

    /// <param name="shortcut">The recording key as the Ready screen showed it, so the two never disagree about its name.</param>
    public static string PracticeSubhead(
        OnboardingPracticeState state,
        bool succeeded,
        string shortcut,
        DictationRecordingMode mode,
        OnboardingTakeDelivery missedTo,
        bool polishSkippedOrFailed = false)
    {
        const string worked = "Those are your words, typed for you.\nIn any other app, click into a text box first.";
        var workedMessage = polishSkippedOrFailed ? PracticeWorkedWithoutPolishSubhead(shortcut, mode) : worked;
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
            OnboardingPracticeState.MissedTheBox => missedTo == OnboardingTakeDelivery.Delivered
                ? $"We heard you. The box was not selected, so your words went to another window.\nClick inside the box, then {(toggle ? "press" : "hold")} {shortcut} again."
                : $"We heard you. The box was not selected, so your words went to the clipboard.\nClick inside the box, then {(toggle ? "press" : "hold")} {shortcut} again.",
            OnboardingPracticeState.SaidNothing =>
                $"Your microphone is working. We just did not hear anything.\nTry {(toggle ? "pressing" : "holding")} {shortcut} and saying: {PracticePrompt}",
            OnboardingPracticeState.Worked => workedMessage,
            _ => succeeded
                ? workedMessage
                : toggle
                    ? $"Press {shortcut} and say something.\nPress it again when you are done."
                    : $"Hold {shortcut} and say something.\nLet go when you are done.",
        };
    }
}
