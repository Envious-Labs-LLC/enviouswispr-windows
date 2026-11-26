using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace EnviousWispr.Services.Input;

public sealed class WindowsTextTargetAdapter : ITextTargetAdapter, IDisposable
{
    private static readonly TimeSpan TargetActivationTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ArrivalObservationTimeout = TimeSpan.FromMilliseconds(250);
    private const int ArrivalPollIntervalMs = 35;
    private readonly SemaphoreSlim _automationGate = new(1, 1);
    private readonly PasteRevealPatience _revealPatience = new();
    private int _arrivalProbeInFlight;

    public void Dispose() => _automationGate.Dispose();

    public Task<TextCommitResult> CopyOnlyAsync(
        ProcessedText text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WindowsClipboardPaste.CopyRequestedAsync(text.Text, cancellationToken);
    }

    public async Task<TargetContextResult> CaptureContextAsync(
        TargetWindowId target,
        TextDeliveryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        try
        {
            await _automationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(
                    () => CaptureContext(target, options),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _automationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedAutomationFailure(exception))
        {
            return new TargetContextResult(
                TargetContextStatus.AccessibilityUnavailable,
                RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable);
        }
    }

    /// <summary>
    /// Asks the focused app for its selection with a synthetic Copy, and puts the clipboard back.
    /// </summary>
    /// <remarks>
    /// For apps that publish no selection. Returns null when nothing could be read, for any reason:
    /// every reason has the same remedy, so a caller branching on them would be inventing
    /// distinctions it cannot act on.
    /// </remarks>
    public static Task<string?> TryReadSelectionWithCopyAsync(CancellationToken cancellationToken) =>
        WindowsClipboardPaste.TryReadSelectionAsync(cancellationToken);

    public async Task<TextCommitResult> CommitAsync(
        TextCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Options);
        ValidateOptions(request.Options);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.ForcedRefusalReason != TextDeliveryRefusalReason.None)
        {
            return await WindowsClipboardPaste.CopyOnlyAsync(
                request.FallbackText.Text,
                request.ForcedRefusalReason,
                cancellationToken).ConfigureAwait(false);
        }

        if (request.ExpectedContext is null)
        {
            return await WindowsClipboardPaste.CopyOnlyAsync(
                request.FallbackText.Text,
                TextDeliveryRefusalReason.AccessibilityUnavailable,
                cancellationToken).ConfigureAwait(false);
        }

        var current = await CaptureContextAsync(
            request.Target,
            request.Options,
            cancellationToken).ConfigureAwait(false);
        if (current.Status != TargetContextStatus.Available ||
            current.Context is null ||
            !CaretUnchanged(request.ExpectedContext, current.Context))
        {
            return await WindowsClipboardPaste.CopyOnlyAsync(
                request.FallbackText.Text,
                RevalidationRefusal(current),
                cancellationToken).ConfigureAwait(false);
        }

        var commitRule = ResolveTargetPolicy(
            request.Target,
            elementClassName: null,
            isControlEdit: current.Context.TargetKind == TextTargetKind.StandardEdit);

        if (CanUseDirectValueWrite(current.Context, request, commitRule))
        {
            var direct = TryDirectValueWrite(request, current.Context);
            if (direct is not null)
            {
                if (direct.Delivered)
                {
                    return direct;
                }

                if (direct.RefusalReason == TextDeliveryRefusalReason.DirectWriteUnverified)
                {
                    return direct;
                }
            }
        }

        var policyRefusal = CompatibilityRefusal(request.TargetKind, request.Text.Text);
        if (policyRefusal != TextDeliveryRefusalReason.None)
        {
            return await WindowsClipboardPaste.CopyOnlyAsync(
                request.FallbackText.Text,
                policyRefusal,
                cancellationToken).ConfigureAwait(false);
        }

        var keystroke = ResolveCommitKeystroke(current.Context, commitRule);
        var processName = !string.IsNullOrWhiteSpace(request.Target.ProcessName)
            ? request.Target.ProcessName
            : WindowsWindowIdentity.TryGetProcessName(request.Target.ProcessId);
        var settleFloor = TimeSpan.FromMilliseconds(commitRule.MinimumPasteSettleMilliseconds);
        var settleDelay = _revealPatience.GetSettleDelay(processName, settleFloor);
        var dispatchToken = _revealPatience.RecordDispatch(processName);
        Stopwatch? dispatchedWatch = null;

        var pasted = await WindowsClipboardPaste.PasteAsync(
            request.Text.Text,
            request.FallbackText.Text,
            request.Options.RestoreClipboardAfterPaste,
            keystroke,
            () => GuardedPreflight(() => PreflightInput(
                request.Target,
                request.ExpectedContext,
                request.Options)),
            cancellationToken,
            settleDelay,
            onPasteDispatched: () => dispatchedWatch = Stopwatch.StartNew()).ConfigureAwait(false);

        if (!pasted.Delivered || pasted.Route != TextDeliveryRoute.ClipboardPaste)
        {
            return pasted;
        }

        // Post-paste arrival verification runs strictly best-effort after the paste keystroke and
        // clipboard restoration already completed: cancellation or observer failure must never turn
        // an already-completed paste into Cancelled/DeliveryFaulted and trigger duplicate retries.
        PasteArrivalStatus arrivalStatus;
        int arrivalElapsedMs;
        try
        {
            (arrivalStatus, arrivalElapsedMs) = await ObserveArrivalAsync(
                request,
                dispatchedWatch,
                dispatchToken,
                settleFloor,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            arrivalStatus = PasteArrivalStatus.Unreadable;
            arrivalElapsedMs = dispatchedWatch is { } w
                ? (int)Math.Min(int.MaxValue, w.ElapsedMilliseconds)
                : 0;
        }

        return pasted with
        {
            ArrivalStatus = arrivalStatus,
            ArrivalElapsedMilliseconds = arrivalElapsedMs,
        };
    }

    private async Task<(PasteArrivalStatus Status, int ElapsedMilliseconds)> ObserveArrivalAsync(
        TextCommitRequest request,
        Stopwatch? dispatchedWatch,
        PasteRevealPatience.DispatchToken? dispatchToken,
        TimeSpan settleFloor,
        CancellationToken cancellationToken)
    {
        var contextWindow = Math.Max(request.Options.ContextWindowCharacters, request.Text.Text.Length + 64);
        var previousLeft = request.ExpectedContext?.HasTextContext == true ? request.ExpectedContext.Left : null;
        var previousValue = request.ExpectedContext?.BaselineValue;
        var deadline = Environment.TickCount64 + (long)ArrivalObservationTimeout.TotalMilliseconds;
        var attempt = 0;

        while (true)
        {
            var remainingMs = (int)Math.Max(0, deadline - Environment.TickCount64);
            if (remainingMs == 0 && attempt > 0)
            {
                var timeoutElapsed = dispatchedWatch is { } tw ? (int)Math.Min(int.MaxValue, tw.ElapsedMilliseconds) : 0;
                return (PasteArrivalStatus.BudgetExhausted, timeoutElapsed);
            }

            if (Interlocked.CompareExchange(ref _arrivalProbeInFlight, 1, 0) != 0)
            {
                var busyElapsed = dispatchedWatch is { } bw ? (int)Math.Min(int.MaxValue, bw.ElapsedMilliseconds) : 0;
                return (PasteArrivalStatus.BudgetExhausted, busyElapsed);
            }

            PasteArrivalStatus status;
            var probeTask = Task.Run(() =>
            {
                try
                {
                    return ObserveArrivalOnce(request, contextWindow, previousLeft, previousValue);
                }
                catch (Exception exception) when (IsExpectedAutomationFailure(exception))
                {
                    return PasteArrivalStatus.Unreadable;
                }
                finally
                {
                    Interlocked.Exchange(ref _arrivalProbeInFlight, 0);
                }
            }, CancellationToken.None);

            var waitMs = Math.Max(1, remainingMs);
            var finished = await Task.WhenAny(
                probeTask,
                Task.Delay(waitMs, cancellationToken)).ConfigureAwait(false);
            if (!ReferenceEquals(finished, probeTask))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expiredElapsed = dispatchedWatch is { } ew ? (int)Math.Min(int.MaxValue, ew.ElapsedMilliseconds) : 0;
                return (PasteArrivalStatus.BudgetExhausted, expiredElapsed);
            }

            status = await probeTask.ConfigureAwait(false);
            var sinceDispatchMs = dispatchedWatch is { } sw ? (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds) : 0;

            if (status == PasteArrivalStatus.Confirmed)
            {
                // First-check confirmation landed inside the already-waited settle delay: report 0 ms
                // so fast confirmations never multiply the prior wait upward into the 1.2 s ceiling.
                var revealSampleMs = attempt == 0 ? 0 : sinceDispatchMs;
                _ = _revealPatience.RecordSighting(dispatchToken, revealSampleMs, settleFloor);
                return (PasteArrivalStatus.Confirmed, sinceDispatchMs);
            }

            if (status != PasteArrivalStatus.NotObserved || Environment.TickCount64 >= deadline)
            {
                return (status, sinceDispatchMs);
            }

            attempt++;
            await Task.Delay(ArrivalPollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record ObservedArrivalText(
        uint FocusProcessId,
        bool IsPassword,
        string? LeftBeforeCaret,
        string? FullValue);

    private static PasteArrivalStatus ObserveArrivalOnce(
        TextCommitRequest request,
        int contextCharacters,
        string? previousLeft,
        string? previousValue)
    {
        if (GetForegroundWindow() != request.Target.Value)
        {
            return PasteArrivalStatus.Unreadable;
        }

        var observed = Automation(() =>
        {
            if (AutomationElement.FocusedElement is not { } element)
            {
                return null;
            }

            var pid = checked((uint)element.Current.ProcessId);
            if (element.Current.IsPassword)
            {
                return new ObservedArrivalText(pid, IsPassword: true, null, null);
            }

            string? leftBeforeCaret = null;
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObj) &&
                textPatternObj is TextPattern textPattern)
            {
                var selections = textPattern.GetSelection();
                if (selections.Length == 1)
                {
                    var leftRange = textPattern.DocumentRange.Clone();
                    leftRange.MoveEndpointByRange(
                        TextPatternRangeEndpoint.End,
                        selections[0],
                        TextPatternRangeEndpoint.Start);
                    leftRange.MoveEndpointByUnit(
                        TextPatternRangeEndpoint.Start,
                        TextUnit.Character,
                        -contextCharacters);
                    leftBeforeCaret = leftRange.GetText(contextCharacters);
                }
            }

            string? fullValue = null;
            if (leftBeforeCaret is null &&
                element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObj) &&
                valuePatternObj is ValuePattern valuePattern)
            {
                var raw = valuePattern.Current.Value;
                if (raw is not null)
                {
                    var maxSlice = contextCharacters * 2;
                    fullValue = raw.Length > maxSlice ? raw[^maxSlice..] : raw;
                }
            }

            return new ObservedArrivalText(
                pid,
                IsPassword: false,
                leftBeforeCaret,
                fullValue);
        });

        if (observed is null ||
            observed.IsPassword ||
            !WindowsWindowIdentity.IsMatchingFocusProcess(
                request.Target.Value,
                request.Target.ProcessId,
                observed.FocusProcessId))
        {
            return PasteArrivalStatus.Unreadable;
        }

        return EvaluateArrival(
            request.Text.Text,
            observed.LeftBeforeCaret,
            observed.FullValue,
            previousLeft,
            previousValue);
    }

    internal static PasteArrivalStatus EvaluateArrival(
        string insertedText,
        string? observedLeft,
        string? observedValue,
        string? previousLeft,
        string? previousValue = null)
    {
        var normalizedExpected = NormalizeArrivalText(insertedText).Trim();
        if (string.IsNullOrEmpty(normalizedExpected))
        {
            return PasteArrivalStatus.Unreadable;
        }

        if (observedLeft is null && observedValue is null)
        {
            return PasteArrivalStatus.Unreadable;
        }

        if (observedLeft is not null)
        {
            if (previousLeft is null && previousValue is null)
            {
                return PasteArrivalStatus.Unreadable;
            }

            var normalizedLeft = NormalizeArrivalText(observedLeft).TrimEnd();
            if (previousLeft is not null)
            {
                var normalizedPreviousLeft = NormalizeArrivalText(previousLeft).TrimEnd();
                var previousAlreadyEndedWithExpected = !string.IsNullOrEmpty(normalizedPreviousLeft) &&
                    normalizedPreviousLeft.EndsWith(normalizedExpected, StringComparison.Ordinal);
                if (normalizedLeft.EndsWith(normalizedExpected, StringComparison.Ordinal) &&
                    (!previousAlreadyEndedWithExpected ||
                     normalizedLeft.Length >= normalizedPreviousLeft.Length + normalizedExpected.Length))
                {
                    return PasteArrivalStatus.Confirmed;
                }
            }
            else if (previousValue is not null)
            {
                var normalizedPrevValue = NormalizeArrivalText(previousValue).TrimEnd();
                if (normalizedLeft.EndsWith(normalizedExpected, StringComparison.Ordinal) &&
                    CountOccurrences(normalizedLeft, normalizedExpected) > CountOccurrences(normalizedPrevValue, normalizedExpected))
                {
                    return PasteArrivalStatus.Confirmed;
                }
            }

            return PasteArrivalStatus.NotObserved;
        }

        // ValuePattern-only controls have no TextPattern caret, so compare against a pre-paste value
        // baseline rather than confirming stale text that was already in the box before the paste.
        if (previousValue is null)
        {
            return PasteArrivalStatus.Unreadable;
        }

        var normalizedValue = NormalizeArrivalText(observedValue!).TrimEnd();
        var normalizedPreviousValue = NormalizeArrivalText(previousValue).TrimEnd();
        return CountOccurrences(normalizedValue, normalizedExpected) > CountOccurrences(normalizedPreviousValue, normalizedExpected)
            ? PasteArrivalStatus.Confirmed
            : PasteArrivalStatus.NotObserved;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle) || string.IsNullOrEmpty(haystack))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string NormalizeArrivalText(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    internal static PasteKeystrokeKind ResolveCommitKeystroke(
        CaretContext context,
        WindowsPasteRule commitRule)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commitRule);
        return context.TargetKind == TextTargetKind.Terminal
            ? PasteKeystrokeKind.ShiftInsert
            : commitRule.Keystroke;
    }

    /// <summary>What the commit refuses with when the target, read again just before the write, is not the one the context was captured from - or cannot be read.</summary>
    /// <remarks>
    /// THE SECOND READ KEEPS ITS OWN NAME (plan-2 step 13, round three). An elevated or protected
    /// target says so; accessibility that did not answer the second time says that, with the reason
    /// the capture gave; a target that is gone, or whose caret moved, is a changed target.
    /// </remarks>
    internal static TextDeliveryRefusalReason RevalidationRefusal(TargetContextResult current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current.Status switch
        {
            TargetContextStatus.Elevated => TextDeliveryRefusalReason.ElevatedTarget,
            TargetContextStatus.Protected => TextDeliveryRefusalReason.ProtectedField,
            TargetContextStatus.AccessibilityUnavailable => current.RefusalReason != TextDeliveryRefusalReason.None
                ? current.RefusalReason
                : TextDeliveryRefusalReason.AccessibilityUnavailable,
            _ => TextDeliveryRefusalReason.TargetChanged,
        };
    }

    /// <summary>The preflight the paste runs on its own thread, with what accessibility refused translated here, inside the adapter; anything else it throws is a defect and comes out.</summary>
    internal static TextDeliveryRefusalReason GuardedPreflight(Func<TextDeliveryRefusalReason> preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        try
        {
            return preflight();
        }
        catch (AutomationRefusalException)
        {
            return TextDeliveryRefusalReason.AccessibilityUnavailable;
        }
    }

    internal static bool CaretUnchanged(CaretContext expected, CaretContext actual) =>
        expected.Target == actual.Target &&
        string.Equals(expected.FocusedElementId, actual.FocusedElementId, StringComparison.Ordinal) &&
        expected.TargetKind == actual.TargetKind &&
        expected.LeftReachedDocumentStart == actual.LeftReachedDocumentStart &&
        expected.RightReachedDocumentEnd == actual.RightReachedDocumentEnd &&
        expected.HasTextContext == actual.HasTextContext &&
        expected.IsScreenDerived == actual.IsScreenDerived &&
        expected.IsUrlBarField == actual.IsUrlBarField &&
        string.Equals(expected.Left, actual.Left, StringComparison.Ordinal) &&
        string.Equals(expected.Selection, actual.Selection, StringComparison.Ordinal) &&
        string.Equals(expected.Right, actual.Right, StringComparison.Ordinal);

    internal static TextDeliveryRefusalReason CompatibilityRefusal(
        TextTargetKind targetKind,
        string text) => targetKind switch
    {
        TextTargetKind.Terminal when text.IndexOfAny(['\r', '\n']) >= 0 =>
            TextDeliveryRefusalReason.UnsafeMultilineTarget,
        TextTargetKind.Game => TextDeliveryRefusalReason.UnsupportedTarget,
        _ => TextDeliveryRefusalReason.None,
    };

    internal static bool CanUseDirectValueWrite(
        CaretContext context,
        TextCommitRequest request,
        WindowsPasteRule rule) =>
        !rule.SkipDirectValueWrite &&
        context.TargetKind == TextTargetKind.StandardEdit &&
        context.SupportsDirectValueWrite &&
        context.DirectValueWriteAtEnd &&
        context.Selection.Length == 0 &&
        request.Text.Text.Length <= request.Options.MaximumDirectValueCharacters;

    private static TextCommitResult? TryDirectValueWrite(
        TextCommitRequest request,
        CaretContext expected)
    {
        var invoked = false;
        try
        {
            var preflight = PreflightInput(request.Target, expected, request.Options);
            if (preflight != TextDeliveryRefusalReason.None)
            {
                return null;
            }

            var valuePattern = Automation(static () =>
                AutomationElement.FocusedElement is { } element &&
                element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObject) &&
                patternObject is ValuePattern pattern &&
                !pattern.Current.IsReadOnly
                    ? pattern
                    : null);
            if (valuePattern is null)
            {
                return null;
            }

            var existing = Automation(() => valuePattern.Current.Value) ?? string.Empty;
            if (existing.Length > request.Options.MaximumDirectValueCharacters ||
                !existing.EndsWith(expected.Left, StringComparison.Ordinal))
            {
                return null;
            }

            var replacement = existing + request.Text.Text;
            if (replacement.Length > request.Options.MaximumDirectValueCharacters)
            {
                return null;
            }

            invoked = true;
            var readBack = Automation(() =>
            {
                valuePattern.SetValue(replacement);
                return valuePattern.Current.Value;
            });
            var verified = string.Equals(readBack, replacement, StringComparison.Ordinal);
            return new TextCommitResult(
                TextDeliveryRoute.UiAutomationValue,
                Delivered: verified,
                ClipboardFallback: false,
                ClipboardRestored: false,
                verified
                    ? TextDeliveryRefusalReason.None
                    : TextDeliveryRefusalReason.DirectWriteUnverified);
        }
        catch (Exception exception) when (IsExpectedAutomationFailure(exception))
        {
            return invoked
                ? new TextCommitResult(
                    TextDeliveryRoute.UiAutomationValue,
                    Delivered: false,
                    ClipboardFallback: false,
                    ClipboardRestored: false,
                    TextDeliveryRefusalReason.DirectWriteUnverified)
                : null;
        }
    }

    private static TargetContextResult CaptureContext(
        TargetWindowId target,
        TextDeliveryOptions options)
    {
        var budget = new UiaPreparationBudget(TargetActivationTimeout);
        var windowStatus = ActivateAndValidateWindow(target, budget, out var wasReactivated);
        if (windowStatus != TargetContextStatus.Available)
        {
            return new TargetContextResult(
                windowStatus,
                RefusalReason: RefusalFor(windowStatus));
        }

        if (wasReactivated)
        {
            while (!budget.IsExhausted && !IsFocusSettledForProcess(target.Value, target.ProcessId))
            {
                if (budget.IsExhausted)
                {
                    break;
                }

                Thread.Sleep(25);
                if (GetForegroundWindow() != target.Value)
                {
                    return new TargetContextResult(
                        TargetContextStatus.TargetChanged,
                        RefusalReason: TextDeliveryRefusalReason.TargetChanged);
                }
            }
        }

        var win32ChildFocusId = WindowsWindowIdentity.TryCaptureDistinctWin32ChildFocusId(target.Value, target.ProcessId);
        if (!budget.TryAdmit())
        {
            return new TargetContextResult(
                TargetContextStatus.AccessibilityUnavailable,
                RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable);
        }

        var element = Automation(static () => AutomationElement.FocusedElement);
        var focus = ReadFocusedElementFacts(element);
        if (element is null || focus is null)
        {
            return new TargetContextResult(
                TargetContextStatus.AccessibilityUnavailable,
                RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable);
        }

        var focusMatchesProcess = WindowsWindowIdentity.IsMatchingFocusProcess(target.Value, target.ProcessId, focus.ProcessId);
        var evaluation = EvaluateFocusedElement(
            target,
            focus,
            win32ChildFocusId,
            options.IsExplicitHistoryPaste || options.TargetMode == Core.Settings.PasteTargetMode.WhereCursorIsAtRelease,
            (className, isControlEdit) => ResolveTargetPolicy(target, className, isControlEdit),
            allowSameWindowEditableReplacement: options.TargetMode == Core.Settings.PasteTargetMode.WhereDictationStarted,
            focusProcessMatchesTarget: focusMatchesProcess);
        if (evaluation.Failure is not null)
        {
            return evaluation.Failure;
        }

        var focusedId = evaluation.EffectiveFocusId;
        var rule = evaluation.Rule!;
        var targetKind = rule.TargetKind;
        if (evaluation.WindowFallbackOnly || !focus.IsEnabled || !focus.IsKeyboardFocusable || !budget.TryAdmit())
        {
            return new TargetContextResult(
                TargetContextStatus.Available,
                EmptyContext(target, focusedId, targetKind));
        }

        var textPattern = Automation(() =>
            element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) && patternObject is TextPattern pattern
                ? pattern
                : null);
        if (textPattern is null)
        {
            string? baselineValue = null;
            if (budget.TryAdmit())
            {
                var maxBaseline = options.ContextWindowCharacters * 2;
                baselineValue = Automation(() =>
                    element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObj) &&
                    valuePatternObj is ValuePattern valuePattern
                        ? valuePattern.Current.Value is { } raw
                            ? (raw.Length > maxBaseline ? raw[^maxBaseline..] : raw)
                            : string.Empty
                        : null);
            }

            return new TargetContextResult(
                TargetContextStatus.Available,
                EmptyContext(target, focusedId, targetKind, baselineValue));
        }

        var caret = Automation(() => ReadCaret(textPattern, options));
        if (caret is null || (caret.NoSelection && targetKind != TextTargetKind.Terminal))
        {
            return new TargetContextResult(
                TargetContextStatus.AccessibilityUnavailable,
                EmptyContext(target, focusedId, targetKind),
                TextDeliveryRefusalReason.UnsupportedTarget);
        }

        if (caret.NoSelection)
        {
            return new TargetContextResult(
                TargetContextStatus.Available,
                EmptyContext(target, focusedId, targetKind));
        }

        var supportsValue = !rule.SkipDirectValueWrite &&
            targetKind == TextTargetKind.StandardEdit &&
            Automation(() =>
                element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject) &&
                valuePatternObject is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly);
        var urlBar = IsUrlBarField(element, targetKind);
        return new TargetContextResult(
            TargetContextStatus.Available,
            new CaretContext(
                target,
                focusedId,
                targetKind,
                caret.Left,
                caret.Selected,
                caret.Right,
                caret.LeftAtStart,
                caret.RightAtEnd,
                HasTextContext: true,
                SupportsDirectValueWrite: supportsValue,
                DirectValueWriteAtEnd: caret.RightAtEnd && caret.Right.Length == 0,
                IsScreenDerived: targetKind == TextTargetKind.Terminal,
                IsUrlBarField: urlBar));
    }

    private static bool IsFocusSettledForProcess(nint windowHandle, uint targetProcessId)
    {
        var focusedPid = Automation(static () =>
            AutomationElement.FocusedElement is { } focused && focused.Current.HasKeyboardFocus
                ? checked((uint)focused.Current.ProcessId)
                : 0u);
        return focusedPid != 0 &&
            WindowsWindowIdentity.IsMatchingFocusProcess(windowHandle, targetProcessId, focusedPid);
    }

    private static FocusedElementFacts? ReadFocusedElementFacts(AutomationElement? element) =>
        element is null
            ? null
            : Automation(() => new FocusedElementFacts(
                checked((uint)element.Current.ProcessId),
                RuntimeId(element),
                element.Current.HasKeyboardFocus,
                element.Current.IsPassword,
                element.Current.IsEnabled,
                element.Current.IsKeyboardFocusable,
                element.Current.ControlType == ControlType.Edit,
                element.Current.ClassName ?? string.Empty,
                element.Current.ControlType == ControlType.Document));

    private static bool IsEditableReplacementCandidate(FocusedElementFacts focus) =>
        !focus.IsPassword &&
        focus.IsEnabled &&
        focus.IsKeyboardFocusable &&
        (focus.IsControlEdit || focus.IsControlDocument);

    internal static FocusEvaluationResult EvaluateFocusedElement(
        TargetWindowId target,
        FocusedElementFacts? focus,
        string? win32ChildFocusId,
        bool isExplicitHistoryPaste,
        Func<string?, bool, WindowsPasteRule> resolvePolicy,
        bool allowSameWindowEditableReplacement = true,
        bool? focusProcessMatchesTarget = null)
    {
        ArgumentNullException.ThrowIfNull(resolvePolicy);
        if (focus is null)
        {
            return new FocusEvaluationResult(
                new TargetContextResult(
                    TargetContextStatus.AccessibilityUnavailable,
                    RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable),
                WindowFallbackOnly: false,
                EffectiveFocusId: string.Empty,
                Rule: null);
        }

        var processMatches = focusProcessMatchesTarget ?? (focus.ProcessId == target.ProcessId);

        if (target.FocusedElementId is null)
        {
            // Live dictation and Undo must never guess a target when neither a UIA RuntimeId nor a
            // distinct Win32 child control HWND was captured when dictation started.
            if (!isExplicitHistoryPaste)
            {
                return new FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.AccessibilityUnavailable,
                        RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            // Explicit History Paste has no prior dictation caret to drift from, but still requires
            // live UIA confirmation that focus is inside target.ProcessId and is not a password field.
            if (!processMatches)
            {
                return new FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.AccessibilityUnavailable,
                        RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            if (focus.IsPassword)
            {
                return new FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.Protected,
                        EmptyContext(target, string.Empty, TextTargetKind.Unknown),
                        TextDeliveryRefusalReason.ProtectedField),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            if (!focus.HasKeyboardFocus)
            {
                return new FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.TargetChanged,
                        RefusalReason: TextDeliveryRefusalReason.TargetChanged),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            var historyRule = resolvePolicy(focus.ClassName, focus.IsControlEdit);
            if (!historyRule.AllowsWindowFallbackWithoutFocusedElement)
            {
                return new FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.AccessibilityUnavailable,
                        EmptyContext(target, string.Empty, historyRule.TargetKind),
                        TextDeliveryRefusalReason.UnsupportedTarget),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            var liveHistoryId = !string.IsNullOrEmpty(focus.RuntimeId)
                ? focus.RuntimeId
                : (win32ChildFocusId ?? string.Empty);
            return new FocusEvaluationResult(
                Failure: null,
                WindowFallbackOnly: true,
                EffectiveFocusId: liveHistoryId,
                Rule: historyRule);
        }

        if (target.FocusedElementId.StartsWith(WindowsWindowIdentity.Win32FocusPrefix, StringComparison.Ordinal))
        {
            if (!focus.HasKeyboardFocus)
            {
                return new FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.TargetChanged,
                        RefusalReason: TextDeliveryRefusalReason.TargetChanged),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            if (!processMatches)
            {
                return new FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.AccessibilityUnavailable,
                        RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            if (focus.IsPassword)
            {
                var protectedRule = resolvePolicy(focus.ClassName, focus.IsControlEdit);
                var protectedFocusId = !string.IsNullOrEmpty(focus.RuntimeId)
                    ? WindowsWindowIdentity.CombineFocusIdentity(win32ChildFocusId, focus.RuntimeId)
                    : (win32ChildFocusId ?? target.FocusedElementId);
                return new FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.Protected,
                        EmptyContext(target, protectedFocusId, protectedRule.TargetKind),
                        TextDeliveryRefusalReason.ProtectedField),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: protectedFocusId,
                    Rule: protectedRule);
            }

            var exactWin32Match = string.Equals(win32ChildFocusId, target.FocusedElementId, StringComparison.Ordinal);
            var resolvedWin32Id = !string.IsNullOrEmpty(focus.RuntimeId)
                ? WindowsWindowIdentity.CombineFocusIdentity(win32ChildFocusId, focus.RuntimeId)
                : win32ChildFocusId;
            if (!exactWin32Match &&
                (!allowSameWindowEditableReplacement ||
                 !IsEditableReplacementCandidate(focus) ||
                 string.IsNullOrEmpty(resolvedWin32Id)))
            {
                return new FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.TargetChanged,
                        RefusalReason: TextDeliveryRefusalReason.TargetChanged),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            var effectiveWin32Id = resolvedWin32Id ?? target.FocusedElementId;
            var win32Rule = resolvePolicy(focus.ClassName, focus.IsControlEdit);
            return new FocusEvaluationResult(
                Failure: null,
                WindowFallbackOnly: false,
                EffectiveFocusId: effectiveWin32Id,
                Rule: win32Rule);
        }

        if (!processMatches || !focus.HasKeyboardFocus)
        {
            return new FocusEvaluationResult(
                new TargetContextResult(
                    TargetContextStatus.TargetChanged,
                    RefusalReason: TextDeliveryRefusalReason.TargetChanged),
                WindowFallbackOnly: false,
                EffectiveFocusId: string.Empty,
                Rule: null);
        }

        var rule = resolvePolicy(focus.ClassName, focus.IsControlEdit);
        if (focus.IsPassword)
        {
            return new FocusEvaluationResult(
                new TargetContextResult(
                    TargetContextStatus.Protected,
                    EmptyContext(target, focus.RuntimeId, rule.TargetKind),
                    TextDeliveryRefusalReason.ProtectedField),
                WindowFallbackOnly: false,
                EffectiveFocusId: focus.RuntimeId,
                Rule: rule);
        }

        if (!string.Equals(focus.RuntimeId, target.FocusedElementId, StringComparison.Ordinal) &&
            (!allowSameWindowEditableReplacement ||
             !IsEditableReplacementCandidate(focus) ||
             string.IsNullOrEmpty(focus.RuntimeId)))
        {
            return new FocusEvaluationResult(
                new TargetContextResult(
                    TargetContextStatus.TargetChanged,
                    RefusalReason: TextDeliveryRefusalReason.TargetChanged),
                WindowFallbackOnly: false,
                EffectiveFocusId: string.Empty,
                Rule: null);
        }

        return new FocusEvaluationResult(
            Failure: null,
            WindowFallbackOnly: false,
            EffectiveFocusId: focus.RuntimeId,
            Rule: rule);
    }

    internal sealed record FocusEvaluationResult(
        TargetContextResult? Failure,
        bool WindowFallbackOnly,
        string EffectiveFocusId,
        WindowsPasteRule? Rule);

    /// <summary>What the focused element says about itself, read in one automation call.</summary>
    internal sealed record FocusedElementFacts(
        uint ProcessId,
        string RuntimeId,
        bool HasKeyboardFocus,
        bool IsPassword,
        bool IsEnabled,
        bool IsKeyboardFocusable,
        bool IsControlEdit,
        string ClassName,
        bool IsControlDocument = false);

    /// <summary>The text around the caret, as the text pattern reports it.</summary>
    private sealed record CaretText(
        string Selected,
        string Left,
        bool LeftAtStart,
        string Right,
        bool RightAtEnd,
        bool NoSelection = false);

    /// <summary>Reads the selection and the bounded text on either side of it; null when the control's selection is not one range within the window.</summary>
    /// <remarks>UI AUTOMATION CALLS ONLY, so the caller can put the whole read behind one boundary.</remarks>
    private static CaretText? ReadCaret(TextPattern textPattern, TextDeliveryOptions options)
    {
        var selections = textPattern.GetSelection();
        if (selections.Length == 0)
        {
            return new CaretText(string.Empty, string.Empty, false, string.Empty, false, NoSelection: true);
        }

        if (selections.Length != 1)
        {
            return null;
        }

        var selection = selections[0];
        var selected = selection.GetText(options.ContextWindowCharacters + 1);
        if (selected.Length > options.ContextWindowCharacters)
        {
            return null;
        }

        var document = textPattern.DocumentRange;
        var leftRange = document.Clone();
        leftRange.MoveEndpointByRange(
            TextPatternRangeEndpoint.End,
            selection,
            TextPatternRangeEndpoint.Start);
        leftRange.MoveEndpointByUnit(
            TextPatternRangeEndpoint.Start,
            TextUnit.Character,
            -options.ContextWindowCharacters);
        var left = leftRange.GetText(options.ContextWindowCharacters);
        var leftAtStart = leftRange.CompareEndpoints(
            TextPatternRangeEndpoint.Start,
            document,
            TextPatternRangeEndpoint.Start) == 0;

        var rightRange = document.Clone();
        rightRange.MoveEndpointByRange(
            TextPatternRangeEndpoint.Start,
            selection,
            TextPatternRangeEndpoint.End);
        rightRange.MoveEndpointByUnit(
            TextPatternRangeEndpoint.End,
            TextUnit.Character,
            options.ContextWindowCharacters);
        var right = rightRange.GetText(options.ContextWindowCharacters);
        var rightAtEnd = rightRange.CompareEndpoints(
            TextPatternRangeEndpoint.End,
            document,
            TextPatternRangeEndpoint.End) == 0;
        return new CaretText(selected, left, leftAtStart, right, rightAtEnd);
    }

    private static CaretContext EmptyContext(
        TargetWindowId target,
        string focusedId,
        TextTargetKind targetKind,
        string? baselineValue = null) => new(
        target,
        focusedId,
        targetKind,
        Left: string.Empty,
        Selection: string.Empty,
        Right: string.Empty,
        LeftReachedDocumentStart: false,
        RightReachedDocumentEnd: false,
        HasTextContext: false,
        SupportsDirectValueWrite: false,
        DirectValueWriteAtEnd: false,
        IsScreenDerived: targetKind == TextTargetKind.Terminal,
        BaselineValue: baselineValue);

    internal static WindowsPasteRule ResolveTargetPolicy(
        TargetWindowId target,
        string? elementClassName,
        bool isControlEdit)
    {
        var processName = !string.IsNullOrWhiteSpace(target.ProcessName)
            ? target.ProcessName
            : WindowsWindowIdentity.TryGetProcessName(target.ProcessId);
        var windowClassName = WindowsWindowIdentity.TryGetClassName(target.Value);
        var fullscreen = IsFullscreen(target.Value);
        return WindowsPastePolicy.Resolve(
            processName,
            windowClassName,
            elementClassName,
            isControlEdit,
            fullscreen);
    }

    private static bool IsUrlBarField(
        AutomationElement element,
        TextTargetKind targetKind)
    {
        if (targetKind != TextTargetKind.Browser)
        {
            return false;
        }

        var (automationId, className) = Automation(() => (
            element.Current.AutomationId ?? string.Empty,
            element.Current.ClassName ?? string.Empty));
        return automationId.Contains("address", StringComparison.OrdinalIgnoreCase) ||
            automationId.Contains("omnibox", StringComparison.OrdinalIgnoreCase) ||
            automationId.Contains("urlbar", StringComparison.OrdinalIgnoreCase) ||
            className.Contains("omnibox", StringComparison.OrdinalIgnoreCase) ||
            className.Contains("urlbar", StringComparison.OrdinalIgnoreCase);
    }

    private static TargetContextStatus ActivateAndValidateWindow(
        TargetWindowId target,
        UiaPreparationBudget budget,
        out bool wasReactivated)
    {
        wasReactivated = false;
        if (!target.IsValid || !IsWindow(target.Value))
        {
            return TargetContextStatus.TargetUnavailable;
        }

        _ = GetWindowThreadProcessId(target.Value, out var currentProcessId);
        if (target.ProcessId == 0 || currentProcessId != target.ProcessId)
        {
            return TargetContextStatus.TargetChanged;
        }

        var integrity = WindowsWindowIdentity.TryIsHigherIntegrityProcess(target.ProcessId);
        if (integrity is null or true)
        {
            return TargetContextStatus.Elevated;
        }

        if (GetForegroundWindow() != target.Value)
        {
            wasReactivated = true;
            _ = WindowsWindowIdentity.TryRestoreAndFocus(target.Value);
            while (GetForegroundWindow() != target.Value && !budget.IsExhausted)
            {
                Thread.Sleep(25);
            }
        }

        return GetForegroundWindow() == target.Value
            ? TargetContextStatus.Available
            : TargetContextStatus.TargetChanged;
    }

    private static TextDeliveryRefusalReason PreflightInput(
        TargetWindowId target,
        CaretContext expected,
        TextDeliveryOptions options)
    {
        if (GetForegroundWindow() != target.Value)
        {
            return TextDeliveryRefusalReason.TargetChanged;
        }

        _ = GetWindowThreadProcessId(target.Value, out var processId);
        if (processId != target.ProcessId)
        {
            return TextDeliveryRefusalReason.TargetChanged;
        }

        if (AnyInputKeyHeld())
        {
            return TextDeliveryRefusalReason.InputStateUnsafe;
        }

        return PreflightRefusal(CaptureContext(target, options), expected);
    }

    /// <summary>What the paste's last look at the target, a moment before the keystroke, refuses with.</summary>
    /// <remarks>
    /// THE CAPTURE'S OWN REFUSAL COMES FIRST (plan-2 step 13, round four). A selection that became
    /// unsupported between the commit's read and this one is UnsupportedTarget, which the capture
    /// says; mapping its status alone read it as "accessibility unavailable". The status is the
    /// answer only when the capture gave no reason of its own.
    /// </remarks>
    internal static TextDeliveryRefusalReason PreflightRefusal(TargetContextResult current, CaretContext expected)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(expected);
        if (current.Status != TargetContextStatus.Available || current.Context is null)
        {
            return current.RefusalReason != TextDeliveryRefusalReason.None
                ? current.RefusalReason
                : RefusalFor(current.Status);
        }

        return CaretUnchanged(expected, current.Context)
            ? TextDeliveryRefusalReason.None
            : TextDeliveryRefusalReason.TargetChanged;
    }

    private static void ValidateOptions(TextDeliveryOptions options)
    {
        if (options.ContextWindowCharacters is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The context window must be between 1 and 4096 characters.");
        }

        if (options.MaximumDirectValueCharacters is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The direct-value limit must be between 1 and 1048576 characters.");
        }
    }

    private static bool AnyInputKeyHeld() =>
        IsKeyHeld(VkControl) ||
        IsKeyHeld(VkMenu) ||
        IsKeyHeld(VkShift) ||
        IsKeyHeld(VkLeftWindows) ||
        IsKeyHeld(VkRightWindows) ||
        IsKeyHeld(VkV);

    private static bool IsKeyHeld(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static string RuntimeId(AutomationElement element) =>
        element.GetRuntimeId() is { Length: > 0 } runtimeId
            ? string.Join('.', runtimeId)
            : string.Empty;

    private static TextDeliveryRefusalReason RefusalFor(TargetContextStatus status) => status switch
    {
        TargetContextStatus.TargetUnavailable => TextDeliveryRefusalReason.TargetUnavailable,
        TargetContextStatus.TargetChanged => TextDeliveryRefusalReason.TargetChanged,
        TargetContextStatus.Protected => TextDeliveryRefusalReason.ProtectedField,
        TargetContextStatus.Elevated => TextDeliveryRefusalReason.ElevatedTarget,
        _ => TextDeliveryRefusalReason.AccessibilityUnavailable,
    };

    /// <summary>
    /// The boundary of one UI Automation call: what Windows accessibility refuses inside it is thrown
    /// on as <see cref="AutomationRefusalException"/>, which the adapter answers "accessibility
    /// unavailable"; a disposal, and anything not in accessibility's vocabulary, comes out unchanged.
    /// </summary>
    /// <remarks>
    /// THE ENVIRONMENT IS NAMED AT THE CALL, NOT BY THE EXCEPTION'S TYPE (plan-2 step 13). UI Automation
    /// answers a refused operation as InvalidOperationException, and so does this adapter's own code
    /// when it has a bug; the type cannot tell them apart, and neither can the throwing assembly -
    /// UI Automation hands most failures to Marshal.ThrowExceptionForHR, which raises the exception
    /// from the runtime itself. What tells them apart is where the exception came out: inside a
    /// call that is nothing but UI Automation, it is the control refusing; outside one, it is ours.
    /// So every call into UI Automation is made through here and nothing else is. An
    /// ObjectDisposedException is never a refusal: it derives from InvalidOperationException, and
    /// it is the adapter's gate gone under the delivery because the app is leaving.
    /// </remarks>
    internal static T Automation<T>(Func<T> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            return call();
        }
        catch (Exception exception) when (IsAutomationRefusal(exception))
        {
            throw new AutomationRefusalException(exception);
        }
    }

    /// <summary>The families accessibility refuses in, when they come out of a UI Automation call; a disposal is not one.</summary>
    internal static bool IsAutomationRefusal(Exception exception) => exception switch
    {
        ObjectDisposedException => false,
        ElementNotAvailableException or ElementNotEnabledException or NoClickablePointException or ProxyAssemblyNotLoadedException => true,
        InvalidOperationException or COMException or UnauthorizedAccessException or Win32Exception => true,
        _ => false,
    };

    /// <summary>Whether an exception is one a UI Automation call refused, answered as "accessibility unavailable" rather than escaping.</summary>
    internal static bool IsExpectedAutomationFailure(Exception exception) => exception is AutomationRefusalException;

    private static bool IsFullscreen(nint window)
    {
        if (!GetWindowRect(window, out var windowRect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != 0 &&
            GetMonitorInfo(monitor, ref info) &&
            windowRect.Left <= info.Monitor.Left &&
            windowRect.Top <= info.Monitor.Top &&
            windowRect.Right >= info.Monitor.Right &&
            windowRect.Bottom >= info.Monitor.Bottom;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkV = 0x56;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}

/// <summary>What Windows accessibility refused inside one UI Automation call, with the refusal it gave; raised only by <see cref="WindowsTextTargetAdapter.Automation{T}"/>.</summary>
public sealed class AutomationRefusalException : Exception
{
    public AutomationRefusalException()
    {
    }

    public AutomationRefusalException(string message)
        : base(message)
    {
    }

    public AutomationRefusalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal AutomationRefusalException(Exception refusal)
        : base("Windows accessibility refused a UI Automation call.", refusal)
    {
    }
}
