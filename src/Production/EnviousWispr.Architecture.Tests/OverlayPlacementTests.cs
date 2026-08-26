using EnviousWispr.Core.Presentation;

namespace EnviousWispr.Architecture.Tests;

public sealed class OverlayPlacementTests
{
    [Theory]
    [InlineData(0, 0, 2560, 1392, 1090, 28)]
    [InlineData(-1920, 0, 0, 1040, -1150, 28)]
    [InlineData(2560, -180, 4480, 860, 3330, -152)]
    public void TopCenterUsesTheTargetMonitorsSignedWorkArea(
        int left,
        int top,
        int right,
        int bottom,
        int expectedX,
        int expectedY)
    {
        var position = OverlayPlacement.TopCenter(
            new DisplayWorkArea(left, top, right, bottom),
            overlayWidth: 380,
            overlayHeight: 108,
            margin: 28);

        Assert.Equal(new OverlayPosition(expectedX, expectedY), position);
    }

    [Theory]
    [InlineData(0, 0, 2560, 1392, 1090, 1256)]
    [InlineData(-1920, 0, 0, 1040, -1150, 904)]
    [InlineData(2560, -180, 4480, 860, 3330, 724)]
    public void BottomCenterUsesTheTargetMonitorsSignedWorkArea(
        int left,
        int top,
        int right,
        int bottom,
        int expectedX,
        int expectedY)
    {
        var position = OverlayPlacement.BottomCenter(
            new DisplayWorkArea(left, top, right, bottom),
            overlayWidth: 380,
            overlayHeight: 108,
            margin: 28);

        Assert.Equal(new OverlayPosition(expectedX, expectedY), position);
    }

    [Fact]
    public void OversizedOverlayStaysInsideTheWorkAreasTopLeftBoundary()
    {
        var position = OverlayPlacement.BottomCenter(
            new DisplayWorkArea(-100, -50, 100, 50),
            overlayWidth: 380,
            overlayHeight: 108,
            margin: 28);

        Assert.Equal(new OverlayPosition(-100, -50), position);
    }

    [Fact]
    public void OversizedTopOverlayStaysInsideTheWorkAreasTopLeftBoundary()
    {
        var position = OverlayPlacement.TopCenter(
            new DisplayWorkArea(-100, -50, 100, 50),
            overlayWidth: 380,
            overlayHeight: 108,
            margin: 28);

        Assert.Equal(new OverlayPosition(-100, -50), position);
    }

    [Fact]
    public void InvalidWorkAreaIsRejected()
    {
        Assert.Throws<ArgumentException>(() => OverlayPlacement.BottomCenter(
            new DisplayWorkArea(0, 0, 0, 100),
            overlayWidth: 380,
            overlayHeight: 108,
            margin: 28));
        Assert.Throws<ArgumentException>(() => OverlayPlacement.TopCenter(
            new DisplayWorkArea(0, 0, 0, 100),
            overlayWidth: 380,
            overlayHeight: 108,
            margin: 28));
    }
}
