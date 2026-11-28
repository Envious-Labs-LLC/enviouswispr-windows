using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace EnviousWispr.App.Controls;

/// <summary>
/// Flow-wrapping horizontal panel for chips and tags inside variable-width cards.
/// </summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing),
        typeof(double),
        typeof(WrapPanel),
        new PropertyMetadata(6.0, OnSpacingChanged));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing),
        typeof(double),
        typeof(WrapPanel),
        new PropertyMetadata(6.0, OnSpacingChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WrapPanel panel)
        {
            panel.InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width);
        var hSpacing = Math.Max(0, HorizontalSpacing);
        var vSpacing = Math.Max(0, VerticalSpacing);

        var lineWidth = 0.0;
        var lineHeight = 0.0;
        var totalWidth = 0.0;
        var totalHeight = 0.0;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            // Measure unconstrained first so shrink-to-fit children (including Grids with a star text column)
            // take their compact natural width and wrap side by side; only cap children wider than the row.
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (child.DesiredSize.Width > maxWidth && !double.IsInfinity(maxWidth))
            {
                child.Measure(new Size(maxWidth, double.PositiveInfinity));
            }

            var desired = child.DesiredSize;

            var neededWidth = lineWidth > 0 ? lineWidth + hSpacing + desired.Width : desired.Width;
            if (lineWidth > 0 && neededWidth > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + vSpacing;
                lineWidth = desired.Width;
                lineHeight = desired.Height;
            }
            else
            {
                lineWidth = neededWidth;
                lineHeight = Math.Max(lineHeight, desired.Height);
            }
        }

        totalWidth = Math.Max(totalWidth, lineWidth);
        totalHeight += lineHeight;
        return new Size(
            double.IsInfinity(availableSize.Width) ? totalWidth : Math.Min(totalWidth, availableSize.Width),
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var maxWidth = Math.Max(0, finalSize.Width);
        var hSpacing = Math.Max(0, HorizontalSpacing);
        var vSpacing = Math.Max(0, VerticalSpacing);

        var x = 0.0;
        var y = 0.0;
        var lineHeight = 0.0;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            var desired = child.DesiredSize;
            if (x > 0 && x + desired.Width > maxWidth)
            {
                x = 0;
                y += lineHeight + vSpacing;
                lineHeight = 0;
            }

            var arrangeWidth = Math.Min(desired.Width, Math.Max(0, maxWidth - x));
            child.Arrange(new Rect(x, y, arrangeWidth, desired.Height));
            x += arrangeWidth + hSpacing;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        return new Size(finalSize.Width, Math.Max(finalSize.Height, y + lineHeight));
    }
}
