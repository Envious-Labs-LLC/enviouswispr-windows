using System.Text;
using System.Text.RegularExpressions;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.PostProcessing;

public static partial class InverseTextNormalizer
{
    internal const char JoinerGapCharacter = '\u200A';

    private static readonly HashSet<string> SymbolDeterminers = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "this", "that", "each", "every", "my", "your", "our", "their", "his", "her", "its",
    };

    private static readonly HashSet<char> TightAfter =
    [
        '(', '[', '{', '<', '\u201C', '\u2018', '\u201E', '\u00AB',
    ];

    private static readonly HashSet<char> TightBefore =
    [
        '.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '\u201D', '\u2019', '\u00BB', '\u2026',
    ];

    private static readonly HashSet<char> JoinerLeftNeighbours =
    [
        '"', '\'', '(', '[', '{', '\u201C', '\u2018', '\u00AB',
    ];

    [GeneratedRegex(
        @"^(\s)*(?:back[^\S\r\n]*slash|(?:forward[^\S\r\n]+)?slash)(?![^\s.,;:!?])(?![.,;:!?]\S)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RecognizerRegexTimeoutMilliseconds)]
    private static partial Regex JoinerWordAtStartRegex();

    [GeneratedRegex(
        @"(?<![^\s""'(\[{\u201C\u2018\u00AB])(?:back[^\S\r\n]*slash|(?:forward[^\S\r\n]+)?slash)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RecognizerRegexTimeoutMilliseconds)]
    private static partial Regex JoinerWordAtEndRegex();

    [GeneratedRegex(@"(\p{L})-hyphen(?:ed)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RecognizerRegexTimeoutMilliseconds)]
    private static partial Regex GluedHyphenCommandRegex();

    public static string ApplyEnglishSymbolCommands(string text, bool joinerGap = false, bool periodWord = true)
    {
        var unglued = GluedHyphenCommandRegex().Replace(text, "$1 hyphen");
        var (converted, _) = ApplySymbolCommands(
            unglued,
            SpokenPunctuationRules.EnglishRules(periodWord),
            startWord: null,
            guardDeterminers: true,
            joinerGap: joinerGap);
        return converted;
    }

    public static (string Text, int Fired) ApplySymbolCommands(
        string text,
        IReadOnlyList<SpokenPunctuationRule> rules,
        string? startWord,
        bool guardDeterminers,
        bool joinerGap)
    {
        var symbols = rules.Where(static r => r.Command.GetRole() == SpokenPunctuationRole.Symbol && r.Command.GetSpacing() is not null).ToArray();
        if (symbols.Length == 0 || string.IsNullOrEmpty(text))
        {
            return (text, 0);
        }

        var ruleByKey = new Dictionary<string, SpokenPunctuationRule>(StringComparer.Ordinal);
        var forms = new List<string>();
        foreach (var rule in symbols)
        {
            foreach (var form in rule.SpokenForms)
            {
                var normalized = form.Normalize(NormalizationForm.FormC);
                ruleByKey[NormalizeFormKey(normalized)] = rule;
                forms.Add(normalized);
            }
        }

        var start = (startWord ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);
        var lead = string.IsNullOrEmpty(start) ? string.Empty : $@"{BuildLiteralPattern(start)}[.,]?[ \t\u00A0]+";
        var pattern = $@"(?<![\p{{L}}\p{{M}}\p{{N}}_-]){lead}({BuildFormAlternation(forms)})(?![\p{{L}}\p{{M}}\p{{N}}_-])";
        var legacyForms = BuildFormAlternation(
            rules.Where(static r => r.Command.GetRole() != SpokenPunctuationRole.Symbol).SelectMany(static r => r.SpokenForms));
        var legacyRegex = Compiled($@"\A[ \t\u00A0]*{lead}(?:{legacyForms})\b", ignoreCase: true);

        var matches = Compiled(pattern, ignoreCase: true).Matches(text);
        if (matches.Count == 0)
        {
            return (text, 0);
        }

        var outBuilder = new StringBuilder(text.Length);
        var cursor = 0;
        var skipLeadingSpace = false;
        var absorbLeadingComma = false;
        var lastWasSpaced = false;
        var lastOpened = false;
        var fired = 0;

        string TrimSymbolGap(string value, char? afterSymbol)
        {
            if (legacyRegex.IsMatch(value))
            {
                return value;
            }

            var rest = value.TrimStart(' ', '\t', '\u00A0');
            if (joinerGap &&
                afterSymbol is char sym &&
                !char.IsWhiteSpace(sym) &&
                !JoinerLeftNeighbours.Contains(sym) &&
                HasJoinerWordAtStart(rest))
            {
                return JoinerGapCharacter + rest;
            }

            return rest;
        }

        string GapBeforeClosing(string before) =>
            joinerGap && JoinerWordAtEndRegex().IsMatch(before) ? JoinerGapCharacter.ToString() : string.Empty;

        foreach (Match match in matches)
        {
            if (match.Index < cursor)
            {
                continue;
            }

            var gap = text[cursor..match.Index];
            if (absorbLeadingComma)
            {
                gap = DropEngineComma(gap);
            }

            if (skipLeadingSpace)
            {
                gap = TrimSymbolGap(gap, outBuilder.Length > 0 ? outBuilder[^1] : null);
            }

            skipLeadingSpace = false;
            absorbLeadingComma = false;
            var matched = match.Value;
            var form = match.Groups[1].Value;
            if (!ruleByKey.TryGetValue(NormalizeFormKey(form), out var rule) ||
                rule.Command.GetSpacing() is not SpokenSymbolSpacing spacing)
            {
                outBuilder.Append(gap).Append(matched);
                cursor = match.Index + match.Length;
                lastWasSpaced = false;
                lastOpened = false;
                continue;
            }

            if (guardDeterminers && SymbolDeterminers.Contains(LastWordOf(outBuilder.ToString() + gap)))
            {
                outBuilder.Append(gap).Append(matched);
                cursor = match.Index + match.Length;
                lastWasSpaced = false;
                lastOpened = false;
                continue;
            }

            outBuilder.Append(gap);
            var followsOpener = lastOpened && gap.All(IsHorizontalSpaceChar);
            lastOpened = spacing == SpokenSymbolSpacing.Opens;
            lastWasSpaced = spacing == SpokenSymbolSpacing.Spaced;

            switch (spacing)
            {
                case SpokenSymbolSpacing.Opens:
                    outBuilder.Append(rule.Replacement);
                    skipLeadingSpace = true;
                    absorbLeadingComma = true;
                    break;

                case SpokenSymbolSpacing.Closes:
                {
                    var before = TrimEngineCommaEnd(outBuilder.ToString());
                    outBuilder.Clear();
                    outBuilder.Append(before).Append(GapBeforeClosing(before)).Append(rule.Replacement);
                    break;
                }

                case SpokenSymbolSpacing.Joins:
                {
                    var before = TrimEngineCommaEnd(outBuilder.ToString());
                    outBuilder.Clear();
                    outBuilder.Append(before).Append(GapBeforeClosing(before)).Append(rule.Replacement);
                    skipLeadingSpace = true;
                    absorbLeadingComma = true;
                    break;
                }

                case SpokenSymbolSpacing.Spaced:
                {
                    var left = TrimTrailingHorizontalSpace(outBuilder.ToString());
                    var right = text[(match.Index + match.Length)..].TrimStart(' ', '\t', '\u00A0');
                    var leftSpace = left.Length == 0 ||
                        left[^1] is '\n' or '\r' ||
                        followsOpener ||
                        TightAfter.Contains(left[^1])
                        ? string.Empty
                        : " ";
                    var rightSpace = right.Length == 0 ||
                        right[0] is '\n' or '\r' ||
                        TightBefore.Contains(right[0])
                        ? string.Empty
                        : " ";
                    outBuilder.Clear();
                    outBuilder.Append(left).Append(leftSpace).Append(rule.Replacement).Append(rightSpace);
                    skipLeadingSpace = true;
                    break;
                }
            }

            fired++;
            cursor = match.Index + match.Length;
        }

        var tail = text[cursor..];
        if (absorbLeadingComma)
        {
            tail = DropEngineComma(tail);
        }

        if (skipLeadingSpace)
        {
            tail = TrimSymbolGap(tail, outBuilder.Length > 0 ? outBuilder[^1] : null);
        }

        if (tail.Length == 0 && lastWasSpaced)
        {
            var trimmedOut = TrimTrailingHorizontalSpace(outBuilder.ToString());
            outBuilder.Clear();
            outBuilder.Append(trimmedOut);
        }

        outBuilder.Append(tail);
        return (outBuilder.ToString(), fired);
    }

    private static bool HasJoinerWordAtStart(string value)
    {
        var trimmed = value.TrimStart(' ', '\t', '\u00A0');
        return JoinerWordAtStartRegex().IsMatch(trimmed);
    }

    private static string DropEngineComma(string value)
    {
        var rest = value.AsSpan().TrimStart(" \t\u00A0");
        if (rest.Length == 0 || rest[0] != ',')
        {
            return value;
        }

        var afterComma = rest[1..].TrimStart(" \t\u00A0");
        if (afterComma.Length > 0 && afterComma[0] == ',')
        {
            return value;
        }

        return rest[1..].ToString();
    }

    private static string TrimEngineCommaEnd(string value)
    {
        var trimmed = TrimTrailingHorizontalSpace(value);
        if (trimmed.Length == 0 || trimmed[^1] != ',')
        {
            return trimmed;
        }

        var beforeComma = TrimTrailingHorizontalSpace(trimmed[..^1]);
        return beforeComma.Length > 0 && beforeComma[^1] == ',' ? trimmed : beforeComma;
    }

    private static bool IsHorizontalSpaceChar(char c) => c is ' ' or '\t' or '\u00A0';

    private static string TrimTrailingHorizontalSpace(string value)
    {
        var end = value.Length;
        while (end > 0 && IsHorizontalSpaceChar(value[end - 1]))
        {
            end--;
        }

        var trimmed = value[..end];
        return trimmed.EndsWith('\u00AB') && value.AsSpan(end).Contains('\u00A0')
            ? trimmed + "\u00A0"
            : trimmed;
    }

    private static string LastWordOf(string value)
    {
        var end = value.Length;
        while (end > 0 && char.IsWhiteSpace(value[end - 1]))
        {
            end--;
        }

        var start = end;
        while (start > 0 && char.IsLetter(value[start - 1]))
        {
            start--;
        }

        return value[start..end];
    }

    private enum SlashReading
    {
        Word,
        Pair,
        Prefix,
        Glue,
        Unresolved,
    }

    private struct SlashContext
    {
        public string Left;
        public string LeftRaw;
        public string Right;
        public bool MarkerCarriesPunctuation;
        public bool LeftSpellsScheme;
        public bool BeforeLeftSpellsScheme;
        public string BeforeLeft;
        public string AfterRight;
        public bool CapitalisedMidSentence;
        public SlashReading? Previous;
    }

    private const string JoinerCommandsPattern =
        @"[^\S\r\n]*(?:(?<=\S),)?[^\S\r\n]*(?<![^\s""'(\[{\u201C\u2018\u00AB])(back[^\S\r\n]*slash|(?:forward[^\S\r\n]+)?slash)(?![^\s.,;:!?])(?![.,;:!?]\S)[^\S\r\n]*";

    private const string SlashSentenceBreakPattern =
        @"(?:(?<![^\p{L}\p{M}][\p{L}\p{M}])(?<!^[\p{L}\p{M}])(?<!\b(?i:mr|mrs|ms|dr|prof|sr|jr|st|mt|vs|etc))\.|[!?…])[""'”’»)\]}]*\s|\r\n|\r|\n";

    [GeneratedRegex(@"^\s*(/[\p{L}\p{N}_][\p{L}\p{N}_-]*)\s*\.?\s*$", RegexOptions.CultureInvariant, RecognizerRegexTimeoutMilliseconds)]
    private static partial Regex LoneCommandPeriodRegex();

    private static readonly HashSet<string> SlashSubjectPronouns = new(["i", "you", "he", "she", "it", "we", "they"], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashObjectPronouns = new(["me", "you", "him", "her", "it", "us", "them"], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashPossessives = new(["my", "your", "his", "her", "its", "our", "their", "mine", "yours", "hers", "ours", "theirs"], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashFunctionPairs = new(
    [
        "and/or", "or/and", "on/off", "off/on", "in/out", "out/in", "up/down", "down/up",
        "yes/no", "no/yes", "before/after", "after/before", "auto/off", "top/down", "bottom/up",
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashDeterminers = new(
    [
        "a", "an", "the", "this", "that", "these", "those", "another", "one", "any", "each",
        "every", "some", "all", "both", "either", "neither", "my", "your", "his", "her", "its", "our", "their",
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashModalsAndNegators = new(
    [
        "will", "would", "can", "could", "should", "must", "may", "might", "shall", "gonna",
        "wanna", "let's", "not", "never", "don't", "doesn't", "didn't", "won't", "wouldn't",
        "can't", "couldn't", "shouldn't", "cannot",
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashAuxiliaries = new(
    [
        "is", "are", "am", "was", "were", "be", "been", "being", "do", "does", "did", "have", "has", "had",
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashConjunctions = new(["and", "or", "but", "so", "if"], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashPrepositions = new(
    [
        "with", "in", "on", "at", "for", "of", "by", "from", "into", "about", "via", "like", "as",
        "than", "between", "after", "before", "until", "under", "over", "through", "to",
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashDiscourse = new(
    [
        "okay", "ok", "alright", "yes", "yeah", "yep", "sure", "hey", "oh", "anyway", "cool", "great", "thanks",
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashParticles = new(["off", "down"], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashParticleVerbs = new(["kick", "kicked", "kicking", "fire", "fired", "firing"], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashDestinationWords = new(["switch", "switched", "switching", "back", "over", "straight", "directly"], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashCommandNouns = new(["command", "commands", "skill", "skills", "shortcut", "shortcuts"], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashIndefinites = new(["everything", "something", "anything", "nothing", "everyone", "someone", "anyone", "everybody", "somebody", "anybody"], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashAdverbs = new(["then", "just", "now", "also", "always", "simply", "first", "next", "please", "maybe", "probably", "perhaps", "usually", "often", "sometimes"], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashSubordinators = new(["after", "before", "until", "when", "once", "while", "whenever"], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashCommandVerbs = new(
    [
        "use", "using", "used", "type", "typed", "typing", "run", "ran", "running", "try", "tried",
        "say", "said", "says", "hit", "send", "sent", "press", "pressed", "enter", "entered", "call",
        "called", "invoke", "invoked", "add", "added", "remember", "forget", "forgot", "forgetting",
        "document", "documented", "mention", "mentioned", "prefix", "replace", "replaced", "show",
        "showed", "shows", "see", "saw", "recommend", "suggest", "prefer", "need", "needs", "want",
        "wants", "know", "learn", "learned", "teach", "taught", "explain", "explained", "define",
        "defined", "insert", "put", "choose", "chose", "pick", "picked", "select", "selected",
        "execute", "executed", "write", "wrote", "means", "meaning", "launch", "launched", "trigger",
        "triggered", "fire", "fired", "kick", "kicked", "issue", "issued", "apply", "applied",
        "perform", "performed",
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> SlashSchemes = new(["http", "https", "ftp"], StringComparer.Ordinal);

    private static string ApplyJoinerCommands(string input, bool spokenPunctuation, bool joinerGap)
    {
        SlashReading? previous = null;
        var previousMarkerEnd = 0;
        var wroteCommand = false;
        var sentenceBreakRegex = Compiled(SlashSentenceBreakPattern, ignoreCase: false);

        var t = Replace(
            input,
            JoinerCommandsPattern,
            match =>
            {
                var command = match.Groups[1].Value;
                if (string.IsNullOrEmpty(command))
                {
                    return match.Value;
                }

                var markerSpan = match.Groups[1];
                var gap = input[previousMarkerEnd..markerSpan.Index];
                if (sentenceBreakRegex.IsMatch(gap))
                {
                    previous = null;
                }

                previousMarkerEnd = markerSpan.Index + markerSpan.Length;
                if (command.StartsWith("back", StringComparison.OrdinalIgnoreCase))
                {
                    // Preserve "backslash" / "back slash" on Windows because "backslash" is the default
                    // snippet trigger keyword (SnippetChainPlacementTests pins unmatched "backslash" phrases).
                    previous = SlashReading.Word;
                    return match.Value;
                }

                var left = SlashNeighbourBefore(input, markerSpan.Index);
                var right = SlashNeighbourAfter(input, markerSpan.Index + markerSpan.Length);
                if (right.Core == "forward")
                {
                    var nextAfterForward = SlashNeighbourAfter(input, right.Edge);
                    if (nextAfterForward.Core == "slash")
                    {
                        right = nextAfterForward;
                    }
                }

                var ctx = new SlashContext
                {
                    Left = left.Core,
                    LeftRaw = left.Raw,
                    Right = right.Core,
                    MarkerCarriesPunctuation = string.IsNullOrEmpty(right.Core) && !string.IsNullOrEmpty(right.Raw),
                    BeforeLeft = string.Empty,
                    AfterRight = string.Empty,
                };

                if (!string.IsNullOrEmpty(left.Raw))
                {
                    var beforeLeft = SlashNeighbourBefore(input, left.Edge);
                    if (left.Core == "slash" && beforeLeft.Core == "forward")
                    {
                        beforeLeft = SlashNeighbourBefore(input, beforeLeft.Edge);
                    }

                    ctx.BeforeLeft = beforeLeft.Core;
                    ctx.LeftSpellsScheme = SlashSpelledScheme(input, left.Edge + left.Raw.Length);
                    if (!string.IsNullOrEmpty(beforeLeft.Raw))
                    {
                        ctx.BeforeLeftSpellsScheme = SlashSpelledScheme(input, beforeLeft.Edge + beforeLeft.Raw.Length);
                    }
                }

                if (!string.IsNullOrEmpty(right.Raw))
                {
                    var next = SlashNeighbourAfter(input, right.Edge);
                    ctx.AfterRight = next.Core;
                    if (next.Core == "forward" && SlashNeighbourAfter(input, next.Edge).Core == "slash")
                    {
                        ctx.AfterRight = "slash";
                    }
                }

                ctx.CapitalisedMidSentence = IsTitleCaseWords(command) && !SlashStartsSentence(input, markerSpan.Index, sentenceBreakRegex);
                ctx.Previous = previous;
                var reading = ClassifySlashReading(ctx, sentenceBreakRegex);
                previous = reading;
                switch (reading)
                {
                    case SlashReading.Word:
                    case SlashReading.Unresolved:
                        return match.Value;
                    case SlashReading.Pair:
                    case SlashReading.Glue:
                        if (right.Core == "slash" && SlashSchemes.Contains(left.Core) && !left.Raw.EndsWith(':'))
                        {
                            return ":/";
                        }

                        return "/";
                    case SlashReading.Prefix:
                        wroteCommand = true;
                        if (string.IsNullOrEmpty(left.Raw))
                        {
                            return "/";
                        }

                        var leading = input[match.Index..markerSpan.Index];
                        return leading + "/";
                    default:
                        return match.Value;
                }
            });

        if (joinerGap)
        {
            t = t.Replace(JoinerGapCharacter.ToString(), string.Empty, StringComparison.Ordinal);
        }

        if (wroteCommand)
        {
            var lone = LoneCommandPeriodRegex().Match(t);
            if (lone.Success)
            {
                t = lone.Groups[1].Value.ToLowerInvariant();
            }
        }

        return t;
    }

    private static SlashReading ClassifySlashReading(SlashContext c, Regex sentenceBreakRegex)
    {
        var leftEndsSentence = !string.IsNullOrEmpty(c.Left) && sentenceBreakRegex.IsMatch(c.LeftRaw + " ");
        var l = leftEndsSentence ? string.Empty : c.Left;
        var r = c.Right;
        var leftIsMarker = l == "backslash" || (l == "slash" && c.BeforeLeft == "back");
        var leftEndsClause = c.LeftRaw.Length > 0 && ",;:.!?".Contains(c.LeftRaw[^1]);
        var previousAdjacentGlued =
            !leftEndsSentence && (c.BeforeLeft == "slash" || leftIsMarker) &&
            (c.Previous == SlashReading.Glue || c.Previous == SlashReading.Pair || (c.Previous == SlashReading.Prefix && !leftEndsClause));

        if (string.IsNullOrEmpty(r))
        {
            return leftIsMarker && c.Previous == SlashReading.Glue ? SlashReading.Glue : SlashReading.Unresolved;
        }

        if (c.CapitalisedMidSentence)
        {
            return SlashReading.Word;
        }

        var leftIsScheme = SlashSchemes.Contains(l) || c.LeftSpellsScheme;
        if (r == "slash")
        {
            if (leftIsScheme)
            {
                return c.AfterRight.Contains('.', StringComparison.Ordinal) || c.LeftSpellsScheme
                    ? SlashReading.Glue
                    : SlashReading.Unresolved;
            }

            return string.IsNullOrEmpty(l) || IsSlashPrefixLeft(l) ? SlashReading.Prefix : SlashReading.Unresolved;
        }

        if (l == "slash")
        {
            if ((SlashSchemes.Contains(c.BeforeLeft) || c.BeforeLeftSpellsScheme) && c.Previous == SlashReading.Glue)
            {
                return SlashReading.Glue;
            }

            return c.Previous == SlashReading.Prefix ? SlashReading.Word : SlashReading.Unresolved;
        }

        var chain = c.AfterRight == "slash";
        if (IsSlashPair(l, r) && (!leftEndsClause || chain))
        {
            return SlashReading.Pair;
        }

        if (!string.IsNullOrEmpty(c.LeftRaw) && string.IsNullOrEmpty(l) && !leftEndsSentence)
        {
            return SlashReading.Glue;
        }

        if (c.LeftRaw == "A")
        {
            return SlashReading.Glue;
        }

        if (SlashModalsAndNegators.Contains(l) || SlashSubjectPronouns.Contains(l) || l == "back")
        {
            return SlashReading.Word;
        }

        if (SlashDeterminers.Contains(r) || SlashObjectPronouns.Contains(r) ||
            SlashSubjectPronouns.Contains(r) || SlashAuxiliaries.Contains(r) ||
            SlashModalsAndNegators.Contains(r) || SlashConjunctions.Contains(r) ||
            SlashIndefinites.Contains(r))
        {
            return SlashReading.Word;
        }

        var proseAfter = SlashDeterminers.Contains(c.AfterRight) ||
            SlashSubjectPronouns.Contains(c.AfterRight) ||
            SlashObjectPronouns.Contains(c.AfterRight);
        if (SlashPrepositions.Contains(r) && (!previousAdjacentGlued || proseAfter))
        {
            return SlashReading.Word;
        }

        if (SlashParticles.Contains(r))
        {
            return SlashReading.Word;
        }

        if (SlashCommandNouns.Contains(c.AfterRight))
        {
            return SlashReading.Prefix;
        }

        if (SlashDeterminers.Contains(l))
        {
            return SlashReading.Word;
        }

        if (previousAdjacentGlued)
        {
            return SlashReading.Glue;
        }

        if (l == "to")
        {
            return chain || SlashDestinationWords.Contains(c.BeforeLeft) ? SlashReading.Prefix : SlashReading.Unresolved;
        }

        if (SlashConjunctions.Contains(l))
        {
            return c.Previous == SlashReading.Prefix || c.BeforeLeft == "ahead" ? SlashReading.Prefix : SlashReading.Unresolved;
        }

        if (c.Previous == SlashReading.Prefix && leftEndsClause)
        {
            return SlashReading.Prefix;
        }

        if (leftEndsClause && !chain)
        {
            return SlashReading.Prefix;
        }

        if (l == "off" && SlashParticleVerbs.Contains(c.BeforeLeft))
        {
            return SlashReading.Prefix;
        }

        if (SlashDiscourse.Contains(l))
        {
            return string.IsNullOrEmpty(c.BeforeLeft) ? SlashReading.Prefix : SlashReading.Glue;
        }

        if (SlashCommandVerbs.Contains(l) && SlashDeterminers.Contains(c.BeforeLeft))
        {
            return SlashReading.Glue;
        }

        if (string.IsNullOrEmpty(l) || IsSlashPrefixLeft(l))
        {
            return SlashReading.Prefix;
        }

        return SlashReading.Glue;
    }

    private static bool IsSlashPair(string l, string r)
    {
        if (SlashFunctionPairs.Contains($"{l}/{r}"))
        {
            return true;
        }

        var subjOrObj = SlashSubjectPronouns.Contains(l) || SlashObjectPronouns.Contains(l);
        if (subjOrObj && (SlashSubjectPronouns.Contains(r) || SlashObjectPronouns.Contains(r)))
        {
            return true;
        }

        return SlashPossessives.Contains(l) && SlashPossessives.Contains(r);
    }

    private static bool IsSlashPrefixLeft(string l) =>
        SlashAuxiliaries.Contains(l) ||
        (SlashPrepositions.Contains(l) && l != "to") ||
        SlashAdverbs.Contains(l) ||
        SlashSubordinators.Contains(l) ||
        SlashCommandVerbs.Contains(l);

    private static (string Raw, string Core, int Edge) SlashNeighbourBefore(string text, int start)
    {
        var end = start;
        while (end > 0 && IsHorizontalSpaceChar(text[end - 1]))
        {
            end--;
        }

        var begin = end;
        while (begin > 0 && !char.IsWhiteSpace(text[begin - 1]))
        {
            begin--;
        }

        var raw = text[begin..end];
        return (raw, SlashCore(raw, keepAfterLastSlash: true), begin);
    }

    private static (string Raw, string Core, int Edge) SlashNeighbourAfter(string text, int end)
    {
        var n = text.Length;
        var begin = end;
        if (begin < n && !char.IsWhiteSpace(text[begin]))
        {
            var k = begin;
            while (k < n && !char.IsWhiteSpace(text[k]))
            {
                k++;
            }

            var glued = text[begin..k];
            return (glued, SlashCore(glued, keepAfterLastSlash: false), k);
        }

        while (begin < n && IsHorizontalSpaceChar(text[begin]))
        {
            begin++;
        }

        var finish = begin;
        while (finish < n && !char.IsWhiteSpace(text[finish]))
        {
            finish++;
        }

        var raw = text[begin..finish];
        return (raw, SlashCore(raw, keepAfterLastSlash: false), finish);
    }

    private static string SlashCore(string raw, bool keepAfterLastSlash)
    {
        var core = TrimTokenEdges(raw);
        if (keepAfterLastSlash)
        {
            var cut = core.LastIndexOfAny(['/', '\\']);
            if (cut >= 0)
            {
                core = core[(cut + 1)..];
            }
        }
        else
        {
            var cut = core.IndexOfAny(['/', '\\']);
            if (cut >= 0)
            {
                core = core[..cut];
            }
        }

        return core.Replace('\u2019', '\'').ToLowerInvariant();
    }

    private static string TrimTokenEdges(string raw)
    {
        var start = 0;
        while (start < raw.Length && IsStripEdgeChar(raw[start]))
        {
            start++;
        }

        var end = raw.Length;
        while (end > start && IsStripEdgeChar(raw[end - 1]))
        {
            end--;
        }

        return raw[start..end];
    }

    private static readonly HashSet<char> TokenStripChars =
    [
        '"', '\'', '\u201C', '\u201D', '\u2018', '\u2019', '(', '[', '{', '<', '\u00AB',
        ')', ']', '}', '>', '\u00BB', '.', ',', ';', ':', '!', '?', '-', '\u2014', '\u2013', '*', '\u2022',
    ];

    private static bool IsStripEdgeChar(char c) =>
        TokenStripChars.Contains(c) || char.IsWhiteSpace(c);

    private static bool SlashSpelledScheme(string text, int endingAt)
    {
        var letters = new List<char>(5);
        var cursor = endingAt;
        while (letters.Count < 5)
        {
            var token = SlashNeighbourBefore(text, cursor);
            if (string.IsNullOrEmpty(token.Raw))
            {
                break;
            }

            if (token.Core.Length != 1)
            {
                break;
            }

            var ch = token.Core[0];
            if (!char.IsAsciiLetter(ch))
            {
                break;
            }

            letters.Insert(0, ch);
            cursor = token.Edge;
            if (SlashSchemes.Contains(new string(letters.ToArray())))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTitleCaseWords(string words)
    {
        var parts = words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(static w =>
            char.IsUpper(w[0]) && w.AsSpan(1).ToString().All(static c => !char.IsUpper(c)));
    }

    private static bool SlashStartsSentence(string text, int start, Regex sentenceBreakRegex)
    {
        var i = start;
        while (i > 0 && char.IsWhiteSpace(text[i - 1]))
        {
            if (text[i - 1] is '\n' or '\r')
            {
                return true;
            }

            i--;
        }

        if (i == 0)
        {
            return true;
        }

        var before = SlashNeighbourBefore(text, start);
        if (string.IsNullOrEmpty(before.Core))
        {
            return before.Raw.EndsWith('.') || before.Raw.EndsWith('!') || before.Raw.EndsWith('?') || before.Raw.EndsWith('\u2026');
        }

        return sentenceBreakRegex.IsMatch(before.Raw + " ");
    }
}
