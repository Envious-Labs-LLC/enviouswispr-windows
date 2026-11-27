using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace EnviousWispr.App.Overlay;

/// <summary>
/// Owns capsule surface styles, severity badges, and the three decorative overlay storyboards
/// (distress pulse, spectrum wheel rotation, and breathing rainbow hairline).
/// </summary>
internal sealed class OverlayChromeAnimator
{
    private readonly Border _overlayRoot;
    private readonly Border _frameBorder;
    private readonly Border _severityWash;
    private readonly Border _bottomRainbowHairline;
    private readonly Border _bottomDistressHairline;
    private readonly FrameworkElement _lipsMark;
    private readonly CompositeTransform _spectrumWheelTransform;
    private readonly Func<bool> _getAnimationsEnabled;

    private readonly Storyboard _distressPulse = new();
    private readonly Storyboard _spectrumWheelPulse = new();
    private readonly Storyboard _hairlineBreath = new();

    public OverlayChromeAnimator(
        Border overlayRoot,
        Border frameBorder,
        Border severityWash,
        Border bottomRainbowHairline,
        Border bottomDistressHairline,
        FrameworkElement lipsMark,
        CompositeTransform spectrumWheelTransform,
        Func<bool> getAnimationsEnabled)
    {
        _overlayRoot = overlayRoot;
        _frameBorder = frameBorder;
        _severityWash = severityWash;
        _bottomRainbowHairline = bottomRainbowHairline;
        _bottomDistressHairline = bottomDistressHairline;
        _lipsMark = lipsMark;
        _spectrumWheelTransform = spectrumWheelTransform;
        _getAnimationsEnabled = getAnimationsEnabled;

        InitializeStoryboards();
    }

    public static Style GetPillStyle(string key) => (Style)Application.Current.Resources[key];

    public void ApplySurface(string surfaceStyle, double radius)
    {
        _overlayRoot.Style = GetPillStyle(surfaceStyle);
        _overlayRoot.CornerRadius = new CornerRadius(0);
        var innerCorners = new CornerRadius(radius);
        _frameBorder.CornerRadius = innerCorners;
        _severityWash.CornerRadius = innerCorners;
    }

    public void ApplySeverity(
        DictationOverlayState state,
        RecordingPillDesign activeDesign,
        FontIcon stateIcon)
    {
        var look = OverlayPillCatalog.ResolveSeverity(state);
        stateIcon.Style = GetPillStyle(look.IconStyleKey);
        _frameBorder.Style = GetPillStyle(OverlayPillCatalog.ResolveFrameEdgeStyle(state, activeDesign));
        _severityWash.Style = state == DictationOverlayState.CorrectionLearned
            ? GetPillStyle(look.WashStyleKey)
            : GetPillStyle("PillNeutralWashStyle");
        _severityWash.CornerRadius = _frameBorder.CornerRadius;
    }

    public void ApplyHairline(bool visible, bool breathes, bool distress)
    {
        _hairlineBreath.Stop();
        _distressPulse.Stop();
        _lipsMark.Opacity = 1;
        _bottomDistressHairline.Visibility = distress ? Visibility.Visible : Visibility.Collapsed;
        _bottomRainbowHairline.Opacity = RecordingPillGeometry.HairlineSteadyOpacity;
        _bottomRainbowHairline.Visibility = visible && !distress ? Visibility.Visible : Visibility.Collapsed;
        if (distress)
        {
            _bottomDistressHairline.Opacity = 0.6;
            if (_getAnimationsEnabled())
            {
                _distressPulse.Begin();
            }

            return;
        }

        if (visible && breathes && _getAnimationsEnabled())
        {
            _hairlineBreath.Begin();
        }
    }

    public void SetSpectrumWheelSpinning(bool spinning)
    {
        if (spinning && _getAnimationsEnabled())
        {
            _spectrumWheelPulse.Begin();
            return;
        }

        _spectrumWheelPulse.Stop();
        _spectrumWheelTransform.Rotation = 0;
    }

    public void StopAll()
    {
        _distressPulse.Stop();
        _hairlineBreath.Stop();
        _spectrumWheelPulse.Stop();
        _spectrumWheelTransform.Rotation = 0;
    }

    private void InitializeStoryboards()
    {
        var distressHairlinePulse = new DoubleAnimation
        {
            From = 0.6,
            To = 0.3,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(distressHairlinePulse, _bottomDistressHairline);
        Storyboard.SetTargetProperty(distressHairlinePulse, "Opacity");
        _distressPulse.Children.Add(distressHairlinePulse);

        var distressLipsPulse = new DoubleAnimation
        {
            From = 1.0,
            To = 0.45,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(distressLipsPulse, _lipsMark);
        Storyboard.SetTargetProperty(distressLipsPulse, "Opacity");
        _distressPulse.Children.Add(distressLipsPulse);

        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(8)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(spin, _spectrumWheelTransform);
        Storyboard.SetTargetProperty(spin, "Rotation");
        _spectrumWheelPulse.Children.Add(spin);

        var glow = new DoubleAnimation
        {
            From = RecordingPillGeometry.HairlineDimOpacity,
            To = RecordingPillGeometry.HairlineBrightOpacity,
            Duration = new Duration(TimeSpan.FromSeconds(2)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(glow, _bottomRainbowHairline);
        Storyboard.SetTargetProperty(glow, "Opacity");
        _hairlineBreath.Children.Add(glow);
    }
}
