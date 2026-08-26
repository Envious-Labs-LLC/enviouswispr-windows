using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

namespace EnviousWispr.ASR;

public sealed class FallbackTranscriptionEngine : ITranscriptionEngine, IDisposable
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

    public async Task<Transcript> TranscribeAsync(
        CapturedAudio audio,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fallbackActive)
            {
                return MarkFallback(await GetFallback().TranscribeAsync(audio, cancellationToken)
                    .ConfigureAwait(false));
            }

            try
            {
                return await _primary.TranscribeAsync(audio, cancellationToken).ConfigureAwait(false);
            }
            catch (TranscriptionEngineException exception) when (
                exception.Error.CanRetry && !cancellationToken.IsCancellationRequested)
            {
                _primaryError = exception.Error;
                _fallbackActive = true;
                return MarkFallback(await GetFallback().TranscribeAsync(audio, cancellationToken)
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
