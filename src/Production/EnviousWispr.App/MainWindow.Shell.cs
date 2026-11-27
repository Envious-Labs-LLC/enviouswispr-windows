using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace EnviousWispr.App;

/// <summary>The window shell: the resizable sidebar, the section eyebrows and the pill page's link to Live Preview.</summary>
public sealed partial class MainWindow
{
    /// <summary>How far one arrow press moves the sidebar edge.</summary>
    private const double SidebarKeyboardStep = 16;

    private double? _sidebarStartingWidth;
    private bool _sidebarResizing;

    internal static double LayoutDouble(string key) => (double)Application.Current.Resources[key];

    /// <summary>Shows every section heading in upper case, as macOS draws its pane eyebrows.</summary>
    /// <remarks>
    /// THE MARKUP KEEPS THE WORDS IN SENTENCE CASE AND THE WINDOW SHOWS THEM IN CAPITALS. WinUI text has no
    /// case transform, and writing the capitals into the markup would make every lookup and comparison of a
    /// heading guess its case; the one comparison there is (CollapseEyebrowThatRepeatsTheTitle) already
    /// ignores case. Runs once, after the sections exist.
    /// </remarks>
    private void UppercaseSectionEyebrows()
    {
        foreach (var section in SettingsSections())
        {
            if (EyebrowOf(section) is { } eyebrow)
            {
                eyebrow.Text = eyebrow.Text.ToUpper(CultureInfo.CurrentUICulture);
            }
        }
    }

    internal void SettingsTabStrip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Grid stripGrid)
        {
            ApplyResponsiveTabStripLayout(stripGrid, e.NewSize.Width);
        }
    }

    private static void ApplyResponsiveTabStripLayout(Grid stripGrid, double availableWidth = 0)
    {
        var width = availableWidth > 0 ? availableWidth : stripGrid.ActualWidth;
        var buttons = stripGrid.Children.OfType<Button>().ToArray();
        if (buttons.Length == 0)
        {
            return;
        }

        for (var i = stripGrid.Children.Count - 1; i >= 0; i--)
        {
            if (stripGrid.Children[i] is Border)
            {
                stripGrid.Children.RemoveAt(i);
            }
        }

        var wrapTwoRows = width > 0 && width < 680 && buttons.Length >= 4;
        stripGrid.ColumnDefinitions.Clear();
        stripGrid.RowDefinitions.Clear();

        var verticalSepStyle = Application.Current.Resources["BrandTopTabVerticalSeparatorStyle"] as Style;
        var horizontalSepStyle = Application.Current.Resources["BrandTopTabHorizontalSeparatorStyle"] as Style;

        if (!wrapTwoRows)
        {
            stripGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < buttons.Length; i++)
            {
                stripGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(buttons[i], 0);
                Grid.SetColumn(buttons[i], i);
                Grid.SetColumnSpan(buttons[i], 1);

                if (i < buttons.Length - 1)
                {
                    var sep = new Border { Style = verticalSepStyle };
                    Grid.SetRow(sep, 0);
                    Grid.SetColumn(sep, i);
                    stripGrid.Children.Add(sep);
                }
            }

            return;
        }

        stripGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stripGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (buttons.Length == 5)
        {
            for (var c = 0; c < 6; c++)
            {
                stripGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            for (var i = 0; i < 3; i++)
            {
                Grid.SetRow(buttons[i], 0);
                Grid.SetColumn(buttons[i], i * 2);
                Grid.SetColumnSpan(buttons[i], 2);
                if (i < 2)
                {
                    var sep = new Border { Style = verticalSepStyle };
                    Grid.SetRow(sep, 0);
                    Grid.SetColumn(sep, (i * 2) + 1);
                    stripGrid.Children.Add(sep);
                }
            }

            for (var i = 0; i < 2; i++)
            {
                var btn = buttons[3 + i];
                Grid.SetRow(btn, 1);
                Grid.SetColumn(btn, i * 3);
                Grid.SetColumnSpan(btn, 3);
                if (i == 0)
                {
                    var sep = new Border { Style = verticalSepStyle };
                    Grid.SetRow(sep, 1);
                    Grid.SetColumn(sep, 2);
                    stripGrid.Children.Add(sep);
                }
            }

            var rowDiv = new Border { Style = horizontalSepStyle };
            Grid.SetRow(rowDiv, 0);
            Grid.SetColumn(rowDiv, 0);
            Grid.SetColumnSpan(rowDiv, 6);
            stripGrid.Children.Add(rowDiv);
            return;
        }

        var cols = (buttons.Length + 1) / 2;
        for (var c = 0; c < cols; c++)
        {
            stripGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var i = 0; i < buttons.Length; i++)
        {
            var row = i / cols;
            var col = i % cols;
            Grid.SetRow(buttons[i], row);
            Grid.SetColumn(buttons[i], col);
            Grid.SetColumnSpan(buttons[i], 1);

            var isLastInRow = col == cols - 1 || i == buttons.Length - 1;
            if (!isLastInRow)
            {
                var sep = new Border { Style = verticalSepStyle };
                Grid.SetRow(sep, row);
                Grid.SetColumn(sep, col);
                stripGrid.Children.Add(sep);
            }
        }

        var divider = new Border { Style = horizontalSepStyle };
        Grid.SetRow(divider, 0);
        Grid.SetColumn(divider, 0);
        Grid.SetColumnSpan(divider, cols);
        stripGrid.Children.Add(divider);
    }

    private static void ApplyResponsiveThreeCardGridLayout(
        Grid? grid,
        double availableWidth,
        double stackThreshold,
        FrameworkElement?[] cards)
    {
        if (grid is null || cards.Any(c => c is null))
        {
            return;
        }

        var stackVertically = availableWidth > 0 && availableWidth < stackThreshold;
        grid.RowSpacing = stackVertically ? LayoutDouble("BrandSpacingM") : 0;
        var span = stackVertically ? cards.Length : 1;
        for (var i = 0; i < cards.Length; i++)
        {
            var card = cards[i]!;
            Grid.SetRow(card, stackVertically ? i : 0);
            Grid.SetColumn(card, stackVertically ? 0 : i);
            Grid.SetColumnSpan(card, span);
        }
    }

    private static void ApplyResponsiveTwoClusterRowLayout(
        Grid? grid,
        FrameworkElement? primary,
        FrameworkElement? secondary,
        double availableWidth,
        double stackThreshold)
    {
        if (grid is null || primary is null || secondary is null)
        {
            return;
        }

        var stack = availableWidth > 0
            && availableWidth < stackThreshold
            && primary.Visibility == Visibility.Visible;
        if (stack)
        {
            grid.RowSpacing = LayoutDouble("BrandSpacingS");
            Grid.SetRow(primary, 0);
            Grid.SetColumn(primary, 0);
            Grid.SetColumnSpan(primary, 2);
            Grid.SetRow(secondary, 1);
            Grid.SetColumn(secondary, 0);
            Grid.SetColumnSpan(secondary, 2);
            secondary.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            grid.RowSpacing = 0;
            Grid.SetRow(primary, 0);
            Grid.SetColumn(primary, 0);
            Grid.SetColumnSpan(primary, 1);
            Grid.SetRow(secondary, 0);
            Grid.SetColumn(secondary, 1);
            Grid.SetColumnSpan(secondary, 1);
            secondary.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private void HistoryHeaderBarGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveTwoClusterRowLayout(
            HistoryHeaderBarGrid,
            HistoryFilterButtonsPanel,
            HistoryStatusChipsPanel,
            e.NewSize.Width,
            stackThreshold: 540);

    internal void WordListHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveTwoClusterRowLayout(
            WordListHeaderGrid,
            WordCountText,
            WordListActionButtonsPanel,
            e.NewSize.Width,
            stackThreshold: 500);

    internal void SnippetsToolbarGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateSnippetsToolbarLayout(e.NewSize.Width);

    private void UpdateSnippetsToolbarLayout(double availableWidth) =>
        ApplyResponsiveTwoClusterRowLayout(
            SnippetsToolbarGrid,
            SnippetSearchContainer,
            SnippetsToolbarActionsPanel,
            availableWidth,
            stackThreshold: 520);

    private Brush ResolveWindowThemeBrush(string key) =>
        ThemeResourceLookup.Resolve<Brush>(WindowRoot, key)
        ?? (Brush)Application.Current.Resources[key];

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
    internal void ConfigureLivePreviewLink_Click(object sender, RoutedEventArgs e) =>
        NavigateToDictationTab("live-preview");
}
