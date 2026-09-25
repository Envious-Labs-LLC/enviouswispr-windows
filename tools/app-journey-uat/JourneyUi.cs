using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace EnviousWispr.AppJourney.Uat;

/// <summary>Finds EnviousWispr's controls through UI Automation and clicks them with a real pointer.</summary>
/// <remarks>
/// THE WINDOW IS FOUND BY NAME, NOT BY PROCESS. The app has two top-level windows, the main one and the
/// recording pill, and the first one UI Automation returns for the process can be the pill, whose tree
/// has none of these controls - an empty answer that reads exactly like a dead app.
///
/// COORDINATES ARE READ AND USED IN ONE DPI CONTEXT. The rectangle comes from UI Automation and the
/// pointer goes to it through SetCursorPos; on a scaled display the two disagree unless the thread doing
/// both is per-monitor aware, and a click that lands beside the button still "happens". So the thread is
/// made aware first, and every click is refused unless the point it is about to press belongs to the app.
/// </remarks>
internal static class JourneyUi
{
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private static readonly nint PerMonitorAwareV2 = -4;
    private static int _lastClickProcessId;

    /// <summary>The app's main window, by its title within the app's process.</summary>
    public static AutomationElement FindMainWindow(int processId, TimeSpan timeout)
    {
        _ = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.NameProperty, "EnviousWispr"));
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (AutomationElement.RootElement.FindFirst(TreeScope.Children, condition) is { } window)
            {
                return window;
            }

            Thread.Sleep(200);
        }

        throw JourneyExpectationException.Instrument("The EnviousWispr main window was not found through UI Automation.");
    }

    /// <summary>A control by its automation id, once it is on screen; null if it never is.</summary>
    public static AutomationElement? WaitForElement(AutomationElement window, string automationId, TimeSpan timeout) =>
        Poll(timeout, () => Visible(window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))));

    /// <summary>A control by its automation id, once it is on screen and enabled.</summary>
    public static AutomationElement? WaitForEnabled(AutomationElement window, string automationId, TimeSpan timeout) =>
        Poll(timeout, () => Visible(window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))) is { Current.IsEnabled: true } found
                ? found
                : null);

    /// <summary>A control by its accessible name and type, once it is on screen.</summary>
    public static AutomationElement? WaitForNamed(AutomationElement window, string name, ControlType type, TimeSpan timeout) =>
        Poll(timeout, () => Visible(window.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, name),
                new PropertyCondition(AutomationElement.ControlTypeProperty, type)))));

    /// <summary>The first child of a type, once there is one on screen.</summary>
    public static AutomationElement? WaitForFirstChild(AutomationElement parent, ControlType type, TimeSpan timeout) =>
        Poll(timeout, () => Visible(parent.FindFirst(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, type))));

    /// <summary>True once no on-screen control in the window carries this name, within the timeout.</summary>
    public static bool WaitForAbsence(AutomationElement window, string name, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (Visible(window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name))) is null)
            {
                return true;
            }

            Thread.Sleep(200);
        }

        return false;
    }

    /// <summary>Moves a topmost window beside the app's window, so a click on the app cannot land in it.</summary>
    public static void MoveClearOf(nint topmost, AutomationElement window)
    {
        _ = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        if (!NativeMethods.GetWindowRect(topmost, out var own))
        {
            throw JourneyExpectationException.Instrument("The controlled target's window rectangle could not be read.");
        }

        var app = window.Current.BoundingRectangle;
        var width = own.Right - own.Left;
        var left = app.Left - width - 16 >= 0 ? (int)app.Left - width - 16 : (int)app.Right + 16;
        const uint noSizeNoZOrderNoActivate = 0x0001 | 0x0004 | 0x0010;
        if (!NativeMethods.SetWindowPos(topmost, 0, left, Math.Max(0, (int)app.Top), 0, 0, noSizeNoZOrderNoActivate))
        {
            throw JourneyExpectationException.Instrument("The controlled target could not be moved clear of EnviousWispr's window.");
        }
    }

    /// <summary>Clicks the middle of a control with the left button, through the input queue.</summary>
    public static void Click(AutomationElement element)
    {
        _ = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        var rectangle = element.Current.BoundingRectangle;
        if (rectangle.IsEmpty || rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            throw JourneyExpectationException.Instrument("The control to click has no on-screen rectangle.");
        }

        var x = (int)(rectangle.Left + (rectangle.Width / 2));
        var y = (int)(rectangle.Top + (rectangle.Height / 2));
        var processId = element.Current.ProcessId;

        // THE POINT MUST BE THE APP'S before anything is pressed: a window over it, or a coordinate read in
        // another DPI context, would put the click somewhere else and the press would still "happen".
        var under = WindowFromPoint(new NativePoint { X = x, Y = y });
        _ = NativeMethods.GetWindowThreadProcessId(under, out var owner);
        if (under == 0 || owner != processId)
        {
            throw JourneyExpectationException.Instrument(
                $"The point to click at {x},{y} belongs to process {owner}, not the app ({processId}).");
        }

        if (!NativeMethods.SetCursorPos(x, y))
        {
            throw JourneyExpectationException.Instrument("The pointer could not be moved to the control.");
        }

        Thread.Sleep(120);
        SendMouseButton(MouseLeftDown);
        Thread.Sleep(60);
        SendMouseButton(MouseLeftUp);
        _lastClickProcessId = (int)owner;
        Thread.Sleep(250);
    }

    /// <summary>The lists and navigation items the window shows, by type and name, for a failure message.</summary>
    public static string DescribeLists(AutomationElement window)
    {
        var found = window.FindAll(
            TreeScope.Descendants,
            new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree)));
        var parts = new List<string>();
        foreach (AutomationElement element in found)
        {
            parts.Add($"{element.Current.ControlType.ProgrammaticName}:{element.Current.Name}:offscreen={element.Current.IsOffscreen}");
        }

        return string.Join(" | ", parts);
    }

    /// <summary>Whether the last click was checked to land on a window of that process.</summary>
    public static bool LastClickLandedInProcess(int processId) => _lastClickProcessId == processId;

    private static void SendMouseButton(uint flags)
    {
        // THE PAYLOAD IS CHECKED BEFORE THE SYSCALL: a 40-byte INPUT, and a button that is actually named.
        if (Marshal.SizeOf<Input>() != 40)
        {
            throw JourneyExpectationException.Instrument("The synthetic mouse input does not match the Win64 ABI (INPUT must be 40 bytes).");
        }

        var input = new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = flags } } };
        if (input.Data.Mouse.Flags is not (MouseLeftDown or MouseLeftUp))
        {
            throw JourneyExpectationException.Instrument("Refusing to send an empty mouse event.");
        }

        if (NativeMethods.SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            throw JourneyExpectationException.Instrument("Synthetic mouse input was not inserted into the input stream.");
        }
    }

    private static AutomationElement? Visible(AutomationElement? element) =>
        element is not null && !element.Current.IsOffscreen ? element : null;

    private static AutomationElement? Poll(TimeSpan timeout, Func<AutomationElement?> find)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (find() is { } found)
                {
                    return found;
                }
            }
            catch (ElementNotAvailableException)
            {
                // The tree changed under the read; the next poll reads it again.
            }

            if (timer.Elapsed >= timeout)
            {
                return null;
            }

            Thread.Sleep(200);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
}
