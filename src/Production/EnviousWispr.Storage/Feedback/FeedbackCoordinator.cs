using System.Net.NetworkInformation;
using System.Security;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Feedback;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Diagnostics;

namespace EnviousWispr.Services.Feedback;

public sealed record FeedbackFlushResult(
    int Attempted,
    int Sent,
    int Remaining,
    bool StoppedDueToUnavailable = false,
    bool OutboxUnreadable = false);

/// <summary>
/// Coordinates privacy-safe diagnostics snapshot building, draft persistence, Sentry feedback
/// submission, and durable offline outbox flushing with automatic connectivity-triggered,
/// rate-limit-respecting, bounded-backoff retries.
/// </summary>
public sealed class FeedbackCoordinator : IDisposable
{
    private const int MaxBufferedEventCodes = 20;
    private static readonly TimeSpan DefaultInitialRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoffDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxServerHoldDuration = TimeSpan.FromHours(6);

    private readonly WindowsObservabilityIdentity _identity;
    private readonly ISentryFeedbackTransport _transport;
    private readonly TimeSpan _initialRetryDelay;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Lock _eventsLock = new();
    private readonly Lock _retryLock = new();
    private readonly Queue<string> _recentEventCodes = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _retryLoopTask;
    private TaskCompletionSource? _retryWakeSignal;
    private DateTimeOffset? _retryNotBeforeUtc;
    private bool _retryRequestedWhileRunning;
    private bool _networkWakePending;
    private bool _networkSubscribed;
    private bool _disposed;

    public FeedbackCoordinator(
        FeedbackDraftStore draftStore,
        FeedbackOutbox outbox,
        ISentryFeedbackTransport transport,
        WindowsObservabilityIdentity identity,
        TimeSpan? initialRetryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(draftStore);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(identity);

        DraftStore = draftStore;
        Outbox = outbox;
        _transport = transport;
        _identity = identity;
        _initialRetryDelay = initialRetryDelay is { } delay && delay > TimeSpan.Zero
            ? delay
            : DefaultInitialRetryDelay;
    }

    public FeedbackDraftStore DraftStore { get; }

    public FeedbackOutbox Outbox { get; }

    public void ObserveDiagnosticRecord(PrivacySafeDiagnosticRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObserveEvent(record.Event);
    }

    public void ObserveEvent(AppEventCode eventCode)
    {
        if (!Enum.IsDefined(eventCode))
        {
            return;
        }

        lock (_eventsLock)
        {
            while (_recentEventCodes.Count >= MaxBufferedEventCodes)
            {
                _recentEventCodes.Dequeue();
            }

            _recentEventCodes.Enqueue(eventCode.ToString());
        }
    }

    public FeedbackDiagnosticsSnapshot BuildDiagnosticsSnapshot(
        AppSettings settings,
        HardwareSnapshot? hardware = null,
        IReadOnlyList<string>? recentEventCodesOverride = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var recentCodes = recentEventCodesOverride ?? SnapshotRecentEventCodes();
        var osDisplay = string.IsNullOrWhiteSpace(_identity.OsBuild)
            ? $"{WindowsObservabilityIdentity.OsName} {_identity.OsVersion}"
            : $"{WindowsObservabilityIdentity.OsName} {_identity.OsVersion} (Build {_identity.OsBuild})";

        return new FeedbackDiagnosticsSnapshot(
            appVersion: _identity.AppVersion,
            osVersion: osDisplay,
            finalAsrEngine: settings.Preferences.Dictation.FinalEngine.ToString(),
            polishProvider: settings.Preferences.Polish.Provider.ToString(),
            livePreviewEnabled: settings.Preferences.LivePreviewEnabled,
            livePreviewEngine: settings.Preferences.LivePreviewEngine.ToString(),
            recordingMode: settings.Preferences.Dictation.RecordingMode.ToString(),
            autoStopEnabled: settings.Preferences.Dictation.AutoStopEnabled,
            hardwareClass: ResolveHardwareClass(hardware).ToString(),
            recentEventCodes: recentCodes);
    }

    public async Task<FeedbackSendResult> SubmitAsync(
        FeedbackSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await RestorePersistedHoldAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (TryGetActiveHoldUntil(now) is { } holdUntil)
        {
            return await EnqueueOfflineAsync(
                submission,
                httpStatusCode: null,
                retryAfterUtc: holdUntil,
                cancellationToken).ConfigureAwait(false);
        }

        var transportResult = await _transport.SendAsync(submission, cancellationToken).ConfigureAwait(false);
        await UpdateRateLimitHoldAsync(transportResult.RetryAfterUtc, cancellationToken).ConfigureAwait(false);

        switch (transportResult.Status)
        {
            case FeedbackTransportStatus.Sent:
                DraftStore.ClearIfMatches(submission.Kind, submission.Message, submission.ContactEmail);
                return new FeedbackSendResult(
                    FeedbackSendOutcome.Sent,
                    submission.SubmissionId,
                    transportResult.Detail,
                    transportResult.HttpStatusCode);

            case FeedbackTransportStatus.Unavailable:
                return new FeedbackSendResult(
                    FeedbackSendOutcome.Unavailable,
                    submission.SubmissionId,
                    transportResult.Detail ?? "Feedback delivery is currently unavailable.",
                    transportResult.HttpStatusCode);

            case FeedbackTransportStatus.Rejected:
                return new FeedbackSendResult(
                    FeedbackSendOutcome.Rejected,
                    submission.SubmissionId,
                    transportResult.Detail ?? "Feedback was rejected by the server.",
                    transportResult.HttpStatusCode);

            case FeedbackTransportStatus.TransientFailure:
            default:
                return await EnqueueOfflineAsync(
                    submission,
                    transportResult.HttpStatusCode,
                    transportResult.RetryAfterUtc,
                    cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<FeedbackFlushResult> FlushOutboxAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<FeedbackSubmission> pending;
            try
            {
                pending = await Outbox.PeekAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or SecurityException)
            {
                return new FeedbackFlushResult(
                    Attempted: 0,
                    Sent: 0,
                    Remaining: 0,
                    StoppedDueToUnavailable: true,
                    OutboxUnreadable: true);
            }

            if (pending.Count == 0)
            {
                return new FeedbackFlushResult(0, 0, 0);
            }

            await RestorePersistedHoldAsync(cancellationToken).ConfigureAwait(false);

            var attempted = 0;
            var sent = 0;
            var hitUnavailable = false;
            for (var i = 0; i < pending.Count; i++)
            {
                var now = DateTimeOffset.UtcNow;
                if (TryGetActiveHoldUntil(now) is not null)
                {
                    break;
                }

                var item = pending[i];
                attempted++;
                var result = await _transport.SendAsync(item, cancellationToken).ConfigureAwait(false);
                await UpdateRateLimitHoldAsync(result.RetryAfterUtc, cancellationToken).ConfigureAwait(false);

                if (result.Status == FeedbackTransportStatus.Sent)
                {
                    await Outbox.RemoveAsync(item.SubmissionId, cancellationToken).ConfigureAwait(false);
                    sent++;
                    continue;
                }

                if (result.Status == FeedbackTransportStatus.Rejected)
                {
                    // Preserve rejected reports in a non-retrying state rather than discarding them.
                    await Outbox.MarkRejectedAsync(item.SubmissionId, result.HttpStatusCode, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (result.Status == FeedbackTransportStatus.Unavailable)
                {
                    hitUnavailable = true;
                }

                // Unavailable or transient failure: preserve remaining pending items in order.
                break;
            }

            IReadOnlyList<FeedbackSubmission> remainingPending;
            try
            {
                remainingPending = await Outbox.PeekAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or SecurityException)
            {
                return new FeedbackFlushResult(
                    attempted,
                    sent,
                    Remaining: 0,
                    StoppedDueToUnavailable: true,
                    OutboxUnreadable: true);
            }

            var remaining = remainingPending.Count;
            if (remaining > 0 && !hitUnavailable)
            {
                EnsureRetryLoopRunning();
            }

            return new FeedbackFlushResult(attempted, sent, remaining, hitUnavailable);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_retryLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            UnsubscribeNetworkMonitorUnsafe();
            _retryWakeSignal?.TrySetResult();
        }

        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _flushGate.Dispose();
    }

    private async Task<FeedbackSendResult> EnqueueOfflineAsync(
        FeedbackSubmission submission,
        int? httpStatusCode,
        DateTimeOffset? retryAfterUtc,
        CancellationToken cancellationToken)
    {
        var enqueued = await Outbox.EnqueueAsync(submission, cancellationToken).ConfigureAwait(false);
        if (!enqueued)
        {
            return new FeedbackSendResult(
                FeedbackSendOutcome.OutboxFull,
                submission.SubmissionId,
                "Could not queue feedback offline. Your draft is still saved.",
                httpStatusCode);
        }

        DraftStore.ClearIfMatches(submission.Kind, submission.Message, submission.ContactEmail);
        await UpdateRateLimitHoldAsync(retryAfterUtc, cancellationToken).ConfigureAwait(false);
        EnsureRetryLoopRunning();
        return new FeedbackSendResult(
            FeedbackSendOutcome.QueuedOffline,
            submission.SubmissionId,
            "Saved offline and will send automatically once connected.",
            httpStatusCode);
    }

    public Task<bool> HasUndeliverableReportsAsync(CancellationToken cancellationToken = default) =>
        Outbox.HasUndeliverableAsync(cancellationToken);

    private async Task RestorePersistedHoldAsync(CancellationToken cancellationToken)
    {
        var persisted = await Outbox.GetNotBeforeUtcAsync(cancellationToken).ConfigureAwait(false);
        if (persisted is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (persisted.Value <= now)
        {
            await Outbox.SetNotBeforeUtcAsync(null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var maxHold = now.Add(MaxServerHoldDuration);
        var bounded = persisted.Value > maxHold ? maxHold : persisted.Value;
        lock (_retryLock)
        {
            if (_retryNotBeforeUtc is null || bounded > _retryNotBeforeUtc.Value)
            {
                _retryNotBeforeUtc = bounded;
            }
        }
    }

    private DateTimeOffset? TryGetActiveHoldUntil(DateTimeOffset nowUtc)
    {
        lock (_retryLock)
        {
            if (_retryNotBeforeUtc is not { } notBefore)
            {
                return null;
            }

            if (notBefore <= nowUtc)
            {
                _retryNotBeforeUtc = null;
                return null;
            }

            var maxHold = nowUtc.Add(MaxServerHoldDuration);
            return notBefore > maxHold ? maxHold : notBefore;
        }
    }

    private async Task UpdateRateLimitHoldAsync(DateTimeOffset? retryAfterUtc, CancellationToken cancellationToken)
    {
        if (retryAfterUtc is not { } candidate)
        {
            return;
        }

        var maxHold = DateTimeOffset.UtcNow.Add(MaxServerHoldDuration);
        var bounded = candidate > maxHold ? maxHold : candidate;

        lock (_retryLock)
        {
            if (_retryNotBeforeUtc is null || bounded > _retryNotBeforeUtc.Value)
            {
                _retryNotBeforeUtc = bounded;
            }
        }

        await Outbox.SetNotBeforeUtcAsync(bounded, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureRetryLoopRunning()
    {
        lock (_retryLock)
        {
            if (_disposed)
            {
                return;
            }

            SubscribeNetworkMonitorUnsafe();
            if (_retryLoopTask is { IsCompleted: false })
            {
                // If the active loop is finishing its final pass, force it to recheck before exiting.
                _retryRequestedWhileRunning = true;
                return;
            }

            _retryRequestedWhileRunning = false;
            _retryLoopTask = Task.Run(() => RunRetryLoopAsync(_lifetimeCts.Token));
        }
    }

    private void SubscribeNetworkMonitorUnsafe()
    {
        if (_networkSubscribed)
        {
            return;
        }

        try
        {
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            _networkSubscribed = true;
        }
        catch
        {
            // Network monitoring is best-effort; bounded periodic retry remains active.
        }
    }

    private void UnsubscribeNetworkMonitorUnsafe()
    {
        if (!_networkSubscribed)
        {
            return;
        }

        _networkSubscribed = false;
        try
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable)
        {
            return;
        }

        lock (_retryLock)
        {
            if (!_disposed)
            {
                _networkWakePending = true;
                _retryWakeSignal?.TrySetResult();
            }
        }
    }

    private async Task RunRetryLoopAsync(CancellationToken cancellationToken)
    {
        var delay = _initialRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            var effectiveDelay = ComputeEffectiveDelay(delay, DateTimeOffset.UtcNow);
            Task? wakeTask = null;
            var immediateWake = false;

            lock (_retryLock)
            {
                if (_disposed)
                {
                    return;
                }

                if (_networkWakePending && TryGetActiveHoldUntilLocked(DateTimeOffset.UtcNow) is null)
                {
                    _networkWakePending = false;
                    immediateWake = true;
                }
                else
                {
                    _retryWakeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wakeTask = _retryWakeSignal.Task;
                }
            }

            if (immediateWake)
            {
                delay = _initialRetryDelay;
            }
            else
            {
                try
                {
                    var completed = await Task.WhenAny(wakeTask!, Task.Delay(effectiveDelay, cancellationToken))
                        .ConfigureAwait(false);
                    if (completed == wakeTask)
                    {
                        lock (_retryLock)
                        {
                            _networkWakePending = false;
                        }

                        // Connectivity returned: reset backoff; if a server rate limit still holds, loop will honor it.
                        delay = _initialRetryDelay;
                        if (TryGetActiveHoldUntil(DateTimeOffset.UtcNow) is not null)
                        {
                            continue;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }

            lock (_retryLock)
            {
                _retryWakeSignal = null;
                _retryRequestedWhileRunning = false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var flushResult = await FlushOutboxAsync(cancellationToken).ConfigureAwait(false);
                if (flushResult.Remaining == 0 || flushResult.StoppedDueToUnavailable)
                {
                    lock (_retryLock)
                    {
                        if (_retryRequestedWhileRunning && !flushResult.StoppedDueToUnavailable && !_disposed)
                        {
                            _retryRequestedWhileRunning = false;
                            delay = _initialRetryDelay;
                            continue;
                        }

                        _retryLoopTask = null;
                        UnsubscribeNetworkMonitorUnsafe();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Keep retrying with bounded backoff.
            }

            var doubledTicks = Math.Min(delay.Ticks * 2, MaxBackoffDelay.Ticks);
            delay = TimeSpan.FromTicks(doubledTicks);
        }
    }

    private TimeSpan ComputeEffectiveDelay(TimeSpan backoffDelay, DateTimeOffset nowUtc)
    {
        if (TryGetActiveHoldUntil(nowUtc) is { } holdUntil)
        {
            var holdDuration = holdUntil - nowUtc;
            if (holdDuration > backoffDelay)
            {
                return holdDuration;
            }
        }

        return backoffDelay;
    }

    private DateTimeOffset? TryGetActiveHoldUntilLocked(DateTimeOffset nowUtc)
    {
        if (_retryNotBeforeUtc is not { } notBefore)
        {
            return null;
        }

        if (notBefore <= nowUtc)
        {
            _retryNotBeforeUtc = null;
            return null;
        }

        return notBefore;
    }

    private string[] SnapshotRecentEventCodes()
    {
        lock (_eventsLock)
        {
            return _recentEventCodes.ToArray();
        }
    }

    private static DiagnosticHardwareClass ResolveHardwareClass(HardwareSnapshot? hardware)
    {
        if (hardware is null)
        {
            return DiagnosticHardwareClass.Unknown;
        }

        if (hardware.Cuda.IsDriverAvailable && hardware.Cuda.DeviceCount > 0)
        {
            return DiagnosticHardwareClass.NvidiaCuda;
        }

        if (hardware.GraphicsAdapters.Any(adapter => adapter.IsActive))
        {
            return DiagnosticHardwareClass.GpuPresent;
        }

        return hardware.Status == HardwareProbeStatus.Complete
            ? DiagnosticHardwareClass.CpuOnly
            : DiagnosticHardwareClass.Unknown;
    }
}
