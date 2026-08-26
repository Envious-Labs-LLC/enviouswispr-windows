using System.Text;
using System.Text.RegularExpressions;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EnviousWispr.ASR;

public sealed class ParakeetTranscriptionEngine : ITranscriptionEngine, IDisposable
{
    public const string ModelId = "parakeet-tdt-0.6b-v3";
    public const int RequiredSampleRate = 16_000;

    private static readonly string[] PreprocessorOutputs = ["features", "features_lens"];
    private static readonly string[] EncoderOutputs = ["outputs", "encoded_lengths"];
    private static readonly string[] DecoderOutputs = ["outputs", "output_states_1", "output_states_2"];
    private static readonly Regex SpacePattern = new(
        @"\A\s|\s\B|(\s)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly InferenceSession _preprocessor;
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
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
        InferenceSession? preprocessor = null;
        InferenceSession? encoder = null;
        InferenceSession? decoder = null;
        try
        {
            using var sessionOptions = CreateSessionOptions(options);
            var modelDirectory = Path.GetFullPath(options.ModelDirectory);
            var encoderFile = options.ModelPack == ParakeetModelPack.FullPrecision
                ? "encoder-model.onnx"
                : "encoder-model.int8.onnx";
            var decoderFile = options.ModelPack == ParakeetModelPack.FullPrecision
                ? "decoder_joint-model.onnx"
                : "decoder_joint-model.int8.onnx";

            preprocessor = new InferenceSession(Path.Combine(modelDirectory, "nemo128.onnx"), sessionOptions);
            encoder = new InferenceSession(Path.Combine(modelDirectory, encoderFile), sessionOptions);
            decoder = new InferenceSession(Path.Combine(modelDirectory, decoderFile), sessionOptions);
            _vocabulary = ParakeetVocabulary.Load(Path.Combine(modelDirectory, "vocab.txt"));

            var decoderInputs = decoder.InputMetadata;
            _encoderDimension = checked((int)decoderInputs["encoder_outputs"].Dimensions[1]);
            _stateLeadingDimension = checked((int)decoderInputs["input_states_1"].Dimensions[0]);
            _stateDimension = checked((int)decoderInputs["input_states_1"].Dimensions[2]);
            _maximumTokensPerStep = options.MaximumTokensPerStep;

            _preprocessor = preprocessor;
            _encoder = encoder;
            _decoder = decoder;
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

    public string EngineId { get; }

    public RuntimeProviderKind Provider { get; }

    public ParakeetModelPack ModelPack { get; }

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

    private Transcript TranscribeCore(CapturedAudio audio, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var runOptions = new RunOptions();
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((RunOptions)state!).Terminate = true,
            runOptions);

        try
        {
            var samples = audio.Samples.ToArray();
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
                runOptions);
            cancellationToken.ThrowIfCancellationRequested();

            var features = preprocessorResult[0].AsEnumerable<float>().ToArray();
            var featureLength = preprocessorResult[1].AsEnumerable<long>().Single();
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
                runOptions);
            cancellationToken.ThrowIfCancellationRequested();

            var encoded = TransposeEncoderOutput(encoderResult[0].AsEnumerable<float>().ToArray());
            var encodedLength = checked((int)encoderResult[1].AsEnumerable<long>().Single());
            var decoded = Decode(encoded, encodedLength, runOptions, cancellationToken);
            var text = TokensToText(decoded);
            var timings = CreateTimings(
                decoded,
                encodedLength,
                TimeSpan.FromSeconds(samples.Length / (double)RequiredSampleRate));
            return new Transcript(audio.SessionId, text, EngineId, timings);
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
            throw CreateFailure(AppErrorCode.TranscriptionFailed, exception);
        }
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
        RunOptions runOptions,
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
            using var result = _decoder.Run(
                [
                    NamedOnnxValue.CreateFromTensor(
                        "encoder_outputs",
                        new DenseTensor<float>(encoderFrame.AsMemory(), [1, _encoderDimension, 1])),
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
                            [_stateLeadingDimension, 1, _stateDimension])),
                    NamedOnnxValue.CreateFromTensor(
                        "input_states_2",
                        new DenseTensor<float>(
                            state2.AsMemory(),
                            [_stateLeadingDimension, 1, _stateDimension])),
                ],
                DecoderOutputs,
                runOptions);

            var output = result[0].AsEnumerable<float>().ToArray();
            var token = IndexOfMaximum(output.AsSpan(0, _vocabulary.Size));
            var step = IndexOfMaximum(output.AsSpan(_vocabulary.Size));
            if (token != _vocabulary.BlankIndex)
            {
                Array.Copy(result[1].AsEnumerable<float>().ToArray(), state1, state1.Length);
                Array.Copy(result[2].AsEnumerable<float>().ToArray(), state2, state2.Length);
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
        TimeSpan audioDuration)
    {
        if (tokens.Count == 0 || encodedLength <= 0)
        {
            return [];
        }

        var result = new TranscriptTokenTiming[tokens.Count];
        for (var index = 0; index < tokens.Count; index++)
        {
            var currentFrame = tokens[index].Frame;
            var nextFrame = index + 1 < tokens.Count
                ? Math.Max(currentFrame + 1, tokens[index + 1].Frame)
                : encodedLength;
            result[index] = new TranscriptTokenTiming(
                _vocabulary.Tokens[tokens[index].Id],
                ScaleTime(audioDuration, currentFrame, encodedLength),
                ScaleTime(audioDuration, Math.Min(nextFrame, encodedLength), encodedLength));
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

        var runtimeDirectory = Path.GetFullPath(options.CudaRuntimeDirectory);
        if (!Directory.Exists(runtimeDirectory))
        {
            throw CreateFailure(AppErrorCode.RuntimeProviderUnavailable);
        }

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!currentPath.Split(Path.PathSeparator).Contains(runtimeDirectory, StringComparer.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable(
                "PATH",
                runtimeDirectory + Path.PathSeparator + currentPath);
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

    private static TranscriptionEngineException CreateFailure(
        AppErrorCode code,
        Exception? innerException = null) => new(
        new AppError(code, AppErrorStage.FinalAsr, CanRetry: true),
        innerException);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _preprocessor.Dispose();
        _encoder.Dispose();
        _decoder.Dispose();
        _transcriptionGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private readonly record struct DecodedToken(int Id, int Frame);
}
