namespace EnviousWispr.Core.Reliability;

/// <summary>What Home should say about how the previous run ended, if anything.</summary>
public enum StartupNotice
{
    /// <summary>Say nothing. The log still carries the fact.</summary>
    None,

    /// <summary>Unfinished text was restored and is waiting to be reviewed.</summary>
    RecoveredText,

    /// <summary>A recovery file exists and could not be read.</summary>
    RecoveryInvalid,

    /// <summary>Windows would not open the recovery file at all.</summary>
    RecoveryUnavailable,

    /// <summary>The previous run stopped mid-dictation and nothing was saved.</summary>
    DictationMayBeLost,
}

/// <summary>Decides what Home says about the previous run.</summary>
/// <remarks>
/// A TYPE RATHER THAN AN IF IN THE WINDOW, because this is a five-way decision over two independent
/// inputs and the only way to check it is to check the whole matrix. Inside a window that needs a
/// display to exist, none of it is reachable, and the version this replaces was verified by reading
/// the source for a phrase.
///
/// WHAT WENT WRONG BEFORE, AND IT WENT WRONG BOTH WAYS. Home used to raise "EnviousWispr did not
/// close properly last time" plus "That has now happened N times in a row" whenever the previous run
/// left no clean-exit flag and there was no text to restore. The count is not evidence of anything:
/// a closed laptop, a Restart from the Start menu, a log off and Task Manager all leave exactly the
/// trace a crash leaves, and on the test machine it reached nineteen, almost all of it a build script
/// releasing a file lock. So the product accused itself on a first screen, to a person with nothing
/// to do about it.
///
/// AND SIMPLY DELETING IT WAS ALSO WRONG, WHICH IS WHY THE MATRIX EXISTS. Recovery text is written
/// only AFTER transcription finishes, so a stop during a dictation leaves nothing to restore and
/// looks identical to an idle restart. That is the one case where somebody must be told, because
/// their words are gone and they have to say them again. Removing the banner outright removed that
/// too. The fix is to say something only when the app knows a dictation was in flight.
/// </remarks>
public static class StartupNoticeDecision
{
    /// <param name="previousRunInterrupted">The previous run recorded no clean exit.</param>
    /// <param name="previousRunWasDictating">That run ended with a dictation in flight.</param>
    /// <param name="recovery">What the recovery store had to offer this time.</param>
    public static StartupNotice For(
        bool previousRunInterrupted,
        bool previousRunWasDictating,
        RecoveryTextLoadStatus recovery)
    {
        // RECOVERY OUTRANKS THE RUN STATE, ALWAYS. Text on screen, or a file that cannot be read,
        // is specific and actionable; how the last run ended is neither. The banner this replaces
        // was raised after these and overwrote their wording with a vaguer sentence.
        if (recovery == RecoveryTextLoadStatus.Found)
        {
            return StartupNotice.RecoveredText;
        }

        if (recovery == RecoveryTextLoadStatus.Invalid)
        {
            return StartupNotice.RecoveryInvalid;
        }

        if (recovery == RecoveryTextLoadStatus.Unavailable)
        {
            return StartupNotice.RecoveryUnavailable;
        }

        // NOTHING TO RESTORE. Only now does how the run ended matter, and only if it ended on a
        // dictation - which is the difference between "your words are gone" and "your computer
        // restarted", and those must not share a sentence.
        return previousRunInterrupted && previousRunWasDictating
            ? StartupNotice.DictationMayBeLost
            : StartupNotice.None;
    }
}
