using System;
using System.Collections.Generic;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;

namespace EnviousWispr.Services.Input;

/// <summary>
/// Resolves terminal caret context by combining Gate 1 process descendancy and Gate 2 screen/caret
/// parsing while isolating cell-grid trailing padding from UIA Right.
/// </summary>
internal static class WindowsTerminalContextResolver
{
    internal sealed record TerminalUiSnapshot(
        string? CaretLeft,
        string? CaretSelected,
        string? CaretRight,
        bool CaretNoSelection,
        string? ScreenTail);

    internal static CaretContext Resolve(
        TargetWindowId target,
        string focusedElementId,
        uint? focusProcessId,
        TerminalUiSnapshot ui,
        Func<uint, uint?, IReadOnlyList<RunningTerminalCli>>? findRunningClis = null)
    {
        ArgumentNullException.ThrowIfNull(ui);

        if (ui.CaretNoSelection)
        {
            return WindowsTextDeliveryPolicy.EmptyContext(target, focusedElementId, TextTargetKind.Terminal);
        }

        var runningClis = (findRunningClis ?? WindowsTerminalProcessScanner.ScanLiveDescendantClis)(
            target.ProcessId,
            focusProcessId);
        if (runningClis.Count == 0)
        {
            return WindowsTextDeliveryPolicy.EmptyContext(target, focusedElementId, TextTargetKind.Terminal);
        }

        var screenTail = !string.IsNullOrEmpty(ui.ScreenTail)
            ? ui.ScreenTail
            : BuildFallbackScreenTail(ui);
        var located = WindowsTerminalScreenParser.Locate(screenTail);
        if (located is null)
        {
            return WindowsTextDeliveryPolicy.EmptyContext(target, focusedElementId, TextTargetKind.Terminal);
        }

        if (!TryExtractPromptLeft(
                ui.CaretLeft,
                ui.CaretRight,
                located,
                out var resolvedLeft,
                out var leftReachedStart))
        {
            return WindowsTextDeliveryPolicy.EmptyContext(target, focusedElementId, TextTargetKind.Terminal);
        }

        // Terminal UIA grids pad Right with trailing U+0020 cells and Codex placeholder text;
        // always force Right = "" so trailing spaces are never suppressed and spinner/grid updates
        // cannot break CaretUnchanged checks.
        return new CaretContext(
            Target: target,
            FocusedElementId: focusedElementId,
            TargetKind: TextTargetKind.Terminal,
            Left: resolvedLeft,
            Selection: ui.CaretSelected ?? string.Empty,
            Right: string.Empty,
            LeftReachedDocumentStart: leftReachedStart,
            RightReachedDocumentEnd: true,
            HasTextContext: true,
            SupportsDirectValueWrite: false,
            DirectValueWriteAtEnd: false,
            IsScreenDerived: true,
            IsUrlBarField: false);
    }

    private static string BuildFallbackScreenTail(TerminalUiSnapshot ui) =>
        (ui.CaretLeft ?? string.Empty) +
        (ui.CaretSelected ?? string.Empty) +
        (ui.CaretRight ?? string.Empty);

    private static bool TryExtractPromptLeft(
        string? caretLeft,
        string? caretRight,
        TerminalLocatedInput located,
        out string resolvedLeft,
        out bool leftReachedStart)
    {
        resolvedLeft = string.Empty;
        leftReachedStart = false;

        if (caretLeft is null)
        {
            if (WindowsTerminalScreenParser.IsCodexPlaceholder(located.InputLine) ||
                string.IsNullOrWhiteSpace(located.InputLine))
            {
                return false;
            }

            resolvedLeft = located.InputLine;
            leftReachedStart = !located.LeftWasCut;
            return true;
        }

        var normalizedLeft = caretLeft.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lastNewline = normalizedLeft.LastIndexOf('\n');
        var activeLeftRow = lastNewline >= 0
            ? normalizedLeft[(lastNewline + 1)..]
            : normalizedLeft;

        var marker = WindowsTerminalScreenParser.MarkerFor(located.Cli);
        var opener = WindowsTerminalScreenParser.OpeningCharacter(activeLeftRow);

        if (opener == marker)
        {
            var stripped = WindowsTerminalScreenParser.StripLeadingMarker(activeLeftRow, marker);
            if (string.IsNullOrWhiteSpace(stripped))
            {
                // Empty prompt (or caret sitting directly at "› " with Codex grey placeholder on Right).
                return false;
            }

            if (located.Cli == SupportedWindowsTerminalCli.Codex &&
                WindowsTerminalScreenParser.IsCodexPlaceholder(stripped))
            {
                return false;
            }

            resolvedLeft = stripped;
            leftReachedStart = true;
            return true;
        }

        if (located.LeftWasCut)
        {
            var strippedContinuation = WindowsTerminalScreenParser.StripContinuationGutter(activeLeftRow);
            if (string.IsNullOrWhiteSpace(strippedContinuation))
            {
                return false;
            }

            resolvedLeft = strippedContinuation;
            leftReachedStart = false;
            return true;
        }

        if (located.Cli == SupportedWindowsTerminalCli.Codex &&
            caretRight is not null &&
            WindowsTerminalScreenParser.IsCodexPlaceholder(caretRight.Trim()))
        {
            return false;
        }

        return false;
    }
}
