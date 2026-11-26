using System.Runtime.InteropServices;
using EnviousWispr.Core.Input;

namespace EnviousWispr.Services.Input;

/// <summary>The last window the person was actually working in - not the taskbar, not the desktop, not this app.</summary>
/// <remarks>
/// WHY A TRAY ACTION CANNOT JUST ASK WHAT IS IN FRONT. Clicking the notification area moves the foreground to
/// the taskbar, and the menu that opens belongs to this app, so by the time "Paste last dictation" is chosen
/// the window the person wants it in is two steps back. This follows the foreground as it moves
/// (`EVENT_SYSTEM_FOREGROUND`) and remembers the last one that could be a target. Ref: #206.
///
/// The shell's own surfaces are skipped by window class - the taskbars, the overflow flyout, Start and search,
/// the desktop, Windows 11 XAML shell overlays, empty UWP frame overlays, and menus - because none of them
/// is somewhere a person dictates, and every one of them sits between the person's window and the tray click.
///
/// Must be created on a thread with a message loop (the UI thread): an out-of-context WinEvent hook delivers
/// its callbacks through that thread's messages.
/// </remarks>
public sealed class WindowsForegroundHistory : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const int DwmWindowCloakedAttribute = 14;
    private const uint GetWindowChild = 5;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "XamlExplorerHostWindow",
        "XamlExplorerHostIslandWindow",
        "Shell_InputSwitchTopLevelWindow",
        "MultitaskingViewFrame",
        "TaskListThumbnailWnd",
        "ForegroundStaging",
        "LockScreenControllerProxyWindow",
        "Windows.UI.Core.CoreWindow",
        "Progman",
        "WorkerW",
        "#32768",
    };

    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly WinEventProcedure _procedure;
    private nint _hook;
    private TargetWindowId? _last;

    public WindowsForegroundHistory()
    {
        _procedure = OnForegroundChanged;
        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            0,
            _procedure,
            0,
            0,
            WinEventOutOfContext);
        Consider(GetForegroundWindow());
    }

    /// <summary>The last window that could take a paste, if it still exists with its process.</summary>
    public TargetWindowId? LastTarget
    {
        get
        {
            // A hook that never installed, or one already removed, is not following anything: what it holds
            // could be any age.
            if (_hook == 0)
            {
                return null;
            }

            var last = _last;
            if (last is null || !IsWindow(last.Value.Value))
            {
                return null;
            }

            _ = GetWindowThreadProcessId(last.Value.Value, out var processId);
            return processId != 0 && (last.Value.ProcessId == 0 || processId == last.Value.ProcessId)
                ? last
                : null;
        }
    }

    public void Dispose()
    {
        var hook = Interlocked.Exchange(ref _hook, 0);
        if (hook != 0)
        {
            UnhookWinEvent(hook);
        }
    }

    private void OnForegroundChanged(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime) => Consider(window);

    private void Consider(nint window)
    {
        if (window == 0 || !IsWindow(window))
        {
            return;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == _ownProcessId || IsShellSurface(window))
        {
            return;
        }

        _last = new TargetWindowId(
            window,
            processId,
            FocusedElementId: null,
            ProcessName: WindowsWindowIdentity.TryGetProcessName(processId));
    }

    internal static bool IsTransientShellWindow(
        string? className,
        bool isVisible,
        bool isCloaked,
        bool hasChildWindow,
        int width,
        int height)
    {
        if (!isVisible || isCloaked)
        {
            return true;
        }

        if (string.IsNullOrEmpty(className))
        {
            return false;
        }

        if (ShellClasses.Contains(className))
        {
            return true;
        }

        if (string.Equals(className, "ApplicationFrameWindow", StringComparison.Ordinal))
        {
            return !hasChildWindow || width <= 0 || height <= 0;
        }

        return false;
    }

    private static bool IsShellSurface(nint window)
    {
        var className = WindowsWindowIdentity.TryGetClassName(window);
        var isVisible = IsWindowVisible(window);
        var isCloaked = IsWindowCloaked(window);
        var hasChild = GetWindow(window, GetWindowChild) != 0;
        var (width, height) = GetWindowRect(window, out var rect)
            ? (rect.Right - rect.Left, rect.Bottom - rect.Top)
            : (0, 0);
        return IsTransientShellWindow(className, isVisible, isCloaked, hasChild, width, height);
    }

    private static bool IsWindowCloaked(nint window)
    {
        try
        {
            return DwmGetWindowAttribute(window, DwmWindowCloakedAttribute, out var cloaked, sizeof(int)) == 0 &&
                cloaked != 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate void WinEventProcedure(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint module,
        WinEventProcedure procedure,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint window,
        int attribute,
        out int value,
        int size);
}
