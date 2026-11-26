using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Core.Input;

/// <summary>The synthesized paste shortcut selected for a destination window.</summary>
public enum PasteKeystrokeKind
{
    CtrlV,
    ShiftInsert,
}

/// <summary>Why UI Automation direct value insertion is bypassed for a destination.</summary>
public enum DirectWriteSkipReason
{
    None,
    Browser,
    ElectronOrChromiumHost,
    Terminal,
    Office,
    Chat,
}

/// <summary>Per-application delivery policy resolved for a Windows target.</summary>
public sealed record WindowsPasteRule(
    TextTargetKind TargetKind,
    bool SkipDirectValueWrite,
    DirectWriteSkipReason DirectWriteSkip,
    PasteKeystrokeKind Keystroke,
    bool AllowsWindowFallbackWithoutFocusedElement,
    int MinimumPasteSettleMilliseconds = 200);

/// <summary>
/// Per-application paste routing policy for Windows targets (ported in spirit from macOS
/// <c>PasteDeliveryPolicy</c>, preserving the 3-route contract for standard Win32 edit controls).
/// </summary>
/// <remarks>
/// Standard Win32/WinForms/WPF edit controls keep UI Automation direct value write as route 1.
/// Chromium/Electron IDEs, browsers, chat apps, Office apps, and terminals skip UIA direct write
/// because Chromium/Electron <c>ValuePattern</c> implementations are commonly read-only, stale, or
/// corrupt editor buffers, and terminal hosts use <c>Shift+Insert</c> instead of <c>Ctrl+V</c>.
/// </remarks>
public static class WindowsPastePolicy
{
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowsterminal",
        "cmd",
        "powershell",
        "pwsh",
        "conhost",
        "wezterm",
        "wezterm-gui",
        "alacritty",
        "mintty",
        "tabby",
        "putty",
        "kitty",
        "ghostty",
        "hyper",
        "git-bash",
        "bash",
        "wsl",
        "ubuntu",
        "rio",
        "warp",
    };

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "msedge",
        "firefox",
        "brave",
        "opera",
        "vivaldi",
        "arc",
        "zen",
        "waterfox",
        "librewolf",
        "chromium",
    };

    private static readonly HashSet<string> ElectronAndChromiumAppProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "antigravity",
        "code",
        "code - insiders",
        "codium",
        "cursor",
        "windsurf",
        "zed",
        "sublime_text",
        "idea64",
        "pycharm64",
        "webstorm64",
        "rider64",
        "clion64",
        "goland64",
        "datagrip64",
        "studio64",
        "obsidian",
        "notion",
        "postman",
        "insomnia",
        "figma",
        "linear",
        "spotify",
        "claude",
        "chatgpt",
        "perplexity",
        "superhuman",
        "thunderbird",
        "logseq",
        "craft",
        "clickup",
        "asana",
        "trello",
        "miro",
        "airtable",
        "canva",
    };

    private static readonly HashSet<string> ChatProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord",
        "slack",
        "ms-teams",
        "teams",
        "teams2",
        "skype",
        "webex",
        "zoom",
        "signal",
        "telegram",
        "whatsapp",
        "mattermost",
        "zulip",
        "element",
    };

    private static readonly HashSet<string> OfficeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "winword",
        "excel",
        "outlook",
        "olk",
        "onenote",
        "onenoteim",
        "powerpnt",
    };

    private static readonly HashSet<string> TerminalWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CASCADIA_HOSTING_WINDOW_CLASS",
        "ConsoleWindowClass",
        "mintty",
        "PuTTY",
    };

    private static readonly HashSet<string> ChromiumOrGeckoHostClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chrome_WidgetWin_0",
        "Chrome_WidgetWin_1",
        "Chrome_WidgetWin_2",
        "Chrome_RenderWidgetHostHWND",
        "CefBrowserWindow",
        "MozillaWindowClass",
        "SunAwtFrame",
    };

    public static WindowsPasteRule Resolve(
        string? processName,
        string? windowClassName = null,
        string? elementClassName = null,
        bool isControlEdit = false,
        bool isFullscreen = false)
    {
        var normalized = NormalizeProcessName(processName);

        if (TerminalProcesses.Contains(normalized) || IsTerminalClass(windowClassName) || IsTerminalClass(elementClassName))
        {
            return new WindowsPasteRule(
                TextTargetKind.Terminal,
                SkipDirectValueWrite: true,
                DirectWriteSkipReason.Terminal,
                PasteKeystrokeKind.ShiftInsert,
                AllowsWindowFallbackWithoutFocusedElement: true,
                MinimumPasteSettleMilliseconds: 300);
        }

        if (OfficeProcesses.Contains(normalized))
        {
            return new WindowsPasteRule(
                TextTargetKind.Office,
                SkipDirectValueWrite: true,
                DirectWriteSkipReason.Office,
                PasteKeystrokeKind.CtrlV,
                AllowsWindowFallbackWithoutFocusedElement: true,
                MinimumPasteSettleMilliseconds: 350);
        }

        if (ChatProcesses.Contains(normalized))
        {
            return new WindowsPasteRule(
                TextTargetKind.Chat,
                SkipDirectValueWrite: true,
                DirectWriteSkipReason.Chat,
                PasteKeystrokeKind.CtrlV,
                AllowsWindowFallbackWithoutFocusedElement: true,
                MinimumPasteSettleMilliseconds: 350);
        }

        if (BrowserProcesses.Contains(normalized))
        {
            return new WindowsPasteRule(
                TextTargetKind.Browser,
                SkipDirectValueWrite: true,
                DirectWriteSkipReason.Browser,
                PasteKeystrokeKind.CtrlV,
                AllowsWindowFallbackWithoutFocusedElement: true,
                MinimumPasteSettleMilliseconds: 350);
        }

        if (ElectronAndChromiumAppProcesses.Contains(normalized) ||
            IsChromiumOrGeckoHostClass(windowClassName) ||
            IsChromiumOrGeckoHostClass(elementClassName))
        {
            return new WindowsPasteRule(
                TextTargetKind.Unknown,
                SkipDirectValueWrite: true,
                DirectWriteSkipReason.ElectronOrChromiumHost,
                PasteKeystrokeKind.CtrlV,
                AllowsWindowFallbackWithoutFocusedElement: true,
                MinimumPasteSettleMilliseconds: 350);
        }

        if (isControlEdit)
        {
            return new WindowsPasteRule(
                TextTargetKind.StandardEdit,
                SkipDirectValueWrite: false,
                DirectWriteSkipReason.None,
                PasteKeystrokeKind.CtrlV,
                AllowsWindowFallbackWithoutFocusedElement: true,
                MinimumPasteSettleMilliseconds: 200);
        }

        if (isFullscreen)
        {
            return new WindowsPasteRule(
                TextTargetKind.Game,
                SkipDirectValueWrite: true,
                DirectWriteSkipReason.None,
                PasteKeystrokeKind.CtrlV,
                AllowsWindowFallbackWithoutFocusedElement: false,
                MinimumPasteSettleMilliseconds: 200);
        }

        return new WindowsPasteRule(
            TextTargetKind.Unknown,
            SkipDirectValueWrite: false,
            DirectWriteSkipReason.None,
            PasteKeystrokeKind.CtrlV,
            AllowsWindowFallbackWithoutFocusedElement: true,
            MinimumPasteSettleMilliseconds: 250);
    }

    public static PasteKeystrokeKind KeystrokeForTargetKind(TextTargetKind targetKind) =>
        targetKind == TextTargetKind.Terminal
            ? PasteKeystrokeKind.ShiftInsert
            : PasteKeystrokeKind.CtrlV;

    internal static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        var trimmed = processName.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    internal static bool IsTerminalClass(string? className) =>
        !string.IsNullOrWhiteSpace(className) &&
        TerminalWindowClasses.Contains(className.Trim());

    internal static bool IsChromiumOrGeckoHostClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        var trimmed = className.Trim();
        return ChromiumOrGeckoHostClasses.Contains(trimmed) ||
            trimmed.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase);
    }
}
