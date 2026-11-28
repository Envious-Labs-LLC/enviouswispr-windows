using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Services.Input;

/// <summary>
/// Detects candidate BCP-47 base language tags using Windows Extended Linguistic Services (ELS).
/// </summary>
public sealed class WindowsElsLanguageDetector : IOsLanguageDetector, IDisposable
{
    private readonly object _gate = new();
    private nint _servicePtr;
    private bool _disposed;
    private bool _unavailable;

    /// <summary>
    /// Initializes an ELS language detection service handle, failing safe when ELS is unavailable.
    /// </summary>
    public WindowsElsLanguageDetector()
    {
        TryInitializeService();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> DetectBaseTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        lock (_gate)
        {
            if (_disposed || _unavailable || _servicePtr == 0)
            {
                return Array.Empty<string>();
            }

            var bag = new MAPPING_PROPERTY_BAG
            {
                Size = (nuint)Marshal.SizeOf<MAPPING_PROPERTY_BAG>(),
            };

            try
            {
                var hresult = NativeMethods.MappingRecognizeText(
                    _servicePtr,
                    text,
                    checked((uint)text.Length),
                    0,
                    0,
                    ref bag);

                if (hresult != 0 || bag.dwRangesCount == 0 || bag.prgResultRanges == 0)
                {
                    return Array.Empty<string>();
                }

                try
                {
                    return ExtractLongestRangeBaseTags(bag);
                }
                finally
                {
                    _ = NativeMethods.MappingFreePropertyBag(ref bag);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OverflowException)
            {
                _unavailable = true;
                return Array.Empty<string>();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_servicePtr != 0)
            {
                var ptr = _servicePtr;
                _servicePtr = 0;
                try
                {
                    _ = NativeMethods.MappingFreeServices(ptr);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    // Best-effort native cleanup.
                }
            }
        }
    }

    private void TryInitializeService()
    {
        nint pGuid = 0;
        try
        {
            var guid = NativeMethods.ELS_GUID_LANGUAGE_DETECTION;
            pGuid = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
            Marshal.StructureToPtr(guid, pGuid, fDeleteOld: false);

            var options = new MAPPING_ENUM_OPTIONS
            {
                Size = (nuint)Marshal.SizeOf<MAPPING_ENUM_OPTIONS>(),
                pGuid = pGuid,
            };

            var hresult = NativeMethods.MappingGetServices(ref options, out var servicesPtr, out var count);
            if (hresult == 0 && count > 0 && servicesPtr != 0)
            {
                _servicePtr = servicesPtr;
            }
            else
            {
                if (servicesPtr != 0)
                {
                    _ = NativeMethods.MappingFreeServices(servicesPtr);
                }

                _unavailable = true;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
        }
        finally
        {
            if (pGuid != 0)
            {
                Marshal.FreeHGlobal(pGuid);
            }
        }
    }

    private static IReadOnlyList<string> ExtractLongestRangeBaseTags(MAPPING_PROPERTY_BAG bag)
    {
        var rangeSize = Marshal.SizeOf<MAPPING_DATA_RANGE>();
        var bestRangeIndex = 0;
        uint bestLength = 0;

        for (var i = 0; i < bag.dwRangesCount; i++)
        {
            var range = Marshal.PtrToStructure<MAPPING_DATA_RANGE>(nint.Add(bag.prgResultRanges, i * rangeSize));
            var length = range.dwEndIndex >= range.dwStartIndex
                ? range.dwEndIndex - range.dwStartIndex + 1
                : 0u;
            if (length > bestLength)
            {
                bestLength = length;
                bestRangeIndex = i;
            }
        }

        var bestRange = Marshal.PtrToStructure<MAPPING_DATA_RANGE>(nint.Add(bag.prgResultRanges, bestRangeIndex * rangeSize));
        if (bestRange.pData == 0 || bestRange.dwDataSize < sizeof(char))
        {
            return Array.Empty<string>();
        }

        var charCount = checked((int)(bestRange.dwDataSize / sizeof(char)));
        var chars = new char[charCount];
        Marshal.Copy(bestRange.pData, chars, 0, charCount);

        var tags = new List<string>();
        var rawTags = new string(chars).Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var rawTag in rawTags)
        {
            var sep = rawTag.IndexOfAny(['-', '_']);
            var baseTag = (sep > 0 ? rawTag[..sep] : rawTag).ToLowerInvariant();
            if (baseTag.Length > 0 && !tags.Contains(baseTag, StringComparer.Ordinal))
            {
                tags.Add(baseTag);
            }
        }

        return tags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MAPPING_ENUM_OPTIONS
    {
        public nuint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszCategory;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszInputLanguage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszOutputLanguage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszInputScript;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszOutputScript;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszInputContentType;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszOutputContentType;
        public nint pGuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MAPPING_DATA_RANGE
    {
        public uint dwStartIndex;
        public uint dwEndIndex;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszDescription;
        public uint dwDescriptionLength;
        public nint pData;
        public uint dwDataSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszContentType;
        public nint prgActionIds;
        public uint dwActionsCount;
        public nint prgActionDisplayNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MAPPING_PROPERTY_BAG
    {
        public nuint Size;
        public nint prgResultRanges;
        public uint dwRangesCount;
        public nint pServiceData;
        public uint dwServiceDataSize;
        public nint pCallerData;
        public uint dwCallerDataSize;
        public nint pContext;
    }

    private static class NativeMethods
    {
        public static readonly Guid ELS_GUID_LANGUAGE_DETECTION = new("CF7E00B1-909B-4d95-A8F4-611F7C377702");

        [DllImport("elscore.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int MappingGetServices(
            ref MAPPING_ENUM_OPTIONS pOptions,
            out nint prgServices,
            out uint pdwServicesCount);

        [DllImport("elscore.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int MappingRecognizeText(
            nint pServiceInfo,
            [MarshalAs(UnmanagedType.LPWStr)] string pszText,
            uint dwLength,
            uint dwIndex,
            nint pOptions,
            ref MAPPING_PROPERTY_BAG pbag);

        [DllImport("elscore.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int MappingFreePropertyBag(ref MAPPING_PROPERTY_BAG pbag);

        [DllImport("elscore.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int MappingFreeServices(nint pServiceInfo);
    }
}
