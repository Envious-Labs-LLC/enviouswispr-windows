using EnviousWispr.Core.Runtime;

namespace EnviousWispr.ASR;

public sealed record ParakeetEngineOptions(
    string ModelDirectory,
    RuntimeProviderKind Provider,
    ParakeetModelPack ModelPack,
    int IntraOpThreads,
    int InterOpThreads = 1,
    int MaximumTokensPerStep = 10,
    int CudaDeviceId = 0,
    string? CudaRuntimeDirectory = null);
