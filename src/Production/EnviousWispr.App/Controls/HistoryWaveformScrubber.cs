using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace EnviousWispr.App.Controls;

/// <summary>
/// Theme-reactive scrubbable vertical-bar waveform for inline History dictation playback.
/// </summary>
public sealed class HistoryWaveformScrubber : Grid
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks),
        typeof(IReadOnlyList<float>),
        typeof(HistoryWaveformScrubber),
        new PropertyMetadata(null, OnVisualStatePropertyChanged));

    public static readonly DependencyProperty ProgressFractionProperty = DependencyProperty.Register(
        nameof(ProgressFraction),
        typeof(double),
        typeof(HistoryWaveformScrubber),
        new PropertyMetadata(0.0, OnVisualStatePropertyChanged));

    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(
        nameof(PlayedBrush),
        typeof(Brush),
        typeof(HistoryWaveformScrubber),
        new PropertyMetadata(null, OnVisualStatePropertyChanged));

    public static readonly DependencyProperty UnplayedBrushProperty = DependencyProperty.Register(
        nameof(UnplayedBrush),
        typeof(Brush),
        typeof(HistoryWaveformScrubber),
        new PropertyMetadata(null, OnVisualStatePropertyChanged));

    private readonly Border[] _bars = new Border[HistoryPlaybackPresentation.DefaultWaveformBarCount];
    private uint? _capturedPointerId;

    public HistoryWaveformScrubber()
    {
        ColumnSpacing = 2;
        IsTabStop = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Center;

        for (var i = 0; i < _bars.Length; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bar = new Border
            {
                MinWidth = 2,
                MaxWidth = 4,
                Height = 6,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            SetColumn(bar, i);
            Children.Add(bar);
            _bars[i] = bar;
        }

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerCanceled;
        PointerCaptureLost += OnPointerCaptureLost;
        KeyDown += OnKeyDown;
        UpdateVisuals();
    }

    public event Action<HistoryWaveformScrubber, double>? SeekRequested;

    public IReadOnlyList<float>? Peaks
    {
        get => (IReadOnlyList<float>?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public double ProgressFraction
    {
        get => (double)GetValue(ProgressFractionProperty);
        set => SetValue(ProgressFractionProperty, value);
    }

    public Brush? PlayedBrush
    {
        get => (Brush?)GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public Brush? UnplayedBrush
    {
        get => (Brush?)GetValue(UnplayedBrushProperty);
        set => SetValue(UnplayedBrushProperty, value);
    }

    private static void OnVisualStatePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HistoryWaveformScrubber scrubber)
        {
            scrubber.UpdateVisuals();
        }
    }

    private void UpdateVisuals()
    {
        var heights = HistoryPlaybackPresentation.ShapeBarHeights(
            Peaks,
            _bars.Length,
            minHeight: 4.0,
            maxHeight: 20.0);
        var fraction = Math.Clamp(ProgressFraction, 0.0, 1.0);
        var playedCount = (int)Math.Floor(fraction * _bars.Length);
        var playedBrush = PlayedBrush;
        var unplayedBrush = UnplayedBrush ?? playedBrush;

        for (var i = 0; i < _bars.Length; i++)
        {
            var bar = _bars[i];
            bar.Height = heights[i];
            var isPlayed = fraction > 0 && i < playedCount;
            bar.Background = isPlayed ? playedBrush : unplayedBrush;
            bar.Opacity = isPlayed ? 1.0 : 0.42;
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ActualWidth <= 0)
        {
            return;
        }

        _capturedPointerId = e.Pointer.PointerId;
        CapturePointer(e.Pointer);
        Focus(FocusState.Pointer);
        EmitSeekForPoint(e.GetCurrentPoint(this).Position.X);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointerId != e.Pointer.PointerId || ActualWidth <= 0)
        {
            return;
        }

        EmitSeekForPoint(e.GetCurrentPoint(this).Position.X);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointerId != e.Pointer.PointerId)
        {
            return;
        }

        EmitSeekForPoint(e.GetCurrentPoint(this).Position.X);
        ReleaseCapture();
        e.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e) => ReleaseCapture();

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => _capturedPointerId = null;

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var step = e.Key switch
        {
            VirtualKey.Left => -0.05,
            VirtualKey.Right => 0.05,
            VirtualKey.Home => -1.0,
            VirtualKey.End => 1.0,
            _ => 0.0,
        };
        if (step == 0.0)
        {
            return;
        }

        var next = Math.Clamp(ProgressFraction + step, 0.0, 1.0);
        SeekRequested?.Invoke(this, next);
        e.Handled = true;
    }

    private void EmitSeekForPoint(double x)
    {
        if (ActualWidth <= 0)
        {
            return;
        }

        var fraction = Math.Clamp(x / ActualWidth, 0.0, 1.0);
        SeekRequested?.Invoke(this, fraction);
    }

    private void ReleaseCapture()
    {
        _capturedPointerId = null;
        ReleasePointerCaptures();
    }
}
