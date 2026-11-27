using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Services.Reliability;

public sealed class WindowsRecoveryTextStore : IRecoveryTextStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumTextCharacters = 1_000_000;
    private const int MaximumEnvelopeBytes = 3 * 1024 * 1024;
    private const uint CryptProtectUiForbidden = 0x1;

    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("EnviousLabs.EnviousWispr.RecoveryText.v1");

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _sourceInvalid;
    private bool _disposed;

    public WindowsRecoveryTextStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<RecoveryTextLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sourceInvalid = false;
            if (!File.Exists(_path))
            {
                return new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing);
            }

            var info = new FileInfo(_path);
            if (info.Length is <= 0 or > MaximumEnvelopeBytes)
            {
                _sourceInvalid = true;
                return Invalid();
            }

            var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<RecoveryEnvelope>(
                json,
                JsonSettingsStore.SerializerOptions);
            if (envelope is not
                {
                    SchemaVersion: CurrentSchemaVersion,
                    ProtectedText.Length: > 0,
                } ||
                envelope.SessionId == Guid.Empty ||
                envelope.CreatedAt == default)
            {
                _sourceInvalid = true;
                return Invalid();
            }

            var protectedBytes = Convert.FromBase64String(envelope.ProtectedText);
            if (protectedBytes.Length is <= 0 or > MaximumEnvelopeBytes)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                _sourceInvalid = true;
                return Invalid();
            }

            byte[] plaintextBytes;
            try
            {
                plaintextBytes = Unprotect(protectedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            try
            {
                var text = Encoding.UTF8.GetString(plaintextBytes);
                if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextCharacters)
                {
                    _sourceInvalid = true;
                    return Invalid();
                }

                return new RecoveryTextLoadResult(
                    RecoveryTextLoadStatus.Found,
                    new RecoveryTextRecord(
                        new DictationSessionId(envelope.SessionId),
                        envelope.CreatedAt,
                        text));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
        catch (Exception exception) when (exception is
            JsonException or
            FormatException or
            CryptographicException or
            Win32Exception)
        {
            _sourceInvalid = true;
            return Invalid();
        }
        catch (IOException)
        {
            return Unavailable(AppErrorCode.StorageUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return Unavailable(AppErrorCode.AccessDenied);
        }
        catch (SecurityException)
        {
            return Unavailable(AppErrorCode.AccessDenied);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SaveAsync(
        RecoveryTextRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.SessionId.Value == Guid.Empty ||
            record.CreatedAt == default ||
            string.IsNullOrWhiteSpace(record.Text) ||
            record.Text.Length > MaximumTextCharacters)
        {
            throw new ArgumentException("Recovery text must be bounded and well formed.", nameof(record));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sourceInvalid && File.Exists(_path))
            {
                File.Copy(_path, _path + ".previous", overwrite: true);
            }

            var plaintextBytes = Encoding.UTF8.GetBytes(record.Text);
            byte[] protectedBytes;
            try
            {
                protectedBytes = Protect(plaintextBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }

            try
            {
                var envelope = new RecoveryEnvelope(
                    CurrentSchemaVersion,
                    record.SessionId.Value,
                    record.CreatedAt,
                    Convert.ToBase64String(protectedBytes));
                await JsonSettingsStore.WriteAtomicallyAsync(envelope, _path, cancellationToken)
                    .ConfigureAwait(false);
                _sourceInvalid = false;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            SecurityException or
            CryptographicException or
            Win32Exception)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            File.Delete(_path);
            _sourceInvalid = false;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private static byte[] Protect(byte[] plaintext) => Transform(
        plaintext,
        protect: true);

    private static byte[] Unprotect(byte[] ciphertext) => Transform(
        ciphertext,
        protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        var entropyPointer = Marshal.AllocHGlobal(OptionalEntropy.Length);
        var output = default(DataBlob);
        IntPtr description = IntPtr.Zero;
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            Marshal.Copy(OptionalEntropy, 0, entropyPointer, OptionalEntropy.Length);
            var inputBlob = new DataBlob(input.Length, inputPointer);
            var entropyBlob = new DataBlob(OptionalEntropy.Length, entropyPointer);
            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    "EnviousWispr interrupted dictation recovery",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output)
                : CryptUnprotectData(
                    ref inputBlob,
                    out description,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output);
            if (!succeeded || output.Data == IntPtr.Zero || output.Length <= 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows data protection failed.");
            }

            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            ZeroNativeBuffer(inputPointer, input.Length);
            ZeroNativeBuffer(entropyPointer, OptionalEntropy.Length);
            Marshal.FreeHGlobal(inputPointer);
            Marshal.FreeHGlobal(entropyPointer);
            if (output.Data != IntPtr.Zero)
            {
                ZeroNativeBuffer(output.Data, output.Length);
                LocalFree(output.Data);
            }

            if (description != IntPtr.Zero)
            {
                LocalFree(description);
            }
        }
    }

    private static void ZeroNativeBuffer(IntPtr pointer, int length)
    {
        for (var index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }
    }

    private static RecoveryTextLoadResult Invalid() => new(
        RecoveryTextLoadStatus.Invalid,
        Error: new AppError(
            AppErrorCode.InvalidData,
            AppErrorStage.RecoveryText,
            CanRetry: false));

    private static RecoveryTextLoadResult Unavailable(AppErrorCode code) => new(
        RecoveryTextLoadStatus.Unavailable,
        Error: new AppError(code, AppErrorStage.RecoveryText, CanRetry: true));

    private sealed record RecoveryEnvelope(
        int SchemaVersion,
        Guid SessionId,
        DateTimeOffset CreatedAt,
        string ProtectedText);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob(int length, IntPtr data)
    {
        public int Length = length;

        public IntPtr Data = data;
    }

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
