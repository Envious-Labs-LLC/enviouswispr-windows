using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Services.Input;
using Xunit;

namespace EnviousWispr.Architecture.Tests;

public sealed class WindowsTerminalContextResolverTests
{
    private static readonly TargetWindowId TerminalWindow =
        new(0x1234, 100, "WindowsTerminal");

    private static IReadOnlyList<RunningTerminalCli> CodexDescendant(uint targetPid, uint? focusPid) =>
        [new RunningTerminalCli(103, SupportedWindowsTerminalCli.Codex)];

    private static IReadOnlyList<RunningTerminalCli> ClaudeDescendant(uint targetPid, uint? focusPid) =>
        [new RunningTerminalCli(104, SupportedWindowsTerminalCli.ClaudeCode)];

    private static IReadOnlyList<RunningTerminalCli> NoClis(uint targetPid, uint? focusPid) =>
        Array.Empty<RunningTerminalCli>();

    [Fact]
    public void CodexTerminalResolvesPromptPrefixStrippedAndForcesRightEmptyToIgnoreGridPadding()
    {
        var padding = new string(' ', 80);
        var ui = new WindowsTerminalContextResolver.TerminalUiSnapshot(
            CaretLeft: "previous line\r\n\u203A Can you ",
            CaretSelected: string.Empty,
            CaretRight: padding,
            CaretNoSelection: false,
            ScreenTail: "previous line\r\n\u203A Can you " + padding + "\r\n\r\n  gpt-5.4 · 90% left");

        var context = WindowsTerminalContextResolver.Resolve(
            TerminalWindow,
            focusedElementId: "term-focus",
            focusProcessId: 100,
            ui,
            CodexDescendant);

        Assert.True(context.HasTextContext);
        Assert.Equal("Can you ", context.Left);
        Assert.Equal(string.Empty, context.Right);
        Assert.True(context.IsScreenDerived);
        Assert.True(context.LeftReachedDocumentStart);
        Assert.False(context.SupportsDirectValueWrite);
        Assert.Equal(TextTargetKind.Terminal, context.TargetKind);
    }

    [Fact]
    public void CodexGreyPlaceholderReturnsUnavailableTextContext()
    {
        var ui = new WindowsTerminalContextResolver.TerminalUiSnapshot(
            CaretLeft: "\u203A ",
            CaretSelected: string.Empty,
            CaretRight: "Ask Codex to do anything" + new string(' ', 40),
            CaretNoSelection: false,
            ScreenTail: "\u203A Ask Codex to do anything" + new string(' ', 40) + "\r\n\r\n  90% left");

        var context = WindowsTerminalContextResolver.Resolve(
            TerminalWindow,
            focusedElementId: "term-focus",
            focusProcessId: 100,
            ui,
            CodexDescendant);

        Assert.False(context.HasTextContext);
        Assert.Equal(string.Empty, context.Left);
        Assert.Equal(string.Empty, context.Right);
        Assert.True(context.IsScreenDerived);
    }

    [Fact]
    public void BareShellWithoutDescendantCliRefusesEvenWithScreenLookalike()
    {
        var ui = new WindowsTerminalContextResolver.TerminalUiSnapshot(
            CaretLeft: "\u203A spoofed ",
            CaretSelected: string.Empty,
            CaretRight: string.Empty,
            CaretNoSelection: false,
            ScreenTail: "\u203A spoofed ");

        var context = WindowsTerminalContextResolver.Resolve(
            TerminalWindow,
            focusedElementId: "term-focus",
            focusProcessId: 100,
            ui,
            NoClis);

        Assert.False(context.HasTextContext);
        Assert.Equal(string.Empty, context.Left);
    }

    [Fact]
    public void BarePowerShellScreenPromptRefusesEvenWhenAnotherTabRunsCodex()
    {
        var ui = new WindowsTerminalContextResolver.TerminalUiSnapshot(
            CaretLeft: "PS C:\\Work\\repo> ",
            CaretSelected: string.Empty,
            CaretRight: new string(' ', 40),
            CaretNoSelection: false,
            ScreenTail: "PS C:\\Work\\repo> " + new string(' ', 40));

        var context = WindowsTerminalContextResolver.Resolve(
            TerminalWindow,
            focusedElementId: "term-focus",
            focusProcessId: 100,
            ui,
            CodexDescendant);

        Assert.False(context.HasTextContext);
        Assert.Equal(string.Empty, context.Left);
    }

    [Fact]
    public void ClaudeCodeBoxUsesScreenTailForClosingBorderAndCaretLeftForExactTail()
    {
        var ui = new WindowsTerminalContextResolver.TerminalUiSnapshot(
            CaretLeft: "────────────────────\r\n❯ Can you ",
            CaretSelected: string.Empty,
            CaretRight: new string(' ', 20) + "\r\n────────────────────",
            CaretNoSelection: false,
            ScreenTail: "────────────────────\r\n❯ Can you           \r\n────────────────────\r\n? for shortcuts");

        var context = WindowsTerminalContextResolver.Resolve(
            TerminalWindow,
            focusedElementId: "term-focus",
            focusProcessId: 100,
            ui,
            ClaudeDescendant);

        Assert.True(context.HasTextContext);
        Assert.Equal("Can you ", context.Left);
        Assert.Equal(string.Empty, context.Right);
        Assert.True(context.IsScreenDerived);
    }

    [Fact]
    public void NoSelectionReturnsUnavailableTerminalContext()
    {
        var ui = new WindowsTerminalContextResolver.TerminalUiSnapshot(
            CaretLeft: null,
            CaretSelected: null,
            CaretRight: null,
            CaretNoSelection: true,
            ScreenTail: null);

        var context = WindowsTerminalContextResolver.Resolve(
            TerminalWindow,
            focusedElementId: "term-focus",
            focusProcessId: 100,
            ui,
            CodexDescendant);

        Assert.False(context.HasTextContext);
        Assert.Equal(string.Empty, context.Right);
    }

    [Fact]
    public void WindowsElsLanguageDetectorHandlesEnglishEmptyAndDisposedSafely()
    {
        using var detector = new WindowsElsLanguageDetector();
        var tags = detector.DetectBaseTags("Complete your tests before pushing.");
        Assert.Contains("en", tags);

        Assert.Empty(detector.DetectBaseTags("   "));

        detector.Dispose();
        Assert.Empty(detector.DetectBaseTags("Complete your tests before pushing."));
    }
}
