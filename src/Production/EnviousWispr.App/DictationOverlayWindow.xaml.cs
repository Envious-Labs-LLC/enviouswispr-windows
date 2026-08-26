using System.Runtime.InteropServices;
using EnviousWispr.Core.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
    private const int OverlayWidth = 380;
    private const int OverlayHeight = 108;
    private const int WorkAreaMargin = 28;

    private readonly DispatcherTimer _hideTimer = new();

    public DictationOverlayWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(OverlayWidth, OverlayHeight));
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
            AppWindow.Hide();
        };
    }

    public void ShowState(DictationOverlayState state, string detail)
    {
        _hideTimer.Stop();
        if (state == DictationOverlayState.Hidden)
        {
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
        StateTitle.Text = presentation.Item1;
        StateIcon.Glyph = presentation.Item2;
        StateDetail.Text = presentation.Item3;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            OverlayRoot,
            $"{presentation.Item1}. {presentation.Item3}");
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
        PreviewText.Text = text ?? string.Empty;
        PreviewText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Shutdown()
    {
        _hideTimer.Stop();
        Close();
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

        var position = OverlayPlacement.BottomCenter(
            new DisplayWorkArea(
                info.WorkArea.Left,
                info.WorkArea.Top,
                info.WorkArea.Right,
                info.WorkArea.Bottom),
            OverlayWidth,
            OverlayHeight,
            WorkAreaMargin);
        AppWindow.Move(new PointInt32(position.X, position.Y));
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

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
