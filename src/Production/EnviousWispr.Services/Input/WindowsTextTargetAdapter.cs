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
        if (WindowsTargetWindowNative.CurrentForegroundWindow() != request.Target.Value)
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
                    var document = textPattern.DocumentRange;
                    var selection = selections[0];
                    var leftRange = selection.Clone();
                    leftRange.MoveEndpointByRange(
                        TextPatternRangeEndpoint.End,
                        selection,
                        TextPatternRangeEndpoint.Start);
                    leftRange.MoveEndpointByUnit(
                        TextPatternRangeEndpoint.Start,
                        TextUnit.Character,
                        -contextCharacters);
                    if (leftRange.CompareEndpoints(
                        TextPatternRangeEndpoint.Start,
                        document,
                        TextPatternRangeEndpoint.Start) < 0)
                    {
                        leftRange.MoveEndpointByRange(
                            TextPatternRangeEndpoint.Start,
                            document,
                            TextPatternRangeEndpoint.Start);
                    }

                    if (leftRange.CompareEndpoints(
                        TextPatternRangeEndpoint.Start,
                        leftRange,
                        TextPatternRangeEndpoint.End) > 0)
                    {
                        leftRange.MoveEndpointByRange(
                            TextPatternRangeEndpoint.Start,
                            leftRange,
                            TextPatternRangeEndpoint.End);
                    }

                    var readLeft = leftRange.GetText(contextCharacters * 2);
                    leftBeforeCaret = readLeft.Length > contextCharacters
                        ? readLeft[^contextCharacters..]
                        : readLeft;
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
        string? previousValue = null) =>
        WindowsTextDeliveryPolicy.EvaluateArrival(
            insertedText,
            observedLeft,
            observedValue,
            previousLeft,
            previousValue);

    internal static PasteKeystrokeKind ResolveCommitKeystroke(
        CaretContext context,
        WindowsPasteRule commitRule) =>
        WindowsTextDeliveryPolicy.ResolveCommitKeystroke(context, commitRule);

    /// <summary>What the commit refuses with when the target, read again just before the write, is not the one the context was captured from - or cannot be read.</summary>
    /// <remarks>
    /// THE SECOND READ KEEPS ITS OWN NAME (plan-2 step 13, round three). An elevated or protected
    /// target says so; accessibility that did not answer the second time says that, with the reason
    /// the capture gave; a target that is gone, or whose caret moved, is a changed target.
    /// </remarks>
    internal static TextDeliveryRefusalReason RevalidationRefusal(TargetContextResult current) =>
        WindowsTextDeliveryPolicy.RevalidationRefusal(current);

    /// <summary>The preflight the paste runs on its own thread, with what accessibility refused translated here, inside the adapter; anything else it throws is a defect and comes out.</summary>
    internal static TextDeliveryRefusalReason GuardedPreflight(Func<TextDeliveryRefusalReason> preflight) =>
        WindowsTextDeliveryPolicy.GuardedPreflight(preflight);

    internal static bool CaretUnchanged(CaretContext expected, CaretContext actual) =>
        WindowsTextDeliveryPolicy.CaretUnchanged(expected, actual);

    internal static TextDeliveryRefusalReason CompatibilityRefusal(
        TextTargetKind targetKind,
        string text) =>
        WindowsTextDeliveryPolicy.CompatibilityRefusal(targetKind, text);

    internal static bool CanUseDirectValueWrite(
        CaretContext context,
        TextCommitRequest request,
        WindowsPasteRule rule) =>
        WindowsTextDeliveryPolicy.CanUseDirectValueWrite(context, request, rule);

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
            if (verified)
            {
                try
                {
                    _ = Automation(static () =>
                    {
                        var focused = AutomationElement.FocusedElement;
                        if (focused is not null &&
                            focused.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObj) &&
                            textPatternObj is TextPattern textPattern &&
                            textPattern.SupportedTextSelection is not SupportedTextSelection.None)
                        {
                            // Win32 EDIT ValuePattern.SetValue sends WM_SETTEXT, which resets the caret
                            // to index 0; move selection back to the end so consecutive dictations append.
                            var document = textPattern.DocumentRange;
                            var endRange = document.Clone();
                            endRange.MoveEndpointByRange(
                                TextPatternRangeEndpoint.Start,
                                document,
                                TextPatternRangeEndpoint.End);
                            endRange.Select();
                        }

                        return true;
                    });
                }
                catch (Exception exception) when (IsExpectedAutomationFailure(exception))
                {
                    // SetValue already verified; best-effort caret positioning at end.
                }
            }

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
                if (WindowsTargetWindowNative.CurrentForegroundWindow() != target.Value)
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

    internal static FocusEvaluationResult EvaluateFocusedElement(
        TargetWindowId target,
        FocusedElementFacts? focus,
        string? win32ChildFocusId,
        bool isExplicitHistoryPaste,
        Func<string?, bool, WindowsPasteRule> resolvePolicy,
        bool allowSameWindowEditableReplacement = true,
        bool? focusProcessMatchesTarget = null) =>
        WindowsTextDeliveryPolicy.EvaluateFocusedElement(
            target,
            focus,
            win32ChildFocusId,
            isExplicitHistoryPaste,
            resolvePolicy,
            allowSameWindowEditableReplacement,
            focusProcessMatchesTarget);

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
        var leftRange = selection.Clone();
        leftRange.MoveEndpointByRange(
            TextPatternRangeEndpoint.End,
            selection,
            TextPatternRangeEndpoint.Start);
        leftRange.MoveEndpointByUnit(
            TextPatternRangeEndpoint.Start,
            TextUnit.Character,
            -options.ContextWindowCharacters);
        var leftAtStart = leftRange.CompareEndpoints(
            TextPatternRangeEndpoint.Start,
            document,
            TextPatternRangeEndpoint.Start) <= 0;
        if (leftAtStart)
        {
            leftRange.MoveEndpointByRange(
                TextPatternRangeEndpoint.Start,
                document,
                TextPatternRangeEndpoint.Start);
        }

        if (leftRange.CompareEndpoints(
            TextPatternRangeEndpoint.Start,
            leftRange,
            TextPatternRangeEndpoint.End) > 0)
        {
            leftRange.MoveEndpointByRange(
                TextPatternRangeEndpoint.Start,
                leftRange,
                TextPatternRangeEndpoint.End);
        }

        var maxReadLength = options.ContextWindowCharacters * 2;
        var left = leftRange.GetText(maxReadLength);
        if (left.Length > options.ContextWindowCharacters)
        {
            left = left[^options.ContextWindowCharacters..];
            leftAtStart = false;
        }

        var rightRange = selection.Clone();
        rightRange.MoveEndpointByRange(
            TextPatternRangeEndpoint.Start,
            selection,
            TextPatternRangeEndpoint.End);
        rightRange.MoveEndpointByUnit(
            TextPatternRangeEndpoint.End,
            TextUnit.Character,
            options.ContextWindowCharacters);
        var rightAtEnd = rightRange.CompareEndpoints(
            TextPatternRangeEndpoint.End,
            document,
            TextPatternRangeEndpoint.End) >= 0;
        if (rightAtEnd)
        {
            rightRange.MoveEndpointByRange(
                TextPatternRangeEndpoint.End,
                document,
                TextPatternRangeEndpoint.End);
        }

        if (rightRange.CompareEndpoints(
            TextPatternRangeEndpoint.Start,
            rightRange,
            TextPatternRangeEndpoint.End) > 0)
        {
            rightRange.MoveEndpointByRange(
                TextPatternRangeEndpoint.End,
                rightRange,
                TextPatternRangeEndpoint.Start);
        }

        var right = rightRange.GetText(maxReadLength);
        if (right.Length > options.ContextWindowCharacters)
        {
            right = right[..options.ContextWindowCharacters];
            rightAtEnd = false;
        }
        return new CaretText(selected, left, leftAtStart, right, rightAtEnd);
    }

    private static CaretContext EmptyContext(
        TargetWindowId target,
        string focusedId,
        TextTargetKind targetKind,
        string? baselineValue = null) =>
        WindowsTextDeliveryPolicy.EmptyContext(target, focusedId, targetKind, baselineValue);

    internal static WindowsPasteRule ResolveTargetPolicy(
        TargetWindowId target,
        string? elementClassName,
        bool isControlEdit) =>
        WindowsTextDeliveryPolicy.ResolveTargetPolicy(target, elementClassName, isControlEdit);

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
        out bool wasReactivated) =>
        WindowsTargetWindowNative.ActivateAndValidateWindow(target, budget, out wasReactivated);

    private static TextDeliveryRefusalReason PreflightInput(
        TargetWindowId target,
        CaretContext expected,
        TextDeliveryOptions options)
    {
        if (!WindowsTargetWindowNative.IsSameForegroundWindowAndProcess(target))
        {
            return TextDeliveryRefusalReason.TargetChanged;
        }

        if (WindowsTargetWindowNative.AnyInputKeyHeld())
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
    internal static TextDeliveryRefusalReason PreflightRefusal(TargetContextResult current, CaretContext expected) =>
        WindowsTextDeliveryPolicy.PreflightRefusal(current, expected);

    private static void ValidateOptions(TextDeliveryOptions options) =>
        WindowsTextDeliveryPolicy.ValidateOptions(options);

    private static string RuntimeId(AutomationElement element) =>
        element.GetRuntimeId() is { Length: > 0 } runtimeId
            ? string.Join('.', runtimeId)
            : string.Empty;

    private static TextDeliveryRefusalReason RefusalFor(TargetContextStatus status) =>
        WindowsTextDeliveryPolicy.RefusalFor(status);

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
