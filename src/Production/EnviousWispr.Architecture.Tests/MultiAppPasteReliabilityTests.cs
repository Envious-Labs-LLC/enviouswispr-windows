using System.Windows.Forms;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Input;

namespace EnviousWispr.Architecture.Tests;

[Collection("SystemClipboard")]
public sealed class MultiAppPasteReliabilityTests
{
    [Fact]
    public void UiaPreparationBudgetLatchesExhaustionAndBoundsRemaining()
    {
        var elapsed = TimeSpan.FromMilliseconds(120);
        var budget = new UiaPreparationBudget(TimeSpan.FromMilliseconds(500), () => elapsed);

        Assert.False(budget.IsExhausted);
        Assert.True(budget.TryAdmit());
        Assert.Equal(TimeSpan.FromMilliseconds(380), budget.Remaining);

        elapsed = TimeSpan.FromMilliseconds(500);
        Assert.False(budget.TryAdmit());
        Assert.True(budget.IsExhausted);
        Assert.Equal(TimeSpan.Zero, budget.Remaining);

        // Even if the clock reports a smaller elapsed time later, exhaustion stays latched.
        elapsed = TimeSpan.FromMilliseconds(10);
        Assert.False(budget.TryAdmit());
        Assert.True(budget.IsExhausted);
        Assert.Equal(TimeSpan.Zero, budget.Remaining);
    }

    [Fact]
    public void PasteRevealPatienceGrowsImmediatelyAndShrinksSlowlyAfterWarmUp()
    {
        var patience = new PasteRevealPatience();
        var floor = TimeSpan.FromMilliseconds(200);

        Assert.Equal(floor, patience.GetSettleDelay("msedge.exe", floor));
        Assert.Equal(floor, patience.GetSettleDelay(null, floor));
        Assert.Null(patience.RecordDispatch(null));

        // Upward growth takes effect immediately on the first admitted slow sighting.
        var first = patience.RecordDispatch("msedge.exe");
        var overlapping = patience.RecordDispatch("msedge");
        Assert.NotNull(first);
        Assert.NotNull(overlapping);

        // Stale overlapping dispatch is rejected cleanly.
        Assert.False(patience.RecordSighting(first, 600, floor));
        Assert.Equal(floor, patience.GetSettleDelay("msedge", floor));

        // 0ms reveal sample (first-check confirmation inside existing settle window) never inflates above floor.
        var fast = patience.RecordDispatch("msedge");
        Assert.True(patience.RecordSighting(fast, 0, floor));
        Assert.Equal(floor, patience.GetSettleDelay("msedge", floor));

        // Late reveal (500ms * 1.5 = 750ms) is adopted at once.
        var slow = patience.RecordDispatch("msedge");
        Assert.True(patience.RecordSighting(slow, 500, floor));
        Assert.Equal(TimeSpan.FromMilliseconds(750), patience.GetSettleDelay("msedge.exe", floor));

        // Samples 2..9 must NOT lower patience during warm-up (WarmUpSamples = 10; fast + slow already count 2).
        for (var i = 3; i <= 9; i++)
        {
            var token = patience.RecordDispatch("msedge");
            Assert.True(patience.RecordSighting(token, 0, floor));
            Assert.Equal(TimeSpan.FromMilliseconds(750), patience.GetSettleDelay("msedge", floor));
        }

        // Sample 10 crosses WarmUpSamples and shrinks by at most 10% (750 * 0.9 = 675ms).
        var tenth = patience.RecordDispatch("msedge");
        Assert.True(patience.RecordSighting(tenth, 0, floor));
        Assert.Equal(TimeSpan.FromMilliseconds(675), patience.GetSettleDelay("msedge", floor));

        // Very slow reveal clamps cleanly to CapMilliseconds (1200ms).
        var capToken = patience.RecordDispatch("msedge");
        Assert.True(patience.RecordSighting(capToken, 3_000, floor));
        Assert.Equal(TimeSpan.FromMilliseconds(PasteRevealPatience.CapMilliseconds), patience.GetSettleDelay("msedge", floor));
    }

    [Theory]
    [InlineData("notepad", "Notepad", "Edit", true, 200)]
    [InlineData("pwsh", "ConsoleWindowClass", null, false, 300)]
    [InlineData("ghostty", null, null, false, 300)]
    [InlineData("warp", null, null, false, 300)]
    [InlineData("msedge", "Chrome_WidgetWin_1", null, false, 350)]
    [InlineData("Code", "Chrome_WidgetWin_1", "Chrome_RenderWidgetHostHWND", true, 350)]
    [InlineData("rider64", "SunAwtFrame", null, false, 350)]
    [InlineData("zed", null, null, false, 350)]
    [InlineData("zoom", null, null, false, 350)]
    [InlineData("onenoteim", null, null, false, 350)]
    public void WindowsPastePolicyAssignsAppClassSettleAndExpandedTargets(
        string processName,
        string? windowClass,
        string? elementClass,
        bool isEdit,
        int expectedSettleMs)
    {
        var rule = WindowsPastePolicy.Resolve(processName, windowClass, elementClass, isControlEdit: isEdit);
        Assert.Equal(expectedSettleMs, rule.MinimumPasteSettleMilliseconds);
    }

    [Fact]
    public void WarpIsClassifiedAsTerminalAndRefusesMultilinePayloads()
    {
        var warpRule = WindowsPastePolicy.Resolve("warp", isControlEdit: true);
        Assert.Equal(TextTargetKind.Terminal, warpRule.TargetKind);
        Assert.Equal(PasteKeystrokeKind.ShiftInsert, warpRule.Keystroke);
        Assert.Equal(
            TextDeliveryRefusalReason.UnsafeMultilineTarget,
            WindowsTextTargetAdapter.CompatibilityRefusal(warpRule.TargetKind, "line1\nline2"));
    }

    [Theory]
    [InlineData("hello world", "prefix hello world", null, "prefix ", null, PasteArrivalStatus.Confirmed)]
    [InlineData("hello\r\nworld", "hello\nworld ", null, "", null, PasteArrivalStatus.Confirmed)]
    [InlineData("hello world", null, "existing hello world", null, "existing ", PasteArrivalStatus.Confirmed)]
    [InlineData("hello world", "hello world", null, "hello world", null, PasteArrivalStatus.NotObserved)]
    [InlineData("hello world", null, "hello world", null, "hello world", PasteArrivalStatus.NotObserved)]
    [InlineData("hello world", null, "hello world", null, null, PasteArrivalStatus.Unreadable)]
    [InlineData("hello world", "hello world hello world", null, "hello world", null, PasteArrivalStatus.Confirmed)]
    [InlineData("hello world", null, "hello world hello world", null, "hello world", PasteArrivalStatus.Confirmed)]
    [InlineData("hello world", "different text", "different text", null, "different text", PasteArrivalStatus.NotObserved)]
    [InlineData("hello world", null, null, null, null, PasteArrivalStatus.Unreadable)]
    public void EvaluateArrivalClassifiesConfirmedNotObservedAndUnreadableCleanly(
        string insertedText,
        string? observedLeft,
        string? observedValue,
        string? previousLeft,
        string? previousValue,
        PasteArrivalStatus expected)
    {
        var actual = WindowsTextTargetAdapter.EvaluateArrival(
            insertedText,
            observedLeft,
            observedValue,
            previousLeft,
            previousValue);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsMatchingFocusProcessCoreAllowsNonElevatedConsoleHostOnlyOnConsoleWindows()
    {
        Assert.True(WindowsWindowIdentity.IsMatchingFocusProcessCore(
            targetProcessId: 45012,
            focusProcessId: 45012,
            () => "Notepad",
            _ => "notepad",
            _ => false));

        Assert.True(WindowsWindowIdentity.IsMatchingFocusProcessCore(
            targetProcessId: 45012,
            focusProcessId: 35352,
            () => "ConsoleWindowClass",
            _ => "conhost.exe",
            _ => false));

        Assert.True(WindowsWindowIdentity.IsMatchingFocusProcessCore(
            targetProcessId: 45012,
            focusProcessId: 35352,
            () => "ConsoleWindowClass",
            _ => "OpenConsole",
            _ => false));

        // Elevated or unreadable integrity console host must never match.
        Assert.False(WindowsWindowIdentity.IsMatchingFocusProcessCore(
            targetProcessId: 45012,
            focusProcessId: 35352,
            () => "ConsoleWindowClass",
            _ => "conhost",
            _ => true));
        Assert.False(WindowsWindowIdentity.IsMatchingFocusProcessCore(
            targetProcessId: 45012,
            focusProcessId: 35352,
            () => "ConsoleWindowClass",
            _ => "conhost",
            _ => null));

        // Non-console window classes and non-host processes keep strict PID equality.
        Assert.False(WindowsWindowIdentity.IsMatchingFocusProcessCore(
            targetProcessId: 45012,
            focusProcessId: 35352,
            () => "Chrome_WidgetWin_1",
            _ => "conhost",
            _ => false));
        Assert.False(WindowsWindowIdentity.IsMatchingFocusProcessCore(
            targetProcessId: 45012,
            focusProcessId: 35352,
            () => "ConsoleWindowClass",
            _ => "notepad",
            _ => false));
    }

    [Fact]
    public void EvaluateFocusedElementAcceptsMatchedConsoleHostTerminalAndBlocksMismatchedFocusOrPassword()
    {
        var terminalTarget = new TargetWindowId(7408622, 45012, FocusedElementId: "42.7408622.4.-1", ProcessName: "pwsh");
        var terminalRule = WindowsPastePolicy.Resolve("pwsh", "ConsoleWindowClass", null, isControlEdit: false);
        var consoleFocus = new WindowsTextTargetAdapter.FocusedElementFacts(
            ProcessId: 35352,
            RuntimeId: "42.7408622.4.-1",
            HasKeyboardFocus: true,
            IsPassword: false,
            IsEnabled: true,
            IsKeyboardFocusable: true,
            IsControlEdit: false,
            ClassName: string.Empty,
            IsControlDocument: true);

        var matched = WindowsTextTargetAdapter.EvaluateFocusedElement(
            terminalTarget,
            consoleFocus,
            win32ChildFocusId: null,
            isExplicitHistoryPaste: false,
            (_, _) => terminalRule,
            focusProcessMatchesTarget: true);
        Assert.Null(matched.Failure);
        Assert.Equal("42.7408622.4.-1", matched.EffectiveFocusId);

        var unmatched = WindowsTextTargetAdapter.EvaluateFocusedElement(
            terminalTarget,
            consoleFocus,
            win32ChildFocusId: null,
            isExplicitHistoryPaste: false,
            (_, _) => terminalRule,
            focusProcessMatchesTarget: false);
        Assert.Equal(TextDeliveryRefusalReason.TargetChanged, unmatched.Failure?.RefusalReason);

        var passwordInConsole = WindowsTextTargetAdapter.EvaluateFocusedElement(
            terminalTarget,
            consoleFocus with { IsPassword = true },
            win32ChildFocusId: null,
            isExplicitHistoryPaste: false,
            (_, _) => terminalRule,
            focusProcessMatchesTarget: true);
        Assert.Equal(TextDeliveryRefusalReason.ProtectedField, passwordInConsole.Failure?.RefusalReason);
    }

    [ClipboardFact]
    public async Task TemporaryPasteClipboardWritesCarryExclusionMarkersWithoutCorruptingRestoreOrCopyOnly()
    {
        using var guard = LocalClipboardGuard.Capture();
        const string originalText = "original user clipboard text";
        byte[] customBytes = [0x10, 0x20, 0x30, 0x40];

        RunOnSta(() =>
        {
            var initial = new DataObject();
            initial.SetData(DataFormats.UnicodeText, autoConvert: false, originalText);
            initial.SetData("Chromium internal source URL", autoConvert: false, new MemoryStream(customBytes));
            Clipboard.SetDataObject(initial, copy: true, retryTimes: 10, retryDelay: 50);
            return true;
        });

        string[]? formatsDuringPaste = null;
        string? textDuringPaste = null;
        var pasted = await WindowsClipboardPaste.PasteAsync(
            "dictated paste text",
            "dictated paste text ",
            restoreClipboard: true,
            static () => TextDeliveryRefusalReason.None,
            WindowsClipboardPaste.SetTemporaryClipboardTextOrThrow,
            () =>
            {
                (textDuringPaste, formatsDuringPaste) = RunOnSta(() =>
                {
                    var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                    return (text, WindowsClipboardPaste.TryGetClipboardFormatNames());
                });
                return true;
            },
            CancellationToken.None,
            settleDelay: TimeSpan.FromMilliseconds(20));

        Assert.True(pasted.Delivered);
        Assert.True(pasted.ClipboardRestored);
        Assert.Equal("dictated paste text", textDuringPaste);
        Assert.NotNull(formatsDuringPaste);
        Assert.Contains(WindowsClipboardPaste.ExcludeMonitorFormatName, formatsDuringPaste!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(WindowsClipboardPaste.CanIncludeInClipboardHistoryFormatName, formatsDuringPaste!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(WindowsClipboardPaste.CanUploadToCloudClipboardFormatName, formatsDuringPaste!, StringComparer.OrdinalIgnoreCase);

        // User's original clipboard + custom format restored cleanly.
        var (restoredText, restoredUrlStream) = RunOnSta(() =>
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            var obj = Clipboard.GetDataObject();
            var stream = obj?.GetData("Chromium internal source URL", autoConvert: false) as MemoryStream;
            return (text, stream?.ToArray());
        });
        Assert.Equal(originalText, restoredText);
        Assert.Equal(customBytes, restoredUrlStream);

        // Snapshotting after restore must omit transient exclusion markers so they never propagate.
        var postRestoreSnapshot = RunOnSta(() => WindowsClipboardPaste.TrySnapshotClipboard());
        Assert.NotNull(postRestoreSnapshot);
        if (postRestoreSnapshot!.NativeEntries is not null)
        {
            Assert.DoesNotContain(
                postRestoreSnapshot.NativeEntries,
                entry => WindowsClipboardPaste.IsTransientMarkerFormat(entry.FormatName));
        }

        // CopyOnlyAsync / CopyRequestedAsync leave normal history behavior enabled (no exclusion markers).
        var copied = await WindowsClipboardPaste.CopyRequestedAsync("user copy", CancellationToken.None);
        Assert.True(copied.Delivered);
        var copyFormats = RunOnSta(() => WindowsClipboardPaste.TryGetClipboardFormatNames() ?? []);
        Assert.DoesNotContain(WindowsClipboardPaste.CanIncludeInClipboardHistoryFormatName, copyFormats, StringComparer.OrdinalIgnoreCase);

        // Preflight refusal via temporary+unmarked-fallback writer leaves fallback text UNMARKED for manual Ctrl+V.
        var refused = await WindowsClipboardPaste.PasteAsync(
            "temp text",
            "retained manual fallback",
            restoreClipboard: true,
            static () => TextDeliveryRefusalReason.TargetChanged,
            WindowsClipboardPaste.SetTemporaryClipboardTextOrThrow,
            static () => true,
            CancellationToken.None,
            settleDelay: TimeSpan.FromMilliseconds(20),
            writeFallbackText: WindowsClipboardPaste.SetClipboardTextOrThrow);
        Assert.True(refused.ClipboardFallback);
        var refusedFormats = RunOnSta(() => WindowsClipboardPaste.TryGetClipboardFormatNames() ?? []);
        Assert.DoesNotContain(WindowsClipboardPaste.CanIncludeInClipboardHistoryFormatName, refusedFormats, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("retained manual fallback", RunOnSta(() => Clipboard.GetText()));
    }

    [Fact]
    public void ArrivalDiagnosticEventsSerializeAndPruneSafely()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ew-arrival-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var logPath = Path.Combine(tempDir, "app.jsonl");
            var logger = new JsonLineFileLogger(logPath);
            var expected = new[]
            {
                AppEventCode.TextDeliveryArrivalConfirmed,
                AppEventCode.TextDeliveryArrivalNotObserved,
                AppEventCode.TextDeliveryArrivalUnreadable,
            };

            foreach (var eventCode in expected)
            {
                logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, eventCode, AppFailureCategory.None, ElapsedMilliseconds: 215));
            }

            var lines = File.ReadAllLines(logPath);
            Assert.Equal(expected.Length, lines.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.True(JsonLineFileLogger.TryParseRecord(lines[i], out var parsed));
                Assert.NotNull(parsed);
                Assert.Equal(expected[i], parsed!.Event);
                Assert.Equal(215, parsed.ElapsedMilliseconds);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static T RunOnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("STA clipboard operation failed.", failure);
        }

        return result!;
    }

    private sealed class LocalClipboardGuard(WindowsClipboardPaste.ClipboardSnapshot snapshot) : IDisposable
    {
        public static LocalClipboardGuard Capture()
        {
            var snapshot = RunOnSta(() => WindowsClipboardPaste.TrySnapshotClipboard());
            Assert.NotNull(snapshot);
            return new LocalClipboardGuard(snapshot!);
        }

        public void Dispose() => _ = RunOnSta(() => WindowsClipboardPaste.TryRestoreClipboard(snapshot));
    }
}
