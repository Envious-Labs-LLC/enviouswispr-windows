using EnviousWispr.Presentation;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace EnviousWispr.App.Controls;

/// <summary>
/// Theme-reactive scrubbable vertical-bar waveform with playhead needle for inline History dictation playback.
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

    public static readonly DependencyProperty NeedleBrushProperty = DependencyProperty.Register(
        nameof(NeedleBrush),
        typeof(Brush),
        typeof(HistoryWaveformScrubber),
        new PropertyMetadata(null, OnVisualStatePropertyChanged));

    public static readonly DependencyProperty IsPlaybackActiveProperty = DependencyProperty.Register(
        nameof(IsPlaybackActive),
        typeof(bool),
        typeof(HistoryWaveformScrubber),
        new PropertyMetadata(false, OnVisualStatePropertyChanged));

    private readonly Border[] _bars = new Border[HistoryPlaybackPresentation.DefaultWaveformBarCount];
    private readonly Canvas _needleLayer;
    private readonly Border _playheadNeedle;
    private uint? _capturedPointerId;
    private bool _isPointerOver;

    public HistoryWaveformScrubber()
    {
        ColumnSpacing = 2;
        IsTabStop = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Center;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

        for (var i = 0; i < _bars.Length; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bar = new Border
            {
                MinWidth = 2,
                MaxWidth = 3,
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

        _playheadNeedle = new Border
        {
            Width = 2,
            Height = 20,
            CornerRadius = new CornerRadius(1),
            IsHitTestVisible = false,
            Opacity = 0.0,
        };
        _needleLayer = new Canvas
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _needleLayer.Children.Add(_playheadNeedle);
        SetColumn(_needleLayer, 0);
        SetColumnSpan(_needleLayer, _bars.Length);
        Children.Add(_needleLayer);

        SizeChanged += (_, _) => UpdateVisuals();
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
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

    public Brush? NeedleBrush
    {
        get => (Brush?)GetValue(NeedleBrushProperty);
        set => SetValue(NeedleBrushProperty, value);
    }

    public bool IsPlaybackActive
    {
        get => (bool)GetValue(IsPlaybackActiveProperty);
        set => SetValue(IsPlaybackActiveProperty, value);
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
        var unplayedOpacity = _isPointerOver || _capturedPointerId is not null ? 0.58 : 0.42;

        for (var i = 0; i < _bars.Length; i++)
        {
            var bar = _bars[i];
            bar.Height = heights[i];
            var isPlayed = fraction > 0 && i < playedCount;
            bar.Background = isPlayed ? playedBrush : unplayedBrush;
            bar.Opacity = isPlayed ? 1.0 : unplayedOpacity;
        }

        _playheadNeedle.Background = NeedleBrush ?? playedBrush;
        var showNeedle = IsPlaybackActive || fraction > 0.0 || _isPointerOver || _capturedPointerId is not null;
        _playheadNeedle.Opacity = showNeedle ? 1.0 : 0.0;

        var width = ActualWidth;
        var height = ActualHeight > 0 ? ActualHeight : 24.0;
        var needleHeight = _isPointerOver || _capturedPointerId is not null
            ? Math.Min(height, 22.0)
            : Math.Min(height, 20.0);
        _playheadNeedle.Height = needleHeight;

        if (width > 2.0)
        {
            Canvas.SetLeft(_playheadNeedle, fraction * (width - 2.0));
            Canvas.SetTop(_playheadNeedle, Math.Max(0.0, (height - needleHeight) / 2.0));
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        UpdateVisuals();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        UpdateVisuals();
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
        UpdateVisuals();
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
        UpdateVisuals();
        e.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        ReleaseCapture();
        UpdateVisuals();
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _capturedPointerId = null;
        UpdateVisuals();
    }

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
