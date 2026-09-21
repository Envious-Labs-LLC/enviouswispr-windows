using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Input;

namespace EnviousWispr.Architecture.Tests;

public sealed class WindowsTextDeliverySafetyTests
{
    [Fact]
    public void TheProductionAdapterNamesOnlyWhatAUiAutomationCallRefusedAsAccessibilityUnavailable()
    {
        // THE BOUNDARY IS THE CALL, NOT THE TYPE (plan-2 step 13). UI Automation hands most of its
        // failures to Marshal.ThrowExceptionForHR, so a refused operation arrives as an
        // InvalidOperationException raised by the runtime itself - the same type, from the same
        // place, as a defect of ours. Inside Automation(...) it is the control refusing and is
        // carried out as AutomationRefusalException; a disposal is never a refusal and comes out
        // unchanged; a cast failure inside is a defect and comes out unchanged; an
        // InvalidOperationException outside any call is ours.
        const int InvalidOperationHResult = unchecked((int)0x80131509);
        const int ElementNotAvailableHResult = unchecked((int)0x80040201);

        var fromHResult = Assert.Throws<AutomationRefusalException>(() =>
            WindowsTextTargetAdapter.Automation(() =>
            {
                Marshal.ThrowExceptionForHR(InvalidOperationHResult);
                return 0;
            }));
        Assert.IsType<InvalidOperationException>(fromHResult.InnerException);
        // RAISED BY THE RUNTIME, NOT BY UI AUTOMATION: the throwing assembly cannot be the test.
        Assert.NotEqual("UIAutomationClient", fromHResult.InnerException.Source);

        var fromComHResult = Assert.Throws<AutomationRefusalException>(() =>
            WindowsTextTargetAdapter.Automation(() =>
            {
                Marshal.ThrowExceptionForHR(ElementNotAvailableHResult);
                return 0;
            }));
        Assert.IsAssignableFrom<COMException>(fromComHResult.InnerException);

        var notEnabled = Assert.Throws<AutomationRefusalException>(() =>
            WindowsTextTargetAdapter.Automation<int>(() => throw new ElementNotEnabledException()));
        Assert.IsType<ElementNotEnabledException>(notEnabled.InnerException);
        Assert.IsType<ElementNotAvailableException>(Assert.Throws<AutomationRefusalException>(() =>
            WindowsTextTargetAdapter.Automation<int>(() => throw new ElementNotAvailableException())).InnerException);
        Assert.IsType<UnauthorizedAccessException>(Assert.Throws<AutomationRefusalException>(() =>
            WindowsTextTargetAdapter.Automation<int>(() => throw new UnauthorizedAccessException())).InnerException);
        Assert.IsType<Win32Exception>(Assert.Throws<AutomationRefusalException>(() =>
            WindowsTextTargetAdapter.Automation<int>(() => throw new Win32Exception(5))).InnerException);

        Assert.Throws<ObjectDisposedException>(() =>
            WindowsTextTargetAdapter.Automation<int>(() => throw new ObjectDisposedException("gate")));
        Assert.Throws<InvalidCastException>(() =>
            WindowsTextTargetAdapter.Automation<int>(() => throw new InvalidCastException()));
        Assert.Throws<OperationCanceledException>(() =>
            WindowsTextTargetAdapter.Automation<int>(() => throw new OperationCanceledException()));

        Assert.True(WindowsTextTargetAdapter.IsExpectedAutomationFailure(fromHResult));
        Assert.False(WindowsTextTargetAdapter.IsExpectedAutomationFailure(new InvalidOperationException("a defect of ours")));
        Assert.False(WindowsTextTargetAdapter.IsExpectedAutomationFailure(new ObjectDisposedException("gate")));
        Assert.False(WindowsTextTargetAdapter.IsExpectedAutomationFailure(fromHResult.InnerException));
        Assert.Equal(1, WindowsTextTargetAdapter.Automation(() => 1));
    }

    [Fact]
    public void EveryUiAutomationCallInTheAdapterIsMadeThroughTheBoundary()
    {
        // NOTHING ELSE TOUCHES UI AUTOMATION: a call made outside Automation(...) would answer a
        // refusal as a fault. Read at the source: every line that reads an element, a pattern or a
        // range sits inside an Automation(...) call or inside ReadCaret, which is only ever called
        // through one.
        var adapter = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Production", "EnviousWispr.Services", "Input", "WindowsTextTargetAdapter.cs"));
        var readCaret = adapter.IndexOf("private static CaretText? ReadCaret(", StringComparison.Ordinal);
        var readCaretEnd = adapter.IndexOf("\n    }\n", readCaret, StringComparison.Ordinal);
        var outside = adapter[..readCaret] + adapter[readCaretEnd..];
        Assert.Contains("Automation(() => ReadCaret(textPattern, options))", adapter, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(adapter, "ReadCaret(textPattern, options)"));
        foreach (var line in outside.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.Contains("AutomationElement.FocusedElement", StringComparison.Ordinal) ||
                trimmed.Contains(".Current.", StringComparison.Ordinal) ||
                trimmed.Contains("TryGetCurrentPattern(", StringComparison.Ordinal) ||
                trimmed.Contains(".SetValue(", StringComparison.Ordinal) ||
                trimmed.Contains(".GetSelection(", StringComparison.Ordinal))
            {
                Assert.True(
                    trimmed.Contains("Automation(", StringComparison.Ordinal) || IsInsideAutomationCall(outside, line),
                    $"A UI Automation call outside the boundary: {trimmed}");
            }
        }
    }

    private static bool IsInsideAutomationCall(string source, string line)
    {
        // The call opens on an earlier line ("Automation(() =>" or "Automation(static () =>") and has
        // not closed by this one: count the parentheses between.
        var at = source.IndexOf(line, StringComparison.Ordinal);
        var open = source.LastIndexOf("Automation(", at, StringComparison.Ordinal);
        if (open < 0)
        {
            return false;
        }

        var depth = 0;
        for (var index = open + "Automation".Length; index < at; index++)
        {
            depth += source[index] switch { '(' => 1, ')' => -1, _ => 0 };
        }

        return depth > 0;
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    [Fact]
    public async Task ADisposedProductionAdapterSurfacesItsDisposalNotAnAccessibilityFailure()
    {
        // THE PRODUCTION ADAPTER, DISPOSED, THEN ASKED. Its automation gate throws
        // ObjectDisposedException before any window is touched; the adapter used to swallow that as
        // "accessibility unavailable" through its InvalidOperationException arm. It escapes now, and
        // the delivery over it answers DeliveryDisposed with the words kept.
        var adapter = new WindowsTextTargetAdapter();
        adapter.Dispose();
        var target = new TargetWindowId(42, 7, "1.2.3");

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            adapter.CaptureContextAsync(target, TextDeliveryOptions.Default));

        var delivery = new ContextAwareTextDelivery(adapter);
        var result = await delivery.DeliverAsync(new TextDeliveryRequest(
            new ProcessedText(new DictationSessionId(Guid.NewGuid()), "kept"),
            target,
            "en",
            TextDeliveryOptions.Default));

        Assert.Equal(TextDeliveryRefusalReason.DeliveryDisposed, result.RefusalReason);
        Assert.Equal(new DeliveryFault(DeliveryStage.ContextCapture, DeliveryFaultKind.ObjectDisposed, nameof(ObjectDisposedException)), result.Fault);
        Assert.Equal("kept", delivery.RecoveryText?.Text);
    }

    [Fact]
    public void ClipboardSnapshotClonesKnownValuesAndRefusesUnknownReferences()
    {
        var bytes = new byte[] { 1, 2, 3 };

        var clonedBytes = Assert.IsType<byte[]>(
            WindowsClipboardPaste.CloneClipboardValue(bytes));

        Assert.Equal(bytes, clonedBytes);
        Assert.NotSame(bytes, clonedBytes);
        Assert.Equal("immutable", WindowsClipboardPaste.CloneClipboardValue("immutable"));
        Assert.Null(WindowsClipboardPaste.CloneClipboardValue(new object()));
    }

    [Theory]
    [InlineData(0, 16_384)]
    [InlineData(4_097, 16_384)]
    [InlineData(256, 0)]
    [InlineData(256, 1_048_577)]
    public async Task DeliveryOptionsRejectUnboundedNativeReadsAndWrites(
        int contextWindowCharacters,
        int maximumDirectValueCharacters)
    {
        using var adapter = new WindowsTextTargetAdapter();
        var options = new TextDeliveryOptions(
            RestoreClipboardAfterPaste: true,
            contextWindowCharacters,
            maximumDirectValueCharacters);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            adapter.CaptureContextAsync(new TargetWindowId(1), options));
    }

    [Fact]
    public void SendInputLayoutMatchesTheWin64Abi()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(40, WindowsClipboardPaste.NativeInputSize);
        Assert.Equal(24, WindowsClipboardPaste.NativeKeyboardInputSize);
        Assert.Equal(4, WindowsClipboardPaste.NativeKeyboardFlagsOffset);
    }

    [Theory]
    [InlineData(TextTargetKind.Terminal, "safe command", TextDeliveryRefusalReason.None)]
    [InlineData(
        TextTargetKind.Terminal,
        "unsafe command\r\n",
        TextDeliveryRefusalReason.UnsafeMultilineTarget)]
    [InlineData(
        TextTargetKind.Game,
        "hello",
        TextDeliveryRefusalReason.UnsupportedTarget)]
    [InlineData(TextTargetKind.Browser, "hello", TextDeliveryRefusalReason.None)]
    public void CompatibilityPolicyRefusesOnlyPinnedUnsafeShapes(
        TextTargetKind kind,
        string text,
        TextDeliveryRefusalReason expected)
    {
        Assert.Equal(expected, WindowsTextTargetAdapter.CompatibilityRefusal(kind, text));
    }

    [Fact]
    public void CaretIdentityIncludesElementAndBoundedSeam()
    {
        var target = new TargetWindowId(42, 7, "1.2.3");
        var expected = Context(target, "left", "", "right");

        Assert.True(WindowsTextTargetAdapter.CaretUnchanged(
            expected,
            Context(target, "left", "", "right")));
        Assert.False(WindowsTextTargetAdapter.CaretUnchanged(
            expected,
            Context(target with { FocusedElementId = "9.9.9" }, "left", "", "right")));
        Assert.False(WindowsTextTargetAdapter.CaretUnchanged(
            expected,
            Context(target, "changed", "", "right")));
    }

    private static CaretContext Context(
        TargetWindowId target,
        string left,
        string selection,
        string right) => new(
        target,
        target.FocusedElementId!,
        TextTargetKind.StandardEdit,
        left,
        selection,
        right,
        LeftReachedDocumentStart: true,
        RightReachedDocumentEnd: true,
        HasTextContext: true,
        SupportsDirectValueWrite: true,
        DirectValueWriteAtEnd: true);
}
