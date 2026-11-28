using System.Text;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace EnviousWispr.ASR;

public sealed class WhisperTranscriptionEngine : IProgressReportingTranscriptionEngine, IAsyncDisposable, IDisposable
{
    public const string ModelId = WhisperModelIds.Final;
    public const string PreviewModelId = WhisperModelIds.Preview;
    public const string NativeRuntimeVersion = "whisper.net-1.9.1-whisper.cpp-23ee035";
    public const int RequiredSampleRate = 16_000;
    private const int MaxLidWindows = 4;
    private const int MinimumLidWindowSamples = RequiredSampleRate / 4;

    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _transcriptionGate = new(1, 1);
    private const string AutomaticLanguage = "auto";

    // THE LOAD-TIME LANGUAGE IS KEPT APART FROM THE CURRENT ONE. A take that names no language runs in
    // the language this engine was built with, never in whatever the previous take switched it to.
    private readonly string _initialLanguage;
    private Action<string, double?>? _activeProgressCallback;
    private string _language;
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
            options.OnInitStage?.Invoke("ModelInitStarted");
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
            options.OnInitStage?.Invoke("WeightsMapped");
            var builder = factory.CreateBuilder()
                .WithThreads(options.ThreadCount)
                .WithNoContext()
                .WithTokenTimestamps()
                .WithoutStringPool()
                .WithProbabilities()
                .WithEncoderBeginHandler(_ =>
                {
                    _activeProgressCallback?.Invoke("EncoderStarted", null);
                    return true;
                })
                .WithProgressHandler(progress =>
                    _activeProgressCallback?.Invoke("DecodeProgress", Math.Clamp(progress / 100.0, 0.0, 1.0)));
            _initialLanguage = string.IsNullOrWhiteSpace(options.Language) ||
                string.Equals(options.Language, AutomaticLanguage, StringComparison.OrdinalIgnoreCase)
                ? AutomaticLanguage
                : options.Language;
            _language = _initialLanguage;
            builder = _language == AutomaticLanguage
                ? builder.WithLanguageDetection()
                : builder.WithLanguage(_language);
            _processor = builder.Build();
            _factory = factory;
            options.OnInitStage?.Invoke("ExecutionProviderReady");
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

    public Task<Transcript> TranscribeAsync(
        CapturedAudio audio,
        CancellationToken cancellationToken = default) =>
        TranscribeCoreAsync(audio, _initialLanguage, onProgress: null, cancellationToken);

    /// <summary>Transcribes this take in the language given, switching the loaded model to it first when it differs.</summary>
    /// <remarks>
    /// NO RELOAD: whisper.cpp reads the language from its decoding parameters on every call, and
    /// Whisper.net's ChangeLanguage replaces only that parameter ("auto" turns detection back on). The
    /// switch happens inside the transcription gate, so a take never runs with half a change.
    /// </remarks>
    public Task<Transcript> TranscribeAsync(
        CapturedAudio audio,
        string? language,
        CancellationToken cancellationToken = default) =>
        TranscribeCoreAsync(audio, NormalizeLanguage(language), onProgress: null, cancellationToken);

    public Task<Transcript> TranscribeAsync(
        CapturedAudio audio,
        string? language,
        Action<string, double?>? onProgress,
        CancellationToken cancellationToken = default) =>
        TranscribeCoreAsync(
            audio,
            language is null ? _initialLanguage : NormalizeLanguage(language),
            onProgress,
            cancellationToken);

    private static string NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) ||
        string.Equals(language.Trim(), AutomaticLanguage, StringComparison.OrdinalIgnoreCase)
            ? AutomaticLanguage
            : language.Trim().ToLowerInvariant();

    private async Task<Transcript> TranscribeCoreAsync(
        CapturedAudio audio,
        string? requestedLanguage,
        Action<string, double?>? onProgress,
        CancellationToken cancellationToken)
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
            if (requestedLanguage is not null &&
                !string.Equals(requestedLanguage, _language, StringComparison.Ordinal))
            {
                _processor.ChangeLanguage(requestedLanguage);
                _language = requestedLanguage;
            }

            (string? Language, double? Confidence, double? Margin) lidResult = (null, null, null);
            if (string.Equals(_language, AutomaticLanguage, StringComparison.Ordinal))
            {
                lidResult = DetectMultiWindowLanguage(audio.Samples.Span, audio.SampleRate, cancellationToken);
            }

            _activeProgressCallback = onProgress;
            var text = new StringBuilder();
            var timings = new List<TranscriptTokenTiming>();
            var pendingTrailingWhitespace = false;
            string? detectedLanguage = null;
            using var processorPhase = UatRuntimePhaseJournal.BeginNativeCall(UatNativeOperation.WhisperProcessor);
            await foreach (var segment in _processor.ProcessAsync(audio.Samples, cancellationToken)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawSegment = segment.Text ?? string.Empty;
                var hadLeadingWhitespace = rawSegment.Length > 0 && char.IsWhiteSpace(rawSegment[0]);
                var cleaned = CleanSegment(rawSegment, segment.NoSpeechProbability);
                if (cleaned.Length == 0)
                {
                    if (rawSegment.Length > 0 && char.IsWhiteSpace(rawSegment[^1]))
                    {
                        pendingTrailingWhitespace = true;
                    }

                    continue;
                }

                AppendCleanedSegment(text, cleaned, hadLeadingWhitespace || pendingTrailingWhitespace);
                pendingTrailingWhitespace = rawSegment.Length > 0 && char.IsWhiteSpace(rawSegment[^1]);
                detectedLanguage ??= string.IsNullOrWhiteSpace(segment.Language)
                    ? null
                    : segment.Language;
                AddTimings(timings, segment, audio.Duration());
            }

            processorPhase.Returned();

            var finalDetectedLanguage = detectedLanguage ?? lidResult.Language;
            double? finalConfidence = null;
            double? finalMargin = null;
            if (lidResult.Language is not null &&
                finalDetectedLanguage is not null &&
                string.Equals(
                    NormalizeBaseLanguage(finalDetectedLanguage),
                    NormalizeBaseLanguage(lidResult.Language),
                    StringComparison.Ordinal))
            {
                finalConfidence = lidResult.Confidence;
                finalMargin = lidResult.Margin;
            }

            return new Transcript(
                audio.SessionId,
                WhisperNonSpeechFilter.Clean(text.ToString()),
                EngineId,
                timings,
                DetectedLanguage: finalDetectedLanguage,
                RecognitionLanguage: _language,
                DetectedLanguageConfidence: finalConfidence,
                DetectedLanguageMargin: finalMargin);
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
            _activeProgressCallback = null;
            _transcriptionGate.Release();
        }
    }

    private (string? Language, double? Confidence, double? Margin) DetectMultiWindowLanguage(
        ReadOnlySpan<float> samples,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        var ranges = BuildVoicedLidWindows(samples, sampleRate);
        if (ranges.Count == 0)
        {
            return (null, null, null);
        }

        var observations = new List<(string Language, float Probability)>(ranges.Count);
        foreach (var (start, length) in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var slice = samples.Slice(start, length).ToArray();
                var (lang, probability) = _processor.DetectLanguageWithProbability(slice);
                if (!string.IsNullOrWhiteSpace(lang) && !float.IsNaN(probability) && !float.IsInfinity(probability))
                {
                    observations.Add((lang, probability));
                }
            }
            catch (Exception exception) when (IsRuntimeFailure(exception))
            {
                // Heart-protection: skip a single failed LID window without aborting decode.
            }
        }

        return AggregateLanguageObservations(observations);
    }

    internal static IReadOnlyList<(int Start, int Length)> BuildVoicedLidWindows(
        ReadOnlySpan<float> samples,
        int sampleRate = RequiredSampleRate,
        int maxWindows = MaxLidWindows)
    {
        if (sampleRate <= 0 || maxWindows <= 0 || samples.Length < MinimumLidWindowSamples)
        {
            return [];
        }

        (double StartSec, double EndSec)[] schedule =
        [
            (0.0, 4.0),
            (4.0, 8.0),
            (1.0, 5.0),
            (0.0, 12.0),
        ];

        var seen = new HashSet<(int, int)>();
        var windows = new List<(int Start, int Length)>(maxWindows);
        foreach (var (startSec, endSec) in schedule)
        {
            var start = Math.Min(samples.Length, (int)(startSec * sampleRate));
            var end = Math.Min(samples.Length, (int)(endSec * sampleRate));
            var length = end - start;
            if (length < MinimumLidWindowSamples || !seen.Add((start, length)))
            {
                continue;
            }

            if (AudioQuietCut.IsBelowVoicedFloor(samples.Slice(start, length)))
            {
                continue;
            }

            windows.Add((start, length));
            if (windows.Count >= maxWindows)
            {
                break;
            }
        }

        return windows;
    }

    internal static (string? Language, double? Confidence, double? Margin) AggregateLanguageObservations(
        IReadOnlyList<(string Language, float Probability)> observations)
    {
        if (observations.Count == 0)
        {
            return (null, null, null);
        }

        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        var validCount = 0;
        foreach (var (language, probability) in observations)
        {
            var normalized = NormalizeBaseLanguage(language);
            if (normalized.Length == 0 || float.IsNaN(probability) || float.IsInfinity(probability))
            {
                continue;
            }

            var clamped = Math.Clamp((double)probability, 0.0, 1.0);
            totals[normalized] = totals.GetValueOrDefault(normalized) + clamped;
            validCount++;
        }

        if (validCount == 0 || totals.Count == 0)
        {
            return (null, null, null);
        }

        string? topLanguage = null;
        var topScore = -1.0;
        var secondObserved = 0.0;
        var totalObserved = 0.0;
        foreach (var (language, sum) in totals)
        {
            var score = sum / validCount;
            totalObserved += score;
            if (score > topScore)
            {
                secondObserved = Math.Max(secondObserved, Math.Max(0.0, topScore));
                topScore = score;
                topLanguage = language;
            }
            else if (score > secondObserved)
            {
                secondObserved = score;
            }
        }

        if (topLanguage is null)
        {
            return (null, null, null);
        }

        var residualUpperBound = Math.Max(0.0, 1.0 - totalObserved);
        var runnerUpBound = Math.Max(secondObserved, residualUpperBound);
        var margin = Math.Max(0.0, topScore - runnerUpBound);
        return (topLanguage, Math.Clamp(topScore, 0.0, 1.0), Math.Clamp(margin, 0.0, 1.0));
    }

    private static string NormalizeBaseLanguage(string? language)
    {
        var trimmed = language?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        var separator = trimmed.AsSpan().IndexOfAny('-', '_');
        var head = separator < 0 ? trimmed : trimmed[..separator];
        return head.ToLowerInvariant();
    }

    internal const float HighNoSpeechProbabilityThreshold = 0.85f;

    internal readonly record struct WhisperSegmentPiece(string Text, float NoSpeechProbability);

    internal static string CleanAndJoinSegments(IEnumerable<WhisperSegmentPiece> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var text = new StringBuilder();
        var pendingTrailingWhitespace = false;
        foreach (var segment in segments)
        {
            var rawSegment = segment.Text ?? string.Empty;
            var hadLeadingWhitespace = rawSegment.Length > 0 && char.IsWhiteSpace(rawSegment[0]);
            var cleaned = CleanSegment(rawSegment, segment.NoSpeechProbability);
            if (cleaned.Length == 0)
            {
                if (rawSegment.Length > 0 && char.IsWhiteSpace(rawSegment[^1]))
                {
                    pendingTrailingWhitespace = true;
                }

                continue;
            }

            AppendCleanedSegment(text, cleaned, hadLeadingWhitespace || pendingTrailingWhitespace);
            pendingTrailingWhitespace = rawSegment.Length > 0 && char.IsWhiteSpace(rawSegment[^1]);
        }

        return WhisperNonSpeechFilter.Clean(text.ToString());
    }

    internal static string CleanSegment(string? segmentText, float noSpeechProbability)
    {
        var cleaned = WhisperNonSpeechFilter.Clean(segmentText);
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        if (noSpeechProbability >= HighNoSpeechProbabilityThreshold &&
            !WhisperNonSpeechFilter.ContainsLetterOrDigit(cleaned))
        {
            return string.Empty;
        }

        return cleaned;
    }

    internal static void AppendCleanedSegment(
        StringBuilder text,
        string cleanedSegment,
        bool preserveRawBoundarySpace = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrEmpty(cleanedSegment))
        {
            return;
        }

        if (text.Length > 0 &&
            !char.IsWhiteSpace(text[^1]) &&
            !char.IsWhiteSpace(cleanedSegment[0]) &&
            !IsAttachedTrailingPunctuation(cleanedSegment[0]))
        {
            var previousUsesUnsegmented = IsUnsegmentedOrCjkCharacter(text[^1]);
            var nextUsesUnsegmented = IsUnsegmentedOrCjkCharacter(cleanedSegment[0]);
            if (preserveRawBoundarySpace || (!previousUsesUnsegmented && !nextUsesUnsegmented))
            {
                text.Append(' ');
            }
        }

        text.Append(cleanedSegment);
    }

    private static bool IsAttachedTrailingPunctuation(char ch) =>
        ch is '.' or ',' or '!' or '?' or ';' or ':' or ')' or ']' or '}' or '\u2019' or '\u201D' or
            '\u3001' or '\u3002' or '\uFF01' or '\uFF1F' or '\uFF0C' or '\uFF1B' or '\uFF1A' or
            '\uFF09' or '\u300D' or '\u300F' or '\u3011' or '\u300B';

    private static bool IsUnsegmentedOrCjkCharacter(char ch) =>
        ch is (>= '\u3000' and <= '\u30FF') or
            (>= '\u3400' and <= '\u9FFF') or
            (>= '\uAC00' and <= '\uD7AF') or
            (>= '\u0E00' and <= '\u0E7F') or
            (>= '\u0E80' and <= '\u0EFF') or
            (>= '\u1000' and <= '\u109F') or
            (>= '\u1780' and <= '\u17FF') or
            (>= '\uFF00' and <= '\uFFEF');

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
