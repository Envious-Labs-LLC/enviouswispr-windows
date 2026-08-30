using EnviousWispr.Core.Runtime;

namespace EnviousWispr.ASR;

public sealed record WhisperEngineOptions(
    string ModelPath,
    RuntimeProviderKind Provider,
    WhisperModelPack ModelPack,
    int ThreadCount,
    string? Language = null,
    int CudaDeviceId = 0,
    bool UseFlashAttention = true);
