using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using EnviousWispr.Core.Presentation;

namespace EnviousWispr.Services.Lifecycle;

/// <summary>Resolved colours used to paint the native Windows 11 tray popup menu.</summary>
internal sealed record TrayMenuColors(
    TrayMenuThemeMode Mode,
    Color Background,
    Color Border,
    Color HoverBackground,
    Color HoverText,
    Color Separator,
    Color TextPrimary,
    Color TextSecondary,
    Color TextDisabled)
{
    public bool IsHighContrast => Mode == TrayMenuThemeMode.HighContrast;

    // Lavender-forward brand palette derived from DesignTokens.xaml / design-system.md.
    public static TrayMenuColors Dark { get; } = new(
        TrayMenuThemeMode.Dark,
        Background: Color.FromArgb(0x20, 0x1B, 0x2B),       // BrandCardBg Dark
        Border: Color.FromArgb(0x3B, 0x35, 0x4A),           // Subtle violet border
        HoverBackground: Color.FromArgb(0x35, 0x2D, 0x4C),  // BrandAccentLight over BrandCardBg
        HoverText: Color.FromArgb(0xEC, 0xE9, 0xF4),        // BrandTextPrimary Dark
        Separator: Color.FromArgb(0x35, 0x30, 0x42),        // BrandDivider Dark over BrandCardBg
        TextPrimary: Color.FromArgb(0xEC, 0xE9, 0xF4),      // BrandTextPrimary Dark
        TextSecondary: Color.FromArgb(0xAA, 0xA2, 0xBF),    // BrandTextSecondary Dark
        TextDisabled: Color.FromArgb(0x7A, 0x72, 0x90));    // BrandTextTertiary Dark

    public static TrayMenuColors Light { get; } = new(
        TrayMenuThemeMode.Light,
        Background: Color.FromArgb(0xF8, 0xF5, 0xFF),       // BrandPageBg Light
        Border: Color.FromArgb(0xE3, 0xDC, 0xEF),           // Subtle lavender border
        HoverBackground: Color.FromArgb(0xEC, 0xE4, 0xFD),  // BrandAccentLight over BrandPageBg
        HoverText: Color.FromArgb(0x0F, 0x0A, 0x1A),        // BrandTextPrimary Light
        Separator: Color.FromArgb(0xEE, 0xE6, 0xFC),        // BrandDivider Light over BrandPageBg
        TextPrimary: Color.FromArgb(0x0F, 0x0A, 0x1A),      // BrandTextPrimary Light
        TextSecondary: Color.FromArgb(0x4A, 0x3D, 0x60),    // BrandTextSecondary Light
        TextDisabled: Color.FromArgb(0x6B, 0x5E, 0x86));    // BrandTextTertiary Light

    public static TrayMenuColors For(TrayMenuThemeMode mode) => mode switch
    {
        TrayMenuThemeMode.Light => Light,
        TrayMenuThemeMode.HighContrast => new TrayMenuColors(
            TrayMenuThemeMode.HighContrast,
            Background: SystemColors.Menu,
            Border: SystemColors.WindowFrame,
            HoverBackground: SystemColors.Highlight,
            HoverText: SystemColors.HighlightText,
            Separator: SystemColors.GrayText,
            TextPrimary: SystemColors.MenuText,
            TextSecondary: SystemColors.GrayText,
            TextDisabled: SystemColors.GrayText),
        _ => Dark,
    };
}

/// <summary>Minimal <see cref="ProfessionalColorTable"/> keeping WinForms fallback paints aligned with <see cref="TrayMenuColors"/>.</summary>
internal sealed class FluentTrayColorTable(TrayMenuColors colors) : ProfessionalColorTable
{
    public override Color MenuBorder => colors.Border;
    public override Color MenuItemBorder => colors.HoverBackground;
    public override Color MenuItemSelected => colors.HoverBackground;
    public override Color MenuStripGradientBegin => colors.Background;
    public override Color MenuStripGradientEnd => colors.Background;
    public override Color ToolStripDropDownBackground => colors.Background;
    public override Color ImageMarginGradientBegin => colors.Background;
    public override Color ImageMarginGradientMiddle => colors.Background;
    public override Color ImageMarginGradientEnd => colors.Background;
    public override Color SeparatorDark => colors.Separator;
    public override Color SeparatorLight => colors.Separator;
    public override Color CheckBackground => Color.Transparent;
    public override Color CheckSelectedBackground => Color.Transparent;
    public override Color CheckPressedBackground => Color.Transparent;
}

/// <summary>Windows 11 Fluent dark/light renderer for <see cref="ContextMenuStrip"/> and its submenus.</summary>
internal sealed class FluentTrayMenuRenderer(TrayMenuColors colors)
    : ToolStripProfessionalRenderer(new FluentTrayColorTable(colors))
{
    private readonly TrayMenuColors _colors = colors;

    public TrayMenuColors Colors => _colors;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(_colors.Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(_colors.Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var bounds = new Rectangle(
            e.AffectedBounds.X,
            e.AffectedBounds.Y,
            Math.Max(0, e.AffectedBounds.Width - 1),
            Math.Max(0, e.AffectedBounds.Height - 1));
        using var pen = new Pen(_colors.Border, 1f);
        e.Graphics.DrawRectangle(pen, bounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not { Selected: true, Enabled: true })
        {
            return;
        }

        var bounds = new RectangleF(4f, 2f, Math.Max(0, e.Item.Width - 8), Math.Max(0, e.Item.Height - 4));
        if (bounds.Width <= 2 || bounds.Height <= 2)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedRect(bounds, 5f);
        using var brush = new SolidBrush(_colors.HoverBackground);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var isShortcut = e.Item is ToolStripMenuItem menuItem &&
            !string.IsNullOrEmpty(menuItem.ShortcutKeyDisplayString) &&
            string.Equals(e.Text, menuItem.ShortcutKeyDisplayString, StringComparison.Ordinal);

        if (e.Item.Selected && e.Item.Enabled && _colors.IsHighContrast)
        {
            e.TextColor = _colors.HoverText;
        }
        else if (!e.Item.Enabled)
        {
            e.TextColor = _colors.TextDisabled;
        }
        else if (isShortcut)
        {
            e.TextColor = _colors.TextSecondary;
        }
        else
        {
            e.TextColor = _colors.TextPrimary;
        }

        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        var left = 10;
        var right = Math.Max(left + 1, e.Item.Width - 10);
        using var pen = new Pen(_colors.Separator, 1f);
        e.Graphics.DrawLine(pen, left, y, right, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        var color = e.Item is { Selected: true, Enabled: true } && _colors.IsHighContrast
            ? _colors.HoverText
            : e.Item is { Enabled: false }
                ? _colors.TextDisabled
                : _colors.TextSecondary;

        var rect = e.ArrowRectangle;
        var cx = rect.Left + (rect.Width / 2f);
        var cy = rect.Top + (rect.Height / 2f);
        const float halfW = 2.2f;
        const float halfH = 4.0f;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        e.Graphics.DrawLines(pen,
        [
            new PointF(cx - halfW, cy - halfH),
            new PointF(cx + halfW, cy),
            new PointF(cx - halfW, cy + halfH),
        ]);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var color = e.Item is { Selected: true, Enabled: true } && _colors.IsHighContrast
            ? _colors.HoverText
            : _colors.TextPrimary;

        var rect = e.ImageRectangle;
        var cx = rect.Left + (rect.Width / 2f);
        var cy = rect.Top + (rect.Height / 2f);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        e.Graphics.DrawLines(pen,
        [
            new PointF(cx - 4.0f, cy + 0.2f),
            new PointF(cx - 1.2f, cy + 3.2f),
            new PointF(cx + 4.2f, cy - 2.8f),
        ]);
    }

    /// <summary>Applies Windows 11 rounded corners and dark/light DWM frame attributes when supported; fails open on older OS builds.</summary>
    internal static void ApplyWindowFrameAttributes(IntPtr hwnd, TrayMenuColors colors)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var roundPreference = DwmWindowCornerRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref roundPreference, sizeof(int));

            if (!colors.IsHighContrast)
            {
                var darkMode = colors.Mode == TrayMenuThemeMode.Dark ? 1 : 0;
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

                var borderRef = ToColorRef(colors.Border);
                _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderRef, sizeof(uint));
            }
        }
        catch
        {
            // Older Windows builds may not expose these DWM attributes; fail open gracefully.
        }
    }

    private static uint ToColorRef(Color color) =>
        (uint)(color.R | (color.G << 8) | (color.B << 16));

    private static GraphicsPath CreateRoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmWindowCornerRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref uint value,
        int size);
}
