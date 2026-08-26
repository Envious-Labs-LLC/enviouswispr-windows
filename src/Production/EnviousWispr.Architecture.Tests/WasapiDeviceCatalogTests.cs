using EnviousWispr.Audio;

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
        });
        Assert.InRange(devices.Count(device => device.IsDefault), 0, 1);
    }
}
