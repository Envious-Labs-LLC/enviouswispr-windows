using System.Diagnostics;
using System.Text;

namespace EnviousWispr.Core.Input;

/// <summary>
/// Resolves a human-friendly application name from a target window's process identity.
/// </summary>
/// <remarks>
/// PRIVACY BOUNDARY: ONLY the process image name is ever inspected. This resolver NEVER reads
/// <see cref="Process.MainWindowTitle"/>, UI Automation accessibility names or values, URLs, or
/// surrounding screen or document text. The name is mapped through a curated table with a clean
/// title-case fallback, sanitized against control characters and tag delimiters, and capped at 64 chars.
/// </remarks>
public static class TargetAppNameResolver
{
    public const int MaximumNameLength = 64;

    private static readonly Dictionary<string, string> CuratedFriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winword"] = "Microsoft Word",
        ["excel"] = "Microsoft Excel",
        ["powerpnt"] = "Microsoft PowerPoint",
        ["outlook"] = "Microsoft Outlook",
        ["olk"] = "Microsoft Outlook",
        ["onenote"] = "Microsoft OneNote",
        ["msedge"] = "Microsoft Edge",
        ["chrome"] = "Google Chrome",
        ["firefox"] = "Mozilla Firefox",
        ["brave"] = "Brave",
        ["code"] = "Visual Studio Code",
        ["devenv"] = "Visual Studio",
        ["windowsterminal"] = "Windows Terminal",
        ["cmd"] = "Command Prompt",
        ["powershell"] = "PowerShell",
        ["pwsh"] = "PowerShell",
        ["slack"] = "Slack",
        ["ms-teams"] = "Microsoft Teams",
        ["teams"] = "Microsoft Teams",
        ["notepad"] = "Notepad",
        ["notepad++"] = "Notepad++",
        ["explorer"] = "File Explorer",
        ["mspaint"] = "Paint",
        ["discord"] = "Discord",
        ["spotify"] = "Spotify",
        ["obsidian"] = "Obsidian",
        ["notion"] = "Notion",
        ["telegram"] = "Telegram",
        ["whatsapp"] = "WhatsApp",
        ["thunderbird"] = "Thunderbird",
        ["sublime_text"] = "Sublime Text",
        ["cursor"] = "Cursor",
        ["rider64"] = "JetBrains Rider",
        ["idea64"] = "IntelliJ IDEA",
        ["clion64"] = "CLion",
        ["pycharm64"] = "PyCharm",
        ["webstorm64"] = "WebStorm",
    };

    /// <summary>
    /// Resolves the friendly application name for a foreground target window.
    /// </summary>
    public static string? Resolve(TargetWindowId? target)
    {
        if (target is null || !target.Value.IsValid)
        {
            return null;
        }

        var processName = target.Value.ProcessName;
        if (string.IsNullOrWhiteSpace(processName) && target.Value.ProcessId != 0)
        {
            processName = TryGetProcessName(target.Value.ProcessId);
        }

        return MapFriendlyName(processName);
    }

    /// <summary>
    /// Maps a raw process name (e.g. "winword" or "code.exe") to its curated friendly name or fallback title-cased name.
    /// </summary>
    public static string? MapFriendlyName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var key = processName.Trim();
        if (key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            key = key[..^4];
        }

        if (CuratedFriendlyNames.TryGetValue(key, out var friendly))
        {
            return Sanitize(friendly);
        }

        var fallback = FormatFallbackTitleCase(key);
        return Sanitize(fallback);
    }

    /// <summary>
    /// Formats an unknown process name with clean title-casing.
    /// </summary>
    public static string FormatFallbackTitleCase(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        var name = rawName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        var hasUpper = name.Any(char.IsUpper);
        var hasLower = name.Any(char.IsLower);
        var replaced = name.Replace('_', ' ');

        var parts = replaced.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var subParts = part.Split('-');
            for (var j = 0; j < subParts.Length; j++)
            {
                var sub = subParts[j];
                if (sub.Length > 0)
                {
                    if (hasUpper && hasLower)
                    {
                        subParts[j] = char.ToUpperInvariant(sub[0]) + sub[1..];
                    }
                    else
                    {
                        subParts[j] = char.ToUpperInvariant(sub[0]) + (sub.Length > 1 ? sub[1..].ToLowerInvariant() : string.Empty);
                    }
                }
            }

            parts[i] = string.Join('-', subParts);
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Strips control characters, newlines, and tag markers, and bounds length to 64 characters.
    /// </summary>
    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsControl(c) || c == '\r' || c == '\n' || c == '<' || c == '>')
            {
                continue;
            }

            sb.Append(c);
        }

        var cleaned = sb.ToString().Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        if (cleaned.Length > MaximumNameLength)
        {
            cleaned = cleaned[..MaximumNameLength].TrimEnd();
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? TryGetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return null;
        }
    }
}
