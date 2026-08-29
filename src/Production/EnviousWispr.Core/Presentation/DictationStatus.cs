namespace EnviousWispr.Core.Presentation;

/// <summary>Which recording pill the user sees, and therefore how bad the news is.</summary>
public enum DictationOverlayState
{
    /// <summary>The status line still says this; no pill appears.</summary>
    Hidden,
    Recording,
    Processing,
    Success,

    /// <summary>
    /// The user's setup needs attention. Deliberately NOT an error.
    /// </summary>
    /// <remarks>
    /// AN ERROR'S RED MARK SAYS OUR SOFTWARE BROKE, AND A SETUP PROBLEM IS NOT THAT. Ollama being
    /// switched off, or a model not installed yet, is the user's machine telling them something
    /// they can fix. Shown as an error it blames the wrong party; shown as nothing at all, which is
    /// what the Windows app did, it leaves the user with a feature that silently does less than
    /// they think. macOS separates the two for exactly this reason and writes the reason down.
    /// </remarks>
    Advisory,
    Warning,

    /// <summary>
    /// Something outside the app interrupted a live dictation.
    /// </summary>
    /// <remarks>
    /// THE INTERRUPTION LOOK. Windows suspending, the machine running out of memory, a session
    /// stopped mid-sentence by something the user did not do. It carries the error red with a
    /// deeper wash and a pulse rather than a colour of its own, because it is the same bad news
    /// arriving louder, not a different kind of bad news.
    /// </remarks>
    Distress,
    Error,
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
}

/// <summary>The one button a notice may carry.</summary>
/// <remarks>
/// WHERE macOS LETS A USER FIX THE THING FROM THE NOTICE, WINDOWS TOLD THEM AND LEFT THEM TO FIND
/// THE SETTING. The macOS pill offers Discard on a recovery and Grant on a permission toast; the
/// Windows overlay markup contained zero buttons, counted rather than assumed.
///
/// A SPOKEN LABEL SEPARATE FROM THE PRINTED ONE, because a button on a pill has room for two words
/// and a screen reader has no surrounding context to lend them meaning. macOS learned this the
/// expensive way: its Discard button spelled its accessibility label as a bare literal inside the
/// view, with no field behind it, so nothing could read it and nothing could check it.
/// </remarks>
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
/// <remarks>
/// THE STATE TRAVELS WITH THE TEXT, because the alternative was reading the text back to find it.
/// The shipped code picked the pill by matching the status sentence - <c>StartsWith("Recording")</c>,
/// <c>Contains("copied only")</c> - so rewording a sentence changed what the user saw. Rewriting
/// "Recording. Release to finish" as "Listening..." dropped through every branch to
/// <see cref="DictationOverlayState.Hidden"/> and removed the pill from a live recording, with no
/// code change and nothing able to report it. The macOS app carries the same warning in its own
/// source, one line long: inferring a visual from a string is how a copy edit silently changes an
/// icon.
///
/// A value type in Core rather than a field on the window, deliberately and for the same reason
/// macOS keeps its overlay vocabulary free of AppKit: every status the app can produce is then
/// reachable from a test with no windowing present.
///
/// Statuses that are only ever read on the settings line, never announced on a pill, say so by
/// naming <see cref="Quiet"/>. That is a decision at the call site rather than a fall-through, so a
/// status added later cannot go quiet by accident.
/// </remarks>
/// <param name="Text">The sentence shown to the user.</param>
/// <param name="State">The pill that carries it.</param>
/// <param name="Action">The one thing the user can do about it, or null.</param>
public readonly record struct DictationStatus(
    string Text,
    DictationOverlayState State,
    PillAction? Action = null)
{
    /// <summary>Shown on the settings status line only. No pill.</summary>
    public static DictationStatus Quiet(string text) => new(text, DictationOverlayState.Hidden);

    /// <summary>A recording is live.</summary>
    public static DictationStatus Recording(string text) =>
        new(text, DictationOverlayState.Recording);

    /// <summary>Work is under way and the user is waiting on it.</summary>
    public static DictationStatus Processing(string text) =>
        new(text, DictationOverlayState.Processing);

    /// <summary>The dictation arrived where it was meant to go.</summary>
    public static DictationStatus Success(string text) =>
        new(text, DictationOverlayState.Success);

    /// <summary>The user's setup needs attention, and the app is not what broke.</summary>
    /// <remarks>
    /// AN ADVISORY IS THE SEVERITY MOST WORTH A BUTTON, because it is the one whose whole content
    /// is a thing the user could go and change. Telling someone their cleanup provider is switched
    /// off and leaving them to find the page is the behaviour this overload exists to replace.
    /// </remarks>
    public static DictationStatus Advisory(string text, PillAction? action = null) =>
        new(text, DictationOverlayState.Advisory, action);

    /// <summary>The text is safe, but it did not arrive the way the user asked.</summary>
    public static DictationStatus Warning(string text) =>
        new(text, DictationOverlayState.Warning);

    /// <summary>Something outside the app interrupted a live dictation.</summary>
    public static DictationStatus Distress(string text) =>
        new(text, DictationOverlayState.Distress);

    /// <summary>Something failed and the user has to know.</summary>
    public static DictationStatus Error(string text) => new(text, DictationOverlayState.Error);

    /// <summary>The sentence, so a status can still be used where only words are wanted.</summary>
    public override string ToString() => Text;
}
