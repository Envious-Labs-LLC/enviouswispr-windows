using System.Text;
using System.Text.RegularExpressions;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EnviousWispr.ASR;

public sealed class ParakeetTranscriptionEngine : ITranscriptionEngine, IDisposable
{
    public const string ModelId = ParakeetModelIds.Final;
    public const int RequiredSampleRate = 16_000;

    // The bundled nemo128 preprocessor reflects 256 samples on each side. Reflection needs
    // more input samples than that pad; this is not a minimum duration for recognizing speech.
    // The artifact digest is pinned by ParakeetInputContractTests so an update revisits this rule.
    internal const int MinimumPreprocessorInputSamples = 257;

    private const string PreprocessorFileName = "nemo128.onnx";
    private const string VocabularyFileName = "vocab.txt";
    private const string FullPrecisionEncoderFileName = "encoder-model.onnx";
    private const string FullPrecisionEncoderDataFileName = "encoder-model.onnx.data";
    private const string QuantizedEncoderFileName = "encoder-model.int8.onnx";
    private const string FullPrecisionDecoderFileName = "decoder_joint-model.onnx";
    private const string QuantizedDecoderFileName = "decoder_joint-model.int8.onnx";

    private const double MinimumVoicedFrameRms = 0.002;
    private const double StrongContinuousSpeechFrameRms = 0.015;
    private const int MinimumVoicedFrames = 8;
    private const int MinimumSustainedSegmentVoicedFrames = 4;
    private const int VoicedTrimPaddingSamples = 1_600;

    private static readonly string[] QuantizedRequiredFiles =
    [
        PreprocessorFileName,
        VocabularyFileName,
        QuantizedEncoderFileName,
        QuantizedDecoderFileName,
    ];

    private static readonly string[] FullPrecisionEncoderRequiredFiles =
    [
        FullPrecisionEncoderFileName,
        FullPrecisionEncoderDataFileName,
    ];

    private static readonly string[] PreprocessorOutputs = ["features", "features_lens"];
    private static readonly string[] EncoderOutputs = ["outputs", "encoded_lengths"];
    private static readonly string[] DecoderOutputs = ["outputs", "output_states_1", "output_states_2"];
    private static readonly Regex SpacePattern = new(
        @"\A\s|\s\B|(\s)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IParakeetBackend _backend;
    private readonly ParakeetVocabulary _vocabulary;
    private readonly SemaphoreSlim _transcriptionGate = new(1, 1);
    private readonly int _maximumTokensPerStep;
    private readonly int _encoderDimension;
    private readonly int _stateDimension;
    private readonly int _stateLeadingDimension;
    private bool _disposed;

    public ParakeetTranscriptionEngine(ParakeetEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        Provider = options.Provider;
        ModelPack = options.ModelPack;
        EngineId = $"{ModelId}:{Provider.ToString().ToLowerInvariant()}";

        ConfigureCudaRuntime(options);
        var (backend, vocabulary) = OnnxParakeetBackend.Create(options);
        _backend = backend;
        _vocabulary = vocabulary;
        _encoderDimension = backend.EncoderDimension;
        _stateLeadingDimension = backend.StateLeadingDimension;
        _stateDimension = backend.StateDimension;
        _maximumTokensPerStep = options.MaximumTokensPerStep;
    }

    internal ParakeetTranscriptionEngine(
        ParakeetEngineOptions options,
        IParakeetBackend backend,
        ParakeetVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ValidateOptions(options);
        Provider = options.Provider;
        ModelPack = options.ModelPack;
        EngineId = $"{ModelId}:{Provider.ToString().ToLowerInvariant()}";
        _backend = backend;
        _vocabulary = vocabulary;
        _encoderDimension = backend.EncoderDimension;
        _stateLeadingDimension = backend.StateLeadingDimension;
        _stateDimension = backend.StateDimension;
        _maximumTokensPerStep = options.MaximumTokensPerStep;
    }

    public string EngineId { get; }

    public RuntimeProviderKind Provider { get; }

    public ParakeetModelPack ModelPack { get; }

    public static bool HasQuantizedModelPack(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        return AllNonEmpty(Path.GetFullPath(modelDirectory), QuantizedRequiredFiles);
    }

    public static bool HasFullPrecisionEncoder(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        return AllNonEmpty(Path.GetFullPath(modelDirectory), FullPrecisionEncoderRequiredFiles);
    }

    public async Task<Transcript> TranscribeAsync(
        CapturedAudio audio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (audio.Outcome == AudioCaptureOutcome.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (audio.SampleRate != RequiredSampleRate || audio.Channels != 1)
        {
            throw CreateFailure(AppErrorCode.AudioFormatUnsupported);
        }

        if (audio.Samples.IsEmpty)
        {
            return new Transcript(audio.SessionId, string.Empty, EngineId, []);
        }

        ValidateInputLength(audio.Samples.Length, cancellationToken);
        await _transcriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(
                () => TranscribeCore(audio, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _transcriptionGate.Release();
        }
    }

    internal static void ValidateInputLength(int sampleCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        if (sampleCount is > 0 and < MinimumPreprocessorInputSamples)
        {
            // The same input cannot be retried. A streaming caller may instead submit the whole
            // recording. No native work has been admitted when this particular error is returned.
            throw new TranscriptionEngineException(new AppError(
                AppErrorCode.TranscriptionInputTooShort,
                AppErrorStage.FinalAsr,
                CanRetry: false));
        }
    }

    internal static VoicedSpeechAnalysis AnalyzeVoicedSpeech(ReadOnlySpan<float> samples)
    {
        var frameLength = RequiredSampleRate * SpeechSegmenter.FrameMilliseconds / 1000;
        if (frameLength <= 0 || samples.Length < frameLength)
        {
            return new VoicedSpeechAnalysis(false, 0, samples.Length);
        }

        var frameCount = samples.Length / frameLength;
        var frameRms = new double[frameCount];
        var voicedFrames = 0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            var rms = RootMeanSquare(samples.Slice(frame * frameLength, frameLength));
            frameRms[frame] = rms;
            if (rms >= MinimumVoicedFrameRms)
            {
                voicedFrames++;
            }
        }

        if (voicedFrames < MinimumVoicedFrames)
        {
            return new VoicedSpeechAnalysis(false, 0, samples.Length);
        }

        var segments = new SpeechSegmenter(RequiredSampleRate, TimeSpan.FromMilliseconds(200)).Segment(samples);
        SpeechSegment? firstSpeech = null;
        SpeechSegment? lastSpeech = null;
        foreach (var segment in segments)
        {
            if (!segment.IsSpeech)
            {
                continue;
            }

            var startFrame = Math.Clamp(segment.StartSample / frameLength, 0, frameCount);
            var endFrame = Math.Clamp(segment.EndSample / frameLength, startFrame, frameCount);
            var segmentVoicedFrames = 0;
            for (var frame = startFrame; frame < endFrame; frame++)
            {
                if (frameRms[frame] >= MinimumVoicedFrameRms)
                {
                    segmentVoicedFrames++;
                }
            }

            // Ignore brief hotkey or microphone clicks (<80 ms voiced) so a leading or trailing
            // transient cannot pin the trim window to the full recording or masquerade as speech.
            if (segmentVoicedFrames < MinimumSustainedSegmentVoicedFrames)
            {
                continue;
            }

            firstSpeech ??= segment;
            lastSpeech = segment;
        }

        if (firstSpeech is { } first && lastSpeech is { } last)
        {
            var trimStart = Math.Max(0, first.StartSample - VoicedTrimPaddingSamples);
            var trimEnd = Math.Min(samples.Length, last.EndSample + VoicedTrimPaddingSamples);
            return new VoicedSpeechAnalysis(true, trimStart, trimEnd - trimStart);
        }

        int? firstStrongRunStart = null;
        var lastStrongRunEnd = 0;
        var currentStrongRunStart = 0;
        var currentStrongRunLength = 0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            if (frameRms[frame] >= StrongContinuousSpeechFrameRms)
            {
                if (currentStrongRunLength == 0)
                {
                    currentStrongRunStart = frame;
                }

                currentStrongRunLength++;
                if (currentStrongRunLength >= MinimumSustainedSegmentVoicedFrames)
                {
                    firstStrongRunStart ??= currentStrongRunStart;
                    lastStrongRunEnd = frame + 1;
                }
            }
            else
            {
                currentStrongRunLength = 0;
            }
        }

        if (firstStrongRunStart is { } strongStartFrame)
        {
            var rawStart = strongStartFrame * frameLength;
            var rawEnd = lastStrongRunEnd == frameCount ? samples.Length : lastStrongRunEnd * frameLength;
            var trimStart = Math.Max(0, rawStart - VoicedTrimPaddingSamples);
            var trimEnd = Math.Min(samples.Length, rawEnd + VoicedTrimPaddingSamples);
            return new VoicedSpeechAnalysis(true, trimStart, trimEnd - trimStart);
        }

        return new VoicedSpeechAnalysis(false, 0, samples.Length);
    }

    private Transcript TranscribeCore(CapturedAudio audio, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var runContext = _backend.CreateRunContext(cancellationToken);

        var samples = audio.Samples.ToArray();
        var (features, featureLength) = _backend.Preprocess(samples, runContext, cancellationToken);
        var (encodedRaw, encodedLength) = _backend.Encode(features, featureLength, runContext, cancellationToken);
        var encoded = TransposeEncoderOutput(encodedRaw);
        var decoded = Decode(encoded, encodedLength, runContext, cancellationToken);

        var timingStartSample = 0;
        var timingSampleCount = samples.Length;
        var timingEncodedLength = encodedLength;

        // The bundled istupakov INT8 encoder uses per-tensor DynamicQuantizeLinear + ConvInteger across
        // FastConformer layers, which can collapse all TDT steps to <blk> on voiced utterances framed
        // by silence or activation outliers (#268). When voiced speech produced zero tokens on CPU INT8,
        // rescue first with the QDQ-free FP32 encoder on the already-computed mel features when present,
        // or retry once on the silence-trimmed voiced span when only the INT8 pack is installed.
        if (decoded.Count == 0 &&
            Provider == RuntimeProviderKind.Cpu &&
            ModelPack == ParakeetModelPack.Quantized)
        {
            var speech = AnalyzeVoicedSpeech(samples);
            if (speech.HasVoicedSpeech)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (_backend.TryEncodeWithFullPrecisionFallback(
                            features,
                            featureLength,
                            runContext,
                            cancellationToken,
                            out var fp32EncodedRaw,
                            out var fp32EncodedLength) &&
                        fp32EncodedLength > 0 &&
                        fp32EncodedRaw.Length >= (long)_encoderDimension * fp32EncodedLength &&
                        fp32EncodedRaw.Length % _encoderDimension == 0)
                    {
                        var fp32Encoded = TransposeEncoderOutput(fp32EncodedRaw);
                        var fp32Decoded = Decode(fp32Encoded, fp32EncodedLength, runContext, cancellationToken);
                        if (fp32Decoded.Count > 0)
                        {
                            decoded = fp32Decoded;
                            timingEncodedLength = fp32EncodedLength;
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
                    // Rescue is best-effort: a faulty optional sidecar or decode step must not turn a
                    // completed zero-token pass into a failed dictation, and trimmed retry can still run.
                }

                if (decoded.Count == 0 &&
                    speech.TrimmedLength >= MinimumPreprocessorInputSamples &&
                    speech.TrimmedLength < samples.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var trimmedSamples = samples.AsSpan(speech.TrimmedStartSample, speech.TrimmedLength).ToArray();
                        var (trimmedFeatures, trimmedFeatureLength) = _backend.Preprocess(
                            trimmedSamples,
                            runContext,
                            cancellationToken);
                        var (trimmedEncodedRaw, trimmedEncodedLength) = _backend.Encode(
                            trimmedFeatures,
                            trimmedFeatureLength,
                            runContext,
                            cancellationToken);
                        if (trimmedEncodedLength > 0 &&
                            trimmedEncodedRaw.Length >= (long)_encoderDimension * trimmedEncodedLength &&
                            trimmedEncodedRaw.Length % _encoderDimension == 0)
                        {
                            var trimmedEncoded = TransposeEncoderOutput(trimmedEncodedRaw);
                            var trimmedDecoded = Decode(
                                trimmedEncoded,
                                trimmedEncodedLength,
                                runContext,
                                cancellationToken);
                            if (trimmedDecoded.Count > 0)
                            {
                                decoded = trimmedDecoded;
                                timingStartSample = speech.TrimmedStartSample;
                                timingSampleCount = speech.TrimmedLength;
                                timingEncodedLength = trimmedEncodedLength;
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
                        // Leave the original empty transcript intact when the best-effort retry fails.
                    }
                }
            }
        }

        var text = TokensToText(decoded);
        var fullDuration = TimeSpan.FromSeconds(samples.Length / (double)RequiredSampleRate);
        var timings = CreateTimings(
            decoded,
            timingEncodedLength,
            fullDuration,
            timingStartSample,
            timingSampleCount);
        return new Transcript(audio.SessionId, text, EngineId, timings);
    }

    private float[] TransposeEncoderOutput(float[] encoded)
    {
        var frameCount = encoded.Length / _encoderDimension;
        var transposed = new float[encoded.Length];
        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var dimension = 0; dimension < _encoderDimension; dimension++)
            {
                transposed[(frame * _encoderDimension) + dimension] =
                    encoded[(dimension * frameCount) + frame];
            }
        }

        return transposed;
    }

    private List<DecodedToken> Decode(
        float[] encoded,
        int encodedLength,
        IParakeetRunContext runContext,
        CancellationToken cancellationToken)
    {
        var decoded = new List<DecodedToken>();
        var stateLength = _stateLeadingDimension * _stateDimension;
        var state1 = new float[stateLength];
        var state2 = new float[stateLength];
        var encoderFrame = new float[_encoderDimension];
        var target = new[] { _vocabulary.BlankIndex };
        var targetLength = new[] { 1 };
        var frame = 0;
        var emittedAtFrame = 0;

        while (frame < encodedLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Copy(encoded, frame * _encoderDimension, encoderFrame, 0, _encoderDimension);
            target[0] = decoded.Count == 0 ? _vocabulary.BlankIndex : decoded[^1].Id;
            var stepResult = _backend.DecodeStep(
                encoderFrame,
                target,
                targetLength,
                state1,
                state2,
                _vocabulary.Size,
                _vocabulary.BlankIndex,
                runContext,
                cancellationToken);

            var token = stepResult.Token;
            var step = stepResult.Step;
            if (token != _vocabulary.BlankIndex)
            {
                decoded.Add(new DecodedToken(token, frame));
                emittedAtFrame++;
            }

            if (step > 0)
            {
                frame += step;
                emittedAtFrame = 0;
            }
            else if (token == _vocabulary.BlankIndex || emittedAtFrame >= _maximumTokensPerStep)
            {
                frame++;
                emittedAtFrame = 0;
            }
        }

        return decoded;
    }

    private string TokensToText(IReadOnlyList<DecodedToken> tokens)
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

        return SpacePattern.Replace(text.ToString(), match => match.Groups[1].Success ? " " : string.Empty);
    }

    private TranscriptTokenTiming[] CreateTimings(
        IReadOnlyList<DecodedToken> tokens,
        int encodedLength,
        TimeSpan fullAudioDuration,
        int windowStartSample,
        int windowSampleCount)
    {
        if (tokens.Count == 0 || encodedLength <= 0)
        {
            return [];
        }

        var windowDuration = windowStartSample == 0 &&
            TimeSpan.FromSeconds(windowSampleCount / (double)RequiredSampleRate) == fullAudioDuration
            ? fullAudioDuration
            : TimeSpan.FromSeconds(windowSampleCount / (double)RequiredSampleRate);
        var startOffset = windowStartSample == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(windowStartSample / (double)RequiredSampleRate);

        var result = new TranscriptTokenTiming[tokens.Count];
        for (var index = 0; index < tokens.Count; index++)
        {
            var currentFrame = tokens[index].Frame;
            var nextFrame = index + 1 < tokens.Count
                ? Math.Max(currentFrame + 1, tokens[index + 1].Frame)
                : encodedLength;
            var rawStart = startOffset + ScaleTime(windowDuration, currentFrame, encodedLength);
            var rawEnd = startOffset + ScaleTime(windowDuration, Math.Min(nextFrame, encodedLength), encodedLength);
            var clampedEnd = rawEnd > fullAudioDuration ? fullAudioDuration : rawEnd;
            var clampedStart = rawStart > clampedEnd ? clampedEnd : rawStart;
            result[index] = new TranscriptTokenTiming(
                _vocabulary.Tokens[tokens[index].Id],
                clampedStart,
                clampedEnd);
        }

        return result;
    }

    private static TimeSpan ScaleTime(TimeSpan duration, int frame, int frameCount) =>
        TimeSpan.FromTicks(checked((long)(duration.Ticks * (frame / (double)frameCount))));

    private static int IndexOfMaximum(ReadOnlySpan<float> values)
    {
        var bestIndex = 0;
        var bestValue = float.NegativeInfinity;
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] > bestValue)
            {
                bestValue = values[index];
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private static double RootMeanSquare(ReadOnlySpan<float> frame)
    {
        double sumSquares = 0;
        foreach (var sample in frame)
        {
            sumSquares += (double)sample * sample;
        }

        return Math.Sqrt(sumSquares / frame.Length);
    }

    private static bool AllNonEmpty(string directory, IEnumerable<string> fileNames) =>
        fileNames.All(fileName =>
        {
            var path = Path.Combine(directory, fileName);
            return File.Exists(path) && new FileInfo(path).Length > 0;
        });

    private static SessionOptions CreateSessionOptions(ParakeetEngineOptions options)
    {
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = options.IntraOpThreads,
            InterOpNumThreads = options.InterOpThreads,
        };
        if (options.Provider == RuntimeProviderKind.Cuda)
        {
            sessionOptions.AppendExecutionProvider_CUDA(options.CudaDeviceId);
        }

        return sessionOptions;
    }

    private static void ConfigureCudaRuntime(ParakeetEngineOptions options)
    {
        if (options.Provider != RuntimeProviderKind.Cuda ||
            string.IsNullOrWhiteSpace(options.CudaRuntimeDirectory))
        {
            return;
        }

        // THE SAME MECHANISM THE WORKER USES, NOT ENVIRONMENT VARIABLES, which a packaged process never searches for a DLL.
        // Kept here as well so a harness that runs this engine in its own process gets the same search.
        if (NativeRuntimeSearchPath.Configure(options.CudaRuntimeDirectory) != NativeRuntimeSearchPathOutcome.Added)
        {
            throw CreateFailure(AppErrorCode.RuntimeProviderUnavailable);
        }
    }

    private static void ValidateOptions(ParakeetEngineOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.IntraOpThreads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.InterOpThreads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumTokensPerStep, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.CudaDeviceId);
        if (options.Provider == RuntimeProviderKind.DirectMl)
        {
            throw CreateFailure(AppErrorCode.RuntimeProviderIncompatible);
        }

        if (options.Provider == RuntimeProviderKind.Cuda &&
            options.ModelPack != ParakeetModelPack.FullPrecision)
        {
            throw CreateFailure(AppErrorCode.RuntimeProviderIncompatible);
        }
    }

    private static bool IsModelOrRuntimeFailure(Exception exception) =>
        exception is OnnxRuntimeException or IOException or UnauthorizedAccessException or
            KeyNotFoundException or InvalidDataException;

    internal static TranscriptionEngineException CreateFailure(
        AppErrorCode code,
        Exception? innerException = null) =>
        CreateFailure(code, AppErrorStage.FinalAsr, innerException);

    internal static TranscriptionEngineException CreateFailure(
        AppErrorCode code,
        AppErrorStage stage,
        Exception? innerException = null) => new(
        new AppError(code, stage, CanRetry: true),
        innerException);

    internal static TranscriptionEngineException CreateStageFailure(
        ParakeetOnnxStage stage,
        Exception? innerException = null) =>
        stage switch
        {
            ParakeetOnnxStage.Preprocessor => CreateFailure(
                AppErrorCode.TranscriptionPreprocessorFailed,
                AppErrorStage.AsrPreprocessor,
                innerException),
            ParakeetOnnxStage.Encoder => CreateFailure(
                AppErrorCode.TranscriptionEncoderFailed,
                AppErrorStage.AsrEncoder,
                innerException),
            ParakeetOnnxStage.Decoder => CreateFailure(
                AppErrorCode.TranscriptionDecoderFailed,
                AppErrorStage.AsrDecoder,
                innerException),
            _ => CreateFailure(AppErrorCode.TranscriptionFailed, AppErrorStage.FinalAsr, innerException),
        };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backend.Dispose();
        _transcriptionGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private readonly record struct DecodedToken(int Id, int Frame);

    internal readonly record struct VoicedSpeechAnalysis(
        bool HasVoicedSpeech,
        int TrimmedStartSample,
        int TrimmedLength);

    internal readonly record struct ParakeetDecoderStepResult(
        int Token,
        int Step);

    internal interface IParakeetRunContext : IDisposable;

    internal interface IParakeetBackend : IDisposable
    {
        int EncoderDimension { get; }

        int StateLeadingDimension { get; }

        int StateDimension { get; }

        IParakeetRunContext CreateRunContext(CancellationToken cancellationToken);

        (float[] Features, long FeatureLength) Preprocess(
            float[] samples,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken);

        (float[] EncodedChannelFirst, int EncodedLength) Encode(
            float[] features,
            long featureLength,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken);

        bool TryEncodeWithFullPrecisionFallback(
            float[] features,
            long featureLength,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken,
            out float[] encodedChannelFirst,
            out int encodedLength);

        ParakeetDecoderStepResult DecodeStep(
            float[] encoderFrame,
            int[] target,
            int[] targetLength,
            float[] state1,
            float[] state2,
            int vocabularySize,
            int blankIndex,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken);
    }

    private sealed class OnnxParakeetBackend : IParakeetBackend
    {
        private readonly ParakeetEngineOptions _options;
        private readonly string _modelDirectory;
        private readonly InferenceSession _preprocessor;
        private readonly InferenceSession _encoder;
        private readonly InferenceSession _decoder;
        private InferenceSession? _fp32FallbackEncoder;
        private bool _fp32FallbackUnavailable;

        private OnnxParakeetBackend(
            ParakeetEngineOptions options,
            string modelDirectory,
            InferenceSession preprocessor,
            InferenceSession encoder,
            InferenceSession decoder,
            int encoderDimension,
            int stateLeadingDimension,
            int stateDimension)
        {
            _options = options;
            _modelDirectory = modelDirectory;
            _preprocessor = preprocessor;
            _encoder = encoder;
            _decoder = decoder;
            EncoderDimension = encoderDimension;
            StateLeadingDimension = stateLeadingDimension;
            StateDimension = stateDimension;
        }

        public int EncoderDimension { get; }

        public int StateLeadingDimension { get; }

        public int StateDimension { get; }

        public static (OnnxParakeetBackend Backend, ParakeetVocabulary Vocabulary) Create(ParakeetEngineOptions options)
        {
            InferenceSession? preprocessor = null;
            InferenceSession? encoder = null;
            InferenceSession? decoder = null;
            try
            {
                using var sessionOptions = CreateSessionOptions(options);
                var modelDirectory = Path.GetFullPath(options.ModelDirectory);
                var encoderFile = options.ModelPack == ParakeetModelPack.FullPrecision
                    ? FullPrecisionEncoderFileName
                    : QuantizedEncoderFileName;
                var decoderFile = options.ModelPack == ParakeetModelPack.FullPrecision
                    ? FullPrecisionDecoderFileName
                    : QuantizedDecoderFileName;

                preprocessor = new InferenceSession(Path.Combine(modelDirectory, PreprocessorFileName), sessionOptions);
                encoder = new InferenceSession(Path.Combine(modelDirectory, encoderFile), sessionOptions);
                decoder = new InferenceSession(Path.Combine(modelDirectory, decoderFile), sessionOptions);
                var vocabulary = ParakeetVocabulary.Load(Path.Combine(modelDirectory, VocabularyFileName));

                var decoderInputs = decoder.InputMetadata;
                var encoderDimension = checked((int)decoderInputs["encoder_outputs"].Dimensions[1]);
                var stateLeadingDimension = checked((int)decoderInputs["input_states_1"].Dimensions[0]);
                var stateDimension = checked((int)decoderInputs["input_states_1"].Dimensions[2]);

                return (
                    new OnnxParakeetBackend(
                        options,
                        modelDirectory,
                        preprocessor,
                        encoder,
                        decoder,
                        encoderDimension,
                        stateLeadingDimension,
                        stateDimension),
                    vocabulary);
            }
            catch (Exception exception) when (IsModelOrRuntimeFailure(exception))
            {
                preprocessor?.Dispose();
                encoder?.Dispose();
                decoder?.Dispose();
                throw CreateFailure(
                    options.Provider == RuntimeProviderKind.Cpu
                        ? AppErrorCode.TranscriptionFailed
                        : AppErrorCode.RuntimeProviderUnavailable,
                    exception);
            }
        }

        public IParakeetRunContext CreateRunContext(CancellationToken cancellationToken) =>
            new OnnxRunContext(cancellationToken);

        public (float[] Features, long FeatureLength) Preprocess(
            float[] samples,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken)
        {
            var options = ((OnnxRunContext)runContext).Options;
            try
            {
                using var preprocessorResult = _preprocessor.Run(
                    [
                        NamedOnnxValue.CreateFromTensor(
                            "waveforms",
                            new DenseTensor<float>(samples.AsMemory(), [1, samples.Length])),
                        NamedOnnxValue.CreateFromTensor(
                            "waveforms_lens",
                            new DenseTensor<long>(new long[] { samples.Length }, [1])),
                    ],
                    PreprocessorOutputs,
                    options);
                cancellationToken.ThrowIfCancellationRequested();

                var features = preprocessorResult[0].AsEnumerable<float>().ToArray();
                var featureLength = preprocessorResult[1].AsEnumerable<long>().Single();
                return (features, featureLength);
            }
            catch (OnnxRuntimeException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Local transcription was cancelled.",
                    exception,
                    cancellationToken);
            }
            catch (OnnxRuntimeException exception)
            {
                throw CreateStageFailure(ParakeetOnnxStage.Preprocessor, exception);
            }
        }

        public (float[] EncodedChannelFirst, int EncodedLength) Encode(
            float[] features,
            long featureLength,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken)
        {
            var options = ((OnnxRunContext)runContext).Options;
            try
            {
                using var encoderResult = _encoder.Run(
                    [
                        NamedOnnxValue.CreateFromTensor(
                            "audio_signal",
                            new DenseTensor<float>(features.AsMemory(), [1, 128, checked((int)featureLength)])),
                        NamedOnnxValue.CreateFromTensor(
                            "length",
                            new DenseTensor<long>(new long[] { featureLength }, [1])),
                    ],
                    EncoderOutputs,
                    options);
                cancellationToken.ThrowIfCancellationRequested();

                var encoded = encoderResult[0].AsEnumerable<float>().ToArray();
                var encodedLength = checked((int)encoderResult[1].AsEnumerable<long>().Single());
                return (encoded, encodedLength);
            }
            catch (OnnxRuntimeException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Local transcription was cancelled.",
                    exception,
                    cancellationToken);
            }
            catch (OnnxRuntimeException exception)
            {
                throw CreateStageFailure(ParakeetOnnxStage.Encoder, exception);
            }
        }

        public bool TryEncodeWithFullPrecisionFallback(
            float[] features,
            long featureLength,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken,
            out float[] encodedChannelFirst,
            out int encodedLength)
        {
            encodedChannelFirst = [];
            encodedLength = 0;
            if (_fp32FallbackUnavailable ||
                _options.Provider != RuntimeProviderKind.Cpu ||
                _options.ModelPack != ParakeetModelPack.Quantized ||
                !HasFullPrecisionEncoder(_modelDirectory))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var options = ((OnnxRunContext)runContext).Options;
            try
            {
                if (_fp32FallbackEncoder is null)
                {
                    using var sessionOptions = CreateSessionOptions(_options);
                    _fp32FallbackEncoder = new InferenceSession(
                        Path.Combine(_modelDirectory, FullPrecisionEncoderFileName),
                        sessionOptions);
                }

                cancellationToken.ThrowIfCancellationRequested();
                using var encoderResult = _fp32FallbackEncoder.Run(
                    [
                        NamedOnnxValue.CreateFromTensor(
                            "audio_signal",
                            new DenseTensor<float>(features.AsMemory(), [1, 128, checked((int)featureLength)])),
                        NamedOnnxValue.CreateFromTensor(
                            "length",
                            new DenseTensor<long>(new long[] { featureLength }, [1])),
                    ],
                    EncoderOutputs,
                    options);
                cancellationToken.ThrowIfCancellationRequested();

                encodedChannelFirst = encoderResult[0].AsEnumerable<float>().ToArray();
                encodedLength = checked((int)encoderResult[1].AsEnumerable<long>().Single());
                if (encodedLength <= 0 ||
                    encodedChannelFirst.Length < (long)EncoderDimension * encodedLength ||
                    encodedChannelFirst.Length % EncoderDimension != 0)
                {
                    _fp32FallbackEncoder.Dispose();
                    _fp32FallbackEncoder = null;
                    _fp32FallbackUnavailable = true;
                    encodedChannelFirst = [];
                    encodedLength = 0;
                    return false;
                }

                return true;
            }
            catch (OnnxRuntimeException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Local transcription was cancelled.",
                    exception,
                    cancellationToken);
            }
            catch (Exception exception) when (
                IsModelOrRuntimeFailure(exception) ||
                exception is InvalidOperationException or OverflowException)
            {
                _fp32FallbackEncoder?.Dispose();
                _fp32FallbackEncoder = null;
                _fp32FallbackUnavailable = true;
                encodedChannelFirst = [];
                encodedLength = 0;
                return false;
            }
        }

        public ParakeetDecoderStepResult DecodeStep(
            float[] encoderFrame,
            int[] target,
            int[] targetLength,
            float[] state1,
            float[] state2,
            int vocabularySize,
            int blankIndex,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken)
        {
            var options = ((OnnxRunContext)runContext).Options;
            try
            {
                using var result = _decoder.Run(
                    [
                        NamedOnnxValue.CreateFromTensor(
                            "encoder_outputs",
                            new DenseTensor<float>(encoderFrame.AsMemory(), [1, EncoderDimension, 1])),
                        NamedOnnxValue.CreateFromTensor(
                            "targets",
                            new DenseTensor<int>(target.AsMemory(), [1, 1])),
                        NamedOnnxValue.CreateFromTensor(
                            "target_length",
                            new DenseTensor<int>(targetLength.AsMemory(), [1])),
                        NamedOnnxValue.CreateFromTensor(
                            "input_states_1",
                            new DenseTensor<float>(
                                state1.AsMemory(),
                                [StateLeadingDimension, 1, StateDimension])),
                        NamedOnnxValue.CreateFromTensor(
                            "input_states_2",
                            new DenseTensor<float>(
                                state2.AsMemory(),
                                [StateLeadingDimension, 1, StateDimension])),
                    ],
                    DecoderOutputs,
                    options);

                var output = result[0].AsEnumerable<float>().ToArray();
                var token = IndexOfMaximum(output.AsSpan(0, vocabularySize));
                var step = IndexOfMaximum(output.AsSpan(vocabularySize));
                if (token != blankIndex)
                {
                    Array.Copy(result[1].AsEnumerable<float>().ToArray(), state1, state1.Length);
                    Array.Copy(result[2].AsEnumerable<float>().ToArray(), state2, state2.Length);
                }

                return new ParakeetDecoderStepResult(token, step);
            }
            catch (OnnxRuntimeException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Local transcription was cancelled.",
                    exception,
                    cancellationToken);
            }
            catch (OnnxRuntimeException exception)
            {
                throw CreateStageFailure(ParakeetOnnxStage.Decoder, exception);
            }
        }

        public void Dispose()
        {
            _preprocessor.Dispose();
            _encoder.Dispose();
            _fp32FallbackEncoder?.Dispose();
            _decoder.Dispose();
        }

        private sealed class OnnxRunContext : IParakeetRunContext
        {
            private readonly CancellationTokenRegistration _registration;

            public OnnxRunContext(CancellationToken cancellationToken)
            {
                Options = new RunOptions();
                _registration = cancellationToken.Register(
                    static state => ((RunOptions)state!).Terminate = true,
                    Options);
            }

            public RunOptions Options { get; }

            public void Dispose()
            {
                _registration.Dispose();
                Options.Dispose();
            }
        }
    }
}

public enum ParakeetOnnxStage
{
    Preprocessor,
    Encoder,
    Decoder,
}
