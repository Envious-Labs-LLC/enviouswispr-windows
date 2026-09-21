using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Input;

namespace EnviousWispr.Architecture.Tests;

/// <summary>A fact that needs the desk's clipboard; skipped where no clipboard can be reached.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ClipboardFactAttribute : FactAttribute
{
    public ClipboardFactAttribute()
    {
        if (!WindowsTextDeliverySafetyTests.ClipboardAvailable())
        {
            Skip = "No clipboard can be reached from this session; the production-paste half did not run.";
        }
    }
}

public sealed class WindowsTextDeliverySafetyTests
{
    internal static bool ClipboardAvailable() => ClipboardGuard.Available();

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
        // refusal as a fault. Read at the source, by offset: every occurrence of an element, pattern
        // or range access sits inside an Automation(...) call, or inside one of the two helpers that
        // are only ever called from inside one (ReadCaret, RuntimeId). A second, bare occurrence of
        // a line that also appears wrapped is caught, because each occurrence is judged where it is.
        var adapter = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Production", "EnviousWispr.Services", "Input", "WindowsTextTargetAdapter.cs"));
        var helpers = new[]
        {
            HelperSpan(adapter, "private static CaretText? ReadCaret("),
            HelperSpan(adapter, "private static string RuntimeId(AutomationElement element) =>"),
        };
        foreach (var call in new[] { "ReadCaret(", "RuntimeId(" })
        {
            var uses = Occurrences(adapter, call).Where(at => !helpers.Any(span => at >= span.Start && at < span.End) && !adapter[..at].EndsWith("static string ", StringComparison.Ordinal) && !adapter[..at].EndsWith("static CaretText? ", StringComparison.Ordinal)).ToArray();
            var use = Assert.Single(uses);
            Assert.True(IsInsideAutomationCall(adapter, use), $"{call} is called outside the boundary");
        }

        var accesses = new[]
        {
            "AutomationElement.FocusedElement", ".Current.", "TryGetCurrentPattern(", ".SetValue(", ".GetSelection(",
            ".DocumentRange", ".Clone()", ".MoveEndpointByRange(", ".MoveEndpointByUnit(", ".CompareEndpoints(", ".GetText(", ".GetRuntimeId(",
        };
        var offset = 0;
        foreach (var line in adapter.Split('\n'))
        {
            var trimmed = line.TrimStart();
            var isComment = trimmed.StartsWith("//", StringComparison.Ordinal);
            foreach (var access in accesses)
            {
                for (var at = line.IndexOf(access, StringComparison.Ordinal); at >= 0 && !isComment; at = line.IndexOf(access, at + access.Length, StringComparison.Ordinal))
                {
                    var here = offset + at;
                    var covered = helpers.Any(span => here >= span.Start && here < span.End) || IsInsideAutomationCall(adapter, here);
                    Assert.True(covered, $"A UI Automation access outside the boundary at offset {here}: {trimmed.Trim()}");
                }
            }

            offset += line.Length + 1;
        }
    }

    /// <summary>The span of a helper method: from its signature to its closing brace at the method's indentation, or to the end of its expression body.</summary>
    private static (int Start, int End) HelperSpan(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"the helper is gone: {signature}");
        var end = signature.EndsWith("=>", StringComparison.Ordinal)
            ? source.IndexOf(";\n", start, StringComparison.Ordinal)
            : source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, $"the helper has no end: {signature}");
        return (start, end);
    }

    private static bool IsInsideAutomationCall(string source, int at)
    {
        // THE SPAN OF A CALL ENDS WHERE ITS OWN PARENTHESIS CLOSES, not where the running depth
        // next reaches zero: an access after a wrapped call, inside some other call's parentheses,
        // is outside the boundary. Every earlier opening is tried, so a call that encloses this
        // offset is found whatever sits between.
        var open = source.LastIndexOf("Automation(", at, StringComparison.Ordinal);
        while (open >= 0)
        {
            var close = ClosingParenthesis(source, open + "Automation".Length);
            if (close > at)
            {
                return true;
            }

            open = open == 0 ? -1 : source.LastIndexOf("Automation(", open - 1, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>The offset of the parenthesis that closes the one at <paramref name="openParenthesis"/>, or -1.</summary>
    private static int ClosingParenthesis(string source, int openParenthesis)
    {
        var depth = 0;
        for (var index = openParenthesis; index < source.Length; index++)
        {
            depth += source[index] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static IEnumerable<int> Occurrences(string text, string needle)
    {
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            yield return at;
        }
    }

    [Theory]
    [InlineData(TargetContextStatus.AccessibilityUnavailable, TextDeliveryRefusalReason.AccessibilityUnavailable, TextDeliveryRefusalReason.AccessibilityUnavailable)]
    [InlineData(TargetContextStatus.AccessibilityUnavailable, TextDeliveryRefusalReason.UnsupportedTarget, TextDeliveryRefusalReason.UnsupportedTarget)]
    [InlineData(TargetContextStatus.AccessibilityUnavailable, TextDeliveryRefusalReason.None, TextDeliveryRefusalReason.AccessibilityUnavailable)]
    [InlineData(TargetContextStatus.Elevated, TextDeliveryRefusalReason.ElevatedTarget, TextDeliveryRefusalReason.ElevatedTarget)]
    [InlineData(TargetContextStatus.Protected, TextDeliveryRefusalReason.ProtectedField, TextDeliveryRefusalReason.ProtectedField)]
    [InlineData(TargetContextStatus.TargetUnavailable, TextDeliveryRefusalReason.TargetUnavailable, TextDeliveryRefusalReason.TargetChanged)]
    [InlineData(TargetContextStatus.TargetChanged, TextDeliveryRefusalReason.TargetChanged, TextDeliveryRefusalReason.TargetChanged)]
    [InlineData(TargetContextStatus.Available, TextDeliveryRefusalReason.None, TextDeliveryRefusalReason.TargetChanged)]
    public void TheCommitsSecondReadKeepsItsOwnName(TargetContextStatus status, TextDeliveryRefusalReason carried, TextDeliveryRefusalReason expected)
    {
        // THE COMMIT READS THE TARGET AGAIN JUST BEFORE THE WRITE. Accessibility that did not answer
        // the second time used to be filed as "target changed"; it says what it is now, with the
        // reason the capture gave. An available target whose caret moved is a changed target.
        var current = new TargetContextResult(status, RefusalReason: carried);

        Assert.Equal(expected, WindowsTextTargetAdapter.RevalidationRefusal(current));
    }

    [Theory]
    [InlineData(TargetContextStatus.AccessibilityUnavailable, TextDeliveryRefusalReason.UnsupportedTarget, TextDeliveryRefusalReason.UnsupportedTarget)]
    [InlineData(TargetContextStatus.AccessibilityUnavailable, TextDeliveryRefusalReason.AccessibilityUnavailable, TextDeliveryRefusalReason.AccessibilityUnavailable)]
    [InlineData(TargetContextStatus.AccessibilityUnavailable, TextDeliveryRefusalReason.None, TextDeliveryRefusalReason.AccessibilityUnavailable)]
    [InlineData(TargetContextStatus.Protected, TextDeliveryRefusalReason.ProtectedField, TextDeliveryRefusalReason.ProtectedField)]
    [InlineData(TargetContextStatus.Elevated, TextDeliveryRefusalReason.None, TextDeliveryRefusalReason.ElevatedTarget)]
    [InlineData(TargetContextStatus.TargetChanged, TextDeliveryRefusalReason.TargetChanged, TextDeliveryRefusalReason.TargetChanged)]
    public void ThePastesLastLookKeepsTheCapturesOwnRefusal(TargetContextStatus status, TextDeliveryRefusalReason carried, TextDeliveryRefusalReason expected)
    {
        // THE PREFLIGHT'S CAPTURE NAMES ITS OWN REFUSAL: a selection that became unsupported
        // between the commit's read and the keystroke is UnsupportedTarget, not "accessibility
        // unavailable"; the status is the answer only when the capture gave no reason.
        var expectedContext = Context(new TargetWindowId(42, 7, "1.2.3"), "before", string.Empty, "after");

        Assert.Equal(expected, WindowsTextTargetAdapter.PreflightRefusal(new TargetContextResult(status, RefusalReason: carried), expectedContext));
        Assert.Equal(
            TextDeliveryRefusalReason.None,
            WindowsTextTargetAdapter.PreflightRefusal(new TargetContextResult(TargetContextStatus.Available, expectedContext), expectedContext));
        Assert.Equal(
            TextDeliveryRefusalReason.TargetChanged,
            WindowsTextTargetAdapter.PreflightRefusal(new TargetContextResult(TargetContextStatus.Available, expectedContext with { Left = "moved" }), expectedContext));
    }

    [ClipboardFact]
    public async Task ASelectionThatBecameUnsupportedAtTheLastLookReachesTheDeliveryUnderItsOwnName()
    {
        // THROUGH THE PRODUCTION PASTE AND THE DELIVERY: the preflight's capture answers
        // AccessibilityUnavailable / UnsupportedTarget, and what comes back is UnsupportedTarget with
        // the fallback words on the clipboard - the code, the sentence and the words agree.
        using var guard = ClipboardGuard.Capture();
        var expectedContext = Context(new TargetWindowId(42, 7, "1.2.3"), "before", string.Empty, "after");
        var delivery = new ContextAwareTextDelivery(new ProductionPastingAdapter(() => WindowsTextTargetAdapter.PreflightRefusal(
            new TargetContextResult(TargetContextStatus.AccessibilityUnavailable, RefusalReason: TextDeliveryRefusalReason.UnsupportedTarget),
            expectedContext)));

        var result = await delivery.DeliverAsync(new TextDeliveryRequest(
            new ProcessedText(new DictationSessionId(Guid.NewGuid()), "kept"),
            new TargetWindowId(42, 7, "1.2.3"),
            "en",
            TextDeliveryOptions.Default));

        Assert.Equal(TextDeliveryRefusalReason.UnsupportedTarget, result.RefusalReason);
        Assert.True(result.ClipboardFallback);
        Assert.Equal(TextDeliveryRoute.ClipboardOnly, result.Route);
        Assert.Null(result.Fault);
        Assert.Equal("Automatic paste is unsafe here, so the text was copied only", EnviousWispr.Core.Presentation.DeliveryStatusReport.For(result).Text);
        Assert.Equal("kept ", ClipboardGuard.GetText());
    }

    [Fact]
    public void ThePastesPreflightTranslatesOnlyWhatAccessibilityRefused()
    {
        // THE PREFLIGHT RUNS ON THE CLIPBOARD THREAD, inside the paste, where nothing knows UI
        // Automation. What accessibility refused is translated here, in the adapter; a defect in the
        // preflight comes out to be named - not answered "clipboard unavailable".
        Assert.Equal(TextDeliveryRefusalReason.None, WindowsTextTargetAdapter.GuardedPreflight(() => TextDeliveryRefusalReason.None));
        Assert.Equal(TextDeliveryRefusalReason.TargetChanged, WindowsTextTargetAdapter.GuardedPreflight(() => TextDeliveryRefusalReason.TargetChanged));
        Assert.Equal(
            TextDeliveryRefusalReason.AccessibilityUnavailable,
            WindowsTextTargetAdapter.GuardedPreflight(() => throw new AutomationRefusalException("refused", new InvalidOperationException())));
        Assert.Throws<InvalidCastException>(() => WindowsTextTargetAdapter.GuardedPreflight(() => throw new InvalidCastException()));
        Assert.Throws<ObjectDisposedException>(() => WindowsTextTargetAdapter.GuardedPreflight(() => throw new ObjectDisposedException("gate")));
    }

    [ClipboardFact]
    public async Task APreflightDefectDuringTheProductionPasteComesOutNamedWithTheClipboardRestoredAndNothingPasted()
    {
        // THE PRODUCTION PASTE, ON THE REAL CLIPBOARD, WITH A PREFLIGHT THAT THROWS. The words are on
        // the clipboard by then and nothing has been pasted; the paste puts the clipboard back
        // (the sentinel placed before it is there after it), the defect comes out of the paste as
        // itself - not as "clipboard unavailable" - and the delivery over an adapter that pastes
        // through the production paste names it DeliveryFaulted at the commit, with the words kept.
        var sentinel = $"EnviousWispr clipboard sentinel {Guid.NewGuid():N}";
        using var guard = ClipboardGuard.Capture();
        ClipboardGuard.SetText(sentinel);

        await Assert.ThrowsAsync<InvalidCastException>(() => WindowsClipboardPaste.PasteAsync(
            "words that must not be pasted",
            "words that must not be pasted ",
            restoreClipboard: true,
            static () => throw new InvalidCastException("a defect in the preflight"),
            CancellationToken.None));
        Assert.Equal(sentinel, ClipboardGuard.GetText());

        var delivery = new ContextAwareTextDelivery(new ProductionPastingAdapter(static () => throw new InvalidCastException("a defect in the preflight")));
        var result = await delivery.DeliverAsync(new TextDeliveryRequest(
            new ProcessedText(new DictationSessionId(Guid.NewGuid()), "kept"),
            new TargetWindowId(42, 7, "1.2.3"),
            "en",
            TextDeliveryOptions.Default));

        Assert.Equal(TextDeliveryRefusalReason.DeliveryFaulted, result.RefusalReason);
        Assert.Equal(new DeliveryFault(DeliveryStage.Commit, DeliveryFaultKind.InvalidCast, nameof(InvalidCastException)), result.Fault);
        Assert.False(result.ClipboardFallback);
        Assert.Equal("kept", delivery.RecoveryText?.Text);
        Assert.Equal(sentinel, ClipboardGuard.GetText());
    }

    [ClipboardFact]
    public async Task AccessibilityRefusedInTheProductionPastesPreflightLeavesTheFallbackOnTheClipboardWithItsName()
    {
        // THE SAME PASTE, WITH ACCESSIBILITY REFUSING IN THE PREFLIGHT (through the adapter's guard):
        // nothing is pasted, the fallback words are left on the clipboard for the person to paste, and
        // the refusal keeps its name through the delivery.
        using var guard = ClipboardGuard.Capture();
        ClipboardGuard.SetText("before");

        var pasted = await WindowsClipboardPaste.PasteAsync(
            "words",
            "words ",
            restoreClipboard: true,
            () => WindowsTextTargetAdapter.GuardedPreflight(static () => throw new AutomationRefusalException("refused", new InvalidOperationException())),
            CancellationToken.None);

        Assert.Equal(TextDeliveryRoute.ClipboardOnly, pasted.Route);
        Assert.True(pasted.ClipboardFallback);
        Assert.False(pasted.Delivered);
        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, pasted.RefusalReason);
        Assert.Equal("words ", ClipboardGuard.GetText());

        var delivery = new ContextAwareTextDelivery(new ProductionPastingAdapter(static () => throw new AutomationRefusalException("refused", new InvalidOperationException())));
        var result = await delivery.DeliverAsync(new TextDeliveryRequest(
            new ProcessedText(new DictationSessionId(Guid.NewGuid()), "kept"),
            new TargetWindowId(42, 7, "1.2.3"),
            "en",
            TextDeliveryOptions.Default));

        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, result.RefusalReason);
        Assert.True(result.ClipboardFallback);
        Assert.Null(result.Fault);
        Assert.Null(delivery.RecoveryText);
    }

    /// <summary>An adapter whose context is a standard field that cannot be written directly and whose commit is the production paste with the given preflight.</summary>
    private sealed class ProductionPastingAdapter(Func<TextDeliveryRefusalReason> preflight) : ITextTargetAdapter
    {
        public Task<TextCommitResult> CopyOnlyAsync(ProcessedText text, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TargetContextResult> CaptureContextAsync(TargetWindowId target, TextDeliveryOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TargetContextResult(TargetContextStatus.Available, Context(target, "before", string.Empty, "after")));

        public Task<TextCommitResult> CommitAsync(TextCommitRequest request, CancellationToken cancellationToken = default) =>
            WindowsClipboardPaste.PasteAsync(
                request.Text.Text,
                request.LegacyText.Text,
                request.Options.RestoreClipboardAfterPaste,
                () => WindowsTextTargetAdapter.GuardedPreflight(preflight),
                cancellationToken);
    }

    /// <summary>
    /// The desk's clipboard, every format copied through the production snapshot before a test
    /// touches it, and put back whole when the test ends - by the using, so on a failed assertion
    /// too. A capture that cannot copy every format refuses, and the test does not run against a
    /// clipboard it could not restore.
    /// </summary>
    private sealed class ClipboardGuard : IDisposable
    {
        private readonly WindowsClipboardPaste.ClipboardSnapshot _snapshot;

        private ClipboardGuard(WindowsClipboardPaste.ClipboardSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public static ClipboardGuard Capture()
        {
            var snapshot = OnSta(WindowsClipboardPaste.TrySnapshotClipboard);
            Assert.True(snapshot is not null, "the desk's clipboard could not be copied whole, so it was not touched");
            return new ClipboardGuard(snapshot!);
        }

        public static string? GetText() => OnSta(static () => System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null);

        public static void SetText(string text) => OnSta(() =>
        {
            System.Windows.Forms.Clipboard.SetText(text);
            return true;
        });

        /// <summary>Whether the clipboard can be reached and copied whole from this session; a test that needs it is skipped otherwise.</summary>
        public static bool Available()
        {
            try
            {
                return OnSta(WindowsClipboardPaste.TrySnapshotClipboard) is not null;
            }
            catch (Exception exception) when (exception is ExternalException or InvalidOperationException or ThreadStateException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (!OnSta(() => WindowsClipboardPaste.TryRestoreClipboard(_snapshot)))
            {
                throw new InvalidOperationException("The desk's clipboard could not be put back after the test.");
            }
        }

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
