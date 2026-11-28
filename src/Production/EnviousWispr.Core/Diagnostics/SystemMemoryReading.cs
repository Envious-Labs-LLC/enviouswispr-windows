namespace EnviousWispr.Core.Diagnostics;

/// <summary>Point-in-time system physical memory capacity and currently used bytes.</summary>
public readonly record struct SystemMemoryReading(ulong TotalPhysicalBytes, ulong UsedBytes);
