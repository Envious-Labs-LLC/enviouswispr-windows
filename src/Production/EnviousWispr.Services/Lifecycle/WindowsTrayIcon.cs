using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using Microsoft.Win32;

namespace EnviousWispr.Services.Lifecycle;

public sealed class WindowsTrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly SynchronizationContext _ui;
    private readonly System.Threading.Timer _sweepTimer;
    private readonly Font _menuFont;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusHeaderItem;
    private readonly ToolStripMenuItem _versionItem;
    private readonly ToolStripMenuItem _recordItem;
    private readonly ToolStripMenuItem _pasteLast;
    private readonly ToolStripMenuItem _copyLast;
    private readonly ToolStripMenuItem _lastPreview;
    private readonly ToolStripMenuItem _microphoneMenu;
    private readonly ToolStripMenuItem _appearanceMenu;
    private readonly ToolStripMenuItem _appearanceSystemItem;
    private readonly ToolStripMenuItem _appearanceLightItem;
    private readonly ToolStripMenuItem _appearanceDarkItem;
    private TrayIconState _state = TrayIconState.Idle;
    private Icon? _renderedIcon;
    private TrayIconPalette _palette = TrayIconPalette.Brand;
    private AppTheme _appTheme = AppTheme.System;
    private TrayMenuColors _menuColors = TrayMenuColors.Dark;
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

        // 10.5pt renders at 14px at 96 DPI, matching the design-system 14px typography floor.
        _menuFont = new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Font = _menuFont,
            Padding = new Padding(2, 6, 2, 6),
        };

        // 1. Disabled status header: active engine & polish + version.
        _statusHeaderItem = CreateInfoItem(
            TrayMenuFormatting.FormatStatusHeader(FinalAsrEngine.Automatic, PolishProvider.None));
        _versionItem = CreateInfoItem(TrayMenuFormatting.FormatVersionLine(version: null));
        _menu.Items.Add(_statusHeaderItem);
        _menu.Items.Add(_versionItem);
        _menu.Items.Add(new ToolStripSeparator());

        // 2. Dictation controls & last-dictation reuse.
        _recordItem = CreateActionItem(
            TrayMenuFormatting.FormatRecordingItemText(_state),
            () => ToggleRecordingRequested?.Invoke());
        _pasteLast = CreateActionItem(
            "Paste last dictation",
            () => PasteLastRequested?.Invoke(),
            enabled: false);
        _copyLast = CreateActionItem(
            "Copy last dictation",
            () => CopyLastRequested?.Invoke(),
            enabled: false);
        _lastPreview = CreateInfoItem("No dictation yet");
        var transcribeFile = CreateActionItem(
            "Transcribe a file...",
            () => TranscribeFileRequested?.Invoke());
        var addToDictionary = CreateActionItem(
            "Add to Dictionary...",
            () => AddToDictionaryRequested?.Invoke());

        _menu.Items.Add(_recordItem);
        _menu.Items.Add(_pasteLast);
        _menu.Items.Add(_copyLast);
        _menu.Items.Add(_lastPreview);
        _menu.Items.Add(transcribeFile);
        _menu.Items.Add(addToDictionary);
        _menu.Items.Add(new ToolStripSeparator());

        // 3. Microphone & Appearance submenus.
        _microphoneMenu = CreateSubmenuHost("Microphone");
        _appearanceMenu = CreateSubmenuHost("Appearance");
        _appearanceSystemItem = CreateCheckableItem("System", () => AppearanceSelected?.Invoke(AppTheme.System));
        _appearanceLightItem = CreateCheckableItem("Light", () => AppearanceSelected?.Invoke(AppTheme.Light));
        _appearanceDarkItem = CreateCheckableItem("Dark", () => AppearanceSelected?.Invoke(AppTheme.Dark));
        _appearanceMenu.DropDownItems.Add(_appearanceSystemItem);
        _appearanceMenu.DropDownItems.Add(_appearanceLightItem);
        _appearanceMenu.DropDownItems.Add(_appearanceDarkItem);
        UpdateAppearanceChecks();

        SetMicrophones([], selectedDeviceId: null);

        _menu.Items.Add(_microphoneMenu);
        _menu.Items.Add(_appearanceMenu);
        _menu.Items.Add(new ToolStripSeparator());

        // 4. App surfaces & exit.
        _menu.Items.Add(CreateActionItem("Open EnviousWispr", () => ShowWindowRequested?.Invoke()));
        _menu.Items.Add(CreateActionItem("Settings", () => OpenSettingsRequested?.Invoke()));
        _menu.Items.Add(CreateActionItem("Help Center", () => HelpCenterRequested?.Invoke()));
        _menu.Items.Add(CreateActionItem("Check for Updates...", () => CheckForUpdatesRequested?.Invoke()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(CreateActionItem("Exit EnviousWispr", () => ExitRequested?.Invoke()));

        AttachPopupLifecycle(_menu);
        AttachPopupLifecycle(_microphoneMenu.DropDown);
        AttachPopupLifecycle(_appearanceMenu.DropDown);

        // READ AGAIN EVERY TIME THE MENU OPENS. History and connected microphones change from places
        // that never tell the tray, and theme preferences may have drifted.
        _menu.Opening += (_, _) => HandleMenuOpening();

        _icon = LoadIcon(iconPath);
        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _icon,
            Text = "EnviousWispr: starting",
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindowRequested?.Invoke();

        // 15 frames a second, which is what macOS uses for the same sweep. Faster buys nothing at
        // this size and wakes the machine more often during a transcription.
        _sweepTimer = new System.Threading.Timer(_ => OnUiThread(AdvanceSweep), null,
            Timeout.Infinite, Timeout.Infinite);

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        RefreshSystemPreferences();
    }

    public event Action? ShowWindowRequested;

    public event Action? OpenSettingsRequested;

    public event Action? ExitRequested;

    public event Action? PasteLastRequested;

    public event Action? CopyLastRequested;

    /// <summary>Open the Transcribe a File page - with the other ways to get words in, as on macOS. Ref: #211.</summary>
    public event Action? TranscribeFileRequested;

    /// <summary>The menu is opening; the app reads the history and answers with <see cref="SetLastDictationPreview"/>.</summary>
    public event Action? LastDictationPreviewRequested;

    /// <summary>The user clicked Start Recording or Stop Recording in the tray menu.</summary>
    public event Action? ToggleRecordingRequested;

    /// <summary>The user clicked Add to Dictionary... in the tray menu.</summary>
    public event Action? AddToDictionaryRequested;

    /// <summary>The tray menu is opening and wants the latest capture device list via <see cref="SetMicrophones"/>.</summary>
    public event Action? MicrophoneMenuOpeningRequested;

    /// <summary>The user selected a microphone (or <see langword="null"/> for System Default) from the Microphone submenu.</summary>
    public event Action<string?>? MicrophoneSelected;

    /// <summary>The user selected an <see cref="AppTheme"/> from the Appearance submenu.</summary>
    public event Action<AppTheme>? AppearanceSelected;

    /// <summary>The user clicked Help Center in the tray menu.</summary>
    public event Action? HelpCenterRequested;

    /// <summary>The user clicked Check for Updates... in the tray menu.</summary>
    public event Action? CheckForUpdatesRequested;

    /// <summary>Updates the active engine and AI polish summary shown at the top of the tray menu.</summary>
    public void SetEngineAndPolishSummary(FinalAsrEngine engine, PolishProvider polish) =>
        SetStatusHeader(TrayMenuFormatting.FormatStatusHeader(engine, polish));

    /// <summary>Updates the disabled status header text at the top of the tray menu.</summary>
    public void SetStatusHeader(string summary)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _statusHeaderItem.Text = TrayMenuFormatting.EscapeMnemonic(summary);
    }

    /// <summary>Updates the disabled version line under the status header.</summary>
    public void SetVersion(string? version)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _versionItem.Text = TrayMenuFormatting.EscapeMnemonic(TrayMenuFormatting.FormatVersionLine(version));
    }

    /// <summary>Shows the active recording shortcut beside Start/Stop Recording.</summary>
    public void SetRecordingShortcut(string? shortcut)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recordItem.ShortcutKeyDisplayString = shortcut ?? string.Empty;
        _recordItem.ShowShortcutKeys = !string.IsNullOrEmpty(shortcut);
    }

    /// <summary>Shows each item's shortcut beside it, or nothing for one that is unset or not listening. Ref: #206.</summary>
    public void SetLastDictationShortcuts(string? pasteShortcut, string? copyShortcut)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pasteLast.ShortcutKeyDisplayString = pasteShortcut ?? string.Empty;
        _copyLast.ShortcutKeyDisplayString = copyShortcut ?? string.Empty;
        _pasteLast.ShowShortcutKeys = !string.IsNullOrEmpty(pasteShortcut);
        _copyLast.ShowShortcutKeys = !string.IsNullOrEmpty(copyShortcut);
    }

    /// <summary>Names the dictation the two items would reuse, or disables them when there is none.</summary>
    public void SetLastDictationPreview(string? preview)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var available = !string.IsNullOrEmpty(preview);
        _pasteLast.Enabled = available;
        _copyLast.Enabled = available;
        _lastPreview.Text = available
            ? $"“{TrayMenuFormatting.EscapeMnemonic(preview!)}”"
            : "No dictation yet";
    }

    /// <summary>Updates the Appearance submenu checkmark and re-themes the tray popup menu.</summary>
    public void SetTheme(AppTheme theme)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _appTheme = theme;
        UpdateAppearanceChecks();
        ApplyMenuTheme();
    }

    /// <summary>Rebuilds the Microphone submenu with System Default followed by each capture device.</summary>
    public void SetMicrophones(IReadOnlyList<TrayMicrophoneItem> devices, string? selectedDeviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(devices);

        var previousItems = _microphoneMenu.DropDownItems.Cast<ToolStripItem>().ToArray();
        _microphoneMenu.DropDownItems.Clear();
        foreach (var existing in previousItems)
        {
            existing.Dispose();
        }

        var hasSpecificSelection = !string.IsNullOrWhiteSpace(selectedDeviceId) &&
            devices.Any(d => string.Equals(d.Id, selectedDeviceId, StringComparison.Ordinal));

        var defaultItem = CreateCheckableItem(
            "System Default",
            () => MicrophoneSelected?.Invoke(null));
        defaultItem.Checked = !hasSpecificSelection;
        _microphoneMenu.DropDownItems.Add(defaultItem);

        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.Id))
            {
                continue;
            }

            var capturedId = device.Id;
            var item = CreateCheckableItem(
                TrayMenuFormatting.EscapeMnemonic(device.DisplayName),
                () => MicrophoneSelected?.Invoke(capturedId));
            item.Checked = string.Equals(capturedId, selectedDeviceId, StringComparison.Ordinal);
            _microphoneMenu.DropDownItems.Add(item);
        }
    }

    public void SetStatus(string status)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var text = $"EnviousWispr: {status}";
        _notifyIcon.Text = text.Length <= 127 ? text : text[..127];
    }

    /// <summary>Puts the tray icon in one state.</summary>
    public void SetState(TrayIconState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (state == _state)
        {
            return;
        }

        _state = state;
        _recordItem.Text = TrayMenuFormatting.FormatRecordingItemText(_state);
        _recordItem.Enabled = _state != TrayIconState.Processing;
        _sweepAngle = 0;
        ReconcileSweep();
        PaintState();
    }

    internal string StatusHeaderText => _statusHeaderItem.Text ?? string.Empty;

    internal string VersionText => _versionItem.Text ?? string.Empty;

    internal string RecordItemText => _recordItem.Text ?? string.Empty;

    internal bool RecordItemEnabled => _recordItem.Enabled;

    internal string RecordItemShortcut => _recordItem.ShortcutKeyDisplayString ?? string.Empty;

    internal TrayMenuThemeMode EffectiveMenuThemeMode => _menuColors.Mode;

    internal IReadOnlyList<(string Text, bool Checked)> MicrophoneMenuItems =>
        _microphoneMenu.DropDownItems
            .OfType<ToolStripMenuItem>()
            .Select(item => (item.Text ?? string.Empty, item.Checked))
            .ToArray();

    internal IReadOnlyList<(string Text, bool Checked)> AppearanceMenuItems =>
        _appearanceMenu.DropDownItems
            .OfType<ToolStripMenuItem>()
            .Select(item => (item.Text ?? string.Empty, item.Checked))
            .ToArray();

    internal void SimulateMenuOpeningForTest() => HandleMenuOpening();

    private void HandleMenuOpening()
    {
        if (_disposed)
        {
            return;
        }

        _pasteLast.Enabled = false;
        _copyLast.Enabled = false;
        ApplyMenuTheme();
        LastDictationPreviewRequested?.Invoke();
        MicrophoneMenuOpeningRequested?.Invoke();
    }

    private void UpdateAppearanceChecks()
    {
        _appearanceSystemItem.Checked = _appTheme == AppTheme.System;
        _appearanceLightItem.Checked = _appTheme == AppTheme.Light;
        _appearanceDarkItem.Checked = _appTheme == AppTheme.Dark;
    }

    private void ApplyMenuTheme()
    {
        var (systemUsesLight, appsUseLight) = ReadPersonalizeThemeFlags();
        var mode = TrayMenuThemeResolver.Resolve(
            _appTheme,
            SystemInformation.HighContrast,
            systemUsesLight,
            appsUseLight);
        _menuColors = TrayMenuColors.For(mode);

        var renderer = new FluentTrayMenuRenderer(_menuColors);
        ApplyThemeToDropDown(_menu, renderer);
        ApplyThemeToDropDown(_microphoneMenu.DropDown, renderer);
        ApplyThemeToDropDown(_appearanceMenu.DropDown, renderer);
    }

    private void ApplyThemeToDropDown(ToolStripDropDown dropDown, FluentTrayMenuRenderer renderer)
    {
        dropDown.Renderer = renderer;
        dropDown.BackColor = _menuColors.Background;
        dropDown.ForeColor = _menuColors.TextPrimary;
        if (dropDown.IsHandleCreated)
        {
            FluentTrayMenuRenderer.ApplyWindowFrameAttributes(dropDown.Handle, _menuColors);
        }
    }

    private void AttachPopupLifecycle(ToolStripDropDown dropDown)
    {
        dropDown.HandleCreated += (_, _) =>
        {
            if (!_disposed && dropDown.IsHandleCreated)
            {
                FluentTrayMenuRenderer.ApplyWindowFrameAttributes(dropDown.Handle, _menuColors);
            }
        };
        dropDown.Opening += (_, _) =>
        {
            if (!_disposed && dropDown.IsHandleCreated)
            {
                FluentTrayMenuRenderer.ApplyWindowFrameAttributes(dropDown.Handle, _menuColors);
            }
        };
    }

    private static ToolStripMenuItem CreateInfoItem(string text) => new(text)
    {
        Enabled = false,
        Padding = new Padding(8, 5, 8, 5),
        Margin = new Padding(0, 1, 0, 1),
    };

    private static ToolStripMenuItem CreateActionItem(string text, Action onClick, bool enabled = true) =>
        new(text, image: null, (_, _) => onClick())
        {
            Enabled = enabled,
            Padding = new Padding(8, 5, 8, 5),
            Margin = new Padding(0, 1, 0, 1),
        };

    private static ToolStripMenuItem CreateCheckableItem(string text, Action onClick) =>
        new(text, image: null, (_, _) => onClick())
        {
            Padding = new Padding(4, 5, 8, 5),
            Margin = new Padding(0, 1, 0, 1),
        };

    private ToolStripMenuItem CreateSubmenuHost(string text)
    {
        var item = new ToolStripMenuItem(text)
        {
            Padding = new Padding(8, 5, 8, 5),
            Margin = new Padding(0, 1, 0, 1),
        };
        if (item.DropDown is ToolStripDropDownMenu dropDownMenu)
        {
            dropDownMenu.ShowImageMargin = false;
            dropDownMenu.ShowCheckMargin = true;
            dropDownMenu.Font = _menuFont;
            dropDownMenu.Padding = new Padding(2, 6, 2, 6);
        }

        return item;
    }

    /// <summary>Re-reads what the user has asked Windows for, and redraws to match.</summary>
    private void RefreshSystemPreferences()
    {
        _animationsAllowed = SystemAnimationsAreOn();
        var (systemUsesLight, _) = ReadPersonalizeThemeFlags();
        _palette = SystemInformation.HighContrast
            ? TrayIconPalette.ForSystem(SystemColors.WindowText)
            : TrayIconPalette.ForTaskbar(systemUsesLight ?? false);
        _iconSize = Math.Max(16, GetSystemMetricsForDpi(SmallIconWidth, GetDpiForSystem()));
        ApplyMenuTheme();
        ReconcileSweep();
        PaintState();
    }

    internal static (bool? SystemUsesLightTheme, bool? AppsUseLightTheme) ReadPersonalizeThemeFlags()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                writable: false);
            if (key is null)
            {
                return (null, null);
            }

            return (
                ReadDwordBool(key.GetValue("SystemUsesLightTheme")),
                ReadDwordBool(key.GetValue("AppsUseLightTheme")));
        }
        catch
        {
            return (null, null);
        }
    }

    private static bool? ReadDwordBool(object? raw) => raw switch
    {
        int value => value != 0,
        _ => null,
    };

    private void ReconcileSweep()
    {
        var running = _state == TrayIconState.Processing && _animationsAllowed;
        _sweepTimer.Change(
            running ? TimeSpan.FromMilliseconds(1000 / 15) : Timeout.InfiniteTimeSpan,
            running ? TimeSpan.FromMilliseconds(1000 / 15) : Timeout.InfiniteTimeSpan);
    }

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

    private void OnUiThread(Action work) => _ui.Post(_ => RunIfAlive(work), state: null);

    private void RunIfAlive(Action work)
    {
        if (_disposed)
        {
            return;
        }

        work();
    }

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

    private static bool SystemAnimationsAreOn()
    {
        var enabled = 0;
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
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _sweepTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _menuFont.Dispose();
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
