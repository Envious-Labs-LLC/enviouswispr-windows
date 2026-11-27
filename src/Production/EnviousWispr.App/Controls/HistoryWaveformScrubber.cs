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
        new PropertyMetadata(null, OnPeaksPropertyChanged));

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
    private readonly double[] _lastHeights = new double[HistoryPlaybackPresentation.DefaultWaveformBarCount];
    private readonly Canvas _needleLayer;
    private readonly Border _playheadNeedle;
    private uint? _capturedPointerId;
    private bool _isPointerOver;
    private bool _heightsInitialized;
    private bool _barsVisualsInitialized;
    private int _lastPlayedCount = -1;
    private Brush? _lastPlayedBrush;
    private Brush? _lastUnplayedBrush;
    private double _lastUnplayedOpacity = -1.0;
    private Brush? _lastNeedleBrush;
    private bool? _lastShowNeedle;
    private double _lastNeedleHeight = -1.0;
    private double _lastNeedleLeft = double.NaN;
    private double _lastNeedleTop = double.NaN;

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

        SizeChanged += (_, _) => UpdatePlaybackVisuals();
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerCanceled;
        PointerCaptureLost += OnPointerCaptureLost;
        KeyDown += OnKeyDown;
        UpdateBarHeights();
        UpdatePlaybackVisuals();
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

    private static void OnPeaksPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HistoryWaveformScrubber scrubber)
        {
            scrubber.UpdateBarHeights();
        }
    }

    private static void OnVisualStatePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HistoryWaveformScrubber scrubber)
        {
            scrubber.UpdatePlaybackVisuals();
        }
    }

    private void UpdateBarHeights()
    {
        var heights = HistoryPlaybackPresentation.ShapeBarHeights(
            Peaks,
            _bars.Length,
            minHeight: 4.0,
            maxHeight: 20.0);

        for (var i = 0; i < _bars.Length; i++)
        {
            var nextHeight = heights[i];
            if (!_heightsInitialized || Math.Abs(_lastHeights[i] - nextHeight) > 0.05)
            {
                _bars[i].Height = nextHeight;
                _lastHeights[i] = nextHeight;
            }
        }

        _heightsInitialized = true;
    }

    private void UpdateVisuals() => UpdatePlaybackVisuals();

    private void UpdatePlaybackVisuals()
    {
        var fraction = Math.Clamp(ProgressFraction, 0.0, 1.0);
        var playedCount = fraction > 0.0 ? (int)Math.Floor(fraction * _bars.Length) : 0;
        var playedBrush = PlayedBrush;
        var unplayedBrush = UnplayedBrush ?? playedBrush;
        var unplayedOpacity = _isPointerOver || _capturedPointerId is not null ? 0.58 : 0.42;

        var brushesOrOpacityChanged = !_barsVisualsInitialized
            || !ReferenceEquals(_lastPlayedBrush, playedBrush)
            || !ReferenceEquals(_lastUnplayedBrush, unplayedBrush)
            || Math.Abs(_lastUnplayedOpacity - unplayedOpacity) > 0.001;

        if (brushesOrOpacityChanged)
        {
            for (var i = 0; i < _bars.Length; i++)
            {
                var isPlayed = i < playedCount;
                _bars[i].Background = isPlayed ? playedBrush : unplayedBrush;
                _bars[i].Opacity = isPlayed ? 1.0 : unplayedOpacity;
            }

            _barsVisualsInitialized = true;
            _lastPlayedCount = playedCount;
            _lastPlayedBrush = playedBrush;
            _lastUnplayedBrush = unplayedBrush;
            _lastUnplayedOpacity = unplayedOpacity;
        }
        else if (_lastPlayedCount != playedCount)
        {
            var start = Math.Max(0, Math.Min(_lastPlayedCount, playedCount));
            var end = Math.Min(_bars.Length, Math.Max(_lastPlayedCount, playedCount));
            for (var i = start; i < end; i++)
            {
                var isPlayed = i < playedCount;
                _bars[i].Background = isPlayed ? playedBrush : unplayedBrush;
                _bars[i].Opacity = isPlayed ? 1.0 : unplayedOpacity;
            }

            _lastPlayedCount = playedCount;
        }

        var needleBrush = NeedleBrush ?? playedBrush;
        if (!ReferenceEquals(_lastNeedleBrush, needleBrush))
        {
            _playheadNeedle.Background = needleBrush;
            _lastNeedleBrush = needleBrush;
        }

        var showNeedle = IsPlaybackActive || fraction > 0.0 || _isPointerOver || _capturedPointerId is not null;
        if (_lastShowNeedle != showNeedle)
        {
            _playheadNeedle.Opacity = showNeedle ? 1.0 : 0.0;
            _lastShowNeedle = showNeedle;
        }

        var width = ActualWidth;
        var height = ActualHeight > 0 ? ActualHeight : 24.0;
        var needleHeight = _isPointerOver || _capturedPointerId is not null
            ? Math.Min(height, 22.0)
            : Math.Min(height, 20.0);
        if (Math.Abs(_lastNeedleHeight - needleHeight) > 0.05)
        {
            _playheadNeedle.Height = needleHeight;
            _lastNeedleHeight = needleHeight;
        }

        if (width > 2.0)
        {
            var left = fraction * (width - 2.0);
            var top = Math.Max(0.0, (height - needleHeight) / 2.0);
            if (double.IsNaN(_lastNeedleLeft) || Math.Abs(_lastNeedleLeft - left) >= 0.5)
            {
                Canvas.SetLeft(_playheadNeedle, left);
                _lastNeedleLeft = left;
            }

            if (double.IsNaN(_lastNeedleTop) || Math.Abs(_lastNeedleTop - top) >= 0.5)
            {
                Canvas.SetTop(_playheadNeedle, top);
                _lastNeedleTop = top;
            }
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
