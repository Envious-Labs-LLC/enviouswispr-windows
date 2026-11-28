using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace EnviousWispr.Services.Input;

/// <summary>
/// Supported terminal-hosted CLI tools recognized by the screen parser and context resolver.
/// </summary>
internal enum SupportedWindowsTerminalCli
{
    ClaudeCode,
    Codex,
    GeminiCli,
}

internal sealed record TerminalProcessEntry(
    uint ProcessId,
    uint ParentProcessId,
    string ExecutableName,
    string? CommandLine = null);

internal sealed record RunningTerminalCli(
    uint ProcessId,
    SupportedWindowsTerminalCli Cli);

internal sealed record TerminalProcessScanResult(
    bool Available,
    IReadOnlyList<TerminalProcessEntry> Processes)
{
    public static TerminalProcessScanResult Unavailable { get; } =
        new(false, Array.Empty<TerminalProcessEntry>());
}

/// <summary>
/// Gate 1 process-tree verifier for Windows terminals using CreateToolhelp32Snapshot and bounded
/// NtQueryInformationProcess command-line reads (no WMI / System.Management).
/// </summary>
internal static class WindowsTerminalProcessScanner
{
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int MaxParentWalkHops = 128;
    private const int MaxCommandLineBufferBytes = 65536;

    private static readonly Dictionary<string, SupportedWindowsTerminalCli> DirectCliNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = SupportedWindowsTerminalCli.ClaudeCode,
            ["codex"] = SupportedWindowsTerminalCli.Codex,
            ["gemini"] = SupportedWindowsTerminalCli.GeminiCli,
        };

    private static readonly HashSet<string> ScriptRuntimes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "node",
            "bun",
            "deno",
        };

    private static readonly HashSet<string> ConsoleShellNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "cmd",
            "powershell",
            "pwsh",
            "bash",
            "zsh",
            "wsl",
            "wslhost",
            "ubuntu",
            "fish",
            "nu",
            "elvish",
        };

    private static readonly HashSet<string> GenericScriptEntryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "cli",
            "index",
            "main",
            "bin",
            "run",
        };

    internal static string NormalizeExecutableBaseName(string? executableNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(executableNameOrPath))
        {
            return string.Empty;
        }

        var trimmed = executableNameOrPath.Trim().Trim('"');
        var fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        foreach (var suffix in new[] { ".exe", ".cmd", ".bat", ".com" })
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                fileName = fileName[..^suffix.Length];
                break;
            }
        }

        return fileName.ToLowerInvariant();
    }

    internal static SupportedWindowsTerminalCli? IdentifyCli(
        string? executableNameOrPath,
        string? commandLine)
    {
        var baseName = NormalizeExecutableBaseName(executableNameOrPath);
        if (DirectCliNames.TryGetValue(baseName, out var directCli))
        {
            return directCli;
        }

        if (!ScriptRuntimes.Contains(baseName))
        {
            return null;
        }

        return TryIdentifyHostedScriptCli(commandLine);
    }

    internal static SupportedWindowsTerminalCli? TryIdentifyHostedScriptCli(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var args = TokenizeCommandLine(commandLine);
        if (args.Count < 2)
        {
            return null;
        }

        // Skip argv[0] (the runtime executable itself) and inspect only the target script argument.
        var skipNext = false;
        for (var i = 1; i < args.Count; i++)
        {
            var token = args[i];
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (skipNext)
            {
                skipNext = false;
                continue;
            }

            if (token.StartsWith('-'))
            {
                if (token is "-e" or "--eval" or "-p" or "--print" or "-c" or "--check")
                {
                    return null;
                }

                if (token is "-r" or "--require" or "--import" or "--loader" or "--env-file")
                {
                    skipNext = true;
                }

                continue;
            }

            return IdentifyScriptPath(token);
        }

        return null;
    }

    private static SupportedWindowsTerminalCli? IdentifyScriptPath(string scriptToken)
    {
        var fileName = Path.GetFileName(scriptToken.Trim().Trim('"'));
        var baseName = fileName;
        foreach (var suffix in new[] { ".mjs", ".cjs", ".js", ".mts", ".cts", ".ts", ".cmd", ".exe" })
        {
            if (baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName[..^suffix.Length];
                break;
            }
        }

        if (DirectCliNames.TryGetValue(baseName, out var directCli))
        {
            return directCli;
        }

        if (baseName.Equals("claude-code", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedWindowsTerminalCli.ClaudeCode;
        }

        if (baseName.Equals("gemini-cli", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedWindowsTerminalCli.GeminiCli;
        }

        if (!GenericScriptEntryNames.Contains(baseName))
        {
            return null;
        }

        var normalizedPath = scriptToken.Replace('\\', '/');
        if (ContainsPathSegment(normalizedPath, "@openai/codex") ||
            ContainsPathSegment(normalizedPath, "codex"))
        {
            return SupportedWindowsTerminalCli.Codex;
        }

        if (ContainsPathSegment(normalizedPath, "@anthropic-ai/claude-code") ||
            ContainsPathSegment(normalizedPath, "claude-code") ||
            ContainsPathSegment(normalizedPath, "claude"))
        {
            return SupportedWindowsTerminalCli.ClaudeCode;
        }

        if (ContainsPathSegment(normalizedPath, "@google/gemini-cli") ||
            ContainsPathSegment(normalizedPath, "gemini-cli"))
        {
            return SupportedWindowsTerminalCli.GeminiCli;
        }

        return null;
    }

    private static bool ContainsPathSegment(string normalizedPath, string segment)
    {
        var needle = "/" + segment + "/";
        return normalizedPath.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(segment + "/", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> TokenizeCommandLine(string commandLine)
    {
        var tokens = new List<string>();
        var current = new List<char>(commandLine.Length);
        var inQuotes = false;
        var hasToken = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var ch = commandLine[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (hasToken)
                {
                    tokens.Add(new string(current.ToArray()));
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Add(ch);
            hasToken = true;
        }

        if (hasToken)
        {
            tokens.Add(new string(current.ToArray()));
        }

        return tokens;
    }

    internal static IReadOnlyList<RunningTerminalCli> FindDescendantSupportedClis(
        IReadOnlyList<TerminalProcessEntry> processes,
        uint targetWindowPid,
        uint? focusPid = null,
        Func<uint, string?>? readCommandLine = null)
    {
        if (processes.Count == 0 || (targetWindowPid == 0 && (!focusPid.HasValue || focusPid.Value == 0)))
        {
            return Array.Empty<RunningTerminalCli>();
        }

        var byPid = new Dictionary<uint, TerminalProcessEntry>(processes.Count);
        foreach (var proc in processes)
        {
            if (proc.ProcessId != 0)
            {
                byPid[proc.ProcessId] = proc;
            }
        }

        var rootPids = BuildRootAncestorPids(byPid, targetWindowPid, focusPid);
        if (rootPids.Count == 0)
        {
            return Array.Empty<RunningTerminalCli>();
        }

        var results = new List<RunningTerminalCli>();
        foreach (var proc in processes)
        {
            if (proc.ProcessId == 0 || rootPids.Contains(proc.ProcessId))
            {
                continue;
            }

            if (!IsStrictDescendant(proc, rootPids, byPid))
            {
                continue;
            }

            var baseName = NormalizeExecutableBaseName(proc.ExecutableName);
            if (DirectCliNames.TryGetValue(baseName, out var directCli))
            {
                results.Add(new RunningTerminalCli(proc.ProcessId, directCli));
                continue;
            }

            if (!ScriptRuntimes.Contains(baseName))
            {
                continue;
            }

            var cmdLine = proc.CommandLine ?? readCommandLine?.Invoke(proc.ProcessId);
            var scriptCli = TryIdentifyHostedScriptCli(cmdLine);
            if (scriptCli.HasValue)
            {
                results.Add(new RunningTerminalCli(proc.ProcessId, scriptCli.Value));
            }
        }

        results.Sort(static (left, right) => left.ProcessId.CompareTo(right.ProcessId));
        return results;
    }

    internal static TerminalProcessScanResult ScanLiveProcesses()
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == 0 || snapshot == new nint(-1))
        {
            return TerminalProcessScanResult.Unavailable;
        }

        try
        {
            var entries = new List<TerminalProcessEntry>(128);
            var entry = new PROCESSENTRY32W
            {
                dwSize = checked((uint)Marshal.SizeOf<PROCESSENTRY32W>()),
            };

            if (!NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                return TerminalProcessScanResult.Unavailable;
            }

            do
            {
                entries.Add(new TerminalProcessEntry(
                    entry.th32ProcessID,
                    entry.th32ParentProcessID,
                    entry.szExeFile ?? string.Empty));
            }
            while (NativeMethods.Process32NextW(snapshot, ref entry));

            return new TerminalProcessScanResult(true, entries);
        }
        finally
        {
            _ = NativeMethods.CloseHandle(snapshot);
        }
    }

    internal static IReadOnlyList<RunningTerminalCli> ScanLiveDescendantClis(
        uint targetWindowPid,
        uint? focusPid = null)
    {
        var scan = ScanLiveProcesses();
        if (!scan.Available)
        {
            return Array.Empty<RunningTerminalCli>();
        }

        return FindDescendantSupportedClis(
            scan.Processes,
            targetWindowPid,
            focusPid,
            TryReadProcessCommandLine);
    }

    internal static string? TryReadProcessCommandLine(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, bInheritHandle: false, processId);
        if (handle == 0 || handle == new nint(-1))
        {
            return null;
        }

        try
        {
            var bufferSize = 2048;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    var status = NativeMethods.NtQueryInformationProcess(
                        handle,
                        ProcessCommandLineInformation,
                        buffer,
                        bufferSize,
                        out var returnLength);

                    if (status >= 0)
                    {
                        return ReadBoundedUnicodeString(buffer, bufferSize);
                    }

                    if (attempt == 0 &&
                        returnLength > bufferSize &&
                        returnLength <= MaxCommandLineBufferBytes)
                    {
                        bufferSize = returnLength;
                        continue;
                    }

                    return null;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OverflowException)
        {
            return null;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(handle);
        }
    }

    private static string? ReadBoundedUnicodeString(nint buffer, int bufferSize)
    {
        var headerSize = Marshal.SizeOf<UNICODE_STRING>();
        if (buffer == 0 || bufferSize < headerSize)
        {
            return null;
        }

        var unicode = Marshal.PtrToStructure<UNICODE_STRING>(buffer);
        if (unicode.Length == 0 || (unicode.Length % 2) != 0 || unicode.Buffer == 0)
        {
            return string.Empty;
        }

        var baseAddress = buffer.ToInt64();
        var dataAddress = unicode.Buffer.ToInt64();
        var endAddress = dataAddress + unicode.Length;
        if (dataAddress < baseAddress + headerSize || endAddress > baseAddress + bufferSize)
        {
            return null;
        }

        return Marshal.PtrToStringUni(unicode.Buffer, unicode.Length / 2);
    }

    private static HashSet<uint> BuildRootAncestorPids(
        Dictionary<uint, TerminalProcessEntry> byPid,
        uint targetWindowPid,
        uint? focusPid)
    {
        var roots = new HashSet<uint>();
        if (targetWindowPid != 0)
        {
            roots.Add(targetWindowPid);
            AddConsoleHostShellParentIfApplicable(byPid, targetWindowPid, roots);
        }

        if (focusPid is > 0)
        {
            roots.Add(focusPid.Value);
            AddConsoleHostShellParentIfApplicable(byPid, focusPid.Value, roots);
        }

        return roots;
    }

    private static void AddConsoleHostShellParentIfApplicable(
        Dictionary<uint, TerminalProcessEntry> byPid,
        uint pid,
        HashSet<uint> roots)
    {
        if (!byPid.TryGetValue(pid, out var hostEntry))
        {
            return;
        }

        var hostName = NormalizeExecutableBaseName(hostEntry.ExecutableName);
        if (hostName is not ("conhost" or "openconsole"))
        {
            return;
        }

        if (hostEntry.ParentProcessId != 0 &&
            byPid.TryGetValue(hostEntry.ParentProcessId, out var parentEntry) &&
            ConsoleShellNames.Contains(NormalizeExecutableBaseName(parentEntry.ExecutableName)))
        {
            roots.Add(parentEntry.ProcessId);
        }
    }

    private static bool IsStrictDescendant(
        TerminalProcessEntry candidate,
        HashSet<uint> rootPids,
        Dictionary<uint, TerminalProcessEntry> byPid)
    {
        var visited = new HashSet<uint> { candidate.ProcessId };
        var cursorPid = candidate.ParentProcessId;

        for (var hop = 0; hop < MaxParentWalkHops && cursorPid != 0; hop++)
        {
            if (rootPids.Contains(cursorPid))
            {
                return true;
            }

            if (!visited.Add(cursorPid) || !byPid.TryGetValue(cursorPid, out var parent))
            {
                return false;
            }

            cursorPid = parent.ParentProcessId;
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32FirstW(nint hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32NextW(nint hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern nint OpenProcess(
            uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
            uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);

        [DllImport("ntdll.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int NtQueryInformationProcess(
            nint processHandle,
            int processInformationClass,
            nint processInformation,
            int processInformationLength,
            out int returnLength);
    }
}
