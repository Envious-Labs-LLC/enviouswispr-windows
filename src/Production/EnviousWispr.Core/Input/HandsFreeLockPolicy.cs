namespace EnviousWispr.Core.Input;

/// <summary>What a press of the record key means, beyond starting and stopping.</summary>
public enum HandsFreePressOutcome
{
    /// <summary>Nothing special. The ordinary push-to-talk rules apply.</summary>
    Ordinary,

    /// <summary>Keep recording after the key is released, so the user can put their hands down.</summary>
    Lock,

    /// <summary>Throw the recording away.</summary>
    Cancel,
}

/// <summary>
/// Reads the quick double and triple press that lock a recording on and then cancel it.
/// </summary>
/// <remarks>
/// WHAT THE GESTURE IS. Start recording, then press the same key again quickly, and the recording
/// keeps running after you let go - so you can stand up, read from a page, or use both hands. Press
/// a third time, still quickly, and it is cancelled. macOS ships this and it is why the Mac's
/// recording modes are three rather than two.
///
/// IT IS A GESTURE ON TOP OF PUSH-TO-TALK, NOT A MODE. There is nothing to choose in settings: a
/// user who never double-presses never meets it, and a user who does gets it without having found
/// a switch first. That is also why it does not apply in toggle mode - there, a second press
/// already means stop, and a gesture that quietly redefined it would break the mode a user did
/// choose.
///
/// THE WINDOW IS MEASURED FROM THE START OF THE RECORDING, not from the previous press. Measuring
/// press-to-press lets a slow triple-press drift arbitrarily far from the start, so a user who
/// pressed twice, thought about it, and pressed again five seconds later would silently cancel a
/// recording they meant to keep. Anchoring to the start bounds the whole gesture.
///
/// WHY CANCEL IS THE THIRD PRESS RATHER THAN A SEPARATE KEY. Once a recording is locked the user
/// has let go of the key, so the only thing they are still holding is the same shortcut. Escape
/// already cancels, and this is the version reachable without going back to the keyboard properly.
/// </remarks>
public sealed class HandsFreeLockPolicy
{
    /// <summary>
    /// How long after a recording starts the extra presses are read as part of the gesture.
    /// </summary>
    /// <remarks>
    /// Matches the macOS window. Long enough for a deliberate double tap, short enough that an
    /// ordinary second dictation started straight after the first is not read as a gesture on it.
    /// </remarks>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    private DateTimeOffset? _recordingStartedAt;
    private bool _locked;

    /// <summary>Whether the recording is running with the key released.</summary>
    public bool IsLocked => _locked;

    /// <summary>Call when a recording starts, whatever started it.</summary>
    public void RecordingStarted(DateTimeOffset at)
    {
        _recordingStartedAt = at;
        _locked = false;
    }

    /// <summary>Call when a recording ends, whatever ended it.</summary>
    /// <remarks>
    /// Clearing BOTH fields matters. A locked flag left set after a recording ends would make the
    /// next recording's second press cancel instead of lock, and the user would have no way to
    /// connect that to what they did a minute earlier.
    /// </remarks>
    public void RecordingEnded()
    {
        _recordingStartedAt = null;
        _locked = false;
    }

    /// <summary>
    /// What a press of the record key means right now.
    /// </summary>
    /// <param name="at">When the press happened.</param>
    /// <param name="isToggleMode">
    /// True in toggle mode, where a second press already means stop and this gesture must not apply.
    /// </param>
    public HandsFreePressOutcome Press(DateTimeOffset at, bool isToggleMode)
    {
        if (isToggleMode || _recordingStartedAt is not { } started)
        {
            return HandsFreePressOutcome.Ordinary;
        }

        if (at - started > Window)
        {
            return HandsFreePressOutcome.Ordinary;
        }

        if (_locked)
        {
            return HandsFreePressOutcome.Cancel;
        }

        _locked = true;
        return HandsFreePressOutcome.Lock;
    }

    /// <summary>
    /// Whether releasing the key should end the recording.
    /// </summary>
    /// <remarks>
    /// This is the whole point of locking: after it, the release does nothing. It is asked as a
    /// question rather than inferred from IsLocked at the call site so that there is one answer to
    /// "does this release stop the recording" rather than two places deciding it.
    /// </remarks>
    public bool ReleaseEndsRecording() => !_locked;
}
