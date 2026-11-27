using System.Runtime.InteropServices;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Lifecycle;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace EnviousWispr.App.Overlay;

/// <summary>
/// Owns Win32 window styling, DWM border removal, DPI scaling, GDI rounded-region clipping,
/// pointer drag positioning, and cached system animation preference for <see cref="DictationOverlayWindow"/>.
/// </summary>
internal sealed class OverlayWin32Host : IDisposable
{
    internal const int WindowBleed = 2;
    private const int WorkAreaMargin = 28;
    private const uint MonitorDefaultToNearest = 2;

    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmSettingChange = 0x001A;
    private const uint WmNcDestroy = 0x0082;
    private const nuint OverlaySubclassId = 0x45573335;

    private const uint SpiGetClientAreaAnimation = 0x1042;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const int DwmwaBorderColor = 34;

    private delegate nint SubclassProc(
        nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    private readonly AppWindow _appWindow;
    private readonly nint _windowHandle;
    private SubclassProc? _subclassProc;
    private nint _subclassedHandle;
    private bool? _animationsEnabledCache;

    private double _rasterScale;
    private int _logicalWidth;
    private int _logicalHeight;
    private int _overlayWidth;
    private int _overlayHeight;
    private double _lastClipCornerRadius = -1;
    private double _lastClipScale = -1;
    private bool _hasClipped;

    private uint? _dragPointer;
    private PointInt32 _dragFromWindow;
    private NativePoint _dragFromCursor;
    private bool _disposed;

    public OverlayWin32Host(Window window, AppWindow appWindow)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(appWindow);
        _appWindow = appWindow;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);

        var dpi = GetDpiForWindow(_windowHandle);
        _rasterScale = dpi > 0 ? dpi / 96d : 1d;

        _ = WindowsToolWindowStyle.HideFromTaskSwitchers(
            () => _appWindow.IsShownInSwitchers = false,
            _windowHandle);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }

        var borderColour = DwmColorNone;
        _ = DwmSetWindowAttribute(_windowHandle, DwmwaBorderColor, ref borderColour, sizeof(uint));
        InstallMinTrackOverride();
    }

    public int LogicalWidth => _logicalWidth;

    public int LogicalHeight => _logicalHeight;

    public double RasterScale => _rasterScale;

    public bool DraggedThisPresentation { get; private set; }

    public bool AnimationsEnabled => _animationsEnabledCache ??= QueryAnimationsEnabled();

    public void InvalidateAnimationsCache() => _animationsEnabledCache = null;

    public void ResetDrag()
    {
        DraggedThisPresentation = false;
        _dragPointer = null;
    }

    public void Resize(int width, int height, double frameCornerRadius)
    {
        var scaleChanged = Math.Abs(_rasterScale - _lastClipScale) >= 0.001;
        var sizeChanged = width != _logicalWidth || height != _logicalHeight;
        var radiusChanged = Math.Abs(frameCornerRadius - _lastClipCornerRadius) >= 0.01;
        if (_hasClipped && !sizeChanged && !scaleChanged && !radiusChanged)
        {
            return;
        }

        _logicalWidth = width;
        _logicalHeight = height;
        _overlayWidth = Math.Max(1, (int)Math.Ceiling((width + WindowBleed * 2) * _rasterScale));
        _overlayHeight = Math.Max(1, (int)Math.Ceiling((height + WindowBleed * 2) * _rasterScale));
        _appWindow.Resize(new SizeInt32(_overlayWidth, _overlayHeight));
        var actualSize = _appWindow.Size;
        if (actualSize.Width > 0 && actualSize.Height > 0)
        {
            _overlayWidth = actualSize.Width;
            _overlayHeight = actualSize.Height;
        }

        ClipToPillShape(frameCornerRadius);
        _lastClipCornerRadius = frameCornerRadius;
        _lastClipScale = _rasterScale;
        _hasClipped = true;
    }

    public bool ApplyScale(double scale, double frameCornerRadius)
    {
        if (scale <= 0 || Math.Abs(scale - _rasterScale) < 0.001)
        {
            return false;
        }

        _rasterScale = scale;
        if (_logicalWidth > 0 && _logicalHeight > 0)
        {
            Resize(_logicalWidth, _logicalHeight, frameCornerRadius);
        }

        return true;
    }

    public void PositionOnForegroundMonitor(OverlayPillPosition position)
    {
        if (DraggedThisPresentation)
        {
            return;
        }

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
        var target = position == OverlayPillPosition.Bottom
            ? OverlayPlacement.BottomCenter(workArea, _overlayWidth, _overlayHeight, physicalMargin)
            : OverlayPlacement.TopCenter(workArea, _overlayWidth, _overlayHeight, physicalMargin);
        _appWindow.Move(new PointInt32(target.X, target.Y));
    }

    public void MoveOffscreenForPreWarm() => _appWindow.Move(new PointInt32(-32000, -32000));

    public void ShowUnactivated() => _appWindow.Show(activateWindow: false);

    public void Hide() => _appWindow.Hide();

    public void BeginDrag(uint pointerId)
    {
        _dragPointer = pointerId;
        _dragFromWindow = new PointInt32(_appWindow.Position.X, _appWindow.Position.Y);
        GetCursorPos(out var cursor);
        _dragFromCursor = cursor;
    }

    public void TryMoveDrag(uint pointerId)
    {
        if (_dragPointer != pointerId)
        {
            return;
        }

        GetCursorPos(out var cursor);
        var moveX = cursor.X - _dragFromCursor.X;
        var moveY = cursor.Y - _dragFromCursor.Y;
        if (moveX == 0 && moveY == 0)
        {
            return;
        }

        DraggedThisPresentation = true;
        _appWindow.Move(new PointInt32(_dragFromWindow.X + moveX, _dragFromWindow.Y + moveY));
    }

    public bool EndDrag(uint pointerId)
    {
        if (_dragPointer != pointerId)
        {
            return false;
        }

        _dragPointer = null;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_subclassedHandle != nint.Zero && _subclassProc is not null)
        {
            _ = RemoveWindowSubclass(_subclassedHandle, _subclassProc, OverlaySubclassId);
            _subclassedHandle = nint.Zero;
        }

        _subclassProc = null;
    }

    private void ClipToPillShape(double frameCornerRadius)
    {
        var fringe = Math.Max(0, Math.Min(
            (int)Math.Floor(WindowBleed * _rasterScale),
            (Math.Min(_overlayWidth, _overlayHeight) - 1) / 2));
        var radius = Math.Max(0, (int)Math.Ceiling(frameCornerRadius * _rasterScale * 2));
        var region = CreateRoundRectRgn(
            fringe,
            fringe,
            _overlayWidth - fringe,
            _overlayHeight - fringe,
            radius,
            radius);
        if (region == nint.Zero)
        {
            return;
        }

        if (SetWindowRgn(_windowHandle, region, bRedraw: true) == 0)
        {
            DeleteObject(region);
        }
    }

    private static bool QueryAnimationsEnabled()
    {
        try
        {
            var enabled = true;
            if (SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0))
            {
                return enabled;
            }
        }
        catch
        {
        }

        return true;
    }

    private void InstallMinTrackOverride()
    {
        if (_windowHandle == nint.Zero || _subclassedHandle != nint.Zero)
        {
            return;
        }

        _subclassProc = OnOverlayWindowMessage;
        if (SetWindowSubclass(_windowHandle, _subclassProc, OverlaySubclassId, 0))
        {
            _subclassedHandle = _windowHandle;
        }
        else
        {
            _subclassProc = null;
        }
    }

    private nint OnOverlayWindowMessage(
        nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WmGetMinMaxInfo && lParam != nint.Zero)
        {
            var result = DefSubclassProc(hWnd, uMsg, wParam, lParam);
            var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            minMax.PtMinTrackSize.X = 1;
            minMax.PtMinTrackSize.Y = 1;
            Marshal.StructureToPtr(minMax, lParam, fDeleteOld: false);
            return result;
        }

        if (uMsg == WmSettingChange)
        {
            _animationsEnabledCache = null;
        }

        if (uMsg == WmNcDestroy && _subclassProc is not null)
        {
            _ = RemoveWindowSubclass(hWnd, _subclassProc, uIdSubclass);
            _subclassedHandle = nint.Zero;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle, int attribute, ref uint value, int size);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(
        int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint windowHandle, nint region, bool bRedraw);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint hWnd, uint uMsg, nint wParam, nint lParam);

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
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint PtReserved;
        public NativePoint PtMaxSize;
        public NativePoint PtMaxPosition;
        public NativePoint PtMinTrackSize;
        public NativePoint PtMaxTrackSize;
    }

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
