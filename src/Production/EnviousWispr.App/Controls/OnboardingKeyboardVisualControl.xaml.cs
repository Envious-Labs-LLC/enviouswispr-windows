using EnviousWispr.Core.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EnviousWispr.App.Controls;

/// <summary>
/// Stylized Windows 11 bottom modifier row (Ctrl, Win, Alt, Space, Alt, Ctrl) highlighting
/// the keys matching the active dictation <see cref="HotkeyGesture"/> and live rehearsal presses.
/// </summary>
public sealed partial class OnboardingKeyboardVisualControl : UserControl
{
    public static readonly DependencyProperty HotkeyGestureTextProperty = DependencyProperty.Register(
        nameof(HotkeyGestureText),
        typeof(string),
        typeof(OnboardingKeyboardVisualControl),
        new PropertyMetadata("Ctrl+Win", OnVisualStateChanged));

    public static readonly DependencyProperty IsPressedProperty = DependencyProperty.Register(
        nameof(IsPressed),
        typeof(bool),
        typeof(OnboardingKeyboardVisualControl),
        new PropertyMetadata(false, OnVisualStateChanged));

    public static readonly DependencyProperty IsCapturingProperty = DependencyProperty.Register(
        nameof(IsCapturing),
        typeof(bool),
        typeof(OnboardingKeyboardVisualControl),
        new PropertyMetadata(false, OnVisualStateChanged));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion),
        typeof(bool),
        typeof(OnboardingKeyboardVisualControl),
        new PropertyMetadata(false, OnVisualStateChanged));

    public OnboardingKeyboardVisualControl()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateKeyVisuals();
        ActualThemeChanged += (_, _) => UpdateKeyVisuals();
    }

    public string HotkeyGestureText
    {
        get => (string)GetValue(HotkeyGestureTextProperty);
        set => SetValue(HotkeyGestureTextProperty, value);
    }

    public bool IsPressed
    {
        get => (bool)GetValue(IsPressedProperty);
        set => SetValue(IsPressedProperty, value);
    }

    public bool IsCapturing
    {
        get => (bool)GetValue(IsCapturingProperty);
        set => SetValue(IsCapturingProperty, value);
    }

    public bool ReduceMotion
    {
        get => (bool)GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    public void SetBinding(HotkeyGesture? gesture) =>
        HotkeyGestureText = gesture?.ToString() ?? string.Empty;

    private static void OnVisualStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OnboardingKeyboardVisualControl control)
        {
            control.UpdateKeyVisuals();
        }
    }

    private void UpdateKeyVisuals()
    {
        var parseResult = HotkeyGestureParser.Parse(HotkeyGestureText);
        var gesture = parseResult.Succeeded ? parseResult.Gesture : null;

        var leftCtrl = false;
        var win = false;
        var leftAlt = false;
        var space = false;
        var rightAlt = false;
        var rightCtrl = false;
        string? extraKey = null;

        if (gesture is { } bound)
        {
            if (bound.Modifiers.HasFlag(HotkeyModifiers.Control))
            {
                leftCtrl = true;
            }

            if (bound.Modifiers.HasFlag(HotkeyModifiers.Windows))
            {
                win = true;
            }

            if (bound.Modifiers.HasFlag(HotkeyModifiers.Alt))
            {
                leftAlt = true;
            }

            switch (bound.Key)
            {
                case "LeftCtrl":
                    leftCtrl = true;
                    break;
                case "RightCtrl":
                    rightCtrl = true;
                    break;
                case "LeftWin" or "RightWin":
                    win = true;
                    break;
                case "LeftAlt":
                    leftAlt = true;
                    break;
                case "RightAlt":
                    rightAlt = true;
                    break;
                case "Space":
                    space = true;
                    break;
                case { Length: > 0 } key:
                    extraKey = key;
                    break;
            }
        }

        var trayBorder = ThemeResourceLookup.Resolve<Brush>(
            this,
            IsCapturing ? "BrandAccentBrush" : "BrandDividerBrush");
        if (trayBorder is not null)
        {
            KeyboardTrayBorder.BorderBrush = trayBorder;
        }

        ApplyKeyStyle(LeftCtrlKey, LeftCtrlText, LeftCtrlTranslate, leftCtrl, isSpace: false);
        ApplyKeyStyle(WinKey, WinText, WinTranslate, win, isSpace: false);
        ApplyKeyStyle(LeftAltKey, LeftAltText, LeftAltTranslate, leftAlt, isSpace: false);
        ApplyKeyStyle(SpaceKey, SpaceText, SpaceTranslate, space, isSpace: true);
        ApplyKeyStyle(RightAltKey, RightAltText, RightAltTranslate, rightAlt, isSpace: false);
        ApplyKeyStyle(RightCtrlKey, RightCtrlText, RightCtrlTranslate, rightCtrl, isSpace: false);

        if (!string.IsNullOrWhiteSpace(extraKey))
        {
            ExtraKeyBadge.Visibility = Visibility.Visible;
            ExtraKeyText.Text = IsPressed ? $"{extraKey} pressed" : $"Key: {extraKey}";
            var chipStyle = ThemeResourceLookup.Resolve<Style>(
                this,
                IsPressed ? "BrandSolidAccentChipStyle" : "BrandAccentChipStyle");
            var chipTextStyle = ThemeResourceLookup.Resolve<Style>(
                this,
                IsPressed ? "BrandSolidAccentChipTextStyle" : "BrandAccentChipTextStyle");
            if (chipStyle is not null)
            {
                ExtraKeyBadge.Style = chipStyle;
            }

            if (chipTextStyle is not null)
            {
                ExtraKeyText.Style = chipTextStyle;
            }
        }
        else
        {
            ExtraKeyBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyKeyStyle(
        Border keyBorder,
        TextBlock keyText,
        TranslateTransform translate,
        bool isHighlighted,
        bool isSpace)
    {
        var pressedActive = isHighlighted && IsPressed;
        var bgKey = pressedActive
            ? "BrandAccentSolidBrush"
            : isHighlighted
                ? "BrandAccentLightBrush"
                : "BrandCardBgBrush";
        var borderKey = isHighlighted
            ? "BrandAccentBrush"
            : "BrandControlBorderBrush";
        var fgKey = pressedActive
            ? "BrandOnAccentBrush"
            : isHighlighted
                ? "BrandAccentBrush"
                : isSpace
                    ? "BrandTextSecondaryBrush"
                    : "BrandTextPrimaryBrush";

        if (ThemeResourceLookup.Resolve<Brush>(this, bgKey) is { } bg)
        {
            keyBorder.Background = bg;
        }

        if (ThemeResourceLookup.Resolve<Brush>(this, borderKey) is { } border)
        {
            keyBorder.BorderBrush = border;
        }

        if (ThemeResourceLookup.Resolve<Brush>(this, fgKey) is { } fg)
        {
            keyText.Foreground = fg;
        }

        keyBorder.BorderThickness = new Thickness(isHighlighted ? 2 : 1);
        translate.Y = pressedActive && !ReduceMotion ? 2.0 : 0.0;
    }
}
