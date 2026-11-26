using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.History;

public sealed class JsonHistoryStore : IHistoryStore, IDisposable
{
    private const int CurrentSchemaVersion = 3;
    private const int MaximumEntries = 10_000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _historyPath;
    private readonly string _recordingsDirectory;
    private readonly string _pendingAudioDeletesPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonHistoryStore(string historyPath, string? recordingsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyPath);
        _historyPath = Path.GetFullPath(historyPath);
        var historyDirectory = Path.GetDirectoryName(_historyPath) ?? AppContext.BaseDirectory;
        _pendingAudioDeletesPath = Path.Combine(
            historyDirectory,
            $".{Path.GetFileName(_historyPath)}.pending-audio-deletes.json");
        _recordingsDirectory = !string.IsNullOrWhiteSpace(recordingsDirectory)
            ? Path.GetFullPath(recordingsDirectory)
            : Path.Combine(
                historyDirectory,
                "recordings",
                "dictations");
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
                DrainPendingAudioDeletes([]);
                return loaded;
            }

            var retained = Retain(loaded.Entries, retentionDays, now);
            if (retained.Length != loaded.Entries.Count)
            {
                await WriteAsync(retained, cancellationToken).ConfigureAwait(false);
                DrainPendingAudioDeletes(CollectRemovedAudioFileNames(loaded.Entries, retained));
            }
            else
            {
                DrainPendingAudioDeletes([]);
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
            DrainPendingAudioDeletes(CollectRemovedAudioFileNames(loaded.Entries, entries));
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

    public async Task<DictationHistoryEntry?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (loaded.Status is not HistoryLoadStatus.Loaded)
            {
                return null;
            }

            return loaded.Entries.FirstOrDefault(entry => entry.Id == id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HistoryRepolishUpdateResult> UpdateRepolishedAsync(
        Guid id,
        string expectedCurrentText,
        string newText,
        bool wasPolished,
        string? providerId,
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCurrentText);
        ArgumentException.ThrowIfNullOrWhiteSpace(newText);
        if (newText.Length > DictationHistoryEntry.MaximumTextLength)
        {
            return HistoryRepolishUpdateResult.Failed(
                new AppError(AppErrorCode.InvalidData, AppErrorStage.HistorySave, CanRetry: false));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (loaded.Status is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable)
            {
                return HistoryRepolishUpdateResult.Failed(loaded.Error);
            }

            var existing = loaded.Entries.FirstOrDefault(entry => entry.Id == id);
            if (existing is null)
            {
                return HistoryRepolishUpdateResult.NotFound();
            }

            if (existing.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            {
                return HistoryRepolishUpdateResult.Expired();
            }

            if (!string.Equals(existing.Text, expectedCurrentText, StringComparison.Ordinal))
            {
                return HistoryRepolishUpdateResult.Stale();
            }

            var updated = existing with
            {
                Text = newText,
                WasPolished = wasPolished,
                OriginalText = existing.OriginalText ?? existing.Text,
                PolishProviderId = providerId,
                PolishModelId = modelId,
            };

            if (!updated.IsValid)
            {
                return HistoryRepolishUpdateResult.Failed(
                    new AppError(AppErrorCode.InvalidData, AppErrorStage.HistorySave, CanRetry: false));
            }

            var entries = loaded.Entries
                .Select(entry => entry.Id == id ? updated : entry)
                .ToArray();

            await WriteAsync(entries, cancellationToken).ConfigureAwait(false);
            return HistoryRepolishUpdateResult.Updated(updated);
        }
        catch (IOException)
        {
            return HistoryRepolishUpdateResult.Failed(
                new AppError(AppErrorCode.StorageUnavailable, AppErrorStage.HistorySave, CanRetry: true));
        }
        catch (UnauthorizedAccessException)
        {
            return HistoryRepolishUpdateResult.Failed(
                new AppError(AppErrorCode.AccessDenied, AppErrorStage.HistorySave, CanRetry: false));
        }
        catch (SecurityException)
        {
            return HistoryRepolishUpdateResult.Failed(
                new AppError(AppErrorCode.AccessDenied, AppErrorStage.HistorySave, CanRetry: false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<HistoryOperationResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            entries => entries.Where(entry => entry.Id != id).ToArray(),
            cancellationToken,
            verifyAudioDeletion: true);

    public Task<HistoryOperationResult> DeleteManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return Task.FromResult(new HistoryOperationResult(true));
        }

        var idSet = ids as HashSet<Guid> ?? new HashSet<Guid>(ids);
        return MutateAsync(
            entries => entries.Where(entry => !idSet.Contains(entry.Id)).ToArray(),
            cancellationToken,
            verifyAudioDeletion: true);
    }

    public Task<HistoryOperationResult> KeepAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            entries => entries
                .Select(entry => entry.Id == id ? entry with { ExpiresAt = null } : entry)
                .ToArray(),
            cancellationToken);

    public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(
            _ => Array.Empty<DictationHistoryEntry>(),
            cancellationToken,
            clearAllAudio: true,
            verifyAudioDeletion: true);

    public void Dispose() => _gate.Dispose();

    private async Task<HistoryOperationResult> MutateAsync(
        Func<IReadOnlyList<DictationHistoryEntry>, IReadOnlyList<DictationHistoryEntry>> mutation,
        CancellationToken cancellationToken,
        bool clearAllAudio = false,
        bool verifyAudioDeletion = false)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (loaded.Status is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable)
            {
                return new HistoryOperationResult(false, loaded.Error);
            }

            var updated = mutation(loaded.Entries);
            if (verifyAudioDeletion)
            {
                // Stage paired WAV removals before mutating history so locked files or write failures
                // roll back cleanly without leaving surviving entries without recordings.
                var targetFiles = CollectRemovedAudioFileNames(loaded.Entries, updated);
                var deleteError = LocalDictationAudioArchive.TryStageDeletes(
                    _recordingsDirectory,
                    targetFiles,
                    includeAllWaveFiles: clearAllAudio,
                    out var stagedDeletes);
                if (deleteError is { } errorCode)
                {
                    return Failure(errorCode, canRetry: errorCode == AppErrorCode.StorageUnavailable);
                }

                try
                {
                    await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    LocalDictationAudioArchive.RollbackStagedDeletes(stagedDeletes);
                    throw;
                }

                LocalDictationAudioArchive.CommitStagedDeletes(
                    _recordingsDirectory,
                    stagedDeletes,
                    cleanRemainingTempFiles: clearAllAudio);
                if (clearAllAudio)
                {
                    DeletePendingDeletesManifest();
                }
                else
                {
                    DrainPendingAudioDeletes([]);
                }

                return new HistoryOperationResult(true);
            }

            await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
            DrainPendingAudioDeletes(CollectRemovedAudioFileNames(loaded.Entries, updated));
            if (clearAllAudio)
            {
                _ = ClearAllAudioFiles();
                DeletePendingDeletesManifest();
            }
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

    private static List<string> CollectRemovedAudioFileNames(
        IReadOnlyList<DictationHistoryEntry> before,
        IReadOnlyList<DictationHistoryEntry> after)
    {
        if (before.Count == 0)
        {
            return [];
        }

        var survivingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in after)
        {
            if (!string.IsNullOrWhiteSpace(entry.AudioFileName))
            {
                survivingFiles.Add(entry.AudioFileName);
            }
        }

        var removed = new List<string>();
        foreach (var entry in before)
        {
            if (!string.IsNullOrWhiteSpace(entry.AudioFileName)
                && !survivingFiles.Contains(entry.AudioFileName))
            {
                removed.Add(entry.AudioFileName);
            }
        }

        return removed;
    }

    private void DrainPendingAudioDeletes(IEnumerable<string> newlyRemovedFileNames)
    {
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in ReadPendingDeletesManifest())
        {
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                pending.Add(fileName);
            }
        }

        foreach (var fileName in newlyRemovedFileNames)
        {
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                pending.Add(fileName);
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        var stillBlocked = new List<string>();
        foreach (var fileName in pending)
        {
            if (LocalDictationAudioArchive.TryDeleteNamedFileOutcome(_recordingsDirectory, fileName) is not null)
            {
                stillBlocked.Add(fileName);
            }
        }

        if (stillBlocked.Count == 0)
        {
            DeletePendingDeletesManifest();
        }
        else
        {
            WritePendingDeletesManifest(stillBlocked);
        }
    }

    private string[] ReadPendingDeletesManifest()
    {
        try
        {
            if (!File.Exists(_pendingAudioDeletesPath))
            {
                return [];
            }

            var json = File.ReadAllText(_pendingAudioDeletesPath);
            return JsonSerializer.Deserialize<string[]>(json, SerializerOptions) ?? [];
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
    }

    private void WritePendingDeletesManifest(IReadOnlyList<string> fileNames)
    {
        try
        {
            var directory = Path.GetDirectoryName(_pendingAudioDeletesPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(fileNames, SerializerOptions);
            File.WriteAllText(_pendingAudioDeletesPath, json);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
        }
    }

    private void DeletePendingDeletesManifest()
    {
        try
        {
            if (File.Exists(_pendingAudioDeletesPath))
            {
                File.Delete(_pendingAudioDeletesPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
        }
    }

    private AppErrorCode? ClearAllAudioFiles() =>
        LocalDictationAudioArchive.DeleteAllFilesWithOutcome(_recordingsDirectory);

    private sealed record HistoryDocument(
        int SchemaVersion,
        IReadOnlyList<DictationHistoryEntry> Entries);
}
