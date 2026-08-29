using System.Runtime.InteropServices;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace EnviousWispr.App;

public sealed partial class DictationOverlayWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;
    private const int NoticeWidth = 380;
    private const int NoticeHeight = 108;
    private const int WorkAreaMargin = 28;

    private readonly DispatcherTimer _hideTimer = new();
    private readonly DispatcherTimer _elapsedTimer = new();
    private readonly Storyboard _distressPulse = new();
    private OverlayPillPosition _position = OverlayPillPosition.Top;
    private bool _livePreviewEnabled;
    private RecordingPillDesign _withoutWordsDesign = RecordingPillDesign.Classic;
    private RecordingPillDesign _withWordsDesign = RecordingPillDesign.ReadingWell;
    private RecordingPillDesign _activeDesign = RecordingPillDesign.Classic;
    private DictationOverlayState _state = DictationOverlayState.Hidden;
    private DateTimeOffset _recordingStartedAt;
    private string? _previewText;
    private int _overlayWidth = NoticeWidth;
    private int _overlayHeight = NoticeHeight;
    private readonly double _rasterScale;

    public DictationOverlayWindow()
    {
        InitializeComponent();
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(windowHandle);
        _rasterScale = dpi > 0 ? dpi / 96d : 1d;
        Resize(NoticeWidth, NoticeHeight);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }

        _hideTimer.Tick += (_, _) => HideOverlay();
        _elapsedTimer.Interval = TimeSpan.FromMilliseconds(250);
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();

        // Distress and error share a red, so the breathing is what separates them. Opacity is a
        // composition property, so this animates off the UI thread and does not need
        // EnableDependentAnimation - which also means it cannot stutter while a dictation is being
        // transcribed on the same machine.
        var breath = new DoubleAnimation
        {
            From = 1,
            To = 0.35,
            Duration = new Duration(TimeSpan.FromMilliseconds(650)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(breath, SeverityWash);
        Storyboard.SetTargetProperty(breath, "Opacity");
        _distressPulse.Children.Add(breath);
    }

    /// <summary>Puts the recording pill in the same theme as the rest of the app.</summary>
    /// <remarks>
    /// THE PILL IS A SEPARATE TOP-LEVEL WINDOW, so setting the theme on the settings window never
    /// reached it. It followed the MACHINE instead - invisible while the two agree, and wrong the
    /// moment someone picks Light on a machine set to Dark.
    ///
    /// It is worse than a mismatched colour, because the settings window shows a PREVIEW of this
    /// pill and the preview does follow the app theme. So the preview was showing a pill that would
    /// never appear. A preview that lies is worse than no preview.
    ///
    /// Same class as the window's caption buttons: anything that is not inside the settings window's
    /// visual tree needs the theme handed to it deliberately.
    /// </remarks>
    public void ApplyTheme(ElementTheme theme) => OverlayRoot.RequestedTheme = theme;

    public void ShowState(DictationOverlayState state, string detail)
    {
        _hideTimer.Stop();
        if (state == DictationOverlayState.Hidden)
        {
            _state = state;
            HideOverlay();
            return;
        }

        var presentation = state switch
        {
            DictationOverlayState.Recording => ("Listening", "\uE720", "Release your key to finish · Escape cancels"),
            DictationOverlayState.Processing => ("Working locally", "\uE895", detail),
            DictationOverlayState.Success => ("Dictation complete", "\uE73E", detail),
            // THE ADVISORY HEADING NAMES THE MACHINE, NOT THE APP, and that is the whole point of
            // the severity: "Dictation stopped safely" over a sentence about Ollama being switched
            // off tells the user our software broke when their setup is simply incomplete.
            DictationOverlayState.Advisory => ("Setup needs attention", "\uE946", detail),
            DictationOverlayState.Warning => ("Your text is safe", "\uE7BA", detail),
            // Distress reuses the error glyph deliberately. It is the same bad news arriving
            // louder, and the pulse plus the deeper wash carry the difference. A codepoint chosen
            // for novelty is a hollow box on a machine whose font does not have it, and nothing in
            // this repository can see that.
            DictationOverlayState.Distress => ("Dictation interrupted", "\uEA39", detail),
            DictationOverlayState.Error => ("Dictation stopped safely", "\uEA39", detail),
            _ => ("EnviousWispr", "\uE720", detail),
        };
        if (state == DictationOverlayState.Recording)
        {
            if (_state != DictationOverlayState.Recording)
            {
                _recordingStartedAt = DateTimeOffset.UtcNow;
                ElapsedText.Text = "00:00";
            }

            _activeDesign = RecordingPillCatalog.Resolve(
                _livePreviewEnabled,
                _withoutWordsDesign,
                _withWordsDesign);
            ConfigureRecordingDesign();
            _elapsedTimer.Start();
        }
        else
        {
            _elapsedTimer.Stop();
            ConfigureNotice();
        }

        _state = state;
        ApplySeverity(state);
        StateTitle.Text = presentation.Item1;
        StateIcon.Glyph = presentation.Item2;
        StateDetail.Text = presentation.Item3;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            OverlayRoot,
            state == DictationOverlayState.Recording
                ? $"{RecordingPillCatalog.DisplayName(_activeDesign)} recording pill. {presentation.Item1}."
                : $"{presentation.Item1}. {presentation.Item3}");
        PositionOnForegroundMonitor();
        AppWindow.Show(activateWindow: false);

        if (state is DictationOverlayState.Success or DictationOverlayState.Advisory
            or DictationOverlayState.Warning or DictationOverlayState.Distress
            or DictationOverlayState.Error)
        {
            // AN ADVISORY DWELLS LONGEST BECAUSE IT ASKS THE USER TO DO SOMETHING. It names a
            // setting they have to go and change, which is more words than "your text is safe" and
            // more thought than a tick. macOS makes the same call and says so in its own source.
            _hideTimer.Interval = TimeSpan.FromSeconds(state switch
            {
                DictationOverlayState.Advisory => 6,
                DictationOverlayState.Error or DictationOverlayState.Distress => 5,
                _ => 3,
            });
            _hideTimer.Start();
        }
    }

    public void SetPreview(string? text)
    {
        _previewText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        ApplyPreviewInk();
        if (_state == DictationOverlayState.Recording &&
            _activeDesign == RecordingPillDesign.ReadingWell)
        {
            PreviewText.Text = _previewText ?? "Listening…";
            ResizeReadingWell();
        }
    }

    public void SetAudioLevel(float rootMeanSquare)
    {
        if (_state != DictationOverlayState.Recording ||
            _activeDesign != RecordingPillDesign.LevelRail)
        {
            return;
        }

        var normalized = Math.Clamp(MathF.Sqrt(Math.Max(0, rootMeanSquare) * 4f), 0f, 1f);
        for (var index = 0; index < LevelBars.Children.Count; index++)
        {
            if (LevelBars.Children[index] is not Border bar)
            {
                continue;
            }

            var wave = 0.45f + 0.55f * MathF.Abs(MathF.Sin(index * 1.7f + normalized * 5.1f));
            bar.Height = 5 + 20 * normalized * wave;
            bar.Opacity = 0.35 + 0.65 * normalized;
        }
    }

    public void ApplyPreferences(
        OverlayPillPosition position,
        bool livePreviewEnabled,
        RecordingPillDesign withoutWordsDesign,
        RecordingPillDesign withWordsDesign)
    {
        _position = position;
        _livePreviewEnabled = livePreviewEnabled;
        _withoutWordsDesign = withoutWordsDesign;
        _withWordsDesign = withWordsDesign;
        if (!livePreviewEnabled)
        {
            SetPreview(text: null);
        }
    }

    public void Shutdown()
    {
        _hideTimer.Stop();
        _elapsedTimer.Stop();
        Close();
    }

    private void ConfigureRecordingDesign()
    {
        StateTitle.Style = GetPillStyle("PillModeQuietTextStyle");
        StateIcon.Visibility = Visibility.Collapsed;
        StateDetail.Visibility = Visibility.Collapsed;
        ElapsedText.Visibility = Visibility.Visible;
        RainbowMark.Visibility = _activeDesign == RecordingPillDesign.Classic
            ? Visibility.Visible
            : Visibility.Collapsed;
        LevelBars.Visibility = _activeDesign == RecordingPillDesign.LevelRail
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewWell.Visibility = _activeDesign == RecordingPillDesign.ReadingWell
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewText.Text = _previewText ?? "Listening…";
        ApplyPreviewInk();
        OverlayRoot.CornerRadius = _activeDesign == RecordingPillDesign.ReadingWell
            ? new CornerRadius(18)
            : new CornerRadius(29);
        // On the CONTENT, not the frame: a Border lays its child out inside its padding, so a
        // frame that owns the padding insets the severity wash away from the capsule edge.
        ContentPanel.Margin = new Thickness(16, 12, 16, 12);

        switch (_activeDesign)
        {
            case RecordingPillDesign.Classic:
                Resize(185, 92);
                break;
            case RecordingPillDesign.LevelRail:
                Resize(288, 92);
                break;
            case RecordingPillDesign.ReadingWell:
                ResizeReadingWell();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    /// <summary>Takes the pill off screen and stops everything it left running.</summary>
    /// <remarks>
    /// BOTH HIDE PATHS GO THROUGH HERE, and they did not before. The dwell timer's tick stopped
    /// two timers; the Hidden state stopped one; neither stopped the distress pulse. So a pill that
    /// had breathed once went on breathing on an invisible window for as long as the app ran. It
    /// was invisible and it did not contaminate the next pill, because the next state stops the
    /// pulse before it draws - which is exactly why nothing would ever have reported it.
    ///
    /// One helper rather than three careful call sites, because the next thing added to this window
    /// that needs stopping will be added by somebody who reads one of them.
    /// </remarks>
    private void HideOverlay()
    {
        _hideTimer.Stop();
        _elapsedTimer.Stop();
        ApplyDistressPulse(pulsing: false);
        AppWindow.Hide();
    }

    /// <summary>Colours the pill for how bad the news is.</summary>
    /// <remarks>
    /// BEFORE THIS, EVERY OUTCOME DREW THE SAME CAPSULE. An error, a warning and a success shared
    /// one surface, one border and one ink, and were told apart only by a small glyph most people
    /// never look at directly. The app's in-window notifications had the identical hole one surface
    /// over, and it was found the same way: nobody chose it, there was simply one tint token and
    /// the severities with none of their own fell through to the neutral card.
    ///
    /// THE SET IS THE UNIT. Every state that can reach the pill is answered here, including the two
    /// that take the neutral pair, so a state added later cannot render as a plain pill and look
    /// deliberate. The wash and the ink are chosen together in one expression for the same reason.
    /// </remarks>
    private void ApplySeverity(DictationOverlayState state)
    {
        // STYLES RATHER THAN BRUSHES, and spelled out rather than composed. A brush read from the
        // application dictionary resolves against the MACHINE's theme, while this window follows
        // the APP's - the trap ApplyTheme exists to close, one property over. A style's setters
        // resolve against the element they land on, so they follow the pill.
        //
        // Every name is written in full because the gate that checks these styles exist reads the
        // source text; a name built by interpolation is not a name it can see, so it would be
        // checked by nothing.
        var (icon, edge, wash) = state switch
        {
            DictationOverlayState.Success =>
                ("PillSuccessIconStyle", "PillSuccessEdgeStyle", "PillSuccessWashStyle"),
            DictationOverlayState.Advisory =>
                ("PillAdvisoryIconStyle", "PillAdvisoryEdgeStyle", "PillAdvisoryWashStyle"),
            DictationOverlayState.Warning =>
                ("PillWarningIconStyle", "PillWarningEdgeStyle", "PillWarningWashStyle"),
            DictationOverlayState.Distress =>
                ("PillDistressIconStyle", "PillDistressEdgeStyle", "PillDistressWashStyle"),
            DictationOverlayState.Error =>
                ("PillErrorIconStyle", "PillErrorEdgeStyle", "PillErrorWashStyle"),
            // The three quiet states are LISTED rather than left to the default arm. A pill with
            // no severity is a choice about how it looks, and the gate that checks this set is
            // complete reads these names - an unlisted state would be covered by nothing and would
            // render as a plain capsule looking exactly as though somebody meant it.
            DictationOverlayState.Hidden or DictationOverlayState.Recording
                or DictationOverlayState.Processing =>
                ("PillNeutralIconStyle", "PillNeutralEdgeStyle", "PillNeutralWashStyle"),
            // An enum can hold a value nobody declared, so this arm has to exist. It takes the
            // neutral look rather than throwing, because a pill is not worth crashing a dictation.
            _ => ("PillNeutralIconStyle", "PillNeutralEdgeStyle", "PillNeutralWashStyle"),
        };

        StateIcon.Style = GetPillStyle(icon);
        OverlayRoot.Style = GetPillStyle(edge);
        SeverityWash.Style = GetPillStyle(wash);
        // The wash fills the pill, so its corners have to be the pill's corners. Reading them off
        // the root rather than restating the number is what stops a design change rounding one
        // layer and not the other.
        SeverityWash.CornerRadius = OverlayRoot.CornerRadius;
        ApplyDistressPulse(state == DictationOverlayState.Distress);
    }

    /// <summary>Breathes the wash while something outside the app is interrupting a dictation.</summary>
    /// <remarks>
    /// THE PULSE IS WHAT SEPARATES DISTRESS FROM ERROR, because they share a colour. Stopping it
    /// explicitly on every other state matters more than starting it: a storyboard left running
    /// would breathe under the next success pill, and the state that started it would be long gone.
    /// </remarks>
    private void ApplyDistressPulse(bool pulsing)
    {
        _distressPulse.Stop();
        SeverityWash.Opacity = 1;
        if (!pulsing)
        {
            return;
        }

        _distressPulse.Begin();
    }

    private void ConfigureNotice()
    {
        StateTitle.Style = GetPillStyle("PillNoticeTextStyle");
        RainbowMark.Visibility = Visibility.Collapsed;
        StateIcon.Visibility = Visibility.Visible;
        StateDetail.Visibility = Visibility.Visible;
        ElapsedText.Visibility = Visibility.Collapsed;
        LevelBars.Visibility = Visibility.Collapsed;
        PreviewWell.Visibility = Visibility.Collapsed;
        OverlayRoot.CornerRadius = new CornerRadius(18);
        ContentPanel.Margin = new Thickness(18, 14, 18, 14);
        Resize(NoticeWidth, NoticeHeight);
    }

    private void ApplyPreviewInk()
    {
        var styleKey = _previewText is null ? "PillDimmedTextStyle" : "PillLiveTextStyle";
        PreviewText.Style = GetPillStyle(styleKey);
    }

    private static Style GetPillStyle(string key) =>
        (Style)Application.Current.Resources[key];



    private void ResizeReadingWell()
    {
        var textLength = _previewText?.Length ?? 0;
        var estimatedLines = Math.Clamp((textLength + 43) / 44, 1, 5);
        Resize(400, 86 + estimatedLines * 22);
        if (_state == DictationOverlayState.Recording)
        {
            PositionOnForegroundMonitor();
        }
    }

    private void Resize(int width, int height)
    {
        _overlayWidth = Math.Max(1, (int)Math.Ceiling(width * _rasterScale));
        _overlayHeight = Math.Max(1, (int)Math.Ceiling(height * _rasterScale));
        AppWindow.Resize(new SizeInt32(_overlayWidth, _overlayHeight));
    }

    private void UpdateElapsed()
    {
        var elapsed = DateTimeOffset.UtcNow - _recordingStartedAt;
        var totalMinutes = Math.Clamp((int)elapsed.TotalMinutes, 0, 99);
        ElapsedText.Text = $"{totalMinutes:00}:{elapsed.Seconds:00}";
    }

    private void PositionOnForegroundMonitor()
    {
        var foreground = GetForegroundWindow();
        var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var workArea = new DisplayWorkArea(
            info.WorkArea.Left,
            info.WorkArea.Top,
            info.WorkArea.Right,
            info.WorkArea.Bottom);
        var physicalMargin = Math.Max(1, (int)Math.Ceiling(WorkAreaMargin * _rasterScale));
        var position = _position == OverlayPillPosition.Bottom
            ? OverlayPlacement.BottomCenter(workArea, _overlayWidth, _overlayHeight, physicalMargin)
            : OverlayPlacement.TopCenter(workArea, _overlayWidth, _overlayHeight, physicalMargin);
        AppWindow.Move(new PointInt32(position.X, position.Y));
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
