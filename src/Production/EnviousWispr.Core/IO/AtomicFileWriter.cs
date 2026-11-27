using System.Security;
using System.Text;
using System.Text.Json;

namespace EnviousWispr.Core.IO;

/// <summary>
/// Canonical crash-safe atomic file writer: writes to a short temporary file in the destination's
/// parent directory, optionally flushes to disk, and moves into place with overwrite semantics.
/// </summary>
public static class AtomicFileWriter
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static Task<bool> WriteBytesAsync(
        string destinationPath,
        ReadOnlyMemory<byte> bytes,
        bool flushToDisk = false,
        bool createParentDirectory = true,
        Func<Action, bool>? commitMove = null,
        CancellationToken cancellationToken = default) =>
        WriteWithStreamAsync(
            destinationPath,
            (stream, ct) => stream.WriteAsync(bytes, ct).AsTask(),
            flushToDisk,
            createParentDirectory,
            commitMove,
            cancellationToken);

    public static bool WriteBytes(
        string destinationPath,
        ReadOnlySpan<byte> bytes,
        bool flushToDisk = false,
        bool createParentDirectory = true,
        Func<Action, bool>? commitMove = null)
    {
        var (fullPath, temporaryPath) = PreparePaths(destinationPath, createParentDirectory);
        var committed = false;
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                if (flushToDisk)
                {
                    stream.Flush(flushToDisk: true);
                }
            }

            committed = CommitMove(temporaryPath, fullPath, commitMove);
            return committed;
        }
        finally
        {
            if (!committed)
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
    }

    public static Task<bool> WriteTextAsync(
        string destinationPath,
        string text,
        bool flushToDisk = false,
        bool createParentDirectory = true,
        Func<Action, bool>? commitMove = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Utf8NoBom.GetBytes(text);
        return WriteBytesAsync(destinationPath, bytes, flushToDisk, createParentDirectory, commitMove, cancellationToken);
    }

    public static bool WriteText(
        string destinationPath,
        string text,
        bool flushToDisk = false,
        bool createParentDirectory = true,
        Func<Action, bool>? commitMove = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Utf8NoBom.GetBytes(text);
        return WriteBytes(destinationPath, bytes, flushToDisk, createParentDirectory, commitMove);
    }

    public static Task<bool> WriteLinesAsync(
        string destinationPath,
        IEnumerable<string> lines,
        bool flushToDisk = false,
        bool createParentDirectory = true,
        Func<Action, bool>? commitMove = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return WriteWithStreamAsync(
            destinationPath,
            async (stream, ct) =>
            {
                await using var writer = new StreamWriter(stream, Utf8NoBom, leaveOpen: true);
                foreach (var line in lines)
                {
                    ct.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                }

                await writer.FlushAsync(ct).ConfigureAwait(false);
            },
            flushToDisk,
            createParentDirectory,
            commitMove,
            cancellationToken);
    }

    public static bool WriteLines(
        string destinationPath,
        IEnumerable<string> lines,
        bool flushToDisk = false,
        bool createParentDirectory = true,
        Func<Action, bool>? commitMove = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return WriteWithStream(
            destinationPath,
            stream =>
            {
                using var writer = new StreamWriter(stream, Utf8NoBom, leaveOpen: true);
                foreach (var line in lines)
                {
                    writer.WriteLine(line);
                }

                writer.Flush();
            },
            flushToDisk,
            createParentDirectory,
            commitMove);
    }

    public static Task<bool> WriteJsonAsync<T>(
        string destinationPath,
        T value,
        JsonSerializerOptions? serializerOptions = null,
        bool flushToDisk = false,
        bool createParentDirectory = true,
        Func<Action, bool>? commitMove = null,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, serializerOptions);
        return WriteTextAsync(destinationPath, json, flushToDisk, createParentDirectory, commitMove, cancellationToken);
    }

    public static bool WriteJson<T>(
        string destinationPath,
        T value,
        JsonSerializerOptions? serializerOptions = null,
        bool flushToDisk = false,
        bool createParentDirectory = true,
        Func<Action, bool>? commitMove = null)
    {
        var json = JsonSerializer.Serialize(value, serializerOptions);
        return WriteText(destinationPath, json, flushToDisk, createParentDirectory, commitMove);
    }

    public static async Task<bool> WriteWithStreamAsync(
        string destinationPath,
        Func<Stream, CancellationToken, Task> writeStream,
        bool flushToDisk = false,
        bool createParentDirectory = true,
        Func<Action, bool>? commitMove = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeStream);
        var (fullPath, temporaryPath) = PreparePaths(destinationPath, createParentDirectory);
        var committed = false;
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await writeStream(stream, cancellationToken).ConfigureAwait(false);
                if (flushToDisk)
                {
                    stream.Flush(flushToDisk: true);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            committed = CommitMove(temporaryPath, fullPath, commitMove);
            return committed;
        }
        finally
        {
            if (!committed)
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
    }

    public static bool WriteWithStream(
        string destinationPath,
        Action<Stream> writeStream,
        bool flushToDisk = false,
        bool createParentDirectory = true,
        Func<Action, bool>? commitMove = null)
    {
        ArgumentNullException.ThrowIfNull(writeStream);
        var (fullPath, temporaryPath) = PreparePaths(destinationPath, createParentDirectory);
        var committed = false;
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writeStream(stream);
                if (flushToDisk)
                {
                    stream.Flush(flushToDisk: true);
                }
            }

            committed = CommitMove(temporaryPath, fullPath, commitMove);
            return committed;
        }
        finally
        {
            if (!committed)
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
    }

    private static (string FullPath, string TemporaryPath) PreparePaths(
        string destinationPath,
        bool createParentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The storage path must have a parent directory.");
        if (createParentDirectory)
        {
            Directory.CreateDirectory(directory);
        }

        // Use a short fixed temp name in the same directory so paths near 255 characters never overflow.
        var temporaryPath = Path.Combine(directory, $".ew-{Guid.NewGuid():N}.tmp");
        return (fullPath, temporaryPath);
    }

    private static bool CommitMove(string temporaryPath, string fullPath, Func<Action, bool>? commitMove)
    {
        if (commitMove is null)
        {
            File.Move(temporaryPath, fullPath, overwrite: true);
            return true;
        }

        return commitMove(() => File.Move(temporaryPath, fullPath, overwrite: true));
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Preserve the primary failure rather than masking it during temp cleanup.
        }
    }
}
