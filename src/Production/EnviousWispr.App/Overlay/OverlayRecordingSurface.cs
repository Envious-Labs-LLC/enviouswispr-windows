using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EnviousWispr.App.Overlay;

/// <summary>
/// Manages hot-path recording visuals (Rainbow Lips, Level Rail, Reading Well meter and viewport)
/// with pre-allocated brushes, cached geometry, and delta-gated per-frame property writes.
/// </summary>
internal sealed class OverlayRecordingSurface
{
    public const int CapsuleHorizontalPadding = 14;
    public const double LevelRailBarsWidth = 118;
    public const int ReadingWellWidth = 400;
    public const int ReadingWellHeaderHeight = 36;
    private const double FrameBorderThickness = 1;
    private const double PreviewLineHeight = 21;
    private const int PreviewMaxLines = 5;
    private const double PreviewFadeFraction = 0.22;
    private const double HeightDeltaEpsilon = 0.05;
    private const float LevelDeltaEpsilon = 0.002f;

    private static readonly Thickness ReadingWellInsets = new(16, 12, 16, 15);
    private static readonly int[] LipsSpectrumColorIndices =
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 7, 6, 5, 4, 3, 2, 1, 0, 8];

    private readonly Canvas _lipsMark;
    private readonly StackPanel _levelBars;
    private readonly StackPanel _wellMeter;
    private readonly Border _previewWell;
    private readonly Canvas _previewViewport;
    private readonly TextBlock _previewText;
    private readonly Border _previewFade;
    private readonly Border _distressRedProbe;
    private readonly StackPanel _lipsSpectrumPalette;

    private readonly Border?[] _lipsBars;
    private readonly Border?[] _levelBarElements;
    private readonly Border?[] _wellMeterElements;
    private readonly SolidColorBrush?[] _levelBarBrushes;
    private readonly SolidColorBrush?[] _wellMeterBrushes;
    private readonly double[] _lastLevelBarHeights;
    private readonly double[] _lastWellMeterHeights;
    private readonly double[] _lipsLastTop;
    private readonly double[] _lipsLastHeight;
    private readonly RectangleGeometry _previewClipGeometry = new();
    private readonly Windows.UI.Color[] _sweepStops = new Windows.UI.Color[RecordingPillGeometry.SpectrumStops];

    private double _lipsLastSize = -1;
    private double _lastClipWidth = -1;
    private double _lastClipHeight = -1;
    private float _lastDrawnLevel = -1f;

    public OverlayRecordingSurface(
        Canvas lipsMark,
        StackPanel levelBars,
        StackPanel wellMeter,
        Border previewWell,
        Canvas previewViewport,
        TextBlock previewText,
        Border previewFade,
        Border distressRedProbe,
        StackPanel lipsSpectrumPalette)
    {
        _lipsMark = lipsMark;
        _levelBars = levelBars;
        _wellMeter = wellMeter;
        _previewWell = previewWell;
        _previewViewport = previewViewport;
        _previewText = previewText;
        _previewFade = previewFade;
        _distressRedProbe = distressRedProbe;
        _lipsSpectrumPalette = lipsSpectrumPalette;

        var lipsCount = Math.Min(_lipsMark.Children.Count, RecordingPillGeometry.LipsBarCount);
        _lipsBars = new Border?[lipsCount];
        _lipsLastTop = new double[lipsCount];
        _lipsLastHeight = new double[lipsCount];
        for (var i = 0; i < lipsCount; i++)
        {
            _lipsBars[i] = _lipsMark.Children[i] as Border;
            _lipsLastTop[i] = double.NaN;
            _lipsLastHeight[i] = double.NaN;
        }

        (_levelBarElements, _levelBarBrushes, _lastLevelBarHeights) = InitializeMeter(_levelBars);
        (_wellMeterElements, _wellMeterBrushes, _lastWellMeterHeights) = InitializeMeter(_wellMeter);

        _previewViewport.Clip = _previewClipGeometry;
        ApplySpectrumSweep();
    }

    public float LastDrawnLevel => Math.Max(0f, _lastDrawnLevel);

    public void DrawLips(float level, bool force = false)
    {
        var size = _lipsMark.Width;
        if (!force
            && Math.Abs(size - _lipsLastSize) < 0.01
            && _lastDrawnLevel >= 0f
            && Math.Abs(level - _lastDrawnLevel) < LevelDeltaEpsilon)
        {
            return;
        }

        var sizeChanged = Math.Abs(size - _lipsLastSize) >= 0.01;
        var corner = sizeChanged ? new CornerRadius(RecordingPillGeometry.LipsCornerRadius(size)) : default;
        _lipsLastSize = size;
        _lastDrawnLevel = level;

        for (var index = 0; index < _lipsBars.Length; index++)
        {
            if (_lipsBars[index] is not { } bar)
            {
                continue;
            }

            var rect = RecordingPillGeometry.LipsBar(index, level, size);
            if (sizeChanged)
            {
                Canvas.SetLeft(bar, rect.X);
                bar.Width = rect.Width;
                bar.CornerRadius = corner;
            }

            if (sizeChanged || double.IsNaN(_lipsLastTop[index]) || Math.Abs(rect.Y - _lipsLastTop[index]) >= HeightDeltaEpsilon)
            {
                Canvas.SetTop(bar, rect.Y);
                _lipsLastTop[index] = rect.Y;
            }

            if (sizeChanged || double.IsNaN(_lipsLastHeight[index]) || Math.Abs(rect.Height - _lipsLastHeight[index]) >= HeightDeltaEpsilon)
            {
                bar.Height = rect.Height;
                _lipsLastHeight[index] = rect.Height;
            }
        }
    }

    public void ApplyLipsColoring(bool distress)
    {
        var redBrush = _distressRedProbe.Background;
        for (var index = 0; index < _lipsBars.Length; index++)
        {
            if (_lipsBars[index] is not { } bar)
            {
                continue;
            }

            if (distress && redBrush is not null)
            {
                bar.Background = redBrush;
            }
            else
            {
                var paletteIndex = LipsSpectrumColorIndices[index];
                if (paletteIndex >= 0
                    && paletteIndex < _lipsSpectrumPalette.Children.Count
                    && _lipsSpectrumPalette.Children[paletteIndex] is Border swatch)
                {
                    bar.Background = swatch.Background;
                }
            }
        }
    }

    public void ApplySpectrumSweep()
    {
        for (var index = 0; index < _sweepStops.Length; index++)
        {
            if (index >= _lipsSpectrumPalette.Children.Count
                || _lipsSpectrumPalette.Children[index] is not Border { Background: SolidColorBrush brush })
            {
                return;
            }

            _sweepStops[index] = brush.Color;
        }

        UpdateSweepColors(_levelBarBrushes, _sweepStops);
        UpdateSweepColors(_wellMeterBrushes, _sweepStops);
    }

    public void DrawSampleLevelRailForPreview()
    {
        var stripHeight = _levelBars.Height;
        for (var index = 0; index < _levelBarElements.Length; index++)
        {
            if (_levelBarElements[index] is not { } bar)
            {
                continue;
            }

            var wave = (float)(0.25 + 0.55 * Math.Abs(Math.Sin(index * 0.45)));
            var nextHeight = RecordingPillGeometry.MeterBarHeight(wave, stripHeight);
            if (Math.Abs(nextHeight - _lastLevelBarHeights[index]) >= HeightDeltaEpsilon)
            {
                bar.Height = nextHeight;
                _lastLevelBarHeights[index] = nextHeight;
            }
        }
    }

    public void DrawLevelHistory(RecordingPillDesign activeDesign, RecordingLevelHistory history)
    {
        switch (activeDesign)
        {
            case RecordingPillDesign.LevelRail:
                DrawMeter(_levelBarElements, _lastLevelBarHeights, _levelBars.Height, history.Levels);
                break;
            case RecordingPillDesign.ReadingWell:
                DrawMeter(_wellMeterElements, _lastWellMeterHeights, _wellMeter.Height, history.Levels);
                break;
        }
    }

    public void ResetLevelVisuals(RecordingLevelHistory history)
    {
        DrawLips(0f, force: true);
        DrawMeter(_levelBarElements, _lastLevelBarHeights, _levelBars.Height, history.Levels);
        DrawMeter(_wellMeterElements, _lastWellMeterHeights, _wellMeter.Height, history.Levels);
    }

    public (int Width, int Height) ComputeReadingWellSize(string? previewText, int noticeExtraHeight)
    {
        if (previewText is null)
        {
            _previewWell.Visibility = Visibility.Collapsed;
            _previewFade.Visibility = Visibility.Collapsed;
            return (ReadingWellWidth, ReadingWellHeaderHeight + noticeExtraHeight);
        }

        _previewWell.Visibility = Visibility.Visible;
        var textWidth = ReadingWellWidth - FrameBorderThickness * 2 - ReadingWellInsets.Left - ReadingWellInsets.Right;
        _previewText.Width = textWidth;
        _previewText.Measure(new Windows.Foundation.Size(textWidth, double.PositiveInfinity));
        var natural = _previewText.DesiredSize.Height;
        if (natural <= 0)
        {
            var estimatedLines = Math.Max(1, (previewText.Length + 43) / 44);
            natural = estimatedLines * PreviewLineHeight;
        }

        var cap = PreviewLineHeight * PreviewMaxLines;
        var viewport = Math.Min(natural, cap);
        _previewViewport.Width = textWidth;
        _previewViewport.Height = viewport;

        if (Math.Abs(textWidth - _lastClipWidth) >= 0.05 || Math.Abs(viewport - _lastClipHeight) >= 0.05)
        {
            _previewClipGeometry.Rect = new Windows.Foundation.Rect(0, 0, textWidth, viewport);
            _lastClipWidth = textWidth;
            _lastClipHeight = viewport;
        }

        Canvas.SetTop(_previewText, viewport - natural);
        _previewFade.Width = textWidth;
        _previewFade.Height = viewport * PreviewFadeFraction;
        _previewFade.Visibility = natural > cap + 0.5 ? Visibility.Visible : Visibility.Collapsed;

        var totalHeight = (int)Math.Ceiling(
            ReadingWellHeaderHeight + 1 + ReadingWellInsets.Top + viewport + ReadingWellInsets.Bottom + noticeExtraHeight);
        return (ReadingWellWidth, totalHeight);
    }

    private static (Border?[] Elements, SolidColorBrush?[] Brushes, double[] LastHeights) InitializeMeter(StackPanel strip)
    {
        var count = strip.Children.Count;
        var elements = new Border?[count];
        var brushes = new SolidColorBrush?[count];
        var heights = new double[count];
        for (var index = 0; index < count; index++)
        {
            heights[index] = double.NaN;
            if (strip.Children[index] is Border bar)
            {
                elements[index] = bar;
                var initialColor = (bar.Background as SolidColorBrush)?.Color
                    ?? Windows.UI.Color.FromArgb(255, 255, 255, 255);
                var persistentBrush = new SolidColorBrush(initialColor);
                brushes[index] = persistentBrush;
                bar.Background = persistentBrush;
            }
        }

        return (elements, brushes, heights);
    }

    private static void UpdateSweepColors(SolidColorBrush?[] brushes, Windows.UI.Color[] stops)
    {
        var count = brushes.Length;
        for (var index = 0; index < count; index++)
        {
            if (brushes[index] is not { } brush)
            {
                continue;
            }

            var (lower, upper, fraction) = RecordingPillGeometry.SpectrumPosition(index, count);
            brush.Color = Blend(stops[lower], stops[upper], fraction);
        }
    }

    private static void DrawMeter(
        Border?[] bars,
        double[] lastHeights,
        double stripHeight,
        IReadOnlyList<float> levels)
    {
        for (var index = 0; index < bars.Length; index++)
        {
            if (bars[index] is not { } bar)
            {
                continue;
            }

            var sample = index < levels.Count ? levels[index] : 0f;
            var nextHeight = RecordingPillGeometry.MeterBarHeight(sample, stripHeight);
            if (double.IsNaN(lastHeights[index]) || Math.Abs(nextHeight - lastHeights[index]) >= HeightDeltaEpsilon)
            {
                bar.Height = nextHeight;
                lastHeights[index] = nextHeight;
            }
        }
    }

    private static Windows.UI.Color Blend(Windows.UI.Color from, Windows.UI.Color to, double amount)
    {
        static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
        return Microsoft.UI.ColorHelper.FromArgb(
            Mix(from.A, to.A, amount),
            Mix(from.R, to.R, amount),
            Mix(from.G, to.G, amount),
            Mix(from.B, to.B, amount));
    }
}
