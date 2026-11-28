using EnviousWispr.ASR;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class ParakeetTdtRecipeTests
{
    private static readonly string[] TestTokens =
    [
        " the",     // 0
        " The",     // 1
        " cat",     // 2
        "s",        // 3
        ".",        // 4
        " sat",     // 5
        " on",      // 6
        " mat",     // 7
        "?",        // 8
        " le",      // 9
        " chat",    // 10
        " привет",  // 11
        "<unk>",    // 12
        "<blk>",    // 13
    ];

    private const int BlankIndex = 13;

    private static ParakeetVocabulary CreateVocab() =>
        ParakeetVocabulary.FromTokens(TestTokens, BlankIndex);

    private static ParakeetTdtRecipe CreateRecipe(
        ScriptedRecipeBackend backend,
        ParakeetVocabulary? vocabulary = null,
        int maxSymbolsPerStep = ParakeetTdtRecipe.MaxSymbolsPerStep) =>
        new(backend, vocabulary ?? CreateVocab(), maxSymbolsPerStep);

    private static float[] Silence(double seconds) =>
        new float[(int)(seconds * ParakeetTdtRecipe.SampleRate)];

    private static float[] Loud(double seconds)
    {
        var samples = new float[(int)(seconds * ParakeetTdtRecipe.SampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i % 2 == 0 ? 0.1f : -0.1f;
        }

        return samples;
    }

    private static float[] Concat(params float[][] parts)
    {
        var total = parts.Sum(part => part.Length);
        var result = new float[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }

    [Fact]
    public void ShortTakeUpToFifteenSecondsRunsOneUnpaddedFrameAlignedPass()
    {
        var backend = new ScriptedRecipeBackend();
        using var runContext = backend.CreateRunContext(CancellationToken.None);
        var recipe = CreateRecipe(backend);

        // 3.0s (48,000) + 100 samples = 48,100 -> rounds up to 38 whole frames = 48,640 samples.
        var samples = new float[48_100];
        Array.Fill(samples, 0.05f);
        var result = recipe.Transcribe(samples, null, null, runContext, CancellationToken.None);

        Assert.Equal([(48_640, 48_640)], backend.PreprocessCalls);
        Assert.Equal(1, result.WindowCount);
        Assert.Equal(0, result.RepairCount);
    }

    [Fact]
    public void FifteenSecondBoundaryIsOnePassAndOverFifteenSecondsSwitchesToPaddedWindows()
    {
        var onePassBackend = new ScriptedRecipeBackend();
        using var onePassContext = onePassBackend.CreateRunContext(CancellationToken.None);
        var onePassResult = CreateRecipe(onePassBackend).Transcribe(
            Loud(15.0), null, null, onePassContext, CancellationToken.None);

        // 15.0s = 240,000 samples (187.5 frames -> frame-aligned 188 frames = 240,640 samples) stays single-pass.
        Assert.Equal([(240_640, 240_640)], onePassBackend.PreprocessCalls);
        Assert.Equal(1, onePassResult.WindowCount);

        var windowedBackend = new ScriptedRecipeBackend();
        using var windowedContext = windowedBackend.CreateRunContext(CancellationToken.None);
        var windowedResult = CreateRecipe(windowedBackend).Transcribe(
            Concat(Loud(15.0), Loud(0.08)), null, null, windowedContext, CancellationToken.None);

        Assert.True(windowedResult.WindowCount >= 2);
        Assert.All(windowedBackend.PreprocessCalls, call => Assert.Equal(ParakeetTdtRecipe.WindowSamples, call.BufferLength));
    }

    [Fact]
    public void LongFortySecondTakeUsesFixedStrideWithBackfilledLastChunkAndFreshStates()
    {
        var backend = new ScriptedRecipeBackend();
        using var runContext = backend.CreateRunContext(CancellationToken.None);
        var recipe = CreateRecipe(backend);

        var result = recipe.Transcribe(Loud(40.0), null, null, runContext, CancellationToken.None);

        Assert.Equal(3, result.WindowCount);
        Assert.Equal(
            [
                (240_000, 238_080),
                (240_000, 238_080 + 1_280),
                (240_000, 238_080),
            ],
            backend.PreprocessCalls);
        Assert.Equal(3, backend.FirstStepStates.Count);
        Assert.All(backend.FirstStepStates, state =>
        {
            Assert.All(state.State1, v => Assert.Equal(0f, v));
            Assert.All(state.State2, v => Assert.Equal(0f, v));
        });
    }

    [Fact]
    public void FortyPointFiveSecondRecordingRecoversAllTokensPastSinglePassCollapse()
    {
        // Gate 0 scenario: 40.50s (648,000 samples) emitting 1 token every 2 frames (~253 tokens total),
        // well past the 150-token single-pass limit / 28.36s collapse.
        var backend = new ScriptedRecipeBackend((_, frame, _, _, _) =>
            frame % 2 == 0 ? (2, 1) : (BlankIndex, 1));
        using var runContext = backend.CreateRunContext(CancellationToken.None);
        var recipe = CreateRecipe(backend);

        var result = recipe.Transcribe(Loud(40.5), null, null, runContext, CancellationToken.None);

        Assert.True(result.WindowCount >= 3);
        Assert.True(result.Tokens.Count > 200, $"Expected >200 tokens across 40.5s, got {result.Tokens.Count}.");
        var lastTime = result.Tokens[^1].Frame * ParakeetTdtRecipe.SecondsPerFrame;
        Assert.True(lastTime > 38.0, $"Expected final token past 38s, got {lastTime:F2}s.");
    }

    [Fact]
    public void TrailingSilenceTrimsFinalChunkAtSpeechEnd()
    {
        var backend = new ScriptedRecipeBackend();
        using var runContext = backend.CreateRunContext(CancellationToken.None);
        var recipe = CreateRecipe(backend);

        // 25s of speech + 10s of trailing silence = 35s total; chunk 2 starts past speechEnd and is skipped.
        var samples = Concat(Loud(25.0), Silence(10.0));
        var result = recipe.Transcribe(samples, null, null, runContext, CancellationToken.None);

        Assert.Equal(2, result.WindowCount);
        Assert.Equal([(240_000, 238_080), (240_000, 239_360)], backend.PreprocessCalls);

        // 25s speech + 2s trailing silence = 27s total; final chunk 1 trims its tail at 25s and backfills to 238,080.
        var tailBackend = new ScriptedRecipeBackend();
        using var tailCtx = tailBackend.CreateRunContext(CancellationToken.None);
        var tailResult = CreateRecipe(tailBackend).Transcribe(
            Concat(Loud(25.0), Silence(2.0)), null, null, tailCtx, CancellationToken.None);
        Assert.Equal(2, tailResult.WindowCount);
        Assert.Equal((240_000, 238_080), tailBackend.PreprocessCalls[1]);
    }

    [Fact]
    public void TokenLimitScalesAndAppliesToKeptEndToken()
    {
        Assert.Equal(150, ParakeetTdtRecipe.TokenLimit(150));
        Assert.Equal(150, ParakeetTdtRecipe.TokenLimit(187));
        Assert.Equal(150, ParakeetTdtRecipe.TokenLimit(188));
        Assert.Equal(300, ParakeetTdtRecipe.TokenLimit(375));

        const int limit = ParakeetTdtRecipe.MaxTokensPerWindow;
        var frames = (int)(14.0 * ParakeetTdtRecipe.SampleRate / ParakeetTdtRecipe.FrameSamples); // 175 frames
        var overLimitBackend = new ScriptedRecipeBackend((_, frame, _, _, _) =>
            frame < limit ? (0, 1) : (frame == frames - 1 ? (2, 4) : (BlankIndex, 1)));
        using var overCtx = overLimitBackend.CreateRunContext(CancellationToken.None);
        var overRecipe = CreateRecipe(overLimitBackend);
        var overResult = overRecipe.Transcribe(Loud(14.0), null, null, overCtx, CancellationToken.None);
        Assert.Equal(limit, overResult.Tokens.Count);
        Assert.DoesNotContain(overResult.Tokens, t => t.Id == 2);

        var underLimitBackend = new ScriptedRecipeBackend((_, frame, _, _, _) =>
            frame < limit - 1 ? (0, 1) : (frame == frames - 1 ? (2, 4) : (BlankIndex, 1)));
        using var underCtx = underLimitBackend.CreateRunContext(CancellationToken.None);
        var underRecipe = CreateRecipe(underLimitBackend);
        var underResult = underRecipe.Transcribe(Loud(14.0), null, null, underCtx, CancellationToken.None);
        Assert.Equal(limit, underResult.Tokens.Count);
        Assert.Equal(2, underResult.Tokens[^1].Id);
    }

    [Fact]
    public void LastWindowKeepsBoundaryTokenWhoseDurationLeavesAudioWhileEarlierWindowDropsIt()
    {
        // Single-window 1.0s take: 13 frames, " cat" at frame 12 with duration 4 leaves bounds -> kept on last window.
        var lastWindowBackend = new ScriptedRecipeBackend((_, frame, _, _, _) =>
            frame == 12 ? (2, 4) : (BlankIndex, 1));
        using var lastCtx = lastWindowBackend.CreateRunContext(CancellationToken.None);
        var lastRecipe = CreateRecipe(lastWindowBackend);
        var lastResult = lastRecipe.Transcribe(Loud(1.0), null, null, lastCtx, CancellationToken.None);
        Assert.Equal("cat", lastRecipe.TokensToText(lastResult.Tokens));

        // Window 0 of a 40s take has 186 frames; token at frame 185 with duration 4 on window 0 is NOT last -> dropped.
        var earlierBackend = new ScriptedRecipeBackend((window, frame, _, _, _) =>
            window == 0 && frame == 185 ? (2, 4) : (BlankIndex, 1));
        using var earlierCtx = earlierBackend.CreateRunContext(CancellationToken.None);
        var earlierRecipe = CreateRecipe(earlierBackend);
        var earlierResult = earlierRecipe.Transcribe(Loud(40.0), null, null, earlierCtx, CancellationToken.None);
        Assert.Equal(string.Empty, earlierRecipe.TokensToText(earlierResult.Tokens));
    }

    [Fact]
    public void LastWindowFinalizationEmitsOnlyPunctuationWithoutCorruptingStateOnRejectedWords()
    {
        var backend = new ScriptedRecipeBackend((_, frame, previous, _, _) =>
        {
            if (frame == 3) return (2, 1);
            if (frame == 9) return (BlankIndex, 4);
            if (previous == 2 && frame >= 12) return (4, 1); // "."
            return (BlankIndex, 1);
        });
        using var ctx = backend.CreateRunContext(CancellationToken.None);
        var recipe = CreateRecipe(backend);
        Assert.Equal("cat.", recipe.TokensToText(recipe.Transcribe(Loud(1.0), null, null, ctx, CancellationToken.None).Tokens));

        var wordBackend = new ScriptedRecipeBackend((_, frame, previous, _, _) =>
        {
            if (frame == 3) return (2, 1);
            if (frame == 9) return (BlankIndex, 4);
            if (previous == 2 && frame >= 12) return (5, 1); // " sat" — lexical, must be suppressed
            return (BlankIndex, 1);
        });
        using var wordCtx = wordBackend.CreateRunContext(CancellationToken.None);
        var wordRecipe = CreateRecipe(wordBackend);
        Assert.Equal("cat", wordRecipe.TokensToText(wordRecipe.Transcribe(Loud(1.0), null, null, wordCtx, CancellationToken.None).Tokens));

        // Verify consecutive blank limit (5) stops finalization even if maxSymbolsPerStep is 10.
        var stepCount = 0;
        var limitBackend = new ScriptedRecipeBackend((_, frame, _, _, _) =>
        {
            stepCount++;
            return (BlankIndex, 4);
        });
        using var limitCtx = limitBackend.CreateRunContext(CancellationToken.None);
        var limitRecipe = CreateRecipe(limitBackend);
        limitRecipe.Transcribe(Loud(1.0), null, null, limitCtx, CancellationToken.None);
        // 13 frames / 4 = 4 main steps (0, 4, 8, 12) + 5 finalization blanks = 9 steps total.
        Assert.Equal(9, stepCount);
    }

    [Fact]
    public void DurationZeroLoopGuardsPreventInfiniteDecodingAndRespectMaxSymbolsPerStep()
    {
        var sameFrameEmissions = 0;
        var backend = new ScriptedRecipeBackend((_, frame, _, _, _) =>
        {
            if (frame == 1)
            {
                sameFrameEmissions++;
                return sameFrameEmissions switch
                {
                    1 => (2, 0), // duration=0 allowed once at frame 1
                    2 => (3, 0), // second emit at frame 1 with duration=0 forced to 1
                    _ => (BlankIndex, 0),
                };
            }

            return (BlankIndex, 0); // blank with duration=0 forced to 1
        });
        using var ctx = backend.CreateRunContext(CancellationToken.None);
        var recipe = CreateRecipe(backend);

        var result = recipe.Transcribe(Loud(0.5), null, null, ctx, CancellationToken.None);
        Assert.Equal("cats", recipe.TokensToText(result.Tokens));

        // Repeated positive-duration or 0-duration emissions hit maxSymbolsPerStep=2 guard cleanly.
        var capBackend = new ScriptedRecipeBackend((_, frame, _, _, _) =>
            frame == 0 ? (2, 0) : (BlankIndex, 1));
        using var capCtx = capBackend.CreateRunContext(CancellationToken.None);
        var capRecipe = CreateRecipe(capBackend, maxSymbolsPerStep: 2);
        var capResult = capRecipe.Transcribe(Loud(0.5), null, null, capCtx, CancellationToken.None);
        Assert.Equal(2, capResult.Tokens.Count);
    }

    [Fact]
    public void MergeHandlesContiguousCaseVariantMidpointMonotonicAndDuplicateCollapse()
    {
        var recipe = CreateRecipe(new ScriptedRecipeBackend());

        // Contiguous overlap match drops right duplicate copy.
        var left = new List<ParakeetTdtRecipe.DecodedToken>
        {
            new(0, 100, 1), new(2, 104, 1), new(5, 108, 1), new(6, 112, 1),
        };
        var right = new List<ParakeetTdtRecipe.DecodedToken>
        {
            new(5, 108, 1), new(6, 112, 1), new(0, 116, 1), new(7, 120, 1),
        };
        Assert.Equal("the cat sat on the mat", recipe.TokensToText(recipe.Merge(left, right)));

        // Case-variant overlap match (" The" vs " the") anchors cleanly.
        var leftVar = new List<ParakeetTdtRecipe.DecodedToken>
        {
            new(2, 100, 1), new(1, 104, 1), new(5, 108, 1),
        };
        var rightVar = new List<ParakeetTdtRecipe.DecodedToken>
        {
            new(0, 104, 1), new(5, 108, 1), new(7, 112, 1),
        };
        Assert.Equal("cat The sat mat", recipe.TokensToText(recipe.Merge(leftVar, rightVar)));

        // Disjoint overlap falls back to midpoint split on splice-safe boundaries.
        var leftDisjoint = new List<ParakeetTdtRecipe.DecodedToken>
        {
            new(2, 100, 1), new(5, 110, 1), new(6, 120, 1),
        };
        var rightDisjoint = new List<ParakeetTdtRecipe.DecodedToken>
        {
            new(0, 105, 1), new(7, 115, 1), new(8, 125, 1),
        };
        Assert.Equal("cat sat mat?", recipe.TokensToText(recipe.Merge(leftDisjoint, rightDisjoint)));

        // Monotonic clamps backwards frames without reordering token IDs.
        var clamped = ParakeetTdtRecipe.EnforceMonotonic(
        [
            new(2, 10, 1), new(3, 8, 1), new(5, 12, 1),
        ]);
        Assert.Equal([2, 3, 5], clamped.Select(t => t.Id));
        Assert.Equal([10, 10, 12], clamped.Select(t => t.Frame));

        // Case-only seam duplicate collapse preserves lowercase and respects sentence boundaries.
        Assert.Equal("the cat", recipe.TokensToText(recipe.CollapseSeamDuplicates([new(0, 10, 1), new(1, 12, 1), new(2, 14, 1)])));
        Assert.Equal("the cat", recipe.TokensToText(recipe.CollapseSeamDuplicates([new(1, 10, 1), new(0, 12, 1), new(2, 14, 1)])));
        Assert.Equal("the. The", recipe.TokensToText(recipe.CollapseSeamDuplicates([new(0, 10, 1), new(4, 11, 1), new(1, 12, 1)])));
    }

    [Fact]
    public void SeamGapRepairSplicesSpeechGapsPreservesLanguageMasksAndSkipsSilence()
    {
        var vocab = CreateVocab();
        var repairBackend = new ScriptedRecipeBackend((_, frame, _, allowed, blocked) =>
        {
            if (frame == 17)
            {
                // Joint proposes " the" (0) at top1, " le" (9) as French-allowed non-blocked fallback,
                // and " привет" (11) even higher when unpinned — forcing both masks to be respected.
                float[] logits = new float[TestTokens.Length];
                logits[0] = 10f;  // " the" (blocked in fr)
                logits[9] = 6f;   // " le" (allowed Latin non-blocked)
                logits[11] = 20f; // " привет" (Cyrillic)
                return (ParakeetVocabulary.SelectToken(logits, BlankIndex, allowed, blocked), 1);
            }

            return (BlankIndex, 1);
        });
        using var runContext = repairBackend.CreateRunContext(CancellationToken.None);
        var recipe = CreateRecipe(repairBackend, vocab);

        var gapTokens = new List<ParakeetTdtRecipe.DecodedToken> { new(2, 0, 1), new(7, 35, 1) };
        var frenchRepaired = recipe.RepairSeamGaps(
            Loud(3.0),
            gapTokens,
            vocab.MaskFor(ParakeetScript.Latin),
            vocab.FrenchBlocklistMaskFor("fr"),
            runContext,
            CancellationToken.None);

        Assert.Equal("cat le mat", recipe.TokensToText(frenchRepaired));
        Assert.All(repairBackend.PreprocessCalls, call => Assert.Equal(ParakeetTdtRecipe.WindowSamples, call.BufferLength));

        // Non-French Latin still keeps " the" when repaired.
        var germanBackend = new ScriptedRecipeBackend((_, frame, _, allowed, blocked) =>
        {
            if (frame == 17)
            {
                float[] logits = new float[TestTokens.Length];
                logits[0] = 10f;
                logits[9] = 6f;
                logits[11] = 20f;
                return (ParakeetVocabulary.SelectToken(logits, BlankIndex, allowed, blocked), 1);
            }

            return (BlankIndex, 1);
        });
        using var deCtx = germanBackend.CreateRunContext(CancellationToken.None);
        var germanRepaired = CreateRecipe(germanBackend, vocab).RepairSeamGaps(
            Loud(3.0),
            gapTokens,
            vocab.MaskFor(ParakeetScript.Latin),
            vocab.FrenchBlocklistMaskFor("de"),
            deCtx,
            CancellationToken.None);
        Assert.Equal("cat the mat", CreateRecipe(germanBackend, vocab).TokensToText(germanRepaired));

        // Silent gap receives zero repair encodes.
        var silentBackend = new ScriptedRecipeBackend((_, frame, _, _, _) =>
            frame == 17 ? (5, 1) : (BlankIndex, 1));
        using var silentCtx = silentBackend.CreateRunContext(CancellationToken.None);
        var untouched = CreateRecipe(silentBackend, vocab).RepairSeamGaps(
            Silence(3.0),
            gapTokens,
            null,
            null,
            silentCtx,
            CancellationToken.None);
        Assert.Equal("cat mat", CreateRecipe(silentBackend, vocab).TokensToText(untouched));
        Assert.Empty(silentBackend.PreprocessCalls);
    }

    [Fact]
    public async Task ParakeetTranscriptionEngineRunsChunkedRecipeWithMonotonicTimingsAndFrenchBlocklist()
    {
        var vocab = CreateVocab();
        var backend = new ScriptedRecipeBackend((window, frame, _, allowed, blocked) =>
        {
            // Window 0: frame 10 -> global 10.
            // Window 1 (backfilled for 20s take, contextStart=81,920 / frame 64): frame 130 -> global 194 (past 161 warmup & overlap).
            if ((window == 0 && frame == 10) || (window == 1 && frame == 130))
            {
                float[] logits = new float[TestTokens.Length];
                logits[0] = 12f; // " the"
                logits[9] = 8f;  // " le"
                logits[11] = 25f; // " привет"
                return (ParakeetVocabulary.SelectToken(logits, BlankIndex, allowed, blocked), 2);
            }

            return (BlankIndex, 1);
        });
        var options = new ParakeetEngineOptions(
            ModelDirectory: "synthetic",
            RuntimeProviderKind.Cpu,
            ParakeetModelPack.Quantized,
            IntraOpThreads: 4);
        using var engine = new ParakeetTranscriptionEngine(options, backend, vocab);

        // Keep speech at the start and end with silence between so seam-gap repair skips the silent middle pause.
        var samples = Concat(Loud(2.0), Silence(12.5), Loud(5.5));
        var audio = new CapturedAudio(DictationSessionId.Create(), samples, 16_000, Channels: 1);

        var french = await engine.TranscribeAsync(audio, "fr");
        Assert.Equal("le le", french.Text);
        Assert.Equal("fr", french.RecognitionLanguage);
        Assert.Null(french.DetectedLanguage);
        Assert.NotNull(french.TokenTimings);
        Assert.Equal(2, french.TokenTimings!.Count);
        Assert.Equal(TimeSpan.FromSeconds(10 * 0.08), french.TokenTimings[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(12 * 0.08), french.TokenTimings[0].End);
        Assert.Equal(TimeSpan.FromSeconds(194 * 0.08), french.TokenTimings[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(196 * 0.08), french.TokenTimings[1].End);

        var russian = await engine.TranscribeAsync(audio, "ru");
        Assert.Equal("привет привет", russian.Text);
        Assert.Equal("ru", russian.RecognitionLanguage);
    }

    private sealed class ScriptedRecipeBackend : ParakeetTranscriptionEngine.IParakeetBackend
    {
        private readonly Func<int, int, int, bool[]?, bool[]?, (int Token, int Step)> _script;
        private int _window = -1;
        private bool _stepped;

        public ScriptedRecipeBackend(
            Func<int, int, int, bool[]?, bool[]?, (int Token, int Step)>? script = null)
        {
            _script = script ?? ((_, _, _, _, _) => (BlankIndex, 1));
        }

        public int EncoderDimension => 4;

        public int StateLeadingDimension => 2;

        public int StateDimension => 4;

        public List<(int BufferLength, int ValidLength)> PreprocessCalls { get; } = [];

        public List<(float[] State1, float[] State2)> FirstStepStates { get; } = [];

        public ParakeetTranscriptionEngine.IParakeetRunContext CreateRunContext(CancellationToken cancellationToken)
        {
            _window = -1;
            return new FakeRunContext();
        }

        public (float[] Features, long FeatureLength) Preprocess(
            float[] samples,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken) =>
            Preprocess(samples, samples.Length, runContext, cancellationToken);

        public (float[] Features, long FeatureLength) Preprocess(
            float[] samples,
            int validSampleCount,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreprocessCalls.Add((samples.Length, validSampleCount));
            var frameCount = ParakeetTdtRecipe.CeilDiv(validSampleCount, ParakeetTdtRecipe.FrameSamples);
            return (new float[128 * frameCount * 8], frameCount * 8L);
        }

        public (float[] EncodedChannelFirst, int EncodedLength) Encode(
            float[] features,
            long featureLength,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _window++;
            _stepped = false;
            var frames = ParakeetTdtRecipe.CeilDiv((int)featureLength, 8);
            var encoded = new float[EncoderDimension * frames];
            for (var frame = 0; frame < frames; frame++)
            {
                encoded[frame] = frame; // channel 0 carries local frame index
            }

            return (encoded, frames);
        }

        public bool TryEncodeWithFullPrecisionFallback(
            float[] features,
            long featureLength,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken,
            out float[] encodedChannelFirst,
            out int encodedLength)
        {
            encodedChannelFirst = [];
            encodedLength = 0;
            return false;
        }

        public ParakeetTranscriptionEngine.ParakeetDecoderStepResult DecodeStep(
            float[] encoderFrame,
            int[] target,
            int[] targetLength,
            float[] state1,
            float[] state2,
            int vocabularySize,
            int blankIndex,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken) =>
            DecodeStep(encoderFrame, target, targetLength, state1, state2, vocabularySize, blankIndex, null, null, runContext, cancellationToken);

        public ParakeetTranscriptionEngine.ParakeetDecoderStepResult DecodeStep(
            float[] encoderFrame,
            int[] target,
            int[] targetLength,
            float[] state1,
            float[] state2,
            int vocabularySize,
            int blankIndex,
            bool[]? allowedTokenMask,
            bool[]? blockedTokenMask,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_stepped)
            {
                FirstStepStates.Add(([.. state1], [.. state2]));
                _stepped = true;
            }

            var frame = (int)Math.Round(encoderFrame[0]);
            var (token, step) = _script(_window, frame, target[0], allowedTokenMask, blockedTokenMask);
            if (token != blankIndex)
            {
                state1[0] = frame + 1;
                state2[0] = token + 1;
            }

            return new ParakeetTranscriptionEngine.ParakeetDecoderStepResult(token, step);
        }

        public void Dispose()
        {
        }

        private sealed class FakeRunContext : ParakeetTranscriptionEngine.IParakeetRunContext
        {
            public void Dispose()
            {
            }
        }
    }
}
