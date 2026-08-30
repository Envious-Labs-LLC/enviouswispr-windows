using EnviousWispr.Core.Audio;
using NAudio.CoreAudioApi;

namespace EnviousWispr.Audio;

public sealed class WasapiDeviceCatalog : IAudioDeviceCatalog
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDeviceNotificationClient _notifications;
    private readonly AudioDeviceChangeTracker _changeTracker = new();
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

        _changeTracker.ReplaceKnownCaptureDevices(result.Select(device => device.Id));

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
        Raise(_changeTracker.Added(args.DeviceId, affectsCapture));
    }

    private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs args)
    {
        Raise(_changeTracker.Removed(args.DeviceId));
    }

    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs args)
    {
        var affectsCapture = TryIsCaptureDevice(args.DeviceId);
        Raise(_changeTracker.StateChanged(
            args.DeviceId,
            affectsCapture,
            args.NewState == DeviceState.Active));
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs args)
    {
        var affectsCapture = args.Flow is DataFlow.Capture or DataFlow.All;
        Raise(AudioDeviceChangeTracker.DefaultChanged(args.DeviceId, affectsCapture));
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
            return false;
        }
    }

    private void Raise(AudioDeviceChange change) => DevicesChanged?.Invoke(this, change);
}
