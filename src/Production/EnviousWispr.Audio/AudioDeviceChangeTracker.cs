using EnviousWispr.Core.Audio;

namespace EnviousWispr.Audio;

internal sealed class AudioDeviceChangeTracker
{
    private readonly object _gate = new();
    private readonly HashSet<string> _knownCaptureDeviceIds = new(StringComparer.Ordinal);

    public void ReplaceKnownCaptureDevices(IEnumerable<AudioDeviceId> deviceIds)
    {
        lock (_gate)
        {
            _knownCaptureDeviceIds.Clear();
            foreach (var deviceId in deviceIds)
            {
                _knownCaptureDeviceIds.Add(deviceId.Value);
            }
        }
    }

    public AudioDeviceChange Added(string deviceId, bool isCapture)
    {
        if (isCapture)
        {
            lock (_gate)
            {
                _knownCaptureDeviceIds.Add(deviceId);
            }
        }

        return Change(deviceId, AudioDeviceChangeKind.Added, isCapture);
    }

    public AudioDeviceChange Removed(string deviceId)
    {
        bool wasCapture;
        lock (_gate)
        {
            wasCapture = _knownCaptureDeviceIds.Remove(deviceId);
        }

        return Change(deviceId, AudioDeviceChangeKind.Removed, wasCapture);
    }

    public AudioDeviceChange StateChanged(string deviceId, bool isCapture, bool isActive)
    {
        bool affectsCapture;
        lock (_gate)
        {
            if (isCapture && isActive)
            {
                _knownCaptureDeviceIds.Add(deviceId);
                affectsCapture = true;
            }
            else if (!isActive)
            {
                affectsCapture = _knownCaptureDeviceIds.Remove(deviceId) || isCapture;
            }
            else
            {
                affectsCapture = false;
            }
        }

        return Change(deviceId, AudioDeviceChangeKind.StateChanged, affectsCapture);
    }

    public static AudioDeviceChange DefaultChanged(string? deviceId, bool affectsCapture) =>
        Change(deviceId ?? string.Empty, AudioDeviceChangeKind.DefaultChanged, affectsCapture);

    private static AudioDeviceChange Change(
        string deviceId,
        AudioDeviceChangeKind kind,
        bool affectsCapture) =>
        new(new AudioDeviceId(deviceId), kind, affectsCapture);
}
