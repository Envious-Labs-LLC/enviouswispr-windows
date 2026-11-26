using System.Globalization;

namespace EnviousWispr.Presentation;

public enum HistoryEditsViewMode
{
    Polished,
    MarkedUp,
    Original,
}

public enum MarkedUpRunKind
{
    Same,
    Removed,
    Added,
}

public sealed record MarkedUpRun(
    MarkedUpRunKind Kind,
    string Text,
    int? ChangeIndex = null,
    string? OriginalSnippet = null,
    string? PolishedSnippet = null);

public sealed record HistoryDiffResult(
    IReadOnlyList<MarkedUpRun> Runs,
    int ChangeCount,
    string SummaryText)
{
    public static readonly HistoryDiffResult Empty = new(
        Array.Empty<MarkedUpRun>(),
        0,
        string.Empty);
}

/// <summary>
/// Bounded LRU cache and run builder for inline AI polish markups in History cards.
/// </summary>
public sealed class HistoryDiffCache
{
    public const int DefaultCapacity = 64;

    private readonly int _capacity;
    private readonly Dictionary<DiffCacheKey, LinkedListNode<CacheEntry>> _entries = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly object _gate = new();

    public HistoryDiffCache(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Cache capacity must be positive.");
        }

        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public HistoryDiffResult GetOrCompute(string? originalText, string? polishedText, CultureInfo? culture = null)
    {
        var orig = originalText ?? string.Empty;
        var polished = polishedText ?? string.Empty;
        var cultureName = (culture ?? CultureInfo.InvariantCulture).Name;
        var key = new DiffCacheKey(orig, polished, cultureName);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return existing.Value.Result;
            }
        }

        var computed = HistoryEditsPresentation.ComputeDiff(orig, polished, culture);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return existing.Value.Result;
            }

            if (_entries.Count >= _capacity && _lru.Last is not null)
            {
                var evicted = _lru.Last;
                _lru.RemoveLast();
                _entries.Remove(evicted.Value.Key);
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, computed));
            _lru.AddFirst(node);
            _entries[key] = node;
            return computed;
        }
    }

    private readonly record struct DiffCacheKey(string Original, string Polished, string CultureName);

    private readonly record struct CacheEntry(DiffCacheKey Key, HistoryDiffResult Result);
}

public static class HistoryEditsPresentation
{
    public const string BeforePolishStatusText = "Your words before AI polish";
    public const string PolishedStatusText = "AI-polished transcript";
    public const string FormattingOnlySummaryText = "Punctuation & casing";

    public static HistoryDiffResult ComputeDiff(
        string? originalText,
        string? polishedText,
        CultureInfo? culture = null)
    {
        var orig = originalText ?? string.Empty;
        var polished = polishedText ?? string.Empty;

        if (string.IsNullOrWhiteSpace(orig) && string.IsNullOrWhiteSpace(polished))
        {
            return HistoryDiffResult.Empty;
        }

        if (string.IsNullOrWhiteSpace(orig))
        {
            var run = new MarkedUpRun(
                MarkedUpRunKind.Added,
                polished,
                ChangeIndex: 0,
                OriginalSnippet: string.Empty,
                PolishedSnippet: polished.Trim());
            return new HistoryDiffResult(new[] { run }, 1, FormatEditCountSummary(1, hasTextDifference: true));
        }

        if (string.IsNullOrWhiteSpace(polished))
        {
            var run = new MarkedUpRun(
                MarkedUpRunKind.Removed,
                orig,
                ChangeIndex: 0,
                OriginalSnippet: orig.Trim(),
                PolishedSnippet: string.Empty);
            return new HistoryDiffResult(new[] { run }, 1, FormatEditCountSummary(1, hasTextDifference: true));
        }

        var entries = WordDiff.Compute(orig, polished, culture);
        var (runs, changeCount) = BuildMarkedUpRuns(entries);
        var hasTextDifference = !string.Equals(orig.Trim(), polished.Trim(), StringComparison.Ordinal);
        var summary = FormatEditCountSummary(changeCount, hasTextDifference);
        return new HistoryDiffResult(runs, changeCount, summary);
    }

    public static (IReadOnlyList<MarkedUpRun> Runs, int ChangeCount) BuildMarkedUpRuns(
        IReadOnlyList<WordDiffEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var runs = new List<MarkedUpRun>(entries.Count);
        var changeIndex = 0;
        var i = 0;

        while (i < entries.Count)
        {
            if (entries[i].Kind == WordDiffKind.Equal)
            {
                var combined = entries[i].Text;
                i++;
                while (i < entries.Count && entries[i].Kind == WordDiffKind.Equal)
                {
                    combined += entries[i].Text;
                    i++;
                }

                runs.Add(new MarkedUpRun(MarkedUpRunKind.Same, combined));
                continue;
            }

            string? removedText = null;
            string? addedText = null;

            while (i < entries.Count && entries[i].Kind != WordDiffKind.Equal)
            {
                if (entries[i].Kind == WordDiffKind.Delete)
                {
                    removedText = (removedText ?? string.Empty) + entries[i].Text;
                }
                else if (entries[i].Kind == WordDiffKind.Insert)
                {
                    addedText = (addedText ?? string.Empty) + entries[i].Text;
                }

                i++;
            }

            var originalSnippet = removedText?.Trim() ?? string.Empty;
            var polishedSnippet = addedText?.Trim() ?? string.Empty;

            if (!string.IsNullOrEmpty(removedText))
            {
                runs.Add(new MarkedUpRun(
                    MarkedUpRunKind.Removed,
                    removedText,
                    changeIndex,
                    originalSnippet,
                    polishedSnippet));
            }

            if (!string.IsNullOrEmpty(addedText))
            {
                runs.Add(new MarkedUpRun(
                    MarkedUpRunKind.Added,
                    addedText,
                    changeIndex,
                    originalSnippet,
                    polishedSnippet));
            }

            if (!string.IsNullOrEmpty(removedText) || !string.IsNullOrEmpty(addedText))
            {
                changeIndex++;
            }
        }

        return (runs, changeIndex);
    }

    public static string FormatEditCountSummary(int changeCount, bool hasTextDifference = true)
    {
        if (changeCount <= 0)
        {
            return hasTextDifference ? FormattingOnlySummaryText : "No edits";
        }

        return changeCount == 1 ? "1 edit" : $"{changeCount} edits";
    }
}
