using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.ASR;

public sealed record WhisperEngineCreationResult(
    ITranscriptionEngine Engine,
    bool UsedFallback,
    AppError? DegradedError = null);

public sealed class WhisperEngineFactory
{
    private readonly Func<WhisperEngineOptions, ITranscriptionEngine> _createEngine;

    public WhisperEngineFactory()
        : this(options => new WhisperTranscriptionEngine(options))
    {
    }

    internal WhisperEngineFactory(Func<WhisperEngineOptions, ITranscriptionEngine> createEngine)
    {
        _createEngine = createEngine;
    }

    public WhisperEngineCreationResult Create(
        WhisperEngineOptions primary,
        WhisperEngineOptions? cpuFallback = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        if (cpuFallback is { Provider: not RuntimeProviderKind.Cpu })
        {
            throw new ArgumentException("The fallback engine must use the CPU provider.", nameof(cpuFallback));
        }

        try
        {
            var primaryEngine = _createEngine(primary);
            return cpuFallback is null || primary.Provider == RuntimeProviderKind.Cpu
                ? new WhisperEngineCreationResult(primaryEngine, UsedFallback: false)
                : new WhisperEngineCreationResult(
                    new FallbackTranscriptionEngine(primaryEngine, () => _createEngine(cpuFallback)),
                    UsedFallback: false);
        }
        catch (TranscriptionEngineException exception) when (
            cpuFallback is not null &&
            primary.Provider != RuntimeProviderKind.Cpu &&
            exception.Error.CanRetry)
        {
            return new WhisperEngineCreationResult(
                _createEngine(cpuFallback),
                UsedFallback: true,
                exception.Error);
        }
    }
}
