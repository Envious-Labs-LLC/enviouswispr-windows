using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Input;

namespace EnviousWispr.Architecture.Tests;

[Collection("SystemClipboard")]
public sealed class WindowsPasteResilienceAndPolicyTests
{
    private const ushort VkShift = 0x10;
    private const ushort VkInsert = 0x2D;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;

    [Fact]
    public void SnapshotDataObjectPreservesAllUserFormatsAndOmitsOnlyExplicitOleWrappers()
    {
        var source = new FakeDataObject(
        [
            (System.Windows.Forms.DataFormats.UnicodeText, "primary user text"),
            (System.Windows.Forms.DataFormats.Text, "primary user text"),
            (System.Windows.Forms.DataFormats.Html, "<html><body>primary user text</body></html>"),
            ("Chromium internal source URL", new MemoryStream([1, 2, 3, 4])),
            ("DataObject", new object()),
            ("Ole Private Data", null),
            ("OleClipboardPersistOnFlush", new ThrowingValue(static () => throw new SerializationException("ole wrapper"))),
        ]);

        var snapshot = WindowsClipboardPaste.TrySnapshotDataObject(source);

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.IsEmpty);
        Assert.NotNull(snapshot.Data);
        Assert.True(snapshot.Data!.TryGetData<string>(System.Windows.Forms.DataFormats.UnicodeText, autoConvert: false, out var unicode));
        Assert.Equal("primary user text", unicode);
        Assert.True(snapshot.Data.TryGetData<string>(System.Windows.Forms.DataFormats.Html, autoConvert: false, out var html));
        Assert.Equal("<html><body>primary user text</body></html>", html);
        Assert.True(snapshot.Data.TryGetData<MemoryStream>("Chromium internal source URL", autoConvert: false, out var urlStream));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, urlStream!.ToArray());
    }

    [Fact]
    public void SnapshotDataObjectPreservesFileDropAndBitmapWithoutDroppingAnyUserFormat()
    {
        using var bitmap = new Bitmap(2, 2);
        bitmap.SetPixel(0, 0, Color.Red);
        string[] files = [@"C:\temp\a.txt", @"C:\temp\b.txt"];
        var source = new FakeDataObject(
        [
            (System.Windows.Forms.DataFormats.FileDrop, files),
            (System.Windows.Forms.DataFormats.Bitmap, bitmap),
            ("Preferred DropEffect", new byte[] { 5, 0, 0, 0 }),
            ("Ole Private Data", null),
        ]);

        var snapshot = WindowsClipboardPaste.TrySnapshotDataObject(source);

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.IsEmpty);
        Assert.True(snapshot.Data!.TryGetData<string[]>(System.Windows.Forms.DataFormats.FileDrop, autoConvert: false, out var restoredFiles));
        Assert.Equal(files, restoredFiles);
        Assert.NotSame(files, restoredFiles);
        Assert.True(snapshot.Data.TryGetData<Bitmap>(System.Windows.Forms.DataFormats.Bitmap, autoConvert: false, out var restoredBitmap));
        Assert.NotNull(restoredBitmap);
        Assert.True(snapshot.Data.TryGetData<byte[]>("Preferred DropEffect", autoConvert: false, out var dropEffect));
        Assert.Equal(new byte[] { 5, 0, 0, 0 }, dropEffect);
    }

    [Fact]
    public void SnapshotDataObjectFailsClosedWheneverAnyUserFormatIsUnreadableOrNull()
    {
        var withNullCustomFormat = new FakeDataObject(
        [
            (System.Windows.Forms.DataFormats.UnicodeText, "primary user text"),
            ("Chromium Web Custom MIME Data Format", null),
        ]);
        Assert.Null(WindowsClipboardPaste.TrySnapshotDataObject(withNullCustomFormat));

        var withUncloneableFormat = new FakeDataObject(
        [
            (System.Windows.Forms.DataFormats.UnicodeText, "primary user text"),
            ("OpaqueVendorObject", new object()),
        ]);
        Assert.Null(WindowsClipboardPaste.TrySnapshotDataObject(withUncloneableFormat));

        var withBrokenDelayedRender = new FakeDataObject(
        [
            (System.Windows.Forms.DataFormats.UnicodeText, "primary user text"),
            ("BrokenDelayedRender", new ThrowingValue(static () => Marshal.ThrowExceptionForHR(unchecked((int)0x80040064)))),
        ]);
        Assert.Throws<COMException>(() => WindowsClipboardPaste.TrySnapshotDataObject(withBrokenDelayedRender));

        var withRejectedSerialization = new FakeDataObject(
        [
            (System.Windows.Forms.DataFormats.UnicodeText, "primary user text"),
            ("Chromium internal source URL", new ThrowingValue(static () => throw new SerializationException("bad ole stream"))),
        ]);
        Assert.Null(WindowsClipboardPaste.TrySnapshotDataObject(withRejectedSerialization));

        var empty = new FakeDataObject([]);
        var emptySnapshot = WindowsClipboardPaste.TrySnapshotDataObject(empty);
        Assert.NotNull(emptySnapshot);
        Assert.True(emptySnapshot!.IsEmpty);
        Assert.Null(WindowsClipboardPaste.TrySnapshotDataObject(null)!.Data);
    }

    [Fact]
    public void SnapshotReadRetryRecoversFromTransientClipboardContention()
    {
        var attempts = 0;
        var waits = 0;
        var expected = new WindowsClipboardPaste.ClipboardSnapshot(IsEmpty: true, Data: null);

        var snapshot = WindowsClipboardPaste.TrySnapshotClipboardWithRetry(
            () =>
            {
                attempts++;
                if (attempts < 4)
                {
                    Marshal.ThrowExceptionForHR(unchecked((int)0x800401D0));
                }

                return expected;
            },
            _ => waits++);

        Assert.Same(expected, snapshot);
        Assert.Equal(4, attempts);
        Assert.Equal(3, waits);
    }

    [Fact]
    public void SnapshotReadRetryHonorsCancellationAndExhaustion()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            WindowsClipboardPaste.TrySnapshotClipboardWithRetry(
                static () => new WindowsClipboardPaste.ClipboardSnapshot(IsEmpty: true, Data: null),
                static _ => { },
                cts.Token));

        var exhausted = WindowsClipboardPaste.TrySnapshotClipboardWithRetry(
            static () =>
            {
                Marshal.ThrowExceptionForHR(unchecked((int)0x800401D0));
                return null;
            },
            static _ => { });
        Assert.Null(exhausted);
    }

    [Fact]
    public void SnapshotAbortsInsteadOfReturningPartialSnapshotWhenPrimaryFileDropFails()
    {
        var withNullFileDrop = new FakeDataObject(
        [
            (System.Windows.Forms.DataFormats.FileDrop, null),
            ("Preferred DropEffect", new byte[] { 5, 0, 0, 0 }),
        ]);
        Assert.Null(WindowsClipboardPaste.TrySnapshotDataObject(withNullFileDrop));

        var withBrokenFileDrop = new FakeDataObject(
        [
            (System.Windows.Forms.DataFormats.FileDrop, new ThrowingValue(static () => throw new SerializationException("drop failed"))),
            (System.Windows.Forms.DataFormats.UnicodeText, "fallback label"),
        ]);
        Assert.Null(WindowsClipboardPaste.TrySnapshotDataObject(withBrokenFileDrop));

        var withContendedFileDrop = new FakeDataObject(
        [
            (System.Windows.Forms.DataFormats.FileDrop, new ThrowingValue(static () => Marshal.ThrowExceptionForHR(unchecked((int)0x800401D0)))),
            (System.Windows.Forms.DataFormats.UnicodeText, "fallback label"),
        ]);
        Assert.Throws<COMException>(() => WindowsClipboardPaste.TrySnapshotDataObject(withContendedFileDrop));

        string[] recoveredFiles = [@"C:\temp\recovered.txt"];
        var retryAttempts = 0;
        var transientContentionSnapshot = WindowsClipboardPaste.TrySnapshotClipboardWithRetry(
            () =>
            {
                retryAttempts++;
                if (retryAttempts < 3)
                {
                    return WindowsClipboardPaste.TrySnapshotDataObject(withContendedFileDrop);
                }

                return WindowsClipboardPaste.TrySnapshotDataObject(new FakeDataObject(
                [
                    (System.Windows.Forms.DataFormats.FileDrop, recoveredFiles),
                ]));
            },
            static _ => { });
        Assert.NotNull(transientContentionSnapshot);
        Assert.Equal(3, retryAttempts);

        var exhaustedContention = WindowsClipboardPaste.TrySnapshotClipboardWithRetry(
            () => WindowsClipboardPaste.TrySnapshotDataObject(withContendedFileDrop),
            static _ => { });
        Assert.Null(exhaustedContention);
    }

    [Theory]
    [InlineData("Bitmap")]
    [InlineData("DeviceIndependentBitmap")]
    [InlineData("Rich Text Format")]
    [InlineData("HTML Format")]
    [InlineData("WaveAudio")]
    public void SnapshotAbortsInsteadOfDroppingPrimaryFormat(string primaryFormat)
    {
        var source = new FakeDataObject(
        [
            (primaryFormat, new object()),
            ("Chromium internal source URL", new MemoryStream([1, 2, 3])),
        ]);

        Assert.Null(WindowsClipboardPaste.TrySnapshotDataObject(source));
    }

    [ClipboardFact]
    public void RestoreReportsFailureInsteadOfClaimingSuccessWithPartialFallback()
    {
        using var guard = StaClipboardFixture.Capture();
        var badSnapshot = new WindowsClipboardPaste.ClipboardSnapshot(
            IsEmpty: false,
            Data: null,
            NativeEntries:
            [
                new WindowsClipboardPaste.NativeClipboardEntry(
                    13,
                    null,
                    WindowsClipboardPaste.NativeClipboardEntryKind.HGlobal,
                    System.Text.Encoding.Unicode.GetBytes("user text\0")),
                new WindowsClipboardPaste.NativeClipboardEntry(
                    0xC000,
                    string.Empty,
                    WindowsClipboardPaste.NativeClipboardEntryKind.HGlobal,
                    [1, 2, 3]),
            ]);

        StaClipboardFixture.SetDataObject(() =>
        {
            var data = new System.Windows.Forms.DataObject();
            data.SetData(System.Windows.Forms.DataFormats.UnicodeText, autoConvert: false, "dictation text");
            return data;
        });

        var restored = StaClipboardFixture.Restore(badSnapshot);
        Assert.False(restored);
        Assert.Equal("dictation text", StaClipboardFixture.GetText());

        var (whileOursRestored, whileOursUncertain) = StaClipboardFixture.RestoreWhileOurs(badSnapshot);
        Assert.False(whileOursRestored);
        Assert.True(whileOursUncertain);
        Assert.Equal("dictation text", StaClipboardFixture.GetText());
    }

    [ClipboardFact]
    public async Task PasteWithRawBinaryElectronFormatsRestoresEveryFormatLosslesslyByteForByte()
    {
        using var guard = StaClipboardFixture.Capture();
        const string expectedText = "user electron clipboard text";
        byte[] expectedUrlBytes = "https://example.test/electron\0"u8.ToArray();
        byte[] expectedVsCodeMetaBytes = "{\"version\":1,\"mode\":\"csharp\"}\0"u8.ToArray();
        byte[] expectedCustomMimeBytes = [0x01, 0x00, 0x00, 0x00, 0xAB, 0xCD, 0xEF, 0x42];

        StaClipboardFixture.SetNativeClipboard(
            expectedText,
            [
                ("Chromium internal source URL", expectedUrlBytes),
                ("vscode-editor-data", expectedVsCodeMetaBytes),
                ("Chromium Web Custom MIME Data Format", expectedCustomMimeBytes),
            ]);

        string? clipboardDuringPaste = null;
        var pasted = await WindowsClipboardPaste.PasteAsync(
            "dictated words",
            "dictated words ",
            restoreClipboard: true,
            static () => TextDeliveryRefusalReason.None,
            WindowsClipboardPaste.SetClipboardTextOrThrow,
            () =>
            {
                clipboardDuringPaste = StaClipboardFixture.GetText();
                return true;
            },
            CancellationToken.None);

        Assert.True(pasted.Delivered);
        Assert.Equal(TextDeliveryRoute.ClipboardPaste, pasted.Route);
        Assert.True(pasted.ClipboardRestored);
        Assert.False(pasted.ClipboardUncertain);
        Assert.Equal("dictated words", clipboardDuringPaste);
        Assert.Equal(expectedText, StaClipboardFixture.GetText());
        Assert.Equal(expectedUrlBytes, StaClipboardFixture.GetRawFormatBytes("Chromium internal source URL"));
        Assert.Equal(expectedVsCodeMetaBytes, StaClipboardFixture.GetRawFormatBytes("vscode-editor-data"));
        Assert.Equal(expectedCustomMimeBytes, StaClipboardFixture.GetRawFormatBytes("Chromium Web Custom MIME Data Format"));
    }

    [ClipboardFact]
    public async Task PasteWithMultiFormatClipboardAndStandInTargetRestoresAllPrimaryAndAuxiliaryFormats()
    {
        using var guard = StaClipboardFixture.Capture();
        const string expectedText = "user electron clipboard text";
        const string expectedHtml = "<html><body>user electron clipboard text</body></html>";
        byte[] customUrlBytes = "https://example.test/electron"u8.ToArray();

        StaClipboardFixture.SetDataObject(() =>
        {
            var data = new System.Windows.Forms.DataObject();
            data.SetData(System.Windows.Forms.DataFormats.UnicodeText, autoConvert: false, expectedText);
            data.SetData(System.Windows.Forms.DataFormats.Html, autoConvert: false, expectedHtml);
            data.SetData("Chromium internal source URL", autoConvert: false, new MemoryStream(customUrlBytes));
            return data;
        });

        string? clipboardDuringPaste = null;
        var pasted = await WindowsClipboardPaste.PasteAsync(
            "dictated words",
            "dictated words ",
            restoreClipboard: true,
            static () => TextDeliveryRefusalReason.None,
            WindowsClipboardPaste.SetClipboardTextOrThrow,
            () =>
            {
                clipboardDuringPaste = StaClipboardFixture.GetText();
                return true;
            },
            CancellationToken.None);

        Assert.True(pasted.Delivered);
        Assert.Equal(TextDeliveryRoute.ClipboardPaste, pasted.Route);
        Assert.True(pasted.ClipboardRestored);
        Assert.False(pasted.ClipboardUncertain);
        Assert.Equal("dictated words", clipboardDuringPaste);
        Assert.Equal(expectedText, StaClipboardFixture.GetText());
        Assert.Equal(expectedHtml, StaClipboardFixture.GetData(System.Windows.Forms.DataFormats.Html));
        var restoredStream = Assert.IsType<MemoryStream>(StaClipboardFixture.GetData("Chromium internal source URL"));
        Assert.Equal(customUrlBytes, restoredStream.ToArray());
    }

    [Theory]
    [InlineData("antigravity", null, null, true, TextTargetKind.Unknown, true, DirectWriteSkipReason.ElectronOrChromiumHost, PasteKeystrokeKind.CtrlV)]
    [InlineData("Antigravity.exe", "Chrome_WidgetWin_1", null, true, TextTargetKind.Unknown, true, DirectWriteSkipReason.ElectronOrChromiumHost, PasteKeystrokeKind.CtrlV)]
    [InlineData("Code", "Chrome_WidgetWin_1", null, true, TextTargetKind.Unknown, true, DirectWriteSkipReason.ElectronOrChromiumHost, PasteKeystrokeKind.CtrlV)]
    [InlineData("Cursor", null, null, true, TextTargetKind.Unknown, true, DirectWriteSkipReason.ElectronOrChromiumHost, PasteKeystrokeKind.CtrlV)]
    [InlineData("Windsurf", null, null, true, TextTargetKind.Unknown, true, DirectWriteSkipReason.ElectronOrChromiumHost, PasteKeystrokeKind.CtrlV)]
    [InlineData("notion", null, null, true, TextTargetKind.Unknown, true, DirectWriteSkipReason.ElectronOrChromiumHost, PasteKeystrokeKind.CtrlV)]
    [InlineData("obsidian", null, null, true, TextTargetKind.Unknown, true, DirectWriteSkipReason.ElectronOrChromiumHost, PasteKeystrokeKind.CtrlV)]
    [InlineData("chrome", null, null, true, TextTargetKind.Browser, true, DirectWriteSkipReason.Browser, PasteKeystrokeKind.CtrlV)]
    [InlineData("msedge", null, null, true, TextTargetKind.Browser, true, DirectWriteSkipReason.Browser, PasteKeystrokeKind.CtrlV)]
    [InlineData("brave", null, null, true, TextTargetKind.Browser, true, DirectWriteSkipReason.Browser, PasteKeystrokeKind.CtrlV)]
    [InlineData("firefox", "MozillaWindowClass", null, true, TextTargetKind.Browser, true, DirectWriteSkipReason.Browser, PasteKeystrokeKind.CtrlV)]
    [InlineData("slack", null, null, true, TextTargetKind.Chat, true, DirectWriteSkipReason.Chat, PasteKeystrokeKind.CtrlV)]
    [InlineData("discord", null, null, true, TextTargetKind.Chat, true, DirectWriteSkipReason.Chat, PasteKeystrokeKind.CtrlV)]
    [InlineData("ms-teams", null, null, true, TextTargetKind.Chat, true, DirectWriteSkipReason.Chat, PasteKeystrokeKind.CtrlV)]
    [InlineData("winword", null, null, true, TextTargetKind.Office, true, DirectWriteSkipReason.Office, PasteKeystrokeKind.CtrlV)]
    [InlineData("windowsterminal", "CASCADIA_HOSTING_WINDOW_CLASS", null, true, TextTargetKind.Terminal, true, DirectWriteSkipReason.Terminal, PasteKeystrokeKind.ShiftInsert)]
    [InlineData("pwsh", "ConsoleWindowClass", null, true, TextTargetKind.Terminal, true, DirectWriteSkipReason.Terminal, PasteKeystrokeKind.ShiftInsert)]
    [InlineData("cmd", null, null, false, TextTargetKind.Terminal, true, DirectWriteSkipReason.Terminal, PasteKeystrokeKind.ShiftInsert)]
    [InlineData("mintty", "mintty", null, false, TextTargetKind.Terminal, true, DirectWriteSkipReason.Terminal, PasteKeystrokeKind.ShiftInsert)]
    [InlineData("tabby", "Chrome_WidgetWin_1", null, true, TextTargetKind.Terminal, true, DirectWriteSkipReason.Terminal, PasteKeystrokeKind.ShiftInsert)]
    [InlineData("custom-electron-fork", "Chrome_WidgetWin_1", "Chrome_RenderWidgetHostHWND", true, TextTargetKind.Unknown, true, DirectWriteSkipReason.ElectronOrChromiumHost, PasteKeystrokeKind.CtrlV)]
    [InlineData("notepad", "Notepad", "RichEditD2DPT", true, TextTargetKind.StandardEdit, false, DirectWriteSkipReason.None, PasteKeystrokeKind.CtrlV)]
    [InlineData("EnviousWispr.Delivery.Target.Uat", "WindowsForms10.Window.8.app.0.141b42a_r8_ad1", "WindowsForms10.EDIT.app.0.141b42a_r8_ad1", true, TextTargetKind.StandardEdit, false, DirectWriteSkipReason.None, PasteKeystrokeKind.CtrlV)]
    public void PastePolicyRoutesStandardEditsElectronIdesBrowsersAndTerminalsCorrectly(
        string processName,
        string? windowClassName,
        string? elementClassName,
        bool isControlEdit,
        TextTargetKind expectedKind,
        bool expectedSkipDirectWrite,
        DirectWriteSkipReason expectedSkipReason,
        PasteKeystrokeKind expectedKeystroke)
    {
        var rule = WindowsPastePolicy.Resolve(
            processName,
            windowClassName,
            elementClassName,
            isControlEdit,
            isFullscreen: false);

        Assert.Equal(expectedKind, rule.TargetKind);
        Assert.Equal(expectedSkipDirectWrite, rule.SkipDirectValueWrite);
        Assert.Equal(expectedSkipReason, rule.DirectWriteSkip);
        Assert.Equal(expectedKeystroke, rule.Keystroke);
        Assert.True(rule.AllowsWindowFallbackWithoutFocusedElement);
    }

    [Fact]
    public void PastePolicyRefusesFullscreenGameWindowFallbackAndDirectWrite()
    {
        var rule = WindowsPastePolicy.Resolve(
            "game-binary",
            windowClassName: "UnrealWindow",
            elementClassName: null,
            isControlEdit: false,
            isFullscreen: true);

        Assert.Equal(TextTargetKind.Game, rule.TargetKind);
        Assert.True(rule.SkipDirectValueWrite);
        Assert.False(rule.AllowsWindowFallbackWithoutFocusedElement);
    }

    [Fact]
    public void ShiftInsertInputsUseExtendedInsertKeyAndReleaseShiftDeterministically()
    {
        var inputs = WindowsClipboardPaste.BuildShiftInsertInputs();

        Assert.Equal(4, inputs.Length);
        Assert.All(inputs, input =>
        {
            Assert.Equal(1u, input.Type);
            Assert.Equal(MenuKeyMask.Tag, input.Union.Keyboard.ExtraInfo);
        });

        Assert.Equal(VkShift, inputs[0].Union.Keyboard.VirtualKey);
        Assert.Equal(0u, inputs[0].Union.Keyboard.Flags);

        Assert.Equal(VkInsert, inputs[1].Union.Keyboard.VirtualKey);
        Assert.Equal(KeyEventExtendedKey, inputs[1].Union.Keyboard.Flags);

        Assert.Equal(VkInsert, inputs[2].Union.Keyboard.VirtualKey);
        Assert.Equal(KeyEventExtendedKey | KeyEventKeyUp, inputs[2].Union.Keyboard.Flags);

        Assert.Equal(VkShift, inputs[3].Union.Keyboard.VirtualKey);
        Assert.Equal(KeyEventKeyUp, inputs[3].Union.Keyboard.Flags);
    }

    [Fact]
    public void AdapterDirectValueWriteRespectsPerAppSkipPolicyWhilePreservingStandardEdit()
    {
        var text = new ProcessedText(DictationSessionId.Create(), "hello");
        var options = TextDeliveryOptions.Default;

        var standardTarget = new TargetWindowId(0, 0, "1.2.3", ProcessName: "notepad");
        var standardRule = WindowsPastePolicy.Resolve("notepad", "Notepad", "Edit", isControlEdit: true);
        var standardContext = new CaretContext(
            standardTarget,
            "1.2.3",
            TextTargetKind.StandardEdit,
            Left: "",
            Selection: "",
            Right: "",
            LeftReachedDocumentStart: true,
            RightReachedDocumentEnd: true,
            HasTextContext: true,
            SupportsDirectValueWrite: true,
            DirectValueWriteAtEnd: true);
        var standardRequest = new TextCommitRequest(text, text, standardTarget, standardContext, TextTargetKind.StandardEdit, options);
        Assert.True(WindowsTextTargetAdapter.CanUseDirectValueWrite(standardContext, standardRequest, standardRule));

        var antigravityRule = WindowsPastePolicy.Resolve("Antigravity", "Chrome_WidgetWin_1", "Chrome_RenderWidgetHostHWND", isControlEdit: true);
        var antigravityContext = standardContext with { Target = standardTarget with { ProcessName = "Antigravity" } };
        var antigravityRequest = new TextCommitRequest(text, text, antigravityContext.Target, antigravityContext, antigravityRule.TargetKind, options);
        Assert.False(WindowsTextTargetAdapter.CanUseDirectValueWrite(antigravityContext, antigravityRequest, antigravityRule));

        var terminalRule = WindowsPastePolicy.Resolve("windowsterminal", "CASCADIA_HOSTING_WINDOW_CLASS", null, isControlEdit: true);
        Assert.False(WindowsTextTargetAdapter.CanUseDirectValueWrite(standardContext, standardRequest, terminalRule));
    }

    [Fact]
    public void CommitKeystrokePreservesTerminalShiftInsertForEmbeddedTerminalControl()
    {
        var unknownHostTarget = new TargetWindowId(0x1234, 42, "42.7.1", ProcessName: "custom-ide-host");
        var captureRule = WindowsPastePolicy.Resolve(
            "custom-ide-host",
            windowClassName: "CustomHostWindow",
            elementClassName: "CASCADIA_HOSTING_WINDOW_CLASS",
            isControlEdit: false);
        Assert.Equal(TextTargetKind.Terminal, captureRule.TargetKind);

        var capturedContext = new CaretContext(
            unknownHostTarget,
            "42.7.1",
            captureRule.TargetKind,
            Left: string.Empty,
            Selection: string.Empty,
            Right: string.Empty,
            LeftReachedDocumentStart: false,
            RightReachedDocumentEnd: false,
            HasTextContext: false,
            SupportsDirectValueWrite: false,
            DirectValueWriteAtEnd: false,
            IsScreenDerived: true);

        // CommitAsync re-resolves window-level policy with elementClassName: null;
        // captured TargetKind.Terminal must still preserve Shift+Insert.
        var commitRule = WindowsPastePolicy.Resolve(
            "custom-ide-host",
            windowClassName: "CustomHostWindow",
            elementClassName: null,
            isControlEdit: false);
        Assert.Equal(PasteKeystrokeKind.CtrlV, commitRule.Keystroke);
        Assert.Equal(
            PasteKeystrokeKind.ShiftInsert,
            WindowsTextTargetAdapter.ResolveCommitKeystroke(capturedContext, commitRule));
    }

    [Fact]
    public void OwnerDisplayAndExplicitCompanionFormatsRespectCapturePolicy()
    {
        const uint cfText = 1;
        const uint cfBitmap = 2;
        const uint cfMetaFilePict = 3;
        const uint cfOemText = 7;
        const uint cfPalette = 9;
        const uint cfLocale = 16;
        const uint cfOwnerDisplay = 0x0080;

        // Only synthesized GDI bitmaps/palettes alongside CF_DIB are omitted.
        Assert.False(WindowsClipboardPaste.ShouldOmitSynthesizedFormat(cfText, hasDib: true));
        Assert.False(WindowsClipboardPaste.ShouldOmitSynthesizedFormat(cfOemText, hasDib: true));
        Assert.False(WindowsClipboardPaste.ShouldOmitSynthesizedFormat(cfLocale, hasDib: true));
        Assert.False(WindowsClipboardPaste.ShouldOmitSynthesizedFormat(cfMetaFilePict, hasDib: true));
        Assert.False(WindowsClipboardPaste.ShouldOmitSynthesizedFormat(cfOwnerDisplay, hasDib: true));
        Assert.True(WindowsClipboardPaste.ShouldOmitSynthesizedFormat(cfBitmap, hasDib: true));
        Assert.True(WindowsClipboardPaste.ShouldOmitSynthesizedFormat(cfPalette, hasDib: true));
        Assert.False(WindowsClipboardPaste.ShouldOmitSynthesizedFormat(cfBitmap, hasDib: false));

        Assert.False(WindowsClipboardPaste.TryCaptureNativeEntry(
            cfOwnerDisplay,
            formatName: null,
            out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void RegisteredFormatNullHandleFallsBackToIStreamDataObjectWithoutDroppingStream()
    {
        var fallback = WindowsClipboardPaste.TrySnapshotRegisteredFallback(() => new FakeDataObject(
        [
            ("FileContents", new MemoryStream([9, 8, 7, 6])),
            (System.Windows.Forms.DataFormats.UnicodeText, "attachment"),
        ]));

        Assert.NotNull(fallback);
        Assert.False(fallback!.IsEmpty);
        Assert.True(fallback.Data!.TryGetData<MemoryStream>("FileContents", autoConvert: false, out var stream));
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, stream!.ToArray());

        var failedFallback = WindowsClipboardPaste.TrySnapshotRegisteredFallback(() => new FakeDataObject(
        [
            ("FileContents", null),
        ]));
        Assert.Null(failedFallback);
    }

    [ClipboardFact]
    public async Task PastePreservesExplicitCompanionCfTextAndCfLocaleAlongsideUnicodeText()
    {
        using var guard = StaClipboardFixture.Capture();
        const string expectedUnicode = "hello world";
        byte[] expectedCfTextBytes = "custom-ansi-companion\0"u8.ToArray();
        byte[] expectedCfLocaleBytes = [0x09, 0x04, 0x00, 0x00];

        StaClipboardFixture.SetNativeStandardFormats(
            [
                (13u, System.Text.Encoding.Unicode.GetBytes(expectedUnicode + '\0')),
                (1u, expectedCfTextBytes),
                (16u, expectedCfLocaleBytes),
            ]);

        var pasted = await WindowsClipboardPaste.PasteAsync(
            "dictated words",
            "dictated words ",
            restoreClipboard: true,
            static () => TextDeliveryRefusalReason.None,
            WindowsClipboardPaste.SetClipboardTextOrThrow,
            static () => true,
            CancellationToken.None);

        Assert.True(pasted.Delivered);
        Assert.True(pasted.ClipboardRestored);
        Assert.False(pasted.ClipboardUncertain);
        Assert.Equal(expectedUnicode, StaClipboardFixture.GetText());
        Assert.Equal(expectedCfTextBytes, StaClipboardFixture.GetRawStandardFormatBytes(1u));
        Assert.Equal(expectedCfLocaleBytes, StaClipboardFixture.GetRawStandardFormatBytes(16u));
    }

    [Theory]
    [InlineData("Shell_TrayWnd", true, false, true, 1920, 48, true)]
    [InlineData("Shell_SecondaryTrayWnd", true, false, true, 1920, 48, true)]
    [InlineData("XamlExplorerHostWindow", true, false, true, 600, 600, true)]
    [InlineData("XamlExplorerHostIslandWindow", true, false, true, 600, 600, true)]
    [InlineData("Windows.UI.Core.CoreWindow", true, false, true, 800, 600, true)]
    [InlineData("Shell_InputSwitchTopLevelWindow", true, false, true, 200, 300, true)]
    [InlineData("MultitaskingViewFrame", true, false, true, 1200, 800, true)]
    [InlineData("TaskListThumbnailWnd", true, false, true, 300, 200, true)]
    [InlineData("ForegroundStaging", true, false, true, 100, 100, true)]
    [InlineData("LockScreenControllerProxyWindow", true, false, true, 100, 100, true)]
    [InlineData("WorkerW", true, false, true, 1920, 1080, true)]
    [InlineData("Progman", true, false, true, 1920, 1080, true)]
    [InlineData("#32768", true, false, true, 200, 300, true)]
    [InlineData("ApplicationFrameWindow", true, false, false, 800, 600, true)]
    [InlineData("ApplicationFrameWindow", true, false, true, 0, 0, true)]
    [InlineData("ApplicationFrameWindow", true, true, true, 800, 600, true)]
    [InlineData("ApplicationFrameWindow", true, false, true, 800, 600, false)]
    [InlineData("Chrome_WidgetWin_1", false, false, true, 1200, 800, true)]
    [InlineData("Chrome_WidgetWin_1", true, true, true, 1200, 800, true)]
    [InlineData("Chrome_WidgetWin_1", true, false, true, 1200, 800, false)]
    [InlineData("Notepad", true, false, true, 1000, 700, false)]
    public void ForegroundHistoryFiltersWin11TransientShellAndEmptyApplicationFrameWindows(
        string className,
        bool isVisible,
        bool isCloaked,
        bool hasChildWindow,
        int width,
        int height,
        bool expectedFiltered)
    {
        Assert.Equal(
            expectedFiltered,
            WindowsForegroundHistory.IsTransientShellWindow(
                className,
                isVisible,
                isCloaked,
                hasChildWindow,
                width,
                height));
    }

    [Fact]
    public async Task ReacquireRestoresMinimizedTargetAndAllowsElectronWithoutFocusedElementId()
    {
        var target = new TargetWindowId(0x4321, 512, FocusedElementId: null, ProcessName: "Antigravity");
        var isMinimized = true;
        var foreground = (nint)0x9999;
        var restoreCalls = 0;

        var reacquired = await WindowsForegroundTargetProvider.ReacquireCoreAsync(
            target,
            TimeSpan.FromMilliseconds(400),
            isWindowValid: static _ => true,
            restoreAndFocus: hwnd =>
            {
                restoreCalls++;
                if (hwnd == target.Value)
                {
                    isMinimized = false;
                    foreground = hwnd;
                }
            },
            isIconic: _ => isMinimized,
            getForegroundWindow: () => foreground,
            captureCurrentTarget: () => new TargetWindowId(target.Value, target.ProcessId, FocusedElementId: null, ProcessName: "Antigravity"),
            getWindowClassName: static _ => "Chrome_WidgetWin_1",
            CancellationToken.None,
            pollIntervalMs: 5,
            focusedElementGraceMs: 25);

        Assert.NotNull(reacquired);
        Assert.Equal(target.Value, reacquired!.Value.Value);
        Assert.Equal(target.ProcessId, reacquired.Value.ProcessId);
        Assert.Null(reacquired.Value.FocusedElementId);
        Assert.Equal(1, restoreCalls);
    }

    [Fact]
    public async Task ReacquireReturnsEarlyWhenFocusedElementAppearsDuringGraceWindow()
    {
        var target = new TargetWindowId(0x4321, 512, FocusedElementId: null, ProcessName: "Code");
        var polls = 0;

        var reacquired = await WindowsForegroundTargetProvider.ReacquireCoreAsync(
            target,
            TimeSpan.FromMilliseconds(500),
            isWindowValid: static _ => true,
            restoreAndFocus: static _ => { },
            isIconic: static _ => false,
            getForegroundWindow: () => target.Value,
            captureCurrentTarget: () =>
            {
                polls++;
                return new TargetWindowId(
                    target.Value,
                    target.ProcessId,
                    FocusedElementId: polls >= 2 ? "42.7.1" : null,
                    ProcessName: "Code");
            },
            getWindowClassName: static _ => "Chrome_WidgetWin_1",
            CancellationToken.None,
            pollIntervalMs: 5,
            focusedElementGraceMs: 200);

        Assert.NotNull(reacquired);
        Assert.Equal("42.7.1", reacquired!.Value.FocusedElementId);
        Assert.Equal(2, polls);
    }

    [Fact]
    public async Task ReacquireRefusesClosedOrNonForegroundTargets()
    {
        var target = new TargetWindowId(0x4321, 512, FocusedElementId: null, ProcessName: "Antigravity");

        var closed = await WindowsForegroundTargetProvider.ReacquireCoreAsync(
            target,
            TimeSpan.FromMilliseconds(100),
            isWindowValid: static _ => false,
            restoreAndFocus: static _ => { },
            isIconic: static _ => false,
            getForegroundWindow: static () => 0,
            captureCurrentTarget: static () => null,
            getWindowClassName: static _ => null,
            CancellationToken.None,
            pollIntervalMs: 5,
            focusedElementGraceMs: 20);
        Assert.Null(closed);

        var blockedFocus = await WindowsForegroundTargetProvider.ReacquireCoreAsync(
            target,
            TimeSpan.FromMilliseconds(40),
            isWindowValid: static _ => true,
            restoreAndFocus: static _ => { },
            isIconic: static _ => false,
            getForegroundWindow: static () => 0x8888,
            captureCurrentTarget: static () => null,
            getWindowClassName: static _ => "Chrome_WidgetWin_1",
            CancellationToken.None,
            pollIntervalMs: 5,
            focusedElementGraceMs: 20);
        Assert.Null(blockedFocus);
    }

    [Fact]
    public async Task HistoryPasteDeliversWhenReacquireReturnsElectronWindowWithoutFocusedElementId()
    {
        var now = new DateTimeOffset(2026, 11, 25, 18, 0, 0, TimeSpan.Zero);
        var entry = DictationHistoryEntry.Create(now.AddMinutes(-2), "pasted into electron", "parakeet", false, true);
        var target = new TargetWindowId(0x2222, 999, FocusedElementId: null, ProcessName: "Antigravity");
        var adapter = new WindowLevelTargetAdapter();
        var persistence = new SessionPersistence(
            new FakeRecoveryStore(),
            new FakeHistoryStore([entry]),
            new NullLogger(),
            TimeProvider.System,
            () => HistoryPreferences.Default,
            new FakeEffects());
        var paste = new SavedDictationPaste(new SavedDictationPasteEnvironment(
            _ => Task.FromResult<IReadOnlyList<DictationHistoryEntry>>([entry]),
            () => now,
            () => false,
            new ContextAwareTextDelivery(adapter),
            () => TextDeliveryOptions.Default,
            (window, _) => Task.FromResult<TargetWindowId?>(window),
            persistence));

        var result = await paste.PasteAsync(entry.Id, target);

        Assert.Equal(SavedDictationPasteOutcome.Pasted, result.Outcome);
        Assert.Equal(TextDeliveryRoute.ClipboardPaste, result.Delivery?.Route);
        var commit = Assert.Single(adapter.Commits);
        Assert.Equal(TextDeliveryRefusalReason.None, commit.ForcedRefusalReason);
        Assert.True(commit.Options.IsExplicitHistoryPaste);
        Assert.Null(commit.Target.FocusedElementId);
    }

    [Fact]
    public async Task ReacquireWaitsBrieflyForUiaFocusWhenWin32FocusIsAlreadyPresent()
    {
        var target = new TargetWindowId(0x4321, 512, FocusedElementId: null, ProcessName: "notepad");
        var polls = 0;

        var reacquired = await WindowsForegroundTargetProvider.ReacquireCoreAsync(
            target,
            TimeSpan.FromMilliseconds(500),
            isWindowValid: static _ => true,
            restoreAndFocus: static _ => { },
            isIconic: static _ => false,
            getForegroundWindow: () => target.Value,
            captureCurrentTarget: () =>
            {
                polls++;
                return new TargetWindowId(
                    target.Value,
                    target.ProcessId,
                    FocusedElementId: polls >= 2 ? "42.99.1" : "win32:1A2B",
                    ProcessName: "notepad");
            },
            getWindowClassName: static _ => "Notepad",
            CancellationToken.None,
            pollIntervalMs: 5,
            focusedElementGraceMs: 200);

        Assert.NotNull(reacquired);
        Assert.Equal("42.99.1", reacquired!.Value.FocusedElementId);
        Assert.Equal(2, polls);
    }

    [Fact]
    public void FocusEvaluationRejectsNonUniqueLiveDictationFocusWhileExplicitHistoryPasteChecksPasswordFailClosed()
    {
        var target = new TargetWindowId(0x1111, 4242, FocusedElementId: null, ProcessName: "Antigravity");
        var electronRule = WindowsPastePolicy.Resolve("Antigravity", "Chrome_WidgetWin_1", "Chrome_RenderWidgetHostHWND", isControlEdit: false);
        var nonEmptyFocus = new WindowsTextTargetAdapter.FocusedElementFacts(
            ProcessId: 4242,
            RuntimeId: string.Empty,
            HasKeyboardFocus: true,
            IsPassword: false,
            IsEnabled: true,
            IsKeyboardFocusable: true,
            IsControlEdit: false,
            ClassName: "Chrome_RenderWidgetHostHWND");

        // Live dictation (isExplicitHistoryPaste: false) MUST fail closed whenever FocusedElementId is null
        // (i.e. neither UIA RuntimeId nor distinct child HWND exists).
        var liveMissingIdentity = WindowsTextTargetAdapter.EvaluateFocusedElement(
            target,
            nonEmptyFocus,
            win32ChildFocusId: null,
            isExplicitHistoryPaste: false,
            (_, _) => electronRule);
        Assert.NotNull(liveMissingIdentity.Failure);
        Assert.Equal(TargetContextStatus.AccessibilityUnavailable, liveMissingIdentity.Failure!.Status);
        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, liveMissingIdentity.Failure.RefusalReason);

        // Explicit History Paste (isExplicitHistoryPaste: true) fails closed on null UIA focus, PID mismatch,
        // or IsPassword == true, and allows confirmed non-password window fallback otherwise.
        var unavailable = WindowsTextTargetAdapter.EvaluateFocusedElement(
            target,
            focus: null,
            win32ChildFocusId: null,
            isExplicitHistoryPaste: true,
            (_, _) => electronRule);
        Assert.NotNull(unavailable.Failure);
        Assert.Equal(TargetContextStatus.AccessibilityUnavailable, unavailable.Failure!.Status);
        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, unavailable.Failure.RefusalReason);

        var mismatchedPid = WindowsTextTargetAdapter.EvaluateFocusedElement(
            target,
            nonEmptyFocus with { ProcessId = 9999, RuntimeId = "42.1.1" },
            win32ChildFocusId: null,
            isExplicitHistoryPaste: true,
            (_, _) => electronRule);
        Assert.NotNull(mismatchedPid.Failure);
        Assert.Equal(TargetContextStatus.AccessibilityUnavailable, mismatchedPid.Failure!.Status);
        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, mismatchedPid.Failure.RefusalReason);

        var passwordField = WindowsTextTargetAdapter.EvaluateFocusedElement(
            target,
            nonEmptyFocus with { IsPassword = true },
            win32ChildFocusId: null,
            isExplicitHistoryPaste: true,
            (_, _) => electronRule);
        Assert.NotNull(passwordField.Failure);
        Assert.Equal(TargetContextStatus.Protected, passwordField.Failure!.Status);
        Assert.Equal(TextDeliveryRefusalReason.ProtectedField, passwordField.Failure.RefusalReason);

        var allowedHistoryPaste = WindowsTextTargetAdapter.EvaluateFocusedElement(
            target,
            nonEmptyFocus,
            win32ChildFocusId: null,
            isExplicitHistoryPaste: true,
            (_, _) => electronRule);
        Assert.Null(allowedHistoryPaste.Failure);
        Assert.True(allowedHistoryPaste.WindowFallbackOnly);
        Assert.NotNull(allowedHistoryPaste.Rule);

        // Distinct Win32 child HWND tracking (win32:1000 vs win32:2000) still catches child control switches.
        var win32Target = target with { FocusedElementId = "win32:1000" };
        var win32Shifted = WindowsTextTargetAdapter.EvaluateFocusedElement(
            win32Target,
            nonEmptyFocus,
            win32ChildFocusId: "win32:2000",
            isExplicitHistoryPaste: false,
            (_, _) => electronRule);
        Assert.NotNull(win32Shifted.Failure);
        Assert.Equal(TargetContextStatus.TargetChanged, win32Shifted.Failure!.Status);

        var win32MismatchedPid = WindowsTextTargetAdapter.EvaluateFocusedElement(
            win32Target,
            nonEmptyFocus with { ProcessId = 9999 },
            win32ChildFocusId: "win32:1000",
            isExplicitHistoryPaste: false,
            (_, _) => electronRule);
        Assert.NotNull(win32MismatchedPid.Failure);
        Assert.Equal(TargetContextStatus.AccessibilityUnavailable, win32MismatchedPid.Failure!.Status);
    }

    [Fact]
    public async Task ExplicitHistoryPasteRefusesWhenPasswordInspectionFailsAtCapture()
    {
        var target = new TargetWindowId(0x1111, 4242, FocusedElementId: null, ProcessName: "Antigravity");
        var adapter = new EvaluableFocusAdapter(static call =>
            call == 1
                ? throw new AutomationRefusalException("uia refused", new InvalidOperationException("E_ACCESSDENIED"))
                : throw new InvalidOperationException("unexpected commit attempt"));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(new TextDeliveryRequest(
            new ProcessedText(DictationSessionId.Create(), "secret dictation"),
            target,
            "en",
            TextDeliveryOptions.Default with { IsExplicitHistoryPaste = true },
            SnippetExpanded: false));

        Assert.False(result.Delivered);
        Assert.True(result.ClipboardFallback);
        Assert.Equal(TextDeliveryRoute.ClipboardOnly, result.Route);
        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, result.RefusalReason);
        Assert.False(adapter.CommitAttempted);
    }

    [Fact]
    public async Task LiveDictationFailsClosedWhenFocusedElementIdIsNullEvenIfElectronExposesNonUniqueFocus()
    {
        var target = new TargetWindowId(0x2222, 7777, FocusedElementId: null, ProcessName: "Antigravity");
        var adapter = new EvaluableFocusAdapter(_ =>
            (new WindowsTextTargetAdapter.FocusedElementFacts(
                ProcessId: 7777,
                RuntimeId: string.Empty,
                HasKeyboardFocus: true,
                IsPassword: false,
                IsEnabled: true,
                IsKeyboardFocusable: true,
                IsControlEdit: false,
                ClassName: "Chrome_RenderWidgetHostHWND"), null));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(new TextDeliveryRequest(
            new ProcessedText(DictationSessionId.Create(), "dictated words"),
            target,
            "en",
            TextDeliveryOptions.Default with { IsExplicitHistoryPaste = false },
            SnippetExpanded: false));

        Assert.False(result.Delivered);
        Assert.True(result.ClipboardFallback);
        Assert.Equal(TextDeliveryRoute.ClipboardOnly, result.Route);
        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, result.RefusalReason);
        Assert.False(adapter.CommitAttempted);
        Assert.False(adapter.PasteKeystrokeSent);
    }

    [Fact]
    public async Task ExplicitHistoryPasteDeliversWhenNonPasswordConfirmedWithoutPreExistingFocusedElementId()
    {
        var target = new TargetWindowId(0x2222, 7777, FocusedElementId: null, ProcessName: "Antigravity");
        var sameFocus = new WindowsTextTargetAdapter.FocusedElementFacts(
            ProcessId: 7777,
            RuntimeId: string.Empty,
            HasKeyboardFocus: true,
            IsPassword: false,
            IsEnabled: true,
            IsKeyboardFocusable: true,
            IsControlEdit: false,
            ClassName: "Chrome_RenderWidgetHostHWND");
        var adapter = new EvaluableFocusAdapter(_ => (sameFocus, null));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(new TextDeliveryRequest(
            new ProcessedText(DictationSessionId.Create(), "dictated words"),
            target,
            "en",
            TextDeliveryOptions.Default with { IsExplicitHistoryPaste = true },
            SnippetExpanded: false));

        Assert.True(result.Delivered);
        Assert.Equal(TextDeliveryRoute.ClipboardPaste, result.Route);
        Assert.Equal(TextDeliveryRefusalReason.None, result.RefusalReason);
        Assert.True(adapter.PasteKeystrokeSent);
    }

    [Fact]
    public async Task IntraWindowWin32ChildShiftIsDetectedAcrossCaptureAndCommit()
    {
        var target = new TargetWindowId(0x2222, 7777, FocusedElementId: "win32:100", ProcessName: "notepad");
        var adapter = new EvaluableFocusAdapter(call =>
            (new WindowsTextTargetAdapter.FocusedElementFacts(
                ProcessId: 7777,
                RuntimeId: string.Empty,
                HasKeyboardFocus: true,
                IsPassword: false,
                IsEnabled: true,
                IsKeyboardFocusable: true,
                IsControlEdit: true,
                ClassName: "Edit"),
            call == 1 ? "win32:100" : "win32:200"));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(new TextDeliveryRequest(
            new ProcessedText(DictationSessionId.Create(), "dictated words"),
            target,
            "en",
            TextDeliveryOptions.Default,
            SnippetExpanded: false));

        Assert.False(result.Delivered);
        Assert.True(result.ClipboardFallback);
        Assert.Equal(TextDeliveryRoute.ClipboardOnly, result.Route);
        Assert.Equal(TextDeliveryRefusalReason.TargetChanged, result.RefusalReason);
        Assert.False(adapter.PasteKeystrokeSent);
    }

    private sealed class EvaluableFocusAdapter(
        Func<int, (WindowsTextTargetAdapter.FocusedElementFacts? Focus, string? Win32ChildFocusId)> sample) : ITextTargetAdapter
    {
        private int _calls;

        public bool CommitAttempted { get; private set; }
        public bool PasteKeystrokeSent { get; private set; }

        public Task<TargetContextResult> CaptureContextAsync(
            TargetWindowId target,
            TextDeliveryOptions options,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            try
            {
                var (focus, win32ChildFocusId) = sample(call);
                var eval = WindowsTextTargetAdapter.EvaluateFocusedElement(
                    target,
                    focus,
                    win32ChildFocusId,
                    options.IsExplicitHistoryPaste,
                    (className, isEdit) => WindowsPastePolicy.Resolve(target.ProcessName, "Chrome_WidgetWin_1", className, isEdit));
                if (eval.Failure is not null)
                {
                    return Task.FromResult(eval.Failure);
                }

                return Task.FromResult(new TargetContextResult(
                    TargetContextStatus.Available,
                    new CaretContext(
                        target,
                        eval.EffectiveFocusId,
                        eval.Rule!.TargetKind,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        false,
                        false,
                        false,
                        false,
                        false)));
            }
            catch (AutomationRefusalException)
            {
                return Task.FromResult(new TargetContextResult(
                    TargetContextStatus.AccessibilityUnavailable,
                    RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable));
            }
        }

        public async Task<TextCommitResult> CommitAsync(
            TextCommitRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.ForcedRefusalReason != TextDeliveryRefusalReason.None)
            {
                return new TextCommitResult(
                    TextDeliveryRoute.ClipboardOnly,
                    Delivered: false,
                    ClipboardFallback: true,
                    ClipboardRestored: false,
                    request.ForcedRefusalReason);
            }

            CommitAttempted = true;
            var current = await CaptureContextAsync(request.Target, request.Options, cancellationToken).ConfigureAwait(false);
            if (current.Status != TargetContextStatus.Available ||
                current.Context is null ||
                request.ExpectedContext is null ||
                !WindowsTextTargetAdapter.CaretUnchanged(request.ExpectedContext, current.Context))
            {
                return new TextCommitResult(
                    TextDeliveryRoute.ClipboardOnly,
                    Delivered: false,
                    ClipboardFallback: true,
                    ClipboardRestored: false,
                    WindowsTextTargetAdapter.RevalidationRefusal(current));
            }

            PasteKeystrokeSent = true;
            return new TextCommitResult(
                TextDeliveryRoute.ClipboardPaste,
                Delivered: true,
                ClipboardFallback: false,
                ClipboardRestored: true);
        }

        public Task<TextCommitResult> CopyOnlyAsync(ProcessedText text, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingValue(Action throwAction)
    {
        public Action Throw { get; } = throwAction;
    }

    private sealed class FakeDataObject(
        IReadOnlyList<(string Format, object? Value)> entries,
        string? autoConvertUnicodeText = null) : System.Windows.Forms.IDataObject, System.Windows.Forms.ITypedDataObject
    {
        public object? GetData(string format) => GetData(format, autoConvert: true);

        public object? GetData(Type format) => GetData(format.FullName ?? string.Empty, autoConvert: true);

        public object? GetData(string format, bool autoConvert)
        {
            if (autoConvert &&
                string.Equals(format, System.Windows.Forms.DataFormats.UnicodeText, StringComparison.Ordinal) &&
                autoConvertUnicodeText is not null)
            {
                return autoConvertUnicodeText;
            }

            foreach (var entry in entries)
            {
                if (string.Equals(entry.Format, format, StringComparison.Ordinal))
                {
                    if (entry.Value is ThrowingValue throwing)
                    {
                        throwing.Throw();
                    }

                    return entry.Value;
                }
            }

            return null;
        }

        public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);

        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName ?? string.Empty, autoConvert: true);

        public bool GetDataPresent(string format, bool autoConvert)
        {
            if (autoConvert &&
                string.Equals(format, System.Windows.Forms.DataFormats.UnicodeText, StringComparison.Ordinal) &&
                autoConvertUnicodeText is not null)
            {
                return true;
            }

            return entries.Any(entry => string.Equals(entry.Format, format, StringComparison.Ordinal));
        }

        public string[] GetFormats() => GetFormats(autoConvert: true);

        public string[] GetFormats(bool autoConvert) =>
            entries.Select(entry => entry.Format).ToArray();

        public void SetData(string format, bool autoConvert, object? data) => throw new NotSupportedException();
        public void SetData(string format, object? data) => throw new NotSupportedException();
        public void SetData(Type format, object? data) => throw new NotSupportedException();
        public void SetData(object? data) => throw new NotSupportedException();

        public bool TryGetData<T>([NotNullWhen(true), MaybeNullWhen(false)] out T data)
        {
            data = default;
            return false;
        }

        public bool TryGetData<T>(string format, [NotNullWhen(true), MaybeNullWhen(false)] out T data) =>
            TryGetData(format, autoConvert: true, out data);

        public bool TryGetData<T>(string format, bool autoConvert, [NotNullWhen(true), MaybeNullWhen(false)] out T data)
        {
            if (GetData(format, autoConvert) is T typed)
            {
                data = typed;
                return true;
            }

            data = default;
            return false;
        }

        public bool TryGetData<T>(string format, Func<TypeName, Type?> resolver, bool autoConvert, [NotNullWhen(true), MaybeNullWhen(false)] out T data) =>
            TryGetData(format, autoConvert, out data);
    }

    private sealed class StaClipboardFixture : IDisposable
    {
        private readonly WindowsClipboardPaste.ClipboardSnapshot _snapshot;

        private StaClipboardFixture(WindowsClipboardPaste.ClipboardSnapshot snapshot) => _snapshot = snapshot;

        public static StaClipboardFixture Capture()
        {
            var snapshot = OnSta(() => WindowsClipboardPaste.TrySnapshotClipboard());
            Assert.NotNull(snapshot);
            return new StaClipboardFixture(snapshot!);
        }

        public static void SetDataObject(Func<System.Windows.Forms.DataObject> factory) => OnSta(() =>
        {
            System.Windows.Forms.Clipboard.SetDataObject(factory(), copy: true, retryTimes: 10, retryDelay: 50);
            return true;
        });

        public static string? GetText() => OnSta(static () =>
            System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null);

        public static object? GetData(string format) => OnSta(() =>
            System.Windows.Forms.Clipboard.GetDataObject()?.GetData(format, autoConvert: false));

        public static bool Restore(WindowsClipboardPaste.ClipboardSnapshot snapshot) =>
            OnSta(() => WindowsClipboardPaste.TryRestoreClipboard(snapshot));

        public static (bool Restored, bool Uncertain) RestoreWhileOurs(WindowsClipboardPaste.ClipboardSnapshot snapshot) =>
            OnSta(() => WindowsClipboardPaste.RestoreWhileOurs(snapshot, GetClipboardSequenceNumber()));

        public static void SetNativeClipboard(
            string unicodeText,
            IReadOnlyList<(string FormatName, byte[] Bytes)> customFormats) => OnSta(() =>
        {
            Assert.True(OpenClipboard(IntPtr.Zero));
            try
            {
                Assert.True(EmptyClipboard());
                var textBytes = System.Text.Encoding.Unicode.GetBytes(unicodeText + '\0');
                Assert.True(SetHGlobalFormat(13, textBytes));
                foreach (var (formatName, bytes) in customFormats)
                {
                    var fmt = RegisterClipboardFormat(formatName);
                    Assert.NotEqual(0u, fmt);
                    Assert.True(SetHGlobalFormat(fmt, bytes));
                }
            }
            finally
            {
                _ = CloseClipboard();
            }

            return true;
        });

        public static void SetNativeStandardFormats(
            IReadOnlyList<(uint FormatId, byte[] Bytes)> standardFormats) => OnSta(() =>
        {
            Assert.True(OpenClipboard(IntPtr.Zero));
            try
            {
                Assert.True(EmptyClipboard());
                foreach (var (formatId, bytes) in standardFormats)
                {
                    Assert.True(SetHGlobalFormat(formatId, bytes));
                }
            }
            finally
            {
                _ = CloseClipboard();
            }

            return true;
        });

        public static byte[]? GetRawStandardFormatBytes(uint formatId) => OnSta(() =>
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                return null;
            }

            try
            {
                var handle = GetClipboardData(formatId);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                var size = GlobalSize(handle);
                if (size == UIntPtr.Zero)
                {
                    return null;
                }

                var locked = GlobalLock(handle);
                if (locked == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var bytes = new byte[checked((int)size)];
                    Marshal.Copy(locked, bytes, 0, bytes.Length);
                    return bytes;
                }
                finally
                {
                    _ = GlobalUnlock(handle);
                }
            }
            finally
            {
                _ = CloseClipboard();
            }
        });

        public static byte[]? GetRawFormatBytes(string formatName)
        {
            var fmt = RegisterClipboardFormat(formatName);
            return fmt == 0 ? null : GetRawStandardFormatBytes(fmt);
        }

        private static bool SetHGlobalFormat(uint format, byte[] bytes)
        {
            var hMem = GlobalAlloc(0x0002, (UIntPtr)bytes.Length);
            if (hMem == IntPtr.Zero)
            {
                return false;
            }

            var locked = GlobalLock(hMem);
            if (locked == IntPtr.Zero)
            {
                _ = GlobalFree(hMem);
                return false;
            }

            try
            {
                Marshal.Copy(bytes, 0, locked, bytes.Length);
            }
            finally
            {
                _ = GlobalUnlock(hMem);
            }

            if (SetClipboardData(format, hMem) == IntPtr.Zero)
            {
                _ = GlobalFree(hMem);
                return false;
            }

            return true;
        }

        public void Dispose()
        {
            Assert.True(OnSta(() => WindowsClipboardPaste.TryRestoreClipboard(_snapshot)));
        }

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr newOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterClipboardFormat(string formatName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr GlobalSize(IntPtr memory);

        private static T OnSta<T>(Func<T> operation)
        {
            T? result = default;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    result = operation();
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                {
                    failure = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure is not null)
            {
                throw failure;
            }

            return result!;
        }
    }

    private sealed class WindowLevelTargetAdapter : ITextTargetAdapter
    {
        public List<TextCommitRequest> Commits { get; } = [];

        public Task<TargetContextResult> CaptureContextAsync(
            TargetWindowId target,
            TextDeliveryOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TargetContextResult(
                TargetContextStatus.Available,
                new CaretContext(
                    target,
                    string.Empty,
                    TextTargetKind.Unknown,
                    Left: string.Empty,
                    Selection: string.Empty,
                    Right: string.Empty,
                    LeftReachedDocumentStart: false,
                    RightReachedDocumentEnd: false,
                    HasTextContext: false,
                    SupportsDirectValueWrite: false,
                    DirectValueWriteAtEnd: false)));

        public Task<TextCommitResult> CommitAsync(
            TextCommitRequest request,
            CancellationToken cancellationToken = default)
        {
            Commits.Add(request);
            return Task.FromResult(new TextCommitResult(
                TextDeliveryRoute.ClipboardPaste,
                Delivered: true,
                ClipboardFallback: false,
                ClipboardRestored: true));
        }

        public Task<TextCommitResult> CopyOnlyAsync(
            ProcessedText text,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRecoveryStore : Core.Reliability.IRecoveryTextStore
    {
        public Task<Core.Reliability.RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Core.Reliability.RecoveryTextLoadResult(Core.Reliability.RecoveryTextLoadStatus.Missing));

        public Task<bool> SaveAsync(Core.Reliability.RecoveryTextRecord record, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeHistoryStore(IReadOnlyList<DictationHistoryEntry> entries) : IHistoryStore
    {
        public Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryLoadResult(entries, HistoryLoadStatus.Loaded));

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(false));

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(false));

        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));
    }

    private sealed class FakeEffects : ISessionPersistenceEffects
    {
        public void ShowPendingRecovery(Core.Reliability.RecoveryTextRecord record, bool undoOffered) { }
        public void ClearRecoveredText() { }
        public void NotifyHistoryChanged() { }
    }

    private sealed class NullLogger : Core.Diagnostics.IAppLogger
    {
        public void Write(Core.Diagnostics.AppLogEntry entry) { }
    }

    [Fact]
    public void NormalizeArrivalTextStripsUnicodeFormatCharactersForRichWebEditors()
    {
        Assert.Equal("Can you ask them", WindowsTextDeliveryPolicy.NormalizeArrivalText("\uFEFFCan you\u200B ask them\u2060"));

        var slateArrival = WindowsTextDeliveryPolicy.EvaluateArrival(
            insertedText: "ask them ",
            observedLeft: "Can you \uFEFFask them\u200B ",
            observedValue: null,
            previousLeft: "Can you \uFEFF");
        Assert.Equal(PasteArrivalStatus.Confirmed, slateArrival);
    }

    [Fact]
    public void CaretUnchangedSkipsTextComparisonOnlyWhenDictatedTextCannotBeDeleted()
    {
        var target = new TargetWindowId(0x2001, 42, "WindowsTerminal");
        var expected = new CaretContext(
            target,
            "term-focus",
            TextTargetKind.Terminal,
            Left: "Can you ",
            Selection: string.Empty,
            Right: string.Empty,
            LeftReachedDocumentStart: true,
            RightReachedDocumentEnd: true,
            HasTextContext: true,
            SupportsDirectValueWrite: false,
            DirectValueWriteAtEnd: false,
            IsScreenDerived: true);
        var spinnerDrifted = expected with { Left = "Can you still ", Right = string.Empty };

        Assert.False(WindowsTextDeliveryPolicy.CaretUnchanged(expected, spinnerDrifted, checkCaretTextUnchanged: true));
        Assert.True(WindowsTextDeliveryPolicy.CaretUnchanged(expected, spinnerDrifted, checkCaretTextUnchanged: false));

        // Window/focus/target-kind mismatches must still fail even when caret text check is skipped.
        var differentFocus = expected with { FocusedElementId = "other-focus" };
        var differentWindow = expected with { Target = new TargetWindowId(0x2002, 42, "WindowsTerminal") };
        Assert.False(WindowsTextDeliveryPolicy.CaretUnchanged(expected, differentFocus, checkCaretTextUnchanged: false));
        Assert.False(WindowsTextDeliveryPolicy.CaretUnchanged(expected, differentWindow, checkCaretTextUnchanged: false));

        // Honest editable targets always revalidate caret text even for non-deleting insertions.
        var honestEdit = expected with { TargetKind = TextTargetKind.StandardEdit, IsScreenDerived = false };
        Assert.True(WindowsTextDeliveryPolicy.ShouldCheckCaretTextUnchanged(honestEdit, candidateDeletesDictatedText: false));
        Assert.True(WindowsTextDeliveryPolicy.ShouldCheckCaretTextUnchanged(honestEdit, candidateDeletesDictatedText: true));
        Assert.False(WindowsTextDeliveryPolicy.ShouldCheckCaretTextUnchanged(expected, candidateDeletesDictatedText: false));
        Assert.True(WindowsTextDeliveryPolicy.ShouldCheckCaretTextUnchanged(expected, candidateDeletesDictatedText: true));
    }

    [Fact]
    public void ShouldRetryColdChromiumFocusMatchesTransientTopLevelChromeWidgetOnly()
    {
        var coldChromium = new WindowsTextTargetAdapter.FocusedElementFacts(
            ProcessId: 100,
            RuntimeId: "1.100",
            HasKeyboardFocus: false,
            IsPassword: false,
            IsEnabled: true,
            IsKeyboardFocusable: true,
            IsControlEdit: false,
            ClassName: "Chrome_WidgetWin_1");
        Assert.True(WindowsTextDeliveryPolicy.ShouldRetryColdChromiumFocus(coldChromium));

        Assert.False(WindowsTextDeliveryPolicy.ShouldRetryColdChromiumFocus(coldChromium with { HasKeyboardFocus = true }));
        Assert.False(WindowsTextDeliveryPolicy.ShouldRetryColdChromiumFocus(coldChromium with { ClassName = "Chrome_RenderWidgetHostHWND" }));
        Assert.False(WindowsTextDeliveryPolicy.ShouldRetryColdChromiumFocus(null));
    }
}
