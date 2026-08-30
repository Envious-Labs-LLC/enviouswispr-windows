using System.Drawing;

namespace EnviousWispr.Services.Lifecycle;

/// <summary>The colours one tray icon is drawn with.</summary>
/// <remarks>
/// A PARAMETER RATHER THAN A CONSTANT, BECAUSE HIGH CONTRAST IS NOT A THEME WE CHOOSE. A bitmap
/// handed to the notification area is drawn exactly as given: the shell does not recolour it, so a
/// fixed grey mark on a High Contrast taskbar is whatever contrast those two colours happen to have.
/// The repository's design contract already says every spectrum and semantic colour collapses to the
/// system's window text colour under High Contrast; this is that rule reaching the one surface that
/// is not XAML and therefore could not inherit it.
/// </remarks>
/// <param name="Idle">The mark when nothing is happening.</param>
/// <param name="Fault">The mark when something failed.</param>
/// <param name="Spectrum">The mark while recording, and the processing ring.</param>
public sealed record TrayIconPalette(Color Idle, Color Fault, Color[] Spectrum)
{
    /// <summary>The brand palette: mid grey, the brand's error red, five spectrum colours.</summary>
    public static TrayIconPalette Brand { get; } = new(
        Color.FromArgb(0x80, 0x80, 0x80),
        Color.FromArgb(0xC0, 0x39, 0x2B),
        [
            Color.FromArgb(0xFF, 0x2A, 0x40),
            Color.FromArgb(0xFF, 0xD7, 0x00),
            Color.FromArgb(0x00, 0xFA, 0x9A),
            Color.FromArgb(0x1E, 0x90, 0xFF),
            Color.FromArgb(0x8A, 0x2B, 0xE2),
        ]);

    /// <summary>One system colour for everything, which is what High Contrast is for.</summary>
    /// <remarks>
    /// EVERY STATE STILL DRAWS A DIFFERENT PICTURE, and that is why collapsing the colours is safe.
    /// The bars differ from the ring by SHAPE, and the recording bars from the idle bars only by
    /// colour - so under High Contrast those two would become identical and the icon would stop
    /// saying anything. Recording therefore keeps a distinct picture by drawing its bars at full
    /// height rather than by tinting them, which is a shape difference and survives.
    /// </remarks>
    public static TrayIconPalette ForSystem(Color windowText) =>
        new(windowText, windowText, [windowText, windowText, windowText, windowText, windowText]);
}
