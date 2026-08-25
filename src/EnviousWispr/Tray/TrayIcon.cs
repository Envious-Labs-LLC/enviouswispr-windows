using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;

namespace EnviousWispr.Tray;

/// System-tray presence with a status line, a "Start with Windows" toggle, and the
/// only quit path (the app has no windows to close). WinForms NotifyIcon — no NuGet
/// dependency. The icon is drawn in memory so no .ico asset ships with the app.
public sealed class TrayIcon : IDisposable
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "EnviousWispr";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon _notify;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly Icon _icon;
    private readonly IntPtr _iconHandle;

    public TrayIcon(string initialStatus, bool autostartEnabled,
        Action<bool> onToggleAutostart, Action onQuit)
    {
        _iconHandle = DrawIconHandle();
        _icon = Icon.FromHandle(_iconHandle); // handle NOT owned by the Icon; we destroy it in Dispose
        _notify = new NotifyIcon { Icon = _icon, Text = initialStatus };

        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem(initialStatus) { Enabled = false };
        _autostartItem = new ToolStripMenuItem("Start with Windows") { Checked = autostartEnabled };
        _autostartItem.Click += (_, _) =>
        {
            var next = !_autostartItem.Checked;
            _autostartItem.Checked = next;
            onToggleAutostart(next);
        };
        var quitItem = new ToolStripMenuItem("Quit EnviousWispr");
        quitItem.Click += (_, _) => onQuit();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostartItem);
        menu.Items.Add(quitItem);
        _notify.ContextMenuStrip = menu;
        _notify.Visible = true;
    }

    /// Update tooltip + status line. Called from worker threads; marshal to the UI thread.
    public void SetStatus(string label, string detail)
    {
        var text = detail.Length == 0 ? label : $"{label} — {detail}";
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply(text);
        else dispatcher.BeginInvoke(new Action(() => Apply(text)));
    }

    private void Apply(string text)
    {
        if (!_notify.Visible) return; // disposed
        _notify.Text = text; // tooltip is capped at 63 chars by the shell
        _statusItem.Text = text;
    }

    // ---- autostart (HKCU Run key) ----

    public static bool AutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunSubKey);
        return key?.GetValue(RunValueName) is string;
    }

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunSubKey);
        if (enabled)
            key?.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        else
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    // ---- icon (dark pill ring + green dot, matches the overlay brand) ----

    private static IntPtr DrawIconHandle()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var ring = new SolidBrush(Color.FromArgb(255, 0x1E, 0x24, 0x2B));
            g.FillEllipse(ring, 1, 1, 30, 30);
            using var dot = new SolidBrush(Color.FromArgb(255, 0x55, 0xFF, 0x55));
            g.FillEllipse(dot, 10, 10, 12, 12);
        }
        return bmp.GetHicon();
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _icon.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
    }
}
