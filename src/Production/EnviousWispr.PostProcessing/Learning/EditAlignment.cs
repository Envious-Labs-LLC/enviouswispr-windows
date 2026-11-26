using System.Globalization;
using System.Text;
using EnviousWispr.Core.Learning;

namespace EnviousWispr.PostProcessing.Learning;

/// <summary>
/// Turns pasted text and edited text into bounded changed word runs via deterministic word-level Levenshtein alignment.
/// Mirrors macOS <c>EditAlignment</c>.
/// </summary>
public static class EditAlignment
{
    public const int MaxWordsPerSide = 4;
    public const int MaxAlignmentCells = 250_000;

    public enum Label
    {
        Match,
        Substitute,
        Casing,
        Insert,
        Delete,
    }

    public sealed record AlignmentOp(Label Kind, string? Original, string? Edited);

    public sealed class Run
    {
        public Run(
            string original,
            string replacement,
            int originalStart,
            int originalEnd,
            int editedStart,
            int editedEnd,
            IReadOnlyList<Label> labels)
        {
            Original = original;
            Replacement = replacement;
            CoreOriginal = LexicalCore(original);
            CoreReplacement = LexicalCore(replacement);
            OriginalStart = originalStart;
            OriginalEnd = originalEnd;
            EditedStart = editedStart;
            EditedEnd = editedEnd;
            Labels = labels;
        }

        public string Original { get; }
        public string Replacement { get; }
        public string CoreOriginal { get; }
        public string CoreReplacement { get; }
        public int OriginalStart { get; }
        public int OriginalEnd { get; }
        public int EditedStart { get; }
        public int EditedEnd { get; }
        public IReadOnlyList<Label> Labels { get; }
    }

    public enum DropReason
    {
        InsertionOrDeletionOnly,
        TooLong,
        CasingOrPunctuationOnly,
        CjkSingleToken,
        DecorationOnly,
    }

    public sealed record Dropped(Run Run, DropReason Reason);

    public sealed record Result(
        IReadOnlyList<Run> Runs,
        IReadOnlyList<Dropped> DroppedRuns,
        IReadOnlyList<AlignmentOp> Steps,
        bool LimitExceeded);

    public static string[] SplitWords(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    public static Result Align(string pasted, string edited)
    {
        ArgumentNullException.ThrowIfNull(pasted);
        ArgumentNullException.ThrowIfNull(edited);

        var a = SplitWords(pasted);
        var b = SplitWords(edited);
        if ((long)(a.Length + 1) * (b.Length + 1) > MaxAlignmentCells)
        {
            return new Result([], [], [], LimitExceeded: true);
        }

        var steps = AlignTokens(a, b);
        var runs = new List<Run>();
        var dropped = new List<Dropped>();
        var i = 0;
        var ai = 0;
        var bi = 0;

        while (i < steps.Count)
        {
            if (steps[i].Kind == Label.Match)
            {
                ai++;
                bi++;
                i++;
                continue;
            }

            var aStart = ai;
            var bStart = bi;
            var labels = new List<Label>();
            var originals = new List<string>();
            var editeds = new List<string>();

            while (i < steps.Count && steps[i].Kind != Label.Match)
            {
                var step = steps[i];
                labels.Add(step.Kind);
                if (step.Original is not null)
                {
                    originals.Add(step.Original);
                    ai++;
                }

                if (step.Edited is not null)
                {
                    editeds.Add(step.Edited);
                    bi++;
                }

                i++;
            }

            var run = new Run(
                string.Join(' ', originals),
                string.Join(' ', editeds),
                aStart,
                ai,
                bStart,
                bi,
                labels);
            var reason = GetDropReason(originals, editeds);
            if (reason is { } dropReason)
            {
                dropped.Add(new Dropped(run, dropReason));
            }
            else
            {
                runs.Add(run);
            }
        }

        return new Result(runs, dropped, steps, LimitExceeded: false);
    }

    public static DropReason? GetDropReason(IReadOnlyList<string> originals, IReadOnlyList<string> editeds)
    {
        if (originals.Count == 0 || editeds.Count == 0)
        {
            return DropReason.InsertionOrDeletionOnly;
        }

        if (originals.Count > MaxWordsPerSide || editeds.Count > MaxWordsPerSide)
        {
            return DropReason.TooLong;
        }

        var o = string.Join(' ', originals);
        var r = string.Join(' ', editeds);
        if (IsCasingOrPunctuationOnly(o, r))
        {
            return DropReason.CasingOrPunctuationOnly;
        }

        if (originals.Count == 1 && editeds.Count == 1 && ContainsCjk(o) && ContainsCjk(r))
        {
            return DropReason.CjkSingleToken;
        }

        var co = CorrectionJudgeRequest.RunText(LexicalCore(o));
        var cr = CorrectionJudgeRequest.RunText(LexicalCore(r));
        if (co.Length == 0 || cr.Length == 0 || string.Equals(co, cr, StringComparison.Ordinal))
        {
            return DropReason.DecorationOnly;
        }

        return null;
    }

    public static string LexicalCore(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cores = new List<string>();
        foreach (var token in SplitWords(text))
        {
            var stripped = StripEdgePunctuation(token);
            if (stripped.Length > 0)
            {
                cores.Add(stripped);
            }
        }

        return string.Join(' ', cores);
    }

    public static string StripEdgePunctuation(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return string.Empty;
        }

        var start = 0;
        var end = token.Length;
        while (start < end && IsEdgePunctuationOrSymbol(token, start, out var charLen))
        {
            start += charLen;
        }

        while (end > start)
        {
            var runeStart = end - 1;
            if (runeStart > start && char.IsLowSurrogate(token[runeStart]) && char.IsHighSurrogate(token[runeStart - 1]))
            {
                runeStart--;
            }

            if (!IsEdgePunctuationOrSymbol(token, runeStart, out _))
            {
                break;
            }

            end = runeStart;
        }

        return token.Substring(start, end - start);
    }

    public static bool IsCasingOrPunctuationOnly(string original, string replacement)
    {
        var left = LettersAndDigitsFolded(original);
        var right = LettersAndDigitsFolded(replacement);
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    public static string LettersAndDigitsFolded(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                sb.Append(Rune.ToLowerInvariant(rune).ToString());
            }
        }

        return sb.ToString();
    }

    private static bool IsEdgePunctuationOrSymbol(string text, int index, out int length)
    {
        var rune = Rune.GetRuneAt(text, index);
        length = rune.Utf16SequenceLength;
        return Rune.IsPunctuation(rune) || Rune.IsSymbol(rune);
    }

    private static bool ContainsCjk(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if ((v >= 0x3040 && v <= 0x30FF) ||
                (v >= 0x3400 && v <= 0x4DBF) ||
                (v >= 0x4E00 && v <= 0x9FFF) ||
                (v >= 0xAC00 && v <= 0xD7AF) ||
                (v >= 0xF900 && v <= 0xFAFF) ||
                (v >= 0x20000 && v <= 0x2FA1F))
            {
                return true;
            }
        }

        return false;
    }

    private static List<AlignmentOp> AlignTokens(string[] a, string[] b)
    {
        var m = a.Length;
        var n = b.Length;
        var na = a.Select(x => x.Normalize(NormalizationForm.FormC)).ToArray();
        var nb = b.Select(x => x.Normalize(NormalizationForm.FormC)).ToArray();
        var la = na.Select(x => x.ToLowerInvariant()).ToArray();
        var lb = nb.Select(x => x.ToLowerInvariant()).ToArray();

        bool Exact(int i, int j) => string.Equals(na[i], nb[j], StringComparison.Ordinal);
        bool Same(int i, int j) => Exact(i, j) || string.Equals(la[i], lb[j], StringComparison.Ordinal);

        var dp = new int[m + 1, n + 1];
        for (var i = 0; i <= m; i++)
        {
            dp[i, 0] = i;
        }

        for (var j = 0; j <= n; j++)
        {
            dp[0, j] = j;
        }

        for (var i = 1; i <= m; i++)
        {
            for (var j = 1; j <= n; j++)
            {
                var diag = dp[i - 1, j - 1] + (Same(i - 1, j - 1) ? 0 : 1);
                dp[i, j] = Math.Min(diag, Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1));
            }
        }

        var steps = new List<AlignmentOp>(m + n);
        var ci = m;
        var cj = n;
        while (ci > 0 || cj > 0)
        {
            if (ci > 0 && cj > 0)
            {
                var diagCost = dp[ci - 1, cj - 1] + (Same(ci - 1, cj - 1) ? 0 : 1);
                if (dp[ci, cj] == diagCost)
                {
                    var label = Exact(ci - 1, cj - 1)
                        ? Label.Match
                        : Same(ci - 1, cj - 1)
                            ? Label.Casing
                            : Label.Substitute;
                    steps.Add(new AlignmentOp(label, a[ci - 1], b[cj - 1]));
                    ci--;
                    cj--;
                    continue;
                }
            }

            if (ci > 0 && dp[ci, cj] == dp[ci - 1, cj] + 1)
            {
                steps.Add(new AlignmentOp(Label.Delete, a[ci - 1], null));
                ci--;
                continue;
            }

            steps.Add(new AlignmentOp(Label.Insert, null, b[cj - 1]));
            cj--;
        }

        steps.Reverse();
        return steps;
    }
}
