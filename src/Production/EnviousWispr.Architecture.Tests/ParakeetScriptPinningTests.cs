using EnviousWispr.ASR;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class ParakeetScriptPinningTests
{
    [Fact]
    public void ScriptForLanguageMapsAllTwentyFiveEuropeanCodesAndRejectsOthers()
    {
        var latinCodes = new[]
        {
            "en", "en-gb", "es", "fr", "de", "it", "pt", "ro", "nl", "da", "sv",
            "fi", "hu", "et", "lv", "lt", "mt", "pl", "cs", "sk", "sl", "hr",
        };
        foreach (var code in latinCodes)
        {
            Assert.Equal(ParakeetScript.Latin, TokenLanguageFilter.ScriptForLanguage(code));
        }

        foreach (var code in new[] { "ru", "ru-RU", "uk", "bg" })
        {
            Assert.Equal(ParakeetScript.Cyrillic, TokenLanguageFilter.ScriptForLanguage(code));
        }

        foreach (var code in new[] { "el", "el-GR" })
        {
            Assert.Equal(ParakeetScript.Greek, TokenLanguageFilter.ScriptForLanguage(code));
        }

        foreach (var unpinned in new[] { null, "", " ", "auto", "ja", "ar", "hi", "ko", "sr", "bs", "be", "zh" })
        {
            Assert.Null(TokenLanguageFilter.ScriptForLanguage(unpinned));
        }
    }

    [Fact]
    public void TokenLanguageFilterMatchesScriptRangesAndNeutralBoundaryTokens()
    {
        Assert.True(TokenLanguageFilter.Matches("\u2581Café", ParakeetScript.Latin));
        Assert.True(TokenLanguageFilter.Matches(" łódź", ParakeetScript.Latin));
        Assert.False(TokenLanguageFilter.Matches(" привет", ParakeetScript.Latin));
        Assert.False(TokenLanguageFilter.Matches(" γεια", ParakeetScript.Latin));

        Assert.True(TokenLanguageFilter.Matches("\u2581привет", ParakeetScript.Cyrillic));
        Assert.True(TokenLanguageFilter.Matches(" Їжа", ParakeetScript.Cyrillic));
        Assert.False(TokenLanguageFilter.Matches(" hello", ParakeetScript.Cyrillic));
        Assert.False(TokenLanguageFilter.Matches("<unk>", ParakeetScript.Cyrillic));

        Assert.True(TokenLanguageFilter.Matches("\u2581Καλημέρα", ParakeetScript.Greek));
        Assert.True(TokenLanguageFilter.Matches(" γεια", ParakeetScript.Greek));
        Assert.False(TokenLanguageFilter.Matches(" hello", ParakeetScript.Greek));
        Assert.False(TokenLanguageFilter.Matches("<unk>", ParakeetScript.Greek));

        foreach (var neutral in new[] { "\u2581", " ", ",", ".", "?", "123", "-", "€", "\u2581€", "£", "¥", "$", "₽", "₹", "«", "»", "\u2014", "\u2018", "\u2019", "\u201C", "\u201D", "\u2026" })
        {
            Assert.True(TokenLanguageFilter.Matches(neutral, ParakeetScript.Latin));
            Assert.True(TokenLanguageFilter.Matches(neutral, ParakeetScript.Cyrillic));
            Assert.True(TokenLanguageFilter.Matches(neutral, ParakeetScript.Greek));
        }

        // Neutral punctuation must not relax Cyrillic/Greek ASCII-letter rejection.
        Assert.False(TokenLanguageFilter.Matches("a\u2019", ParakeetScript.Cyrillic));
        Assert.False(TokenLanguageFilter.Matches("«hello»", ParakeetScript.Greek));
    }

    [Fact]
    public async Task FrenchAppliesEnglishBlocklistWhileOtherLatinLanguagesDoNot()
    {
        var vocab = ParakeetVocabulary.FromTokens(
            [" the", " and", " so", " we", " was", " le", " et", " привет", "<unk>", "<blk>"],
            blankIndex: 9);
        var options = new ParakeetEngineOptions(
            ModelDirectory: "synthetic",
            RuntimeProviderKind.Cpu,
            ParakeetModelPack.Quantized,
            IntraOpThreads: 4);
        var backend = new FakeFrenchBlocklistBackend();
        using var engine = new ParakeetTranscriptionEngine(options, backend, vocab);

        var samples = new float[16_000];
        Array.Fill(samples, 0.05f, 1_000, 12_000);
        var audio = new CapturedAudio(DictationSessionId.Create(), samples, 16_000, Channels: 1);

        var french = await engine.TranscribeAsync(audio, "fr");
        Assert.Equal("le et", french.Text);
        Assert.Null(french.DetectedLanguage);
        Assert.Equal("fr", french.RecognitionLanguage);

        // Other Latin languages (#840) and auto/English must NOT suppress " the"/" and"/" so"/" we"/" was".
        foreach (var nonFrench in new[] { "de", "nl", "it", "es", "en", "auto" })
        {
            var result = await engine.TranscribeAsync(audio, nonFrench);
            Assert.Equal("the and", result.Text);
        }

        // Blank stays blank even when French blocklist is active, and non-Latin fallback picks Latin non-blocked.
        Assert.Equal(9, ParakeetVocabulary.SelectToken(
            [10f, 8f, 0f, 0f, 0f, 5f, 4f, 0f, 0f, 20f],
            vocab.BlankIndex,
            vocab.MaskFor(ParakeetScript.Latin),
            vocab.FrenchBlocklistMaskFor("fr")));
        Assert.Equal(5, ParakeetVocabulary.SelectToken(
            [10f, 8f, 0f, 0f, 0f, 5f, 4f, 25f, 22f, 0f],
            vocab.BlankIndex,
            vocab.MaskFor(ParakeetScript.Latin),
            vocab.FrenchBlocklistMaskFor("fr")));
    }

    [Fact]
    public void VocabularyLoadPrecomputesMasksAndExcludesBlankAndControlTokens()
    {
        var vocabPath = Path.Combine(Path.GetTempPath(), $"enviouswispr-script-vocab-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(vocabPath,
            [
                "\u2581hello 0",
                "\u2581привет 1",
                "\u2581γεια 2",
                "\u2581 3",
                ", 4",
                "<unk> 5",
                "<s> 6",
                "<blk> 7",
            ]);

            var vocab = ParakeetVocabulary.Load(vocabPath);
            var cyrillicMask = vocab.MaskFor(ParakeetScript.Cyrillic)!;
            var greekMask = vocab.MaskFor(ParakeetScript.Greek)!;

            Assert.Null(vocab.MaskFor(null));
            Assert.False(cyrillicMask[0]); // Latin
            Assert.True(cyrillicMask[1]);  // Cyrillic
            Assert.False(cyrillicMask[2]); // Greek
            Assert.True(cyrillicMask[3]);  // stripped boundary whitespace -> neutral
            Assert.True(cyrillicMask[4]);  // punctuation -> neutral
            Assert.False(cyrillicMask[5]); // <unk> excluded
            Assert.False(cyrillicMask[6]); // <s> excluded
            Assert.False(cyrillicMask[7]); // <blk> excluded

            Assert.False(greekMask[0]);
            Assert.False(greekMask[1]);
            Assert.True(greekMask[2]);
            Assert.False(greekMask[5]);
            Assert.False(greekMask[7]);

            // When top-1 is mismatched Latin (0) and <blk> (7) + <unk> (5) have higher logits than Cyrillic (1),
            // fallback picks Cyrillic (1) rather than substituting <blk> or <unk>.
            float[] logits = [10f, 1f, 0f, -5f, -5f, 8f, 7f, 9f];
            Assert.Equal(1, ParakeetVocabulary.SelectToken(logits, vocab.BlankIndex, cyrillicMask));

            // When top-1 IS <blk> (7), script pinning leaves <blk> untouched.
            float[] blankTopLogits = [5f, 1f, 0f, -5f, -5f, 0f, 0f, 20f];
            Assert.Equal(7, ParakeetVocabulary.SelectToken(blankTopLogits, vocab.BlankIndex, cyrillicMask));
        }
        finally
        {
            if (File.Exists(vocabPath))
            {
                File.Delete(vocabPath);
            }
        }
    }

    [Fact]
    public async Task ParakeetEnginePinsScriptPerTakeWithoutSettingDetectedLanguage()
    {
        var vocab = ParakeetVocabulary.FromTokens(
            [" hello", " привет", " γεια", "<unk>", "<blk>"],
            blankIndex: 4);
        var options = new ParakeetEngineOptions(
            ModelDirectory: "synthetic",
            RuntimeProviderKind.Cpu,
            ParakeetModelPack.Quantized,
            IntraOpThreads: 4);
        using var engine = new ParakeetTranscriptionEngine(options, new FakeScriptBackend(), vocab);
        Assert.IsAssignableFrom<ILanguageSelectableTranscriptionEngine>(engine);

        var samples = new float[16_000];
        Array.Fill(samples, 0.05f, 1_000, 12_000);
        var audio = new CapturedAudio(DictationSessionId.Create(), samples, 16_000, Channels: 1);

        var russian = await engine.TranscribeAsync(audio, "ru");
        Assert.Equal("привет", russian.Text);
        Assert.Null(russian.DetectedLanguage);
        Assert.Equal("ru", russian.RecognitionLanguage);

        var greek = await engine.TranscribeAsync(audio, "el");
        Assert.Equal("γεια", greek.Text);
        Assert.Null(greek.DetectedLanguage);
        Assert.Equal("el", greek.RecognitionLanguage);

        var english = await engine.TranscribeAsync(audio, "en-gb");
        Assert.Equal("hello", english.Text);
        Assert.Null(english.DetectedLanguage);
        Assert.Equal("en", english.RecognitionLanguage);

        var automatic = await engine.TranscribeAsync(audio, "auto");
        Assert.Equal("hello", automatic.Text);
        Assert.Null(automatic.DetectedLanguage);
        Assert.Equal("auto", automatic.RecognitionLanguage);

        var unsupported = await engine.TranscribeAsync(audio, "ja");
        Assert.Equal("hello", unsupported.Text);
        Assert.Null(unsupported.DetectedLanguage);
        Assert.Equal("auto", unsupported.RecognitionLanguage);
    }

    private sealed class FakeScriptBackend : ParakeetTranscriptionEngine.IParakeetBackend
    {
        public int EncoderDimension => 4;

        public int StateLeadingDimension => 2;

        public int StateDimension => 4;

        public ParakeetTranscriptionEngine.IParakeetRunContext CreateRunContext(CancellationToken cancellationToken) =>
            new FakeRunContext();

        public (float[] Features, long FeatureLength) Preprocess(
            float[] samples,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken) =>
            ([1f, 1f], 2);

        public (float[] EncodedChannelFirst, int EncodedLength) Encode(
            float[] features,
            long featureLength,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken) =>
            ([1f, 1f, 1f, 1f, 2f, 2f, 2f, 2f], 2);

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
            DecodeStep(encoderFrame, target, targetLength, state1, state2, vocabularySize, blankIndex, null, runContext, cancellationToken);

        public ParakeetTranscriptionEngine.ParakeetDecoderStepResult DecodeStep(
            float[] encoderFrame,
            int[] target,
            int[] targetLength,
            float[] state1,
            float[] state2,
            int vocabularySize,
            int blankIndex,
            bool[]? allowedTokenMask,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken)
        {
            // Frame 0: Latin top-1 (0), with Cyrillic (1) > Greek (2) or vice versa depending on target.
            // Frame 1: <blk> (4) is top-1 so blank must remain unpinned.
            float[] logits = target[0] == blankIndex
                ? [10f, 3f, 2f, 8f, 0f]
                : [0f, 0f, 0f, 0f, 12f];

            // Verify on Greek that index 2 beats non-Greek compatible fallback only when Greek is allowed.
            if (target[0] == blankIndex && allowedTokenMask is not null && allowedTokenMask[2] && !allowedTokenMask[1])
            {
                logits = [10f, 9f, 2f, 8f, 0f];
            }

            var token = ParakeetVocabulary.SelectToken(logits, blankIndex, allowedTokenMask);
            if (token != blankIndex)
            {
                state1[0] = 1f;
            }

            return new ParakeetTranscriptionEngine.ParakeetDecoderStepResult(token, Step: 1);
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

    private sealed class FakeFrenchBlocklistBackend : ParakeetTranscriptionEngine.IParakeetBackend
    {
        public int EncoderDimension => 4;

        public int StateLeadingDimension => 2;

        public int StateDimension => 4;

        public ParakeetTranscriptionEngine.IParakeetRunContext CreateRunContext(CancellationToken cancellationToken) =>
            new FakeRunContext();

        public (float[] Features, long FeatureLength) Preprocess(
            float[] samples,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken) =>
            ([1f, 1f], 2);

        public (float[] EncodedChannelFirst, int EncodedLength) Encode(
            float[] features,
            long featureLength,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken) =>
            ([1f, 1f, 1f, 1f, 2f, 2f, 2f, 2f], 2);

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
            // Vocab: 0=" the", 1=" and", 2=" so", 3=" we", 4=" was", 5=" le", 6=" et", 7=" привет", 8="<unk>", 9="<blk>".
            // Step 0 (target=blank): top1 is " the" (0), Latin fallback non-blocked is " le" (5).
            // Step 1 (target=0 or 5): top1 is " and" (1), Latin fallback non-blocked is " et" (6).
            float[] logits = target[0] == blankIndex
                ? [10f, 1f, 0f, 0f, 0f, 4f, 2f, 0f, 8f, 0f]
                : (target[0] is 0 or 5
                    ? [0f, 10f, 0f, 0f, 0f, 1f, 4f, 0f, 8f, 0f]
                    : [0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 12f]);

            var token = ParakeetVocabulary.SelectToken(logits, blankIndex, allowedTokenMask, blockedTokenMask);
            if (token != blankIndex)
            {
                state1[0] = token + 1;
            }

            return new ParakeetTranscriptionEngine.ParakeetDecoderStepResult(token, Step: 1);
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
