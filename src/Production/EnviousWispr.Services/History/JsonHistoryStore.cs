using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.History;

public sealed class JsonHistoryStore : IHistoryStore, IDisposable
{
    private const int CurrentSchemaVersion = 2;
    private const int MaximumEntries = 10_000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _historyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonHistoryStore(string historyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyPath);
        _historyPath = Path.GetFullPath(historyPath);
    }

    public async Task<HistoryLoadResult> LoadAsync(
        int retentionDays,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateRetention(retentionDays);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (loaded.Status is not HistoryLoadStatus.Loaded)
            {
                return loaded;
            }

            var retained = Retain(loaded.Entries, retentionDays, now);
            if (retained.Length != loaded.Entries.Count)
            {
                await WriteAsync(retained, cancellationToken).ConfigureAwait(false);
            }

            return loaded with { Entries = retained };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HistoryOperationResult> AddAsync(
        DictationHistoryEntry entry,
        int retentionDays,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateRetention(retentionDays);
        if (!entry.IsValid)
        {
            return Failure(AppErrorCode.InvalidData, canRetry: false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (loaded.Status is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable)
            {
                return new HistoryOperationResult(false, loaded.Error);
            }

            var entries = Retain(loaded.Entries, retentionDays, now)
                .Where(existing => existing.Id != entry.Id)
                .Prepend(entry)
                .OrderByDescending(existing => existing.CreatedAt)
                .Take(MaximumEntries)
                .ToArray();
            await WriteAsync(entries, cancellationToken).ConfigureAwait(false);
            return new HistoryOperationResult(true);
        }
        catch (IOException)
        {
            return Failure(AppErrorCode.StorageUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(AppErrorCode.AccessDenied);
        }
        catch (SecurityException)
        {
            return Failure(AppErrorCode.AccessDenied);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<HistoryOperationResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        MutateAsync(entries => entries.Where(entry => entry.Id != id).ToArray(), cancellationToken);

    public Task<HistoryOperationResult> KeepAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            entries => entries
                .Select(entry => entry.Id == id ? entry with { ExpiresAt = null } : entry)
                .ToArray(),
            cancellationToken);

    public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(_ => Array.Empty<DictationHistoryEntry>(), cancellationToken);

    public void Dispose() => _gate.Dispose();

    private async Task<HistoryOperationResult> MutateAsync(
        Func<IReadOnlyList<DictationHistoryEntry>, IReadOnlyList<DictationHistoryEntry>> mutation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (loaded.Status is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable)
            {
                return new HistoryOperationResult(false, loaded.Error);
            }

            await WriteAsync(mutation(loaded.Entries), cancellationToken).ConfigureAwait(false);
            return new HistoryOperationResult(true);
        }
        catch (IOException)
        {
            return Failure(AppErrorCode.StorageUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(AppErrorCode.AccessDenied);
        }
        catch (SecurityException)
        {
            return Failure(AppErrorCode.AccessDenied);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HistoryLoadResult> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_historyPath))
        {
            return new HistoryLoadResult([], HistoryLoadStatus.Missing);
        }

        try
        {
            var json = await File.ReadAllTextAsync(_historyPath, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<HistoryDocument>(json, SerializerOptions);
            if (document is null ||
                document.SchemaVersion is < 1 or > CurrentSchemaVersion ||
                document.Entries is null ||
                document.Entries.Count > MaximumEntries ||
                document.Entries.Any(entry => entry is null || !entry.IsValid))
            {
                return Invalid();
            }

            return new HistoryLoadResult(
                document.Entries.OrderByDescending(entry => entry.CreatedAt).ToArray(),
                HistoryLoadStatus.Loaded);
        }
        catch (JsonException)
        {
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
    }

    private async Task WriteAsync(
        IReadOnlyList<DictationHistoryEntry> entries,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_historyPath)
            ?? throw new InvalidOperationException("The history path must have a parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_historyPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(
                new HistoryDocument(CurrentSchemaVersion, entries),
                SerializerOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _historyPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static DictationHistoryEntry[] Retain(
        IReadOnlyList<DictationHistoryEntry> entries,
        int retentionDays,
        DateTimeOffset now)
    {
        if (retentionDays == 0)
        {
            return entries
                .Where(entry => entry.ExpiresAt is null || entry.ExpiresAt > now)
                .OrderByDescending(entry => entry.CreatedAt)
                .ToArray();
        }

        var cutoff = now - TimeSpan.FromDays(retentionDays);
        return entries
            .Where(entry => entry.ExpiresAt is null || entry.ExpiresAt > now)
            .Where(entry => entry.CreatedAt >= cutoff)
            .OrderByDescending(entry => entry.CreatedAt)
            .ToArray();
    }

    private static void ValidateRetention(int retentionDays)
    {
        if (retentionDays is < RetentionDays.HistoryMinimum or > RetentionDays.HistoryMaximum)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        }
    }

    private static HistoryLoadResult Invalid() => new(
        [],
        HistoryLoadStatus.Invalid,
        new AppError(AppErrorCode.InvalidData, AppErrorStage.HistoryLoad, CanRetry: false));

    private static HistoryLoadResult Unavailable(AppErrorCode code) => new(
        [],
        HistoryLoadStatus.Unavailable,
        new AppError(code, AppErrorStage.HistoryLoad, CanRetry: true));

    private static HistoryOperationResult Failure(AppErrorCode code, bool canRetry = true) => new(
        false,
        new AppError(code, AppErrorStage.HistorySave, canRetry));

    private sealed record HistoryDocument(
        int SchemaVersion,
        IReadOnlyList<DictationHistoryEntry> Entries);
}
