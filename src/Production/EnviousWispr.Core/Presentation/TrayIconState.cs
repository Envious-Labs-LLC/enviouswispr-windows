namespace EnviousWispr.Core.Presentation;

/// <summary>What the tray icon is showing.</summary>
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
    public static TrayIconState For(DictationOverlayState state) => state switch
    {
        DictationOverlayState.Recording => TrayIconState.Recording,
        DictationOverlayState.Processing or DictationOverlayState.Recovering => TrayIconState.Processing,
        DictationOverlayState.Error or DictationOverlayState.Distress => TrayIconState.Error,
        DictationOverlayState.Hidden
            or DictationOverlayState.ColdStart
            or DictationOverlayState.EngineReady
            or DictationOverlayState.ImportStatus
            or DictationOverlayState.Success
            or DictationOverlayState.Advisory
            or DictationOverlayState.Suggestion
            or DictationOverlayState.Warning
            or DictationOverlayState.RetainedPaste
            or DictationOverlayState.Busy
            or DictationOverlayState.EscapeRecovery
            or DictationOverlayState.CorrectionLearned
            or DictationOverlayState.BluetoothCard
            or DictationOverlayState.PolishSetupCard => TrayIconState.Idle,
        _ => TrayIconState.Idle,
    };
}
