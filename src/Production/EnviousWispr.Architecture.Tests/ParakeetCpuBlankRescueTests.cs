using EnviousWispr.ASR;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class ParakeetCpuBlankRescueTests
{
    [Fact]
    public async Task VoicedSpeechBlankInt8EncoderRescuesWithFullPrecisionEncoderWithoutRecomputingFeatures()
    {
        var samples = BuildOlymposEnvelopeSamples();
        var backend = new FakeParakeetBackend
        {
            Fp32FallbackAvailable = true,
            PrimaryEncoderFrameSignal = 0.0f,
            Fp32EncoderFrameSignal = 1.0f,
        };
        using var engine = CreateEngine(backend);

        var first = await engine.TranscribeAsync(new CapturedAudio(
            DictationSessionId.Create(),
            samples,
            ParakeetTranscriptionEngine.RequiredSampleRate,
            Channels: 1));
        var second = await engine.TranscribeAsync(new CapturedAudio(
            DictationSessionId.Create(),
            samples,
            ParakeetTranscriptionEngine.RequiredSampleRate,
            Channels: 1));

        Assert.Equal("popular tourist area", first.Text);
        Assert.Equal("popular tourist area", second.Text);
        Assert.False(first.UsedFallback);
        Assert.Null(first.DegradedError);
        Assert.Equal(2, backend.PreprocessCalls);
        Assert.Equal(2, backend.PrimaryEncodeCalls);
        Assert.Equal(2, backend.Fp32FallbackEncodeCalls);
        AssertTimingsInBounds(
            first.TokenTimings,
            TimeSpan.FromSeconds(samples.Length / (double)ParakeetTranscriptionEngine.RequiredSampleRate));
    }

    [Fact]
    public async Task NonBlankInt8OutputNeverInvokesFullPrecisionEncoderOrTrimmedRetry()
    {
        var samples = BuildOlymposEnvelopeSamples();
        var backend = new FakeParakeetBackend
        {
            Fp32FallbackAvailable = true,
            PrimaryEncoderFrameSignal = 1.0f,
            Fp32EncoderFrameSignal = 1.0f,
        };
        using var engine = CreateEngine(backend);

        var result = await engine.TranscribeAsync(new CapturedAudio(
            DictationSessionId.Create(),
            samples,
            ParakeetTranscriptionEngine.RequiredSampleRate,
            Channels: 1));

        Assert.Equal("popular tourist area", result.Text);
        Assert.Equal(1, backend.PreprocessCalls);
        Assert.Equal(1, backend.PrimaryEncodeCalls);
        Assert.Equal(0, backend.Fp32FallbackEncodeCalls);
    }

    [Fact]
    public async Task SilenceAndTransientClickNeverInvokeFullPrecisionEncoderOrTrimmedRetry()
    {
        var digitalSilence = new float[32_000];
        var roomHissAndClick = new float[32_000];
        for (var i = 0; i < roomHissAndClick.Length; i++)
        {
            roomHissAndClick[i] = (i % 2 == 0) ? 0.0004f : -0.0004f;
        }

        // Single 40 ms transient click (2 frames < 8 voiced-frame minimum).
        Array.Fill(roomHissAndClick, 0.15f, 8_000, 640);

        var backend = new FakeParakeetBackend
        {
            Fp32FallbackAvailable = true,
            PrimaryEncoderFrameSignal = 0.0f,
            Fp32EncoderFrameSignal = 1.0f,
        };
        using var engine = CreateEngine(backend);

        var first = await engine.TranscribeAsync(new CapturedAudio(
            DictationSessionId.Create(),
            digitalSilence,
            ParakeetTranscriptionEngine.RequiredSampleRate,
            Channels: 1));
        var second = await engine.TranscribeAsync(new CapturedAudio(
            DictationSessionId.Create(),
            roomHissAndClick,
            ParakeetTranscriptionEngine.RequiredSampleRate,
            Channels: 1));

        Assert.Equal(string.Empty, first.Text);
        Assert.Equal(string.Empty, second.Text);
        Assert.Equal(2, backend.PreprocessCalls);
        Assert.Equal(2, backend.PrimaryEncodeCalls);
        Assert.Equal(0, backend.Fp32FallbackEncodeCalls);
    }

    [Fact]
    public async Task StoreInt8OnlyLayoutRetriesVoicedWindowWithoutSilenceAndAlignsTimingsToFullAudio()
    {
        var samples = BuildOlymposEnvelopeSamples();
        var backend = new FakeParakeetBackend
        {
            Fp32FallbackAvailable = false,
            EmitWordsOnlyWhenTrimmed = true,
        };
        using var engine = CreateEngine(backend);

        var result = await engine.TranscribeAsync(new CapturedAudio(
            DictationSessionId.Create(),
            samples,
            ParakeetTranscriptionEngine.RequiredSampleRate,
            Channels: 1));

        Assert.Equal("popular tourist area", result.Text);
        Assert.Equal(2, backend.PreprocessCalls);
        Assert.Equal(2, backend.PrimaryEncodeCalls);
        Assert.Equal(0, backend.Fp32FallbackEncodeCalls);
        Assert.True(backend.LastPreprocessedSampleCount < samples.Length);
        Assert.True(backend.LastPreprocessedSampleCount >= ParakeetTranscriptionEngine.MinimumPreprocessorInputSamples);

        var fullDuration = TimeSpan.FromSeconds(samples.Length / (double)ParakeetTranscriptionEngine.RequiredSampleRate);
        AssertTimingsInBounds(result.TokenTimings, fullDuration);
        Assert.True(
            result.TokenTimings![0].Start >= TimeSpan.FromMilliseconds(850),
            $"Expected trimmed-window token start after leading silence, got {result.TokenTimings[0].Start}.");
    }

    [Fact]
    public async Task CancellationDuringBlankRescuePropagatesOperationCanceled()
    {
        var samples = BuildOlymposEnvelopeSamples();
        using var cancellation = new CancellationTokenSource();
        var backend = new FakeParakeetBackend
        {
            Fp32FallbackAvailable = true,
            PrimaryEncoderFrameSignal = 0.0f,
            OnFp32Fallback = () => cancellation.Cancel(),
        };
        using var engine = CreateEngine(backend);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.TranscribeAsync(
                new CapturedAudio(
                    DictationSessionId.Create(),
                    samples,
                    ParakeetTranscriptionEngine.RequiredSampleRate,
                    Channels: 1),
                cancellation.Token));
    }

    [Fact]
    public async Task ModelDirectoryFileProbesDistinguishStoreInt8OnlyAndFullPacks()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"enviouswispr-parakeet-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var file in new[]
                     {
                         "nemo128.onnx",
                         "vocab.txt",
                         "encoder-model.int8.onnx",
                         "decoder_joint-model.int8.onnx",
                         "config.json",
                     })
            {
                await File.WriteAllBytesAsync(Path.Combine(directory, file), [1]);
            }

            Assert.True(ParakeetTranscriptionEngine.HasQuantizedModelPack(directory));
            Assert.False(ParakeetTranscriptionEngine.HasFullPrecisionEncoder(directory));

            await File.WriteAllBytesAsync(Path.Combine(directory, "encoder-model.onnx"), [1]);
            Assert.False(ParakeetTranscriptionEngine.HasFullPrecisionEncoder(directory));

            await File.WriteAllBytesAsync(Path.Combine(directory, "encoder-model.onnx.data"), [1]);
            Assert.True(ParakeetTranscriptionEngine.HasFullPrecisionEncoder(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ParakeetTranscriptionEngine CreateEngine(
        ParakeetTranscriptionEngine.IParakeetBackend backend)
    {
        var vocabPath = Path.Combine(Path.GetTempPath(), $"enviouswispr-vocab-{Guid.NewGuid():N}.txt");
        ParakeetVocabulary vocabulary;
        try
        {
            File.WriteAllLines(vocabPath,
            [
                "\u2581popular 0",
                "\u2581tourist 1",
                "\u2581area 2",
                "<blk> 3",
            ]);
            vocabulary = ParakeetVocabulary.Load(vocabPath);
        }
        finally
        {
            if (File.Exists(vocabPath))
            {
                File.Delete(vocabPath);
            }
        }

        var options = new ParakeetEngineOptions(
            ModelDirectory: "synthetic",
            RuntimeProviderKind.Cpu,
            ParakeetModelPack.Quantized,
            IntraOpThreads: 8);
        return new ParakeetTranscriptionEngine(options, backend, vocabulary);
    }

    private static float[] BuildOlymposEnvelopeSamples()
    {
        // Mirrors the 73,728-sample (4.608s) Olympos recording shape:
        // 1.00s leading silence, 2.75s voiced speech (~0.045 RMS), 0.858s trailing silence.
        var samples = new float[73_728];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (i % 2 == 0) ? 0.0001f : -0.0001f;
        }

        for (var i = 16_000; i < 60_000; i++)
        {
            samples[i] = 0.065f * MathF.Sin(i * 0.19f);
        }

        return samples;
    }

    private static void AssertTimingsInBounds(
        IReadOnlyList<TranscriptTokenTiming>? timings,
        TimeSpan fullAudioDuration)
    {
        Assert.NotNull(timings);
        Assert.Equal(3, timings.Count);
        var previousStart = TimeSpan.Zero;
        foreach (var timing in timings)
        {
            Assert.False(string.IsNullOrWhiteSpace(timing.Text));
            Assert.True(timing.Start >= TimeSpan.Zero);
            Assert.True(timing.End >= timing.Start);
            Assert.True(timing.End <= fullAudioDuration);
            Assert.True(timing.Start >= previousStart);
            previousStart = timing.Start;
        }
    }

    private sealed class FakeParakeetBackend : ParakeetTranscriptionEngine.IParakeetBackend
    {
        public int EncoderDimension => 4;

        public int StateLeadingDimension => 2;

        public int StateDimension => 4;

        public bool Fp32FallbackAvailable { get; init; }

        public float PrimaryEncoderFrameSignal { get; init; }

        public float Fp32EncoderFrameSignal { get; init; } = 1.0f;

        public bool EmitWordsOnlyWhenTrimmed { get; init; }

        public Action? OnFp32Fallback { get; init; }

        public int PreprocessCalls { get; private set; }

        public int PrimaryEncodeCalls { get; private set; }

        public int Fp32FallbackEncodeCalls { get; private set; }

        public int LastPreprocessedSampleCount { get; private set; }

        public ParakeetTranscriptionEngine.IParakeetRunContext CreateRunContext(CancellationToken cancellationToken) =>
            new FakeRunContext();

        public (float[] Features, long FeatureLength) Preprocess(
            float[] samples,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreprocessCalls++;
            LastPreprocessedSampleCount = samples.Length;
            var signal = EmitWordsOnlyWhenTrimmed && samples.Length < 70_000 ? 1.0f : PrimaryEncoderFrameSignal;
            return ([signal, signal, signal], 3);
        }

        public (float[] EncodedChannelFirst, int EncodedLength) Encode(
            float[] features,
            long featureLength,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrimaryEncodeCalls++;
            return BuildEncodedFrames(features[0], (int)featureLength);
        }

        public bool TryEncodeWithFullPrecisionFallback(
            float[] features,
            long featureLength,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken,
            out float[] encodedChannelFirst,
            out int encodedLength)
        {
            OnFp32Fallback?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (!Fp32FallbackAvailable)
            {
                encodedChannelFirst = [];
                encodedLength = 0;
                return false;
            }

            Fp32FallbackEncodeCalls++;
            (encodedChannelFirst, encodedLength) = BuildEncodedFrames(Fp32EncoderFrameSignal, (int)featureLength);
            return true;
        }

        public ParakeetTranscriptionEngine.ParakeetDecoderStepResult DecodeStep(
            float[] encoderFrame,
            int[] target,
            int[] targetLength,
            float[] state1,
            float[] state2,
            ParakeetTranscriptionEngine.IParakeetRunContext runContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 4 vocab tokens (0=" popular", 1=" tourist", 2=" area", 3="<blk>") + 2 step durations (0, 1).
            var output = new float[6];
            if (encoderFrame[0] <= 0.5f)
            {
                output[3] = 5.0f; // <blk>
                output[5] = 2.0f; // step = 1
            }
            else
            {
                var nextToken = target[0] switch
                {
                    3 => 0,
                    0 => 1,
                    1 => 2,
                    _ => 3,
                };
                output[nextToken] = 5.0f;
                output[5] = 2.0f; // step = 1 frame per word
            }

            return new ParakeetTranscriptionEngine.ParakeetDecoderStepResult(
                output,
                (float[])state1.Clone(),
                (float[])state2.Clone());
        }

        public void Dispose()
        {
        }

        private (float[] Encoded, int Length) BuildEncodedFrames(float signal, int frameCount)
        {
            var raw = new float[EncoderDimension * frameCount];
            Array.Fill(raw, signal);
            return (raw, frameCount);
        }

        private sealed class FakeRunContext : ParakeetTranscriptionEngine.IParakeetRunContext
        {
            public void Dispose()
            {
            }
        }
    }
}
