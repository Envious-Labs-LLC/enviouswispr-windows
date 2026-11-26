using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.Learning;

namespace EnviousWispr.Services.Learning;

/// <summary>
/// Companion file store (<c>learn-memory.json</c>) tracking judge refusals and user rejections
/// for the Self-Learning Dictionary. Outlives deleted dictionary entries and isolates failures
/// cleanly from <c>settings.json</c>.
/// </summary>
public sealed class JsonLearnMemoryStore : ILearnMemoryStore
{
    public const int CurrentVersion = 1;
    public const int UnblockedCap = 2_000;
    public const int BlockedCap = 10_000;
    public static readonly TimeSpan UnblockedLifetime = TimeSpan.FromDays(90);
    public const string FileName = "learn-memory.json";
    private const string CorruptedMarker = ".corrupted-";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record StoreFile(
        int Version,
        Dictionary<string, LearnMemoryEntry> Pairs);

    private readonly record struct FileStamp(bool Exists, long Length, long LastWriteUtcTicks);

    private readonly string _filePath;
    private readonly string _lockPath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _inProcessLock = new();
    private Dictionary<string, LearnMemoryEntry> _pairs = new(StringComparer.Ordinal);
    private FileStamp? _stamp;

    public JsonLearnMemoryStore(string filePath, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _lockPath = _filePath + ".lock";
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        PrepareDirectory();
    }

    public event Action? Changed;

    public string FilePath => _filePath;

    public bool IsRepeat(string pairKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairKey);
        lock (_inProcessLock)
        {
            RefreshIfChanged();
            return _pairs.TryGetValue(pairKey, out var entry) &&
                   entry.JudgeRefusals >= 1 &&
                   !entry.Blocked;
        }
    }

    public bool IsBlocked(string pairKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairKey);
        lock (_inProcessLock)
        {
            RefreshIfChanged();
            return _pairs.TryGetValue(pairKey, out var entry) && entry.Blocked;
        }
    }

    public IReadOnlySet<string> BlockedKeys
    {
        get
        {
            lock (_inProcessLock)
            {
                RefreshIfChanged();
                return _pairs
                    .Where(kv => kv.Value.Blocked)
                    .Select(kv => kv.Key)
                    .ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    public int BlockedCount
    {
        get
        {
            lock (_inProcessLock)
            {
                RefreshIfChanged();
                return _pairs.Values.Count(entry => entry.Blocked);
            }
        }
    }

    public bool HasStoredMemory
    {
        get
        {
            lock (_inProcessLock)
            {
                RefreshIfChanged();
                if (_pairs.Count > 0)
                {
                    return true;
                }

                try
                {
                    return GetCorruptedCopies().Length > 0;
                }
                catch (Exception exception) when (IsStorageException(exception))
                {
                    return true;
                }
            }
        }
    }

    public LearnMemoryEntry? GetEntry(string pairKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairKey);
        lock (_inProcessLock)
        {
            RefreshIfChanged();
            return _pairs.TryGetValue(pairKey, out var entry) ? entry : null;
        }
    }

    public LearnMemoryMutation<bool> RecordJudgeRefusal(string pairKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairKey);
        var at = _clock();
        return Transact(pairs =>
        {
            var entry = pairs.TryGetValue(pairKey, out var existing)
                ? existing with { JudgeRefusals = Math.Max(1, existing.JudgeRefusals), LastSeen = at }
                : new LearnMemoryEntry(JudgeRefusals: 1, UserNos: 0, Blocked: false, LastSeen: at);
            pairs[pairKey] = entry;
            return (true, true);
        });
    }

    public LearnMemoryMutation<LearnUserNoResult> RecordUndo(string pairKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairKey);
        var at = _clock();
        return Transact(pairs =>
        {
            var existing = pairs.TryGetValue(pairKey, out var found)
                ? found
                : new LearnMemoryEntry(JudgeRefusals: 0, UserNos: 0, Blocked: false, LastSeen: at);
            if (existing.Blocked)
            {
                return (true, LearnUserNoResult.Blocked);
            }

            var nextNos = existing.UserNos + 1;
            var blockNow = nextNos >= 2;
            if (blockNow && pairs.Values.Count(e => e.Blocked) >= BlockedCap)
            {
                return (false, default);
            }

            pairs[pairKey] = new LearnMemoryEntry(
                existing.JudgeRefusals,
                nextNos,
                blockNow,
                at);
            return (true, blockNow ? LearnUserNoResult.Blocked : LearnUserNoResult.Watching);
        });
    }

    public IReadOnlyDictionary<string, LearnMemoryEntry> ExportEntries()
    {
        lock (_inProcessLock)
        {
            RefreshIfChanged();
            return new Dictionary<string, LearnMemoryEntry>(_pairs, StringComparer.Ordinal);
        }
    }

    public bool ImportEntries(IReadOnlyDictionary<string, LearnMemoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return false;
        }

        var mutation = Transact(pairs =>
        {
            var changed = false;
            foreach (var (rawKey, incoming) in entries)
            {
                if (string.IsNullOrWhiteSpace(rawKey) || rawKey.Length > 512 || incoming is null)
                {
                    continue;
                }

                var existing = pairs.GetValueOrDefault(rawKey);
                var refusals = Math.Max(existing?.JudgeRefusals ?? 0, Math.Max(0, incoming.JudgeRefusals));
                var userNos = Math.Max(existing?.UserNos ?? 0, Math.Max(0, incoming.UserNos));
                var blocked = (existing?.Blocked ?? false) || incoming.Blocked || userNos >= 2;
                if (blocked && !(existing?.Blocked ?? false) && pairs.Values.Count(e => e.Blocked) >= BlockedCap)
                {
                    continue;
                }

                var lastSeen = existing is not null && existing.LastSeen > incoming.LastSeen
                    ? existing.LastSeen
                    : incoming.LastSeen;
                var merged = new LearnMemoryEntry(refusals, userNos, blocked, lastSeen);
                if (existing != merged)
                {
                    pairs[rawKey] = merged;
                    changed = true;
                }
            }

            return (changed, changed);
        });

        return mutation.Committed && mutation.Value;
    }

    public bool ForgetAll()
    {
        var erased = false;
        lock (_inProcessLock)
        {
            try
            {
                using var fileLock = AcquireExclusiveLock();
                if (fileLock is null)
                {
                    _stamp = null;
                    return false;
                }

                if (File.Exists(_filePath))
                {
                    File.Delete(_filePath);
                }

                foreach (var copy in GetCorruptedCopies())
                {
                    File.Delete(copy);
                }

                erased = !File.Exists(_filePath) && GetCorruptedCopies().Length == 0;
                if (erased)
                {
                    _pairs.Clear();
                    _stamp = new FileStamp(false, 0, 0);
                }
                else
                {
                    _stamp = null;
                }
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                _stamp = null;
                erased = false;
            }
        }

        Changed?.Invoke();
        return erased;
    }

    private LearnMemoryMutation<T> Transact<T>(Func<Dictionary<string, LearnMemoryEntry>, (bool Accepted, T Value)> apply)
    {
        LearnMemoryMutation<T> outcome;
        lock (_inProcessLock)
        {
            outcome = LockedTransact(apply);
        }

        if (outcome.Committed)
        {
            Changed?.Invoke();
        }

        return outcome;
    }

    private LearnMemoryMutation<T> LockedTransact<T>(Func<Dictionary<string, LearnMemoryEntry>, (bool Accepted, T Value)> apply)
    {
        try
        {
            PrepareDirectory();
            using var fileLock = AcquireExclusiveLock();
            if (fileLock is null)
            {
                return LearnMemoryMutation.NotRemembered<T>();
            }

            var fresh = LoadWhileLocked();
            var (accepted, value) = apply(fresh);
            if (!accepted)
            {
                return LearnMemoryMutation.NotRemembered<T>();
            }

            Prune(fresh, _clock());
            WriteAtomically(new StoreFile(CurrentVersion, fresh));
            _pairs = fresh;
            _stamp = ReadStamp();
            return LearnMemoryMutation.Commit(value);
        }
        catch (Exception exception) when (IsStorageException(exception) || exception is JsonException)
        {
            _stamp = null;
            return LearnMemoryMutation.NotRemembered<T>();
        }
    }

    private void RefreshIfChanged()
    {
        var cutoff = _clock().Subtract(UnblockedLifetime);
        if (_pairs.Values.Any(e => !e.Blocked && e.LastSeen < cutoff))
        {
            foreach (var key in _pairs.Where(kv => !kv.Value.Blocked && kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToArray())
            {
                _pairs.Remove(key);
            }
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = ReadStamp();
            if (_stamp == before)
            {
                return;
            }

            var (status, loaded) = TryRead();
            if (ReadStamp() != before)
            {
                continue;
            }

            switch (status)
            {
                case ReadStatus.Absent:
                    _pairs = new Dictionary<string, LearnMemoryEntry>(StringComparer.Ordinal);
                    _stamp = before;
                    return;
                case ReadStatus.Loaded:
                    Prune(loaded!, _clock());
                    _pairs = loaded!;
                    _stamp = before;
                    return;
                case ReadStatus.Unreadable:
                    RefreshUnreadableUnderLock();
                    return;
            }
        }

        _stamp = null;
    }

    private void RefreshUnreadableUnderLock()
    {
        try
        {
            using var fileLock = AcquireExclusiveLock();
            if (fileLock is null)
            {
                _pairs = new Dictionary<string, LearnMemoryEntry>(StringComparer.Ordinal);
                _stamp = null;
                return;
            }

            _pairs = LoadWhileLocked();
            _stamp = ReadStamp();
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            _pairs = new Dictionary<string, LearnMemoryEntry>(StringComparer.Ordinal);
            _stamp = null;
        }
    }

    private Dictionary<string, LearnMemoryEntry> LoadWhileLocked()
    {
        var (status, loaded) = TryRead();
        switch (status)
        {
            case ReadStatus.Absent:
                return new Dictionary<string, LearnMemoryEntry>(StringComparer.Ordinal);
            case ReadStatus.Loaded:
                Prune(loaded!, _clock());
                return loaded!;
            default:
                MoveAsideCorrupted();
                return new Dictionary<string, LearnMemoryEntry>(StringComparer.Ordinal);
        }
    }

    private enum ReadStatus
    {
        Absent,
        Loaded,
        Unreadable,
    }

    private (ReadStatus Status, Dictionary<string, LearnMemoryEntry>? Pairs) TryRead()
    {
        if (!File.Exists(_filePath))
        {
            return (ReadStatus.Absent, null);
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var parsed = JsonSerializer.Deserialize<StoreFile>(json, SerializerOptions);
            if (parsed is null || parsed.Version != CurrentVersion || parsed.Pairs is null)
            {
                return (ReadStatus.Unreadable, null);
            }

            return (ReadStatus.Loaded, new Dictionary<string, LearnMemoryEntry>(parsed.Pairs, StringComparer.Ordinal));
        }
        catch (Exception exception) when (IsStorageException(exception) || exception is JsonException)
        {
            return (ReadStatus.Unreadable, null);
        }
    }

    private void MoveAsideCorrupted()
    {
        try
        {
            foreach (var copy in GetCorruptedCopies())
            {
                try
                {
                    File.Delete(copy);
                }
                catch (Exception exception) when (IsStorageException(exception))
                {
                }
            }

            if (File.Exists(_filePath))
            {
                var dir = Path.GetDirectoryName(_filePath)!;
                var aside = Path.Combine(dir, Path.GetFileName(_filePath) + CorruptedMarker + Guid.NewGuid().ToString("D"));
                File.Move(_filePath, aside);
            }
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
        }
    }

    private string[] GetCorruptedCopies()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return [];
        }

        var prefix = Path.GetFileName(_filePath) + CorruptedMarker;
        return Directory.EnumerateFiles(dir, prefix + "*").ToArray();
    }

    private void WriteAtomically(StoreFile file)
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(file, SerializerOptions);
            File.WriteAllText(temp, json);
            File.Move(temp, _filePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
            }
        }
    }

    private FileStream? AcquireExclusiveLock()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(5);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
            {
                return null;
            }
        }

        return null;
    }

    private void PrepareDirectory()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
        }
    }

    private FileStamp ReadStamp()
    {
        try
        {
            var info = new FileInfo(_filePath);
            if (!info.Exists)
            {
                return new FileStamp(false, 0, 0);
            }

            return new FileStamp(true, info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return new FileStamp(false, -1, -1);
        }
    }

    public static void Prune(Dictionary<string, LearnMemoryEntry> pairs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var cutoff = now.Subtract(UnblockedLifetime);
        foreach (var key in pairs.Where(kv => !kv.Value.Blocked && kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToArray())
        {
            pairs.Remove(key);
        }

        var unblocked = pairs.Where(kv => !kv.Value.Blocked).ToArray();
        if (unblocked.Length <= UnblockedCap)
        {
            return;
        }

        foreach (var victim in unblocked.OrderBy(kv => kv.Value.LastSeen).Take(unblocked.Length - UnblockedCap))
        {
            pairs.Remove(victim.Key);
        }
    }

    private static bool IsStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;
}
