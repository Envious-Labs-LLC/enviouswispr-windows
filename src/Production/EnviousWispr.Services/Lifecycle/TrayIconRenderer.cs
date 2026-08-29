using System.Drawing;
using System.Drawing.Drawing2D;
using EnviousWispr.Core.Presentation;

namespace EnviousWispr.Services.Lifecycle;

/// <summary>Draws the tray icon for one state, at one size.</summary>
/// <remarks>
/// DRAWN RATHER THAN SHIPPED AS FILES, because the tray asks for a different size depending on the
/// display's scale and how the user has set their taskbar, and a set of pre-rendered .ico files is a
/// set that is wrong at some scale nobody tested on. macOS renders its menu bar icon the same way and
/// for the same reason.
///
/// FIVE BARS, NOT THE MARK'S EIGHTEEN. The brand mark is two rows of nine bars, which at sixteen
/// pixels is a smudge - roughly one pixel per bar with no gap. Five bars in one row keep the mark's
/// silhouette, its centre peak, and its spectrum, and survive being drawn a sixth the size.
///
/// EVERY STATE HAS ITS OWN SHAPE, NOT JUST ITS OWN COLOUR, AND THAT IS LOAD-BEARING. macOS tells idle
/// from recording by colour alone, which it can afford because the menu bar tints its icons for the
/// user's appearance. The Windows notification area does not: a bitmap is drawn exactly as handed
/// over. So under High Contrast, where every colour collapses to one system colour, a colour-only
/// distinction would leave three of the four states drawing the identical picture, and the icon
/// would stop saying anything at the moment it matters most.
/// </remarks>
public static class TrayIconRenderer
{
    /// <summary>The mark's own silhouette, sampled down to five bars.</summary>
    /// <remarks>
    /// TAKEN FROM `EnviousWisprMark.svg` RATHER THAN INVENTED. Its top row runs 20, 32, 48, 36, 24,
    /// 36, 48, 32, 20, which is a centre dip between two peaks; read across both rows the mark's
    /// silhouette rises to the middle. These are that shape at five bars, as a fraction of the
    /// icon's height.
    /// </remarks>
    private static readonly double[] Speaking = [0.42, 0.68, 0.92, 0.68, 0.42];

    /// <summary>The same mark, quieter. Waiting rather than listening.</summary>
    private static readonly double[] Waiting = [0.26, 0.42, 0.57, 0.42, 0.26];

    /// <summary>A wave with its middle flattened. The shape of something having gone wrong.</summary>
    private static readonly double[] Broken = [0.88, 0.22, 0.22, 0.22, 0.88];

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
        var colours = palette ?? TrayIconPalette.Brand;
        var bitmap = new Bitmap(size, size);
        using var canvas = Graphics.FromImage(bitmap);
        canvas.SmoothingMode = SmoothingMode.AntiAlias;
        canvas.Clear(Color.Transparent);

        if (state == TrayIconState.Processing)
        {
            DrawSweep(canvas, size, colours, rotationDegrees);
            return bitmap;
        }

        DrawBars(canvas, size, state, colours);
        return bitmap;
    }

    private static void DrawBars(
        Graphics canvas, int size, TrayIconState state, TrayIconPalette colours)
    {
        var heights = state switch
        {
            TrayIconState.Recording => Speaking,
            TrayIconState.Error => Broken,
            _ => Waiting,
        };

        // Five bars and four gaps across the width. Widths are derived rather than chosen: a bar is
        // twice a gap, which is the mark's own 14-wide bar against its 10-wide gap, rounded.
        var unit = size / 15.0;
        var barWidth = Math.Max(1.0, unit * 2);
        var gap = unit;
        var totalWidth = (barWidth * heights.Length) + (gap * (heights.Length - 1));
        var left = (size - totalWidth) / 2;
        var radius = (float)Math.Max(1, barWidth / 2);

        for (var index = 0; index < heights.Length; index++)
        {
            var height = Math.Max(1.0, size * heights[index]);
            var x = (float)(left + (index * (barWidth + gap)));
            var y = (float)((size - height) / 2);
            var colour = state switch
            {
                TrayIconState.Recording => colours.Spectrum[index % colours.Spectrum.Length],
                TrayIconState.Error => colours.Fault,
                _ => colours.Idle,
            };
            using var brush = new SolidBrush(colour);
            using var bar = RoundedBar(x, y, (float)barWidth, (float)height, radius);
            canvas.FillPath(brush, bar);
        }
    }

    /// <summary>The rotating spectrum sweep that says work is under way.</summary>
    /// <remarks>
    /// A RING RATHER THAN THE BARS, because a bar chart that spins is not a thing, and the one idea
    /// this state has to carry is "wait". macOS uses a rotating spectrum wheel here for the same
    /// reason. Each arc is drawn separately rather than through a gradient brush: a sweep gradient
    /// at sixteen pixels bands visibly, and nine flat arcs read as a smooth ring at that size.
    /// </remarks>
    private static void DrawSweep(
        Graphics canvas, int size, TrayIconPalette colours, double rotationDegrees)
    {
        var thickness = Math.Max(1.5f, size / 7f);
        var inset = thickness / 2f;
        var box = new RectangleF(inset, inset, size - thickness, size - thickness);
        const float sweep = 360f / 9f;
        for (var index = 0; index < 9; index++)
        {
            var colour = colours.Spectrum[index % colours.Spectrum.Length];
            // The tail fades so the ring reads as TRAVELLING rather than merely present, and that
            // fade is also what keeps it a ring rather than a plain circle when every colour in it
            // is the same system colour.
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
        var cap = Math.Min(radius, Math.Min(width, height) / 2);
        if (cap <= 0.5f)
        {
            path.AddRectangle(new RectangleF(x, y, width, height));
            return path;
        }

        var diameter = cap * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
