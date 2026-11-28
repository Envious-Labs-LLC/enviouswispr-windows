using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EnviousWispr.App.Controls;

/// <summary>
/// Interactive Before/After AI Polish demonstration card ("Spoken" vs "Polished") for PolishIntro.
/// Auto-cycles every 4.0 seconds and pauses cleanly on user interaction, hover, unload, or Reduce Motion.
/// </summary>
public sealed partial class PolishBeforeAfterDemoControl : UserControl
{
    public static readonly DependencyProperty ShowPolishedProperty = DependencyProperty.Register(
        nameof(ShowPolished),
        typeof(bool),
        typeof(PolishBeforeAfterDemoControl),
        new PropertyMetadata(true, OnShowPolishedChanged));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion),
        typeof(bool),
        typeof(PolishBeforeAfterDemoControl),
        new PropertyMetadata(false, OnMotionConfigChanged));

    public static readonly DependencyProperty AutoCycleProperty = DependencyProperty.Register(
        nameof(AutoCycle),
        typeof(bool),
        typeof(PolishBeforeAfterDemoControl),
        new PropertyMetadata(true, OnMotionConfigChanged));

    private const double CrossfadeDurationSeconds = 0.26;
    private static readonly TimeSpan AutoCycleInterval = TimeSpan.FromSeconds(4.0);

    private readonly DispatcherTimer _cycleTimer = new() { Interval = AutoCycleInterval };
    private readonly Stopwatch _transitionClock = new();

    private double _blend = 1.0;
    private double _blendFrom = 1.0;
    private double _blendTarget = 1.0;
    private bool _isLoaded;
    private bool _isPointerOver;
    private bool _userInteracted;
    private bool _isTransitionSubscribed;
    private long _visibilityCallbackToken;

    public PolishBeforeAfterDemoControl()
    {
        InitializeComponent();
        _cycleTimer.Tick += OnCycleTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    public event EventHandler<bool>? ModeChanged;

    public bool ShowPolished
    {
        get => (bool)GetValue(ShowPolishedProperty);
        set => SetValue(ShowPolishedProperty, value);
    }

    public bool ReduceMotion
    {
        get => (bool)GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    public bool AutoCycle
    {
        get => (bool)GetValue(AutoCycleProperty);
        set => SetValue(AutoCycleProperty, value);
    }

    public void SetMode(bool polished, bool pauseAutoCycle = false)
    {
        if (pauseAutoCycle)
        {
            _userInteracted = true;
            SyncAutoCycleTimer();
        }

        if (ShowPolished != polished)
        {
            ShowPolished = polished;
        }
        else
        {
            UpdateControlsVisualState();
        }
    }

    private static void OnShowPolishedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PolishBeforeAfterDemoControl control)
        {
            return;
        }

        var polished = (bool)e.NewValue;
        control.UpdateControlsVisualState();
        control.StartCrossfadeTo(polished ? 1.0 : 0.0);
        control.ModeChanged?.Invoke(control, polished);
    }

    private static void OnMotionConfigChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PolishBeforeAfterDemoControl control)
        {
            if (control.ReduceMotion)
            {
                control.StopTransitionLoop();
                control._blend = control.ShowPolished ? 1.0 : 0.0;
                control.ApplyBlendVisuals(control._blend);
            }

            control.SyncAutoCycleTimer();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        if (_visibilityCallbackToken == 0)
        {
            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
        }

        _blend = ShowPolished ? 1.0 : 0.0;
        ApplyBlendVisuals(_blend);
        UpdateControlsVisualState();
        SyncAutoCycleTimer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        if (_visibilityCallbackToken != 0)
        {
            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }

        _cycleTimer.Stop();
        StopTransitionLoop();
    }

    private bool IsEffectivelyVisible()
    {
        for (DependencyObject? current = this; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element && element.Visibility != Visibility.Visible)
            {
                return false;
            }
        }

        return true;
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (!IsEffectivelyVisible())
        {
            _cycleTimer.Stop();
            StopTransitionLoop();
            return;
        }

        SyncAutoCycleTimer();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) =>
        UpdateControlsVisualState();

    private void SpokenToggleButton_Click(object sender, RoutedEventArgs e) =>
        SetMode(polished: false, pauseAutoCycle: true);

    private void PolishedToggleButton_Click(object sender, RoutedEventArgs e) =>
        SetMode(polished: true, pauseAutoCycle: true);

    private void DemoCardBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        SyncAutoCycleTimer();
    }

    private void DemoCardBorder_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        SyncAutoCycleTimer();
    }

    private void OnCycleTimerTick(object? sender, object e)
    {
        if (!_isLoaded || !IsEffectivelyVisible() || ReduceMotion || _isPointerOver || _userInteracted)
        {
            _cycleTimer.Stop();
            return;
        }

        ShowPolished = !ShowPolished;
    }

    private void SyncAutoCycleTimer()
    {
        var shouldRun = _isLoaded
            && IsEffectivelyVisible()
            && AutoCycle
            && !ReduceMotion
            && !_isPointerOver
            && !_userInteracted;

        if (shouldRun)
        {
            if (!_cycleTimer.IsEnabled)
            {
                _cycleTimer.Start();
            }
        }
        else if (_cycleTimer.IsEnabled)
        {
            _cycleTimer.Stop();
        }
    }

    private void StartCrossfadeTo(double target)
    {
        _blendTarget = target;
        if (ReduceMotion || !_isLoaded || !IsEffectivelyVisible())
        {
            StopTransitionLoop();
            _blend = target;
            ApplyBlendVisuals(_blend);
            return;
        }

        _blendFrom = _blend;
        _transitionClock.Restart();
        if (!_isTransitionSubscribed)
        {
            CompositionTarget.Rendering += OnTransitionRendering;
            _isTransitionSubscribed = true;
        }
    }

    private void StopTransitionLoop()
    {
        if (!_isTransitionSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= OnTransitionRendering;
        _isTransitionSubscribed = false;
        _transitionClock.Stop();
    }

    private void OnTransitionRendering(object? sender, object e)
    {
        var linear = Math.Clamp(_transitionClock.Elapsed.TotalSeconds / CrossfadeDurationSeconds, 0.0, 1.0);
        var eased = 1.0 - Math.Pow(1.0 - linear, 3.0);
        _blend = _blendFrom + (_blendTarget - _blendFrom) * eased;
        ApplyBlendVisuals(_blend);

        if (linear >= 1.0)
        {
            StopTransitionLoop();
        }
    }

    private void ApplyBlendVisuals(double blend)
    {
        var polishedOpacity = Math.Clamp(blend, 0.0, 1.0);
        var spokenOpacity = 1.0 - polishedOpacity;

        PolishedSamplePanel.Opacity = polishedOpacity;
        SpokenSamplePanel.Opacity = spokenOpacity;
        PolishedSamplePanel.IsHitTestVisible = polishedOpacity > 0.5;
        SpokenSamplePanel.IsHitTestVisible = spokenOpacity >= 0.5;

        if (ReduceMotion)
        {
            PolishedTranslate.Y = 0.0;
            SpokenTranslate.Y = 0.0;
        }
        else
        {
            PolishedTranslate.Y = (1.0 - polishedOpacity) * 6.0;
            SpokenTranslate.Y = (1.0 - spokenOpacity) * -6.0;
        }
    }

    private void UpdateControlsVisualState()
    {
        var polished = ShowPolished;

        var activeButtonStyle = ThemeResourceLookup.Resolve<Style>(this, "BrandSegmentedActiveButtonStyle");
        var idleButtonStyle = ThemeResourceLookup.Resolve<Style>(this, "BrandSegmentedButtonStyle");
        if (activeButtonStyle is not null && idleButtonStyle is not null)
        {
            PolishedToggleButton.Style = polished ? activeButtonStyle : idleButtonStyle;
            SpokenToggleButton.Style = polished ? idleButtonStyle : activeButtonStyle;
        }

        var badgeStyle = ThemeResourceLookup.Resolve<Style>(this, polished ? "BrandAccentChipStyle" : "BrandNeutralChipStyle");
        var badgeTextStyle = ThemeResourceLookup.Resolve<Style>(this, polished ? "BrandAccentChipTextStyle" : "BrandNeutralChipTextStyle");
        if (badgeStyle is not null)
        {
            ModeBadgeBorder.Style = badgeStyle;
        }

        if (badgeTextStyle is not null)
        {
            ModeBadgeText.Style = badgeTextStyle;
        }

        ModeBadgeText.Text = polished ? "AI polished" : "Raw speech";
        CaptionIcon.Glyph = polished ? "\uE73E" : "\uE720";
        if (ThemeResourceLookup.Resolve<Brush>(this, polished ? "BrandSuccessBrush" : "BrandTextSecondaryBrush") is { } iconBrush)
        {
            CaptionIcon.Foreground = iconBrush;
        }

        DemoCaptionText.Text = polished
            ? "Removed fillers, fixed a self-correction, and formatted the greeting and time."
            : "Natural spoken words with filler sounds and a mid-sentence correction.";

        if (ThemeResourceLookup.Resolve<Brush>(this, polished ? "BrandAccentBrush" : "BrandDividerBrush") is { } stageBorder)
        {
            StageSurfaceBorder.BorderBrush = stageBorder;
        }
    }
}
