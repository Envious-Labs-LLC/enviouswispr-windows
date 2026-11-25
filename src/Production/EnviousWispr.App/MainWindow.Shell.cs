using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EnviousWispr.App;

/// <summary>The window shell: the resizable sidebar, the section eyebrows and the pill page's link to Live Preview.</summary>
public sealed partial class MainWindow
{
    /// <summary>How far one arrow press moves the sidebar edge.</summary>
    private const double SidebarKeyboardStep = 16;

    private double? _sidebarStartingWidth;
    private bool _sidebarResizing;

    private static double LayoutDouble(string key) => (double)Application.Current.Resources[key];

    /// <summary>Shows every section heading in upper case, as macOS draws its pane eyebrows.</summary>
    /// <remarks>
    /// THE MARKUP KEEPS THE WORDS IN SENTENCE CASE AND THE WINDOW SHOWS THEM IN CAPITALS. WinUI text has no
    /// case transform, and writing the capitals into the markup would make every lookup and comparison of a
    /// heading guess its case; the one comparison there is (CollapseEyebrowThatRepeatsTheTitle) already
    /// ignores case. Runs once, after the sections exist.
    /// </remarks>
    private void UppercaseSectionEyebrows()
    {
        foreach (var section in SettingsSections().Concat(HelpSections()))
        {
            if (EyebrowOf(section) is { } eyebrow)
            {
                eyebrow.Text = eyebrow.Text.ToUpper(CultureInfo.CurrentUICulture);
            }
        }
    }

    private void SetSidebarWidth(double requested)
    {
        _sidebarStartingWidth ??= ProductNavigation.OpenPaneLength;
        var width = Math.Clamp(requested, LayoutDouble("BrandSidebarMinWidth"), LayoutDouble("BrandSidebarMaxWidth"));
        if (Math.Abs(ProductNavigation.OpenPaneLength - width) >= 0.5)
        {
            ProductNavigation.OpenPaneLength = width;
        }

        PlaceSidebarResizeHandle();
    }

    /// <summary>Centres the handle on the seam between the sidebar and the page.</summary>
    private void PlaceSidebarResizeHandle() =>
        SidebarResizeHandle.Margin = new Thickness(
            ProductNavigation.OpenPaneLength - (SidebarResizeHandle.Width / 2), 0, 0, 0);

    /// <summary>A finished resize moves the window's minimum width with the sidebar, as the shell arithmetic requires.</summary>
    private void FinishSidebarResize()
    {
        _sidebarResizing = false;
        SidebarResizeGrip.Opacity = 0;
        ConfigureMinimumWindowWidth();
    }

    private void SidebarResizeHandle_Loaded(object sender, RoutedEventArgs e)
    {
        _sidebarStartingWidth ??= ProductNavigation.OpenPaneLength;
        PlaceSidebarResizeHandle();
    }

    private void SidebarResizeHandle_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SidebarResizeGrip.Opacity = 1;

    private void SidebarResizeHandle_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_sidebarResizing)
        {
            SidebarResizeGrip.Opacity = 0;
        }
    }

    private void SidebarResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(SidebarResizeHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _sidebarResizing = SidebarResizeHandle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void SidebarResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_sidebarResizing)
        {
            return;
        }

        // The sidebar starts at the shell's left edge, so the pointer's position there IS the new width.
        SetSidebarWidth(e.GetCurrentPoint(ShellContentGrid).Position.X);
        e.Handled = true;
    }

    private void SidebarResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_sidebarResizing)
        {
            return;
        }

        SidebarResizeHandle.ReleasePointerCapture(e.Pointer);
        FinishSidebarResize();
        e.Handled = true;
    }

    private void SidebarResizeHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_sidebarResizing)
        {
            FinishSidebarResize();
        }
    }

    private void SidebarResizeHandle_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SetSidebarWidth(_sidebarStartingWidth ?? ProductNavigation.OpenPaneLength);
        ConfigureMinimumWindowWidth();
        e.Handled = true;
    }

    private void SidebarResizeHandle_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        double? target = e.Key switch
        {
            VirtualKey.Left => ProductNavigation.OpenPaneLength - SidebarKeyboardStep,
            VirtualKey.Right => ProductNavigation.OpenPaneLength + SidebarKeyboardStep,
            VirtualKey.Home => LayoutDouble("BrandSidebarMinWidth"),
            VirtualKey.End => LayoutDouble("BrandSidebarMaxWidth"),
            _ => null,
        };
        if (target is not { } width)
        {
            return;
        }

        SetSidebarWidth(width);
        ConfigureMinimumWindowWidth();
        e.Handled = true;
    }

    /// <summary>The pill page's "Configure Live Preview" link opens the Live Preview tab beside it.</summary>
    private void ConfigureLivePreviewLink_Click(object sender, RoutedEventArgs e) =>
        NavigateToDictationTab("live-preview");
}
