using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.Feedback;
using EnviousWispr.Core.IO;

namespace EnviousWispr.Services.Feedback;

/// <summary>
/// Persisted entry inside <c>feedback-outbox.json</c>, preserving rejected items in a
/// non-retrying state instead of silently discarding them.
/// </summary>
public sealed record FeedbackOutboxEntry(
    FeedbackSubmission Submission,
    bool IsRejected = false,
    int? RejectedStatusCode = null);

/// <summary>
/// Durable JSON queue (<c>feedback-outbox.json</c>) for offline feedback retries,
/// capped at <see cref="MaxItems"/> pending items and deduplicated by
/// <see cref="FeedbackSubmission.SubmissionId"/>. Preserves rejected items in bounded
/// retention and persists server rate-limit deadlines across restarts.
/// </summary>
public sealed class FeedbackOutbox : IDisposable
{
    public const int MaxItems = 20;
    public const int MaxRejectedItems = 10;
    public const string DefaultFileName = "feedback-outbox.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private bool _disposed;

    public FeedbackOutbox(string dataDirectory, string fileName = DefaultFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        _directory = Path.GetFullPath(dataDirectory);
        FilePath = Path.Combine(_directory, fileName);
    }

    public string FilePath { get; }

    public async Task<bool> EnqueueAsync(
        FeedbackSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (success, document) = await TryReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!success)
            {
                return false;
            }

            if (document.Entries.Any(existing => existing.Submission.SubmissionId == submission.SubmissionId))
            {
                return true;
            }

            var pendingCount = document.Entries.Count(entry => !entry.IsRejected);
            if (pendingCount >= MaxItems)
            {
                return false;
            }

            document.Entries.Add(new FeedbackOutboxEntry(submission));
            await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns all pending (non-rejected) queued submissions in FIFO order.
    /// Throws <see cref="InvalidDataException"/> or <see cref="IOException"/> when an existing
    /// outbox file cannot be read or decoded, rather than treating it as empty.
    /// </summary>
    public async Task<IReadOnlyList<FeedbackSubmission>> PeekAllAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadOrThrowUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return document.Entries
                .Where(entry => !entry.IsRejected)
                .Select(entry => entry.Submission)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<FeedbackOutboxEntry>> PeekEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadOrThrowUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return document.Entries.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasUndeliverableAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (success, document) = await TryReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!success)
            {
                return File.Exists(FilePath);
            }

            return document.Entries.Any(entry => entry.IsRejected);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DateTimeOffset?> GetNotBeforeUtcAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (success, document) = await TryReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return success ? document.NotBeforeUtc : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetNotBeforeUtcAsync(
        DateTimeOffset? notBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (success, document) = await TryReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!success)
            {
                return;
            }

            if (notBeforeUtc is { } candidate &&
                document.NotBeforeUtc is { } existing &&
                candidate <= existing)
            {
                return;
            }

            if (document.NotBeforeUtc == notBeforeUtc)
            {
                return;
            }

            var updated = document with { NotBeforeUtc = notBeforeUtc };
            await WriteUnsafeAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> MarkRejectedAsync(
        Guid submissionId,
        int? httpStatusCode,
        CancellationToken cancellationToken = default)
    {
        if (submissionId == Guid.Empty)
        {
            return false;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (success, document) = await TryReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!success)
            {
                return false;
            }

            var index = document.Entries.FindIndex(entry => entry.Submission.SubmissionId == submissionId);
            if (index < 0)
            {
                return false;
            }

            document.Entries[index] = document.Entries[index] with
            {
                IsRejected = true,
                RejectedStatusCode = httpStatusCode,
            };

            TrimRejectedEntries(document.Entries);
            await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        if (submissionId == Guid.Empty)
        {
            return false;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (success, document) = await TryReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!success)
            {
                return false;
            }

            var removed = document.Entries.RemoveAll(entry => entry.Submission.SubmissionId == submissionId);
            if (removed == 0)
            {
                return false;
            }

            await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
            return true;
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

    private async Task<OutboxState> ReadOrThrowUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return new OutboxState([], null);
        }

        var json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var versionElement) ||
                !versionElement.TryGetInt32(out var schemaVersion))
            {
                throw new InvalidDataException("Feedback outbox document has an invalid schema header.");
            }

            if (schemaVersion == 2)
            {
                var v2 = JsonSerializer.Deserialize<OutboxDocumentV2>(json, SerializerOptions)
                    ?? throw new InvalidDataException("Feedback outbox document could not be deserialized.");
                if (v2.Entries is null)
                {
                    throw new InvalidDataException("Feedback outbox document entries cannot be null.");
                }

                return new OutboxState(DeduplicateEntries(v2.Entries), v2.NotBeforeUtc);
            }

            if (schemaVersion == 1)
            {
                var v1 = JsonSerializer.Deserialize<OutboxDocumentV1>(json, SerializerOptions)
                    ?? throw new InvalidDataException("Feedback outbox v1 document could not be deserialized.");
                if (v1.Items is null)
                {
                    throw new InvalidDataException("Feedback outbox v1 document items cannot be null.");
                }

                return new OutboxState(
                    DeduplicateEntries(v1.Items.Select(item => new FeedbackOutboxEntry(item))),
                    null);
            }

            throw new InvalidDataException($"Unsupported feedback outbox schema version {schemaVersion}.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Feedback outbox file contains malformed JSON.", ex);
        }
    }

    private async Task<(bool Success, OutboxState Document)> TryReadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await ReadOrThrowUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return (true, document);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return (false, new OutboxState([], null));
        }
    }

    private static List<FeedbackOutboxEntry> DeduplicateEntries(IEnumerable<FeedbackOutboxEntry> source)
    {
        var seen = new HashSet<Guid>();
        var result = new List<FeedbackOutboxEntry>();
        var pendingCount = 0;

        foreach (var entry in source)
        {
            if (entry is null || entry.Submission is null || !seen.Add(entry.Submission.SubmissionId))
            {
                continue;
            }

            if (!entry.IsRejected)
            {
                if (pendingCount >= MaxItems)
                {
                    continue;
                }

                pendingCount++;
            }

            result.Add(entry);
        }

        TrimRejectedEntries(result);
        return result;
    }

    private static void TrimRejectedEntries(List<FeedbackOutboxEntry> entries)
    {
        var rejectedCount = entries.Count(entry => entry.IsRejected);
        while (rejectedCount > MaxRejectedItems)
        {
            var oldestRejectedIndex = entries.FindIndex(entry => entry.IsRejected);
            if (oldestRejectedIndex < 0)
            {
                break;
            }

            entries.RemoveAt(oldestRejectedIndex);
            rejectedCount--;
        }
    }

    private async Task WriteUnsafeAsync(
        OutboxState state,
        CancellationToken cancellationToken)
    {
        var document = new OutboxDocumentV2(
            SchemaVersion: 2,
            Entries: state.Entries,
            NotBeforeUtc: state.NotBeforeUtc);
        _ = await AtomicFileWriter.WriteJsonAsync(
            FilePath,
            document,
            SerializerOptions,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private sealed record OutboxState(
        List<FeedbackOutboxEntry> Entries,
        DateTimeOffset? NotBeforeUtc);

    private sealed record OutboxDocumentV1(
        int SchemaVersion,
        List<FeedbackSubmission>? Items);

    private sealed record OutboxDocumentV2(
        int SchemaVersion,
        List<FeedbackOutboxEntry>? Entries,
        DateTimeOffset? NotBeforeUtc = null);
}
