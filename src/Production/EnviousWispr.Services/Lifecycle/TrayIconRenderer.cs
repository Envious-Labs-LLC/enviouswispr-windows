using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using EnviousWispr.Core.Presentation;

namespace EnviousWispr.Services.Lifecycle;

/// <summary>Draws the tray icon for one state, at one size.</summary>
/// <remarks>
/// DRAWN VIA 4X SUPERSAMPLING RATHER THAN SHIPPED AS STATIC .ICO FILES, because Windows asks for
/// different tray icon sizes (16, 20, 24, 32, 48) depending on display DPI and taskbar density.
/// Rendering at 4x onto an antialiased high-resolution surface and downsampling with HighQualityBicubic
/// preserves the rounded bar caps and crisp horizontal mouth gap of the EnviousWispr waveform lips
/// at every scale.
///
/// EVERY STATE HAS ITS OWN SHAPE, NOT JUST ITS OWN COLOUR. Under High Contrast every colour collapses
/// to <see cref="SystemColors.WindowText"/>, so Idle (compact lips), Recording (full-scale lips),
/// Processing (rotating spectrum ring), and Error (faulted lips with collapsed centre) remain distinct
/// even without colour.
/// </remarks>
public static class TrayIconRenderer
{
    private const int SupersampleFactor = 4;

    private readonly record struct LipBar(float Top, float Height);

    // 9-column two-row waveform lips silhouette modeled directly from EnviousWisprMark.svg
    // (normalized to mark bounds: width 206, height 151.67, origin at top-left of mark).
    // Upper lip has symmetrical twin peaks (cols 2 & 6) with a Cupid's-bow centre dip (col 4).
    private static readonly LipBar[] UpperLipNormalized =
    [
        new(40.84f / 151.67f, 20f / 151.67f),
        new(22.39f / 151.67f, 32f / 151.67f),
        new(0.00f / 151.67f, 48f / 151.67f),
        new(19.68f / 151.67f, 36f / 151.67f),
        new(38.07f / 151.67f, 24f / 151.67f),
        new(19.68f / 151.67f, 36f / 151.67f),
        new(0.00f / 151.67f, 48f / 151.67f),
        new(22.39f / 151.67f, 32f / 151.67f),
        new(40.84f / 151.67f, 20f / 151.67f),
    ];

    // Fuller lower lip curving to a central downward peak (col 4), separated by a crisp mouth gap.
    private static readonly LipBar[] LowerLipNormalized =
    [
        new(82.44f / 151.67f, 20f / 151.67f),
        new(75.99f / 151.67f, 36f / 151.67f),
        new(69.60f / 151.67f, 48f / 151.67f),
        new(77.28f / 151.67f, 60f / 151.67f),
        new(83.67f / 151.67f, 68f / 151.67f),
        new(77.28f / 151.67f, 60f / 151.67f),
        new(69.60f / 151.67f, 48f / 151.67f),
        new(75.99f / 151.67f, 36f / 151.67f),
        new(82.44f / 151.67f, 20f / 151.67f),
    ];

    // 7-column compact silhouette for 16px/20px taskbars so every bar stays >= 1px wide with crisp gaps.
    private static readonly LipBar[] CompactUpperLipNormalized =
    [
        new(38.00f / 151.67f, 23f / 151.67f),
        new(0.00f / 151.67f, 48f / 151.67f),
        new(19.68f / 151.67f, 36f / 151.67f),
        new(37.00f / 151.67f, 25f / 151.67f),
        new(19.68f / 151.67f, 36f / 151.67f),
        new(0.00f / 151.67f, 48f / 151.67f),
        new(38.00f / 151.67f, 23f / 151.67f),
    ];

    private static readonly LipBar[] CompactLowerLipNormalized =
    [
        new(81.00f / 151.67f, 22f / 151.67f),
        new(70.00f / 151.67f, 46f / 151.67f),
        new(77.00f / 151.67f, 58f / 151.67f),
        new(83.67f / 151.67f, 68f / 151.67f),
        new(77.00f / 151.67f, 58f / 151.67f),
        new(70.00f / 151.67f, 46f / 151.67f),
        new(81.00f / 151.67f, 22f / 151.67f),
    ];

    // Error silhouette: outer lip corners remain while the middle waveform breaks/flattens,
    // staying visually distinct from both Idle and Recording even in monochrome High Contrast.
    private static readonly LipBar[] ErrorUpperLipNormalized =
    [
        new(30.00f / 151.67f, 30f / 151.67f),
        new(12.00f / 151.67f, 42f / 151.67f),
        new(36.00f / 151.67f, 20f / 151.67f),
        new(42.00f / 151.67f, 18f / 151.67f),
        new(42.00f / 151.67f, 18f / 151.67f),
        new(42.00f / 151.67f, 18f / 151.67f),
        new(36.00f / 151.67f, 20f / 151.67f),
        new(12.00f / 151.67f, 42f / 151.67f),
        new(30.00f / 151.67f, 30f / 151.67f),
    ];

    private static readonly LipBar[] ErrorLowerLipNormalized =
    [
        new(86.00f / 151.67f, 30f / 151.67f),
        new(80.00f / 151.67f, 46f / 151.67f),
        new(86.00f / 151.67f, 20f / 151.67f),
        new(90.00f / 151.67f, 18f / 151.67f),
        new(90.00f / 151.67f, 18f / 151.67f),
        new(90.00f / 151.67f, 18f / 151.67f),
        new(86.00f / 151.67f, 20f / 151.67f),
        new(80.00f / 151.67f, 46f / 151.67f),
        new(86.00f / 151.67f, 30f / 151.67f),
    ];

    private static readonly LipBar[] CompactErrorUpperLipNormalized =
    [
        new(26.00f / 151.67f, 34f / 151.67f),
        new(10.00f / 151.67f, 46f / 151.67f),
        new(40.00f / 151.67f, 18f / 151.67f),
        new(40.00f / 151.67f, 18f / 151.67f),
        new(40.00f / 151.67f, 18f / 151.67f),
        new(10.00f / 151.67f, 46f / 151.67f),
        new(26.00f / 151.67f, 34f / 151.67f),
    ];

    private static readonly LipBar[] CompactErrorLowerLipNormalized =
    [
        new(86.00f / 151.67f, 34f / 151.67f),
        new(80.00f / 151.67f, 48f / 151.67f),
        new(90.00f / 151.67f, 18f / 151.67f),
        new(90.00f / 151.67f, 18f / 151.67f),
        new(90.00f / 151.67f, 18f / 151.67f),
        new(80.00f / 151.67f, 48f / 151.67f),
        new(86.00f / 151.67f, 34f / 151.67f),
    ];

    // Matches EnviousWisprMark.svg's cross-mirrored lower-lip rainbow order over the 9-colour spectrum.
    private static readonly int[] LowerSpectrumOrder = [7, 6, 5, 4, 3, 2, 1, 0, 8];
    private static readonly int[] CompactUpperSpectrumOrder = [0, 2, 3, 4, 5, 6, 8];
    private static readonly int[] CompactLowerSpectrumOrder = [7, 5, 4, 3, 2, 1, 8];

    /// <summary>Draws one frame. The caller owns the bitmap.</summary>
    /// <param name="state">What the icon is saying.</param>
    /// <param name="size">The square edge in pixels, which the tray decides.</param>
    /// <param name="palette">The colours available, which High Contrast may collapse to one.</param>
    /// <param name="rotationDegrees">Where the processing sweep has got to. Ignored otherwise.</param>
    public static Bitmap Render(
        TrayIconState state,
        int size,
        TrayIconPalette? palette = null,
        double rotationDegrees = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        var colours = palette ?? TrayIconPalette.Brand;
        var monochromeTint = ResolveMonochromeTint(state, colours);
        var supersampledSize = size * SupersampleFactor;

        using var highRes = new Bitmap(supersampledSize, supersampledSize, PixelFormat.Format32bppPArgb);
        using (var canvas = Graphics.FromImage(highRes))
        {
            canvas.SmoothingMode = SmoothingMode.AntiAlias;
            canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
            canvas.CompositingQuality = CompositingQuality.HighQuality;
            canvas.Clear(Color.Transparent);

            // Render monochrome states in pure white before bicubic downsampling, then apply the
            // exact target RGB while keeping the bicubic-filtered alpha. This prevents bicubic
            // ringing from pushing RGB channels past their intended monochrome hue (notably under
            // High Contrast).
            var renderPalette = monochromeTint.HasValue
                ? TrayIconPalette.ForSystem(Color.White)
                : colours;

            if (state == TrayIconState.Processing)
            {
                DrawSweep(canvas, supersampledSize, renderPalette, rotationDegrees);
            }
            else
            {
                DrawWaveformLips(canvas, supersampledSize, size, state, renderPalette);
            }
        }

        var output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var outputCanvas = Graphics.FromImage(output))
        {
            outputCanvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
            outputCanvas.SmoothingMode = SmoothingMode.AntiAlias;
            outputCanvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
            outputCanvas.CompositingQuality = CompositingQuality.HighQuality;
            outputCanvas.Clear(Color.Transparent);
            outputCanvas.DrawImage(
                highRes,
                new Rectangle(0, 0, size, size),
                new Rectangle(0, 0, supersampledSize, supersampledSize),
                GraphicsUnit.Pixel);
        }

        if (monochromeTint is { } tint)
        {
            ApplyMonochromeTint(output, tint);
        }

        return output;
    }

    private static Color? ResolveMonochromeTint(TrayIconState state, TrayIconPalette colours)
    {
        switch (state)
        {
            case TrayIconState.Idle:
                return colours.Idle;
            case TrayIconState.Error:
                return colours.Fault;
            default:
                if (colours.Spectrum.Length == 0)
                {
                    return colours.Idle;
                }

                var first = colours.Spectrum[0];
                return colours.Spectrum.All(c => c.ToArgb() == first.ToArgb()) ? first : null;
        }
    }

    private static void ApplyMonochromeTint(Bitmap bitmap, Color tint)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var byteCount = Math.Abs(data.Stride) * data.Height;
            var pixels = new byte[byteCount];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, byteCount);

            for (var y = 0; y < data.Height; y++)
            {
                var rowOffset = y * data.Stride;
                for (var x = 0; x < data.Width; x++)
                {
                    var idx = rowOffset + (x * 4);
                    var alpha = pixels[idx + 3];
                    if (alpha == 0)
                    {
                        continue;
                    }

                    // Preserve the bicubic-filtered alpha (scaled by tint.A if translucent).
                    pixels[idx] = tint.B;
                    pixels[idx + 1] = tint.G;
                    pixels[idx + 2] = tint.R;
                    pixels[idx + 3] = (byte)((alpha * tint.A) / 255);
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, byteCount);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void DrawWaveformLips(
        Graphics canvas, int canvasSize, int targetSize, TrayIconState state, TrayIconPalette colours)
    {
        var compact = targetSize <= 20;
        var upper = (state == TrayIconState.Error, compact) switch
        {
            (true, true) => CompactErrorUpperLipNormalized,
            (true, false) => ErrorUpperLipNormalized,
            (false, true) => CompactUpperLipNormalized,
            (false, false) => UpperLipNormalized,
        };
        var lower = (state == TrayIconState.Error, compact) switch
        {
            (true, true) => CompactErrorLowerLipNormalized,
            (true, false) => ErrorLowerLipNormalized,
            (false, true) => CompactLowerLipNormalized,
            (false, false) => LowerLipNormalized,
        };

        // Idle is slightly more compact than Recording so the two remain shape-distinct under High Contrast
        // while keeping the exact Cupid's-bow two-row lips silhouette and crisp mouth gap.
        var markScale = state == TrayIconState.Idle ? 0.80f : 0.92f;
        var markWidth = canvasSize * markScale;
        var markHeight = markWidth * (151.67f / 206f);
        var markLeft = (canvasSize - markWidth) / 2f;
        var markTop = (canvasSize - markHeight) / 2f;

        var columns = upper.Length;
        // Derive bar width & gap from column count: 1.45:1 bar-to-gap ratio so bars stay >= 1px at 16px.
        var totalGapUnits = (columns * 1.45f) + (columns - 1);
        var gap = markWidth / totalGapUnits;
        var barWidth = gap * 1.45f;
        var columnPitch = barWidth + gap;
        var cornerRadius = Math.Max(1f, barWidth * 0.42f);

        for (var col = 0; col < columns; col++)
        {
            var x = markLeft + (col * columnPitch);

            var upperColor = ResolveLipColor(state, colours, col, isLower: false, compact);
            var lowerColor = ResolveLipColor(state, colours, col, isLower: true, compact);

            FillLipBar(canvas, x, markTop + (upper[col].Top * markHeight), barWidth, upper[col].Height * markHeight, cornerRadius, upperColor);
            FillLipBar(canvas, x, markTop + (lower[col].Top * markHeight), barWidth, lower[col].Height * markHeight, cornerRadius, lowerColor);
        }
    }

    private static Color ResolveLipColor(
        TrayIconState state, TrayIconPalette colours, int column, bool isLower, bool compact)
    {
        if (state == TrayIconState.Error)
        {
            return colours.Fault;
        }

        if (state != TrayIconState.Recording || colours.Spectrum.Length == 0)
        {
            return colours.Idle;
        }

        var index = (compact, isLower) switch
        {
            (true, true) => CompactLowerSpectrumOrder[column % CompactLowerSpectrumOrder.Length],
            (true, false) => CompactUpperSpectrumOrder[column % CompactUpperSpectrumOrder.Length],
            (false, true) => LowerSpectrumOrder[column % LowerSpectrumOrder.Length],
            (false, false) => column,
        };
        return colours.Spectrum[index % colours.Spectrum.Length];
    }

    private static void FillLipBar(
        Graphics canvas, float x, float y, float width, float height, float radius, Color colour)
    {
        using var brush = new SolidBrush(colour);
        using var path = RoundedBar(x, y, width, height, radius);
        canvas.FillPath(brush, path);
    }

    /// <summary>The rotating spectrum sweep that says work is under way.</summary>
    private static void DrawSweep(
        Graphics canvas, int size, TrayIconPalette colours, double rotationDegrees)
    {
        var thickness = Math.Max(1.5f * SupersampleFactor, size / 7f);
        var inset = thickness / 2f;
        var box = new RectangleF(inset, inset, size - thickness, size - thickness);
        const float sweep = 360f / 9f;
        for (var index = 0; index < 9; index++)
        {
            var colour = colours.Spectrum[index % colours.Spectrum.Length];
            var alpha = (int)(255 * (0.25 + (0.75 * index / 8.0)));
            using var pen = new Pen(Color.FromArgb(alpha, colour), thickness)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            canvas.DrawArc(pen, box, (float)rotationDegrees + (index * sweep), sweep);
        }
    }

    private static GraphicsPath RoundedBar(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        var cap = Math.Min(radius, Math.Min(width, height) / 2f);
        if (cap <= 0.5f)
        {
            path.AddRectangle(new RectangleF(x, y, width, height));
            return path;
        }

        var diameter = cap * 2f;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
