using System.Security.Cryptography;
using System.Text;

namespace EnviousWispr.Services.Lifecycle;

public sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private SingleInstanceLock(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    public static string ResolveKey(string baseKey, string? customDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseKey);
        if (string.IsNullOrWhiteSpace(customDataDirectory))
        {
            return baseKey;
        }

        var normalized = Path.GetFullPath(customDataDirectory).TrimEnd('\\', '/').ToLowerInvariant();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hash = Convert.ToHexString(hashBytes.AsSpan(0, 8)).ToLowerInvariant();
        return $"{baseKey}-{hash}";
    }

    public static bool IsHeld(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            if (!Mutex.TryOpenExisting($@"Local\{key}", out var existing))
            {
                return false;
            }

            existing.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static bool TryAcquire(string key, out SingleInstanceLock? instanceLock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var mutex = new Mutex(initiallyOwned: true, $@"Local\{key}", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            instanceLock = null;
            return false;
        }

        instanceLock = new SingleInstanceLock(mutex);
        return true;
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
