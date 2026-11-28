using System.Text;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Pipeline;

/// <summary>Thread-safe snapshot of committed streaming progress shared with live preview.</summary>
public sealed record StreamingProgressSnapshot(
    DictationSessionId SessionId,
    int CommittedThroughSample,
    string CommittedPrefix)
{
    public StreamingProgressSnapshot(Guid sessionValue, int committedThroughSample, string committedPrefix)
        : this(new Core.Dictation.DictationSessionId(sessionValue), committedThroughSample, committedPrefix)
    {
    }
}

/// <summary>
/// UFAL <c>LocalAgreement-2</c> word-unit stabilizer for live preview over a rolling window,
/// anchored behind any streaming quiet-valley chunks already finalized by ASR.
/// </summary>
public sealed class LivePreviewAgreementStabilizer
{
    public readonly record struct WordUnit(string RawSegment, string Key);

    private int _lastAnchoredSample;
    private string _anchoredPrefix = string.Empty;
    private readonly List<WordUnit> _agreedTail = [];
    private List<WordUnit> _previousHypothesis = [];

    public IReadOnlyList<WordUnit> AgreedTail => _agreedTail;

    public string AnchoredPrefix => _anchoredPrefix;

    public int LastAnchoredSample => _lastAnchoredSample;

    /// <summary>
    /// Advances stabilization with a newly decoded trailing hypothesis and optional committed-prefix mark.
    /// </summary>
    public string Stabilize(string rawTrailingHypothesis, string? committedPrefix = null, int committedThroughSample = 0)
    {
        if (committedThroughSample > _lastAnchoredSample)
        {
            _lastAnchoredSample = committedThroughSample;
            _anchoredPrefix = committedPrefix?.Trim() ?? string.Empty;
            // Committed chunks moved out of the trailing preview slice; start trailing agreement fresh.
            _agreedTail.Clear();
            _previousHypothesis.Clear();
        }
        else if (!string.IsNullOrWhiteSpace(committedPrefix) && string.IsNullOrEmpty(_anchoredPrefix))
        {
            _anchoredPrefix = committedPrefix.Trim();
        }

        var units = TokenizeUnits(rawTrailingHypothesis);
        if (units.Count == 0)
        {
            var fallbackTail = JoinUnits(_agreedTail);
            return CombineAnchoredAndTail(_anchoredPrefix, fallbackTail);
        }

        var cut = ComputeCommittedCutIndex(_agreedTail, units);
        var fresh = units.GetRange(cut, units.Count - cut);

        var agreed = 0;
        while (agreed < fresh.Count &&
               agreed < _previousHypothesis.Count &&
               string.Equals(fresh[agreed].Key, _previousHypothesis[agreed].Key, StringComparison.Ordinal))
        {
            agreed++;
        }

        for (var i = 0; i < agreed; i++)
        {
            _agreedTail.Add(fresh[i]);
        }

        _previousHypothesis = fresh.GetRange(agreed, fresh.Count - agreed);

        var tailText = JoinUnits(_agreedTail, _previousHypothesis);
        return CombineAnchoredAndTail(_anchoredPrefix, tailText);
    }

    internal static int ComputeCommittedCutIndex(IReadOnlyList<WordUnit> committed, IReadOnlyList<WordUnit> hypothesis)
    {
        if (committed.Count == 0 || hypothesis.Count == 0)
        {
            return 0;
        }

        // Exact prefix match first (most common rolling decode case).
        var maxPrefix = Math.Min(committed.Count, hypothesis.Count);
        var exact = 0;
        while (exact < maxPrefix &&
               string.Equals(committed[exact].Key, hypothesis[exact].Key, StringComparison.Ordinal))
        {
            exact++;
        }

        if (exact == committed.Count)
        {
            return exact;
        }

        // Allow at most 1 acoustic revision inside the already-committed head window when aligned afterwards
        // so minor revisions (e.g., "thermal" -> "formal") do not re-emit previously locked words.
        if (committed.Count >= 2 && hypothesis.Count >= committed.Count)
        {
            var matches = 0;
            for (var i = 0; i < committed.Count; i++)
            {
                if (string.Equals(committed[i].Key, hypothesis[i].Key, StringComparison.Ordinal))
                {
                    matches++;
                }
            }

            if (matches >= committed.Count - 1)
            {
                return committed.Count;
            }
        }

        // When a sliding window trimmed off older committed words, look for suffix-of-committed == prefix-of-hypothesis.
        var maxOverlap = Math.Min(committed.Count, hypothesis.Count);
        for (var overlap = maxOverlap; overlap >= 1; overlap--)
        {
            var start = committed.Count - overlap;
            var aligned = true;
            for (var i = 0; i < overlap; i++)
            {
                if (!string.Equals(committed[start + i].Key, hypothesis[i].Key, StringComparison.Ordinal))
                {
                    aligned = false;
                    break;
                }
            }

            if (aligned)
            {
                return overlap;
            }
        }

        return exact;
    }

    internal static List<WordUnit> TokenizeUnits(string? text)
    {
        var units = new List<WordUnit>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return units;
        }

        var span = text.AsSpan();
        var index = 0;
        while (index < span.Length)
        {
            var tokenStart = index;
            while (index < span.Length && char.IsWhiteSpace(span[index]))
            {
                index++;
            }

            if (index >= span.Length)
            {
                break;
            }

            var wordStart = index;
            while (index < span.Length && !char.IsWhiteSpace(span[index]))
            {
                index++;
            }

            var rawSegment = span[tokenStart..index].ToString();
            var wordSpan = span[wordStart..index];
            var key = NormalizeWordKey(wordSpan);
            if (key.Length == 0)
            {
                key = wordSpan.ToString().ToLowerInvariant();
            }

            units.Add(new WordUnit(rawSegment, key));
        }

        return units;
    }

    private static string NormalizeWordKey(ReadOnlySpan<char> token)
    {
        var start = 0;
        while (start < token.Length && char.IsPunctuation(token[start]))
        {
            start++;
        }

        var end = token.Length - 1;
        while (end >= start && char.IsPunctuation(token[end]))
        {
            end--;
        }

        if (end < start)
        {
            return string.Empty;
        }

        return token[start..(end + 1)].ToString().ToLowerInvariant();
    }

    private static string JoinUnits(IReadOnlyList<WordUnit> committed, IReadOnlyList<WordUnit>? inProgress = null)
    {
        var sb = new StringBuilder();
        AppendUnits(sb, committed);
        if (inProgress is not null)
        {
            AppendUnits(sb, inProgress);
        }

        return sb.ToString().Trim();
    }

    private static void AppendUnits(StringBuilder sb, IReadOnlyList<WordUnit> units)
    {
        foreach (var unit in units)
        {
            if (string.IsNullOrEmpty(unit.RawSegment))
            {
                continue;
            }

            var segment = unit.RawSegment;
            if (sb.Length == 0)
            {
                sb.Append(segment.TrimStart());
                continue;
            }

            var needsSpace = !char.IsWhiteSpace(sb[^1]) && !char.IsWhiteSpace(segment[0]);
            if (needsSpace)
            {
                sb.Append(' ');
            }

            sb.Append(segment);
        }
    }

    private static string CombineAnchoredAndTail(string anchored, string tail)
    {
        if (string.IsNullOrWhiteSpace(anchored))
        {
            return tail;
        }

        if (string.IsNullOrWhiteSpace(tail))
        {
            return anchored;
        }

        var acc = new StreamingTranscriptAccumulator();
        acc.Append(anchored);
        acc.Append(tail);
        return acc.ToString();
    }
}
