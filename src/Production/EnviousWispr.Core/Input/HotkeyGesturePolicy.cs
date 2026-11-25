namespace EnviousWispr.Core.Input;

/// <summary>What the user's key gesture turned out to mean.</summary>
public enum HotkeyGestureOutcome
{
    /// <summary>Nothing yet. Either no gesture, or one still in progress.</summary>
    Nothing,

    /// <summary>Hold began. Start recording and keep going while the key is down.</summary>
    HoldStarted,

    /// <summary>Hold ended. Finish the recording and deliver it.</summary>
    HoldEnded,

    /// <summary>Two taps. Start recording hands-free, with no key held.</summary>
    ToggleStarted,

    /// <summary>One tap while hands-free. Finish the recording and deliver it.</summary>
    ToggleStopped,

    /// <summary>Three taps. Throw the recording away.</summary>
    Cancelled,

    /// <summary>Shortcut chord interference or lone brief tap expired: abort quietly without recovery.</summary>
    Discarded,
}

/// <summary>
/// Turns presses of one key into hold, double-tap and triple-tap gestures.
/// </summary>
/// <remarks>
/// ONE KEY, FOUR MEANINGS, matching the iOS and macOS apps: hold to talk, double-tap to record
/// hands-free, one tap to stop when hands-free, three taps to throw it away.
///
/// THE HOLD THRESHOLD IS WHAT MAKES A MODIFIER USABLE AS THE RECORD KEY, and it is the piece that
/// was missing. Firing on key-DOWN means binding Control starts a recording before the user reaches
/// the C in Control-C. Waiting a moment, and abandoning the moment any other key arrives, separates
/// "I am holding this to talk" from "this is the first half of a shortcut". It is also what a
/// six-minute phantom recording on macOS was traced to, from an Option-only binding with no
/// threshold.
///
/// THE THRESHOLD COSTS AN ORDINARY KEY NOTHING, because it is zero for one. F8 has no shortcut to be
/// confused with, so it starts on key-down exactly as it does today. Only a binding that could be
/// the start of a shortcut pays, and only that binding.
///
/// AND THE TAP WINDOW COSTS PUSH-TO-TALK NOTHING, WHICH IS THE POINT THAT KILLED THIS FEATURE LAST
/// TIME. An earlier attempt made every dictation wait half a second after release, to see whether a
/// second press was coming - so the common path paid for a gesture most people never use, and it was
/// rightly reverted. It is avoidable: a HOLD cannot be the first tap of a double-tap, because it was
/// not a tap. Releasing a hold finalises instantly. Only a SHORT press waits, and a short press
/// recorded nothing, so the wait costs nobody anything.
///
/// STOPPING HANDS-FREE DOES WAIT, and that is unavoidable rather than an oversight: one tap and
/// three taps begin identically, so "stop" cannot be told from "cancel" until the window closes. It
/// lands after the user has stopped speaking, where a quarter of a second is invisible beside the
/// polish that follows.
/// </remarks>
/// <summary>The record binding the Keybinds page offers when somebody wants the four gestures.</summary>
/// <remarks>
/// THE GESTURES SHIPPED AND NOBODY COULD REACH THEM. Hold to talk, double tap to record hands-free,
/// one tap to stop and three taps to throw away are all built and all tested, and every one of them
/// requires a modifier binding. The default is now Ctrl+Win (#66), so a fresh install has them; this
/// is the one-press way back to them for anyone who moved to an ordinary key such as F8, and a
/// one-handed alternative to the two-key default.
///
/// RIGHT CONTROL, AND THE CHOICE IS FORCED RATHER THAN PICKED. macOS binds right Option. Alt is
/// refused by the engine because a lone Alt tap opens a window's menu bar, which rules out the
/// literal twin. Right Windows opens the Start menu on its own. Right Shift is used by the shell for
/// sticky keys. Right Control is the one bare right-hand modifier Windows itself does nothing with,
/// and leaving the LEFT one alone means every shortcut somebody already knows keeps working.
///
/// IT IS AN OFFER, NOT THE DEFAULT. Which key a fresh install starts on is a separate question and
/// belongs to whoever owns the product; this changes nothing for anybody who does not press it.
/// </remarks>
public static class HandsFreeRecordBinding
{
    /// <summary>The gesture string the offer writes into the recording keybind.</summary>
    public const string Suggested = "RightCtrl";

    /// <summary>What the button says.</summary>
    public const string OfferLabel = "Use Right Ctrl";
}

public sealed class HotkeyGesturePolicy
{
    /// <summary>How long a bare modifier must be held before it starts recording.</summary>
    /// <remarks>
    /// Long enough that reaching for Control-C never arms a recording, short enough that it does not
    /// feel like the key is ignoring you. The same figure another dictation app settled on for the
    /// same problem.
    ///
    /// NOT SYMMETRIC IF WRONG. Too long and a deliberate hold feels unresponsive, which the user
    /// notices and works around. Too short and a recording starts while they were typing a shortcut,
    /// which they may not notice until text appears somewhere.
    /// </remarks>
    public static readonly TimeSpan ModifierHoldThreshold = TimeSpan.FromMilliseconds(200);

    /// <summary>How long after a tap another tap still counts as part of the same gesture.</summary>
    /// <remarks>
    /// Windows' own double-click default is 500ms, which is generous for a deliberate gesture and
    /// adds that much to stopping a hands-free recording. 300 is comfortably reachable by anyone
    /// tapping on purpose and keeps the stop responsive.
    /// </remarks>
    public static readonly TimeSpan MultiTapWindow = TimeSpan.FromMilliseconds(300);

    private readonly uint _boundKey;
    private readonly TimeSpan _holdThreshold;

    private TimeSpan? _pressedAt;
    private bool _pressDisqualified;
    private bool _holding;
    private bool _holdConfirmed;
    private bool _tentativeRecording;
    private int _tapCount;
    private TimeSpan? _lastTapAt;
    private bool _toggleRecording;

    /// <param name="boundKey">The virtual key the user bound.</param>
    /// <param name="needsHoldThreshold">
    /// True when the bound key could begin a shortcut - a bare modifier or a modifier set. False for
    /// an ordinary key, which then starts on key-down with no delay at all.
    /// </param>
    public HotkeyGesturePolicy(uint boundKey, bool needsHoldThreshold)
    {
        _boundKey = boundKey;
        _holdThreshold = needsHoldThreshold ? ModifierHoldThreshold : TimeSpan.Zero;
    }

    /// <summary>True while hands-free recording is running.</summary>
    public bool IsRecordingHandsFree => _toggleRecording;

    /// <summary>True while the bound key is being held down as a recording.</summary>
    public bool IsHolding => _holding;

    /// <summary>True while a modifier recording started immediately on press and has not yet settled as a confirmed hold or hands-free lock.</summary>
    public bool HasTentativeRecording => _tentativeRecording;

    /// <summary>Throws away everything, including a hands-free recording that is running.</summary>
    /// <remarks>
    /// FOR A CANCEL, WHICH IS THE ONE CASE Reset IS WRONG FOR. Reset deliberately leaves hands-free
    /// running, because losing focus is not a reason to discard what somebody is still saying. A
    /// cancel is the opposite: they have said to throw it away, and leaving the hold behind means
    /// letting go of the key afterwards delivers a recording that was already cancelled.
    /// </remarks>
    public void Abandon()
    {
        Reset();
        _toggleRecording = false;
    }

    /// <summary>Clears an active hands-free recording without disturbing any physical key press in progress.</summary>
    public void EndHandsFree() => _toggleRecording = false;

    /// <summary>When the caller should next call <see cref="Elapsed"/>, or null if never.</summary>
    /// <remarks>
    /// A TAP-THEN-HOLD AND A TAP WINDOW BOTH COMPLETE WITHOUT A KEY EVENT, so the caller has to be
    /// told when to look again. Returning the deadline rather than owning a timer keeps this a pure
    /// decision that a test can drive by hand.
    /// </remarks>
    public TimeSpan? NextDeadline
    {
        get
        {
            if (_pressedAt is { } pressed && !_holdConfirmed && !_pressDisqualified &&
                (_tapCount > 0 || _toggleRecording || !_holding))
            {
                return pressed + _holdThreshold;
            }

            return _pressedAt is null && _lastTapAt is { } tapped ? tapped + MultiTapWindow : null;
        }
    }

    /// <summary>Feeds one key event in.</summary>
    /// <param name="virtualKey">The key that changed.</param>
    /// <param name="isKeyDown">True on press, false on release.</param>
    /// <param name="now">A monotonic reading. Only differences are used.</param>
    public HotkeyGestureOutcome Process(uint virtualKey, bool isKeyDown, TimeSpan now)
    {
        if (virtualKey != _boundKey)
        {
            // Any other key going down during an unconfirmed modifier hold or tentative single-tap
            // window means this was a shortcut chord or typing after a stray brush. Once the hold
            // threshold has passed, accidental key presses do not discard the active dictation.
            // Key-UP is ignored so lift order never affects shortcut safety.
            if (isKeyDown)
            {
                var holdConfirmed = _holdConfirmed ||
                    (_pressedAt is { } heldSince && now - heldSince >= _holdThreshold);
                if (_holdThreshold > TimeSpan.Zero && !_toggleRecording && !holdConfirmed &&
                    (_holding || _tentativeRecording))
                {
                    _pressDisqualified = _pressedAt is not null;
                    _holding = false;
                    _holdConfirmed = false;
                    _tentativeRecording = false;
                    _tapCount = 0;
                    _lastTapAt = null;
                    return HotkeyGestureOutcome.Discarded;
                }

                if (_pressedAt is not null && !holdConfirmed)
                {
                    _pressDisqualified = true;
                }
            }

            return HotkeyGestureOutcome.Nothing;
        }

        return isKeyDown ? PressDown(now) : PressUp(now);
    }

    /// <summary>Call when <see cref="NextDeadline"/> has passed.</summary>
    /// <remarks>
    /// Two different deadlines land here and they cannot both be pending: a press either becomes a
    /// confirmed hold or it does not, and the tap window only opens once the key is up.
    /// </remarks>
    public HotkeyGestureOutcome Elapsed(TimeSpan now)
    {
        if (_pressedAt is { } pressed)
        {
            if (!_holdConfirmed && !_pressDisqualified && now - pressed >= _holdThreshold)
            {
                _holdConfirmed = true;
                _tapCount = 0;
                _lastTapAt = null;
                if (_toggleRecording)
                {
                    _holding = true;
                    return HotkeyGestureOutcome.Nothing;
                }

                var wasTentative = _tentativeRecording;
                _tentativeRecording = false;
                _holding = true;
                return wasTentative ? HotkeyGestureOutcome.Nothing : HotkeyGestureOutcome.HoldStarted;
            }

            return HotkeyGestureOutcome.Nothing;
        }

        if (_lastTapAt is not { } tapped || now - tapped < MultiTapWindow)
        {
            return HotkeyGestureOutcome.Nothing;
        }

        var taps = _tapCount;
        _tapCount = 0;
        _lastTapAt = null;

        return ResolveTaps(taps);
    }

    /// <summary>Forgets any gesture in progress, without ending a hands-free recording.</summary>
    /// <remarks>
    /// For focus leaving the machine mid-press - a lock, a user switch, a remote session. The
    /// release never arrives, so without this the press stays open and the next release reads as a
    /// hold that lasted as long as the user was away.
    ///
    /// A HANDS-FREE RECORDING IS DELIBERATELY LEFT RUNNING. It does not depend on a key being held,
    /// so losing focus is not a reason to throw away what someone is still saying.
    /// </remarks>
    public void Reset()
    {
        _pressedAt = null;
        _pressDisqualified = false;
        _holding = false;
        _holdConfirmed = false;
        _tentativeRecording = false;
        _tapCount = 0;
        _lastTapAt = null;
    }

    private HotkeyGestureOutcome PressDown(TimeSpan now)
    {
        if (_pressedAt is not null)
        {
            // Auto-repeat while the key is held. Taking the first press as the start is what stops
            // a long hold looking like a fresh gesture every few milliseconds.
            return HotkeyGestureOutcome.Nothing;
        }

        _pressedAt = now;
        _pressDisqualified = false;

        // An ordinary key has no shortcut to be confused with, so it holds immediately and the
        // common path pays nothing for a feature it does not use.
        if (_holdThreshold == TimeSpan.Zero)
        {
            _holdConfirmed = true;
            if (_tapCount == 0)
            {
                _holding = true;
                return HotkeyGestureOutcome.HoldStarted;
            }

            return HotkeyGestureOutcome.Nothing;
        }

        // Instant start on bare modifier / modifier-set key-down without waiting 200ms. Quick
        // releases (< ModifierHoldThreshold) stay inside the multi-tap window so double-tap keeps
        // early words or a lone stray tap cleanly discards without committing.
        _holdConfirmed = false;
        if (!_toggleRecording)
        {
            _holding = true;
            if (_tapCount == 0 && !_tentativeRecording)
            {
                _tentativeRecording = true;
                return HotkeyGestureOutcome.HoldStarted;
            }
        }

        return HotkeyGestureOutcome.Nothing;
    }

    private HotkeyGestureOutcome PressUp(TimeSpan now)
    {
        if (_pressedAt is not { } pressed)
        {
            // A release with no press: the app started with the key down, or the press went to
            // another window first.
            return HotkeyGestureOutcome.Nothing;
        }

        var wasHolding = _holding;
        var disqualified = _pressDisqualified;
        var wasHoldConfirmed = _holdConfirmed || now - pressed >= _holdThreshold;
        _pressedAt = null;
        _pressDisqualified = false;
        _holding = false;
        _holdConfirmed = false;

        if (disqualified)
        {
            return HotkeyGestureOutcome.Nothing;
        }

        if (wasHoldConfirmed)
        {
            // A hold is not a tap, so it opens no window and finalises immediately.
            _tentativeRecording = false;
            _tapCount = 0;
            _lastTapAt = null;
            if (_toggleRecording)
            {
                _toggleRecording = false;
                return HotkeyGestureOutcome.ToggleStopped;
            }

            return wasHolding ? HotkeyGestureOutcome.HoldEnded : HotkeyGestureOutcome.Nothing;
        }

        _tapCount++;
        _lastTapAt = now;
        return HotkeyGestureOutcome.Nothing;
    }

    private HotkeyGestureOutcome ResolveTaps(int taps)
    {
        if (taps >= 3)
        {
            if (_toggleRecording)
            {
                _toggleRecording = false;
                _tentativeRecording = false;
                return HotkeyGestureOutcome.Cancelled;
            }

            if (_tentativeRecording)
            {
                _tentativeRecording = false;
                return HotkeyGestureOutcome.Discarded;
            }

            return HotkeyGestureOutcome.Nothing;
        }

        if (taps == 2)
        {
            if (_toggleRecording)
            {
                // Already hands-free. Two taps is not a second start, and treating it as one would
                // discard what the user has already said.
                return HotkeyGestureOutcome.Nothing;
            }

            _toggleRecording = true;
            _tentativeRecording = false;
            return HotkeyGestureOutcome.ToggleStarted;
        }

        if (taps == 1 && _toggleRecording)
        {
            _toggleRecording = false;
            return HotkeyGestureOutcome.ToggleStopped;
        }

        if (taps == 1 && _tentativeRecording)
        {
            // A single super-brief tap (< ModifierHoldThreshold) with no second tap: abort quietly
            // without transcription or recovery so stray modifier presses never commit text.
            _tentativeRecording = false;
            return HotkeyGestureOutcome.Discarded;
        }

        return HotkeyGestureOutcome.Nothing;
    }
}
