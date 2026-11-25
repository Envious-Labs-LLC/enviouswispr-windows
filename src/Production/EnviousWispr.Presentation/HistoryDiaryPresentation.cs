using System.Globalization;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>Relative local-calendar date range filter for History diary entries.</summary>
public enum HistoryDateRangeFilter
{
    AnyTime,
    Today,
    Yesterday,
    Last7Days,
    Last30Days,
}

/// <summary>Display option for one <see cref="HistoryDateRangeFilter"/> value.</summary>
public sealed record HistoryDateRangeOption(HistoryDateRangeFilter Filter, string Label);

/// <summary>Formatted day-section header for a group of History entries.</summary>
public sealed record HistoryDayHeader(
    DateOnly Day,
    string PrimaryTitle,
    string? SecondaryDateLabel,
    string? RelativeLabel,
    string AccessibilityLabel);

/// <summary>Entries sharing a single local calendar day in the chronological diary.</summary>
public sealed record HistoryDayGroup(
    HistoryDayHeader Header,
    IReadOnlyList<DictationHistoryEntry> Entries);

/// <summary>One bounded slice of filtered history entries plus how many older entries remain.</summary>
public sealed record HistoryPagedResult(
    IReadOnlyList<DictationHistoryEntry> Items,
    int RemainingCount)
{
    /// <summary>Whether older matching entries exist beyond the loaded slice.</summary>
    public bool HasMore => RemainingCount > 0;
}

/// <summary>One explanatory row inside the History help flyout.</summary>
public sealed record HistoryHelpItem(string Glyph, string Title, string Body);

/// <summary>Pure presentation helpers for the chronological History card diary.</summary>
public static class HistoryDiaryPresentation
{
    /// <summary>Number of history cards rendered initially and added per page step.</summary>
    public const int DefaultPageSize = 30;

    /// <summary>Date filter options ordered for dropdown menus.</summary>
    public static IReadOnlyList<HistoryDateRangeOption> DateRangeOptions { get; } =
    [
        new(HistoryDateRangeFilter.AnyTime, "Any time"),
        new(HistoryDateRangeFilter.Today, "Today"),
        new(HistoryDateRangeFilter.Yesterday, "Yesterday"),
        new(HistoryDateRangeFilter.Last7Days, "Last 7 days"),
        new(HistoryDateRangeFilter.Last30Days, "Last 30 days"),
    ];

    /// <summary>Human-readable label for a <see cref="HistoryDateRangeFilter"/>.</summary>
    public static string DateRangeLabel(HistoryDateRangeFilter filter) => filter switch
    {
        HistoryDateRangeFilter.Today => "Today",
        HistoryDateRangeFilter.Yesterday => "Yesterday",
        HistoryDateRangeFilter.Last7Days => "Last 7 days",
        HistoryDateRangeFilter.Last30Days => "Last 30 days",
        _ => "Any time",
    };

    /// <summary>Matches transcript text, unpolished original words, speech engine, or polish model names.</summary>
    public static bool MatchesSearch(DictationHistoryEntry entry, string trimmedQuery)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(trimmedQuery);
        if (trimmedQuery.Length == 0)
        {
            return true;
        }

        if (entry.Text.Contains(trimmedQuery, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entry.OriginalText)
            && entry.OriginalText.Contains(trimmedQuery, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entry.EngineId))
        {
            if (entry.EngineId.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var displayEngine = FormatEngineDisplayName(entry.EngineId);
            if (!string.IsNullOrWhiteSpace(displayEngine)
                && displayEngine.Contains(trimmedQuery, StringComparison.CurrentCultureIgnoreCase))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.PolishModelId))
        {
            if (entry.PolishModelId.Contains(trimmedQuery, StringComparison.CurrentCultureIgnoreCase))
            {
                return true;
            }

            var displayPolishModel = FormatPolishModelLabel(entry.PolishModelId);
            if (!string.IsNullOrWhiteSpace(displayPolishModel)
                && displayPolishModel.Contains(trimmedQuery, StringComparison.CurrentCultureIgnoreCase))
            {
                return true;
            }
        }

        var displayProvider = FormatPolishProviderLabel(entry.PolishProviderId);
        if (!string.IsNullOrWhiteSpace(displayProvider)
            && displayProvider.Contains(trimmedQuery, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entry.LanguageCode))
        {
            var displayLanguage = LanguageCatalog.DisplayNameFor(entry.LanguageCode);
            if (!string.IsNullOrWhiteSpace(displayLanguage)
                && displayLanguage.Contains(trimmedQuery, StringComparison.CurrentCultureIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Formats a stored speech engine ID into a user-facing label.</summary>
    public static string FormatEngineDisplayName(string engineId)
    {
        ArgumentNullException.ThrowIfNull(engineId);
        var raw = TranscriptionEngineNames.DisplayName(engineId);
        if (!string.Equals(raw, engineId, StringComparison.Ordinal))
        {
            return raw;
        }

        if (engineId.Contains("parakeet", StringComparison.OrdinalIgnoreCase))
        {
            return TranscriptionEngineNames.Parakeet;
        }

        if (engineId.Contains("whisper", StringComparison.OrdinalIgnoreCase))
        {
            return TranscriptionEngineNames.Whisper;
        }

        return raw;
    }

    /// <summary>Formats a stored AI polish model ID into its display label.</summary>
    public static string? FormatPolishModelLabel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        return modelId.Trim().ToLowerInvariant() switch
        {
            "eg-1" or "eg1" => "EG-1",
            "eg-1-mini" or "eg1-mini" => "EG-1 Mini",
            "eg-1-micro" or "eg1-micro" => "EG-1 Micro",
            _ => modelId.Trim(),
        };
    }

    /// <summary>Formats a stored AI polish provider ID into its display label.</summary>
    public static string? FormatPolishProviderLabel(string? providerId) =>
        providerId?.Trim().ToLowerInvariant() switch
        {
            "eg_one" or "egone" or "enviousegone" => "EG-1",
            "ollama" => "Ollama",
            "openai" => "OpenAI",
            "anthropic" => "Anthropic",
            "gemini" => "Gemini",
            _ => null,
        };

    /// <summary>Evaluates whether <paramref name="entry"/> falls within <paramref name="dateRange"/> in local time.</summary>
    public static bool MatchesDateRange(
        DictationHistoryEntry entry,
        HistoryDateRangeFilter dateRange,
        DateTimeOffset localNow,
        TimeZoneInfo? localTimeZone = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (dateRange == HistoryDateRangeFilter.AnyTime)
        {
            return true;
        }

        var zone = localTimeZone ?? TimeZoneInfo.Local;
        var entryDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.CreatedAt, zone).DateTime);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(localNow, zone).DateTime);

        return dateRange switch
        {
            HistoryDateRangeFilter.Today => entryDay == today,
            HistoryDateRangeFilter.Yesterday => entryDay == today.AddDays(-1),
            HistoryDateRangeFilter.Last7Days => entryDay >= today.AddDays(-6) && entryDay <= today,
            HistoryDateRangeFilter.Last30Days => entryDay >= today.AddDays(-29) && entryDay <= today,
            _ => true,
        };
    }

    /// <summary>Whether an entry has meaningful AI Polish edits distinct from its original words.</summary>
    public static bool HasPolishEdits(DictationHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.WasPolished
            && !string.IsNullOrWhiteSpace(entry.OriginalText)
            && !string.Equals(entry.OriginalText, entry.Text, StringComparison.Ordinal);
    }

    /// <summary>Formats day-section headings matching macOS diary conventions ("Today", "Yesterday", "Monday, November 24").</summary>
    public static HistoryDayHeader FormatDayHeader(
        DateOnly day,
        DateOnly today,
        CultureInfo? culture = null)
    {
        var activeCulture = culture ?? CultureInfo.CurrentCulture;
        var format = day.Year == today.Year ? "dddd, MMMM d" : "dddd, MMMM d, yyyy";
        var dateLabel = day.ToDateTime(TimeOnly.MinValue).ToString(format, activeCulture);

        string? relativeLabel = day == today
            ? "Today"
            : day == today.AddDays(-1)
                ? "Yesterday"
                : null;

        var primaryTitle = relativeLabel ?? dateLabel;
        var secondaryDateLabel = relativeLabel is not null ? dateLabel : null;
        var accessibilityLabel = secondaryDateLabel is not null
            ? $"{primaryTitle}, {secondaryDateLabel}"
            : primaryTitle;

        return new HistoryDayHeader(day, primaryTitle, secondaryDateLabel, relativeLabel, accessibilityLabel);
    }

    /// <summary>Groups entries by local calendar day while preserving entry order within each day.</summary>
    public static IReadOnlyList<HistoryDayGroup> GroupByDay(
        IReadOnlyList<DictationHistoryEntry> entries,
        DateTimeOffset localNow,
        TimeZoneInfo? localTimeZone = null,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return Array.Empty<HistoryDayGroup>();
        }

        var zone = localTimeZone ?? TimeZoneInfo.Local;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(localNow, zone).DateTime);
        var groupsByDay = new Dictionary<DateOnly, List<DictationHistoryEntry>>();
        var orderedDays = new List<DateOnly>();

        foreach (var entry in entries)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.CreatedAt, zone).DateTime);
            if (!groupsByDay.TryGetValue(day, out var bucket))
            {
                bucket = new List<DictationHistoryEntry>();
                groupsByDay[day] = bucket;
                orderedDays.Add(day);
            }

            bucket.Add(entry);
        }

        var result = new List<HistoryDayGroup>(orderedDays.Count);
        foreach (var day in orderedDays)
        {
            result.Add(new HistoryDayGroup(FormatDayHeader(day, today, culture), groupsByDay[day]));
        }

        return result;
    }

    /// <summary>Counts total matching entries per local calendar day across the full unpaged result set.</summary>
    public static IReadOnlyDictionary<DateOnly, int> CountByDay(
        IReadOnlyList<DictationHistoryEntry> entries,
        TimeZoneInfo? localTimeZone = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var counts = new Dictionary<DateOnly, int>();
        if (entries.Count == 0)
        {
            return counts;
        }

        var zone = localTimeZone ?? TimeZoneInfo.Local;
        foreach (var entry in entries)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.CreatedAt, zone).DateTime);
            counts[day] = counts.TryGetValue(day, out var count) ? count + 1 : 1;
        }

        return counts;
    }

    /// <summary>Formats an entry count badge for a day header or selection bar.</summary>
    public static string FormatItemCount(int count) =>
        count == 1 ? "1 item" : $"{count} items";

    /// <summary>Formats the selection count headline shown in the selection toolbar.</summary>
    public static string FormatSelectionCount(int count) =>
        count == 1 ? "1 selected" : $"{count} selected";

    /// <summary>Returns at most <paramref name="maxCount"/> entries along with how many remain.</summary>
    public static HistoryPagedResult TakePage(IReadOnlyList<DictationHistoryEntry> entries, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var limit = Math.Max(0, maxCount);
        if (entries.Count <= limit)
        {
            return new HistoryPagedResult(entries, 0);
        }

        return new HistoryPagedResult(entries.Take(limit).ToArray(), entries.Count - limit);
    }

    /// <summary>Keeps only selected IDs whose entries still remain after a delete attempt.</summary>
    public static IReadOnlySet<Guid> ReconcileSelectionAfterDelete(
        IReadOnlyCollection<Guid> selectedIds,
        IEnumerable<DictationHistoryEntry> remainingEntries)
    {
        ArgumentNullException.ThrowIfNull(selectedIds);
        ArgumentNullException.ThrowIfNull(remainingEntries);
        if (selectedIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var surviving = new HashSet<Guid>(remainingEntries.Select(entry => entry.Id));
        var next = new HashSet<Guid>();
        foreach (var id in selectedIds)
        {
            if (surviving.Contains(id))
            {
                next.Add(id);
            }
        }

        return next;
    }

    /// <summary>Returns the help items displayed in the "How History works" flyout.</summary>
    public static IReadOnlyList<HistoryHelpItem> HelpItems(int historyRetentionDays)
    {
        var retentionBody = historyRetentionDays switch
        {
            <= 0 => "History stays on this PC until you delete it. Escape Recovery copies expire after 24 hours unless you choose Keep. Change retention anytime in Settings > App Settings > Privacy.",
            1 => "History stays on this PC for 1 day. Escape Recovery copies expire after 24 hours unless you choose Keep, which keeps them under your 1-day history retention. Change retention anytime in Settings > App Settings > Privacy.",
            _ => $"History stays on this PC for {historyRetentionDays} days. Escape Recovery copies expire after 24 hours unless you choose Keep, which keeps them under that {historyRetentionDays}-day retention. Change retention anytime in Settings > App Settings > Privacy.",
        };

        return
        [
            new(
                "\uE721",
                "Search and filters",
                "Search polished text, unpolished words, speech engines, or AI models. Narrow results by tab or date range."),
            new(
                "\uE70F",
                "Show edits and re-polish",
                "Compare what AI Polish changed against your original unpolished words right on a card, or re-polish older dictations anytime."),
            new(
                "\uE72E",
                "Stored locally on this PC",
                retentionBody),
            new(
                "\uE73A",
                "Delete several at once",
                "Choose Select, tick the cards you want or use Select all matching, then delete them together."),
        ];
    }
}
