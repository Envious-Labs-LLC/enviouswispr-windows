using System.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;

namespace EnviousWispr.Services.Input;

/// <summary>Result reported once asynchronous post-paste arrival verification and clipboard cleanup settle.</summary>
public sealed record ClipboardCleanupOutcome(
    PasteArrivalStatus ArrivalStatus,
    int ArrivalElapsedMilliseconds,
    bool ClipboardRestored,
    bool ClipboardUncertain,
    bool RetainedOnClipboard,
    bool Preempted);

/// <summary>Deferred post-paste arrival verification, conditional checked-miss retention, and clipboard restoration work.</summary>
public sealed class PendingPostPasteCleanup
{
    public required TextCommitRequest Request { get; init; }

    public required bool EligibleForCheckedMissRetention { get; init; }

    public required bool SnapshotPresent { get; init; }

    public required uint? OwnedSequence { get; init; }

    public required string FallbackText { get; init; }

    public required TimeSpan SettleDelay { get; init; }

    public required TimeSpan SettleFloor { get; init; }

    public Stopwatch? DispatchedWatch { get; init; }

    public PasteRevealPatience.DispatchToken? DispatchToken { get; init; }

    public required Func<CancellationToken, Task<(PasteArrivalStatus Status, int ElapsedMs)>> ObserveArrivalAsync { get; init; }

    public required Func<bool> IsSequenceStillOurs { get; init; }

    public required Func<Task<(bool Restored, bool Uncertain)>> RestoreClipboardAsync { get; init; }

    public required Func<Task<(bool Rewritten, bool SequenceMatched)>> RewriteUnmarkedFallbackAsync { get; init; }

    public Action<ClipboardCleanupOutcome>? OnCompleted { get; init; }
}

/// <summary>
/// Tracks and arbitrates off-critical-path post-paste clipboard settlement, UIA arrival probing,
/// checked-miss fallback retention, and snapshot restoration with clean preemption and exit drain.
/// </summary>
public sealed class ClipboardCleanupTracker
{
    private readonly ClipboardBoardOwnership _ownership;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private ActiveCleanup? _active;

    public ClipboardCleanupTracker(
        ClipboardBoardOwnership? ownership = null,
        TimeProvider? clock = null)
    {
        _ownership = ownership ?? ClipboardBoardOwnership.Shared;
        _clock = clock ?? TimeProvider.System;
    }

    public bool HasPendingCleanup
    {
        get
        {
            lock (_sync)
            {
                return _active is { IsDone: false };
            }
        }
    }

    public void SchedulePostPaste(PendingPostPasteCleanup work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var active = new ActiveCleanup(this, work);
        ActiveCleanup? previous;
        lock (_sync)
        {
            previous = _active;
            _active = active;
        }

        previous?.CancelOnly();
        active.Start();
    }

    /// <summary>
    /// Preempts any in-flight post-paste cleanup so a caller taking clipboard ownership
    /// (such as QuickAdd Copy or a new delivery) sees the restored board without waiting on timers.
    /// </summary>
    public async Task PreemptAsync(CancellationToken cancellationToken = default)
    {
        ActiveCleanup? current;
        lock (_sync)
        {
            current = _active;
        }

        if (current is null)
        {
            return;
        }

        await current.PreemptAndRestoreIfNeededAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for any scheduled background clipboard cleanup
    /// to finish cleanly (without cancelling its restore) before shutting down dependencies.
    /// </summary>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        ActiveCleanup? current;
        lock (_sync)
        {
            current = _active;
        }

        if (current is null)
        {
            return true;
        }

        return await current.DrainAsync(timeout, _clock).ConfigureAwait(false);
    }

    private sealed class ActiveCleanup : IDisposable
    {
        private readonly ClipboardCleanupTracker _tracker;
        private readonly PendingPostPasteCleanup _work;
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<Task> _preemptDelegate;
        private readonly TaskCompletionSource _preemptSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _generation;
        private int _clipboardFinalized;
        private int _notified;
        private Task? _task;

        public ActiveCleanup(ClipboardCleanupTracker tracker, PendingPostPasteCleanup work)
        {
            _tracker = tracker;
            _work = work;
            _preemptDelegate = () => PreemptAndRestoreIfNeededAsync(CancellationToken.None);
        }

        public bool IsDone => _task?.IsCompleted ?? false;

        public void Dispose() => _cts.Dispose();

        public void Start()
        {
            _generation = _tracker._ownership.RegisterCleanup(_preemptDelegate);
            _task = RunAsync();
        }

        public void CancelOnly()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public Task PreemptAndRestoreIfNeededAsync(CancellationToken cancellationToken)
        {
            CancelOnly();
            return CompletePreemptAsync(cancellationToken);
        }

        private async Task CompletePreemptAsync(CancellationToken cancellationToken)
        {
            var running = Volatile.Read(ref _task);
            if (running is not null && !running.IsCompleted)
            {
                try
                {
                    await running.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                }
            }

            if (Interlocked.CompareExchange(ref _clipboardFinalized, 1, 0) == 0)
            {
                var restored = false;
                var uncertain = false;
                try
                {
                    if (_work.Request.Options.RestoreClipboardAfterPaste &&
                        _work.SnapshotPresent &&
                        _work.IsSequenceStillOurs())
                    {
                        (restored, uncertain) = await _work.RestoreClipboardAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    NotifyOnce(new ClipboardCleanupOutcome(
                        PasteArrivalStatus.NotAttempted,
                        0,
                        restored,
                        uncertain,
                        RetainedOnClipboard: false,
                        Preempted: true));
                }
            }

            _preemptSettled.TrySetResult();
            _tracker._ownership.ClearCleanup(_generation, _preemptDelegate);
            ClearFromTracker();
        }

        public async Task<bool> DrainAsync(TimeSpan timeout, TimeProvider clock)
        {
            var running = Volatile.Read(ref _task);
            if (running is null || running.IsCompleted)
            {
                return true;
            }

            try
            {
                await running.WaitAsync(timeout, clock).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        private async Task RunAsync()
        {
            try
            {
                var elapsed = _work.DispatchedWatch?.Elapsed ?? TimeSpan.Zero;
                var remainingSettle = _work.SettleDelay - elapsed;
                if (remainingSettle > TimeSpan.Zero)
                {
                    await Task.Delay(remainingSettle, _tracker._clock, _cts.Token).ConfigureAwait(false);
                }

                var (arrivalStatus, arrivalElapsedMs) = await _work.ObserveArrivalAsync(_cts.Token).ConfigureAwait(false);
                if (_cts.IsCancellationRequested || _tracker._ownership.IsSuperseded(_generation))
                {
                    return;
                }

                await using var restoreLease = await _tracker._ownership
                    .TryBeginRestoreAsync(_generation, _cts.Token)
                    .ConfigureAwait(false);
                if (restoreLease is null || _cts.IsCancellationRequested)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _clipboardFinalized, 1, 0) != 0)
                {
                    return;
                }

                if (_work.EligibleForCheckedMissRetention &&
                    arrivalStatus == PasteArrivalStatus.NotObserved &&
                    _work.IsSequenceStillOurs())
                {
                    var (rewritten, seqMatched) = await _work.RewriteUnmarkedFallbackAsync().ConfigureAwait(false);
                    var retained = rewritten || seqMatched;
                    NotifyOnce(new ClipboardCleanupOutcome(
                        arrivalStatus,
                        arrivalElapsedMs,
                        ClipboardRestored: false,
                        ClipboardUncertain: false,
                        RetainedOnClipboard: retained,
                        Preempted: false));
                    return;
                }

                var restored = false;
                var uncertain = false;
                if (_work.Request.Options.RestoreClipboardAfterPaste && _work.SnapshotPresent)
                {
                    (restored, uncertain) = await _work.RestoreClipboardAsync().ConfigureAwait(false);
                }

                NotifyOnce(new ClipboardCleanupOutcome(
                    arrivalStatus,
                    arrivalElapsedMs,
                    restored,
                    uncertain,
                    RetainedOnClipboard: false,
                    Preempted: false));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                if (Interlocked.CompareExchange(ref _clipboardFinalized, 1, 0) == 0)
                {
                    NotifyOnce(new ClipboardCleanupOutcome(
                        PasteArrivalStatus.Unreadable,
                        0,
                        ClipboardRestored: false,
                        ClipboardUncertain: _work.Request.Options.RestoreClipboardAfterPaste && _work.SnapshotPresent,
                        RetainedOnClipboard: false,
                        Preempted: false));
                }
            }
            finally
            {
                _tracker._ownership.ClearCleanup(_generation, _preemptDelegate);
                ClearFromTracker();
            }
        }

        private void NotifyOnce(ClipboardCleanupOutcome outcome)
        {
            if (Interlocked.CompareExchange(ref _notified, 1, 0) == 0)
            {
                _work.OnCompleted?.Invoke(outcome);
            }
        }

        private void ClearFromTracker()
        {
            lock (_tracker._sync)
            {
                if (ReferenceEquals(_tracker._active, this))
                {
                    _tracker._active = null;
                }
            }
        }
    }
}
