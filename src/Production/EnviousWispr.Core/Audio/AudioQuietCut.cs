namespace EnviousWispr.Core.Audio;

/// <summary>
/// Cuts continuous audio near a duration limit at its quietest 100 ms valley so words stay whole.
/// </summary>
public static class AudioQuietCut
{
    public const int DefaultSampleRate = 16_000;
    public const int MaximumPieceSeconds = 60;
    public const int SearchWindowSeconds = 10;

    /// <summary>How many samples of <paramref name="audio"/> the next piece takes.</summary>
    public static int NextPieceLength(ReadOnlySpan<float> audio, bool atEnd, int sampleRate = DefaultSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        var maximumPieceSamples = checked(sampleRate * MaximumPieceSeconds);
        if (audio.Length <= maximumPieceSamples)
        {
            return atEnd ? audio.Length : 0;
        }

        var (quietestCenter, _) = FindQuietestFrame(audio, sampleRate, maximumPieceSamples);
        return quietestCenter > 0 ? quietestCenter : maximumPieceSamples;
    }

    /// <summary>
    /// Finds a verified quiet 100 ms valley inside the trailing search window before <see cref="MaximumPieceSeconds"/>.
    /// Returns false when continuous loud speech spans the entire window so streaming never cuts mid-word.
    /// </summary>
    public static bool TryFindQuietValley(
        ReadOnlySpan<float> audio,
        int sampleRate,
        out int cutLength,
        double maximumFrameRms = 0.0015)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        cutLength = 0;
        var maximumPieceSamples = checked(sampleRate * MaximumPieceSeconds);
        if (audio.Length <= maximumPieceSamples)
        {
            return false;
        }

        var (quietestCenter, quietestRms) = FindQuietestFrame(audio, sampleRate, maximumPieceSamples);
        if (quietestCenter <= 0 || quietestRms > maximumFrameRms)
        {
            return false;
        }

        cutLength = quietestCenter;
        return true;
    }

    private static (int CenterSample, double Rms) FindQuietestFrame(
        ReadOnlySpan<float> audio,
        int sampleRate,
        int maximumPieceSamples)
    {
        var searchSamples = Math.Min(maximumPieceSamples, sampleRate * SearchWindowSeconds);
        var frameSamples = Math.Max(1, sampleRate / 10);
        var searchStart = maximumPieceSamples - searchSamples;
        var quietest = 0;
        var quietestRms = double.MaxValue;
        for (var frameStart = searchStart; frameStart + frameSamples <= maximumPieceSamples; frameStart += frameSamples)
        {
            var energy = 0.0;
            foreach (var sample in audio.Slice(frameStart, frameSamples))
            {
                energy += sample * (double)sample;
            }

            var rms = Math.Sqrt(energy / frameSamples);
            // The LAST of equally quiet frames, so a silence is cut at its far end and the piece keeps its words whole.
            if (rms <= quietestRms)
            {
                quietestRms = rms;
                quietest = frameStart + (frameSamples / 2);
            }
        }

        return (quietest, quietestRms);
    }

    /// <summary>A piece with nothing in it: engines invent words into silence, so it can be skipped.</summary>
    public static bool IsSilent(ReadOnlySpan<float> piece) => IsBelowPeak(piece, 0.005f);

    /// <summary>
    /// Confirmed silence strictly below Parakeet's 0.002 RMS voiced floor so quiet speech around 0.003 is never dropped.
    /// </summary>
    public static bool IsBelowVoicedFloor(ReadOnlySpan<float> piece) => IsBelowPeak(piece, 0.0015f);

    /// <summary>Minimum quiet trailing padding retained after voiced speech so unvoiced consonants (#950) are not clipped.</summary>
    public const int QuietTailPaddingMs = 200;

    /// <summary>Minimum duration (ms) of a dropped tail candidate before #950 tail preservation applies.</summary>
    public const int TailPreserveFloorMs = 400;

    /// <summary>Maximum duration (ms) of a dropped tail candidate eligible for #950 tail preservation.</summary>
    public const int TailPreserveMaxRecoverMs = 8_000;

    /// <summary>Minimum fraction of 40 ms voiced frames required for #950 tail preservation.</summary>
    public const double TailPreserveVoicedFractionFloor = 0.50;

    /// <summary>Length of one frame (ms) used to measure sustained voicing inside a tail candidate.</summary>
    public const int TailPreserveFrameMs = 40;

    /// <summary>Minimum leading degraded/dead stretch (s) before #1434 degraded-lead salvage activates.</summary>
    public const double MinimumDegradedLeadSeconds = 0.4;

    /// <summary>Minimum audio (s) that must remain after trimming a degraded lead.</summary>
    public const double MinimumSalvageRemainingSeconds = 0.7;

    /// <summary>Minimum overall speech peak required to consider a recording valid after a degraded lead.</summary>
    public const float MinimumDegradedLeadReferencePeak = 0.005f;

    /// <summary>
    /// Extends a candidate cut point past voiced speech by <see cref="QuietTailPaddingMs"/> (#950) so
    /// soft trailing consonants are not hard-clipped.
    /// </summary>
    public static int ComputeTrailingConsonantPreserveEnd(
        int audioLength,
        int voicedEndSample,
        int sampleRate = DefaultSampleRate,
        int marginMs = QuietTailPaddingMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(audioLength);
        ArgumentOutOfRangeException.ThrowIfNegative(voicedEndSample);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(marginMs);

        var clampedVoicedEnd = Math.Min(audioLength, voicedEndSample);
        var marginSamples = (int)((long)sampleRate * marginMs / 1000);
        return (int)Math.Min(audioLength, (long)clampedVoicedEnd + marginSamples);
    }

    /// <summary>
    /// Evaluates whether a candidate tail carries sustained speech energy (#950) and should be preserved for ASR.
    /// </summary>
    public static TailPreserveDecision EvaluateTailPreserve(
        ReadOnlySpan<float> tail,
        int sampleRate = DefaultSampleRate,
        int floorMs = TailPreserveFloorMs,
        int maxRecoverMs = TailPreserveMaxRecoverMs,
        double voicedFractionFloor = TailPreserveVoicedFractionFloor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        if (tail.IsEmpty)
        {
            return TailPreserveDecision.Refuse("no_tail");
        }

        var tailMs = (int)(tail.Length * 1000L / sampleRate);
        if (tailMs < floorMs)
        {
            return TailPreserveDecision.Refuse("too_short");
        }

        if (tailMs > maxRecoverMs)
        {
            return TailPreserveDecision.Refuse("too_long");
        }

        var frameSamples = Math.Max(1, sampleRate * TailPreserveFrameMs / 1000);
        var totalFrames = Math.Max(1, (tail.Length + frameSamples - 1) / frameSamples);
        var voicedFrames = 0;
        for (var offset = 0; offset < tail.Length; offset += frameSamples)
        {
            var frame = tail.Slice(offset, Math.Min(frameSamples, tail.Length - offset));
            if (!IsBelowVoicedFloor(frame))
            {
                voicedFrames++;
            }
        }

        var voicedFraction = voicedFrames / (double)totalFrames;
        return voicedFraction >= voicedFractionFloor
            ? TailPreserveDecision.Preserve(voicedFraction)
            : TailPreserveDecision.Refuse("low_voiced_fraction", voicedFraction);
    }

    /// <summary>
    /// Computes up to 3 deduped start-trim offsets for audio whose leading stretch is dead/degraded
    /// (&gt;= 0.4 s below peak/16) followed by valid speech (&gt;= 0.7 s remaining, #1434).
    /// </summary>
    public static IReadOnlyList<int> FindDegradedLeadTrimCandidates(
        ReadOnlySpan<float> samples,
        int sampleRate = DefaultSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        var minDeadSamples = (int)(MinimumDegradedLeadSeconds * sampleRate);
        var minRemainingSamples = (int)(MinimumSalvageRemainingSeconds * sampleRate);
        if (samples.Length <= minDeadSamples + minRemainingSamples)
        {
            return [];
        }

        var overallPeak = 0f;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > overallPeak)
            {
                overallPeak = magnitude;
            }
        }

        if (overallPeak < MinimumDegradedLeadReferencePeak)
        {
            return [];
        }

        var leadCeiling = overallPeak / 16f;
        var deadEnd = samples.Length;
        for (var index = 0; index < samples.Length; index++)
        {
            if (Math.Abs(samples[index]) >= leadCeiling)
            {
                deadEnd = index;
                break;
            }
        }

        if (deadEnd < minDeadSamples)
        {
            return [];
        }

        var quarterCeiling = overallPeak / 4f;
        var higherOnset = deadEnd;
        for (var index = deadEnd; index < samples.Length; index++)
        {
            if (Math.Abs(samples[index]) >= quarterCeiling)
            {
                higherOnset = index;
                break;
            }
        }

        var step = sampleRate / 4; // 250 ms ladder step
        int[] rawCandidates = [deadEnd, higherOnset, deadEnd + step];
        var results = new List<int>(3);
        foreach (var candidate in rawCandidates)
        {
            if (candidate <= 0 ||
                samples.Length - candidate < minRemainingSamples ||
                results.Contains(candidate))
            {
                continue;
            }

            results.Add(candidate);
            if (results.Count == 3)
            {
                break;
            }
        }

        return results;
    }

    private static bool IsBelowPeak(ReadOnlySpan<float> piece, float threshold)
    {
        var peak = 0f;
        foreach (var sample in piece)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return peak < threshold;
    }
}

/// <summary>Outcome of evaluating whether a dropped quiet tail actually carries speech (#950).</summary>
public readonly record struct TailPreserveDecision(
    bool ShouldPreserve,
    string Reason,
    double? VoicedFraction = null)
{
    public static TailPreserveDecision Preserve(double voicedFraction) =>
        new(ShouldPreserve: true, "preserve", voicedFraction);

    public static TailPreserveDecision Refuse(string reason, double? voicedFraction = null) =>
        new(ShouldPreserve: false, reason, voicedFraction);
}
