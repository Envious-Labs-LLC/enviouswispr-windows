using System.Globalization;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>Snapshot of the single active inline audio playback across all History cards.</summary>
public sealed record HistoryPlaybackSnapshot(
    Guid? ActiveEntryId,
    bool IsPlaying,
    long PositionMs,
    long DurationMs);

/// <summary>Action produced when toggling Play/Pause for a History dictation card.</summary>
public enum HistoryPlaybackToggleOutcome
{
    None,
    StartNew,
    Resume,
    Pause,
}

/// <summary>
/// Tracks single-active playback state across History cards so starting one card stops any previous card.
/// </summary>
public sealed class SingleActivePlaybackTracker
{
    public Guid? ActiveEntryId { get; private set; }

    public bool IsPlaying { get; private set; }

    public long PositionMs { get; private set; }

    public long DurationMs { get; private set; }

    public HistoryPlaybackSnapshot Snapshot =>
        new(ActiveEntryId, IsPlaying, PositionMs, DurationMs);

    public HistoryPlaybackToggleOutcome Toggle(Guid entryId, long durationMs)
    {
        if (entryId == Guid.Empty)
        {
            return HistoryPlaybackToggleOutcome.None;
        }

        var safeDuration = Math.Max(0L, durationMs);
        if (ActiveEntryId == entryId)
        {
            if (IsPlaying)
            {
                IsPlaying = false;
                return HistoryPlaybackToggleOutcome.Pause;
            }

            if (safeDuration > 0 && PositionMs >= safeDuration)
            {
                PositionMs = 0;
            }

            if (safeDuration > 0)
            {
                DurationMs = safeDuration;
            }

            IsPlaying = true;
            return HistoryPlaybackToggleOutcome.Resume;
        }

        ActiveEntryId = entryId;
        DurationMs = safeDuration;
        PositionMs = 0;
        IsPlaying = true;
        return HistoryPlaybackToggleOutcome.StartNew;
    }

    public long Seek(Guid entryId, double fraction, long fallbackDurationMs)
    {
        if (entryId == Guid.Empty)
        {
            return 0;
        }

        var clamped = Math.Clamp(fraction, 0.0, 1.0);
        if (ActiveEntryId != entryId)
        {
            ActiveEntryId = entryId;
            IsPlaying = false;
            DurationMs = Math.Max(0L, fallbackDurationMs);
            PositionMs = 0;
        }
        else if (DurationMs <= 0 && fallbackDurationMs > 0)
        {
            DurationMs = fallbackDurationMs;
        }

        var targetMs = (long)Math.Round(DurationMs * clamped);
        PositionMs = Math.Clamp(targetMs, 0L, DurationMs);
        return PositionMs;
    }

    public void UpdateProgress(Guid entryId, long positionMs, long durationMs)
    {
        if (entryId == Guid.Empty || ActiveEntryId != entryId)
        {
            return;
        }

        if (durationMs > 0)
        {
            DurationMs = durationMs;
        }

        PositionMs = Math.Clamp(positionMs, 0L, Math.Max(DurationMs, Math.Max(0L, positionMs)));
    }

    public void MarkCompleted(Guid entryId)
    {
        if (ActiveEntryId != entryId)
        {
            return;
        }

        IsPlaying = false;
        PositionMs = 0;
    }

    public void Stop(Guid? onlyEntryId = null)
    {
        if (onlyEntryId is { } target && ActiveEntryId != target)
        {
            return;
        }

        ActiveEntryId = null;
        IsPlaying = false;
        PositionMs = 0;
        DurationMs = 0;
    }
}

/// <summary>Readiness snapshot tying History status badge and card Re-polish button together.</summary>
public sealed record HistoryPolishReadiness(
    PolishProvider Provider,
    string StatusText,
    bool IsEnabled,
    bool IsReady)
{
    public bool CanRepolishEntry(bool isGlobalBusy, bool isTemporary) =>
        IsEnabled && IsReady && !isGlobalBusy && !isTemporary;
}

/// <summary>Pure formatting, waveform bar shaping, and polish readiness helpers for History playback & cards.</summary>
public static class HistoryPlaybackPresentation
{
    public const int DefaultWaveformBarCount = 36;

    private static readonly float[] FallbackWavePattern =
    [
        0.18f, 0.28f, 0.42f, 0.30f, 0.56f, 0.72f, 0.44f, 0.34f, 0.62f,
        0.78f, 0.50f, 0.36f, 0.64f, 0.48f, 0.82f, 0.58f, 0.38f, 0.54f,
        0.70f, 0.42f, 0.32f, 0.60f, 0.74f, 0.46f, 0.34f, 0.52f, 0.66f,
        0.40f, 0.28f, 0.48f, 0.60f, 0.36f, 0.26f, 0.40f, 0.28f, 0.20f,
    ];

    public static string FormatClock(long milliseconds)
    {
        var safeMs = Math.Max(0L, milliseconds);
        var span = TimeSpan.FromMilliseconds(safeMs);
        if (span.TotalHours >= 1.0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)span.TotalMinutes}:{span.Seconds:00}");
    }

    public static string FormatClockDisplay(long positionMs, long durationMs, bool isActive)
    {
        var total = FormatClock(durationMs);
        if (!isActive && positionMs <= 0)
        {
            return total;
        }

        var current = FormatClock(Math.Clamp(positionMs, 0L, Math.Max(0L, durationMs)));
        return $"{current} / {total}";
    }

    public static int ComputePlayedBarCount(long positionMs, long durationMs, int barCount)
    {
        if (barCount <= 0 || durationMs <= 0 || positionMs <= 0)
        {
            return 0;
        }

        if (positionMs >= durationMs)
        {
            return barCount;
        }

        var fraction = Math.Clamp((double)positionMs / durationMs, 0.0, 1.0);
        return (int)Math.Floor(fraction * barCount);
    }

    public static IReadOnlyList<float> NormalizeOrFallbackPeaks(
        IReadOnlyList<float>? rawPeaks,
        int barCount = DefaultWaveformBarCount)
    {
        if (barCount <= 0)
        {
            return [];
        }

        if (rawPeaks is null || rawPeaks.Count == 0)
        {
            return BuildDefaultBars(barCount);
        }

        var maxPeak = 0f;
        for (var i = 0; i < rawPeaks.Count; i++)
        {
            var clamped = Math.Clamp(rawPeaks[i], 0f, 1f);
            if (clamped > maxPeak)
            {
                maxPeak = clamped;
            }
        }

        if (maxPeak <= 0.001f)
        {
            var silent = new float[barCount];
            Array.Fill(silent, 0.14f);
            return silent;
        }

        var shaped = new float[barCount];
        for (var i = 0; i < barCount; i++)
        {
            var sourceIdx = rawPeaks.Count == barCount
                ? i
                : Math.Min(rawPeaks.Count - 1, (int)((long)i * rawPeaks.Count / barCount));
            var normalized = Math.Clamp(rawPeaks[sourceIdx] / maxPeak, 0f, 1f);
            // Square-root compression keeps conversational amplitude legible beside louder syllables.
            shaped[i] = Math.Max(0.14f, MathF.Sqrt(normalized));
        }

        return shaped;
    }

    public static double[] ShapeBarHeights(
        IReadOnlyList<float>? peaks,
        int barCount = DefaultWaveformBarCount,
        double minHeight = 4.0,
        double maxHeight = 20.0)
    {
        var normalized = NormalizeOrFallbackPeaks(peaks, barCount);
        var range = Math.Max(0.0, maxHeight - minHeight);
        var heights = new double[normalized.Count];
        for (var i = 0; i < normalized.Count; i++)
        {
            heights[i] = minHeight + (Math.Clamp(normalized[i], 0f, 1f) * range);
        }

        return heights;
    }

    public static HistoryPolishReadiness DescribePolishReadiness(
        PolishProvider provider,
        string? modelId,
        bool isEgOneReady,
        bool isOllamaEndpointValid,
        bool hasCloudCredential,
        bool isCloudKeyRejected)
    {
        var hasModel = !string.IsNullOrWhiteSpace(modelId);
        return provider switch
        {
            PolishProvider.None => new(
                PolishProvider.None,
                "Off",
                IsEnabled: false,
                IsReady: false),
            PolishProvider.EgOne => new(
                PolishProvider.EgOne,
                isEgOneReady ? "EG-1" : "EG-1 · Setup needed",
                IsEnabled: true,
                IsReady: isEgOneReady),
            PolishProvider.Ollama => new(
                PolishProvider.Ollama,
                !isOllamaEndpointValid ? "Ollama · Check endpoint"
                    : !hasModel ? "Ollama · Pick a model"
                    : "Ollama",
                IsEnabled: true,
                IsReady: isOllamaEndpointValid && hasModel),
            PolishProvider.OpenAI => DescribeCloudReadiness(
                provider, "OpenAI", hasCloudCredential, isCloudKeyRejected, hasModel),
            PolishProvider.Anthropic => DescribeCloudReadiness(
                provider, "Anthropic", hasCloudCredential, isCloudKeyRejected, hasModel),
            PolishProvider.Gemini => DescribeCloudReadiness(
                provider, "Gemini", hasCloudCredential, isCloudKeyRejected, hasModel),
            _ => new(provider, "Off", IsEnabled: false, IsReady: false),
        };
    }

    private static HistoryPolishReadiness DescribeCloudReadiness(
        PolishProvider provider,
        string displayName,
        bool hasCloudCredential,
        bool isCloudKeyRejected,
        bool hasModel)
    {
        if (!hasCloudCredential)
        {
            return new(provider, $"{displayName} · No key", IsEnabled: true, IsReady: false);
        }

        if (isCloudKeyRejected)
        {
            return new(provider, $"{displayName} · Key rejected", IsEnabled: true, IsReady: false);
        }

        if (!hasModel)
        {
            return new(provider, $"{displayName} · Pick a model", IsEnabled: true, IsReady: false);
        }

        return new(provider, displayName, IsEnabled: true, IsReady: true);
    }

    private static float[] BuildDefaultBars(int barCount)
    {
        var bars = new float[barCount];
        for (var i = 0; i < barCount; i++)
        {
            bars[i] = FallbackWavePattern[i % FallbackWavePattern.Length];
        }

        return bars;
    }
}
