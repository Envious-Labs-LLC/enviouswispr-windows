using System.Drawing;
using System.Windows.Forms;

namespace EnviousWispr.Services.Lifecycle;

public sealed class WindowsTrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private bool _disposed;

    public WindowsTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open EnviousWispr", image: null, (_, _) => ShowWindowRequested?.Invoke());
        menu.Items.Add("Settings", image: null, (_, _) => OpenSettingsRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit EnviousWispr", image: null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Text = "EnviousWispr — starting",
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindowRequested?.Invoke();
    }

    public event Action? ShowWindowRequested;

    public event Action? OpenSettingsRequested;

    public event Action? ExitRequested;

    public void SetStatus(string status)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var text = $"EnviousWispr — {status}";
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
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
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
