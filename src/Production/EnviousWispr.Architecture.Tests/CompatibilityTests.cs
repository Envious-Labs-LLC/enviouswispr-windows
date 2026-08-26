using EnviousWispr.Core.Compatibility;

namespace EnviousWispr.Architecture.Tests;

public sealed class CompatibilityTests
{
    [Theory]
    [InlineData(7, PhysicalMemoryTier.Below8GiB)]
    [InlineData(8, PhysicalMemoryTier.From8To15GiB)]
    [InlineData(15, PhysicalMemoryTier.From8To15GiB)]
    [InlineData(16, PhysicalMemoryTier.From16To31GiB)]
    [InlineData(32, PhysicalMemoryTier.From32To63GiB)]
    [InlineData(64, PhysicalMemoryTier.AtLeast64GiB)]
    public void PhysicalMemoryUsesStablePublicBuckets(
        ulong gibibytes,
        PhysicalMemoryTier expected)
    {
        Assert.Equal(
            expected,
            CompatibilityBuckets.ClassifyMemory(gibibytes * 1024UL * 1024 * 1024));
    }

    [Theory]
    [InlineData(96, DisplayScaleTier.From100To124Percent)]
    [InlineData(120, DisplayScaleTier.From125To149Percent)]
    [InlineData(144, DisplayScaleTier.From150To199Percent)]
    [InlineData(192, DisplayScaleTier.AtLeast200Percent)]
    public void DisplayDpiUsesStablePublicBuckets(uint dpi, DisplayScaleTier expected)
    {
        Assert.Equal(expected, CompatibilityBuckets.ClassifyDisplayScale(dpi));
    }

    [Theory]
    [InlineData(1366, 768, PrimaryResolutionTier.Below1080p)]
    [InlineData(1920, 1080, PrimaryResolutionTier.FullHd)]
    [InlineData(2560, 1440, PrimaryResolutionTier.QuadHd)]
    [InlineData(3840, 2160, PrimaryResolutionTier.FourKOrHigher)]
    public void ResolutionUsesOrientationIndependentPublicBuckets(
        int width,
        int height,
        PrimaryResolutionTier expected)
    {
        Assert.Equal(expected, CompatibilityBuckets.ClassifyResolution(width, height));
        Assert.Equal(expected, CompatibilityBuckets.ClassifyResolution(height, width));
    }

    [Fact]
    public void CompatibilitySnapshotCannotHoldNamesIdentifiersOrPaths()
    {
        var forbiddenTokens = new[]
        {
            "name",
            "identifier",
            "path",
            "model",
            "serial",
            "account",
            "user",
            "text",
            "audio",
            "clipboard",
        };
        var propertyNames = typeof(WindowsCompatibilitySnapshot)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, propertyName =>
            forbiddenTokens.Any(token => propertyName.Contains(token, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(
            typeof(WindowsCompatibilitySnapshot).GetProperties(),
            property => property.PropertyType == typeof(string));
    }
}
