using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace EnviousWispr.Services.Input;

internal static partial class WindowsClipboardPaste
{
    private const int ClipboardReadAttempts = 8;

    private static readonly TimeSpan ClipboardReadRetryDelay = TimeSpan.FromMilliseconds(25);

    private static readonly HashSet<string> OleWrapperFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "DataObject",
        "DataObjectAttributes",
        "DataObjectAttributesRequiringElevation",
        "Ole Private Data",
        "OleClipboardPersistOnFlush",
    };

    /// <summary>
    /// Captures all user clipboard formats via native Win32 clipboard APIs with bounded STA pumping
    /// retries on transient clipboard lock contention. Preserves custom binary Electron/Chromium
    /// HGLOBAL blobs byte-for-byte, omits only explicit OLE wrapper artifacts and OS-synthesized
    /// companions, and fails closed (returns null) if any user format cannot be captured intact.
    /// </summary>
    internal static ClipboardSnapshot? TrySnapshotClipboard() =>
        TrySnapshotClipboard(CancellationToken.None);

    internal static ClipboardSnapshot? TrySnapshotClipboard(CancellationToken cancellationToken) =>
        TrySnapshotClipboardWithRetry(
            TrySnapshotClipboardWin32Once,
            PumpingWait,
            cancellationToken);

    internal static string[]? TryGetClipboardFormatNames() =>
        TryClipboardReadWithRetry(
            TryGetClipboardFormatNamesWin32Once,
            PumpingWait,
            CancellationToken.None);

    internal static ClipboardSnapshot? TrySnapshotClipboardWithRetry(
        Func<ClipboardSnapshot?> readOnce,
        Action<TimeSpan> wait,
        CancellationToken cancellationToken = default) =>
        TryClipboardReadWithRetry(readOnce, wait, cancellationToken);

    private static T? TryClipboardReadWithRetry<T>(
        Func<T?> readOnce,
        Action<TimeSpan> wait,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readOnce);
        ArgumentNullException.ThrowIfNull(wait);

        for (var remaining = ClipboardReadAttempts; ; remaining--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return readOnce();
            }
            catch (ExternalException) when (remaining > 1)
            {
                wait(ClipboardReadRetryDelay);
            }
            catch (Exception exception) when (
                exception is ExternalException or ThreadStateException or
                    InvalidOperationException or ArgumentException)
            {
                return default;
            }
        }
    }

    internal static bool IsOleWrapperFormat(string? formatName) =>
        !string.IsNullOrWhiteSpace(formatName) && OleWrapperFormats.Contains(formatName.Trim());

    private static ClipboardSnapshot? TrySnapshotClipboardWin32Once()
    {
        var capture = TryCaptureNativeSnapshotInOpenClipboard();
        if (!capture.RequiresManagedFallback)
        {
            return capture.Snapshot;
        }

        // OpenClipboard has been closed: registered OLE IStream-backed formats (such as FileContents)
        // return NULL from GetClipboardData and must be read through OleGetClipboard / IDataObject.
        lock (ProcessClipboardGate)
        {
            return TrySnapshotRegisteredFallback(static () => Clipboard.GetDataObject());
        }
    }

    internal static ClipboardSnapshot? TrySnapshotRegisteredFallback(Func<System.Windows.Forms.IDataObject?> getDataObject)
    {
        ArgumentNullException.ThrowIfNull(getDataObject);
        return TrySnapshotDataObject(getDataObject());
    }

    private readonly record struct NativeCaptureAttempt(ClipboardSnapshot? Snapshot, bool RequiresManagedFallback);

    private static NativeCaptureAttempt TryCaptureNativeSnapshotInOpenClipboard()
    {
        lock (ProcessClipboardGate)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Marshal.ThrowExceptionForHR(ClipboardOpenErrorHr);
            }

            try
            {
                var formatIds = EnumerateClipboardFormatIds();
                if (formatIds is null)
                {
                    return new NativeCaptureAttempt(Snapshot: null, RequiresManagedFallback: false);
                }

                if (formatIds.Count == 0)
                {
                    return new NativeCaptureAttempt(
                        new ClipboardSnapshot(IsEmpty: true, Data: null),
                        RequiresManagedFallback: false);
                }

                var hasDib = formatIds.Contains(CfDib) || formatIds.Contains(CfDibV5);

                var entries = new List<NativeClipboardEntry>(formatIds.Count);
                foreach (var formatId in formatIds)
                {
                    string? formatName = null;
                    if (formatId >= RegisteredFormatBase)
                    {
                        formatName = TryGetClipboardFormatName(formatId);
                        if (formatName is null)
                        {
                            return new NativeCaptureAttempt(Snapshot: null, RequiresManagedFallback: false);
                        }

                        if (IsOleWrapperFormat(formatName) || IsTransientMarkerFormat(formatName))
                        {
                            continue;
                        }
                    }
                    else if (ShouldOmitSynthesizedFormat(formatId, hasDib))
                    {
                        continue;
                    }

                    var result = TryCaptureNativeEntryCore(formatId, formatName, out var entry);
                    if (result == NativeEntryCaptureStatus.RequiresManagedFallback)
                    {
                        return new NativeCaptureAttempt(Snapshot: null, RequiresManagedFallback: true);
                    }

                    if (result != NativeEntryCaptureStatus.Captured)
                    {
                        return new NativeCaptureAttempt(Snapshot: null, RequiresManagedFallback: false);
                    }

                    entries.Add(entry!);
                }

                return new NativeCaptureAttempt(
                    entries.Count == 0
                        ? new ClipboardSnapshot(IsEmpty: true, Data: null)
                        : new ClipboardSnapshot(IsEmpty: false, Data: null, NativeEntries: entries),
                    RequiresManagedFallback: false);
            }
            finally
            {
                _ = CloseClipboard();
            }
        }
    }

    private static string[]? TryGetClipboardFormatNamesWin32Once()
    {
        lock (ProcessClipboardGate)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Marshal.ThrowExceptionForHR(ClipboardOpenErrorHr);
            }

            try
            {
                var formatIds = EnumerateClipboardFormatIds();
                if (formatIds is null)
                {
                    return null;
                }

                var names = new List<string>(formatIds.Count);
                foreach (var formatId in formatIds)
                {
                    var name = TryDescribeClipboardFormat(formatId);
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }

                return [.. names];
            }
            finally
            {
                _ = CloseClipboard();
            }
        }
    }

    private static string? TryDescribeClipboardFormat(uint formatId) =>
        formatId >= RegisteredFormatBase
            ? TryGetClipboardFormatName(formatId)
            : formatId switch
            {
                CfText => DataFormats.Text,
                CfBitmap => DataFormats.Bitmap,
                CfMetaFilePict => DataFormats.MetafilePict,
                CfOemText => DataFormats.OemText,
                CfDib => DataFormats.Dib,
                CfPalette => DataFormats.Palette,
                CfUnicodeText => DataFormats.UnicodeText,
                CfEnhMetaFile => DataFormats.EnhancedMetafile,
                CfLocale => "Locale",
                _ => null,
            };

    private static List<uint>? EnumerateClipboardFormatIds()
    {
        var formatIds = new List<uint>();
        uint current = 0;
        while (true)
        {
            Marshal.SetLastPInvokeError(0);
            current = EnumClipboardFormats(current);
            if (current == 0)
            {
                return Marshal.GetLastPInvokeError() == 0
                    ? formatIds
                    : null;
            }

            formatIds.Add(current);
        }
    }

    private static string? TryGetClipboardFormatName(uint formatId)
    {
        var buffer = new char[256];
        var length = GetClipboardFormatName(formatId, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    internal static bool ShouldOmitSynthesizedFormat(
        uint formatId,
        bool hasDib) =>
        formatId is CfBitmap or CfPalette && hasDib;

    internal enum NativeEntryCaptureStatus
    {
        Captured,
        Failed,
        RequiresManagedFallback,
    }

    internal static bool TryCaptureNativeEntry(
        uint formatId,
        string? formatName,
        out NativeClipboardEntry? entry) =>
        TryCaptureNativeEntryCore(formatId, formatName, out entry) == NativeEntryCaptureStatus.Captured;

    internal static NativeEntryCaptureStatus TryCaptureNativeEntryCore(
        uint formatId,
        string? formatName,
        out NativeClipboardEntry? entry)
    {
        entry = null;
        if (formatId is CfOwnerDisplay or CfDspText or CfDspBitmap or CfDspMetaFilePict or CfDspEnhMetaFile ||
            (formatId >= CfGdiObjFirst && formatId <= CfGdiObjLast))
        {
            return NativeEntryCaptureStatus.Failed;
        }

        var handle = GetClipboardData(formatId);
        if (handle == IntPtr.Zero)
        {
            return formatId >= RegisteredFormatBase
                ? NativeEntryCaptureStatus.RequiresManagedFallback
                : NativeEntryCaptureStatus.Failed;
        }

        if (formatId == CfEnhMetaFile)
        {
            var size = GetEnhMetaFileBits(handle, 0, null);
            if (size == 0)
            {
                return NativeEntryCaptureStatus.Failed;
            }

            var bytes = new byte[size];
            if (GetEnhMetaFileBits(handle, size, bytes) != size)
            {
                return NativeEntryCaptureStatus.Failed;
            }

            entry = new NativeClipboardEntry(formatId, formatName, NativeClipboardEntryKind.EnhMetaFile, bytes);
            return NativeEntryCaptureStatus.Captured;
        }

        if (formatId == CfBitmap)
        {
            try
            {
                using var bitmap = Image.FromHbitmap(handle);
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                var bytes = ms.ToArray();
                if (bytes.Length == 0)
                {
                    return NativeEntryCaptureStatus.Failed;
                }

                entry = new NativeClipboardEntry(formatId, formatName, NativeClipboardEntryKind.BitmapPng, bytes);
                return NativeEntryCaptureStatus.Captured;
            }
            catch (Exception exception) when (exception is ArgumentException or ExternalException or InvalidOperationException)
            {
                return NativeEntryCaptureStatus.Failed;
            }
        }

        if (formatId == CfPalette)
        {
            return NativeEntryCaptureStatus.Failed;
        }

        if (formatId == CfMetaFilePict)
        {
            var pictSize = GlobalSize(handle);
            if (pictSize < (UIntPtr)Marshal.SizeOf<NativeMetaFilePict>())
            {
                return NativeEntryCaptureStatus.Failed;
            }

            var lockedPict = GlobalLock(handle);
            if (lockedPict == IntPtr.Zero)
            {
                return NativeEntryCaptureStatus.Failed;
            }

            try
            {
                var pict = Marshal.PtrToStructure<NativeMetaFilePict>(lockedPict);
                if (pict.MetaFile == IntPtr.Zero)
                {
                    return NativeEntryCaptureStatus.Failed;
                }

                var wmfSize = GetMetaFileBitsEx(pict.MetaFile, 0, null);
                if (wmfSize == 0)
                {
                    return NativeEntryCaptureStatus.Failed;
                }

                var wmfBits = new byte[wmfSize];
                if (GetMetaFileBitsEx(pict.MetaFile, wmfSize, wmfBits) != wmfSize)
                {
                    return NativeEntryCaptureStatus.Failed;
                }

                var packed = new byte[checked(12 + wmfBits.Length)];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(0, 4), pict.Mm);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(4, 4), pict.XExt);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(8, 4), pict.YExt);
                Buffer.BlockCopy(wmfBits, 0, packed, 12, wmfBits.Length);

                entry = new NativeClipboardEntry(formatId, formatName, NativeClipboardEntryKind.MetaFilePict, packed);
                return NativeEntryCaptureStatus.Captured;
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }

        var hGlobalSize = GlobalSize(handle);
        if (hGlobalSize == UIntPtr.Zero)
        {
            return NativeEntryCaptureStatus.Failed;
        }

        var rawSize = checked((int)hGlobalSize);
        var locked = GlobalLock(handle);
        if (locked == IntPtr.Zero)
        {
            return NativeEntryCaptureStatus.Failed;
        }

        try
        {
            var bytes = new byte[rawSize];
            Marshal.Copy(locked, bytes, 0, rawSize);
            entry = new NativeClipboardEntry(formatId, formatName, NativeClipboardEntryKind.HGlobal, bytes);
            return NativeEntryCaptureStatus.Captured;
        }
        finally
        {
            _ = GlobalUnlock(handle);
        }
    }

    private static void RestoreSnapshotOnce(ClipboardSnapshot snapshot, IntPtr ownerWindow)
    {
        if (snapshot.IsEmpty)
        {
            ClearOnce();
            return;
        }

        if (snapshot.NativeEntries is { Count: > 0 } nativeEntries)
        {
            RestoreNativeEntriesOnce(nativeEntries, ownerWindow);
            return;
        }

        lock (ProcessClipboardGate)
        {
            Clipboard.SetDataObject(BuildMarkedRestoreDataObject(snapshot.Data!), copy: true, retryTimes: 0, retryDelay: 0);
        }
    }

    private static DataObject BuildMarkedRestoreDataObject(DataObject source)
    {
        var copy = new DataObject();
        foreach (var format in source.GetFormats(autoConvert: false) ?? [])
        {
            if (string.IsNullOrWhiteSpace(format) || IsTransientMarkerFormat(format))
            {
                continue;
            }

            var value = ((System.Windows.Forms.IDataObject)source).GetData(format, autoConvert: false);
            if (value is not null)
            {
                copy.SetData(format, autoConvert: false, CloneClipboardValue(value) ?? value);
            }
        }

        AttachTransientMarkers(copy);
        return copy;
    }

    private static void AttachTransientMarkers(DataObject data)
    {
        // Zero DWORDs signal to Windows Clipboard History (Win+V) and Cloud Clipboard to exclude
        // temporary paste transfers and restored snapshots from recording duplicate history entries.
        data.SetData(ExcludeMonitorFormatName, autoConvert: false, new MemoryStream([0, 0, 0, 0]));
        data.SetData(CanIncludeInClipboardHistoryFormatName, autoConvert: false, new MemoryStream([0, 0, 0, 0]));
        data.SetData(CanUploadToCloudClipboardFormatName, autoConvert: false, new MemoryStream([0, 0, 0, 0]));
    }

    private static void RestoreNativeEntriesOnce(
        IReadOnlyList<NativeClipboardEntry> entries,
        IntPtr ownerWindow)
    {
        var prepared = PrepareNativeHandles(entries);
        try
        {
            lock (ProcessClipboardGate)
            {
                if (!OpenClipboard(ownerWindow))
                {
                    Marshal.ThrowExceptionForHR(ClipboardOpenErrorHr);
                }

                try
                {
                    if (!EmptyClipboard())
                    {
                        Marshal.ThrowExceptionForHR(ClipboardEmptyErrorHr);
                    }

                    for (var i = 0; i < prepared.Length; i++)
                    {
                        var item = prepared[i];
                        if (SetClipboardData(item.FormatId, item.Handle) == IntPtr.Zero)
                        {
                            Marshal.ThrowExceptionForHR(ClipboardSetErrorHr);
                        }

                        // SetClipboardData succeeded: Windows now owns the handle.
                        prepared[i] = item with { Handle = IntPtr.Zero };
                    }
                }
                finally
                {
                    _ = CloseClipboard();
                }
            }
        }
        finally
        {
            FreePreparedHandles(prepared);
        }
    }

    private static PreparedNativeHandle[] PrepareNativeHandles(IReadOnlyList<NativeClipboardEntry> entries)
    {
        var prepared = new List<PreparedNativeHandle>(entries.Count + TransientMarkerFormats.Count);
        var presentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var formatId = entry.FormatId;
                if (entry.FormatName is not null)
                {
                    presentNames.Add(entry.FormatName);
                    formatId = RegisterClipboardFormat(entry.FormatName);
                    if (formatId == 0)
                    {
                        throw new InvalidOperationException("Failed to register clipboard format during restore.");
                    }
                }

                var handle = AllocateNativeHandle(entry);
                prepared.Add(new PreparedNativeHandle(formatId, entry.Kind, handle));
            }

            foreach (var markerName in TransientMarkerFormats)
            {
                if (presentNames.Contains(markerName))
                {
                    continue;
                }

                var markerFormatId = RegisterClipboardFormat(markerName);
                if (markerFormatId == 0)
                {
                    continue;
                }

                var markerHandle = AllocateNativeHandle(new NativeClipboardEntry(
                    markerFormatId,
                    markerName,
                    NativeClipboardEntryKind.HGlobal,
                    [0, 0, 0, 0]));
                prepared.Add(new PreparedNativeHandle(markerFormatId, NativeClipboardEntryKind.HGlobal, markerHandle));
            }

            return prepared.ToArray();
        }
        catch
        {
            FreePreparedHandles(prepared.ToArray());
            throw;
        }
    }

    private static IntPtr AllocateNativeHandle(NativeClipboardEntry entry)
    {
        switch (entry.Kind)
        {
            case NativeClipboardEntryKind.EnhMetaFile:
            {
                var emf = SetEnhMetaFileBits((uint)entry.Bytes.Length, entry.Bytes);
                if (emf == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to reconstruct clipboard enhanced metafile.");
                }

                return emf;
            }
            case NativeClipboardEntryKind.BitmapPng:
            {
                using var ms = new MemoryStream(entry.Bytes, writable: false);
                using var bitmap = new Bitmap(ms);
                var hBmp = bitmap.GetHbitmap();
                if (hBmp == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to reconstruct clipboard bitmap.");
                }

                return hBmp;
            }
            case NativeClipboardEntryKind.MetaFilePict:
            {
                if (entry.Bytes.Length < 13)
                {
                    throw new InvalidOperationException("Invalid clipboard metafile picture payload.");
                }

                var wmfBytes = entry.Bytes.AsSpan(12).ToArray();
                var hMetaFile = SetMetaFileBitsEx((uint)wmfBytes.Length, wmfBytes);
                if (hMetaFile == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to reconstruct clipboard metafile.");
                }

                var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)Marshal.SizeOf<NativeMetaFilePict>());
                if (hGlobal == IntPtr.Zero)
                {
                    _ = DeleteMetaFile(hMetaFile);
                    throw new InvalidOperationException("Failed to allocate clipboard metafile picture handle.");
                }

                var locked = GlobalLock(hGlobal);
                if (locked == IntPtr.Zero)
                {
                    _ = GlobalFree(hGlobal);
                    _ = DeleteMetaFile(hMetaFile);
                    throw new InvalidOperationException("Failed to lock clipboard metafile picture handle.");
                }

                try
                {
                    var pict = new NativeMetaFilePict
                    {
                        Mm = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(entry.Bytes.AsSpan(0, 4)),
                        XExt = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(entry.Bytes.AsSpan(4, 4)),
                        YExt = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(entry.Bytes.AsSpan(8, 4)),
                        MetaFile = hMetaFile,
                    };
                    Marshal.StructureToPtr(pict, locked, fDeleteOld: false);
                }
                finally
                {
                    _ = GlobalUnlock(hGlobal);
                }

                return hGlobal;
            }
            default:
            {
                var size = Math.Max(1, entry.Bytes.Length);
                var hMem = GlobalAlloc(GmemMoveable, (UIntPtr)size);
                if (hMem == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to allocate global clipboard memory.");
                }

                var locked = GlobalLock(hMem);
                if (locked == IntPtr.Zero)
                {
                    _ = GlobalFree(hMem);
                    throw new InvalidOperationException("Failed to lock allocated clipboard memory.");
                }

                try
                {
                    if (entry.Bytes.Length > 0)
                    {
                        Marshal.Copy(entry.Bytes, 0, locked, entry.Bytes.Length);
                    }
                    else
                    {
                        Marshal.WriteByte(locked, 0);
                    }
                }
                finally
                {
                    _ = GlobalUnlock(hMem);
                }

                return hMem;
            }
        }
    }

    private static void FreePreparedHandles(PreparedNativeHandle[] prepared)
    {
        foreach (var item in prepared)
        {
            if (item.Handle == IntPtr.Zero)
            {
                continue;
            }

            switch (item.Kind)
            {
                case NativeClipboardEntryKind.EnhMetaFile:
                    _ = DeleteEnhMetaFile(item.Handle);
                    break;
                case NativeClipboardEntryKind.BitmapPng:
                    _ = DeleteObject(item.Handle);
                    break;
                case NativeClipboardEntryKind.MetaFilePict:
                {
                    var locked = GlobalLock(item.Handle);
                    if (locked != IntPtr.Zero)
                    {
                        try
                        {
                            var pict = Marshal.PtrToStructure<NativeMetaFilePict>(locked);
                            if (pict.MetaFile != IntPtr.Zero)
                            {
                                _ = DeleteMetaFile(pict.MetaFile);
                            }
                        }
                        finally
                        {
                            _ = GlobalUnlock(item.Handle);
                        }
                    }

                    _ = GlobalFree(item.Handle);
                    break;
                }
                default:
                    _ = GlobalFree(item.Handle);
                    break;
            }
        }
    }

    /// <summary>
    /// Fail-closed IDataObject snapshot helper (omits only explicit OLE wrapper bookkeeping formats).
    /// Fails with null or rethrows ExternalException whenever any user format cannot be captured whole.
    /// </summary>
    internal static ClipboardSnapshot? TrySnapshotDataObject(System.Windows.Forms.IDataObject? source)
    {
        if (source is null)
        {
            return new ClipboardSnapshot(IsEmpty: true, Data: null);
        }

        var formats = source.GetFormats(autoConvert: false);
        if (formats is null || formats.Length == 0)
        {
            return new ClipboardSnapshot(IsEmpty: true, Data: null);
        }

        var copy = new DataObject();
        var copiedFormats = 0;

        foreach (var format in formats)
        {
            if (string.IsNullOrWhiteSpace(format) || IsOleWrapperFormat(format) || IsTransientMarkerFormat(format))
            {
                continue;
            }

            try
            {
                var value = source.GetData(format, autoConvert: false);
                var cloned = value is null ? null : CloneClipboardValue(value);
                if (cloned is null)
                {
                    return null;
                }

                copy.SetData(format, autoConvert: false, cloned);
                copiedFormats++;
            }
            catch (ExternalException)
            {
                throw;
            }
            catch (Exception exception) when (IsClipboardFormatReadFailure(exception))
            {
                return null;
            }
        }

        return copiedFormats == 0
            ? new ClipboardSnapshot(IsEmpty: true, Data: null)
            : new ClipboardSnapshot(IsEmpty: false, Data: copy);
    }

    private static bool IsClipboardFormatReadFailure(Exception exception) =>
        exception is ExternalException or ThreadStateException or InvalidOperationException or
            ArgumentException or NotSupportedException or InvalidCastException or
            SerializationException or IOException;

    /// <summary>The whole of a seekable stream, from its start, with its position put back; null for a stream whose whole contents cannot be read without consuming it, or that fails to read.</summary>
    /// <remarks>
    /// A SNAPSHOT IS THE WHOLE VALUE OR NOTHING. A stream copied from where it happened to stand
    /// lost its prefix, and a stream that cannot seek is consumed by the copy - the clipboard would
    /// then hold less than it did. Both answer null, and a null refuses the snapshot, so nothing that
    /// borrows the clipboard proceeds against a clipboard it could not put back whole.
    /// </remarks>
    internal static MemoryStream? CloneStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            return null;
        }

        var originalPosition = stream.Position;
        MemoryStream? copy = null;
        try
        {
            stream.Position = 0;
            copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException or UnauthorizedAccessException)
        {
            copy = null;
        }

        // THE COPY COUNTS ONLY WITH THE SOURCE PUT BACK. A copy that read everything and then could
        // not return the stream to where it stood has changed the clipboard's value; that is a
        // refusal too, not a success with a note.
        try
        {
            stream.Position = originalPosition;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            copy = null;
        }

        return copy;
    }

    internal static object? CloneClipboardValue(object value)
    {
        switch (value)
        {
            case byte[] bytes:
                return bytes.ToArray();
            case MemoryStream memory:
                return new MemoryStream(memory.ToArray(), writable: false);
            case Stream stream:
                return CloneStream(stream);
            case Bitmap bitmap:
                return bitmap.Clone();
            case StringCollection strings:
            {
                var clone = new StringCollection();
                clone.AddRange(strings.Cast<string>().ToArray());
                return clone;
            }
            case string[] stringArray:
                return (string[])stringArray.Clone();
            case ICloneable cloneable:
                return cloneable.Clone();
            case string or char or bool or byte or sbyte or short or ushort or int or uint or
                long or ulong or float or double or decimal or DateTime or DateTimeOffset or
                TimeSpan or Guid:
                return value;
            default:
                return null;
        }
    }

    /// <summary>Puts a snapshot back with no guard at all: for the tests' own desk-clipboard guard, which restores what it took whatever happened since. Every restore the app makes goes through the guarded give-back instead.</summary>
    internal static bool TryRestoreClipboard(ClipboardSnapshot snapshot)
    {
        try
        {
            if (snapshot.IsEmpty)
            {
                WithPumpingRetry(ClearOnce);
            }
            else
            {
                using var owner = snapshot.NativeEntries is { Count: > 0 }
                    ? new ClipboardMessageWindow()
                    : null;
                WithPumpingRetry(() => RestoreSnapshotOnce(snapshot, owner?.Handle ?? IntPtr.Zero));
            }

            return true;
        }
        catch (Exception exception) when (
            exception is ExternalException or ThreadStateException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    internal enum NativeClipboardEntryKind
    {
        HGlobal,
        EnhMetaFile,
        BitmapPng,
        MetaFilePict,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMetaFilePict
    {
        public int Mm;
        public int XExt;
        public int YExt;
        public IntPtr MetaFile;
    }

    internal sealed record NativeClipboardEntry(
        uint FormatId,
        string? FormatName,
        NativeClipboardEntryKind Kind,
        byte[] Bytes);

    private struct PreparedNativeHandle(uint formatId, NativeClipboardEntryKind kind, IntPtr handle)
    {
        public uint FormatId { get; } = formatId;
        public NativeClipboardEntryKind Kind { get; } = kind;
        public IntPtr Handle { get; set; } = handle;
    }

    internal sealed record ClipboardSnapshot(
        bool IsEmpty,
        DataObject? Data,
        IReadOnlyList<NativeClipboardEntry>? NativeEntries = null);
}
