namespace EnviousWispr.Core.Audio;

/// <summary>
/// Why a capture device was chosen by <see cref="InputDeviceResolver"/>.
/// </summary>
public enum InputDeviceResolutionSource
{
    None,
    Pinned,
    SystemDefault,
    PhysicalPriorityFallback,
    VirtualFallback,
}

/// <summary>
/// Immutable result of resolving which active capture device to use for dictation or testing.
/// </summary>
public sealed record InputDeviceResolution(
    AudioDeviceInfo? SelectedDevice,
    InputDeviceResolutionSource Source,
    AudioDeviceInfo? BypassedDefaultDevice = null,
    bool FallbackFromPinned = false)
{
    public AudioDeviceId? SelectedDeviceId => SelectedDevice?.Id;
}

/// <summary>
/// Stateless, hardware-free microphone selection authority shared by live capture, microphone tests, and settings UI.
/// </summary>
public static class InputDeviceResolver
{
    public static InputDeviceResolution Resolve(
        IReadOnlyList<AudioDeviceInfo> devices,
        string? preferredDeviceId = null)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var hasPreferred = !string.IsNullOrWhiteSpace(preferredDeviceId);
        var active = devices.Where(static device => device.IsActive).ToList();
        if (active.Count == 0)
        {
            return new InputDeviceResolution(
                SelectedDevice: null,
                Source: InputDeviceResolutionSource.None,
                FallbackFromPinned: hasPreferred);
        }

        if (hasPreferred)
        {
            var pinned = active.FirstOrDefault(device =>
                string.Equals(device.Id.Value, preferredDeviceId, StringComparison.Ordinal));
            if (pinned is not null)
            {
                return new InputDeviceResolution(pinned, InputDeviceResolutionSource.Pinned);
            }
        }

        var physical = active.Where(static device => !device.IsVirtualOrLoopback).ToList();
        var defaultDevice = active.FirstOrDefault(static device => device.IsDefault);

        if (defaultDevice is not null)
        {
            if (!defaultDevice.IsVirtualOrLoopback)
            {
                return new InputDeviceResolution(
                    defaultDevice,
                    InputDeviceResolutionSource.SystemDefault,
                    FallbackFromPinned: hasPreferred);
            }

            if (physical.Count > 0)
            {
                return new InputDeviceResolution(
                    SelectByPriority(physical),
                    InputDeviceResolutionSource.PhysicalPriorityFallback,
                    BypassedDefaultDevice: defaultDevice,
                    FallbackFromPinned: hasPreferred);
            }

            return new InputDeviceResolution(
                defaultDevice,
                InputDeviceResolutionSource.VirtualFallback,
                FallbackFromPinned: hasPreferred);
        }

        if (physical.Count > 0)
        {
            return new InputDeviceResolution(
                SelectByPriority(physical),
                InputDeviceResolutionSource.PhysicalPriorityFallback,
                FallbackFromPinned: hasPreferred);
        }

        return new InputDeviceResolution(
            active[0],
            InputDeviceResolutionSource.VirtualFallback,
            FallbackFromPinned: hasPreferred);
    }

    public static int GetTransportPriorityRank(AudioDeviceTransport transport) =>
        transport switch
        {
            AudioDeviceTransport.BuiltIn => 0,
            AudioDeviceTransport.Usb => 1,
            AudioDeviceTransport.Bluetooth => 2,
            AudioDeviceTransport.DockOrLine => 3,
            AudioDeviceTransport.Unknown => 4,
            AudioDeviceTransport.Virtual => 5,
            _ => 6,
        };

    public static string? FormatTransportLabel(AudioDeviceTransport transport) =>
        transport switch
        {
            AudioDeviceTransport.Usb => "USB",
            AudioDeviceTransport.BuiltIn => "Built-in",
            AudioDeviceTransport.Bluetooth => "Bluetooth",
            AudioDeviceTransport.DockOrLine => "Dock/Line",
            AudioDeviceTransport.Virtual => "Virtual",
            _ => null,
        };

    public static string FormatDeviceDisplayName(AudioDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var transportLabel = FormatTransportLabel(device.Transport);
        if (transportLabel is not null && device.IsDefault)
        {
            return $"{device.DisplayName} ({transportLabel} · Windows default)";
        }

        if (transportLabel is not null)
        {
            return $"{device.DisplayName} ({transportLabel})";
        }

        return device.IsDefault
            ? $"{device.DisplayName} (Windows default)"
            : device.DisplayName;
    }

    public static string FormatAutoSelectionLabel(InputDeviceResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        if (resolution.SelectedDevice is not { } selected)
        {
            return "Auto";
        }

        var transportLabel = FormatTransportLabel(selected.Transport);
        return transportLabel is null
            ? $"Auto · {selected.DisplayName}"
            : $"Auto · {selected.DisplayName} ({transportLabel})";
    }

    private static AudioDeviceInfo SelectByPriority(IReadOnlyList<AudioDeviceInfo> devices) =>
        devices.OrderBy(static device => GetTransportPriorityRank(device.Transport)).First();
}
