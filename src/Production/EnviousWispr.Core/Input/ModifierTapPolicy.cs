namespace EnviousWispr.Core.Input;

/// <summary>What a modifier key's press and release turned out to mean.</summary>
public enum ModifierTapOutcome
{
    /// <summary>Nothing yet. The gesture is either not started or still in progress.</summary>
    Nothing,

    /// <summary>A deliberate tap. Start or stop recording.</summary>
    Tap,

    /// <summary>The key was doing its ordinary job. Leave it alone.</summary>
    Abandoned,
}

/// <summary>
/// Decides when a modifier pressed on its own was meant as the dictation key.
/// </summary>
/// <remarks>
/// A MODIFIER CANNOT BE HELD TO TALK, AND THAT IS THE WHOLE DESIGN CONSTRAINT. Holding Control is how
/// every shortcut on the machine begins, so a hold-to-record binding on a bare modifier would arm a
/// recording at the start of copy, paste, save and every other thing the user does all day. The
/// gesture has to be one the operating system does not already own.
///
/// SO IT IS A TAP: press and release, alone, quickly. Pressing a modifier and releasing it without
/// pressing anything else is a gesture Windows itself does not use, which is exactly why it is
/// available. The one place it is nearly used is Alt, which opens a window's menu bar on a lone tap -
/// so Alt is refused as a binding rather than fought over.
///
/// THREE THINGS DISQUALIFY A TAP, and each is a real thing users do:
/// another key pressed while the modifier is down, which is a shortcut;
/// another modifier pressed, which is also a shortcut;
/// and holding it too long, which is how people reach for menus, drag-modify, and pause mid-thought
/// with a hand resting on the keyboard.
///
/// A DISQUALIFIED PRESS IS NEVER CONSUMED. The user's shortcut has to work exactly as it would if
/// this app were not installed. That is why the outcome is a three-value answer rather than a
/// boolean: "not a tap" and "not yet" need different handling by the caller, and collapsing them is
/// how a key gets swallowed.
/// </remarks>
public sealed class ModifierTapPolicy
{
    /// <summary>The longest a tap can last and still count as a tap.</summary>
    /// <remarks>
    /// Long enough that a deliberate tap is never missed - a comfortable tap is well under 200ms and
    /// a slow, careful one still lands inside this - and short enough that resting a hand on a
    /// modifier, or holding it while deciding what to do next, is not read as a dictation.
    ///
    /// GETTING THIS WRONG IS NOT SYMMETRIC. Too short means a user taps and nothing happens, which
    /// they notice immediately and can retry. Too long means a recording starts while they were
    /// reaching for a shortcut, which they may not notice at all until text appears somewhere.
    /// It is set nearer the short end for that reason.
    /// </remarks>
    public static readonly TimeSpan TapMaximum = TimeSpan.FromMilliseconds(400);

    private readonly uint _boundKey;
    private TimeSpan? _pressedAt;
    private bool _disqualified;

    /// <param name="boundKey">The virtual-key code of the modifier bound to dictation.</param>
    public ModifierTapPolicy(uint boundKey) => _boundKey = boundKey;

    /// <summary>True while a press is in progress and could still become a tap.</summary>
    public bool IsCandidate => _pressedAt is not null && !_disqualified;

    /// <summary>
    /// Feeds one key event in and says what it meant.
    /// </summary>
    /// <param name="virtualKey">The key that changed.</param>
    /// <param name="isKeyDown">True on press, false on release.</param>
    /// <param name="timestamp">A monotonic reading. Only differences are used.</param>
    public ModifierTapOutcome Process(uint virtualKey, bool isKeyDown, TimeSpan timestamp)
    {
        if (virtualKey != _boundKey)
        {
            // ANY other key going down during the press means this was a shortcut. Key-UP is
            // ignored on purpose: releasing a key that went down BEFORE the modifier says nothing
            // about what the modifier is for, and treating it as a disqualifier would make the
            // gesture fail depending on the order a user lifts their fingers.
            if (isKeyDown && _pressedAt is not null)
            {
                _disqualified = true;
            }

            return ModifierTapOutcome.Nothing;
        }

        if (isKeyDown)
        {
            // Auto-repeat sends the same key down again while it is held. Taking the first press as
            // the start is what stops a long hold looking like a fresh tap every few milliseconds.
            _pressedAt ??= timestamp;
            return ModifierTapOutcome.Nothing;
        }

        if (_pressedAt is not { } pressedAt)
        {
            // A release with no press. Happens when the app starts with the key already down, or
            // when a press went to another window first.
            return ModifierTapOutcome.Nothing;
        }

        var held = timestamp - pressedAt;
        var disqualified = _disqualified;
        _pressedAt = null;
        _disqualified = false;

        return disqualified || held > TapMaximum
            ? ModifierTapOutcome.Abandoned
            : ModifierTapOutcome.Tap;
    }

    /// <summary>Forgets any press in progress.</summary>
    /// <remarks>
    /// Needed when focus leaves the machine mid-press - a lock, a switch to another user, a remote
    /// session taking over. Without it the release never arrives, the press stays open, and the next
    /// release of that key reads as a tap that lasted however long the user was away.
    /// </remarks>
    public void Reset()
    {
        _pressedAt = null;
        _disqualified = false;
    }
}
