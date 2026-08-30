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
