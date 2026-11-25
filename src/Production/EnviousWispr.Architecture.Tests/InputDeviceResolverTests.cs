using EnviousWispr.Core.Audio;

namespace EnviousWispr.Architecture.Tests;

public sealed class InputDeviceResolverTests
{
    [Fact]
    public void ResolveReturnsNoneWhenNoActiveDevicesExist()
    {
        IReadOnlyList<AudioDeviceInfo> devices =
        [
            new(new AudioDeviceId("inactive"), "Old Mic", IsDefault: true, IsActive: false, AudioDeviceTransport.Usb),
        ];

        var resolution = InputDeviceResolver.Resolve(devices, preferredDeviceId: "inactive");

        Assert.Null(resolution.SelectedDevice);
        Assert.Null(resolution.SelectedDeviceId);
        Assert.Equal(InputDeviceResolutionSource.None, resolution.Source);
        Assert.True(resolution.FallbackFromPinned);
    }

    [Fact]
    public void ResolveSelectsPinnedActiveDeviceEvenWhenVirtualOrNonDefault()
    {
        var virtualPinned = new AudioDeviceInfo(
            new AudioDeviceId("cable"), "CABLE Output", IsDefault: false, IsActive: true, AudioDeviceTransport.Virtual);
        var usbDefault = new AudioDeviceInfo(
            new AudioDeviceId("usb"), "Logitech BRIO", IsDefault: true, IsActive: true, AudioDeviceTransport.Usb);

        var resolution = InputDeviceResolver.Resolve([usbDefault, virtualPinned], preferredDeviceId: "cable");

        Assert.Equal(virtualPinned, resolution.SelectedDevice);
        Assert.Equal(InputDeviceResolutionSource.Pinned, resolution.Source);
        Assert.Null(resolution.BypassedDefaultDevice);
        Assert.False(resolution.FallbackFromPinned);
    }

    [Fact]
    public void ResolveFallsBackCleanlyWhenPinnedDeviceIsUnplugged()
    {
        var builtIn = new AudioDeviceInfo(
            new AudioDeviceId("builtin"), "Microphone Array", IsDefault: true, IsActive: true, AudioDeviceTransport.BuiltIn);
        var usb = new AudioDeviceInfo(
            new AudioDeviceId("usb"), "Logitech BRIO", IsDefault: false, IsActive: true, AudioDeviceTransport.Usb);

        var resolution = InputDeviceResolver.Resolve([usb, builtIn], preferredDeviceId: "unplugged-yeti");

        Assert.Equal(builtIn, resolution.SelectedDevice);
        Assert.Equal(InputDeviceResolutionSource.SystemDefault, resolution.Source);
        Assert.True(resolution.FallbackFromPinned);
    }

    [Fact]
    public void ResolveAutoUsesPhysicalWindowsDefault()
    {
        var usb = new AudioDeviceInfo(
            new AudioDeviceId("usb"), "Logitech BRIO", IsDefault: false, IsActive: true, AudioDeviceTransport.Usb);
        var bluetooth = new AudioDeviceInfo(
            new AudioDeviceId("bt"), "AirPods Pro", IsDefault: true, IsActive: true, AudioDeviceTransport.Bluetooth);

        var resolution = InputDeviceResolver.Resolve([usb, bluetooth]);

        Assert.Equal(bluetooth, resolution.SelectedDevice);
        Assert.Equal(InputDeviceResolutionSource.SystemDefault, resolution.Source);
        Assert.Null(resolution.BypassedDefaultDevice);
    }

    [Fact]
    public void ResolveAutoBypassesVirtualDefaultForPhysicalDevicePriority()
    {
        var virtualDefault = new AudioDeviceInfo(
            new AudioDeviceId("cable"), "CABLE Output", IsDefault: true, IsActive: true, AudioDeviceTransport.Virtual);
        var bluetooth = new AudioDeviceInfo(
            new AudioDeviceId("bt"), "Hands-Free AG", IsDefault: false, IsActive: true, AudioDeviceTransport.Bluetooth);
        var builtIn = new AudioDeviceInfo(
            new AudioDeviceId("builtin"), "Internal Mic", IsDefault: false, IsActive: true, AudioDeviceTransport.BuiltIn);
        var usb = new AudioDeviceInfo(
            new AudioDeviceId("usb"), "Plantronics USB", IsDefault: false, IsActive: true, AudioDeviceTransport.Usb);

        var resolution = InputDeviceResolver.Resolve([virtualDefault, bluetooth, builtIn, usb]);

        Assert.Equal(builtIn, resolution.SelectedDevice);
        Assert.Equal(InputDeviceResolutionSource.PhysicalPriorityFallback, resolution.Source);
        Assert.Equal(virtualDefault, resolution.BypassedDefaultDevice);
    }

    [Fact]
    public void ResolvePrefersPhysicalTransportsInStrictPriorityOrderWhenNoDefaultExists()
    {
        var unknown = new AudioDeviceInfo(new AudioDeviceId("unk"), "Unknown Mic", IsDefault: false, IsActive: true, AudioDeviceTransport.Unknown);
        var dock = new AudioDeviceInfo(new AudioDeviceId("dock"), "Line In", IsDefault: false, IsActive: true, AudioDeviceTransport.DockOrLine);
        var bt = new AudioDeviceInfo(new AudioDeviceId("bt"), "Bluetooth Headset", IsDefault: false, IsActive: true, AudioDeviceTransport.Bluetooth);
        var usb = new AudioDeviceInfo(new AudioDeviceId("usb"), "USB Mic", IsDefault: false, IsActive: true, AudioDeviceTransport.Usb);
        var builtIn = new AudioDeviceInfo(new AudioDeviceId("bi"), "Microphone Array", IsDefault: false, IsActive: true, AudioDeviceTransport.BuiltIn);

        Assert.Equal(builtIn, InputDeviceResolver.Resolve([unknown, dock, bt, usb, builtIn]).SelectedDevice);
        Assert.Equal(usb, InputDeviceResolver.Resolve([unknown, dock, bt, usb]).SelectedDevice);
        Assert.Equal(bt, InputDeviceResolver.Resolve([unknown, dock, bt]).SelectedDevice);
        Assert.Equal(dock, InputDeviceResolver.Resolve([unknown, dock]).SelectedDevice);
        Assert.Equal(unknown, InputDeviceResolver.Resolve([unknown]).SelectedDevice);
    }

    [Fact]
    public void ResolveFallsBackToVirtualOnlyWhenNoPhysicalDeviceExists()
    {
        var virtualDefault = new AudioDeviceInfo(
            new AudioDeviceId("cable"), "CABLE Output", IsDefault: true, IsActive: true, AudioDeviceTransport.Virtual);
        var virtualSecondary = new AudioDeviceInfo(
            new AudioDeviceId("vm"), "Voicemeeter Out", IsDefault: false, IsActive: true, AudioDeviceTransport.Virtual);

        var withDefault = InputDeviceResolver.Resolve([virtualSecondary, virtualDefault]);
        Assert.Equal(virtualDefault, withDefault.SelectedDevice);
        Assert.Equal(InputDeviceResolutionSource.VirtualFallback, withDefault.Source);
        Assert.Null(withDefault.BypassedDefaultDevice);

        var withoutDefault = InputDeviceResolver.Resolve([
            virtualSecondary with { IsDefault = false },
            virtualDefault with { IsDefault = false },
        ]);
        Assert.Equal(virtualSecondary.Id, withoutDefault.SelectedDeviceId);
        Assert.Equal(InputDeviceResolutionSource.VirtualFallback, withoutDefault.Source);
    }

    [Fact]
    public void FormattingHelpersProduceExpectedAutoAndTransportLabels()
    {
        var usb = new AudioDeviceInfo(
            new AudioDeviceId("usb"), "Logitech BRIO", IsDefault: false, IsActive: true, AudioDeviceTransport.Usb);
        var builtInDefault = new AudioDeviceInfo(
            new AudioDeviceId("bi"), "Microphone Array", IsDefault: true, IsActive: true, AudioDeviceTransport.BuiltIn);
        var virtualDev = new AudioDeviceInfo(
            new AudioDeviceId("v"), "CABLE Output", IsDefault: true, IsActive: true, AudioDeviceTransport.Virtual);
        var unknown = new AudioDeviceInfo(
            new AudioDeviceId("u"), "Generic Mic", IsDefault: false, IsActive: true, AudioDeviceTransport.Unknown);

        Assert.Equal("Auto · Logitech BRIO (USB)", InputDeviceResolver.FormatAutoSelectionLabel(InputDeviceResolver.Resolve([usb])));
        Assert.Equal("Auto", InputDeviceResolver.FormatAutoSelectionLabel(InputDeviceResolver.Resolve([])));
        Assert.Equal("Microphone Array (Built-in · Windows default)", InputDeviceResolver.FormatDeviceDisplayName(builtInDefault));
        Assert.Equal("CABLE Output (Virtual · Windows default)", InputDeviceResolver.FormatDeviceDisplayName(virtualDev));
        Assert.Equal("Generic Mic", InputDeviceResolver.FormatDeviceDisplayName(unknown));
    }
}
