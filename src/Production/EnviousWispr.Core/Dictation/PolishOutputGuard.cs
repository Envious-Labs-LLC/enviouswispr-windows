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

    /// <summary>How much longer than its input a polished result may be.</summary>
    /// <remarks>
    /// MULTIPLIED AND THEN ADDED TO, WHICH IS WHAT MAKES IT SAFE FOR A SHORT DICTATION. A bare
    /// multiplier has to be skipped below some floor, because "ok" becoming "Okay, that works." is
    /// correct polish that fails any ratio - and skipping it left every short dictation with NO
    /// ceiling at all, so "ok" could come back as a thousand words of invention and be pasted. The
    /// fifty characters are the allowance a short input needs; the multiplier handles the rest, so
    /// nothing has to be exempt.
    ///
    /// GENEROUS ON PURPOSE. Polish expands text legitimately - spelling out numbers, adding
    /// punctuation, splitting run-on sentences - and a tight bound would refuse good work.
    /// </remarks>
    public const double MaximumGrowthFactor = 1.5;

    /// <summary>The characters allowed on top of the multiplier, so short inputs need no exemption.</summary>
    public const int GrowthAllowance = 50;

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
    /// <remarks>
    /// THE REQUEST PHRASE HAS TO SURVIVE WHOLE, not its words in any order. Requiring "answer" and
    /// "question" separately accepted "The answer to this question is Paris", which is the plainest
    /// execution there is wearing both of the words the guard was watching for. A polish keeps the
    /// request as a request; an execution replaces it with the result.
    ///
    /// ALTERNATIVES ARE LISTED RATHER THAN INFERRED. Polish legitimately rewords, so a small set of
    /// approved rephrasings is spelled out; anything outside it falls back to the input, which is the
    /// direction this guard is allowed to be wrong in.
    ///
    /// THIS IS A HEURISTIC OVER NATURAL LANGUAGE AND IT HAS HOLES IN BOTH DIRECTIONS. Three
    /// successive tightenings each closed one counterexample and opened another: a single survivor
    /// word passed "The answer is Paris"; the verb and object as unordered words passed "The answer
    /// to this question is Paris"; the whole phrase passes "I can answer this question: Paris is the
    /// capital of France" and refuses "summarize this" reworded to "summarize it". No syntactic rule
    /// closes this, because whether a sentence IS a request is not a property of which words it
    /// contains.
    ///
    /// SO THE RULE IS DELIBERATELY SIMPLE AND BIASED TO REFUSING, AND THE REAL FIX IS NOT SYNTAX.
    /// macOS carries a weaker version of this check and does the actual work with a trained
    /// classifier; Windows has no equivalent model, which is why this guard is the whole defence
    /// here rather than the cheap first pass it is there. Tightening it further trades a false
    /// refusal, which costs somebody a tidy-up, against a false acceptance, which pastes a
    /// fabrication into their document - so it stays biased toward refusing and stops being tuned.
    /// The classifier is issue #87.
    /// </remarks>
    private static readonly (string Trigger, string[] Accepted)[] InstructionGuards =
    [
        ("write a sql query", ["write a sql query", "write an sql query"]),
        ("draft a cron expression", ["draft a cron expression", "write a cron expression"]),
        ("answer this question", ["answer this question", "answer the question", "respond to this question"]),
        ("explain the difference", ["explain the difference"]),
        ("translate this", ["translate this"]),
        ("summarize this", ["summarize this", "summarise this"]),
        ("rewrite this", ["rewrite this"]),
        ("convert this into json", ["convert this into json", "convert this to json"]),
        ("respond with only markdown", ["respond with only markdown"]),
        ("turn this into", ["turn this into"]),
        ("write a poem", ["write a poem"]),
        ("brainstorm", ["brainstorm"]),
        ("dictate the words", ["dictate the words"]),
        ("preserve the words", ["preserve the words"]),
        ("keep the words", ["keep the words"]),
        ("keep the phrase", ["keep the phrase"]),
        ("create a regex", ["create a regex"]),
        ("generate a regex", ["generate a regex"]),
        ("write a regex", ["write a regex"]),
        ("create a pattern", ["create a pattern"]),
    ];

    /// <summary>First lines a model writes about the text rather than as the text.</summary>
    private static readonly string[] PreambleOpenings =
    [
        "here", "below", "the corrected", "the cleaned", "the polished", "the rewritten",
        "corrected version", "cleaned", "polished",
    ];

    /// <summary>Pleasantries a model opens with before doing as it was asked.</summary>
    private static readonly string[] Acknowledgements =
    [
        "Certainly!", "Sure!", "Sure,", "Of course!", "Got it.", "Got it!", "Absolutely!",
        "Here you go:",
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

        // STRIPPED BEFORE ANYTHING IS JUDGED, because "Sure, here is the cleaned transcript:" is
        // not something anybody said and every measurement below is about what they did say. It is
        // also what the caller uses, so the chatter cannot reach the document.
        var wrote = StripPreamble(input, output ?? string.Empty);
        if (wrote.Length == 0)
        {
            return Refuse(PolishOutputVerdict.RefusedEmpty, input);
        }

        // SIZE FIRST, SO THE WORK IS BOUNDED. Everything below reads the whole output, and the
        // pattern scan reads it line by line; a model that returned a thousand words to a two-word
        // dictation has already failed, and measuring that first means nothing else has to walk it.
        if (wrote.Length > said.Length * MaximumGrowthFactor + GrowthAllowance)
        {
            return Refuse(PolishOutputVerdict.RefusedRunaway, input);
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

        if (said.Length >= MinimumLengthForShorteningCheck &&
            wrote.Length < said.Length * MinimumSurvivingFraction)
        {
            return Refuse(PolishOutputVerdict.RefusedGutted, input);
        }

        return new PolishOutputReview(PolishOutputVerdict.Accepted, wrote);
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

        // NON-BACKTRACKING, WITH A TIMEOUT BEHIND IT, AND A TIMEOUT READS AS CODE. The engine that
        // cannot backtrack cannot be made to run away by hostile input, and the timeout is the
        // second line rather than the first. If one ever fires, the honest answer is that this text
        // could not be judged safe, and the safe direction is refusing the polish - never letting an
        // exception out of here, which would end the dictation over a pattern scan.
        var hits = 0;
        try
        {
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var pattern in CodeLinePatterns)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(
                        line,
                        pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                            System.Text.RegularExpressions.RegexOptions.CultureInvariant |
                            System.Text.RegularExpressions.RegexOptions.NonBacktracking,
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
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return true;
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
    /// <remarks>
    /// THE VERB AND ITS OBJECT, NOT ONE WORD. A single survivor let the plainest execution of all
    /// through: "answer this question about the capital" coming back as "The answer is Paris" still
    /// contains "answer", so the guard saw the word it was looking for and passed the very thing it
    /// exists to catch. Requiring the object as well - "question" - refuses it.
    ///
    /// WHOLE WORDS, so "answered" and "questionable" do not stand in for the words that were said.
    /// A legitimate rewording that drops one of them falls back to the input, which is the safe
    /// direction and the one this guard is allowed to be wrong in.
    /// </remarks>
    private static bool CarriedOutAnInstruction(string said, string wrote)
    {
        foreach (var (trigger, accepted) in InstructionGuards)
        {
            // THE TRIGGER IS A WHOLE PHRASE TOO. As a substring, "brainstorm" fired on somebody
            // saying "we brainstormed on Tuesday", which is not a request for anything.
            if (!ContainsWholeWord(said, trigger))
            {
                continue;
            }

            var survived = false;
            foreach (var phrase in accepted)
            {
                if (ContainsWholeWord(wrote, phrase))
                {
                    survived = true;
                    break;
                }
            }

            if (!survived)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsWholeWord(string text, string word)
    {
        var from = 0;
        while (from <= text.Length - word.Length)
        {
            var at = text.IndexOf(word, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }

            var beforeIsBoundary = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var after = at + word.Length;
            var afterIsBoundary = after == text.Length || !char.IsLetterOrDigit(text[after]);
            if (beforeIsBoundary && afterIsBoundary)
            {
                return true;
            }

            from = at + 1;
        }

        return false;
    }

    /// <summary>Removes what a model wrote ABOUT the text before the text itself.</summary>
    /// <remarks>
    /// "SURE, HERE IS THE CLEANED TRANSCRIPT:" IS NOT SOMETHING ANYBODY SAID, and without this it
    /// was pasted into their document along with their words. A first line only counts as a preamble
    /// when it is short, ends with a colon and opens with one of the phrases a model uses to
    /// introduce its own work - three conditions together, because any one alone describes sentences
    /// people really dictate.
    ///
    /// AN OPENING PLEASANTRY IS ONLY DROPPED WHEN WHAT FOLLOWS LOOKS LIKE A MODEL TALKING. "Sure,"
    /// begins plenty of real dictation, so it goes only when the rest is either an introduction line
    /// or a short standalone reply. Prose that runs on with commas is somebody speaking and is kept.
    /// </remarks>
    public static string StripPreamble(string said, string wrote)
    {
        ArgumentNullException.ThrowIfNull(said);
        ArgumentNullException.ThrowIfNull(wrote);
        var spoken = said.Trim();
        var result = wrote.Trim();

        foreach (var acknowledgement in Acknowledgements)
        {
            if (!result.StartsWith(acknowledgement, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // WHAT THE PERSON SAID DECIDES, AND AS A WHOLE WORD. Somebody who opened with "Sure,"
            // has that word in their dictation and removing it edits their sentence - but a prefix
            // test also read "Surely we should ship" as somebody saying "Sure", which is a different
            // word and a different meaning.
            var word = acknowledgement.TrimEnd('!', ',', '.', ':');
            if (StartsWithWholeWord(spoken, word))
            {
                break;
            }

            // ONLY WHEN A WRAPPER LINE FOLLOWS. The looser test - "and the next sentence is short" -
            // deleted "Sure," from real speech, because a short first sentence is what most speech
            // has. A line that introduces the text is the only thing that proves a model is talking.
            var rest = result[acknowledgement.Length..].Trim();
            if (FirstLineIntroducesTheText(spoken, rest))
            {
                result = rest;
            }

            break;
        }

        if (FirstLineIntroducesTheText(spoken, result))
        {
            var newline = result.IndexOf('\n', StringComparison.Ordinal);
            result = (newline < 0 ? string.Empty : result[(newline + 1)..]).Trim();
        }

        return result;
    }

    /// <remarks>
    /// IT IS ONLY A HEADING IF THE PERSON DID NOT SAY ONE. "Here is the plan:" is a sentence people
    /// dictate, and deleting their first line because a model also writes lines like it is the same
    /// mistake in the other direction.
    /// </remarks>
    private static bool FirstLineIntroducesTheText(string said, string wrote)
    {
        var newline = wrote.IndexOf('\n', StringComparison.Ordinal);
        var firstLine = (newline < 0 ? wrote : wrote[..newline]).Trim();
        if (firstLine.Length == 0 || firstLine.Length >= 100 || !firstLine.EndsWith(':'))
        {
            return false;
        }

        var opensLikeAModel = false;
        foreach (var opening in PreambleOpenings)
        {
            if (StartsWithWholeWord(firstLine, opening))
            {
                opensLikeAModel = true;
                break;
            }
        }

        if (!opensLikeAModel)
        {
            return false;
        }

        // THE WHOLE HEADING, WHEREVER IT SITS IN WHAT THEY SAID. Comparing only the opening word
        // failed in both directions: "here we agreed on Tuesday" let a model's "Here is the polished
        // transcript:" through because both begin with "here", and "Okay, here is the plan" lost its
        // own dictated heading because the words were not at position zero.
        return !ContainsWholeWord(said, firstLine.TrimEnd(':').Trim());
    }

    /// <summary>Whether the text opens with this word, and not merely with these letters.</summary>
    private static bool StartsWithWholeWord(string text, string word)
    {
        var start = text.TrimStart();
        if (!start.StartsWith(word, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var after = word.Length;
        return after == start.Length || !char.IsLetterOrDigit(start[after]);
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
