namespace EnviousWispr.Core.Presentation;

/// <summary>Which recording pill the user sees, and therefore how bad the news is.</summary>
public enum DictationOverlayState
{
    /// <summary>The status line still says this; no pill appears.</summary>
    Hidden,
    Recording,
    Processing,
    ColdStart,
    EngineReady,
    ImportStatus,
    Success,

    /// <summary>
    /// The user's setup needs attention. Deliberately NOT an error.
    /// </summary>
    Advisory,

    /// <summary>
    /// The app noticed something it could do better, and is asking.
    /// </summary>
    Suggestion,
    Warning,

    /// <summary>
    /// Something outside the app interrupted a live dictation.
    /// </summary>
    Distress,
    Error,

    /// <summary>
    /// A record press was refused because something else has the session - a file being transcribed.
    /// </summary>
    Busy,

    /// <summary>An Escape Recovery copy is waiting and the pill offers a one-shot Undo button.</summary>
    EscapeRecovery,

    /// <summary>The Self-Learning Dictionary just learned a correction and offers an Undo pill.</summary>
    CorrectionLearned,

    /// <summary>Floating theme-aware awareness card explaining Bluetooth microphone cold starts.</summary>
    BluetoothCard,

    /// <summary>Floating theme-aware card inviting the user to finish AI polish setup.</summary>
    PolishSetupCard,
}

/// <summary>Inline notice shown inside an active recording overlay surface.</summary>
public enum RecordingNoticeKind
{
    None,
    ApproachingCap,
    AutoStopUnavailable,
    MicSilentCalm,
    MicSilentStrong,
    MicMuted,
}

/// <summary>Visual variant for rich error capsules that offer a recovery action.</summary>
public enum RichErrorVariant
{
    None,
    PermissionDenied,
    MicNotResponding,
}

/// <summary>What pressing a pill's button asks the app to do.</summary>
/// <remarks>
/// AN INTENT, NOT A DESTINATION. The pill lives in Core and the pages it would send someone to are
/// named in the app, so a page tag here would make the vocabulary of the overlay depend on the
/// spelling of a navigation row. The app owns that translation, and a gate holds it to answering
/// every member.
/// </remarks>
public enum PillActionKind
{
    /// <summary>Show the page where the cleanup provider is chosen and configured.</summary>
    OpenPolishSettings,

    /// <summary>Show the page where the speech engine is chosen and installed.</summary>
    OpenTranscriptionSettings,

    /// <summary>Show the Microphone and Input tab in Dictation Settings.</summary>
    OpenMicrophoneSettings,

    /// <summary>Open Windows Microphone privacy settings to restore mic access.</summary>
    OpenMicrophonePrivacySettings,

    /// <summary>Pin recognition to the language the app keeps hearing.</summary>
    LockDetectedLanguage,

    /// <summary>Show the Transcribe a File page, where the job that has the session can be watched or stopped.</summary>
    OpenFileTranscription,

    /// <summary>Restore the Escape-cancelled dictation straight back into the field it came from.</summary>
    UndoEscapeRecovery,

    /// <summary>Undo an automatically learned correction from the Self-Learning Dictionary pill.</summary>
    UndoLearnedCorrection,
}

/// <summary>The one button a notice may carry.</summary>
/// <param name="Label">What is printed on the button.</param>
/// <param name="Kind">What pressing it asks for.</param>
/// <param name="AccessibilityLabel">
/// What a screen reader says instead, where the printed label is too terse to stand alone.
/// Null means the label speaks for itself.
/// </param>
public sealed record PillAction(string Label, PillActionKind Kind, string? AccessibilityLabel = null)
{
    /// <summary>What a screen reader should read, resolved here so two leaves cannot differ.</summary>
    public string SpokenLabel => AccessibilityLabel ?? Label;
}

/// <summary>
/// One sentence for the user, carrying the pill it is meant to appear on.
/// </summary>
public readonly record struct DictationStatus(
    string Text,
    DictationOverlayState State,
    PillAction? Action = null,
    bool DescribesTranscriptionEngine = false,
    bool IsHandsFree = false,
    string? Subtitle = null,
    RecordingNoticeKind RecordingNotice = RecordingNoticeKind.None,
    RichErrorVariant ErrorVariant = RichErrorVariant.None,
    double? DwellSeconds = null,
    PillAction? SecondaryAction = null)
{
    /// <summary>Marks a status as one the Transcription card should show.</summary>
    public DictationStatus AboutTheTranscriptionEngine() =>
        this with { DescribesTranscriptionEngine = true };

    /// <summary>Attaches an in-panel notice to a live recording status.</summary>
    public DictationStatus WithRecordingNotice(RecordingNoticeKind notice) =>
        this with { RecordingNotice = notice };

    /// <summary>Shown on the settings status line only. No pill.</summary>
    public static DictationStatus Quiet(string text) => new(text, DictationOverlayState.Hidden);

    /// <summary>A recording is live.</summary>
    public static DictationStatus Recording(string text, bool isHandsFree = false) =>
        new(text, DictationOverlayState.Recording, IsHandsFree: isHandsFree);

    /// <summary>A recording is live with an in-panel status notice.</summary>
    public static DictationStatus Recording(
        string text,
        bool isHandsFree,
        RecordingNoticeKind notice) =>
        new(text, DictationOverlayState.Recording, IsHandsFree: isHandsFree, RecordingNotice: notice);

    /// <summary>Work is under way and the user is waiting on it.</summary>
    public static DictationStatus Processing(string text) =>
        new(text, DictationOverlayState.Processing);

    /// <summary>The local engine is warming up or downloading before dictation is ready.</summary>
    public static DictationStatus ColdStart(
        string text = "Getting dictation ready...",
        string? subtitle = null) =>
        new(text, DictationOverlayState.ColdStart, Subtitle: subtitle);

    /// <summary>The speech engine finished warming up and is ready for dictation.</summary>
    public static DictationStatus EngineReady(string text = "Ready, press to dictate") =>
        new(text, DictationOverlayState.EngineReady, DwellSeconds: 1.5);

    /// <summary>A dictionary or profile import notice is shown temporarily.</summary>
    public static DictationStatus ImportStatus(string text) =>
        new(text, DictationOverlayState.ImportStatus, DwellSeconds: 3.0);

    /// <summary>The dictation arrived where it was meant to go (or copied to clipboard).</summary>
    public static DictationStatus Success(string text) =>
        new(text, DictationOverlayState.Success, DwellSeconds: 2.5);

    /// <summary>The user's setup needs attention, and the app is not what broke.</summary>
    public static DictationStatus Advisory(string text, PillAction? action = null) =>
        new(text, DictationOverlayState.Advisory, action, DwellSeconds: 8.0);

    /// <summary>The app has a suggestion about how it is being used, and is asking.</summary>
    public static DictationStatus Suggestion(string text, PillAction action) =>
        new(text, DictationOverlayState.Suggestion, action, DwellSeconds: 6.0);

    /// <summary>The app has a suggestion offering an affirmative action and an explicit dismiss button.</summary>
    public static DictationStatus Suggestion(
        string text,
        PillAction action,
        PillAction? secondaryAction) =>
        new(
            text,
            DictationOverlayState.Suggestion,
            action,
            DwellSeconds: 6.0,
            SecondaryAction: secondaryAction);

    /// <summary>The text is safe, but it did not arrive the way the user asked.</summary>
    public static DictationStatus Warning(string text) =>
        new(text, DictationOverlayState.Warning, DwellSeconds: 2.5);

    /// <summary>A press was refused because something else has the session, and the pill says what.</summary>
    public static DictationStatus Busy(string text, PillAction? action = null) =>
        new(text, DictationOverlayState.Busy, action, DwellSeconds: 6.0);

    /// <summary>Something outside the app interrupted a live dictation.</summary>
    public static DictationStatus Distress(string text) =>
        new(text, DictationOverlayState.Distress, DwellSeconds: 2.0);

    /// <summary>A cancelled dictation was saved for recovery and offers one-shot Undo.</summary>
    public static DictationStatus EscapeRecovery(string text, PillAction action) =>
        new(text, DictationOverlayState.EscapeRecovery, action, DwellSeconds: 3.0);

    /// <summary>Something failed and the user has to know.</summary>
    public static DictationStatus Error(string text) =>
        new(text, DictationOverlayState.Error, DwellSeconds: 3.0);

    /// <summary>An actionable error card with a soft-red icon badge, subtitle, and capsule button.</summary>
    public static DictationStatus RichError(
        string title,
        string subtitle,
        PillAction action,
        RichErrorVariant variant,
        double? dwellSeconds = null) =>
        new(
            title,
            DictationOverlayState.Error,
            action,
            Subtitle: subtitle,
            ErrorVariant: variant,
            DwellSeconds: dwellSeconds ?? (variant == RichErrorVariant.MicNotResponding ? 8.0 : 6.0));

    /// <summary>Floating educational card shown once per launch when a Bluetooth mic is selected.</summary>
    public static DictationStatus BluetoothCard() =>
        new(
            "Bluetooth mic detected",
            DictationOverlayState.BluetoothCard,
            Action: new("Adjust settings", PillActionKind.OpenMicrophoneSettings, "Adjust microphone settings"),
            Subtitle: "Bluetooth microphones can take a moment on a cold start.");

    /// <summary>Floating prompt card shown when AI polish needs model setup or credentials.</summary>
    public static DictationStatus PolishSetupCard(
        string title = "Polish needs setup.",
        string buttonLabel = "Set up AI polish",
        string subtitle = "Pasted without AI polish.") =>
        new(
            title,
            DictationOverlayState.PolishSetupCard,
            Action: new(buttonLabel, PillActionKind.OpenPolishSettings, "Open AI polish settings"),
            Subtitle: subtitle);

    /// <summary>The sentence, so a status can still be used where only words are wanted.</summary>
    public override string ToString() => Text;
}
