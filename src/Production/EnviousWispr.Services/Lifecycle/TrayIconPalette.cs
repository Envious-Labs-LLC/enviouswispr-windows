using System.Drawing;

namespace EnviousWispr.Services.Lifecycle;

/// <summary>The colours one tray icon is drawn with.</summary>
/// <remarks>
/// A PARAMETER RATHER THAN A CONSTANT, BECAUSE HIGH CONTRAST AND TASKBAR THEME ARE NOT FIXED. A bitmap
/// handed to the notification area is drawn exactly as given: the shell does not recolour it, so a
/// fixed mid-grey mark can vanish on a dark taskbar or blend into a light overflow flyout.
/// Idle flips between bright monochrome on dark taskbars and deep monochrome on light taskbars,
/// while High Contrast collapses every semantic and spectrum colour to <see cref="SystemColors.WindowText"/>.
/// </remarks>
/// <param name="Idle">The mark when nothing is happening.</param>
/// <param name="Fault">The mark when something failed.</param>
/// <param name="Spectrum">The mark while recording, and the processing ring.</param>
public sealed record TrayIconPalette(Color Idle, Color Fault, Color[] Spectrum)
{
    private static readonly Color FaultRed = Color.FromArgb(0xE5, 0x48, 0x4D);

    private static readonly Color[] BrandSpectrum =
    [
        Color.FromArgb(0xFF, 0x2A, 0x40),
        Color.FromArgb(0xFF, 0x8C, 0x00),
        Color.FromArgb(0xFF, 0xD7, 0x00),
        Color.FromArgb(0xAD, 0xFF, 0x2F),
        Color.FromArgb(0x00, 0xFA, 0x9A),
        Color.FromArgb(0x00, 0xFF, 0xFF),
        Color.FromArgb(0x1E, 0x90, 0xFF),
        Color.FromArgb(0x41, 0x69, 0xE1),
        Color.FromArgb(0x8A, 0x2B, 0xE2),
    ];

    /// <summary>The brand palette tuned for dark Windows 11 taskbars: bright monochrome lips, crisp fault red, 9-colour spectrum.</summary>
    public static TrayIconPalette Brand { get; } = new(
        Color.FromArgb(0xF4, 0xF1, 0xFA),
        FaultRed,
        BrandSpectrum);

    /// <summary>The brand palette tuned for dark Windows 11 taskbars.</summary>
    public static TrayIconPalette DarkTaskbar => Brand;

    /// <summary>The brand palette tuned for light Windows 11 taskbars: deep monochrome lips.</summary>
    public static TrayIconPalette LightTaskbar { get; } = new(
        Color.FromArgb(0x1C, 0x18, 0x26),
        FaultRed,
        BrandSpectrum);

    /// <summary>Selects the taskbar-appropriate brand palette.</summary>
    public static TrayIconPalette ForTaskbar(bool systemUsesLightTheme) =>
        systemUsesLightTheme ? LightTaskbar : DarkTaskbar;

    /// <summary>One system colour for everything, which is what High Contrast is for.</summary>
    /// <remarks>
    /// EVERY STATE STILL DRAWS A DIFFERENT PICTURE, and that is why collapsing the colours is safe.
    /// Idle draws compact lips, Recording draws full-scale lips, Error draws faulted lips, and
    /// Processing draws the rotating ring - so every state keeps a distinct silhouette under High Contrast.
    /// </remarks>
    public static TrayIconPalette ForSystem(Color windowText) =>
        new(windowText, windowText, Enumerable.Repeat(windowText, BrandSpectrum.Length).ToArray());
}
