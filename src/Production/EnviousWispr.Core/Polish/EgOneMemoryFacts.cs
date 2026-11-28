namespace EnviousWispr.Core.Polish;

/// <summary>Grounded Windows working-set peak memory estimates per EG-1 size tier.</summary>
public static class EgOneMemoryFacts
{
    public const ulong BytesPerGiB = 1UL << 30;
    public const long FullPeakMemoryBytes = 3_650_722_202L;
    public const long MiniPeakMemoryBytes = 1_717_986_918L;
    public const long MicroPeakMemoryBytes = 1_073_741_824L;

    /// <summary>Returns the peak working-set estimate in bytes for the normalized EG-1 model tier.</summary>
    public static long PeakMemoryBytes(string? modelId) =>
        EgOneModelCatalog.NormalizeOrDefault(modelId) switch
        {
            EgOneModelCatalog.MiniId => MiniPeakMemoryBytes,
            EgOneModelCatalog.MicroId => MicroPeakMemoryBytes,
            _ => FullPeakMemoryBytes,
        };
}
