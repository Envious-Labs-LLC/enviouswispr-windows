using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;

namespace EnviousWispr.Architecture.Tests;

public sealed class WasapiDeviceCatalogTests
{
    [Fact]
    public async Task EnumerationReturnsOnlyWellFormedActiveCaptureDevices()
    {
        using var catalog = new WasapiDeviceCatalog();

        var devices = await catalog.GetCaptureDevicesAsync();

        Assert.All(devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id.Value));
            Assert.False(string.IsNullOrWhiteSpace(device.DisplayName));
            Assert.True(device.IsActive);
            Assert.True(Enum.IsDefined(device.Transport));
            Assert.Equal(device.Transport == AudioDeviceTransport.Virtual, device.IsVirtualOrLoopback);
        });
        Assert.InRange(devices.Count(device => device.IsDefault), 0, 1);

        if (devices.FirstOrDefault(d => d.DisplayName.Contains("Logitech BRIO", StringComparison.OrdinalIgnoreCase)) is { } brio)
        {
            Assert.Equal(AudioDeviceTransport.Usb, brio.Transport);
        }

        if (devices.FirstOrDefault(d => d.DisplayName.Contains("Plantronics", StringComparison.OrdinalIgnoreCase)) is { } plantronics)
        {
            Assert.Equal(AudioDeviceTransport.Usb, plantronics.Transport);
        }

        if (devices.FirstOrDefault(d => d.DisplayName.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase)) is { } vbCable)
        {
            Assert.Equal(AudioDeviceTransport.Virtual, vbCable.Transport);
        }
    }

    [Theory]
    [InlineData(4, "USB", "Logitech BRIO", "Microphone", "Microphone (Logitech BRIO)", AudioDeviceTransport.Usb)]
    [InlineData(5, "USB", "Plantronics Blackwire 5220 Series", "Headset Microphone", "Headset Microphone (Plantronics Blackwire 5220 Series)", AudioDeviceTransport.Usb)]
    [InlineData(4, "USB", "Cable Mic", "Audio Endpoint", "Cable Mic", AudioDeviceTransport.Usb)]
    [InlineData(4, "SWD", "Cable Mic", "Audio Endpoint", "Cable Mic", AudioDeviceTransport.Unknown)]
    [InlineData(4, "USB", "Sonar Pro USB", "Audio Endpoint", "Microphone (Sonar Pro USB)", AudioDeviceTransport.Usb)]
    [InlineData(null, null, "USB Audio Device", null, "Microphone (USB Audio)", AudioDeviceTransport.Usb)]
    [InlineData(5, "BTHHFENUM", "AirPods Pro", "Hands-Free AG Audio", "Headset (AirPods Pro Hands-Free AG Audio)", AudioDeviceTransport.Bluetooth)]
    [InlineData(null, "BTHENUM", "Sony WH-1000XM5", "Bluetooth Audio", "Sony WH-1000XM5", AudioDeviceTransport.Bluetooth)]
    [InlineData(4, "HDAUDIO", "Realtek High Definition Audio", "Microphone", "Microphone Array (Realtek(R) Audio)", AudioDeviceTransport.BuiltIn)]
    [InlineData(4, "INTELAUDIO", "Intel Smart Sound Technology", "Microphone Array", "Microphone Array (Intel)", AudioDeviceTransport.BuiltIn)]
    [InlineData(2, "HDAUDIO", "Realtek Audio", "Line In", "Line In (Realtek(R) Audio)", AudioDeviceTransport.DockOrLine)]
    [InlineData(8, null, "Optical Audio", "SPDIF Interface", "Digital Input", AudioDeviceTransport.DockOrLine)]
    [InlineData(2, "ROOT", "Audio", "Audio Endpoint", "Generic Cable Device", AudioDeviceTransport.Virtual)]
    [InlineData(2, "ROOT", "VB-Audio Virtual Cable", "CABLE Output", "CABLE Output (VB-Audio Virtual Cable)", AudioDeviceTransport.Virtual)]
    [InlineData(4, "ROOT", "Voicemeeter", "Voicemeeter Out B1", "Voicemeeter Out B1", AudioDeviceTransport.Virtual)]
    [InlineData(4, null, "Elgato Wave Link", "Wave Link MicrophoneFX", "Wave Link MicrophoneFX", AudioDeviceTransport.Virtual)]
    [InlineData(4, null, "SteelSeries Sonar", "SteelSeries Sonar - Microphone", "SteelSeries Sonar - Microphone", AudioDeviceTransport.Virtual)]
    [InlineData(2, "HDAUDIO", "Realtek Audio", "Stereo Mix", "Stereo Mix (Realtek(R) Audio)", AudioDeviceTransport.Virtual)]
    [InlineData(4, null, "BlackHole 2ch", "BlackHole 2ch", "BlackHole 2ch", AudioDeviceTransport.Virtual)]
    [InlineData(null, null, null, null, null, AudioDeviceTransport.Unknown)]
    [InlineData(4, null, null, null, "Generic Audio Device", AudioDeviceTransport.Unknown)]
    public void ClassifyTransportDetectsHardwareBusesAndVirtualLoopbackAdapters(
        int? formFactor,
        string? enumeratorName,
        string? interfaceFriendlyName,
        string? deviceDescription,
        string? friendlyName,
        AudioDeviceTransport expected)
    {
        var actual = WasapiDeviceCatalog.ClassifyTransport(
            formFactor,
            enumeratorName,
            interfaceFriendlyName,
            deviceDescription,
            friendlyName);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(@"USB\VID_046D&PID_085E&MI_02\6&23AB&0&0002", "USB")]
    [InlineData(@"ROOT\MEDIA\0000", "ROOT")]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC", "HDAUDIO")]
    [InlineData(null, null)]
    public void ExtractEnumeratorPrefixParsesBusToken(string? pnpDeviceId, string? expected)
    {
        Assert.Equal(expected, WasapiDeviceCatalog.ExtractEnumeratorPrefix(pnpDeviceId));
    }

    [Fact]
    public void TryResolveParentHardwareEnumeratorHandlesMissingOrMalformedIdsSafely()
    {
        Assert.Null(WasapiDeviceCatalog.TryResolveParentHardwareEnumerator(null));
        Assert.Null(WasapiDeviceCatalog.TryResolveParentHardwareEnumerator("   "));
        Assert.Null(WasapiDeviceCatalog.TryResolveParentHardwareEnumerator(@"SWD\MMDEVAPI\{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}"));
    }
}
