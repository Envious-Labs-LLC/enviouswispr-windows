using System.Text;
using EnviousWispr.Core.Dictation;

namespace EnviousWispr.ASR;

/// <summary>
/// Chunked Parakeet TDT decoding, overlap merging, seam-gap repair, and monotonic timing
/// assembly matching macOS FluidAudio (<c>ChunkProcessor</c>/<c>TdtDecoderV3</c>) and Android <c>TdtRecipe</c>.
/// </summary>
internal sealed class ParakeetTdtRecipe
{
    internal const int SampleRate = 16_000;
    internal const int FrameSamples = 1_280;
    internal const double SecondsPerFrame = 0.08;
    internal const int WindowSamples = 240_000;
    internal const int FramesPerWindow = WindowSamples / FrameSamples;
    internal const int OnePassSamples = 240_000;
    internal const int ChunkSamples = 238_080;
    internal const int OverlapSamples = 32_000;
    internal const int StrideSamples = ChunkSamples - OverlapSamples;
    internal const double OverlapSeconds = 2.0;
    internal const int MaxTokensPerWindow = 150;
    internal const int MaxSymbolsPerStep = 10;
    internal const int ConsecutiveBlankLimit = 5;
    internal static readonly int[] DurationBins = [0, 1, 2, 3, 4];
    internal const double MinGapSeconds = 1.5;
    internal const double MinGapSpeechSeconds = 0.5;
    internal const int MaxRepairs = 32;
    internal const int EdgeFrames = 6;
    internal const int RepairWindowSamples = ((WindowSamples - 160) / FrameSamples) * FrameSamples;
    internal const float SpeechRmsFloor = 0.0005f;
    internal const float SpeechRmsCeiling = 0.008f;

    private const char SentencePieceBoundary = '\u2581';
    private const int RmsBlockFrames = 64;

    private readonly ParakeetTranscriptionEngine.IParakeetBackend _backend;
    private readonly ParakeetVocabulary _vocabulary;
    private readonly int _maxSymbolsPerStep;
    private readonly bool[] _spliceSafe;
    private readonly bool[] _punctuationOrSymbol;
    private readonly int[] _caseVariant;

    internal readonly record struct DecodedToken(int Id, int Frame, int Duration);

    internal readonly record struct SinglePassRescueResult(
        IReadOnlyList<DecodedToken> Tokens,
        int StartSampleOffset);

    internal readonly record struct RecipeResult(
        IReadOnlyList<DecodedToken> Tokens,
        int WindowCount,
        int RepairCount,
        int StartSampleOffset);

    internal delegate SinglePassRescueResult SinglePassBlankRescue(
        float[] originalSamples,
        float[] primaryFeatures,
        long primaryFeatureLength,
        bool[]? allowedTokenMask,
        bool[]? blockedTokenMask,
        ParakeetTranscriptionEngine.IParakeetRunContext runContext,
        CancellationToken cancellationToken);

    public ParakeetTdtRecipe(
        ParakeetTranscriptionEngine.IParakeetBackend backend,
        ParakeetVocabulary vocabulary,
        int maxSymbolsPerStep = MaxSymbolsPerStep)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSymbolsPerStep, 1);

        _backend = backend;
        _vocabulary = vocabulary;
        _maxSymbolsPerStep = maxSymbolsPerStep;

        var tokens = vocabulary.Tokens;
        _spliceSafe = new bool[tokens.Length];
        _punctuationOrSymbol = new bool[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            var piece = tokens[i];
            if (string.IsNullOrEmpty(piece) || ParakeetVocabulary.IsControlToken(piece))
            {
                continue;
            }

            _punctuationOrSymbol[i] = IsPunctuationOrSymbol(piece);
            _spliceSafe[i] = IsBoundary(piece) || _punctuationOrSymbol[i];
        }

        _caseVariant = BuildCaseVariants(tokens);
    }

    internal RecipeResult Transcribe(
        float[] samples,
        bool[]? allowedTokenMask,
        bool[]? blockedTokenMask,
        ParakeetTranscriptionEngine.IParakeetRunContext runContext,
        CancellationToken cancellationToken,
        SinglePassBlankRescue? singlePassBlankRescue = null,
        Func<float[], (bool HasVoicedSpeech, int TrimmedStartSample, int TrimmedLength)>? voicedTrimLocator = null,
        Action<string, double?>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(runContext);
        cancellationToken.ThrowIfCancellationRequested();

        var totalSamples = samples.Length;
        if (totalSamples == 0)
        {
            return new RecipeResult([], 0, 0, 0);
        }

        if (totalSamples <= OnePassSamples)
        {
            var aligned = AlignToFrameBoundary(samples);
            var (features, featureLength) = _backend.Preprocess(
                aligned,
                aligned.Length,
                runContext,
                cancellationToken);
            onProgress?.Invoke("PreprocessorComplete", 0.15);
            var (encodedRaw, encodedLength) = _backend.Encode(
                features,
                featureLength,
                runContext,
                cancellationToken);
            onProgress?.Invoke("EncoderComplete", 0.45);
            IReadOnlyList<DecodedToken> singlePass = DecodeEncoded(
                encodedRaw,
                encodedLength,
                realSampleCount: aligned.Length,
                contextSamples: 0,
                offsetSample: 0,
                isLast: true,
                padToWindow: false,
                allowedTokenMask,
                blockedTokenMask,
                runContext,
                cancellationToken,
                onDecodeProgress: fraction => onProgress?.Invoke("DecodeStep", 0.45 + (fraction * 0.55)));

            var startSampleOffset = 0;
            if (singlePass.Count == 0 && singlePassBlankRescue is not null)
            {
                var rescued = singlePassBlankRescue(
                    samples,
                    features,
                    featureLength,
                    allowedTokenMask,
                    blockedTokenMask,
                    runContext,
                    cancellationToken);
                if (rescued.Tokens is { Count: > 0 })
                {
                    singlePass = rescued.Tokens;
                    startSampleOffset = rescued.StartSampleOffset;
                }
            }

            return new RecipeResult(
                EnforceMonotonic(singlePass),
                WindowCount: 1,
                RepairCount: 0,
                startSampleOffset);
        }

        var speechEnd = FindSpeechEnd(samples, cancellationToken);
        var effectiveTotal = Math.Max(FrameSamples, Math.Min(totalSamples, speechEnd));
        var outputs = new List<List<DecodedToken>>();
        var windowRanges = new List<(int StartSample, int EndSample)>();
        var start = 0;
        var windowIndex = 0;
        var allowFullPrecisionFallback = singlePassBlankRescue is not null;
        while (start < effectiveTotal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var warmup = LastChunkWarmup(start, effectiveTotal, speechEnd);
            var visible = Math.Max(FrameSamples, ChunkSamples - warmup);
            var isLast = start + visible >= effectiveTotal;
            var end = isLast ? effectiveTotal : start + visible;
            var audioEnd = isLast ? Math.Min(end, speechEnd) : end;
            if (end <= start || audioEnd <= start)
            {
                break;
            }

            var context = warmup > 0 ? 0 : (windowIndex > 0 ? FrameSamples : 0);
            var contextStart = start - Math.Max(warmup, context);
            var segment = samples.AsSpan(contextStart, audioEnd - contextStart);
            windowRanges.Add((start, audioEnd));
            onProgress?.Invoke("DecodeWindow", Math.Clamp((double)start / Math.Max(1, effectiveTotal), 0.0, 0.99));
            if (warmup > 0)
            {
                outputs.Add(DecodeWindow(
                    segment,
                    contextSamples: 0,
                    offsetSample: contextStart,
                    isLast: isLast,
                    padToWindow: true,
                    allowedTokenMask,
                    blockedTokenMask,
                    runContext,
                    cancellationToken,
                    emitAfterFrame: start / FrameSamples,
                    startFrame: 0,
                    allowFullPrecisionFallback: allowFullPrecisionFallback));
            }
            else
            {
                outputs.Add(DecodeWindow(
                    segment,
                    contextSamples: context,
                    offsetSample: contextStart,
                    isLast: isLast,
                    padToWindow: true,
                    allowedTokenMask,
                    blockedTokenMask,
                    runContext,
                    cancellationToken,
                    allowFullPrecisionFallback: allowFullPrecisionFallback));
            }

            windowIndex++;
            if (isLast)
            {
                break;
            }

            start += StrideSamples;
        }

        var merged = outputs.Count > 0 ? outputs[0] : [];
        var seamRanges = new List<(int StartFrame, int EndFrame)>(Math.Max(0, outputs.Count - 1));
        for (var i = 1; i < outputs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seamStart = Math.Min(windowRanges[i].StartSample / FrameSamples, CeilDiv(windowRanges[i - 1].EndSample, FrameSamples)) - EdgeFrames;
            var seamEnd = Math.Max(windowRanges[i].StartSample / FrameSamples, CeilDiv(windowRanges[i - 1].EndSample, FrameSamples)) + EdgeFrames;
            seamRanges.Add((Math.Max(0, seamStart), seamEnd));
            merged = Merge(merged, outputs[i]);
        }

        merged = EnforceMonotonic(merged);
        if (outputs.Count > 1)
        {
            merged = CollapseSeamDuplicates(merged, seamRanges);
        }

        var repairs = 0;
        if (outputs.Count > 1 && merged.Count > 1)
        {
            var repaired = RepairSeamGaps(
                samples,
                merged,
                allowedTokenMask,
                blockedTokenMask,
                runContext,
                cancellationToken);
            repairs = Math.Max(0, repaired.Count - merged.Count);
            merged = repaired;
        }

        if (merged.Count == 0 && voicedTrimLocator is not null)
        {
            try
            {
                var (hasVoicedSpeech, trimmedStart, trimmedLength) = voicedTrimLocator(samples);
                if (hasVoicedSpeech &&
                    trimmedLength >= ParakeetTranscriptionEngine.MinimumPreprocessorInputSamples &&
                    trimmedLength < samples.Length)
                {
                    var trimmedSamples = samples.AsSpan(trimmedStart, trimmedLength).ToArray();
                    var retried = Transcribe(
                        trimmedSamples,
                        allowedTokenMask,
                        blockedTokenMask,
                        runContext,
                        cancellationToken,
                        singlePassBlankRescue: null,
                        voicedTrimLocator: null);
                    if (retried.Tokens.Count > 0)
                    {
                        return new RecipeResult(
                            retried.Tokens,
                            retried.WindowCount,
                            retried.RepairCount,
                            trimmedStart + retried.StartSampleOffset);
                    }
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception exception) when (
                exception is not (OperationCanceledException or StackOverflowException or OutOfMemoryException))
            {
                // Preserve empty result when trimmed chunked retry fails.
            }
        }

        return new RecipeResult(merged, outputs.Count, repairs, StartSampleOffset: 0);
    }

    internal List<DecodedToken> DecodeWindow(
        ReadOnlySpan<float> samples,
        int contextSamples,
        int offsetSample,
        bool isLast,
        bool padToWindow,
        bool[]? allowedTokenMask,
        bool[]? blockedTokenMask,
        ParakeetTranscriptionEngine.IParakeetRunContext runContext,
        CancellationToken cancellationToken,
        int? emitAfterFrame = null,
        int? startFrame = null,
        bool allowFullPrecisionFallback = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var realSampleCount = samples.Length;
        if (realSampleCount == 0)
        {
            return [];
        }

        float[] inputSamples;
        if (padToWindow && realSampleCount < WindowSamples)
        {
            inputSamples = new float[WindowSamples];
            samples.CopyTo(inputSamples);
        }
        else
        {
            inputSamples = samples.ToArray();
        }

        var (features, featureLength) = _backend.Preprocess(
            inputSamples,
            realSampleCount,
            runContext,
            cancellationToken);
        var (encodedRaw, encodedLength) = _backend.Encode(
            features,
            featureLength,
            runContext,
            cancellationToken);
        var decoded = DecodeEncoded(
            encodedRaw,
            encodedLength,
            realSampleCount,
            contextSamples,
            offsetSample,
            isLast,
            padToWindow,
            allowedTokenMask,
            blockedTokenMask,
            runContext,
            cancellationToken,
            emitAfterFrame,
            startFrame);
        if (decoded.Count == 0 && allowFullPrecisionFallback)
        {
            try
            {
                if (_backend.TryEncodeWithFullPrecisionFallback(
                        features,
                        featureLength,
                        runContext,
                        cancellationToken,
                        out var fp32EncodedRaw,
                        out var fp32EncodedLength))
                {
                    var fp32Decoded = DecodeEncoded(
                        fp32EncodedRaw,
                        fp32EncodedLength,
                        realSampleCount,
                        contextSamples,
                        offsetSample,
                        isLast,
                        padToWindow,
                        allowedTokenMask,
                        blockedTokenMask,
                        runContext,
                        cancellationToken,
                        emitAfterFrame,
                        startFrame);
                    if (fp32Decoded.Count > 0)
                    {
                        return fp32Decoded;
                    }
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception exception) when (
                exception is not (OperationCanceledException or StackOverflowException or OutOfMemoryException))
            {
                // Rescue is best-effort: keep original empty chunk result when optional FP32 fallback fails.
            }
        }

        return decoded;
    }

    internal List<DecodedToken> DecodeEncoded(
        float[] encodedChannelFirst,
        int encodedLength,
        int realSampleCount,
        int contextSamples,
        int offsetSample,
        bool isLast,
        bool padToWindow,
        bool[]? allowedTokenMask,
        bool[]? blockedTokenMask,
        ParakeetTranscriptionEngine.IParakeetRunContext runContext,
        CancellationToken cancellationToken,
        int? emitAfterFrame = null,
        int? startFrame = null,
        Action<double>? onDecodeProgress = null)
    {
        var encoderDimension = _backend.EncoderDimension;
        if (encodedLength <= 0 ||
            encodedChannelFirst.Length < (long)encoderDimension * encodedLength ||
            encodedChannelFirst.Length % encoderDimension != 0)
        {
            return [];
        }

        var actualFrames = CeilDiv(realSampleCount, FrameSamples);
        var frames = Math.Min(encodedLength, actualFrames);
        if (padToWindow ? frames <= 1 : frames <= 0)
        {
            return [];
        }

        var encoded = TransposeEncoderOutput(encodedChannelFirst, encoderDimension);
        var stateLength = _backend.StateLeadingDimension * _backend.StateDimension;
        var state1 = new float[stateLength];
        var state2 = new float[stateLength];
        var encoderFrame = new float[encoderDimension];
        var blankIndex = _vocabulary.BlankIndex;
        var target = new[] { blankIndex };
        var targetLength = new[] { 1 };

        var offsetFrame = offsetSample / FrameSamples;
        var decoded = new List<DecodedToken>();
        int? lastHistoryToken = null;
        var t = startFrame ?? (contextSamples / FrameSamples);
        var lastEmit = -1;
        var atFrame = 0;
        var count = 0;
        var tokenLimit = TokenLimit(frames);
        var lastReportedFrame = t - 16;

        while (t < frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onDecodeProgress is not null && t - lastReportedFrame >= 16)
            {
                lastReportedFrame = t;
                onDecodeProgress((double)t / frames);
            }

            Array.Copy(encoded, t * encoderDimension, encoderFrame, 0, encoderDimension);
            target[0] = lastHistoryToken ?? blankIndex;

            var step = _backend.DecodeStep(
                encoderFrame,
                target,
                targetLength,
                state1,
                state2,
                _vocabulary.Size,
                blankIndex,
                allowedTokenMask,
                blockedTokenMask,
                runContext,
                cancellationToken);

            var token = step.Token;
            var duration = MapDuration(step.Step);
            if (token != blankIndex && duration == 0 && t == lastEmit && atFrame >= 1)
            {
                duration = 1;
            }

            if (token == blankIndex && duration == 0)
            {
                duration = 1;
            }

            var current = t;
            t += duration;
            if (t >= frames)
            {
                if (isLast && token != blankIndex && current < frames && ++count <= tokenLimit)
                {
                    var globalFrame = current + offsetFrame;
                    if (emitAfterFrame is null || globalFrame >= emitAfterFrame.Value)
                    {
                        decoded.Add(new DecodedToken(token, globalFrame, duration));
                    }

                    lastHistoryToken = token;
                }

                break;
            }

            if (token == blankIndex)
            {
                continue;
            }

            if (++count > tokenLimit)
            {
                break;
            }

            var global = current + offsetFrame;
            if (emitAfterFrame is null || global >= emitAfterFrame.Value)
            {
                decoded.Add(new DecodedToken(token, global, duration));
            }

            lastHistoryToken = token;
            atFrame = current == lastEmit ? atFrame + 1 : 1;
            lastEmit = current;
            if (atFrame >= _maxSymbolsPerStep)
            {
                t = current + 1;
                atFrame = 0;
            }
        }

        if (isLast)
        {
            var blanks = 0;
            var index = Math.Min(t, frames - 1);
            var rotation = new[] { index, frames - 1, Math.Max(0, frames - 2) };
            var scratchState1 = new float[stateLength];
            var scratchState2 = new float[stateLength];

            for (var k = 0; k < _maxSymbolsPerStep; k++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (blanks >= ConsecutiveBlankLimit)
                {
                    break;
                }

                var frameIndex = rotation[k % 3];
                Array.Copy(encoded, frameIndex * encoderDimension, encoderFrame, 0, encoderDimension);
                target[0] = lastHistoryToken ?? blankIndex;
                Array.Copy(state1, scratchState1, stateLength);
                Array.Copy(state2, scratchState2, stateLength);

                var step = _backend.DecodeStep(
                    encoderFrame,
                    target,
                    targetLength,
                    scratchState1,
                    scratchState2,
                    _vocabulary.Size,
                    blankIndex,
                    allowedTokenMask,
                    blockedTokenMask,
                    runContext,
                    cancellationToken);

                var duration = MapDuration(step.Step);
                if (step.Token == blankIndex || !IsPunctuationPiece(_vocabulary.Tokens[step.Token]))
                {
                    blanks++;
                }
                else
                {
                    blanks = 0;
                    var global = Math.Min(index, frames - 1) + offsetFrame;
                    if (emitAfterFrame is null || global >= emitAfterFrame.Value)
                    {
                        decoded.Add(new DecodedToken(step.Token, global, 1));
                    }

                    lastHistoryToken = step.Token;
                    Array.Copy(scratchState1, state1, stateLength);
                    Array.Copy(scratchState2, state2, stateLength);
                }

                index += Math.Max(1, duration);
                rotation[0] = Math.Min(index, frames - 1);
            }
        }

        return decoded;
    }

    internal List<DecodedToken> Merge(IReadOnlyList<DecodedToken> left, IReadOnlyList<DecodedToken> right)
    {
        if (left.Count == 0)
        {
            return [.. right];
        }

        if (right.Count == 0)
        {
            return [.. left];
        }

        var leftEnd = left[^1].Frame * SecondsPerFrame + SecondsPerFrame;
        var rightStart = right[0].Frame * SecondsPerFrame;
        if (leftEnd <= rightStart)
        {
            var combined = new List<DecodedToken>(left.Count + right.Count);
            combined.AddRange(left);
            combined.AddRange(right);
            return combined;
        }

        var overlapLeft = new List<int>();
        for (var idx = 0; idx < left.Count; idx++)
        {
            if (left[idx].Frame * SecondsPerFrame + SecondsPerFrame > rightStart - OverlapSeconds)
            {
                overlapLeft.Add(idx);
            }
        }

        var overlapRight = new List<int>();
        for (var idx = 0; idx < right.Count; idx++)
        {
            if (right[idx].Frame * SecondsPerFrame < leftEnd + OverlapSeconds)
            {
                overlapRight.Add(idx);
            }
        }

        if (overlapLeft.Count < 2 || overlapRight.Count < 2)
        {
            return MergeByMidpoint(left, right, leftEnd, rightStart);
        }

        bool Matches(int p, int q)
        {
            var a = left[overlapLeft[p]];
            var b = right[overlapRight[q]];
            return IdsMatch(a.Id, b.Id) && Math.Abs(a.Frame - b.Frame) * SecondsPerFrame < OverlapSeconds / 2.0;
        }

        var best = new List<(int LeftIndex, int RightIndex)>();
        for (var p = 0; p < overlapLeft.Count; p++)
        {
            for (var q = 0; q < overlapRight.Count; q++)
            {
                if (!Matches(p, q))
                {
                    continue;
                }

                var current = new List<(int LeftIndex, int RightIndex)>();
                var k = p;
                var l = q;
                while (k < overlapLeft.Count && l < overlapRight.Count && Matches(k, l))
                {
                    current.Add((k, l));
                    k++;
                    l++;
                }

                if (current.Count > best.Count)
                {
                    best = current;
                }
            }
        }

        if (best.Count >= Math.Max(overlapLeft.Count / 2, 1))
        {
            return UseMatches(best, overlapLeft, overlapRight, left, right);
        }

        var rows = overlapLeft.Count;
        var cols = overlapRight.Count;
        var dp = new int[rows + 1, cols + 1];
        for (var r = 1; r <= rows; r++)
        {
            for (var c = 1; c <= cols; c++)
            {
                dp[r, c] = Matches(r - 1, c - 1)
                    ? dp[r - 1, c - 1] + 1
                    : Math.Max(dp[r - 1, c], dp[r, c - 1]);
            }
        }

        var lcs = new List<(int LeftIndex, int RightIndex)>();
        var i = rows;
        var j = cols;
        while (i > 0 && j > 0)
        {
            if (Matches(i - 1, j - 1))
            {
                lcs.Add((i - 1, j - 1));
                i--;
                j--;
            }
            else if (dp[i - 1, j] > dp[i, j - 1])
            {
                i--;
            }
            else
            {
                j--;
            }
        }

        lcs.Reverse();
        return lcs.Count == 0
            ? MergeByMidpoint(left, right, leftEnd, rightStart)
            : UseMatches(lcs, overlapLeft, overlapRight, left, right);
    }

    private List<DecodedToken> UseMatches(
        List<(int LeftIndex, int RightIndex)> matches,
        List<int> overlapLeft,
        List<int> overlapRight,
        IReadOnlyList<DecodedToken> left,
        IReadOnlyList<DecodedToken> right)
    {
        var leftIndices = new int[matches.Count];
        var rightIndices = new int[matches.Count];
        for (var idx = 0; idx < matches.Count; idx++)
        {
            leftIndices[idx] = overlapLeft[matches[idx].LeftIndex];
            rightIndices[idx] = overlapRight[matches[idx].RightIndex];
        }

        var result = new List<DecodedToken>(left.Count + right.Count);
        for (var idx = 0; idx < leftIndices[0]; idx++)
        {
            result.Add(left[idx]);
        }

        for (var x = 0; x < matches.Count; x++)
        {
            result.Add(left[leftIndices[x]]);
            if (x < matches.Count - 1)
            {
                var gapLeftCount = Math.Max(0, leftIndices[x + 1] - (leftIndices[x] + 1));
                var gapRightCount = Math.Max(0, rightIndices[x + 1] - (rightIndices[x] + 1));
                if (gapRightCount > gapLeftCount)
                {
                    for (var idx = rightIndices[x] + 1; idx < rightIndices[x + 1]; idx++)
                    {
                        result.Add(right[idx]);
                    }
                }
                else
                {
                    for (var idx = leftIndices[x] + 1; idx < leftIndices[x + 1]; idx++)
                    {
                        result.Add(left[idx]);
                    }
                }
            }
        }

        var lastRight = rightIndices[^1];
        if (lastRight + 1 < right.Count)
        {
            var firstTailId = right[lastRight + 1].Id;
            if (!IsSpliceSafe(firstTailId))
            {
                var wordStart = -1;
                for (var idx = lastRight; idx >= 0; idx--)
                {
                    if (IsSpliceSafe(right[idx].Id))
                    {
                        wordStart = idx;
                        break;
                    }
                }

                var pop = -1;
                for (var idx = result.Count - 1; idx >= 0; idx--)
                {
                    if (IsSpliceSafe(result[idx].Id))
                    {
                        pop = idx;
                        break;
                    }
                }

                if (wordStart >= 0 && pop >= 0)
                {
                    result.RemoveRange(pop, result.Count - pop);
                    for (var idx = wordStart; idx < right.Count; idx++)
                    {
                        result.Add(right[idx]);
                    }
                }
                else
                {
                    var cursor = leftIndices[^1] + 1;
                    while (cursor < left.Count && !IsSpliceSafe(left[cursor].Id))
                    {
                        result.Add(left[cursor++]);
                    }

                    var resume = -1;
                    for (var idx = lastRight + 1; idx < right.Count; idx++)
                    {
                        if (IsSpliceSafe(right[idx].Id))
                        {
                            resume = idx;
                            break;
                        }
                    }

                    var appendFrom = resume >= 0 ? resume : lastRight + 1;
                    for (var idx = appendFrom; idx < right.Count; idx++)
                    {
                        result.Add(right[idx]);
                    }
                }
            }
            else
            {
                for (var idx = lastRight + 1; idx < right.Count; idx++)
                {
                    result.Add(right[idx]);
                }
            }
        }

        return result;
    }

    private List<DecodedToken> MergeByMidpoint(
        IReadOnlyList<DecodedToken> left,
        IReadOnlyList<DecodedToken> right,
        double leftEnd,
        double rightStart)
    {
        var cutoff = (leftEnd + rightStart) / 2.0;
        var leftStop = left.Count;
        for (var idx = 0; idx < left.Count; idx++)
        {
            if (left[idx].Frame * SecondsPerFrame >= cutoff)
            {
                leftStop = idx;
                break;
            }
        }

        var rightFrom = right.Count;
        for (var idx = 0; idx < right.Count; idx++)
        {
            if (right[idx].Frame * SecondsPerFrame >= cutoff)
            {
                rightFrom = idx;
                break;
            }
        }

        if (leftStop > 0)
        {
            while (leftStop < left.Count && !IsSpliceSafe(left[leftStop].Id))
            {
                leftStop++;
            }
        }

        var scan = rightFrom;
        while (scan < right.Count && !IsSpliceSafe(right[scan].Id))
        {
            scan++;
        }

        if (scan < right.Count)
        {
            rightFrom = scan;
        }

        var result = new List<DecodedToken>(leftStop + (right.Count - rightFrom));
        for (var idx = 0; idx < leftStop; idx++)
        {
            result.Add(left[idx]);
        }

        for (var idx = rightFrom; idx < right.Count; idx++)
        {
            result.Add(right[idx]);
        }

        return result;
    }

    internal static List<DecodedToken> EnforceMonotonic(IReadOnlyList<DecodedToken> tokens)
    {
        if (tokens.Count < 2)
        {
            return [.. tokens];
        }

        var result = new List<DecodedToken>(tokens.Count) { tokens[0] };
        var lastFrame = tokens[0].Frame;
        for (var i = 1; i < tokens.Count; i++)
        {
            var current = tokens[i];
            if (current.Frame < lastFrame)
            {
                result.Add(current with { Frame = lastFrame });
            }
            else
            {
                lastFrame = current.Frame;
                result.Add(current);
            }
        }

        return result;
    }

    internal List<DecodedToken> CollapseSeamDuplicates(
        IReadOnlyList<DecodedToken> tokens,
        IReadOnlyList<(int StartFrame, int EndFrame)>? seamRanges = null)
    {
        if (tokens.Count < 2)
        {
            return [.. tokens];
        }

        var overlapFrames = (int)Math.Round(OverlapSeconds / SecondsPerFrame);
        var words = new List<List<DecodedToken>>();
        foreach (var token in tokens)
        {
            if (words.Count == 0 || IsBoundary(_vocabulary.Tokens[token.Id]))
            {
                words.Add([token]);
            }
            else
            {
                words[^1].Add(token);
            }
        }

        var cores = new string[words.Count];
        var endsSentence = new bool[words.Count];
        for (var w = 0; w < words.Count; w++)
        {
            var sb = new StringBuilder();
            foreach (var token in words[w])
            {
                sb.Append(StripBoundaryPrefix(_vocabulary.Tokens[token.Id]));
            }

            var joined = sb.ToString();
            endsSentence[w] = joined.Length > 0 && joined[^1] is '.' or '?' or '!' or ':';
            cores[w] = TrimPunctuationAndWhitespace(joined);
        }

        var keep = new bool[words.Count];
        Array.Fill(keep, true);
        var lastKept = -1;
        for (var i = 0; i < words.Count; i++)
        {
            if (lastKept < 0)
            {
                lastKept = i;
                continue;
            }

            var previous = cores[lastKept];
            var current = cores[i];
            var previousLower = previous.ToLowerInvariant();
            var currentLower = current.ToLowerInvariant();
            var isDuplicate = previous.Length > 0 &&
                current.Length > 0 &&
                !string.Equals(previous, current, StringComparison.Ordinal) &&
                string.Equals(previousLower, currentLower, StringComparison.Ordinal) &&
                char.IsLetter(current[0]) &&
                !endsSentence[lastKept] &&
                words[i][0].Frame - words[lastKept][0].Frame <= overlapFrames &&
                (seamRanges is null || IsInAnySeam(words[lastKept][0].Frame, words[i][0].Frame, seamRanges));
            if (!isDuplicate)
            {
                lastKept = i;
                continue;
            }

            if (string.Equals(current, currentLower, StringComparison.Ordinal) &&
                !string.Equals(previous, previousLower, StringComparison.Ordinal))
            {
                keep[lastKept] = false;
                lastKept = i;
            }
            else
            {
                keep[i] = false;
            }
        }

        var result = new List<DecodedToken>(tokens.Count);
        for (var w = 0; w < words.Count; w++)
        {
            if (keep[w])
            {
                result.AddRange(words[w]);
            }
        }

        return result;
    }

    private static bool IsInAnySeam(
        int leftFrame,
        int rightFrame,
        IReadOnlyList<(int StartFrame, int EndFrame)> seamRanges)
    {
        for (var i = 0; i < seamRanges.Count; i++)
        {
            var (startFrame, endFrame) = seamRanges[i];
            if (leftFrame >= startFrame && rightFrame <= endFrame)
            {
                return true;
            }
        }

        return false;
    }

    internal List<DecodedToken> RepairSeamGaps(
        float[] samples,
        IReadOnlyList<DecodedToken> tokens,
        bool[]? allowedTokenMask,
        bool[]? blockedTokenMask,
        ParakeetTranscriptionEngine.IParakeetRunContext runContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var n = samples.Length;
        var frameCount = n / FrameSamples;
        var rms = new float[frameCount];
        var nonZeroRms = new List<float>(frameCount);
        for (var frame = 0; frame < frameCount; frame++)
        {
            if ((frame & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var value = ComputeFrameRms(samples, frame * FrameSamples, FrameSamples);
            rms[frame] = value;
            if (value > 0f)
            {
                nonZeroRms.Add(value);
            }
        }

        float threshold;
        if (nonZeroRms.Count == 0)
        {
            threshold = SpeechRmsCeiling;
        }
        else
        {
            nonZeroRms.Sort();
            var reference = nonZeroRms[Math.Min(nonZeroRms.Count - 1, (int)(nonZeroRms.Count * 0.75))];
            threshold = Math.Clamp(reference * 0.3f, SpeechRmsFloor, SpeechRmsCeiling);
        }

        double SpeechSeconds(int fromSample, int toSample)
        {
            var speechFrames = 0;
            var f = fromSample / FrameSamples;
            var last = Math.Min(CeilDiv(toSample, FrameSamples), frameCount);
            while (f < last)
            {
                if (rms[f] >= threshold)
                {
                    speechFrames++;
                }

                f++;
            }

            return speechFrames * SecondsPerFrame;
        }

        DecodedToken Neighbour(IReadOnlyList<DecodedToken> stream, int from, int step)
        {
            var idx = from;
            while (idx + step >= 0 &&
                   idx + step < stream.Count &&
                   IsTokenPunctuationOrSymbol(stream[idx].Id))
            {
                idx += step;
            }

            return stream[idx];
        }

        bool SamePiece(int a, int b) =>
            a == b ||
            string.Equals(
                _vocabulary.Tokens[a].ToLowerInvariant(),
                _vocabulary.Tokens[b].ToLowerInvariant(),
                StringComparison.Ordinal);

        var minGapFrames = Math.Max(2, (int)(MinGapSeconds / SecondsPerFrame));
        var working = tokens.ToList();
        var probes = 0;
        var probed = new HashSet<int>();

        for (var pass = 0; pass < 3; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inserts = new List<DecodedToken>();
            for (var i = 0; i < working.Count - 1; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (probes >= MaxRepairs)
                {
                    break;
                }

                var current = working[i];
                var next = working[i + 1];
                var gapStart = current.Frame + Math.Max(1, current.Duration);
                var gapEnd = next.Frame;
                if (gapEnd - gapStart < minGapFrames || !probed.Add(gapStart))
                {
                    continue;
                }

                var startSample = gapStart * FrameSamples;
                var endSample = Math.Min(gapEnd * FrameSamples, n);
                if (endSample <= startSample || SpeechSeconds(startSample, endSample) < MinGapSpeechSeconds)
                {
                    continue;
                }

                probes++;
                var centre = (startSample + endSample) / 2;
                var placements = new[] { startSample, centre - (RepairWindowSamples / 2) };
                foreach (var placement in placements)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var windowStart = Math.Max(0, Math.Min(placement, n - RepairWindowSamples)) / FrameSamples * FrameSamples;
                    var windowEnd = Math.Min(windowStart + RepairWindowSamples, n);
                    if (windowEnd <= windowStart)
                    {
                        continue;
                    }

                    var window = DecodeWindow(
                        samples.AsSpan(windowStart, windowEnd - windowStart),
                        contextSamples: 0,
                        offsetSample: windowStart,
                        isLast: windowEnd >= n,
                        padToWindow: true,
                        allowedTokenMask,
                        blockedTokenMask,
                        runContext,
                        cancellationToken);

                    var candidate = new List<DecodedToken>();
                    foreach (var token in window)
                    {
                        if (token.Frame > gapStart && token.Frame < gapEnd - 1)
                        {
                            candidate.Add(token);
                        }
                    }

                    while (candidate.Count > 0 && !IsSpliceSafe(candidate[0].Id))
                    {
                        candidate.RemoveAt(0);
                    }

                    var lead = Neighbour(working, i, -1);
                    var trail = Neighbour(working, i + 1, 1);
                    while (candidate.Count > 0 &&
                           SamePiece(candidate[0].Id, lead.Id) &&
                           Math.Abs(candidate[0].Frame - lead.Frame) <= EdgeFrames)
                    {
                        candidate.RemoveAt(0);
                    }

                    while (candidate.Count > 0 &&
                           SamePiece(candidate[^1].Id, trail.Id) &&
                           Math.Abs(trail.Frame - candidate[^1].Frame) <= EdgeFrames)
                    {
                        candidate.RemoveAt(candidate.Count - 1);
                    }

                    while (candidate.Count > 0 &&
                           (!IsSpliceSafe(candidate[0].Id) || IsTokenPunctuationOrSymbol(candidate[0].Id)))
                    {
                        candidate.RemoveAt(0);
                    }

                    if (candidate.Count > 0)
                    {
                        inserts.AddRange(candidate);
                        break;
                    }
                }
            }

            if (inserts.Count == 0)
            {
                return working;
            }

            working.AddRange(inserts);
            working = working.OrderBy(token => token.Frame).ToList();
        }

        return working;
    }

    internal string TokensToText(IReadOnlyList<DecodedToken> tokens)
    {
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var token in tokens)
        {
            text.Append(_vocabulary.Tokens[token.Id]);
        }

        return ParakeetTranscriptionEngine.NormalizeDecodedTokenText(
            text.ToString().Replace(SentencePieceBoundary, ' '));
    }

    internal static TranscriptTokenTiming[] BuildTimings(
        IReadOnlyList<DecodedToken> tokens,
        string[] vocabularyTokens,
        TimeSpan fullAudioDuration,
        int startSampleOffset = 0)
    {
        if (tokens.Count == 0)
        {
            return [];
        }

        var startOffset = startSampleOffset == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(startSampleOffset / (double)SampleRate);
        var result = new TranscriptTokenTiming[tokens.Count];
        var previousStart = TimeSpan.Zero;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var rawStart = startOffset + TimeSpan.FromSeconds(token.Frame * SecondsPerFrame);
            var rawEnd = startOffset + TimeSpan.FromSeconds((token.Frame + Math.Max(1, token.Duration)) * SecondsPerFrame);
            if (i + 1 < tokens.Count)
            {
                var nextStart = startOffset + TimeSpan.FromSeconds(tokens[i + 1].Frame * SecondsPerFrame);
                if (rawEnd > nextStart)
                {
                    rawEnd = nextStart;
                }
            }

            var clampedEnd = rawEnd > fullAudioDuration ? fullAudioDuration : rawEnd;
            if (clampedEnd < TimeSpan.Zero)
            {
                clampedEnd = TimeSpan.Zero;
            }

            var clampedStart = rawStart > clampedEnd ? clampedEnd : rawStart;
            if (clampedStart < previousStart)
            {
                clampedStart = previousStart;
            }

            if (clampedStart > clampedEnd)
            {
                clampedEnd = clampedStart;
            }

            previousStart = clampedStart;
            result[i] = new TranscriptTokenTiming(vocabularyTokens[token.Id], clampedStart, clampedEnd);
        }

        return result;
    }

    internal static int TokenLimit(int frames) =>
        Math.Max(MaxTokensPerWindow, frames * MaxTokensPerWindow / FramesPerWindow);

    internal static int CeilDiv(int a, int b) =>
        a <= 0 ? 0 : (a + b - 1) / b;

    internal static int LastChunkWarmup(int start, int totalSamples, int speechEnd)
    {
        var effectiveTotal = Math.Min(totalSamples, speechEnd);
        var isLast = start + ChunkSamples >= effectiveTotal;
        var remaining = effectiveTotal - start;
        if (!isLast || remaining <= 0 || start <= 0)
        {
            return 0;
        }

        var fill = (ChunkSamples - remaining) / FrameSamples * FrameSamples;
        if (fill <= 0)
        {
            return 0;
        }

        return Math.Max(0, Math.Min(start / FrameSamples * FrameSamples, fill));
    }

    internal static int FindSpeechEnd(ReadOnlySpan<float> samples, CancellationToken cancellationToken = default)
    {
        var blockEnd = samples.Length;
        while (blockEnd > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockStart = Math.Max(0, blockEnd - (RmsBlockFrames * FrameSamples));
            var end = blockEnd;
            while (end > blockStart)
            {
                var from = Math.Max(blockStart, end - FrameSamples);
                if (ComputeFrameRms(samples, from, end - from) >= SpeechRmsFloor)
                {
                    return end;
                }

                end = from;
            }

            blockEnd = blockStart;
        }

        return samples.Length;
    }

    internal static bool IsBoundary(string piece) =>
        piece.Length > 0 && (piece[0] == SentencePieceBoundary || piece[0] == ' ');

    internal static bool IsPunctuationPiece(string? piece)
    {
        if (string.IsNullOrEmpty(piece) || ParakeetVocabulary.IsControlToken(piece))
        {
            return false;
        }

        var core = StripBoundaryPrefix(piece).Trim();
        if (core.Length == 0)
        {
            return false;
        }

        foreach (var ch in core)
        {
            if (!char.IsPunctuation(ch))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsPunctuationOrSymbol(string? piece)
    {
        if (string.IsNullOrEmpty(piece) || ParakeetVocabulary.IsControlToken(piece))
        {
            return false;
        }

        foreach (var ch in piece)
        {
            if (!char.IsPunctuation(ch) && !char.IsSymbol(ch))
            {
                return false;
            }
        }

        return true;
    }

    internal static float[] TransposeEncoderOutput(float[] encoded, int encoderDimension)
    {
        var frameCount = encoded.Length / encoderDimension;
        var transposed = new float[encoded.Length];
        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var dimension = 0; dimension < encoderDimension; dimension++)
            {
                transposed[(frame * encoderDimension) + dimension] =
                    encoded[(dimension * frameCount) + frame];
            }
        }

        return transposed;
    }

    private static float[] AlignToFrameBoundary(float[] samples)
    {
        var remainder = samples.Length % FrameSamples;
        if (remainder == 0)
        {
            return samples;
        }

        var aligned = new float[samples.Length + FrameSamples - remainder];
        Array.Copy(samples, aligned, samples.Length);
        return aligned;
    }

    private static int MapDuration(int durationBin) =>
        (uint)durationBin < (uint)DurationBins.Length
            ? DurationBins[durationBin]
            : Math.Max(0, durationBin);

    private bool IdsMatch(int a, int b) =>
        a == b || (_caseVariant[a] >= 0 && _caseVariant[a] == _caseVariant[b]);

    private bool IsSpliceSafe(int tokenId) =>
        (uint)tokenId < (uint)_spliceSafe.Length && _spliceSafe[tokenId];

    private bool IsTokenPunctuationOrSymbol(int tokenId) =>
        (uint)tokenId < (uint)_punctuationOrSymbol.Length && _punctuationOrSymbol[tokenId];

    private static int[] BuildCaseVariants(string[] tokens)
    {
        var canonicalByFolded = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Length; i++)
        {
            var folded = tokens[i].ToLowerInvariant();
            if (!canonicalByFolded.TryGetValue(folded, out var ids))
            {
                ids = [];
                canonicalByFolded[folded] = ids;
            }

            ids.Add(i);
        }

        var variants = new int[tokens.Length];
        Array.Fill(variants, -1);
        foreach (var (folded, ids) in canonicalByFolded)
        {
            if (ids.Count <= 1)
            {
                continue;
            }

            var canonical = -1;
            foreach (var id in ids)
            {
                if (string.Equals(tokens[id], folded, StringComparison.Ordinal))
                {
                    canonical = id;
                    break;
                }
            }

            if (canonical < 0)
            {
                canonical = ids[0];
            }

            foreach (var id in ids)
            {
                variants[id] = canonical;
            }
        }

        return variants;
    }

    private static string StripBoundaryPrefix(string piece) =>
        piece.Length > 0 && (piece[0] == SentencePieceBoundary || piece[0] == ' ')
            ? piece[1..]
            : piece;

    private static string TrimPunctuationAndWhitespace(string value)
    {
        var start = 0;
        var end = value.Length - 1;
        while (start <= end && (char.IsWhiteSpace(value[start]) || char.IsPunctuation(value[start])))
        {
            start++;
        }

        while (end >= start && (char.IsWhiteSpace(value[end]) || char.IsPunctuation(value[end])))
        {
            end--;
        }

        return start > end ? string.Empty : value[start..(end + 1)];
    }

    private static float ComputeFrameRms(ReadOnlySpan<float> samples, int from, int count)
    {
        var end = Math.Min(samples.Length, from + count);
        if (end <= from)
        {
            return 0f;
        }

        var sumSquares = 0.0;
        for (var i = from; i < end; i++)
        {
            var sample = samples[i];
            sumSquares += sample * sample;
        }

        return (float)Math.Sqrt(sumSquares / (end - from));
    }
}
