using System.Globalization;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Presentation;

/// <summary>Fit warning classification for one EG-1 size tier on this PC.</summary>
public enum EgOneMemoryWarning
{
    None,
    Tight,
    WontFitNow,
}

/// <summary>Legend entries and combined percent caption rendered beneath an EG-1 memory budget bar.</summary>
public sealed record EgOneMemoryKey(
    string InUseLabel,
    string SizeLabel,
    string PercentText);

/// <summary>Pure presentation model for an EG-1 size card's live or fallback memory budget bar.</summary>
public sealed record EgOneMemoryPresentation(
    long PeakBytes,
    ulong TotalPhysicalBytes,
    ulong? SystemUsedBytes,
    EgOneServerActivity Activity,
    double UsedFraction,
    double PeakStart,
    double PeakFraction,
    int? ProjectedPercent,
    EgOneMemoryWarning Warning,
    string ValueText,
    string? LightestMark,
    EgOneMemoryKey? Key,
    string? WarningText,
    string AccessibilityValue);

/// <summary>Pure #4525 memory bar geometry, threshold warnings, legend copy, and PC header formatting.</summary>
public static class EgOneMemoryBarPresentation
{
    public const string WontFitNowMessage = "Won't fit right now. Close some apps or pick a smaller size.";
    public const string TightOnThisPcMessage = "Tight on this PC";

    public static EgOneMemoryPresentation Describe(
        string modelId,
        string modelTitle,
        ulong? totalPhysicalBytes,
        ulong? systemUsedBytes,
        EgOneServerActivity activity,
        bool isLightest,
        CultureInfo? culture = null) =>
        Describe(
            EgOneMemoryFacts.PeakMemoryBytes(modelId),
            modelTitle,
            totalPhysicalBytes,
            systemUsedBytes,
            activity,
            isLightest,
            culture);

    public static EgOneMemoryPresentation Describe(
        long peakMemoryBytes,
        string modelTitle,
        ulong? totalPhysicalBytes,
        ulong? systemUsedBytes,
        EgOneServerActivity activity,
        bool isLightest,
        CultureInfo? culture = null)
    {
        var effectiveCulture = culture ?? CultureInfo.InvariantCulture;
        var safeTotal = totalPhysicalBytes ?? 0UL;
        var safePeak = (ulong)Math.Max(0L, peakMemoryBytes);
        ulong? clampedUsed = safeTotal > 0 && systemUsedBytes is { } rawUsed
            ? Math.Min(rawUsed, safeTotal)
            : null;

        var usedFraction = clampedUsed is { } used && safeTotal > 0
            ? ShareOf(used, safeTotal)
            : 0d;

        double? includedFraction = clampedUsed is { } usedLive && safeTotal > 0 && activity != EgOneServerActivity.Inactive
            ? ShareOf(Math.Min(safePeak, usedLive), safeTotal)
            : null;

        var peakStart = includedFraction is { } inc
            ? Math.Max(0d, usedFraction - inc)
            : usedFraction;

        var peakFraction = includedFraction is { } included
            ? included
            : safeTotal > 0
                ? Math.Min(1d - peakStart, ShareOf(safePeak, safeTotal))
                : 0d;

        int? projectedPercent = null;
        if (clampedUsed is { } liveUsed && safeTotal > 0)
        {
            UInt128 projectedBytes = activity == EgOneServerActivity.Inactive
                ? (UInt128)liveUsed + safePeak
                : liveUsed;
            projectedPercent = (int)Math.Round((double)projectedBytes / safeTotal * 100d, MidpointRounding.AwayFromZero);
        }

        var warning = ComputeWarning(safePeak, safeTotal, clampedUsed, activity);
        var needText = FormatGiB(safePeak, effectiveCulture);
        var totalWholeGb = safeTotal > 0
            ? Math.Max(1, (int)Math.Round(safeTotal / (double)EgOneMemoryFacts.BytesPerGiB, MidpointRounding.AwayFromZero))
            : 0;

        var valueText = safeTotal > 0
            ? string.Format(effectiveCulture, "{0} of {1} GB", needText, totalWholeGb)
            : string.Format(effectiveCulture, "~{0} GB memory", needText);

        var lightestMark = isLightest && warning == EgOneMemoryWarning.None
            ? "lightest"
            : null;

        EgOneMemoryKey? key = null;
        if (clampedUsed is not null && projectedPercent is { } pct)
        {
            key = activity switch
            {
                EgOneServerActivity.Ready => new EgOneMemoryKey(
                    InUseLabel: "Other apps",
                    SizeLabel: $"{modelTitle}, running",
                    PercentText: string.Format(effectiveCulture, "{0}% in use", pct)),
                EgOneServerActivity.Starting => new EgOneMemoryKey(
                    InUseLabel: "Other apps",
                    SizeLabel: $"{modelTitle}, starting",
                    PercentText: string.Format(effectiveCulture, "{0}% in use", pct)),
                _ => new EgOneMemoryKey(
                    InUseLabel: "In use now",
                    SizeLabel: $"{modelTitle} adds {needText} GB",
                    PercentText: string.Format(effectiveCulture, "{0}% with this size", pct)),
            };
        }

        var warningText = warning switch
        {
            EgOneMemoryWarning.WontFitNow => WontFitNowMessage,
            EgOneMemoryWarning.Tight => TightOnThisPcMessage,
            _ => null,
        };

        var accessibilityValue = BuildAccessibilityValue(
            safeTotal,
            totalWholeGb,
            clampedUsed,
            projectedPercent,
            needText,
            activity,
            warningText,
            effectiveCulture);

        return new EgOneMemoryPresentation(
            PeakBytes: peakMemoryBytes,
            TotalPhysicalBytes: safeTotal,
            SystemUsedBytes: clampedUsed,
            Activity: activity,
            UsedFraction: usedFraction,
            PeakStart: peakStart,
            PeakFraction: peakFraction,
            ProjectedPercent: projectedPercent,
            Warning: warning,
            ValueText: valueText,
            LightestMark: lightestMark,
            Key: key,
            WarningText: warningText,
            AccessibilityValue: accessibilityValue);
    }

    public static string FormatPcMemoryHeading(
        double totalRamGb,
        GpuAccelerationKind gpuKind,
        double? dedicatedVramGb,
        int physicalCoreCount,
        SystemMemoryReading? liveReading,
        CultureInfo? culture = null)
    {
        if (liveReading is not { TotalPhysicalBytes: > 0 } live)
        {
            return HardwareSummaryFormatting.FormatSummary(totalRamGb, gpuKind, dedicatedVramGb, physicalCoreCount);
        }

        var effectiveCulture = culture ?? CultureInfo.InvariantCulture;
        var totalWhole = Math.Max(
            1,
            (int)Math.Round(live.TotalPhysicalBytes / (double)EgOneMemoryFacts.BytesPerGiB, MidpointRounding.AwayFromZero));
        var clampedUsed = Math.Min(live.UsedBytes, live.TotalPhysicalBytes);
        var usedText = FormatGiB(clampedUsed, effectiveCulture);
        var percent = (int)Math.Round(
            (double)clampedUsed / live.TotalPhysicalBytes * 100d,
            MidpointRounding.AwayFromZero);
        var suffix = HardwareSummaryFormatting.FormatGpuOrCpuSuffix(gpuKind, dedicatedVramGb, physicalCoreCount);
        return string.Format(
            effectiveCulture,
            "This PC: {0} GB RAM \u00B7 {1} GB in use \u00B7 {2}%{3}",
            totalWhole,
            usedText,
            percent,
            suffix);
    }

    public static string FormatGiB(ulong bytes, CultureInfo? culture = null)
    {
        var effectiveCulture = culture ?? CultureInfo.InvariantCulture;
        var gib = bytes / (double)EgOneMemoryFacts.BytesPerGiB;
        return gib.ToString("F1", effectiveCulture);
    }

    private static EgOneMemoryWarning ComputeWarning(
        ulong safePeak,
        ulong safeTotal,
        ulong? clampedUsed,
        EgOneServerActivity activity)
    {
        if (activity != EgOneServerActivity.Inactive || safeTotal == 0)
        {
            return EgOneMemoryWarning.None;
        }

        if (clampedUsed is { } used)
        {
            return ((UInt128)used + safePeak) * 10 > (UInt128)safeTotal * 9
                ? EgOneMemoryWarning.WontFitNow
                : EgOneMemoryWarning.None;
        }

        return (UInt128)safePeak * 2 > (UInt128)safeTotal
            ? EgOneMemoryWarning.Tight
            : EgOneMemoryWarning.None;
    }

    private static string BuildAccessibilityValue(
        ulong safeTotal,
        int totalWholeGb,
        ulong? clampedUsed,
        int? projectedPercent,
        string needText,
        EgOneServerActivity activity,
        string? warningText,
        CultureInfo culture)
    {
        string baseText;
        if (safeTotal == 0)
        {
            baseText = $"Needs about {needText} GB.";
        }
        else if (clampedUsed is { } used && projectedPercent is { } pct)
        {
            var usedText = FormatGiB(used, culture);
            var endPhrase = activity == EgOneServerActivity.Inactive
                ? string.Format(culture, "{0}% with this size.", pct)
                : string.Format(culture, "{0}% in use.", pct);
            baseText = string.Format(
                culture,
                "Needs about {0} GB. PC is using {1} GB of {2} GB now. {3}",
                needText,
                usedText,
                totalWholeGb,
                endPhrase);
        }
        else
        {
            baseText = string.Format(
                culture,
                "Needs about {0} GB of {1} GB.",
                needText,
                totalWholeGb);
        }

        return string.IsNullOrWhiteSpace(warningText)
            ? baseText
            : $"{baseText} {warningText}";
    }

    private static double ShareOf(ulong part, ulong whole) =>
        whole == 0 ? 0d : Math.Clamp((double)part / whole, 0d, 1d);
}
