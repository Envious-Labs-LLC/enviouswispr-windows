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

        // 500ms * 1.5 = 750ms adopted at once.
        Assert.True(patience.RecordSighting(overlapping, 500, floor));
        Assert.Equal(TimeSpan.FromMilliseconds(750), patience.GetSettleDelay("msedge.exe", floor));

        // Samples 2..9 must NOT lower patience during warm-up (WarmUpSamples = 10).
        for (var i = 2; i <= 9; i++)
        {
            var token = patience.RecordDispatch("msedge");
            Assert.True(patience.RecordSighting(token, 50, floor));
            Assert.Equal(TimeSpan.FromMilliseconds(750), patience.GetSettleDelay("msedge", floor));
        }

        // Sample 10 crosses WarmUpSamples and shrinks by at most 10% (750 * 0.9 = 675ms).
        var tenth = patience.RecordDispatch("msedge");
        Assert.True(patience.RecordSighting(tenth, 50, floor));
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
    [InlineData("msedge", "Chrome_WidgetWin_1", null, false, 350)]
    [InlineData("Code", "Chrome_WidgetWin_1", "Chrome_RenderWidgetHostHWND", true, 350)]
    [InlineData("rider64", "SunAwtFrame", null, false, 350)]
    [InlineData("zed", null, null, false, 350)]
    [InlineData("warp", null, null, false, 350)]
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

    [Theory]
    [InlineData("hello world", "prefix hello world", null, "prefix ", PasteArrivalStatus.Confirmed)]
    [InlineData("hello\r\nworld", "hello\nworld ", null, "", PasteArrivalStatus.Confirmed)]
    [InlineData("hello world", null, "existing hello world", null, PasteArrivalStatus.Confirmed)]
    [InlineData("hello world", "hello world", null, "hello world", PasteArrivalStatus.NotObserved)]
    [InlineData("hello world", "hello world hello world", null, "hello world", PasteArrivalStatus.Confirmed)]
    [InlineData("hello world", "different text", "different text", null, PasteArrivalStatus.NotObserved)]
    [InlineData("hello world", null, null, null, PasteArrivalStatus.Unreadable)]
    public void EvaluateArrivalClassifiesConfirmedNotObservedAndUnreadableCleanly(
        string insertedText,
        string? observedLeft,
        string? observedValue,
        string? previousLeft,
        PasteArrivalStatus expected)
    {
        var actual = WindowsTextTargetAdapter.EvaluateArrival(
            insertedText,
            observedLeft,
            observedValue,
            previousLeft);
        Assert.Equal(expected, actual);
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
                    var obj = Clipboard.GetDataObject();
                    var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                    return (text, obj?.GetFormats(autoConvert: false));
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
        var copyFormats = RunOnSta(() => Clipboard.GetDataObject()?.GetFormats(autoConvert: false) ?? []);
        Assert.DoesNotContain(WindowsClipboardPaste.CanIncludeInClipboardHistoryFormatName, copyFormats, StringComparer.OrdinalIgnoreCase);
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
