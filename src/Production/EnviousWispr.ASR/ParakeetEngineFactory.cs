using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

namespace EnviousWispr.ASR;

public sealed record ParakeetEngineCreationResult(
    ITranscriptionEngine Engine,
    bool UsedFallback,
    AppError? DegradedError = null);

public sealed class ParakeetEngineFactory
{
    private readonly Func<ParakeetEngineOptions, ITranscriptionEngine> _createEngine;

    public ParakeetEngineFactory()
        : this(options => new ParakeetTranscriptionEngine(options))
    {
    }

    internal ParakeetEngineFactory(Func<ParakeetEngineOptions, ITranscriptionEngine> createEngine)
    {
        _createEngine = createEngine;
    }

    public ParakeetEngineCreationResult Create(
        ParakeetEngineOptions primary,
        ParakeetEngineOptions? cpuFallback = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        if (cpuFallback is { Provider: not EnviousWispr.Core.Runtime.RuntimeProviderKind.Cpu })
        {
            throw new ArgumentException("The fallback engine must use the CPU provider.", nameof(cpuFallback));
        }

        try
        {
            var primaryEngine = _createEngine(primary);
            return cpuFallback is null || primary.Provider == EnviousWispr.Core.Runtime.RuntimeProviderKind.Cpu
                ? new ParakeetEngineCreationResult(primaryEngine, UsedFallback: false)
                : new ParakeetEngineCreationResult(
                    new FallbackTranscriptionEngine(primaryEngine, () => _createEngine(cpuFallback)),
                    UsedFallback: false);
        }
        catch (TranscriptionEngineException exception) when (
            cpuFallback is not null &&
            primary.Provider != EnviousWispr.Core.Runtime.RuntimeProviderKind.Cpu &&
            exception.Error.CanRetry)
        {
            return new ParakeetEngineCreationResult(
                _createEngine(cpuFallback),
                UsedFallback: true,
                exception.Error);
        }
    }
}
