using System.Runtime.InteropServices;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace EnviousWispr.App;

public enum DictationOverlayState
{
    Hidden,
    Recording,
    Processing,
    Success,
    Warning,
    Error,
}

public sealed partial class DictationOverlayWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;
    private const int NoticeWidth = 380;
    private const int NoticeHeight = 108;
    private const int WorkAreaMargin = 28;

    private readonly DispatcherTimer _hideTimer = new();
    private readonly DispatcherTimer _elapsedTimer = new();
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

        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            _elapsedTimer.Stop();
            AppWindow.Hide();
        };
        _elapsedTimer.Interval = TimeSpan.FromMilliseconds(250);
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();
    }

    public void ShowState(DictationOverlayState state, string detail)
    {
        _hideTimer.Stop();
        if (state == DictationOverlayState.Hidden)
        {
            _state = state;
            _elapsedTimer.Stop();
            AppWindow.Hide();
            return;
        }

        var presentation = state switch
        {
            DictationOverlayState.Recording => ("Listening", "\uE720", "Release your key to finish · Escape cancels"),
            DictationOverlayState.Processing => ("Working locally", "\uE895", detail),
            DictationOverlayState.Success => ("Dictation complete", "\uE73E", detail),
            DictationOverlayState.Warning => ("Your text is safe", "\uE7BA", detail),
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

        if (state is DictationOverlayState.Success or DictationOverlayState.Warning or DictationOverlayState.Error)
        {
            _hideTimer.Interval = TimeSpan.FromSeconds(state == DictationOverlayState.Error ? 5 : 3);
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
        OverlayRoot.Padding = new Thickness(16, 12, 16, 12);

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
        OverlayRoot.Padding = new Thickness(18, 14, 18, 14);
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
