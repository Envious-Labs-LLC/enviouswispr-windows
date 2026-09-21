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
    public void TheProductionAdapterNamesOnlyWindowsFailuresAsAccessibilityUnavailable()
    {
        // THE FILTER IS THE ADAPTER'S BOUNDARY (plan-2 step 13). UI Automation's own
        // InvalidOperationException - an unsupported pattern, a refused operation - is the
        // environment; one raised by this adapter's code is a defect; an ObjectDisposedException,
        // which derives from InvalidOperationException, is the app leaving. Only the first is
        // answered "accessibility unavailable"; the others escape to the delivery, which names them.
        var fromAutomation = new InvalidOperationException("Unsupported Pattern.") { Source = "UIAutomationClient" };
        var fromUs = new InvalidOperationException("a defect of ours");
        var disposedFromAutomation = new ObjectDisposedException("gate") { Source = "UIAutomationClient" };

        Assert.True(WindowsTextTargetAdapter.IsExpectedAutomationFailure(fromAutomation));
        Assert.False(WindowsTextTargetAdapter.IsExpectedAutomationFailure(fromUs));
        Assert.False(WindowsTextTargetAdapter.IsExpectedAutomationFailure(new ObjectDisposedException("gate")));
        Assert.False(WindowsTextTargetAdapter.IsExpectedAutomationFailure(disposedFromAutomation));
        Assert.True(WindowsTextTargetAdapter.IsExpectedAutomationFailure(new ElementNotAvailableException()));
        Assert.True(WindowsTextTargetAdapter.IsExpectedAutomationFailure(Marshal.GetExceptionForHR(unchecked((int)0x80040201))!));
        Assert.True(WindowsTextTargetAdapter.IsExpectedAutomationFailure(new UnauthorizedAccessException()));
        Assert.True(WindowsTextTargetAdapter.IsExpectedAutomationFailure(new Win32Exception(5)));
        Assert.False(WindowsTextTargetAdapter.IsExpectedAutomationFailure(new InvalidCastException()));
        Assert.False(WindowsTextTargetAdapter.IsExpectedAutomationFailure(new OperationCanceledException()));
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
        Assert.Equal(new DeliveryFault(DeliveryStage.ContextCapture, nameof(ObjectDisposedException)), result.Fault);
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
