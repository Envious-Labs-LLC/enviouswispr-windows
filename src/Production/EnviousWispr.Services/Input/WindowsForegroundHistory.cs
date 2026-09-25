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
/// the desktop, and a menu - because none of them is somewhere a person dictates, and every one of them sits
/// between the person's window and the tray click.
///
/// Must be created on a thread with a message loop (the UI thread): an out-of-context WinEvent hook delivers
/// its callbacks through that thread's messages.
/// </remarks>
public sealed class WindowsForegroundHistory : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "XamlExplorerHostIslandWindow",
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

    /// <summary>The last window that could take a paste, if it still exists.</summary>
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
            return last is not null && IsWindow(last.Value.Value) ? last : null;
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
        if (window == 0)
        {
            return;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == _ownProcessId || IsShellSurface(window))
        {
            return;
        }

        _last = new TargetWindowId(window, processId, FocusedElementId: null);
    }

    private static bool IsShellSurface(nint window)
    {
        var name = new char[128];
        var length = GetClassName(window, name, name.Length);
        return length > 0 && ShellClasses.Contains(new string(name, 0, length));
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, [Out] char[] name, int capacity);
}
