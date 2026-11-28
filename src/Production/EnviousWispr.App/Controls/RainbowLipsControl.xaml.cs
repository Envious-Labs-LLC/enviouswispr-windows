using System.Diagnostics;
using EnviousWispr.Core.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EnviousWispr.App.Controls;

/// <summary>
/// 18-bar expressive EnviousWispr rainbow lips hero driven by <see cref="RainbowLipsMotion"/>.
/// Automatically stops rendering when unloaded, collapsed, in Reduce Motion, or bound to an external clock.
/// </summary>
public sealed partial class RainbowLipsControl : UserControl
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood),
        typeof(RainbowLipsMood),
        typeof(RainbowLipsControl),
        new PropertyMetadata(RainbowLipsMood.Idle, OnMoodPropertyChanged));

    public static readonly DependencyProperty VoiceLevelProperty = DependencyProperty.Register(
        nameof(VoiceLevel),
        typeof(double?),
        typeof(RainbowLipsControl),
        new PropertyMetadata(null, OnVisualInputChanged));

    public static readonly DependencyProperty ExternalClockSecondsProperty = DependencyProperty.Register(
        nameof(ExternalClockSeconds),
        typeof(double?),
        typeof(RainbowLipsControl),
        new PropertyMetadata(null, OnExternalClockChanged));

    public static readonly DependencyProperty ExternalMoodElapsedSecondsProperty = DependencyProperty.Register(
        nameof(ExternalMoodElapsedSeconds),
        typeof(double),
        typeof(RainbowLipsControl),
        new PropertyMetadata(0.0, OnExternalClockChanged));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion),
        typeof(bool),
        typeof(RainbowLipsControl),
        new PropertyMetadata(false, OnReduceMotionChanged));

    public static readonly DependencyProperty SpeakingBeatsProperty = DependencyProperty.Register(
        nameof(SpeakingBeats),
        typeof(int),
        typeof(RainbowLipsControl),
        new PropertyMetadata(2, OnVisualInputChanged));

    public static readonly DependencyProperty TapToBounceEnabledProperty = DependencyProperty.Register(
        nameof(TapToBounceEnabled),
        typeof(bool),
        typeof(RainbowLipsControl),
        new PropertyMetadata(true));

    private const double TapBouncePeakScale = 1.15;
    private const double TapBounceUpSeconds = 0.14;
    private const double TapBounceDownSeconds = 0.24;

    private readonly Border[] _bars;
    private readonly ScaleTransform[] _scales;
    private readonly Border[] _shimmers;
    private readonly RainbowLipsGlideMemory _glideMemory = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _moodStartSeconds;
    private double? _burstStartSeconds;
    private double? _tapBounceStartSeconds;
    private bool _isLoaded;
    private bool _isRenderingSubscribed;
    private long _visibilityCallbackToken;

    public RainbowLipsControl()
    {
        InitializeComponent();

        _bars =
        [
            UpperBar0, UpperBar1, UpperBar2, UpperBar3, UpperBar4, UpperBar5, UpperBar6, UpperBar7, UpperBar8,
            LowerBar0, LowerBar1, LowerBar2, LowerBar3, LowerBar4, LowerBar5, LowerBar6, LowerBar7, LowerBar8,
        ];
        _scales =
        [
            UpperScale0, UpperScale1, UpperScale2, UpperScale3, UpperScale4, UpperScale5, UpperScale6, UpperScale7, UpperScale8,
            LowerScale0, LowerScale1, LowerScale2, LowerScale3, LowerScale4, LowerScale5, LowerScale6, LowerScale7, LowerScale8,
        ];
        _shimmers =
        [
            UpperShimmer0, UpperShimmer1, UpperShimmer2, UpperShimmer3, UpperShimmer4, UpperShimmer5, UpperShimmer6, UpperShimmer7, UpperShimmer8,
            LowerShimmer0, LowerShimmer1, LowerShimmer2, LowerShimmer3, LowerShimmer4, LowerShimmer5, LowerShimmer6, LowerShimmer7, LowerShimmer8,
        ];

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    public RainbowLipsMood Mood
    {
        get => (RainbowLipsMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    public double? VoiceLevel
    {
        get => (double?)GetValue(VoiceLevelProperty);
        set => SetValue(VoiceLevelProperty, value);
    }

    /// <summary>Optional zero-allocation live mic RMS sampler (0..1); takes precedence over <see cref="VoiceLevel"/>.</summary>
    public Func<double>? VoiceLevelSource { get; set; }

    /// <summary>When set, stops the internal render loop and samples the lips at this external clock timestamp.</summary>
    public double? ExternalClockSeconds
    {
        get => (double?)GetValue(ExternalClockSecondsProperty);
        set => SetValue(ExternalClockSecondsProperty, value);
    }

    /// <summary>Mood-local elapsed seconds paired with <see cref="ExternalClockSeconds"/>.</summary>
    public double ExternalMoodElapsedSeconds
    {
        get => (double)GetValue(ExternalMoodElapsedSecondsProperty);
        set => SetValue(ExternalMoodElapsedSecondsProperty, value);
    }

    public bool ReduceMotion
    {
        get => (bool)GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    public int SpeakingBeats
    {
        get => (int)GetValue(SpeakingBeatsProperty);
        set => SetValue(SpeakingBeatsProperty, value);
    }

    public bool TapToBounceEnabled
    {
        get => (bool)GetValue(TapToBounceEnabledProperty);
        set => SetValue(TapToBounceEnabledProperty, value);
    }

    /// <summary>Plays a 0.8 s celebratory Happy bounce before returning smoothly to the current mood.</summary>
    public void TriggerBurst()
    {
        if (ReduceMotion || ExternalClockSeconds.HasValue)
        {
            return;
        }

        _burstStartSeconds = _clock.Elapsed.TotalSeconds;
        RenderCurrentFrame();
    }

    private static void OnMoodPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RainbowLipsControl control)
        {
            return;
        }

        var oldMood = (RainbowLipsMood)e.OldValue;
        var newMood = (RainbowLipsMood)e.NewValue;
        if (!(RainbowLipsMotion.IsModelMood(oldMood) && RainbowLipsMotion.IsModelMood(newMood)))
        {
            control._moodStartSeconds = control._clock.Elapsed.TotalSeconds;
        }

        control.RenderCurrentFrame();
    }

    private static void OnVisualInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RainbowLipsControl control && !control._isRenderingSubscribed)
        {
            control.RenderCurrentFrame();
        }
    }

    private static void OnExternalClockChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RainbowLipsControl control)
        {
            control.SyncRenderingSubscription();
            control.RenderCurrentFrame();
        }
    }

    private static void OnReduceMotionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RainbowLipsControl control)
        {
            if (control.ReduceMotion)
            {
                control._burstStartSeconds = null;
                control._tapBounceStartSeconds = null;
                control._glideMemory.Reset();
                control.TapScaleTransform.ScaleX = 1.0;
                control.TapScaleTransform.ScaleY = 1.0;
            }

            control.SyncRenderingSubscription();
            control.RenderCurrentFrame();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        _moodStartSeconds = _clock.Elapsed.TotalSeconds;
        if (_visibilityCallbackToken == 0)
        {
            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
        }

        RefreshThemeBrushes();
        SyncRenderingSubscription();
        RenderCurrentFrame();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        if (_visibilityCallbackToken != 0)
        {
            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }

        StopRendering();
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        SyncRenderingSubscription();
        if (Visibility == Visibility.Visible)
        {
            RenderCurrentFrame();
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        RefreshThemeBrushes();
        RenderCurrentFrame();
    }

    private void LipsRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!TapToBounceEnabled || ReduceMotion || ExternalClockSeconds.HasValue)
        {
            return;
        }

        _tapBounceStartSeconds = _clock.Elapsed.TotalSeconds;
        TriggerBurst();
    }

    private void SyncRenderingSubscription()
    {
        var shouldRun = _isLoaded
            && Visibility == Visibility.Visible
            && !ReduceMotion
            && !ExternalClockSeconds.HasValue;

        if (shouldRun)
        {
            StartRendering();
        }
        else
        {
            StopRendering();
        }
    }

    private void StartRendering()
    {
        if (_isRenderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering += OnCompositionTargetRendering;
        _isRenderingSubscribed = true;
    }

    private void StopRendering()
    {
        if (!_isRenderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= OnCompositionTargetRendering;
        _isRenderingSubscribed = false;
    }

    private void OnCompositionTargetRendering(object? sender, object e)
    {
        if (!_isLoaded || Visibility != Visibility.Visible || ReduceMotion || ExternalClockSeconds.HasValue)
        {
            StopRendering();
            return;
        }

        RenderCurrentFrame();
    }

    private void RefreshThemeBrushes()
    {
        var allBars = RainbowLipsMotion.AllBars;
        var shimmerBrush = ThemeResourceLookup.Resolve<Brush>(this, "BrandOnAccentBrush");
        for (var i = 0; i < _bars.Length; i++)
        {
            if (ThemeResourceLookup.Resolve<Brush>(this, allBars[i].BrushResourceKey) is { } barBrush)
            {
                _bars[i].Background = barBrush;
            }

            if (shimmerBrush is not null)
            {
                _shimmers[i].Background = shimmerBrush;
            }
        }

        if (ThemeResourceLookup.Resolve<Brush>(this, "BrandAccentBrush") is { } accentBrush)
        {
            GlowEllipse.Fill = accentBrush;
        }
    }

    private void RenderCurrentFrame()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var wallSeconds = ExternalClockSeconds ?? now;
        var moodElapsed = ExternalClockSeconds.HasValue
            ? ExternalMoodElapsedSeconds
            : Math.Max(0.0, now - _moodStartSeconds);

        double? sinceBurst = null;
        if (!ExternalClockSeconds.HasValue && _burstStartSeconds is { } burstStart)
        {
            var elapsedBurst = now - burstStart;
            if (elapsedBurst >= RainbowLipsMotion.BurstDurationSeconds)
            {
                _burstStartSeconds = null;
            }
            else if (elapsedBurst >= 0.0)
            {
                sinceBurst = elapsedBurst;
            }
        }

        var sampledLevel = VoiceLevelSource is not null ? VoiceLevelSource() : VoiceLevel;
        var frame = RainbowLipsMotion.EvaluateFrame(
            mood: Mood,
            wallSeconds: wallSeconds,
            sinceStateChangeSeconds: moodElapsed,
            sinceBurstSeconds: sinceBurst,
            voiceLevel: sampledLevel,
            speakingBeats: SpeakingBeats,
            reduceMotion: ReduceMotion,
            glideMemory: _glideMemory);

        var isHighContrast = ThemeResourceLookup.IsHighContrastActive();
        var globalOpacityDim = frame.GlobalModifiers.Saturation < 0.99 ? 0.58 : 1.0;

        for (var i = 0; i < _bars.Length; i++)
        {
            var spec = RainbowLipsMotion.AllBars[i];
            var shimmer = frame.Shimmers[i];
            _scales[i].ScaleY = frame.Scales[i];
            _bars[i].Opacity = Math.Clamp(spec.BaseOpacity * shimmer.Opacity * globalOpacityDim, 0.0, 1.0);
            _shimmers[i].Opacity = isHighContrast || ReduceMotion ? 0.0 : Math.Clamp(shimmer.Brightness * 0.35, 0.0, 0.35);
        }

        GlowEllipse.Opacity = isHighContrast || ReduceMotion
            ? 0.0
            : Math.Clamp(frame.Glow.Opacity * 0.28, 0.0, 0.25);

        UpdateTapBounce(now);
    }

    private void UpdateTapBounce(double nowSeconds)
    {
        if (ReduceMotion || ExternalClockSeconds.HasValue || _tapBounceStartSeconds is not { } start)
        {
            TapScaleTransform.ScaleX = 1.0;
            TapScaleTransform.ScaleY = 1.0;
            return;
        }

        var dt = nowSeconds - start;
        var total = TapBounceUpSeconds + TapBounceDownSeconds;
        if (dt < 0.0 || dt >= total)
        {
            _tapBounceStartSeconds = null;
            TapScaleTransform.ScaleX = 1.0;
            TapScaleTransform.ScaleY = 1.0;
            return;
        }

        double scale;
        if (dt < TapBounceUpSeconds)
        {
            var p = dt / TapBounceUpSeconds;
            var eased = 1.0 - Math.Pow(1.0 - p, 3.0);
            scale = 1.0 + (TapBouncePeakScale - 1.0) * eased;
        }
        else
        {
            var p = (dt - TapBounceUpSeconds) / TapBounceDownSeconds;
            var eased = p < 0.5 ? 4.0 * p * p * p : 1.0 - Math.Pow(-2.0 * p + 2.0, 3.0) / 2.0;
            scale = TapBouncePeakScale - (TapBouncePeakScale - 1.0) * eased;
        }

        TapScaleTransform.ScaleX = scale;
        TapScaleTransform.ScaleY = scale;
    }
}
