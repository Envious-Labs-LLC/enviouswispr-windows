using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using EnviousWispr.Core.Presentation;
using Microsoft.Win32;

namespace EnviousWispr.Services.Lifecycle;

public sealed class WindowsTrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly SynchronizationContext _ui;
    private readonly System.Threading.Timer _sweepTimer;
    private TrayIconState _state = TrayIconState.Idle;
    private Icon? _renderedIcon;
    private TrayIconPalette _palette = TrayIconPalette.Brand;
    private int _iconSize = 16;
    private bool _animationsAllowed;
    private double _sweepAngle;
    private bool _disposed;

    public WindowsTrayIcon(string? iconPath = null)
    {
        // THE UI CONTEXT IS REQUIRED, NOT OPTIONAL, and the alternative was worse than a throw. A
        // null context meant timer ticks and system-preference callbacks running render work on
        // whatever thread Windows chose, concurrently with each other and with disposal. NotifyIcon
        // is a Windows Forms component and expects one thread. A tray icon built off the UI thread
        // is a defect in the caller, and this is the only moment it is cheap to notice.
        _ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException(
                "The tray icon must be created on the UI thread: it marshals timer ticks and "
                    + "system-preference callbacks back through that thread's context.");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open EnviousWispr", image: null, (_, _) => ShowWindowRequested?.Invoke());
        menu.Items.Add("Settings", image: null, (_, _) => OpenSettingsRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit EnviousWispr", image: null, (_, _) => ExitRequested?.Invoke());

        _icon = LoadIcon(iconPath);
        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = _icon,
            Text = "EnviousWispr: starting",
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindowRequested?.Invoke();

        // 15 frames a second, which is what macOS uses for the same sweep. Faster buys nothing at
        // this size and wakes the machine more often during a transcription.
        //
        // A THREADING TIMER RATHER THAN A FORMS ONE, and the marshalling below is why. A Forms timer
        // needs WM_TIMER pumped on the thread that made it, which is an assumption about a host this
        // class does not own; a threading timer needs none, and the same captured context that
        // carries its ticks back to the UI thread also carries the system-settings callbacks, which
        // arrive on a thread of the operating system's choosing.
        _sweepTimer = new System.Threading.Timer(_ => OnUiThread(AdvanceSweep), null,
            Timeout.Infinite, Timeout.Infinite);

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        RefreshSystemPreferences();
    }

    public event Action? ShowWindowRequested;

    public event Action? OpenSettingsRequested;

    public event Action? ExitRequested;

    public void SetStatus(string status)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var text = $"EnviousWispr: {status}";
        // 127, not 63. NOTIFYICONDATA's tip has been 128 characters since Windows 2000 and the
        // shorter limit belongs to a struct version this app does not use, so several shipped
        // sentences were being cut for no reason.
        _notifyIcon.Text = text.Length <= 127 ? text : text[..127];
    }

    /// <summary>Puts the tray icon in one state.</summary>
    /// <remarks>
    /// THE ICON NEVER CHANGED BEFORE THIS. It was assigned once at construction and the only thing
    /// that moved was the tooltip, so the one surface the app owns that is always visible said
    /// nothing about whether the microphone was open.
    /// </remarks>
    public void SetState(TrayIconState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (state == _state)
        {
            return;
        }

        _state = state;
        _sweepAngle = 0;
        ReconcileSweep();
        PaintState();
    }

    /// <summary>Re-reads what the user has asked Windows for, and redraws to match.</summary>
    /// <remarks>
    /// READ LIVE, NOT ONCE AT STARTUP, AND THE THREE THINGS IT READS FAILED THE SAME WAY. A person
    /// who turns animations off mid-transcription, plugs in a second display at a different scale,
    /// or switches High Contrast on has changed what this icon should be - and a value sampled in a
    /// constructor cannot know. All three arrive through the same two system events, so they are
    /// answered in one place rather than patched one at a time.
    /// </remarks>
    private void RefreshSystemPreferences()
    {
        _animationsAllowed = SystemAnimationsAreOn();
        _palette = SystemInformation.HighContrast
            ? TrayIconPalette.ForSystem(SystemColors.WindowText)
            : TrayIconPalette.Brand;
        // GetSystemMetrics is documented as NOT DPI-aware and must not be used from a per-monitor
        // aware thread, which this process is. The ForDpi variant answers the question actually
        // being asked: how big is a small icon at the DPI the shell is drawing at right now.
        _iconSize = Math.Max(16, GetSystemMetricsForDpi(SmallIconWidth, GetDpiForSystem()));
        ReconcileSweep();
        PaintState();
    }

    /// <summary>Starts or stops the sweep to match the state and the user's animation setting.</summary>
    /// <remarks>
    /// CALLED FROM BOTH PATHS, INCLUDING WHEN THE STATE HAS NOT CHANGED. SetState returns early on a
    /// repeat, so if this lived only there, turning animations off during a long transcription would
    /// leave the timer running until the next state change - which on a slow machine is the whole
    /// point at which somebody would be turning animations off.
    /// </remarks>
    private void ReconcileSweep()
    {
        var running = _state == TrayIconState.Processing && _animationsAllowed;
        // A stopped sweep still shows the ring, held still. The state keeps its own picture.
        _sweepTimer.Change(
            running ? TimeSpan.FromMilliseconds(1000 / 15) : Timeout.InfiniteTimeSpan,
            running ? TimeSpan.FromMilliseconds(1000 / 15) : Timeout.InfiniteTimeSpan);
    }

    /// <summary>Moves the sweep on one frame, if a frame is still wanted.</summary>
    /// <remarks>
    /// THE CONDITION IS RECHECKED HERE AND NOT ONLY AT THE TIMER, because stopping a timer does not
    /// unpost the work it has already queued. A tick that was in flight when the user turned
    /// animations off would still arrive and still redraw, and if the UI thread had been busy for a
    /// moment several would arrive together and the ring would visibly jump before settling. The
    /// timer decides whether to ASK for a frame; this decides whether to draw one.
    /// </remarks>
    private void AdvanceSweep()
    {
        if (_state != TrayIconState.Processing || !_animationsAllowed)
        {
            return;
        }

        _sweepAngle = (_sweepAngle + 24) % 360;
        PaintState();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) =>
        OnUiThread(RefreshSystemPreferences);

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        OnUiThread(RefreshSystemPreferences);

    /// <summary>Runs work where the tray icon lives.</summary>
    /// <remarks>
    /// SYSTEM EVENT CALLBACKS ARRIVE ON A THREAD OF WINDOWS' CHOOSING, and NotifyIcon is a Windows
    /// Forms component that expects one thread. The disposed check happens again inside, because a
    /// post can be delivered after Dispose has run.
    /// </remarks>
    private void OnUiThread(Action work) => _ui.Post(_ => RunIfAlive(work), state: null);

    private void RunIfAlive(Action work)
    {
        if (_disposed)
        {
            return;
        }

        work();
    }

    /// <summary>Renders the current state and hands the new icon to the tray.</summary>
    /// <remarks>
    /// EVERY ICON HANDLE IS DESTROYED, AND THIS IS THE WHOLE REASON THIS METHOD EXISTS SEPARATELY.
    /// `Bitmap.GetHicon` allocates a handle the garbage collector does not own; `Icon.FromHandle`
    /// wraps it without taking responsibility for it either. At fifteen frames a second that is
    /// nine hundred leaked handles a minute, and the process hits the ten-thousand handle ceiling
    /// in about eleven minutes of one long transcription. The leak is invisible until the app stops
    /// being able to draw anything at all.
    ///
    /// The previous icon is released only AFTER the new one is on the tray, because releasing the
    /// icon the tray is currently drawing is a flicker at best.
    /// </remarks>
    private void PaintState()
    {
        using var frame = TrayIconRenderer.Render(_state, _iconSize, _palette, _sweepAngle);
        var handle = frame.GetHicon();
        try
        {
            using var fresh = Icon.FromHandle(handle);
            var previous = _renderedIcon;
            _renderedIcon = (Icon)fresh.Clone();
            _notifyIcon.Icon = _renderedIcon;
            previous?.Dispose();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>Whether the user has asked Windows to animate things.</summary>
    /// <remarks>
    /// macOS HONOURS REDUCE MOTION HERE AND SO DOES THIS. A spinning icon in the corner of the
    /// screen is exactly the kind of thing somebody turns that setting off to stop. With animation
    /// off the processing state still gets its own picture, it simply holds still.
    /// </remarks>
    private static bool SystemAnimationsAreOn()
    {
        var enabled = 0;
        // FAILS CLOSED. An accessibility preference we cannot read is not a preference to guess in
        // favour of ourselves: the setting exists so that moving content does not reach people it
        // harms, and the cost of being wrong in this direction is a ring that holds still.
        return SystemParametersInfo(ClientAreaAnimation, 0, ref enabled, 0) && enabled != 0;
    }

    public void ShowBackgroundNotice() => _notifyIcon.ShowBalloonTip(
        2500,
        "EnviousWispr is still ready",
        "Use the tray icon to reopen settings. Push-to-talk keeps working in the background.",
        ToolTipIcon.Info);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Detached FIRST. A static event holding an instance method keeps this object alive for the
        // life of the process, and a callback arriving mid-teardown would touch a disposed tray.
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _sweepTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _renderedIcon?.Dispose();
        _icon.Dispose();
    }

    private static Icon LoadIcon(string? iconPath)
    {
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            return new Icon(iconPath);
        }

        return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ??
            (Icon)SystemIcons.Application.Clone();
    }

    private const uint ClientAreaAnimation = 0x1042;
    private const int SmallIconWidth = 49;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action, uint param, ref int value, uint update);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
