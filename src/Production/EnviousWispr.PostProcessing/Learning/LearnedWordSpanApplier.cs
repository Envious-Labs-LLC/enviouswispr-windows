using System.Text;
using EnviousWispr.Core.Learning;

namespace EnviousWispr.PostProcessing.Learning;

/// <summary>
/// Resolves duplicate, nested, and overlapping approved learned-word spans deterministically
/// and applies non-contested edits right-to-left. Mirrors macOS <c>LearnedWordSpanApplier</c>.
/// </summary>
public static class LearnedWordSpanApplier
{
    public sealed record Result(string Text, int Applied, int Contested);

    private sealed record ApprovedSpan(
        int RangeStart,
        int RangeLength,
        string Word,
        double? Score)
    {
        public int RangeEnd => RangeStart + RangeLength;
    }

    public static Result Apply(
        string text,
        IReadOnlyList<LearnedWordCheckQuestion> questions,
        IReadOnlyList<LearnedWordCheckDecision> decisions,
        bool scoresComparable = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(decisions);

        if (questions.Count == 0 || decisions.Count == 0)
        {
            return new Result(text, 0, 0);
        }

        var questionCounts = new Dictionary<int, int>(questions.Count);
        foreach (var q in questions)
        {
            questionCounts[q.Id] = questionCounts.GetValueOrDefault(q.Id) + 1;
        }

        var decisionCounts = new Dictionary<int, int>(decisions.Count);
        var decisionsById = new Dictionary<int, LearnedWordCheckDecision>(decisions.Count);
        foreach (var d in decisions)
        {
            decisionCounts[d.QuestionId] = decisionCounts.GetValueOrDefault(d.QuestionId) + 1;
            decisionsById.TryAdd(d.QuestionId, d);
        }

        var approved = new List<ApprovedSpan>();
        foreach (var q in questions)
        {
            if (questionCounts[q.Id] != 1 ||
                decisionCounts.GetValueOrDefault(q.Id) != 1 ||
                !decisionsById.TryGetValue(q.Id, out var decision) ||
                !decision.Approved ||
                !string.Equals(q.Sentence, text, StringComparison.Ordinal) ||
                q.RangeLength <= 0 ||
                q.RangeStart < 0 ||
                q.RangeStart + q.RangeLength > text.Length)
            {
                continue;
            }

            var score = decision.Score is { } s && double.IsFinite(s) ? s : (double?)null;
            approved.Add(new ApprovedSpan(q.RangeStart, q.RangeLength, q.Word, score));
        }

        if (approved.Count == 0)
        {
            return new Result(text, 0, 0);
        }

        var removed = new HashSet<int>();

        // Same-word duplicate or nested span: inner span wins (macOS parity).
        for (var left = 0; left < approved.Count; left++)
        {
            for (var right = left + 1; right < approved.Count; right++)
            {
                var a = approved[left];
                var b = approved[right];
                if (!string.Equals(a.Word, b.Word, StringComparison.Ordinal))
                {
                    continue;
                }

                if (a.RangeStart == b.RangeStart && a.RangeLength == b.RangeLength)
                {
                    removed.Add(right);
                }
                else if (Contains(a, b))
                {
                    removed.Add(left);
                }
                else if (Contains(b, a))
                {
                    removed.Add(right);
                }
            }
        }

        // Different words: longest strict container wins (#3518).
        var longestFirst = Enumerable.Range(0, approved.Count)
            .Where(i => !removed.Contains(i))
            .OrderByDescending(i => approved[i].RangeLength)
            .ThenBy(i => i)
            .ToArray();
        foreach (var outer in longestFirst)
        {
            if (removed.Contains(outer))
            {
                continue;
            }

            foreach (var inner in longestFirst)
            {
                if (inner == outer || removed.Contains(inner))
                {
                    continue;
                }

                var a = approved[outer];
                var b = approved[inner];
                if (!string.Equals(a.Word, b.Word, StringComparison.Ordinal) &&
                    !(a.RangeStart == b.RangeStart && a.RangeLength == b.RangeLength) &&
                    Contains(a, b))
                {
                    removed.Add(inner);
                }
            }
        }

        // Optional score tie-break for comparable checkers.
        if (scoresComparable)
        {
            var scored = Enumerable.Range(0, approved.Count)
                .Where(i => !removed.Contains(i) && approved[i].Score.HasValue)
                .OrderByDescending(i => approved[i].Score!.Value)
                .ThenBy(i => i)
                .ToArray();
            foreach (var winner in scored)
            {
                if (removed.Contains(winner))
                {
                    continue;
                }

                foreach (var loser in scored)
                {
                    if (loser == winner || removed.Contains(loser))
                    {
                        continue;
                    }

                    var a = approved[winner];
                    var b = approved[loser];
                    if (a.Score!.Value > b.Score!.Value &&
                        !string.Equals(a.Word, b.Word, StringComparison.Ordinal) &&
                        Overlaps(a, b))
                    {
                        removed.Add(loser);
                    }
                }
            }
        }

        // Any remaining overlap is contested: drop both.
        var contested = new HashSet<int>();
        for (var left = 0; left < approved.Count; left++)
        {
            if (removed.Contains(left))
            {
                continue;
            }

            for (var right = left + 1; right < approved.Count; right++)
            {
                if (removed.Contains(right))
                {
                    continue;
                }

                if (Overlaps(approved[left], approved[right]))
                {
                    contested.Add(left);
                    contested.Add(right);
                }
            }
        }

        var winners = Enumerable.Range(0, approved.Count)
            .Where(i => !removed.Contains(i) && !contested.Contains(i))
            .Select(i => approved[i])
            .OrderByDescending(s => s.RangeStart)
            .ToArray();

        var rewritten = new StringBuilder(text);
        foreach (var winner in winners)
        {
            rewritten.Remove(winner.RangeStart, winner.RangeLength).Insert(winner.RangeStart, winner.Word);
        }

        return new Result(rewritten.ToString(), winners.Length, contested.Count);
    }

    private static bool Contains(ApprovedSpan outer, ApprovedSpan inner) =>
        outer.RangeStart <= inner.RangeStart && inner.RangeEnd <= outer.RangeEnd;

    private static bool Overlaps(ApprovedSpan a, ApprovedSpan b) =>
        a.RangeStart < b.RangeEnd && b.RangeStart < a.RangeEnd;
}
