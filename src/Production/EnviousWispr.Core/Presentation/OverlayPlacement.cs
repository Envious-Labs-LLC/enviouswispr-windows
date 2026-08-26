namespace EnviousWispr.Core.Presentation;

public readonly record struct DisplayWorkArea(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);
}

public readonly record struct OverlayPosition(int X, int Y);

public static class OverlayPlacement
{
    public static OverlayPosition BottomCenter(
        DisplayWorkArea workArea,
        int overlayWidth,
        int overlayHeight,
        int margin)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(overlayWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(overlayHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(margin);
        if (workArea.Width == 0 || workArea.Height == 0)
        {
            throw new ArgumentException("The display work area must have positive dimensions.", nameof(workArea));
        }

        var x = workArea.Left + Math.Max(0, (workArea.Width - overlayWidth) / 2);
        var y = workArea.Bottom - overlayHeight - margin;
        y = Math.Max(workArea.Top, y);
        return new OverlayPosition(x, y);
    }
}
