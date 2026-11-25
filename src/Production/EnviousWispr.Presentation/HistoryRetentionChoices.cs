using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>One selectable history-retention duration option for Settings.</summary>
public sealed record HistoryRetentionOption(int Days, string DisplayName)
{
    /// <inheritdoc />
    public override string ToString() => DisplayName;
}

/// <summary>The dropdown choices and active selection for history retention duration.</summary>
public sealed record HistoryRetentionChoiceState(
    IReadOnlyList<HistoryRetentionOption> Options,
    int SelectedIndex);

/// <summary>
/// Builds preset retention options while preserving any custom day count loaded from existing settings.
/// </summary>
public static class HistoryRetentionChoices
{
    private static readonly HistoryRetentionOption[] Presets =
    [
        new(0, "Keep until you delete it"),
        new(7, "After 7 days"),
        new(30, "After 30 days"),
        new(90, "After 90 days"),
        new(365, "After 1 year"),
    ];

    /// <summary>Resolves dropdown items and selected index for a stored retention value.</summary>
    public static HistoryRetentionChoiceState Resolve(int storedDays)
    {
        var normalized = RetentionDays.FromField(
            storedDays,
            HistoryPreferences.Default.RetentionDays,
            RetentionDays.HistoryMinimum,
            RetentionDays.HistoryMaximum);

        var options = new List<HistoryRetentionOption>(Presets.Length + 1);
        var insertedCustom = false;
        var selectedIndex = -1;

        foreach (var preset in Presets)
        {
            if (preset.Days == normalized)
            {
                selectedIndex = options.Count;
            }
            else if (!insertedCustom && normalized < preset.Days && !IsPreset(normalized))
            {
                selectedIndex = options.Count;
                options.Add(new HistoryRetentionOption(normalized, FormatCustomLabel(normalized)));
                insertedCustom = true;
            }

            options.Add(preset);
        }

        if (selectedIndex < 0)
        {
            selectedIndex = options.Count;
            options.Add(new HistoryRetentionOption(normalized, FormatCustomLabel(normalized)));
        }

        return new HistoryRetentionChoiceState(options, selectedIndex);
    }

    /// <summary>Extracts the day count to save from the selected dropdown item.</summary>
    public static int SelectedDaysOrFallback(object? selectedItem, int fallbackDays) =>
        selectedItem is HistoryRetentionOption option
            ? option.Days
            : RetentionDays.FromField(
                fallbackDays,
                HistoryPreferences.Default.RetentionDays,
                RetentionDays.HistoryMinimum,
                RetentionDays.HistoryMaximum);

    private static bool IsPreset(int days) => Array.Exists(Presets, preset => preset.Days == days);

    private static string FormatCustomLabel(int days) =>
        days == 1 ? "After 1 day (custom)" : $"After {days} days (custom)";
}
