using EnviousWispr.Core.Audio;
using NAudio.CoreAudioApi;

namespace EnviousWispr.Audio;

public sealed class WasapiDeviceCatalog : IAudioDeviceCatalog
{
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDeviceNotificationClient _notifications;
    private readonly HashSet<string> _knownCaptureDeviceIds = new(StringComparer.Ordinal);
    private bool _disposed;

    public WasapiDeviceCatalog()
    {
        _enumerator = new MMDeviceEnumerator();
        _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _notifications.DeviceAdded += OnDeviceAdded;
        _notifications.DeviceRemoved += OnDeviceRemoved;
        _notifications.DeviceStateChanged += OnDeviceStateChanged;
        _notifications.DefaultDeviceChanged += OnDefaultDeviceChanged;
    }

    public event EventHandler<AudioDeviceChange>? DevicesChanged;

    public Task<IReadOnlyList<AudioDeviceInfo>> GetCaptureDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        string? defaultDeviceId = null;
        try
        {
            using var defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            defaultDeviceId = defaultDevice.ID;
        }
        catch
        {
            // Having no default capture endpoint is a normal, representable state.
        }

        var result = new List<AudioDeviceInfo>();
        using var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (device)
            {
                result.Add(new AudioDeviceInfo(
                    new AudioDeviceId(device.ID),
                    device.FriendlyName,
                    string.Equals(device.ID, defaultDeviceId, StringComparison.Ordinal),
                    IsActive: true));
            }
        }

        lock (_gate)
        {
            _knownCaptureDeviceIds.Clear();
            foreach (var device in result)
            {
                _knownCaptureDeviceIds.Add(device.Id.Value);
            }
        }

        return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(result);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifications.DeviceAdded -= OnDeviceAdded;
        _notifications.DeviceRemoved -= OnDeviceRemoved;
        _notifications.DeviceStateChanged -= OnDeviceStateChanged;
        _notifications.DefaultDeviceChanged -= OnDefaultDeviceChanged;
        _notifications.Dispose();
        _enumerator.Dispose();
    }

    private void OnDeviceAdded(object? sender, DeviceNotificationEventArgs args)
    {
        var affectsCapture = TryIsCaptureDevice(args.DeviceId);
        if (affectsCapture)
        {
            lock (_gate)
            {
                _knownCaptureDeviceIds.Add(args.DeviceId);
            }
        }

        Raise(args.DeviceId, AudioDeviceChangeKind.Added, affectsCapture);
    }

    private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs args)
    {
        bool affectsCapture;
        lock (_gate)
        {
            affectsCapture = _knownCaptureDeviceIds.Remove(args.DeviceId);
        }

        Raise(args.DeviceId, AudioDeviceChangeKind.Removed, affectsCapture);
    }

    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs args)
    {
        var affectsCapture = TryIsCaptureDevice(args.DeviceId);
        lock (_gate)
        {
            if (affectsCapture && args.NewState == DeviceState.Active)
            {
                _knownCaptureDeviceIds.Add(args.DeviceId);
            }
            else if (args.NewState != DeviceState.Active)
            {
                affectsCapture = _knownCaptureDeviceIds.Remove(args.DeviceId) || affectsCapture;
            }
        }

        Raise(args.DeviceId, AudioDeviceChangeKind.StateChanged, affectsCapture);
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs args)
    {
        var affectsCapture = args.Flow is DataFlow.Capture or DataFlow.All;
        Raise(args.DeviceId ?? string.Empty, AudioDeviceChangeKind.DefaultChanged, affectsCapture);
    }

    private bool TryIsCaptureDevice(string deviceId)
    {
        try
        {
            using var device = _enumerator.GetDevice(deviceId);
            return device.DataFlow == DataFlow.Capture;
        }
        catch
        {
            lock (_gate)
            {
                return _knownCaptureDeviceIds.Contains(deviceId);
            }
        }
    }

    private void Raise(string deviceId, AudioDeviceChangeKind kind, bool affectsCapture) =>
        DevicesChanged?.Invoke(
            this,
            new AudioDeviceChange(new AudioDeviceId(deviceId), kind, affectsCapture));
}
