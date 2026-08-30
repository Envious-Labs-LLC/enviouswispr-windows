namespace EnviousWispr.Core.Presentation;

/// <summary>What the tray icon is showing.</summary>
/// <remarks>
/// THE TRAY ICON IS THE ONLY SURFACE THE APP OWNS THAT IS ALWAYS VISIBLE. The recording pill appears
/// over the user's work and dismisses itself; the icon does not. On macOS a person can glance at the
/// menu bar and know the app is listening. On Windows they could not: the icon was assigned once at
/// construction and never again, and the only thing that changed was a tooltip nobody has a reason to
/// hover.
///
/// FOUR STATES, MATCHING macOS RATHER THAN THE PILL. The pill has eight, because it carries a sentence
/// and a sentence can be many kinds of news. An icon at sixteen pixels can carry about four ideas, and
/// macOS picked these four after shipping. A warning or an advisory reads as idle here on purpose:
/// the text is safe and nothing is broken, so an alarming icon would be lying about the severity in
/// the one place the user cannot dismiss.
///
/// Recording is deliberately NOT audio-reactive, and macOS wrote down why: the recording overlay
/// already shows a legible meter, and at icon size reactivity only reads as the icon randomly looking
/// smaller.
/// </remarks>
public enum TrayIconState
{
    /// <summary>Nothing is happening. The app is waiting for its key.</summary>
    Idle,

    /// <summary>A recording is live.</summary>
    Recording,

    /// <summary>Work is under way and the user is waiting on it.</summary>
    Processing,

    /// <summary>Something failed, or something outside the app interrupted a dictation.</summary>
    Error,
}

/// <summary>Which icon a pill state puts in the tray.</summary>
public static class TrayIconStates
{
    /// <summary>Maps one pill state onto the four ideas an icon can carry.</summary>
    /// <remarks>
    /// ONE MAPPING, SO THE TWO SURFACES CANNOT DISAGREE ABOUT WHAT IS HAPPENING. Both the pill and the
    /// icon are driven from the same status, and a second opinion about which state means "recording"
    /// is how a user ends up watching a listening pill beside an idle icon.
    ///
    /// The default arm exists because an enum can hold a value nobody declared, and it lands on Idle:
    /// an icon that under-claims is recoverable, and one that says a recording is live when it is not
    /// tells the user their microphone is open when it is closed.
    /// </remarks>
    public static TrayIconState For(DictationOverlayState state) => state switch
    {
        DictationOverlayState.Recording => TrayIconState.Recording,
        DictationOverlayState.Processing => TrayIconState.Processing,
        DictationOverlayState.Error or DictationOverlayState.Distress => TrayIconState.Error,
        DictationOverlayState.Hidden or DictationOverlayState.Success
            or DictationOverlayState.Advisory or DictationOverlayState.Suggestion
            or DictationOverlayState.Warning => TrayIconState.Idle,
        _ => TrayIconState.Idle,
    };
}
