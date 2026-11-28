using EnviousWispr.Services.Input;
using Xunit;

namespace EnviousWispr.Architecture.Tests;

public sealed class WindowsTerminalScreenParserTests
{
    [Fact]
    public void CodexPromptPreservesTrailingSpaceAndHandlesStatusBar()
    {
        var located = WindowsTerminalScreenParser.Locate(
            "previous tool output\r\n" +
            "\u203A Can you \r\n" +
            "\r\n" +
            "  gpt-5.4 high · 90% left · C:\\repo");

        Assert.NotNull(located);
        Assert.Equal(SupportedWindowsTerminalCli.Codex, located!.Cli);
        Assert.Equal("Can you ", located.InputLine);
        Assert.False(located.LeftWasCut);
    }

    [Fact]
    public void CodexWrappedContinuationReturnsLastLineWithLeftWasCut()
    {
        var located = WindowsTerminalScreenParser.Locate(
            "\u203A first wrapped line\n" +
            "  second line \n" +
            "\n" +
            "  gpt-5.4 · 80% left");

        Assert.NotNull(located);
        Assert.Equal(SupportedWindowsTerminalCli.Codex, located!.Cli);
        Assert.Equal("second line ", located.InputLine);
        Assert.True(located.LeftWasCut);
    }

    [Theory]
    [InlineData("\u203A Ask Codex to do anything\n\n  90% left")]
    [InlineData("\u203A Ask Codex to do anything...\n\n  90% left")]
    [InlineData("\u203A   \n\n  90% left")]
    [InlineData("\u203A line one\n  \n\n  90% left")]
    [InlineData("\u203A old command\nPS C:\\Users\\dev> ")]
    [InlineData("\u203A old command\nC:\\Users\\dev>")]
    public void CodexRefusesPlaceholderEmptyInputAmbiguousBlankRowAndShellScrollback(string screen)
    {
        Assert.Null(WindowsTerminalScreenParser.Locate(screen));
    }

    [Fact]
    public void ClaudeCodePlainAndTitledBoxesAreLocatedAndTrailingSpacePreserved()
    {
        var plain = WindowsTerminalScreenParser.Locate(
            "────────────────────────────────\n" +
            "❯\u00A0finish the review \n" +
            "────────────────────────────────");

        Assert.NotNull(plain);
        Assert.Equal(SupportedWindowsTerminalCli.ClaudeCode, plain!.Cli);
        Assert.Equal("finish the review ", plain.InputLine);
        Assert.Equal(TerminalBoxOpeningKind.Plain, plain.BoxOpeningKind);
        Assert.False(plain.LeftWasCut);

        var titledSingleSuffix = WindowsTerminalScreenParser.Locate(
            "───────────────────────── fix-ui ─\n" +
            "❯ check now \n" +
            "──────────────────────────────────");

        Assert.NotNull(titledSingleSuffix);
        Assert.Equal(SupportedWindowsTerminalCli.ClaudeCode, titledSingleSuffix!.Cli);
        Assert.Equal("check now ", titledSingleSuffix.InputLine);
        Assert.Equal(TerminalBoxOpeningKind.Titled, titledSingleSuffix.BoxOpeningKind);

        var titledDoubleSuffix = WindowsTerminalScreenParser.Locate(
            "──────────────────────── fix-ui ──\n" +
            "❯ line one\n" +
            "  wrapped tail \n" +
            "──────────────────────────────────");

        Assert.NotNull(titledDoubleSuffix);
        Assert.Equal("wrapped tail ", titledDoubleSuffix!.InputLine);
        Assert.True(titledDoubleSuffix.LeftWasCut);
        Assert.Equal(TerminalBoxOpeningKind.Titled, titledDoubleSuffix.BoxOpeningKind);
    }

    [Theory]
    [InlineData("──── bad ├ title ──\n❯ hello\n───────────────────")]
    [InlineData("────────────────────Title──\n❯ hello\n───────────────────────────")]
    [InlineData("────────────────────────\n❯   \n────────────────────────")]
    [InlineData("────────────────────────\n❯ first\n  \n────────────────────────")]
    [InlineData("────────────────────────\n❯ stale\n────────────────────────\nPS C:\\repo> ")]
    public void ClaudeCodeRefusesInvalidTitlesEmptyBoxAmbiguousBlankRowAndStaleShell(string screen)
    {
        Assert.Null(WindowsTerminalScreenParser.Locate(screen));
    }

    [Fact]
    public void GeminiBlockBoxAcceptsPlainBorderAndRejectsTitledBlockBorder()
    {
        var plain = WindowsTerminalScreenParser.Locate(
            "▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄\n" +
            "> explain this \n" +
            "▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀");

        Assert.NotNull(plain);
        Assert.Equal(SupportedWindowsTerminalCli.GeminiCli, plain!.Cli);
        Assert.Equal("explain this ", plain.InputLine);

        var titledBlock = WindowsTerminalScreenParser.Locate(
            "▄▄▄▄▄▄▄▄▄▄ title ▄▄\n" +
            "> explain this\n" +
            "▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀");

        Assert.Null(titledBlock);
    }

    [Theory]
    [InlineData("PS C:\\Users\\dev> ")]
    [InlineData("C:\\Windows\\System32>")]
    [InlineData("user@host:~$ ")]
    [InlineData("% ")]
    [InlineData("❯ ")]
    public void BareShellPromptsReturnNull(string screen)
    {
        Assert.Null(WindowsTerminalScreenParser.Locate(screen));
    }
}
