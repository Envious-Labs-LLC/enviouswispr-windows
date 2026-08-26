using System.Text;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace EnviousWispr.ASR;

public sealed class WhisperTranscriptionEngine : ITranscriptionEngine, IAsyncDisposable, IDisposable
{
    public const string ModelId = WhisperModelIds.Final;
    public const string PreviewModelId = WhisperModelIds.Preview;
    public const string NativeRuntimeVersion = "whisper.net-1.9.1-whisper.cpp-23ee035";
    public const int RequiredSampleRate = 16_000;

    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _transcriptionGate = new(1, 1);
    private bool _disposed;

    public WhisperTranscriptionEngine(WhisperEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        Provider = options.Provider;
        ModelPack = options.ModelPack;
        EngineId = $"{WhisperModelIds.For(options.ModelPack)}:{Provider.ToString().ToLowerInvariant()}";

        WhisperFactory? factory = null;
        try
        {
            RuntimeOptions.LoadedLibrary = null;
            RuntimeOptions.RuntimeLibraryOrder = options.Provider == RuntimeProviderKind.Cuda
                ? [RuntimeLibrary.Cuda]
                : [RuntimeLibrary.Cpu];
            factory = WhisperFactory.FromPath(
                Path.GetFullPath(options.ModelPath),
                new WhisperFactoryOptions
                {
                    UseGpu = options.Provider == RuntimeProviderKind.Cuda,
                    UseFlashAttention = options.UseFlashAttention,
                    GpuDevice = options.CudaDeviceId,
                });
            var builder = factory.CreateBuilder()
                .WithThreads(options.ThreadCount)
                .WithNoContext()
                .WithTokenTimestamps()
                .WithoutStringPool();
            builder = string.IsNullOrWhiteSpace(options.Language) ||
                string.Equals(options.Language, "auto", StringComparison.OrdinalIgnoreCase)
                ? builder.WithLanguageDetection()
                : builder.WithLanguage(options.Language);
            _processor = builder.Build();
            _factory = factory;
        }
        catch (Exception exception) when (IsRuntimeFailure(exception))
        {
            factory?.Dispose();
            throw Failure(
                options.Provider == RuntimeProviderKind.Cpu
                    ? AppErrorCode.TranscriptionFailed
                    : AppErrorCode.RuntimeProviderUnavailable,
                exception);
        }
    }

    public string EngineId { get; }

    public RuntimeProviderKind Provider { get; }

    public WhisperModelPack ModelPack { get; }

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
            throw Failure(AppErrorCode.AudioFormatUnsupported);
        }

        if (audio.Samples.IsEmpty)
        {
            return new Transcript(audio.SessionId, string.Empty, EngineId, []);
        }

        await _transcriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var text = new StringBuilder();
            var timings = new List<TranscriptTokenTiming>();
            string? detectedLanguage = null;
            await foreach (var segment in _processor.ProcessAsync(audio.Samples, cancellationToken)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                text.Append(segment.Text);
                detectedLanguage ??= string.IsNullOrWhiteSpace(segment.Language)
                    ? null
                    : segment.Language;
                AddTimings(timings, segment, audio.Duration());
            }

            return new Transcript(
                audio.SessionId,
                text.ToString().Trim(),
                EngineId,
                timings,
                DetectedLanguage: detectedLanguage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRuntimeFailure(exception))
        {
            throw Failure(AppErrorCode.TranscriptionFailed, exception);
        }
        finally
        {
            _transcriptionGate.Release();
        }
    }

    private static void AddTimings(
        List<TranscriptTokenTiming> timings,
        SegmentData segment,
        TimeSpan audioDuration)
    {
        foreach (var token in segment.Tokens)
        {
            if (string.IsNullOrEmpty(token.Text) || token.Start < 0 || token.End < token.Start)
            {
                continue;
            }

            var start = TimeSpan.FromMilliseconds(token.Start * 10);
            var end = TimeSpan.FromMilliseconds(token.End * 10);
            if (start > audioDuration)
            {
                continue;
            }

            timings.Add(new TranscriptTokenTiming(
                token.Text,
                start,
                end > audioDuration ? audioDuration : end));
        }
    }

    private static void Validate(WhisperEngineOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ThreadCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.CudaDeviceId);
        if (options.Provider is not (RuntimeProviderKind.Cpu or RuntimeProviderKind.Cuda))
        {
            throw Failure(AppErrorCode.RuntimeProviderIncompatible);
        }

        if (!File.Exists(options.ModelPath) || new FileInfo(options.ModelPath).Length == 0)
        {
            throw Failure(AppErrorCode.ModelPackUnavailable);
        }
    }

    private static bool IsRuntimeFailure(Exception exception) =>
        exception is WhisperModelLoadException or WhisperProcessingException or
            DllNotFoundException or BadImageFormatException or TypeInitializationException or
            IOException or UnauthorizedAccessException;

    private static TranscriptionEngineException Failure(
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
        _processor.Dispose();
        _factory.Dispose();
        _transcriptionGate.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _processor.DisposeAsync().ConfigureAwait(false);
        _factory.Dispose();
        _transcriptionGate.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal static class CapturedAudioDurationExtensions
{
    public static TimeSpan Duration(this CapturedAudio audio) =>
        TimeSpan.FromSeconds(audio.Samples.Length / (double)audio.SampleRate);
}
