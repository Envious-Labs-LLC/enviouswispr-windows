using System.Globalization;
using System.Runtime.InteropServices;
using EnviousWispr.Core.Audio;
using NAudio.CoreAudioApi;

namespace EnviousWispr.Audio;

public sealed class WasapiDeviceCatalog : IAudioDeviceCatalog
{
    private const uint CrSuccess = 0;
    private const uint CmLocateDevNodeNormal = 0;
    private const int MaxParentEnumeratorHops = 4;

    private static readonly PropertyKey DevpkeyDeviceEnumeratorName =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    private static readonly PropertyKey DevpkeyDeviceInstanceId =
        new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

    // CoreAudio EndpointFormFactor values from mmdeviceapi.h.
    private const int FormFactorLineLevel = 2;
    private const int FormFactorMicrophone = 4;
    private const int FormFactorHeadset = 5;
    private const int FormFactorHandset = 6;
    private const int FormFactorUnknownDigitalPassthrough = 7;
    private const int FormFactorSpdif = 8;
    private const int FormFactorDigitalAudioDisplay = 9;

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
                var transport = ClassifyEndpoint(device);
                result.Add(new AudioDeviceInfo(
                    new AudioDeviceId(device.ID),
                    device.FriendlyName,
                    string.Equals(device.ID, defaultDeviceId, StringComparison.Ordinal),
                    IsActive: true,
                    Transport: transport));
            }
        }

        _changeTracker.ReplaceKnownCaptureDevices(result.Select(device => device.Id));

        return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(result);
    }

    /// <summary>
    /// Classifies an audio capture endpoint's transport from CoreAudio properties and driver hints.
    /// Falls back safely to <see cref="AudioDeviceTransport.Unknown"/> when metadata is missing.
    /// </summary>
    public static AudioDeviceTransport ClassifyTransport(
        int? formFactor,
        string? enumeratorName,
        string? interfaceFriendlyName,
        string? deviceDescription,
        string? friendlyName,
        string? iconPath = null)
    {
        var combined = string.Join(
            " ",
            friendlyName ?? string.Empty,
            interfaceFriendlyName ?? string.Empty,
            deviceDescription ?? string.Empty,
            iconPath ?? string.Empty);

        var normalizedEnumerator = enumeratorName?.Trim().ToUpperInvariant() ?? string.Empty;

        if (ContainsVirtualAdapterMarker(combined) ||
            string.Equals(normalizedEnumerator, "ROOT", StringComparison.Ordinal))
        {
            return AudioDeviceTransport.Virtual;
        }

        if (normalizedEnumerator.StartsWith("BTH", StringComparison.Ordinal) ||
            normalizedEnumerator.Contains("BLUETOOTH", StringComparison.Ordinal) ||
            combined.Contains("bluetooth", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("hands-free", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("ag audio", StringComparison.OrdinalIgnoreCase))
        {
            return AudioDeviceTransport.Bluetooth;
        }

        if (normalizedEnumerator.StartsWith("USB", StringComparison.Ordinal) ||
            combined.Contains("usb audio", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("usb microphone", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("usb pnp", StringComparison.OrdinalIgnoreCase))
        {
            return AudioDeviceTransport.Usb;
        }

        if (formFactor is FormFactorLineLevel or FormFactorSpdif or FormFactorUnknownDigitalPassthrough or FormFactorDigitalAudioDisplay ||
            combined.Contains("line in", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("line-in", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("thunderbolt", StringComparison.OrdinalIgnoreCase) ||
            ContainsWholeWord(combined, "dock"))
        {
            return AudioDeviceTransport.DockOrLine;
        }

        if (normalizedEnumerator is "HDAUDIO" or "INTELAUDIO" or "ACPI" or "SOUNDWIRE" or "QCOM" ||
            normalizedEnumerator.StartsWith("HDAUDIO\\", StringComparison.Ordinal) ||
            normalizedEnumerator.StartsWith("INTELAUDIO\\", StringComparison.Ordinal) ||
            combined.Contains("microphone array", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("built-in", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("builtin", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("internal microphone", StringComparison.OrdinalIgnoreCase) ||
            (combined.Contains("realtek", StringComparison.OrdinalIgnoreCase) &&
             formFactor is null or FormFactorMicrophone or FormFactorHeadset or FormFactorHandset))
        {
            return AudioDeviceTransport.BuiltIn;
        }

        return AudioDeviceTransport.Unknown;
    }

    private static AudioDeviceTransport ClassifyEndpoint(MMDevice device)
    {
        PropertyStore? store = null;
        try
        {
            store = device.Properties;
        }
        catch
        {
            // Unreadable property store falls back safely to friendly/icon hints or Unknown.
        }

        var formFactor = TryGetPropertyInt(store, PropertyKeys.PKEY_AudioEndpoint_FormFactor);
        var enumeratorName = TryResolveHardwareEnumerator(
            device,
            store,
            TryGetPropertyString(store, DevpkeyDeviceEnumeratorName));
        var interfaceFriendlyName = TryGetPropertyString(store, PropertyKeys.PKEY_DeviceInterface_FriendlyName);
        var deviceDescription = TryGetPropertyString(store, PropertyKeys.PKEY_Device_DeviceDesc);
        var friendlyName = TryReadString(() => device.FriendlyName);
        var iconPath = TryReadString(() => device.IconPath);

        if (string.IsNullOrWhiteSpace(interfaceFriendlyName))
        {
            interfaceFriendlyName = TryReadString(() => device.DeviceFriendlyName);
        }

        return ClassifyTransport(
            formFactor,
            enumeratorName,
            interfaceFriendlyName,
            deviceDescription,
            friendlyName,
            iconPath);
    }

    private static bool ContainsVirtualAdapterMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains("vb-audio", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("vbaudio", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("vb-cable", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cable output", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cable input", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("hi-fi cable", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("voicemeeter", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("wave link", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("wavelink", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("steelseries sonar", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("sonar microphone", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("stereo mix", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("what u hear", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("blackhole", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("soundflower", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("loopback", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cable-a", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cable-b", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("steelseries sonar", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("sonar microphone", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsWholeWord(string text, string word)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word))
        {
            return false;
        }

        var index = 0;
        while (index <= text.Length - word.Length)
        {
            var match = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                return false;
            }

            var startBoundary = match == 0 || !char.IsLetterOrDigit(text[match - 1]);
            var endIndex = match + word.Length;
            var endBoundary = endIndex == text.Length || !char.IsLetterOrDigit(text[endIndex]);
            if (startBoundary && endBoundary)
            {
                return true;
            }

            index = match + word.Length;
        }

        return false;
    }

    private static int? TryGetPropertyInt(PropertyStore? store, PropertyKey key)
    {
        if (store is null)
        {
            return null;
        }

        try
        {
            if (!store.Contains(key))
            {
                return null;
            }

            var value = store[key].Value;
            return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetPropertyString(PropertyStore? store, PropertyKey key)
    {
        if (store is null)
        {
            return null;
        }

        try
        {
            if (!store.Contains(key))
            {
                return null;
            }

            return store[key].Value?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadString(Func<string> reader)
    {
        try
        {
            return reader();
        }
        catch
        {
            return null;
        }
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

    private static string? TryResolveHardwareEnumerator(
        MMDevice device,
        PropertyStore? store,
        string? endpointEnumerator)
    {
        if (!string.IsNullOrWhiteSpace(endpointEnumerator) &&
            !string.Equals(endpointEnumerator, "SWD", StringComparison.OrdinalIgnoreCase))
        {
            return endpointEnumerator;
        }

        var instanceId = TryGetPropertyString(store, DevpkeyDeviceInstanceId);
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            var endpointId = TryReadString(() => device.ID);
            if (!string.IsNullOrWhiteSpace(endpointId))
            {
                instanceId = $"SWD\\MMDEVAPI\\{endpointId}";
            }
        }

        return TryResolveParentHardwareEnumerator(instanceId) ?? endpointEnumerator;
    }

    internal static string? TryResolveParentHardwareEnumerator(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        try
        {
            if (CM_Locate_DevNodeW(out var currentDevInst, instanceId, CmLocateDevNodeNormal) != CrSuccess)
            {
                return null;
            }

            for (var hop = 0; hop < MaxParentEnumeratorHops; hop++)
            {
                if (CM_Get_Parent(out var parentDevInst, currentDevInst, 0) != CrSuccess)
                {
                    return null;
                }

                var parentId = TryReadDeviceId(parentDevInst);
                if (string.IsNullOrWhiteSpace(parentId))
                {
                    return null;
                }

                var enumerator = ExtractEnumeratorPrefix(parentId);
                if (string.IsNullOrWhiteSpace(enumerator) ||
                    string.Equals(enumerator, "HTREE", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (!string.Equals(enumerator, "SWD", StringComparison.OrdinalIgnoreCase))
                {
                    return enumerator;
                }

                currentDevInst = parentDevInst;
            }
        }
        catch
        {
            // Keep endpoint-level classification when PnP devnode lookup is unavailable.
        }

        return null;
    }

    internal static string? ExtractEnumeratorPrefix(string? pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId))
        {
            return null;
        }

        var slashIndex = pnpDeviceId.IndexOf('\\');
        return slashIndex > 0 ? pnpDeviceId[..slashIndex] : pnpDeviceId;
    }

    private static string? TryReadDeviceId(uint devInst)
    {
        if (CM_Get_Device_ID_Size(out var charCount, devInst, 0) != CrSuccess ||
            charCount == 0 ||
            charCount > 4096)
        {
            return null;
        }

        var buffer = new char[charCount + 1];
        if (CM_Get_Device_IDW(devInst, buffer, (uint)buffer.Length, 0) != CrSuccess)
        {
            return null;
        }

        var nullIndex = Array.IndexOf(buffer, '\0');
        var length = nullIndex >= 0 ? nullIndex : buffer.Length;
        return new string(buffer, 0, length);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("CfgMgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("CfgMgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_IDW(uint dnDevInst, [Out] char[] buffer, uint bufferLen, uint ulFlags);
}
