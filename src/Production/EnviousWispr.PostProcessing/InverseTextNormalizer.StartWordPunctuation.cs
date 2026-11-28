using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.PostProcessing;

/// <summary>#2450 / #3737: Result of start-word spoken punctuation.</summary>
public readonly record struct SpokenPunctuationResult(
    string Text,
    int RulesFired,
    bool SpokenFinalFullStop = false);

public static partial class InverseTextNormalizer
{
    private const string StartWordHorizontalSpace = @"[ \t\u00A0]";

    public static SpokenPunctuationResult ApplyStartWordPunctuation(
        string text,
        string? language,
        string startWord,
        bool englishPeriodWord = true,
        IReadOnlyList<string>? protectedSentinels = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var unchanged = new SpokenPunctuationResult(text, 0);
        var rules = SpokenPunctuationRules.ActiveStartWordRules(language, englishPeriodWord);
        if (rules is null || rules.Count == 0 || string.IsNullOrEmpty(text))
        {
            return unchanged;
        }

        var sentinels = protectedSentinels ?? [];
        var start = (startWord ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);

        var (afterSymbols, symbolFired) = ApplySymbolCommands(
            text,
            rules,
            start,
            guardDeterminers: false,
            joinerGap: false);

        var ruleByKey = new Dictionary<string, SpokenPunctuationRule>(StringComparer.Ordinal);
        var breakForms = new List<string>();
        var markForms = new List<string>();
        foreach (var rule in rules)
        {
            if (rule.Command.GetRole() == SpokenPunctuationRole.Symbol)
            {
                continue;
            }

            var isBreak = rule.Command.GetRole() == SpokenPunctuationRole.LineBreak;
            foreach (var form in rule.SpokenForms)
            {
                var normalized = form.Normalize(NormalizationForm.FormC);
                ruleByKey[NormalizeFormKey(normalized)] = rule;
                if (isBreak)
                {
                    breakForms.Add(normalized);
                }
                else
                {
                    markForms.Add(normalized);
                }
            }
        }

        var pattern = BuildStartWordPattern(start, breakForms, markForms);
        var allLegacyForms = BuildFormAlternation(breakForms.Concat(markForms));
        var startPattern = string.IsNullOrEmpty(start) ? string.Empty : BuildLiteralPattern(start);
        var working = MergeRepeatedStartWordCommand(afterSymbols, allLegacyForms, startPattern);

        var matches = Compiled(pattern, ignoreCase: true).Matches(working);
        if (matches.Count == 0)
        {
            return symbolFired == 0 ? unchanged : new SpokenPunctuationResult(working, symbolFired);
        }

        var outBuilder = new StringBuilder(working.Length);
        var cursor = 0;
        var pendingCapital = false;
        var fired = symbolFired;
        var finalFullStop = false;
        var baseLang = SpokenPunctuationRules.BaseCode(language) ?? "en";

        foreach (Match match in matches)
        {
            var gap = working[cursor..match.Index];
            outBuilder.Append(pendingCapital ? CapitalizeGap(gap, sentinels, ref pendingCapital) : gap);
            cursor = match.Index + match.Length;

            if (gap.Any(static c => !char.IsWhiteSpace(c)))
            {
                finalFullStop = false;
            }

            var form = match.Groups[1].Success
                ? match.Groups[1].Value
                : match.Groups[2].Success
                    ? match.Groups[2].Value
                    : string.Empty;
            if (!ruleByKey.TryGetValue(NormalizeFormKey(form), out var rule))
            {
                finalFullStop = false;
                outBuilder.Append(match.Value);
                pendingCapital = false;
                continue;
            }

            if (fired == 0 || !StartWordCloserGapRegex().IsMatch(gap))
            {
                DropStartWordRecognizerMark(outBuilder, rule, start, baseLang, sentinels);
            }

            if (rule.Command.GetRole() == SpokenPunctuationRole.Mark &&
                outBuilder.Length > 0 &&
                outBuilder[^1] == '\u00AB')
            {
                outBuilder.Append('\u00A0');
            }

            outBuilder.Append(rule.Replacement);
            fired++;

            switch (rule.Command)
            {
                case SpokenPunctuationCommand.Period:
                    finalFullStop = true;
                    break;
                case SpokenPunctuationCommand.LineBreak:
                case SpokenPunctuationCommand.ParagraphBreak:
                    break;
                default:
                    finalFullStop = false;
                    break;
            }

            pendingCapital = rule.Command is SpokenPunctuationCommand.Period
                or SpokenPunctuationCommand.QuestionMark
                or SpokenPunctuationCommand.ExclamationMark
                or SpokenPunctuationCommand.LineBreak
                or SpokenPunctuationCommand.ParagraphBreak;
        }

        var tail = working[cursor..];
        outBuilder.Append(pendingCapital ? CapitalizeGap(tail, sentinels, ref pendingCapital) : tail);
        if (tail.Any(static c => !char.IsWhiteSpace(c)))
        {
            finalFullStop = false;
        }

        return new SpokenPunctuationResult(outBuilder.ToString(), fired, finalFullStop);
    }

    public static bool HasStartWordCommand(
        string text,
        string? language,
        string startWord,
        bool englishPeriodWord = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var start = (startWord ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);
        if (string.IsNullOrEmpty(start))
        {
            return false;
        }

        var rules = SpokenPunctuationRules.ActiveStartWordRules(language, englishPeriodWord);
        if (rules is null || rules.Count == 0)
        {
            return false;
        }

        var breakForms = new List<string>();
        var markForms = new List<string>();
        foreach (var rule in rules)
        {
            var isBreak = rule.Command.GetRole() == SpokenPunctuationRole.LineBreak;
            foreach (var form in rule.SpokenForms)
            {
                var normalized = form.Normalize(NormalizationForm.FormC);
                if (isBreak)
                {
                    breakForms.Add(normalized);
                }
                else
                {
                    markForms.Add(normalized);
                }
            }
        }

        var pattern = BuildStartWordPattern(start, breakForms, markForms);
        return Compiled(pattern, ignoreCase: true).IsMatch(text);
    }

    [GeneratedRegex(@"^[\s\u201D\u2019""'\)\]\}\u00BB]*$", RegexOptions.CultureInvariant, RecognizerRegexTimeoutMilliseconds)]
    private static partial Regex StartWordCloserGapRegex();

    private static string MergeRepeatedStartWordCommand(string text, string formsAlt, string startWordPattern)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var lead = string.IsNullOrEmpty(startWordPattern)
            ? string.Empty
            : $@"(?:{startWordPattern}[.,]?{SeparatorSpace}+)";
        var anchor = string.IsNullOrEmpty(startWordPattern) ? @"(?<!\S)" : @"\b";
        var primaryPattern = $@"{anchor}{lead}({formsAlt})\b";
        var sep = $@"{SeparatorSpace}*(?:[.,;:!?]|[.]{2}|,{2}|;{2}|:{2}|!{2}|[?]{2})(?![.,;:!?…]){SeparatorSpace}*";
        var repeatedPattern = $@"\G{sep}{lead}({formsAlt})\b";

        var primaryRegex = Compiled(primaryPattern, ignoreCase: true);
        var repeatedRegex = Compiled(repeatedPattern, ignoreCase: true);
        var matches = primaryRegex.Matches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        var outBuilder = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (Match match in matches)
        {
            if (match.Index < cursor)
            {
                continue;
            }

            var end = match.Index + match.Length;
            var key = CollapseWhitespaceFoldApostrophe(match.Groups[1].Value);
            while (end < text.Length)
            {
                var next = repeatedRegex.Match(text, end);
                if (!next.Success || !string.Equals(CollapseWhitespaceFoldApostrophe(next.Groups[1].Value), key, StringComparison.Ordinal))
                {
                    break;
                }

                end += next.Length;
            }

            if (end > match.Index + match.Length)
            {
                outBuilder.Append(text, cursor, (match.Index + match.Length) - cursor);
                cursor = end;
            }
        }

        if (cursor == 0)
        {
            return text;
        }

        outBuilder.Append(text, cursor, text.Length - cursor);
        return outBuilder.ToString();
    }

    private static void DropStartWordRecognizerMark(
        StringBuilder outBuilder,
        SpokenPunctuationRule rule,
        string startWord,
        string language,
        IReadOnlyList<string> sentinels)
    {
        if (rule.Command.GetRole() == SpokenPunctuationRole.LineBreak)
        {
            return;
        }

        var text = outBuilder.ToString();
        var end = text.Length;
        var closers = 0;
        while (end > 0 && closers < 3 && IsStartWordCloser(text[end - 1]))
        {
            end--;
            closers++;
        }

        var start = end;
        while (start > 0 && ".,;:!?".Contains(text[start - 1]))
        {
            start--;
        }

        var length = end - start;
        if (length is not (1 or 2))
        {
            return;
        }

        var mark = text[start];
        if (length == 2 && text[start + 1] != mark)
        {
            return;
        }

        var before = start;
        if ("?!:;".Contains(mark) && before > 0 && text[before - 1] is ' ' or '\u00A0' or '\u202F')
        {
            before--;
        }

        if (before <= 0 || !(char.IsLetter(text[before - 1]) || char.IsDigit(text[before - 1])))
        {
            return;
        }

        if (!string.IsNullOrEmpty(startWord) &&
            Compiled($@"\b{BuildLiteralPattern(startWord)}$", ignoreCase: true).IsMatch(text[..before]))
        {
            return;
        }

        if (mark == '.' && rule.Command != SpokenPunctuationCommand.Period)
        {
            var token = PrecedingToken(text[..before]);
            var isSentinel = sentinels.Any(s => !string.IsNullOrEmpty(s) && token.EndsWith(s, StringComparison.Ordinal));
            if (!isSentinel && IsProtectedDotToken(token, digitIsOrdinal: language != "en", language))
            {
                return;
            }
        }

        outBuilder.Remove(before, end - before);
    }

    private static bool IsStartWordCloser(char c) =>
        c is '\u201D' or '\u2019' or '"' or '\'' or ')' or ']' or '}' or '\u00BB';

    private static string BuildStartWordPattern(
        string start,
        IReadOnlyList<string> breakForms,
        IReadOnlyList<string> markForms)
    {
        const string trailingMark = @"(?:(?:[.,!?]|[ \t\u00A0\u202F][!?])(?=\s|$))?";
        var breaks = BuildFormAlternation(breakForms);
        var marks = BuildFormAlternation(markForms);
        var lead = string.IsNullOrEmpty(start)
            ? string.Empty
            : $@"{BuildLiteralPattern(start)}[.,]?{StartWordHorizontalSpace}+";
        return $@"{StartWordHorizontalSpace}*\b{lead}(?:({breaks})\b{trailingMark}{StartWordHorizontalSpace}*|({marks})\b{trailingMark})";
    }

    internal static string BuildFormAlternation(IEnumerable<string> forms)
    {
        var list = forms.Distinct(StringComparer.Ordinal)
            .OrderByDescending(static f => f.Length)
            .ThenBy(static f => f, StringComparer.Ordinal)
            .Select(BuildLiteralPattern)
            .ToArray();
        return list.Length == 0 ? "(?!)" : string.Join('|', list);
    }

    private static string BuildLiteralPattern(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        var previousWasSpace = false;
        foreach (var ch in text)
        {
            if (ch == ' ')
            {
                if (!previousWasSpace)
                {
                    sb.Append(StartWordHorizontalSpace).Append('+');
                }

                previousWasSpace = true;
                continue;
            }

            previousWasSpace = false;
            if (ch == '\'')
            {
                sb.Append(@"['\u2019]");
                continue;
            }

            var s = ch.ToString();
            var composed = s.Normalize(NormalizationForm.FormC);
            var decomposed = s.Normalize(NormalizationForm.FormD);
            var escaped = Regex.Escape(composed);
            if (composed == decomposed)
            {
                sb.Append(escaped);
            }
            else
            {
                sb.Append("(?:").Append(escaped).Append('|').Append(Regex.Escape(decomposed)).Append(')');
            }
        }

        return sb.ToString();
    }

    private static string NormalizeFormKey(string text)
    {
        var sb = new StringBuilder(text.Length);
        var previousWasSpace = false;
        foreach (var ch in text.Normalize(NormalizationForm.FormC).ToLowerInvariant())
        {
            if (ch is ' ' or '\t' or '\u00A0')
            {
                if (!previousWasSpace)
                {
                    sb.Append(' ');
                }

                previousWasSpace = true;
            }
            else
            {
                previousWasSpace = false;
                sb.Append(ch == '\u2019' ? '\'' : ch);
            }
        }

        return sb.ToString();
    }

    private static string CollapseWhitespaceFoldApostrophe(string phrase) =>
        string.Join(' ', phrase.ToLowerInvariant().Replace('\u2019', '\'').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string CapitalizeGap(string gap, IReadOnlyList<string> sentinels, ref bool pendingCapital)
    {
        var index = 0;
        while (index < gap.Length && IsWhitespaceOrOpeningForCap(gap[index]))
        {
            index++;
        }

        if (index >= gap.Length)
        {
            return gap;
        }

        pendingCapital = false;
        var rest = gap.AsSpan(index);
        foreach (var sentinel in sentinels)
        {
            if (!string.IsNullOrEmpty(sentinel) && rest.StartsWith(sentinel.AsSpan(), StringComparison.Ordinal))
            {
                return gap;
            }
        }

        var ch = gap[index];
        if (!char.IsLower(ch))
        {
            return gap;
        }

        return gap[..index] + char.ToUpperInvariant(ch) + gap[(index + 1)..];
    }

    private static bool IsWhitespaceOrOpeningForCap(char c)
    {
        if (char.IsWhiteSpace(c) || c is '"' or '\'' or '\u00BF' or '\u00A1' or '<' or '#' or '~')
        {
            return true;
        }

        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat is UnicodeCategory.OpenPunctuation or UnicodeCategory.InitialQuotePunctuation;
    }
}
