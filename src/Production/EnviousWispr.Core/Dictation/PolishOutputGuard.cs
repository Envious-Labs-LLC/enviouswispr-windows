namespace EnviousWispr.Core.Dictation;

/// <summary>Why a polish result was refused, or that it was accepted.</summary>
public enum PolishOutputVerdict
{
    /// <summary>Use it.</summary>
    Accepted,

    /// <summary>The model produced nothing, or nothing but whitespace.</summary>
    RefusedEmpty,

    /// <summary>The model got stuck repeating itself.</summary>
    RefusedRepetition,

    /// <summary>The model wrote far more than it was given, so it invented rather than polished.</summary>
    RefusedRunaway,
}

/// <summary>
/// Decides whether a polish result is worth showing a user, or whether the cleaned text they
/// already had is better.
/// </summary>
/// <remarks>
/// THE PRODUCT RULE THIS IMPLEMENTS. Polish is a limb; the transcript is the heart. A limb failing
/// must leave the user with the last SUCCESSFUL text, and that includes failing by producing
/// nonsense rather than by erroring. A model that gets stuck repeating a phrase does not throw, does
/// not time out and does not return an error - it returns a confident string, and every check that
/// asks "did the call succeed" says yes.
///
/// SO EVERY TEST HERE IS AGAINST THE INPUT, never against an absolute. "Long" means long compared to
/// what was dictated. A fixed character limit would refuse a genuinely long dictation and accept a
/// short hallucination, which is exactly backwards.
///
/// REFUSING IS CHEAP AND ACCEPTING IS NOT. A wrongly refused polish costs the user some tidying they
/// would have liked. A wrongly accepted hallucination is pasted into their document as if they had
/// said it. The thresholds are therefore deliberately loose: they catch a model that has plainly
/// come off the rails, not one that was merely wordier than expected.
/// </remarks>
public static class PolishOutputGuard
{
    /// <summary>
    /// How many times a phrase must repeat consecutively before the output is refused.
    /// </summary>
    /// <remarks>
    /// Three, not two. "very very" is a thing people say and a thing a transcript legitimately
    /// contains; "very very very very" is not something a polish step should be producing.
    /// </remarks>
    public const int MaximumConsecutiveRepeats = 3;

    /// <summary>
    /// How much longer than its input a polished result may be.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. Polish expands text legitimately - spelling out numbers, adding
    /// punctuation, splitting run-on sentences - and a tight bound would refuse good work. Doubling
    /// the length is beyond any of that.
    /// </remarks>
    public const double MaximumGrowthFactor = 2.5;

    /// <summary>
    /// Below this many characters, growth is not measured at all.
    /// </summary>
    /// <remarks>
    /// A three-word dictation can legitimately triple: "ok" becoming "Okay, that works." is correct
    /// polish and would fail any ratio. Short inputs are protected by the repetition test instead,
    /// which does not care about length.
    /// </remarks>
    public const int MinimumLengthForGrowthCheck = 40;

    /// <summary>
    /// Whether to use the polished text, and if not, why not.
    /// </summary>
    /// <param name="input">The cleaned transcript that was sent to the model.</param>
    /// <param name="output">What came back.</param>
    public static PolishOutputVerdict Evaluate(string input, string output)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(output))
        {
            return PolishOutputVerdict.RefusedEmpty;
        }

        if (HasRunawayRepetition(output))
        {
            return PolishOutputVerdict.RefusedRepetition;
        }

        if (input.Length >= MinimumLengthForGrowthCheck &&
            output.Length > input.Length * MaximumGrowthFactor)
        {
            return PolishOutputVerdict.RefusedRunaway;
        }

        return PolishOutputVerdict.Accepted;
    }

    /// <summary>
    /// Whether any word or short phrase repeats consecutively more than a person would.
    /// </summary>
    /// <remarks>
    /// Checks phrases of one, two and three words, because a stuck model repeats a PHRASE at least
    /// as often as it repeats a single word - "in the end in the end in the end" contains no word
    /// repeated consecutively at all, so a word-only check would pass it. That is the case a naive
    /// implementation misses and the reason this loops over phrase lengths.
    /// </remarks>
    private static bool HasRunawayRepetition(string output)
    {
        var words = output.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length < MaximumConsecutiveRepeats + 1)
        {
            return false;
        }

        for (var phraseLength = 1; phraseLength <= 3; phraseLength++)
        {
            if (RepeatsConsecutively(words, phraseLength))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RepeatsConsecutively(string[] words, int phraseLength)
    {
        var repeats = 1;
        for (var start = phraseLength; start + phraseLength <= words.Length; start += phraseLength)
        {
            if (SamePhrase(words, start - phraseLength, start, phraseLength))
            {
                if (++repeats > MaximumConsecutiveRepeats)
                {
                    return true;
                }
            }
            else
            {
                repeats = 1;
            }
        }

        return false;
    }

    private static bool SamePhrase(string[] words, int first, int second, int length)
    {
        for (var offset = 0; offset < length; offset++)
        {
            if (!string.Equals(words[first + offset], words[second + offset], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
