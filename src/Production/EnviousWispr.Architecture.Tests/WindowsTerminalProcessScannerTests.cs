using EnviousWispr.Services.Input;
using Xunit;

namespace EnviousWispr.Architecture.Tests;

public sealed class WindowsTerminalProcessScannerTests
{
    [Fact]
    public void StrictDescendancyUnderWindowsTerminalMatchesSupportedClisAndRejectsGlobalImpostors()
    {
        var processes = new TerminalProcessEntry[]
        {
            new(10, 1, "explorer.exe"),
            // Desktop Electron / ChatGPT hosts that must NEVER authorize terminal insertion:
            new(11, 10, "claude.exe"),
            new(12, 1, "ChatGPT.exe"),
            new(13, 12, "codex.exe"),
            // Target terminal tree:
            new(100, 10, "WindowsTerminal.exe"),
            new(101, 100, "OpenConsole.exe"),
            new(102, 100, "pwsh.exe"),
            new(103, 102, "node.exe", "\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\ProgramData\\npm\\node_modules\\@openai\\codex\\bin\\codex.js\""),
            new(104, 102, "claude.exe"),
        };

        var matches = WindowsTerminalProcessScanner.FindDescendantSupportedClis(
            processes,
            targetWindowPid: 100);

        Assert.Equal(2, matches.Count);
        Assert.Equal(new RunningTerminalCli(103, SupportedWindowsTerminalCli.Codex), matches[0]);
        Assert.Equal(new RunningTerminalCli(104, SupportedWindowsTerminalCli.ClaudeCode), matches[1]);
    }

    [Fact]
    public void BareShellUnderTerminalReturnsNoSupportedClisEvenWhenDesktopHostsAreOpen()
    {
        var processes = new TerminalProcessEntry[]
        {
            new(10, 1, "explorer.exe"),
            new(11, 10, "claude.exe"),
            new(12, 1, "ChatGPT.exe"),
            new(13, 12, "codex.exe"),
            new(200, 10, "WindowsTerminal.exe"),
            new(201, 200, "pwsh.exe"),
        };

        var matches = WindowsTerminalProcessScanner.FindDescendantSupportedClis(
            processes,
            targetWindowPid: 200);

        Assert.Empty(matches);
    }

    [Fact]
    public void ConhostShellParentResolutionAllowsSiblingCliUnderConsoleShellOnly()
    {
        var validConsole = new TerminalProcessEntry[]
        {
            new(10, 1, "explorer.exe"),
            new(11, 10, "claude.exe"),
            new(300, 10, "cmd.exe"),
            new(301, 300, "conhost.exe"),
            new(302, 300, "gemini.exe"),
        };

        var validMatches = WindowsTerminalProcessScanner.FindDescendantSupportedClis(
            validConsole,
            targetWindowPid: 301);

        Assert.Single(validMatches);
        Assert.Equal(new RunningTerminalCli(302, SupportedWindowsTerminalCli.GeminiCli), validMatches[0]);

        // If conhost was spawned by a non-shell desktop process, never widen to sibling apps.
        var desktopConhost = new TerminalProcessEntry[]
        {
            new(10, 1, "explorer.exe"),
            new(400, 10, "conhost.exe"),
            new(401, 10, "claude.exe"),
        };

        Assert.Empty(WindowsTerminalProcessScanner.FindDescendantSupportedClis(
            desktopConhost,
            targetWindowPid: 400));
    }

    [Theory]
    [InlineData("codex.exe", null, SupportedWindowsTerminalCli.Codex)]
    [InlineData("claude.exe", null, SupportedWindowsTerminalCli.ClaudeCode)]
    [InlineData("gemini.exe", null, SupportedWindowsTerminalCli.GeminiCli)]
    [InlineData("node.exe", "node --no-warnings C:\\tools\\@openai\\codex\\bin\\codex.js", SupportedWindowsTerminalCli.Codex)]
    [InlineData("node.exe", "\"C:\\node.exe\" C:\\npm\\@anthropic-ai\\claude-code\\cli.js", SupportedWindowsTerminalCli.ClaudeCode)]
    [InlineData("bun.exe", "bun C:\\npm\\@google\\gemini-cli\\dist\\index.js", SupportedWindowsTerminalCli.GeminiCli)]
    [InlineData("node.exe", "node -e \"console.log('codex')\"", null)]
    [InlineData("node.exe", "node C:\\scripts\\editor.js codex", null)]
    [InlineData("vim.exe", "vim codex.js", null)]
    internal void IdentifyCliDistinguishesRealHostsFromUnrelatedScriptsAndFlags(
        string executable,
        string? commandLine,
        SupportedWindowsTerminalCli? expected)
    {
        Assert.Equal(expected, WindowsTerminalProcessScanner.IdentifyCli(executable, commandLine));
    }

    [Fact]
    public void CircularParentIdsDoNotHangTraversal()
    {
        var processes = new TerminalProcessEntry[]
        {
            new(500, 501, "pwsh.exe"),
            new(501, 500, "codex.exe"),
        };

        Assert.Empty(WindowsTerminalProcessScanner.FindDescendantSupportedClis(
            processes,
            targetWindowPid: 999));
    }

    [Fact]
    public void LiveToolhelpSnapshotEnumeratesProcessesWithoutWmi()
    {
        var scan = WindowsTerminalProcessScanner.ScanLiveProcesses();
        Assert.True(scan.Available);
        Assert.NotEmpty(scan.Processes);
    }
}
