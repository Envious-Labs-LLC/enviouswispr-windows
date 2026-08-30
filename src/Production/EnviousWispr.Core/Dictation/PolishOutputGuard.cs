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

    /// <summary>The model wrote code, and nothing that was said was code.</summary>
    RefusedCodeShape,

    /// <summary>The model wrote a data structure, and nothing that was said was one.</summary>
    RefusedStructuredData,

    /// <summary>The person described an instruction and the model carried it out.</summary>
    /// <remarks>
    /// THE MOST EXPENSIVE FAILURE POLISH HAS, because the result reads perfectly. Somebody says "ask
    /// him to write a poem about the deadline" and gets back a poem about the deadline, which is
    /// fluent, confident, on topic, and not what they said.
    /// </remarks>
    RefusedInstructionExecuted,

    /// <summary>Most of what was said is gone, so this is a summary rather than a tidy-up.</summary>
    RefusedGutted,
}

/// <summary>What the guard decided, and the text to use if it accepted.</summary>
/// <param name="Verdict">Whether the polished text is worth showing.</param>
/// <param name="Text">
/// The text to use. On a refusal this is the input, so a caller that ignores the verdict still
/// cannot paste a hallucination.
/// </param>
public readonly record struct PolishOutputReview(PolishOutputVerdict Verdict, string Text)
{
    /// <summary>True when the polished text may be used.</summary>
    public bool Accepted => Verdict == PolishOutputVerdict.Accepted;
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
    /// How much shorter than its input a polished result may be before it is a summary.
    /// </summary>
    /// <remarks>
    /// A MODEL THAT DROPS THE FRAMING AND KEEPS THE INNER PHRASE IS NOT POLISHING. "The menu item
    /// should read AI Polish, not Apple Intelligence" coming back as "AI Polish" is fluent, correct
    /// about something, and not what was said. Forty per cent leaves ordinary filler removal alone:
    /// "So, um, yeah, the meeting went well" losing a third of its characters is the good case.
    /// </remarks>
    public const double MinimumSurvivingFraction = 0.4;

    /// <summary>Below this many characters, shortening is not measured.</summary>
    public const int MinimumLengthForShorteningCheck = 30;

    /// <summary>
    /// Phrases that describe an instruction, and the word the polished text must still contain.
    /// </summary>
    /// <remarks>
    /// A DESCRIPTION OF AN INSTRUCTION IS STILL DICTATION. Somebody saying "tell her to translate
    /// this into German" wants those words typed, and a model that translates instead has produced
    /// something confident, fluent and wrong. The test is not whether the output looks like an
    /// answer - it is whether the word that named the instruction SURVIVED. A polish keeps it; an
    /// execution replaces it with the result.
    ///
    /// THE LIST IS THE macOS ONE, phrase for phrase, because it was built from real failures rather
    /// than imagined ones. The preservation entries are the same idea from the other side: somebody
    /// who says "keep the words exactly" has asked for no transformation at all.
    /// </remarks>
    private static readonly (string Trigger, string Survivor)[] InstructionGuards =
    [
        ("write a sql query", "query"),
        ("draft a cron expression", "cron expression"),
        ("answer this question", "answer"),
        ("explain the difference", "explain"),
        ("translate this", "translate"),
        ("summarize this", "summarize"),
        ("rewrite this", "rewrite"),
        ("convert this into json", "convert"),
        ("respond with only markdown", "respond"),
        ("turn this into", "turn this"),
        ("write a poem", "poem"),
        ("brainstorm", "brainstorm"),
        ("dictate the words", "dictate"),
        ("preserve the words", "preserve"),
        ("keep the words", "keep"),
        ("keep the phrase", "keep"),
        ("create a regex", "regex"),
        ("generate a regex", "regex"),
        ("write a regex", "regex"),
        ("create a pattern", "pattern"),
    ];

    /// <summary>Lines that only appear in code.</summary>
    private static readonly string[] CodeLinePatterns =
    [
        @"^\s*import\s+[\w\.]+$",
        @"^\s*from\s+[\w\.]+\s+import\s",
        @"^\s*import\s+.+\s+from\s+[""'][^""']+[""'];?\s*$",
        @"^\s*def\s+\w+\s*\(",
        @"^\s*class\s+\w+[\s{:(]",
        @"^\s*func\s+\w+\s*\(",
        @"^\s*(public|private|internal|fileprivate)\s+(class|struct|func|enum|var|let)\s",
        @"^\s*(let|var|const)\s+\w+\s*=",
        @"^\s*#!/",
        @"^\s*#include\s+[<""]",
        @"^\s*select\b.+\bfrom\b.+$",
        @"^\s*(insert|update|delete)\b.+$",
        @"^\s*if\s+.*:$",
        @"^\s*for\s+\w+\s+in\s+.*:$",
    ];

    /// <summary>
    /// Whether to use the polished text, and if not, why not.
    /// </summary>
    /// <remarks>
    /// EVERY REFUSAL HANDS BACK THE INPUT, so a caller that reads only the text still cannot paste a
    /// hallucination. The verdict is for the log; the text is the safety.
    ///
    /// ORDER IS DELIBERATE. The shape guards run before the size guards because a model that wrote
    /// code produced something the person must never see whatever its length, and reporting it as
    /// merely too long would name the symptom instead of the fault.
    /// </remarks>
    /// <param name="input">The cleaned transcript that was sent to the model.</param>
    /// <param name="output">What came back.</param>
    public static PolishOutputReview Review(string input, string output)
    {
        ArgumentNullException.ThrowIfNull(input);

        var said = input.Trim();
        var wrote = (output ?? string.Empty).Trim();
        if (wrote.Length == 0)
        {
            return Refuse(PolishOutputVerdict.RefusedEmpty, input);
        }

        if (HasRunawayRepetition(wrote))
        {
            return Refuse(PolishOutputVerdict.RefusedRepetition, input);
        }

        // ONLY WHEN THE INPUT WAS NOT ALREADY THAT SHAPE. Somebody dictating a code review says
        // words that look like code, and refusing their polish because the output still looks like
        // code would break the case the guard is meant to protect.
        if (LooksLikeCode(wrote) && !LooksLikeCode(said))
        {
            return Refuse(PolishOutputVerdict.RefusedCodeShape, input);
        }

        if (LooksLikeStructuredData(wrote) && !LooksLikeStructuredData(said))
        {
            return Refuse(PolishOutputVerdict.RefusedStructuredData, input);
        }

        if (CarriedOutAnInstruction(said, wrote))
        {
            return Refuse(PolishOutputVerdict.RefusedInstructionExecuted, input);
        }

        if (said.Length >= MinimumLengthForGrowthCheck &&
            wrote.Length > said.Length * MaximumGrowthFactor)
        {
            return Refuse(PolishOutputVerdict.RefusedRunaway, input);
        }

        if (said.Length >= MinimumLengthForShorteningCheck &&
            wrote.Length < said.Length * MinimumSurvivingFraction)
        {
            return Refuse(PolishOutputVerdict.RefusedGutted, input);
        }

        return new PolishOutputReview(PolishOutputVerdict.Accepted, output!);
    }

    private static PolishOutputReview Refuse(PolishOutputVerdict verdict, string input) =>
        new(verdict, input);

    /// <summary>Whether the text reads as source code rather than as speech.</summary>
    /// <remarks>
    /// TWO LINES, NOT ONE. A single line that matches is a coincidence somebody could have said;
    /// two is a program. The punctuation ratio catches the rest: braces and semicolons are rare in
    /// speech and dense in code, and it only applies past fifty characters because a short string
    /// can reach any ratio by accident.
    /// </remarks>
    private static bool LooksLikeCode(string text)
    {
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            return true;
        }

        var hits = 0;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var pattern in CodeLinePatterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(
                    line,
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)))
                {
                    hits++;
                    break;
                }
            }

            if (hits >= 2)
            {
                return true;
            }
        }

        if (text.Length <= 50)
        {
            return false;
        }

        var codeCharacters = text.Count(character => character is '{' or '}' or ';');
        return (double)codeCharacters / text.Length > 0.08;
    }

    /// <summary>Whether the text is a data structure rather than a sentence.</summary>
    private static bool LooksLikeStructuredData(string text) => text.Length >= 2 &&
        ((text[0] == '{' && text[^1] == '}') || (text[0] == '[' && text[^1] == ']'));

    /// <summary>Whether a described instruction was carried out instead of written down.</summary>
    private static bool CarriedOutAnInstruction(string said, string wrote)
    {
        foreach (var (trigger, survivor) in InstructionGuards)
        {
            if (said.Contains(trigger, StringComparison.OrdinalIgnoreCase) &&
                !wrote.Contains(survivor, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
