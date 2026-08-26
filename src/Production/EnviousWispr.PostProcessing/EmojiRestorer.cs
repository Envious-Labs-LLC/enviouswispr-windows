using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EnviousWispr.PostProcessing;

public sealed record EmojiRestoreResult(
    string Text,
    int EmojiInInput,
    int Dropped,
    int Restored);

public static partial class EmojiRestorer
{
    public const int MaximumAlignmentTokens = 1_000;

    private static readonly HashSet<char> BoundTrailingSymbols = ['%', '°', '+', '#', '*', '‰'];

    private static readonly HashSet<char> NoSpaceBefore =
    [
        '.', ',', '!', '?', ';', ':', ')', ']', '}', '%', '°', '\'', '’', '…',
    ];

    public static EmojiRestoreResult Restore(string polished, string prePolish)
    {
        ArgumentNullException.ThrowIfNull(polished);
        ArgumentNullException.ThrowIfNull(prePolish);

        var preEmoji = EmojiClusters(prePolish).ToArray();
        var emojiInInput = preEmoji.Length;
        if (emojiInInput == 0)
        {
            return new EmojiRestoreResult(polished, 0, 0, 0);
        }

        var preWords = WordTokens(prePolish);
        var postWords = WordTokens(polished);
        var postGlyphs = EmojiClusters(polished)
            .Select(cluster => new PostGlyph(
                MatchKey(cluster.Text),
                LeftKey(postWords, cluster.Start),
                cluster.Start,
                cluster.End))
            .ToArray();

        var droppedFlags = Enumerable.Repeat(true, preEmoji.Length).ToArray();
        var keptPostStart = new int?[preEmoji.Length];
        var keptPostEnd = new int?[preEmoji.Length];

        for (var index = 0; index < preEmoji.Length; index++)
        {
            var key = MatchKey(preEmoji[index].Text);
            var leftKey = LeftKey(preWords, preEmoji[index].Start);
            var match = Array.FindIndex(
                postGlyphs,
                glyph => !glyph.Consumed &&
                    glyph.Key.Equals(key, StringComparison.Ordinal) &&
                    glyph.LeftKey.Equals(leftKey, StringComparison.Ordinal));
            if (match >= 0)
            {
                Consume(postGlyphs[match], index, droppedFlags, keptPostStart, keptPostEnd);
            }
        }

        for (var index = 0; index < preEmoji.Length; index++)
        {
            if (!droppedFlags[index])
            {
                continue;
            }

            var key = MatchKey(preEmoji[index].Text);
            var match = Array.FindIndex(
                postGlyphs,
                glyph => !glyph.Consumed && glyph.Key.Equals(key, StringComparison.Ordinal));
            if (match >= 0)
            {
                Consume(postGlyphs[match], index, droppedFlags, keptPostStart, keptPostEnd);
            }
        }

        var droppedCount = droppedFlags.Count(value => value);
        if (droppedCount == 0)
        {
            return new EmojiRestoreResult(polished, emojiInInput, 0, 0);
        }

        if (Math.Max(preWords.Length, postWords.Length) > MaximumAlignmentTokens)
        {
            return new EmojiRestoreResult(polished, emojiInInput, droppedCount, 0);
        }

        var runs = BuildDroppedRuns(prePolish, preEmoji, droppedFlags);
        var alignment = Align(preWords, postWords);
        var sentences = SentenceSpans(prePolish);
        var insertions = runs
            .Select((run, order) => new Insertion(
                ResolvePosition(
                    run,
                    prePolish,
                    polished,
                    preEmoji,
                    droppedFlags,
                    keptPostStart,
                    keptPostEnd,
                    preWords,
                    postWords,
                    alignment,
                    sentences),
                run.Glyphs,
                order))
            .OrderBy(insertion => insertion.Index)
            .ThenBy(insertion => insertion.Order)
            .ToArray();

        var result = new StringBuilder(polished.Length + runs.Sum(run => run.Glyphs.Length + 2));
        var cursor = 0;
        foreach (var insertion in insertions)
        {
            var position = Math.Max(cursor, Math.Min(insertion.Index, polished.Length));
            result.Append(polished, cursor, position - cursor);
            if (result.Length > 0 && !char.IsWhiteSpace(result[^1]))
            {
                result.Append(' ');
            }

            result.Append(insertion.Text);
            if (position < polished.Length &&
                !char.IsWhiteSpace(polished[position]) &&
                !NoSpaceBefore.Contains(polished[position]))
            {
                result.Append(' ');
            }

            cursor = position;
        }

        result.Append(polished, cursor, polished.Length - cursor);
        return new EmojiRestoreResult(result.ToString(), emojiInInput, droppedCount, droppedCount);
    }

    public static int AlignmentTokenCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WordRegex().Count(text);
    }

    private static void Consume(
        PostGlyph glyph,
        int preIndex,
        bool[] droppedFlags,
        int?[] keptPostStart,
        int?[] keptPostEnd)
    {
        glyph.Consumed = true;
        droppedFlags[preIndex] = false;
        keptPostStart[preIndex] = glyph.Start;
        keptPostEnd[preIndex] = glyph.End;
    }

    private static string LeftKey(IReadOnlyList<WordToken> words, int index)
    {
        var answer = string.Empty;
        foreach (var word in words)
        {
            if (word.End <= index)
            {
                answer = word.Key;
            }
            else
            {
                break;
            }
        }

        return answer;
    }

    private static DroppedRun[] BuildDroppedRuns(
        string prePolish,
        EmojiCluster[] emoji,
        bool[] dropped)
    {
        var runs = new List<DroppedRun>();
        var index = 0;
        while (index < emoji.Length)
        {
            if (!dropped[index])
            {
                index++;
                continue;
            }

            var start = emoji[index].Start;
            var end = emoji[index].End;
            var next = index + 1;
            while (next < emoji.Length &&
                dropped[next] &&
                IsWhitespaceOnly(prePolish, end, emoji[next].Start))
            {
                end = emoji[next].End;
                next++;
            }

            runs.Add(new DroppedRun(
                prePolish[start..end],
                start,
                end,
                index,
                next));
            index = next;
        }

        return runs.ToArray();
    }

    private static int ResolvePosition(
        DroppedRun run,
        string prePolish,
        string polished,
        EmojiCluster[] preEmoji,
        bool[] dropped,
        int?[] keptPostStart,
        int?[] keptPostEnd,
        IReadOnlyList<WordToken> preWords,
        IReadOnlyList<WordToken> postWords,
        int?[] alignment,
        IReadOnlyList<TextSpan> sentences)
    {
        int? PostImageStart(int index) => alignment[index] is int match ? postWords[match].Start : null;
        int? PostImageEnd(int index) => alignment[index] is int match ? postWords[match].End : null;

        var sentence = sentences.FirstOrDefault(span => span.Contains(run.Start));
        if (sentence == default)
        {
            sentence = new TextSpan(0, prePolish.Length);
        }

        var leftWord = LeftWordIn(preWords, run.Start, sentence.Start);
        var rightWord = RightWordIn(preWords, run.End, sentence.End);
        var hugLeft = !LeftSeparator(prePolish, run.Start);
        var rightSurvives = rightWord is int rightIndex && PostImageStart(rightIndex) is not null;
        var followBreak = rightSurvives && !PreEnderRight(prePolish, run.End);
        var trailsSeparator = LeftSeparator(prePolish, run.Start) && !rightSurvives;

        int AfterLeft(int end)
        {
            var position = TokenEnd(polished, end);
            if (followBreak || trailsSeparator)
            {
                while (position < polished.Length && IsEnder(polished[position]))
                {
                    position++;
                }
            }

            return position;
        }

        int? Scan(int low, int high)
        {
            var candidates = preWords
                .Select((word, index) => (word, index))
                .Where(candidate => candidate.word.Start >= low && candidate.word.End <= high)
                .Where(candidate => candidate.word.End <= run.Start || candidate.word.Start >= run.End)
                .Select(candidate => candidate.word.End <= run.Start
                    ? new AnchorCandidate(candidate.index, true, run.Start - candidate.word.End)
                    : new AnchorCandidate(candidate.index, false, candidate.word.Start - run.End))
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Left == hugLeft ? 0 : 1);

            foreach (var candidate in candidates)
            {
                if (candidate.Left && PostImageEnd(candidate.Index) is int end)
                {
                    return AfterLeft(end);
                }

                if (!candidate.Left && PostImageStart(candidate.Index) is int start)
                {
                    return start;
                }
            }

            return null;
        }

        int corePosition;
        if (hugLeft && leftWord is int left && PostImageEnd(left) is int leftEnd)
        {
            corePosition = AfterLeft(leftEnd);
        }
        else if (!hugLeft && rightWord is int right && PostImageStart(right) is int rightStart)
        {
            corePosition = rightStart;
        }
        else if (!hugLeft && rightWord is null && leftWord is int trailingLeft &&
            PostImageEnd(trailingLeft) is int trailingEnd)
        {
            corePosition = AfterLeft(trailingEnd);
        }
        else
        {
            corePosition = Scan(sentence.Start, sentence.End) ??
                Scan(0, prePolish.Length) ??
                polished.Length;
        }

        var low = 0;
        var high = polished.Length;
        if (run.PreStart > 0 &&
            !dropped[run.PreStart - 1] &&
            keptPostEnd[run.PreStart - 1] is int priorEnd &&
            IsWhitespaceOnly(prePolish, preEmoji[run.PreStart - 1].End, run.Start))
        {
            low = priorEnd;
        }

        if (run.PreEnd < preEmoji.Length &&
            !dropped[run.PreEnd] &&
            keptPostStart[run.PreEnd] is int nextStart &&
            IsWhitespaceOnly(prePolish, run.End, preEmoji[run.PreEnd].Start))
        {
            high = nextStart;
        }

        return low <= high ? Math.Min(Math.Max(corePosition, low), high) : corePosition;
    }

    private static int? LeftWordIn(IReadOnlyList<WordToken> words, int position, int low)
    {
        int? answer = null;
        for (var index = 0; index < words.Count; index++)
        {
            if (words[index].Start < low)
            {
                continue;
            }

            if (words[index].End <= position)
            {
                answer = index;
            }
            else
            {
                break;
            }
        }

        return answer;
    }

    private static int? RightWordIn(IReadOnlyList<WordToken> words, int position, int high)
    {
        for (var index = 0; index < words.Count; index++)
        {
            if (words[index].Start >= position && words[index].End <= high)
            {
                return index;
            }
        }

        return null;
    }

    private static bool LeftSeparator(string text, int start)
    {
        var index = start - 1;
        while (index >= 0 && char.IsWhiteSpace(text[index]))
        {
            index--;
        }

        return index >= 0 &&
            !char.IsLetterOrDigit(text, index) &&
            !BoundTrailingSymbols.Contains(text[index]) &&
            !IsEmojiAt(text, index);
    }

    private static bool PreEnderRight(string text, int end)
    {
        var index = end;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index < text.Length && IsEnder(text[index]);
    }

    private static int TokenEnd(string text, int end)
    {
        var position = end;
        while (position < text.Length)
        {
            if (BoundTrailingSymbols.Contains(text[position]))
            {
                position++;
                continue;
            }

            if (text[position] is '-' or '‑' &&
                position + 1 < text.Length &&
                char.IsLetterOrDigit(text, position + 1))
            {
                position++;
                while (position < text.Length && char.IsLetterOrDigit(text, position))
                {
                    position++;
                }

                continue;
            }

            break;
        }

        return position;
    }

    private static TextSpan[] SentenceSpans(string text)
    {
        var spans = new List<TextSpan>();
        var low = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (IsEnder(text[index]))
            {
                var dotGuarded = text[index] == '.' &&
                    index > 0 &&
                    index + 1 < text.Length &&
                    char.IsLetterOrDigit(text, index - 1) &&
                    char.IsLetterOrDigit(text, index + 1);
                if (!dotGuarded)
                {
                    var next = index + 1;
                    while (next < text.Length && IsEnder(text[next]))
                    {
                        next++;
                    }

                    spans.Add(new TextSpan(low, next));
                    low = next;
                    index = next;
                    continue;
                }
            }

            index++;
        }

        if (low < text.Length)
        {
            spans.Add(new TextSpan(low, text.Length));
        }

        return spans.ToArray();
    }

    private static bool IsWhitespaceOnly(string text, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEnder(char value) => value is '.' or '!' or '?';

    private static WordToken[] WordTokens(string text) =>
        WordRegex().Matches(text)
            .Select(match => new WordToken(
                match.Value.Replace('’', '\'').ToLowerInvariant(),
                match.Index,
                match.Length))
            .ToArray();

    private static int?[] Align(
        IReadOnlyList<WordToken> before,
        IReadOnlyList<WordToken> after)
    {
        var matches = new int?[before.Count];
        if (before.Count == 0 || after.Count == 0)
        {
            return matches;
        }

        var table = new int[before.Count + 1, after.Count + 1];
        for (var left = before.Count - 1; left >= 0; left--)
        {
            for (var right = after.Count - 1; right >= 0; right--)
            {
                table[left, right] = before[left].Key.Equals(after[right].Key, StringComparison.Ordinal)
                    ? table[left + 1, right + 1] + 1
                    : Math.Max(table[left + 1, right], table[left, right + 1]);
            }
        }

        var beforeIndex = 0;
        var afterIndex = 0;
        while (beforeIndex < before.Count && afterIndex < after.Count)
        {
            if (before[beforeIndex].Key.Equals(after[afterIndex].Key, StringComparison.Ordinal))
            {
                matches[beforeIndex++] = afterIndex++;
            }
            else if (table[beforeIndex + 1, afterIndex] >= table[beforeIndex, afterIndex + 1])
            {
                beforeIndex++;
            }
            else
            {
                afterIndex++;
            }
        }

        return matches;
    }

    private static IEnumerable<EmojiCluster> EmojiClusters(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var rune = element.EnumerateRunes().FirstOrDefault();
            if (IsEmojiRune(rune))
            {
                yield return new EmojiCluster(enumerator.ElementIndex, element.Length, element);
            }
        }
    }

    private static bool IsEmojiAt(string text, int index)
    {
        if (index < 0 || index >= text.Length || !Rune.TryGetRuneAt(text, index, out var rune))
        {
            return false;
        }

        return IsEmojiRune(rune);
    }

    private static bool IsEmojiRune(Rune rune) => rune.Value is
        (>= 0x1F000 and <= 0x1FAFF) or
        (>= 0x2600 and <= 0x27BF) or
        (>= 0x2B00 and <= 0x2BFF) or
        (>= 0x2190 and <= 0x21FF) or
        (>= 0x2300 and <= 0x23FF);

    private static string MatchKey(string value) => string.Concat(value.EnumerateRunes()
        .Where(rune => rune.Value != 0xFE0F && rune.Value is not (>= 0x1F3FB and <= 0x1F3FF))
        .Select(rune => rune.ToString()));

    private sealed record EmojiCluster(int Start, int Length, string Text)
    {
        public int End => Start + Length;
    }

    private sealed class PostGlyph(string key, string leftKey, int start, int end)
    {
        public string Key { get; } = key;

        public string LeftKey { get; } = leftKey;

        public int Start { get; } = start;

        public int End { get; } = end;

        public bool Consumed { get; set; }
    }

    private sealed record DroppedRun(
        string Glyphs,
        int Start,
        int End,
        int PreStart,
        int PreEnd);

    private sealed record WordToken(string Key, int Start, int Length)
    {
        public int End => Start + Length;
    }

    private readonly record struct TextSpan(int Start, int End)
    {
        public bool Contains(int index) => index >= Start && index < End;
    }

    private sealed record Insertion(int Index, string Text, int Order);

    private readonly record struct AnchorCandidate(int Index, bool Left, int Distance);

    [GeneratedRegex(
        @"[\p{L}\p{N}]+(?:['\u2019][\p{L}\p{N}]+)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
