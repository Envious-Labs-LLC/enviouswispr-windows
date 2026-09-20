using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace EnviousWispr.ModelDelivery;

/// <summary>How one transfer attempt ended.</summary>
public enum DownloadAttemptResult
{
    /// <summary>Every byte the manifest promised is in the partial file.</summary>
    Complete,

    /// <summary>The source may answer differently next time: a transient status, a short read, an inactivity timeout.</summary>
    TransientFailure,

    /// <summary>The source answered in a way that will not improve: a refusal, a range it cannot honour, more bytes than promised.</summary>
    PermanentFailure,
}

/// <param name="RetryAfter">The source's own idea of when to try again, bounded; null when it gave none.</param>
public readonly record struct DownloadAttemptOutcome(
    DownloadAttemptResult Result,
    TimeSpan? RetryAfter = null);

/// <summary>One transfer attempt of one artifact from one source, into a partial file, resumable.</summary>
/// <remarks>
/// THE HTTP MECHANICS AND NOTHING ELSE. Which sources to try, how many times, what a failure means for
/// the installation, and what happens to a complete file are the store's decisions; this knows how
/// to ask a source for the bytes it does not yet have and put them at the end of the partial file.
///
/// A RESUME IS ONLY OFFERED WHEN THE SERVER CAN SAY THE FILE HAS NOT CHANGED. The partial file's
/// length is the offset; the resume record beside it names the source and the validator - an ETag
/// or a last-modified date - that the range request is made conditional on. A partial file with no
/// record, a record from another source, or a record with no validator is thrown away rather than
/// appended to, because bytes from a file that may have changed are worse than none.
///
/// BOUNDED READS, ALWAYS. Every read waits at most the request timeout, and a response that keeps
/// going past the promised size is a permanent failure at the first byte over: a source that lies
/// about size is not a source to keep reading from.
/// </remarks>
public sealed class ArtifactDownloadTransport
{
    private const int BufferSize = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly IModelDeliveryObserver _observer;
    private readonly TimeSpan _requestTimeout;

    /// <param name="requestTimeout">
    /// The longest any single wait on the source may take: the headers, then each read. Any value the
    /// cancellation timer accepts is accepted here, as it was when the store armed the timer itself.
    /// </param>
    public ArtifactDownloadTransport(HttpClient httpClient, TimeSpan requestTimeout, IModelDeliveryObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _requestTimeout = requestTimeout;
        _observer = observer ?? NullModelDeliveryObserver.Instance;
    }

    /// <summary>Fetches what the partial file does not yet hold, from one source, once.</summary>
    /// <param name="partialPath">Where the bytes accumulate; its length is the resume offset.</param>
    /// <param name="resumePath">The record of which source and validator the partial file belongs to.</param>
    /// <param name="completedBeforeArtifact">Bytes of earlier artifacts, for the progress events.</param>
    /// <param name="totalBytes">The whole installation's size, for the progress events.</param>
    /// <exception cref="OperationCanceledException">The caller cancelled, or a wait on the source exceeded the request timeout.</exception>
    /// <exception cref="HttpRequestException">The transport failed before a status could be read.</exception>
    public async Task<DownloadAttemptOutcome> DownloadAttemptAsync(
        Uri source,
        ModelArtifact artifact,
        string partialPath,
        string resumePath,
        long completedBeforeArtifact,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(partialPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(resumePath);

        var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (offset > artifact.SizeBytes)
        {
            File.Delete(partialPath);
            DeliveryFiles.DeleteIfExists(resumePath);
            offset = 0;
        }

        ResumeMetadata? resume = null;
        if (offset > 0 && File.Exists(resumePath))
        {
            try
            {
                resume = JsonSerializer.Deserialize<ResumeMetadata>(
                    await File.ReadAllBytesAsync(resumePath, cancellationToken).ConfigureAwait(false),
                    JsonOptions);
            }
            catch (JsonException)
            {
                resume = null;
            }
        }

        if (offset > 0 &&
            (resume is null || !string.Equals(resume.Source, source.AbsoluteUri, StringComparison.Ordinal)))
        {
            File.Delete(partialPath);
            DeliveryFiles.DeleteIfExists(resumePath);
            offset = 0;
            resume = null;
        }

        if (offset > 0 &&
            string.IsNullOrWhiteSpace(resume?.ETag) &&
            resume?.LastModified is null)
        {
            File.Delete(partialPath);
            DeliveryFiles.DeleteIfExists(resumePath);
            offset = 0;
            resume = null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            if (!string.IsNullOrWhiteSpace(resume?.ETag))
            {
                request.Headers.TryAddWithoutValidation("If-Range", resume.ETag);
            }
            else if (resume?.LastModified is not null)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(resume.LastModified.Value);
            }

            _observer.Observe(new(
                DateTimeOffset.UtcNow,
                ModelDeliveryEventCode.DownloadResumed,
                CompletedBytes: completedBeforeArtifact + offset,
                TotalBytes: totalBytes));
        }
        else
        {
            _observer.Observe(new(
                DateTimeOffset.UtcNow,
                ModelDeliveryEventCode.DownloadStarted,
                CompletedBytes: completedBeforeArtifact,
                TotalBytes: totalBytes));
        }

        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(_requestTimeout);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            requestTimeout.Token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return new(offset == artifact.SizeBytes
                ? DownloadAttemptResult.Complete
                : DownloadAttemptResult.PermanentFailure);
        }

        if (IsTransientStatus(response.StatusCode))
        {
            return new(DownloadAttemptResult.TransientFailure, RetryAfter(response));
        }

        if (!response.IsSuccessStatusCode)
        {
            return new(DownloadAttemptResult.PermanentFailure);
        }

        var append = response.StatusCode == HttpStatusCode.PartialContent && offset > 0;
        if (append && response.Content.Headers.ContentRange?.From != offset)
        {
            return new(DownloadAttemptResult.PermanentFailure);
        }

        if (!append)
        {
            offset = 0;
        }

        var metadata = new ResumeMetadata(
            source.AbsoluteUri,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified);
        await DeliveryFiles.WriteAtomicAsync(
            resumePath,
            JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new FileStream(
            partialPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            useAsync: true);
        var buffer = new byte[BufferSize];
        long written = offset;
        while (true)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(_requestTimeout);
            var read = await input.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            written = checked(written + read);
            if (written > artifact.SizeBytes)
            {
                return new(DownloadAttemptResult.PermanentFailure);
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            _observer.Observe(new(
                DateTimeOffset.UtcNow,
                ModelDeliveryEventCode.DownloadStarted,
                CompletedBytes: completedBeforeArtifact + written,
                TotalBytes: totalBytes));
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new(written == artifact.SizeBytes
            ? DownloadAttemptResult.Complete
            : DownloadAttemptResult.TransientFailure);
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode is 425 or 500 or 502 or 503 or 504 or 522 or 524;

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ??
            (header?.Date is null ? null : header.Date.Value - DateTimeOffset.UtcNow);
        if (delay is null)
        {
            return null;
        }

        return TimeSpan.FromMilliseconds(Math.Clamp(
            delay.Value.TotalMilliseconds,
            0,
            TimeSpan.FromSeconds(10).TotalMilliseconds));
    }

    private sealed record ResumeMetadata(string Source, string? ETag, DateTimeOffset? LastModified);
}

/// <summary>The file operations the store, the downloader and the transport share.</summary>
internal static class DeliveryFiles
{
    /// <summary>Whether the file at the path is exactly the artifact: its size, then its hash.</summary>
    public static async Task<bool> MatchesAsync(
        string path,
        ModelArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != artifact.SizeBytes)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexString(hash),
            artifact.Sha256,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Joins a relative path under a root and refuses one that would escape it.</summary>
    public static string SafeCombine(string root, string relativePath)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("A model path escaped its store root.");
        }

        return combined;
    }

    /// <summary>Writes to a sibling temporary file and moves it into place, so a reader never sees half a file.</summary>
    public static async Task WriteAtomicAsync(
        string destination,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes.ToArray(), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            DeleteIfExists(temporary);
        }
    }

    public static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
