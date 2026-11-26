using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Learning;

namespace EnviousWispr.Services.Input;

/// <summary>
/// Post-paste UI Automation observer that tracks the focused field after dictation delivery
/// and emits anchor-bounded region changes/settlements through <see cref="PastedRegionTracker"/>.
/// Kept completely outside <c>WindowsTextTargetAdapter.cs</c> to honor its UIA boundary gate.
/// </summary>
public sealed class WindowsPastedRegionObserver : IPastedRegionObserver, IDisposable
{
    private readonly Lock _sync = new();
    private CancellationTokenSource? _activeCts;
    private Task? _activeTask;
    private Action<PastedRegionEvent>? _onEvent;
    private PastedRegionTracker? _tracker;
    private long _generation;
    private bool _disposed;

    public bool IsObserving
    {
        get
        {
            lock (_sync)
            {
                return _activeCts is not null && !(_tracker?.HasEnded ?? true);
            }
        }
    }

    public Task StartObservationAsync(
        PasteCompletedEvent paste,
        Action<PastedRegionEvent> onEvent,
        Action<string> onSkipped,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paste);
        ArgumentNullException.ThrowIfNull(onEvent);
        ArgumentNullException.ThrowIfNull(onSkipped);

        CancelObservation();

        CancellationTokenSource cts;
        long gen;
        lock (_sync)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                onSkipped("disposed");
                return Task.CompletedTask;
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCts = cts;
            _onEvent = onEvent;
            _tracker = null;
            _generation++;
            gen = _generation;
        }

        var task = Task.Run(() => RunLoopAsync(paste, gen, onEvent, onSkipped, cts.Token), cts.Token);
        lock (_sync)
        {
            if (_generation == gen)
            {
                _activeTask = task;
            }
        }

        return task;
    }

    public void Finish(PastedRegionEndReason reason)
    {
        PastedRegionTracker? tracker;
        Action<PastedRegionEvent>? callback;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            tracker = _tracker;
            callback = _onEvent;
            cts = _activeCts;
            _tracker = null;
            _activeCts = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (tracker is not null && !tracker.HasEnded && callback is not null)
        {
            var events = tracker.Finish(reason, unchecked((int)Environment.TickCount64));
            foreach (var ev in events)
            {
                callback(ev);
            }
        }
        else
        {
            callback?.Invoke(new PastedRegionEvent.Ended(reason));
        }

        cts?.Dispose();
    }

    public void CancelObservation()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            _generation++;
            cts = _activeCts;
            _activeCts = null;
            _tracker = null;
            _onEvent = null;
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            cts.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }

        CancelObservation();
    }

    private async Task RunLoopAsync(
        PasteCompletedEvent paste,
        long gen,
        Action<PastedRegionEvent> onEvent,
        Action<string> onSkipped,
        CancellationToken token)
    {
        try
        {
            var target = paste.Target;
            if (!target.IsValid || WindowsWindowIdentity.TryIsHigherIntegrityProcess(target.ProcessId) == true)
            {
                if (IsCurrent(gen, token))
                {
                    onSkipped("elevated_or_invalid");
                    ClearIfCurrent(gen);
                }

                return;
            }

            var processName = !string.IsNullOrWhiteSpace(target.ProcessName)
                ? target.ProcessName
                : WindowsWindowIdentity.TryGetProcessName(target.ProcessId);
            var windowClass = WindowsWindowIdentity.TryGetClassName(target.Value);
            var policy = WindowsPastePolicy.Resolve(processName, windowClass);
            if (policy.TargetKind == TextTargetKind.Terminal)
            {
                if (IsCurrent(gen, token))
                {
                    onSkipped("terminal");
                    ClearIfCurrent(gen);
                }

                return;
            }

            PastedRegionTarget? captured = null;
            PastedRegionEndReason? endReason = null;
            string? skipReason = null;
            var captureDeadline = Environment.TickCount64 + PastedRegionTiming.CaptureGraceMs;

            while (IsCurrent(gen, token))
            {
                var attempt = TryCaptureOnce(paste, target, processName);
                if (attempt.Captured is not null)
                {
                    captured = attempt.Captured;
                    break;
                }

                if (attempt.FatalSkipReason is not null)
                {
                    skipReason = attempt.FatalSkipReason;
                    break;
                }

                endReason = attempt.EndReason;
                skipReason = attempt.RetryableSkipReason;
                if (Environment.TickCount64 >= captureDeadline)
                {
                    break;
                }

                await Task.Delay(PastedRegionTiming.CaptureRetryIntervalMs, token).ConfigureAwait(false);
            }

            if (!IsCurrent(gen, token))
            {
                return;
            }

            if (captured is null && skipReason is not null)
            {
                onSkipped(skipReason);
                ClearIfCurrent(gen);
                return;
            }

            if (captured is null)
            {
                onEvent(new PastedRegionEvent.Ended(endReason ?? PastedRegionEndReason.DictatedTextNotFound));
                ClearIfCurrent(gen);
                return;
            }

            var tracker = new PastedRegionTracker(captured, unchecked((int)Environment.TickCount64));
            lock (_sync)
            {
                if (_generation != gen || token.IsCancellationRequested)
                {
                    return;
                }

                _tracker = tracker;
            }

            while (IsCurrent(gen, token) && !tracker.HasEnded)
            {
                await Task.Delay(PastedRegionTiming.PollMs, token).ConfigureAwait(false);
                if (!IsCurrent(gen, token))
                {
                    return;
                }

                var snapshot = ReadLiveSnapshot(target, captured);
                IReadOnlyList<PastedRegionEvent> events;
                lock (_sync)
                {
                    if (_generation != gen || _tracker is not { } liveTracker || liveTracker.HasEnded)
                    {
                        return;
                    }

                    events = liveTracker.Poll(snapshot, unchecked((int)Environment.TickCount64));
                    if (liveTracker.HasEnded)
                    {
                        _activeCts?.Dispose();
                        _activeCts = null;
                        _tracker = null;
                    }
                }

                foreach (var ev in events)
                {
                    onEvent(ev);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            if (IsCurrent(gen, token))
            {
                onEvent(new PastedRegionEvent.Ended(PastedRegionEndReason.CaptureUnsupported));
                ClearIfCurrent(gen);
            }
        }
    }

    private bool IsCurrent(long gen, CancellationToken token)
    {
        lock (_sync)
        {
            return _generation == gen && !token.IsCancellationRequested && !_disposed;
        }
    }

    private void ClearIfCurrent(long gen)
    {
        lock (_sync)
        {
            if (_generation == gen)
            {
                _activeCts?.Dispose();
                _activeCts = null;
                _tracker = null;
            }
        }
    }

    private readonly record struct CaptureAttempt(
        PastedRegionTarget? Captured,
        string? FatalSkipReason,
        PastedRegionEndReason? EndReason,
        string? RetryableSkipReason = null);

    private static CaptureAttempt TryCaptureOnce(
        PasteCompletedEvent paste,
        TargetWindowId target,
        string? processName)
    {
        if (!IsWindow(target.Value))
        {
            return new CaptureAttempt(null, "window_closed", null);
        }

        var foreground = GetForegroundWindow();
        if (foreground != target.Value)
        {
            return new CaptureAttempt(null, null, null, RetryableSkipReason: "destination_mismatch");
        }

        AutomationElement? focused;
        try
        {
            focused = AutomationElement.FocusedElement;
        }
        catch (Exception exception) when (IsExpectedUiaException(exception))
        {
            return new CaptureAttempt(null, null, PastedRegionEndReason.CaptureUnsupported);
        }

        if (focused is null)
        {
            return new CaptureAttempt(null, null, null, RetryableSkipReason: "no_focused_element");
        }

        try
        {
            var current = focused.Current;
            if (current.IsPassword)
            {
                return new CaptureAttempt(null, "secure_field", null);
            }

            if (!WindowsWindowIdentity.IsMatchingFocusProcess(target.Value, target.ProcessId, checked((uint)current.ProcessId)))
            {
                return new CaptureAttempt(null, null, null, RetryableSkipReason: "destination_mismatch");
            }
        }
        catch (Exception exception) when (IsExpectedUiaException(exception))
        {
            return new CaptureAttempt(null, null, PastedRegionEndReason.CaptureUnsupported);
        }

        var read = ReadTextAndCaret(focused);
        if (read.TooLong)
        {
            return new CaptureAttempt(null, null, PastedRegionEndReason.CaptureUnsupported);
        }

        if (!read.Readable || read.Text is null)
        {
            return new CaptureAttempt(null, null, PastedRegionEndReason.CaptureUnsupported);
        }

        if (PastedRegionTracker.TryCaptureTarget(
                read.Text,
                paste.PastedText,
                checked((int)target.ProcessId),
                target.Value,
                processName,
                out var captured,
                out var endReason))
        {
            return new CaptureAttempt(captured, null, null);
        }

        return new CaptureAttempt(null, null, endReason);
    }

    private static PastedRegionTracker.ReadSnapshot ReadLiveSnapshot(TargetWindowId target, PastedRegionTarget captured)
    {
        if (!IsWindow(target.Value))
        {
            return new PastedRegionTracker.ReadSnapshot(false, null, null, false, false, ElementAlive: false);
        }

        var foreground = GetForegroundWindow();
        var sameWindow = foreground == target.Value;
        var sameAppFocus = sameWindow;
        if (!sameAppFocus && foreground != 0)
        {
            _ = GetWindowThreadProcessId(foreground, out var fgPid);
            sameAppFocus = WindowsWindowIdentity.IsMatchingFocusProcess(target.Value, target.ProcessId, fgPid);
        }

        if (!sameAppFocus)
        {
            return new PastedRegionTracker.ReadSnapshot(false, null, null, SameApplicationFocus: false, SameElementFocus: false, ElementAlive: true);
        }

        AutomationElement? focused;
        try
        {
            focused = AutomationElement.FocusedElement;
        }
        catch (Exception exception) when (IsExpectedUiaException(exception))
        {
            return new PastedRegionTracker.ReadSnapshot(false, null, null, SameApplicationFocus: true, SameElementFocus: false, ElementAlive: true);
        }

        if (focused is null)
        {
            return new PastedRegionTracker.ReadSnapshot(false, null, null, SameApplicationFocus: true, SameElementFocus: false, ElementAlive: true);
        }

        bool isSameElementFocus;
        try
        {
            var current = focused.Current;
            if (current.IsPassword)
            {
                return new PastedRegionTracker.ReadSnapshot(false, null, null, SameApplicationFocus: false, SameElementFocus: false, ElementAlive: true);
            }

            isSameElementFocus = sameWindow && WindowsWindowIdentity.IsMatchingFocusProcess(
                target.Value,
                target.ProcessId,
                checked((uint)current.ProcessId));
        }
        catch (Exception exception) when (IsExpectedUiaException(exception))
        {
            return new PastedRegionTracker.ReadSnapshot(false, null, null, SameApplicationFocus: true, SameElementFocus: false, ElementAlive: true);
        }

        var read = ReadTextAndCaret(focused);
        return new PastedRegionTracker.ReadSnapshot(
            read.Readable,
            read.Text,
            read.CaretOffset,
            SameApplicationFocus: true,
            SameElementFocus: isSameElementFocus && read.Readable,
            ElementAlive: true,
            TooLong: read.TooLong);
    }

    private readonly record struct FieldRead(bool Readable, string? Text, int? CaretOffset, bool TooLong);

    private static FieldRead ReadTextAndCaret(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textObj) && textObj is TextPattern textPattern)
            {
                var doc = textPattern.DocumentRange;
                if (doc is not null)
                {
                    var text = doc.GetText(PastedRegionTiming.MaxValueUtf16 + 1);
                    if (text is not null)
                    {
                        if (text.Length > PastedRegionTiming.MaxValueUtf16)
                        {
                            return new FieldRead(false, null, null, TooLong: true);
                        }

                        var caretOffset = TryReadCaretOffset(textPattern, doc);
                        return new FieldRead(true, text, caretOffset, TooLong: false);
                    }
                }
            }

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj) && valueObj is ValuePattern valuePattern)
            {
                var text = valuePattern.Current.Value ?? string.Empty;
                if (text.Length > PastedRegionTiming.MaxValueUtf16)
                {
                    return new FieldRead(false, null, null, TooLong: true);
                }

                return new FieldRead(true, text, CaretOffset: null, TooLong: false);
            }
        }
        catch (Exception exception) when (IsExpectedUiaException(exception))
        {
            return new FieldRead(false, null, null, TooLong: false);
        }

        return new FieldRead(false, null, null, TooLong: false);
    }

    private static int? TryReadCaretOffset(TextPattern textPattern, TextPatternRange doc)
    {
        try
        {
            var selection = textPattern.GetSelection();
            if (selection is null || selection.Length == 0)
            {
                return null;
            }

            var caretRange = selection[0];
            var prefix = doc.Clone();
            prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, caretRange, TextPatternRangeEndpoint.End);
            var prefixText = prefix.GetText(PastedRegionTiming.MaxValueUtf16 + 1);
            return prefixText?.Length;
        }
        catch (Exception exception) when (IsExpectedUiaException(exception))
        {
            return null;
        }
    }

    private static bool IsExpectedUiaException(Exception exception) =>
        exception is ElementNotAvailableException or InvalidOperationException or COMException or NotSupportedException or UnauthorizedAccessException or Win32Exception;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}
