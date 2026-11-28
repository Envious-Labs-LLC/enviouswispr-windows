using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Reliability;

namespace EnviousWispr.Storage.Reliability;

/// <summary>
/// Persists per-session DPAPI-protected AES-GCM encrypted live PCM recovery spools so an unexpected crash,
/// power loss, or forced kill mid-dictation or mid-ASR can replay audio on the next launch.
/// </summary>
public sealed class RecoverySpoolStore : IRecoverySpoolStore
{
    private static ReadOnlySpan<byte> FileMagic => "EWREC1"u8;
    private const uint FrameMagic = 0x464D5245; // 'ERMF'
    private const byte FrameTypeAudio = 1;
    private const byte FrameTypeFinalizing = 2;
    private const int KeyLengthBytes = 32;
    private const int NonceLengthBytes = 12;
    private const int TagLengthBytes = 16;
    private const int FrameHeaderBytes = 4 + 1 + 4 + 8 + 4; // magic + type + chunkIndex + sampleOffset + payloadLen
    private const int ChunkAadBytes = 16 + 1 + 4 + 8 + 3; // sessionId + frameType + chunkIndex + sampleOffset + version tag
    private const int MaximumHeaderEnvelopeBytes = 64 * 1024;
    private const uint CryptProtectUiForbidden = 0x1;

    private static readonly byte[] DpapiEntropy =
        "EnviousWispr.RecoverySpool.Key.v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _directoryPath;
    private readonly Func<string, long?>? _availableDiskBytesProvider;
    private readonly Func<string, Stream>? _openWriteStreamForTesting;
    private readonly ConcurrentDictionary<string, byte> _inProcessOwnedFiles = new(StringComparer.OrdinalIgnoreCase);

    public RecoverySpoolStore(
        string? directoryPath = null,
        Func<string, long?>? availableDiskBytesProvider = null)
    {
        _directoryPath = string.IsNullOrWhiteSpace(directoryPath)
            ? DefaultDirectoryPath()
            : Path.GetFullPath(directoryPath);
        _availableDiskBytesProvider = availableDiskBytesProvider;
    }

    internal RecoverySpoolStore(
        string? directoryPath,
        Func<string, Stream> openWriteStreamForTesting)
        : this(directoryPath, availableDiskBytesProvider: null)
    {
        _openWriteStreamForTesting = openWriteStreamForTesting;
    }

    public string DirectoryPath => _directoryPath;

    public static string DefaultDirectoryPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Envious Labs",
        "EnviousWispr",
        RecoverySpoolConstants.DirectoryName);

    public async Task<IRecoverySpoolWriter?> OpenSessionAsync(
        RecoverySpoolHeader header,
        bool canPersistRecovery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (!canPersistRecovery || !HasDiskHeadroom())
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        byte[]? aesKey = null;
        byte[]? wrappedKey = null;
        try
        {
            Directory.CreateDirectory(_directoryPath);
            var filePath = Path.Combine(
                _directoryPath,
                $"{header.CreatedAt.UtcDateTime:yyyyMMddHHmmssfff}-{header.SessionId.Value:N}{RecoverySpoolConstants.FileExtension}");
            var fullPath = Path.GetFullPath(filePath);

            aesKey = RandomNumberGenerator.GetBytes(KeyLengthBytes);
            wrappedKey = DpapiTransform(aesKey, protect: true);

            var writer = await EncryptedSpoolWriter.CreateAsync(
                this,
                header,
                fullPath,
                aesKey,
                wrappedKey,
                cancellationToken).ConfigureAwait(false);
            aesKey = null; // Ownership transferred to writer.
            _inProcessOwnedFiles[fullPath] = 0;
            return writer;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception or CryptographicException)
        {
            return null;
        }
        finally
        {
            if (aesKey is not null)
            {
                CryptographicOperations.ZeroMemory(aesKey);
            }

            if (wrappedKey is not null)
            {
                Array.Clear(wrappedKey);
            }
        }
    }

    public bool HasCandidateFiles()
    {
        if (!Directory.Exists(_directoryPath))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(
                _directoryPath,
                $"*{RecoverySpoolConstants.FileExtension}",
                SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public IReadOnlyList<RecoverySpoolMetadata> ListOrphanSpools()
    {
        if (!Directory.Exists(_directoryPath))
        {
            return [];
        }

        var items = new List<RecoverySpoolMetadata>();
        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(_directoryPath, $"*{RecoverySpoolConstants.FileExtension}", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }

        foreach (var path in candidates)
        {
            var fullPath = Path.GetFullPath(path);
            if (_inProcessOwnedFiles.ContainsKey(fullPath))
            {
                continue;
            }

            if (TryInspectSpool(fullPath, out var metadata) && metadata is not null)
            {
                items.Add(metadata);
            }
        }

        items.Sort(static (left, right) => left.CreatedAt.CompareTo(right.CreatedAt));
        return items;
    }

    public async Task<RecoverySpoolReadResult> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(filePath);
        return await Task.Run(() => ReadInternal(fullPath, includeAudioSamples: true, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteSessionAsync(DictationSessionId sessionId)
    {
        if (sessionId.Value == Guid.Empty)
        {
            return Task.FromResult(false);
        }

        if (!Directory.Exists(_directoryPath))
        {
            return Task.FromResult(false);
        }

        var pattern = $"*-{sessionId.Value:N}{RecoverySpoolConstants.FileExtension}";
        string[] matches;
        try
        {
            matches = Directory.GetFiles(_directoryPath, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Task.FromResult(false);
        }

        var deletedAny = true;
        foreach (var match in matches)
        {
            if (!DeletePathInternal(Path.GetFullPath(match)))
            {
                deletedAny = false;
            }
        }

        return Task.FromResult(matches.Length > 0 && deletedAny);
    }

    public Task<bool> DeleteFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(DeletePathInternal(Path.GetFullPath(filePath)));
    }

    private void ReleaseOwnedFileTracking(string fullPath) =>
        _inProcessOwnedFiles.TryRemove(fullPath, out _);

    private bool DeletePathInternal(string fullPath)
    {
        _inProcessOwnedFiles.TryRemove(fullPath, out _);
        try
        {
            if (!File.Exists(fullPath))
            {
                return false;
            }

            File.Delete(fullPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private bool HasDiskHeadroom()
    {
        try
        {
            var probePath = Directory.Exists(_directoryPath)
                ? _directoryPath
                : Path.GetDirectoryName(_directoryPath) ?? _directoryPath;
            var available = _availableDiskBytesProvider is not null
                ? _availableDiskBytesProvider(probePath)
                : TryGetFreeDiskBytes(probePath);
            return available is null or >= SystemResourceAdmissionPolicy.MinimumRecoveryDiskBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return true;
        }
    }

    private static long? TryGetFreeDiskBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var drive = new DriveInfo(root);
        return drive.IsReady ? drive.AvailableFreeSpace : null;
    }

    private static bool TryInspectSpool(string fullPath, out RecoverySpoolMetadata? metadata)
    {
        metadata = null;
        try
        {
            var read = ReadInternal(fullPath, includeAudioSamples: false, CancellationToken.None);
            if (!read.Succeeded || read.Header is null)
            {
                return false;
            }

            var length = new FileInfo(fullPath).Length;
            metadata = new RecoverySpoolMetadata(
                read.Header.SessionId,
                fullPath,
                read.Header.CreatedAt,
                read.Header.SampleRate,
                read.Header.Channels,
                read.Header.LanguageCode,
                read.Header.AutoLanguageDetection,
                read.Status,
                read.SampleCount,
                length);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static RecoverySpoolReadResult ReadInternal(string fullPath, bool includeAudioSamples, CancellationToken cancellationToken)
    {
        if (!File.Exists(fullPath))
        {
            return Failed("Spool file no longer exists.");
        }

        byte[]? aesKey = null;
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> prelude = stackalloc byte[12];
            if (!TryReadExact(stream, prelude))
            {
                return Failed("Spool header is incomplete.");
            }

            if (!prelude[..6].SequenceEqual(FileMagic))
            {
                return Failed("Spool file magic mismatch.");
            }

            var version = BinaryPrimitives.ReadUInt16LittleEndian(prelude.Slice(6, 2));
            if (version != RecoverySpoolConstants.FormatVersion)
            {
                return Failed($"Unsupported spool format version {version}.");
            }

            var wrappedKeyLength = BinaryPrimitives.ReadInt32LittleEndian(prelude.Slice(8, 4));
            if (wrappedKeyLength is <= 0 or > 16 * 1024)
            {
                return Failed("Wrapped session key length is invalid.");
            }

            var wrappedKey = new byte[wrappedKeyLength];
            if (!TryReadExact(stream, wrappedKey))
            {
                return Failed("Wrapped session key is truncated.");
            }

            aesKey = DpapiTransform(wrappedKey, protect: false);
            if (aesKey.Length != KeyLengthBytes)
            {
                return Failed("Unwrapped session key length is invalid.");
            }

            Span<byte> headerLenBuf = stackalloc byte[4];
            if (!TryReadExact(stream, headerLenBuf))
            {
                return Failed("Header envelope length missing.");
            }

            var headerCipherLength = BinaryPrimitives.ReadInt32LittleEndian(headerLenBuf);
            if (headerCipherLength is <= 0 or > MaximumHeaderEnvelopeBytes)
            {
                return Failed("Header envelope size is invalid.");
            }

            var headerNonce = new byte[NonceLengthBytes];
            var headerTag = new byte[TagLengthBytes];
            var headerCipher = new byte[headerCipherLength];
            if (!TryReadExact(stream, headerNonce) ||
                !TryReadExact(stream, headerTag) ||
                !TryReadExact(stream, headerCipher))
            {
                return Failed("Encrypted header envelope is truncated.");
            }

            var headerPlain = new byte[headerCipherLength];
            using var aesGcm = new AesGcm(aesKey, TagLengthBytes);
            aesGcm.Decrypt(
                headerNonce,
                headerCipher,
                headerTag,
                headerPlain,
                Encoding.UTF8.GetBytes("ewrec:hdr:v1"));

            var header = JsonSerializer.Deserialize<RecoverySpoolHeader>(headerPlain, JsonOptions);
            CryptographicOperations.ZeroMemory(headerPlain);
            if (header is null ||
                header.SessionId.Value == Guid.Empty ||
                header.SampleRate <= 0 ||
                header.Channels <= 0)
            {
                return Failed("Decrypted spool header is invalid.");
            }

            var samples = includeAudioSamples ? new List<float>() : null;
            var status = RecoverySpoolStatus.Recording;
            uint expectedChunkIndex = 0;
            long expectedSampleOffset = 0;
            var truncatedTail = false;
            var headerBuf = new byte[FrameHeaderBytes];
            var aadBuf = new byte[ChunkAadBytes];
            var nonce = new byte[NonceLengthBytes];
            var tag = new byte[TagLengthBytes];

            while (stream.Position < stream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frameStart = stream.Position;
                if (!TryReadExact(stream, headerBuf))
                {
                    truncatedTail = true;
                    break;
                }

                var magic = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(0, 4));
                var frameType = headerBuf[4];
                var chunkIndex = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(5, 4));
                var sampleOffset = BinaryPrimitives.ReadInt64LittleEndian(headerBuf.AsSpan(9, 8));
                var payloadBytes = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(17, 4));

                if (magic != FrameMagic ||
                    chunkIndex != expectedChunkIndex ||
                    sampleOffset != expectedSampleOffset ||
                    payloadBytes < 0 ||
                    payloadBytes > RecoverySpoolConstants.MaximumFrameSamples * sizeof(float) ||
                    (frameType == FrameTypeAudio && payloadBytes % sizeof(float) != 0) ||
                    (frameType != FrameTypeAudio && frameType != FrameTypeFinalizing))
                {
                    truncatedTail = stream.Length > frameStart;
                    break;
                }

                if (!includeAudioSamples)
                {
                    var frameBodyBytes = (long)NonceLengthBytes + TagLengthBytes + payloadBytes;
                    if (stream.Length - stream.Position < frameBodyBytes)
                    {
                        truncatedTail = true;
                        break;
                    }

                    if (frameType == FrameTypeAudio)
                    {
                        var frameSamples = payloadBytes / sizeof(float);
                        if (expectedSampleOffset + frameSamples > RecoverySpoolConstants.MaximumSpoolSamples)
                        {
                            truncatedTail = true;
                            break;
                        }

                        expectedSampleOffset += frameSamples;
                    }
                    else if (frameType == FrameTypeFinalizing)
                    {
                        status = RecoverySpoolStatus.Finalizing;
                    }

                    stream.Seek(frameBodyBytes, SeekOrigin.Current);
                    expectedChunkIndex++;
                    continue;
                }

                if (!TryReadExact(stream, nonce) || !TryReadExact(stream, tag))
                {
                    truncatedTail = true;
                    break;
                }

                var cipher = new byte[payloadBytes];
                if (payloadBytes > 0 && !TryReadExact(stream, cipher))
                {
                    truncatedTail = true;
                    break;
                }

                var plain = new byte[payloadBytes];
                try
                {
                    BuildChunkAad(header.SessionId.Value, frameType, chunkIndex, sampleOffset, aadBuf);
                    aesGcm.Decrypt(nonce, cipher, tag, plain, aadBuf);
                }
                catch (CryptographicException)
                {
                    CryptographicOperations.ZeroMemory(plain);
                    truncatedTail = true;
                    break;
                }

                if (frameType == FrameTypeAudio)
                {
                    var frameSamples = payloadBytes / sizeof(float);
                    if (expectedSampleOffset + frameSamples > RecoverySpoolConstants.MaximumSpoolSamples)
                    {
                        CryptographicOperations.ZeroMemory(plain);
                        truncatedTail = true;
                        break;
                    }

                    var floatCount = frameSamples;
                    var existing = samples!.Count;
                    samples.EnsureCapacity(existing + floatCount);
                    var floats = MemoryMarshal.Cast<byte, float>(plain.AsSpan());
                    for (var i = 0; i < floats.Length; i++)
                    {
                        samples.Add(floats[i]);
                    }

                    expectedSampleOffset += floatCount;
                }
                else if (frameType == FrameTypeFinalizing)
                {
                    status = RecoverySpoolStatus.Finalizing;
                }

                CryptographicOperations.ZeroMemory(plain);
                expectedChunkIndex++;
            }

            return new RecoverySpoolReadResult(
                Succeeded: true,
                Header: header,
                Samples: samples is not null ? [.. samples] : [],
                Status: status,
                TruncatedTail: truncatedTail,
                SampleCount: expectedSampleOffset);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception or CryptographicException or JsonException)
        {
            return Failed(ex.Message);
        }
        finally
        {
            if (aesKey is not null)
            {
                CryptographicOperations.ZeroMemory(aesKey);
            }
        }

        static RecoverySpoolReadResult Failed(string message) =>
            new(Succeeded: false, Header: null, Samples: [], Status: RecoverySpoolStatus.Recording, TruncatedTail: false, ErrorMessage: message);
    }

    private static bool TryReadExact(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    private static void BuildChunkAad(
        Guid sessionId,
        byte frameType,
        uint chunkIndex,
        long sampleOffset,
        Span<byte> destination)
    {
        sessionId.TryWriteBytes(destination[..16]);
        destination[16] = frameType;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(17, 4), chunkIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(21, 8), unchecked((ulong)sampleOffset));
        destination[29] = (byte)'E';
        destination[30] = (byte)'W';
        destination[31] = RecoverySpoolConstants.FormatVersion;
    }

    private static byte[] DpapiTransform(byte[] input, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        var entropyPointer = Marshal.AllocHGlobal(DpapiEntropy.Length);
        var output = default(DataBlob);
        IntPtr description = IntPtr.Zero;
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            Marshal.Copy(DpapiEntropy, 0, entropyPointer, DpapiEntropy.Length);
            var inputBlob = new DataBlob(input.Length, inputPointer);
            var entropyBlob = new DataBlob(DpapiEntropy.Length, entropyPointer);
            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    "EnviousWispr encrypted dictation audio spool",
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
            ZeroNativeBuffer(entropyPointer, DpapiEntropy.Length);
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

    private sealed class EncryptedSpoolWriter : IRecoverySpoolWriter
    {
        private enum CommandKind
        {
            Append,
            Flush,
            MarkFinalizing,
            Discard,
        }

        private readonly record struct WriterCommand(
            CommandKind Kind,
            float[]? Samples = null,
            TaskCompletionSource? Completion = null);

        private readonly RecoverySpoolStore _owner;
        private readonly RecoverySpoolHeader _header;
        private readonly byte[] _aesKey;
        private readonly Stream _stream;
        private readonly AesGcm _aesGcm;
        private readonly Channel<WriterCommand> _commands;
        private readonly Task _writerLoop;
        private readonly object _stateLock = new();
        private long _samplesEnqueued;
        private long _samplesWritten;
        private uint _chunkIndex;
        private Exception? _failure;
        private bool _stopped;
        private bool _deleted;
        private bool _disposed;

        private EncryptedSpoolWriter(
            RecoverySpoolStore owner,
            RecoverySpoolHeader header,
            string filePath,
            byte[] aesKey,
            Stream stream)
        {
            _owner = owner;
            _header = header;
            FilePath = filePath;
            _aesKey = aesKey;
            _stream = stream;
            _aesGcm = new AesGcm(aesKey, TagLengthBytes);
            _commands = Channel.CreateUnbounded<WriterCommand>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
            _writerLoop = Task.Run(RunWriterLoopAsync);
        }

        public DictationSessionId SessionId => _header.SessionId;

        public string FilePath { get; }

        public long SamplesWritten => Interlocked.Read(ref _samplesWritten);

        public static async Task<EncryptedSpoolWriter> CreateAsync(
            RecoverySpoolStore owner,
            RecoverySpoolHeader header,
            string filePath,
            byte[] aesKey,
            byte[] wrappedKey,
            CancellationToken cancellationToken)
        {
            Stream? stream = null;
            try
            {
                stream = owner._openWriteStreamForTesting?.Invoke(filePath)
                    ?? new FileStream(
                        filePath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        bufferSize: 64 * 1024,
                        FileOptions.None);

                using var aes = new AesGcm(aesKey, TagLengthBytes);
                var headerPlain = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
                var headerNonce = RandomNumberGenerator.GetBytes(NonceLengthBytes);
                var headerTag = new byte[TagLengthBytes];
                var headerCipher = new byte[headerPlain.Length];
                try
                {
                    aes.Encrypt(
                        headerNonce,
                        headerPlain,
                        headerCipher,
                        headerTag,
                        Encoding.UTF8.GetBytes("ewrec:hdr:v1"));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(headerPlain);
                }

                var prelude = new byte[12 + wrappedKey.Length + 4 + NonceLengthBytes + TagLengthBytes + headerCipher.Length];
                FileMagic.CopyTo(prelude.AsSpan(0, 6));
                BinaryPrimitives.WriteUInt16LittleEndian(prelude.AsSpan(6, 2), RecoverySpoolConstants.FormatVersion);
                BinaryPrimitives.WriteInt32LittleEndian(prelude.AsSpan(8, 4), wrappedKey.Length);
                wrappedKey.CopyTo(prelude.AsSpan(12));
                var offset = 12 + wrappedKey.Length;
                BinaryPrimitives.WriteInt32LittleEndian(prelude.AsSpan(offset, 4), headerCipher.Length);
                offset += 4;
                headerNonce.CopyTo(prelude.AsSpan(offset));
                offset += NonceLengthBytes;
                headerTag.CopyTo(prelude.AsSpan(offset));
                offset += TagLengthBytes;
                headerCipher.CopyTo(prelude.AsSpan(offset));

                await stream.WriteAsync(prelude, cancellationToken).ConfigureAwait(false);
                FlushStream(stream);

                var writer = new EncryptedSpoolWriter(owner, header, filePath, aesKey, stream);
                stream = null;
                return writer;
            }
            finally
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    try
                    {
                        File.Delete(filePath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        public void EnqueueSamples(ReadOnlyMemory<float> samples)
        {
            if (samples.IsEmpty)
            {
                return;
            }

            lock (_stateLock)
            {
                if (_failure is not null || _stopped || _disposed)
                {
                    return;
                }

                var current = _samplesEnqueued;
                if (current >= RecoverySpoolConstants.MaximumSpoolSamples)
                {
                    return;
                }

                var allowed = (int)Math.Min(
                    samples.Length,
                    RecoverySpoolConstants.MaximumSpoolSamples - current);
                if (allowed <= 0)
                {
                    return;
                }

                _samplesEnqueued = current + allowed;
                var copy = samples.Span[..allowed].ToArray();
                _commands.Writer.TryWrite(new WriterCommand(CommandKind.Append, copy));
            }
        }

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateLock)
            {
                if (_failure is { } failure)
                {
                    return Task.FromException(failure);
                }

                if (_stopped || _disposed)
                {
                    return Task.CompletedTask;
                }

                _commands.Writer.TryWrite(new WriterCommand(CommandKind.Flush, Completion: completion));
            }

            return completion.Task.WaitAsync(cancellationToken);
        }

        public async Task MarkFinalizingAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource? completion = null;
            lock (_stateLock)
            {
                if (_failure is { } failure)
                {
                    throw failure;
                }

                if (_disposed)
                {
                    return;
                }

                if (!_stopped)
                {
                    _stopped = true;
                    completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _commands.Writer.TryWrite(new WriterCommand(CommandKind.MarkFinalizing, Completion: completion));
                    _commands.Writer.TryComplete();
                }
            }

            if (completion is not null)
            {
                await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await _writerLoop.ConfigureAwait(false);
        }

        public async Task DiscardAndDeleteAsync()
        {
            TaskCompletionSource? completion = null;
            lock (_stateLock)
            {
                if (_disposed)
                {
                    _owner.DeletePathInternal(FilePath);
                    return;
                }

                if (!_stopped)
                {
                    _stopped = true;
                    completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _commands.Writer.TryWrite(new WriterCommand(CommandKind.Discard, Completion: completion));
                    _commands.Writer.TryComplete();
                }
            }

            if (completion is not null)
            {
                await completion.Task.ConfigureAwait(false);
            }

            await _writerLoop.ConfigureAwait(false);
            _owner.DeletePathInternal(FilePath);
            _deleted = true;
        }

        public async ValueTask DisposeAsync()
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (!_stopped)
                {
                    _stopped = true;
                    _commands.Writer.TryComplete();
                }
            }

            try
            {
                await _writerLoop.ConfigureAwait(false);
            }
            finally
            {
                _aesGcm.Dispose();
                CryptographicOperations.ZeroMemory(_aesKey);
                await _stream.DisposeAsync().ConfigureAwait(false);
                if (_deleted)
                {
                    _owner.DeletePathInternal(FilePath);
                }
            }
        }

        private async Task RunWriterLoopAsync()
        {
            var aad = new byte[ChunkAadBytes];
            var framePrefix = new byte[FrameHeaderBytes + NonceLengthBytes + TagLengthBytes];
            var nonce = new byte[NonceLengthBytes];
            var tag = new byte[TagLengthBytes];

            TaskCompletionSource? activeCompletion = null;
            try
            {
                while (await _commands.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (_commands.Reader.TryRead(out var command))
                    {
                        if (command.Kind == CommandKind.Discard)
                        {
                            DrainDiscardedCommands(command.Completion);
                            return;
                        }

                        if (command.Kind == CommandKind.Append && command.Samples is { } firstSamples)
                        {
                            var batch = new List<float[]> { firstSamples };
                            var totalSamples = firstSamples.Length;
                            var sawControl = false;
                            var sawDiscard = false;
                            WriterCommand pendingControl = default;

                            while (totalSamples < RecoverySpoolConstants.MaximumFrameSamples &&
                                   _commands.Reader.TryRead(out var next))
                            {
                                if (next.Kind == CommandKind.Discard)
                                {
                                    sawDiscard = true;
                                    pendingControl = next;
                                    break;
                                }

                                if (next.Kind == CommandKind.Append && next.Samples is { } moreSamples)
                                {
                                    batch.Add(moreSamples);
                                    totalSamples += moreSamples.Length;
                                    continue;
                                }

                                sawControl = true;
                                pendingControl = next;
                                break;
                            }

                            if (sawDiscard)
                            {
                                DrainDiscardedCommands(pendingControl.Completion);
                                return;
                            }

                            activeCompletion = pendingControl.Completion;
                            WriteAudioBatch(batch, totalSamples, framePrefix, nonce, tag, aad);
                            FlushStream(_stream);

                            if (sawControl)
                            {
                                HandleControlCommand(pendingControl, framePrefix, nonce, tag, aad);
                                activeCompletion = null;
                                if (pendingControl.Kind == CommandKind.MarkFinalizing)
                                {
                                    return;
                                }
                            }
                            else
                            {
                                activeCompletion = null;
                            }

                            continue;
                        }

                        activeCompletion = command.Completion;
                        HandleControlCommand(command, framePrefix, nonce, tag, aad);
                        activeCompletion = null;
                        if (command.Kind == CommandKind.MarkFinalizing)
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or CryptographicException)
            {
                DrainFailedCommands(ex, activeCompletion);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(aad);
                CryptographicOperations.ZeroMemory(framePrefix);
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(tag);
            }
        }

        private void HandleControlCommand(
            WriterCommand command,
            byte[] framePrefix,
            byte[] nonce,
            byte[] tag,
            byte[] aad)
        {
            if (command.Kind == CommandKind.Flush)
            {
                FlushStream(_stream);
                command.Completion?.TrySetResult();
            }
            else if (command.Kind == CommandKind.MarkFinalizing)
            {
                WriteControlFrame(FrameTypeFinalizing, framePrefix, nonce, tag, aad);
                FlushStream(_stream);
                command.Completion?.TrySetResult();
            }
        }

        private static void FlushStream(Stream stream)
        {
            if (stream is FileStream fs)
            {
                fs.Flush(flushToDisk: true);
            }
            else
            {
                stream.Flush();
            }
        }

        private void DrainDiscardedCommands(TaskCompletionSource? immediateCompletion)
        {
            immediateCompletion?.TrySetResult();
            while (_commands.Reader.TryRead(out var leftover))
            {
                if (leftover.Samples is { } arr)
                {
                    Array.Clear(arr);
                }

                leftover.Completion?.TrySetResult();
            }
        }

        private void DrainFailedCommands(Exception exception, TaskCompletionSource? activeCompletion)
        {
            lock (_stateLock)
            {
                _failure ??= exception;
                _stopped = true;
                _commands.Writer.TryComplete();
            }

            activeCompletion?.TrySetException(exception);
            while (_commands.Reader.TryRead(out var leftover))
            {
                if (leftover.Samples is { } arr)
                {
                    Array.Clear(arr);
                }

                leftover.Completion?.TrySetException(exception);
            }
        }

        private void WriteAudioBatch(
            List<float[]> batch,
            int totalSamples,
            byte[] framePrefix,
            byte[] nonce,
            byte[] tag,
            byte[] aad)
        {
            var payloadBytes = totalSamples * sizeof(float);
            var plain = new byte[payloadBytes];
            var cipher = new byte[payloadBytes];
            try
            {
                var sampleCursor = 0;
                foreach (var chunk in batch)
                {
                    var byteSpan = MemoryMarshal.AsBytes(chunk.AsSpan());
                    byteSpan.CopyTo(plain.AsSpan(sampleCursor * sizeof(float)));
                    Array.Clear(chunk);
                    sampleCursor += chunk.Length;
                }

                var sampleOffset = Interlocked.Read(ref _samplesWritten);
                var chunkIndex = _chunkIndex++;
                RandomNumberGenerator.Fill(nonce);
                BuildChunkAad(_header.SessionId.Value, FrameTypeAudio, chunkIndex, sampleOffset, aad);
                _aesGcm.Encrypt(nonce, plain, cipher, tag, aad);

                BinaryPrimitives.WriteUInt32LittleEndian(framePrefix.AsSpan(0, 4), FrameMagic);
                framePrefix[4] = FrameTypeAudio;
                BinaryPrimitives.WriteUInt32LittleEndian(framePrefix.AsSpan(5, 4), chunkIndex);
                BinaryPrimitives.WriteInt64LittleEndian(framePrefix.AsSpan(9, 8), sampleOffset);
                BinaryPrimitives.WriteInt32LittleEndian(framePrefix.AsSpan(17, 4), payloadBytes);
                nonce.CopyTo(framePrefix.AsSpan(FrameHeaderBytes));
                tag.CopyTo(framePrefix.AsSpan(FrameHeaderBytes + NonceLengthBytes));

                _stream.Write(framePrefix);
                _stream.Write(cipher);
                Interlocked.Add(ref _samplesWritten, totalSamples);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
                CryptographicOperations.ZeroMemory(cipher);
            }
        }

        private void WriteControlFrame(
            byte frameType,
            byte[] framePrefix,
            byte[] nonce,
            byte[] tag,
            byte[] aad)
        {
            var sampleOffset = Interlocked.Read(ref _samplesWritten);
            var chunkIndex = _chunkIndex++;
            RandomNumberGenerator.Fill(nonce);
            BuildChunkAad(_header.SessionId.Value, frameType, chunkIndex, sampleOffset, aad);
            _aesGcm.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag, aad);

            BinaryPrimitives.WriteUInt32LittleEndian(framePrefix.AsSpan(0, 4), FrameMagic);
            framePrefix[4] = frameType;
            BinaryPrimitives.WriteUInt32LittleEndian(framePrefix.AsSpan(5, 4), chunkIndex);
            BinaryPrimitives.WriteInt64LittleEndian(framePrefix.AsSpan(9, 8), sampleOffset);
            BinaryPrimitives.WriteInt32LittleEndian(framePrefix.AsSpan(17, 4), 0);
            nonce.CopyTo(framePrefix.AsSpan(FrameHeaderBytes));
            tag.CopyTo(framePrefix.AsSpan(FrameHeaderBytes + NonceLengthBytes));
            _stream.Write(framePrefix);
        }
    }
}
