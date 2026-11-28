using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

namespace EnviousWispr.ASR;

public sealed class FallbackTranscriptionEngine : IProgressReportingTranscriptionEngine, IDisposable
{
    private readonly ITranscriptionEngine _primary;
    private readonly Func<ITranscriptionEngine> _fallbackFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITranscriptionEngine? _fallback;
    private AppError? _primaryError;
    private bool _fallbackActive;
    private bool _disposed;

    public FallbackTranscriptionEngine(
        ITranscriptionEngine primary,
        Func<ITranscriptionEngine> fallbackFactory)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallbackFactory = fallbackFactory ?? throw new ArgumentNullException(nameof(fallbackFactory));
    }

    public string EngineId => _fallbackActive && _fallback is not null
        ? _fallback.EngineId
        : _primary.EngineId;

    public Task<Transcript> TranscribeAsync(
        CapturedAudio audio,
        CancellationToken cancellationToken = default) =>
        TranscribeCoreAsync(audio, language: null, languageGiven: false, onProgress: null, cancellationToken);

    /// <summary>The take's language goes to whichever engine runs it, the fallback included.</summary>
    public Task<Transcript> TranscribeAsync(
        CapturedAudio audio,
        string? language,
        CancellationToken cancellationToken = default) =>
        TranscribeCoreAsync(audio, language, languageGiven: true, onProgress: null, cancellationToken);

    public Task<Transcript> TranscribeAsync(
        CapturedAudio audio,
        string? language,
        Action<string, double?>? onProgress,
        CancellationToken cancellationToken = default) =>
        TranscribeCoreAsync(audio, language, languageGiven: language is not null, onProgress, cancellationToken);

    private static Task<Transcript> On(
        ITranscriptionEngine engine,
        CapturedAudio audio,
        string? language,
        bool languageGiven,
        Action<string, double?>? onProgress,
        CancellationToken cancellationToken)
    {
        if (onProgress is not null && engine is IProgressReportingTranscriptionEngine progressEngine)
        {
            return progressEngine.TranscribeAsync(audio, languageGiven ? language : null, onProgress, cancellationToken);
        }

        return languageGiven && engine is ILanguageSelectableTranscriptionEngine selectable
            ? selectable.TranscribeAsync(audio, language, cancellationToken)
            : engine.TranscribeAsync(audio, cancellationToken);
    }

    private async Task<Transcript> TranscribeCoreAsync(
        CapturedAudio audio,
        string? language,
        bool languageGiven,
        Action<string, double?>? onProgress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fallbackActive)
            {
                return MarkFallback(await On(GetFallback(), audio, language, languageGiven, onProgress, cancellationToken)
                    .ConfigureAwait(false));
            }

            try
            {
                return await On(_primary, audio, language, languageGiven, onProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (TranscriptionEngineException exception) when (
                exception.Error.CanRetry && !cancellationToken.IsCancellationRequested)
            {
                _primaryError = exception.Error;
                _fallbackActive = true;
                return MarkFallback(await On(GetFallback(), audio, language, languageGiven, onProgress, cancellationToken)
                    .ConfigureAwait(false));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private ITranscriptionEngine GetFallback() => _fallback ??= _fallbackFactory();

    private Transcript MarkFallback(Transcript transcript) => transcript with
    {
        UsedFallback = true,
        DegradedError = _primaryError,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        (_primary as IDisposable)?.Dispose();
        (_fallback as IDisposable)?.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
