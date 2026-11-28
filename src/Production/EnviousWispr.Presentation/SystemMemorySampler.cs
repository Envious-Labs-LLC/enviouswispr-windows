using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Polish;

namespace EnviousWispr.Presentation;

/// <summary>Samples live PC memory on a 2-second cadence while visible, reporting only shown changes.</summary>
public sealed class SystemMemorySampler
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    private readonly Func<SystemMemoryReading?> _read;
    private readonly Func<bool> _isVisible;
    private SampleKey? _lastKey;

    public SystemMemorySampler(Func<SystemMemoryReading?> read, Func<bool> isVisible)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(isVisible);
        _read = read;
        _isVisible = isVisible;
    }

    public SystemMemoryReading? LatestReading { get; private set; }

    /// <summary>Reads system memory if visible and returns true when tenths-of-GiB, total GiB, availability, or any 90% threshold state changes.</summary>
    public bool Tick()
    {
        if (!_isVisible())
        {
            return false;
        }

        var raw = _read();
        var normalized = raw is { TotalPhysicalBytes: > 0 } reading
            ? new SystemMemoryReading(reading.TotalPhysicalBytes, Math.Min(reading.UsedBytes, reading.TotalPhysicalBytes))
            : (SystemMemoryReading?)null;

        var nextKey = normalized is { } valid
            ? new SampleKey(
                ComputeTotalWholeGiB(valid.TotalPhysicalBytes),
                ComputeUsedTenthsGiB(valid.UsedBytes),
                ComputeWarningMask(valid))
            : (SampleKey?)null;

        if (nextKey == _lastKey)
        {
            return false;
        }

        _lastKey = nextKey;
        LatestReading = normalized;
        return true;
    }

    public void Reset()
    {
        _lastKey = null;
        LatestReading = null;
    }

    private static long ComputeTotalWholeGiB(ulong totalPhysicalBytes) =>
        Math.Max(1L, (long)Math.Round(totalPhysicalBytes / (double)EgOneMemoryFacts.BytesPerGiB, MidpointRounding.AwayFromZero));

    private static long ComputeUsedTenthsGiB(ulong usedBytes) =>
        (long)Math.Round(usedBytes / (double)EgOneMemoryFacts.BytesPerGiB * 10.0, MidpointRounding.AwayFromZero);

    private static int ComputeWarningMask(SystemMemoryReading reading)
    {
        var mask = 0;
        var bit = 1;
        foreach (var modelId in EgOneModelCatalog.AllIds)
        {
            var peak = (ulong)Math.Max(0L, EgOneMemoryFacts.PeakMemoryBytes(modelId));
            if (((UInt128)reading.UsedBytes + peak) * 10 > (UInt128)reading.TotalPhysicalBytes * 9)
            {
                mask |= bit;
            }

            bit <<= 1;
        }

        return mask;
    }

    private readonly record struct SampleKey(long TotalWholeGiB, long UsedTenthsGiB, int WarningMask);
}
