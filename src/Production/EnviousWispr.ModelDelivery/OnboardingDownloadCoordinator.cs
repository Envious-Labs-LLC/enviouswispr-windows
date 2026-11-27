using System.Globalization;

namespace EnviousWispr.ModelDelivery;

public enum OnboardingDownloadStage
{
    Pending,
    Downloading,
    Activating,
    Installed,
    Cancelled,
    Failed,
    CannotDownload,
}

public sealed record OnboardingDownloadPlan(
    string SpeechModelId,
    long SpeechTotalBytes,
    string? PolishModelId,
    long PolishTotalBytes,
    bool ParallelDownloads,
    bool SpeechAlreadyInstalled = false,
    bool PolishAlreadyInstalled = false,
    long? SpeechDiskBytes = null,
    long? PolishDiskBytes = null);

public sealed record OnboardingDownloadPreflightResult(
    bool Succeeded,
    ModelDeliveryFailure Failure,
    long RequiredBytes,
    long AvailableBytes,
    string? Message = null);

public sealed record OnboardingModelDownloadState(
    string ModelId,
    OnboardingDownloadStage Stage,
    long CompletedBytes,
    long TotalBytes,
    double Percent,
    string? StatusDetail = null,
    bool CanRetry = false,
    bool CanCancel = false);

public sealed record OnboardingDownloadSnapshot(
    OnboardingDownloadPlan Plan,
    OnboardingModelDownloadState Speech,
    OnboardingModelDownloadState? Polish,
    double CombinedPercent,
    bool IsRunning,
    bool IsSpeechReady,
    bool IsPolishReady,
    string? BackgroundPillSummary,
    string? FailureMessage);

/// <summary>
/// Dual onboarding download coordinator supporting parallel (>= 16 GB RAM) vs sequential Speech-then-EG-1
/// orchestration, disk-space preflight (+500 MB headroom), per-model retry/cancel, immediate model activation
/// callbacks, and background pill summary formatting.
/// </summary>
public sealed class OnboardingDownloadCoordinator : IDisposable
{
    public const long DiskHeadroomBytes = 500L * 1024 * 1024;

    private readonly OnboardingDownloadPlan _plan;
    private readonly Func<string, IModelDeliveryObserver, CancellationToken, Task<ModelDeliveryResult>> _provisionAsync;
    private readonly Func<CancellationToken, Task>? _onSpeechInstalledAsync;
    private readonly Func<CancellationToken, Task>? _onPolishInstalledAsync;
    private readonly Action<OnboardingDownloadSnapshot>? _onSnapshotChanged;
    private readonly IDiskSpaceProbe _diskSpaceProbe;
    private readonly string _diskProbePath;
    private readonly Func<ModelDeliveryResult, string> _failureFormatter;

    private readonly Lock _sync = new();
    private CancellationTokenSource? _activeCts;
    private OnboardingModelDownloadState _speechState;
    private OnboardingModelDownloadState? _polishState;
    private bool _isRunning;
    private string? _failureMessage;
    private bool _disposed;

    public OnboardingDownloadCoordinator(
        OnboardingDownloadPlan plan,
        Func<string, IModelDeliveryObserver, CancellationToken, Task<ModelDeliveryResult>> provisionAsync,
        Func<CancellationToken, Task>? onSpeechInstalledAsync = null,
        Func<CancellationToken, Task>? onPolishInstalledAsync = null,
        Action<OnboardingDownloadSnapshot>? onSnapshotChanged = null,
        IDiskSpaceProbe? diskSpaceProbe = null,
        string? diskProbePath = null,
        Func<ModelDeliveryResult, string>? failureFormatter = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.SpeechModelId);
        ArgumentNullException.ThrowIfNull(provisionAsync);

        _plan = plan;
        _provisionAsync = provisionAsync;
        _onSpeechInstalledAsync = onSpeechInstalledAsync;
        _onPolishInstalledAsync = onPolishInstalledAsync;
        _onSnapshotChanged = onSnapshotChanged;
        _diskSpaceProbe = diskSpaceProbe ?? new WindowsDiskSpaceProbe();
        _diskProbePath = string.IsNullOrWhiteSpace(diskProbePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : diskProbePath;
        _failureFormatter = failureFormatter ?? DefaultFailureSentence;

        _speechState = new OnboardingModelDownloadState(
            ModelId: plan.SpeechModelId,
            Stage: plan.SpeechAlreadyInstalled ? OnboardingDownloadStage.Installed : OnboardingDownloadStage.Pending,
            CompletedBytes: plan.SpeechAlreadyInstalled ? plan.SpeechTotalBytes : 0,
            TotalBytes: Math.Max(0, plan.SpeechTotalBytes),
            Percent: plan.SpeechAlreadyInstalled ? 100d : 0d);

        _polishState = string.IsNullOrWhiteSpace(plan.PolishModelId)
            ? null
            : new OnboardingModelDownloadState(
                ModelId: plan.PolishModelId,
                Stage: plan.PolishAlreadyInstalled ? OnboardingDownloadStage.Installed : OnboardingDownloadStage.Pending,
                CompletedBytes: plan.PolishAlreadyInstalled ? plan.PolishTotalBytes : 0,
                TotalBytes: Math.Max(0, plan.PolishTotalBytes),
                Percent: plan.PolishAlreadyInstalled ? 100d : 0d);
    }

    public OnboardingDownloadSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return BuildSnapshotLocked();
            }
        }
    }

    public static long ComputeManifestRequiredDiskBytes(VerifiedModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Payload.Files.Sum(file => file.SizeBytes + (file.IsSharded ? file.SizeBytes : 0L));
    }

    public static OnboardingDownloadPreflightResult PreflightDiskSpace(
        OnboardingDownloadPlan plan,
        long availableBytes)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var speechRemaining = plan.SpeechAlreadyInstalled ? 0L : Math.Max(0L, plan.SpeechDiskBytes ?? plan.SpeechTotalBytes);
        var polishRemaining = string.IsNullOrWhiteSpace(plan.PolishModelId) || plan.PolishAlreadyInstalled
            ? 0L
            : Math.Max(0L, plan.PolishDiskBytes ?? plan.PolishTotalBytes);
        var neededModelBytes = speechRemaining + polishRemaining;

        if (neededModelBytes == 0)
        {
            return new OnboardingDownloadPreflightResult(
                Succeeded: true,
                Failure: ModelDeliveryFailure.None,
                RequiredBytes: 0,
                AvailableBytes: Math.Max(0L, availableBytes));
        }

        var requiredBytes = checked(neededModelBytes + DiskHeadroomBytes);
        if (availableBytes >= requiredBytes)
        {
            return new OnboardingDownloadPreflightResult(
                Succeeded: true,
                Failure: ModelDeliveryFailure.None,
                RequiredBytes: requiredBytes,
                AvailableBytes: availableBytes);
        }

        var message = string.Format(
            CultureInfo.InvariantCulture,
            "Not enough free disk space: about {0} MB needed (with 500 MB headroom), {1} MB free.",
            Megabytes(requiredBytes),
            Megabytes(Math.Max(0L, availableBytes)));

        return new OnboardingDownloadPreflightResult(
            Succeeded: false,
            Failure: ModelDeliveryFailure.InsufficientDisk,
            RequiredBytes: requiredBytes,
            AvailableBytes: Math.Max(0L, availableBytes),
            Message: message);
    }

    public static string? FormatBackgroundPillSummary(OnboardingDownloadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsRunning)
        {
            return null;
        }

        var hasActiveSlot = snapshot.Speech.Stage is OnboardingDownloadStage.Downloading or OnboardingDownloadStage.Activating ||
            snapshot.Polish?.Stage is OnboardingDownloadStage.Downloading or OnboardingDownloadStage.Activating;
        if (!hasActiveSlot)
        {
            return null;
        }

        var rounded = Math.Clamp((int)Math.Round(snapshot.CombinedPercent, MidpointRounding.AwayFromZero), 0, 100);
        return string.Format(CultureInfo.InvariantCulture, "Downloading models \u00B7 {0}%", rounded);
    }

    public OnboardingDownloadPreflightResult Preflight(long? availableBytesOverride = null)
    {
        long availableBytes;
        if (availableBytesOverride is { } supplied)
        {
            availableBytes = supplied;
        }
        else
        {
            try
            {
                availableBytes = _diskSpaceProbe.GetAvailableBytes(_diskProbePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new OnboardingDownloadPreflightResult(
                    Succeeded: false,
                    Failure: ModelDeliveryFailure.StorageUnavailable,
                    RequiredBytes: 0,
                    AvailableBytes: 0,
                    Message: "Could not read free disk space on this drive.");
            }
        }

        return PreflightDiskSpace(_plan, availableBytes);
    }

    public async Task<OnboardingDownloadSnapshot> RunAsync(CancellationToken externalCancellation = default)
    {
        CancellationTokenSource? runCts = null;
        bool needSpeech;
        bool needPolish;

        var alreadyComplete = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRunning)
            {
                return BuildSnapshotLocked();
            }

            _failureMessage = null;
            needSpeech = _speechState.Stage != OnboardingDownloadStage.Installed;
            needPolish = _polishState is not null && _polishState.Stage != OnboardingDownloadStage.Installed;

            if (!needSpeech && !needPolish)
            {
                alreadyComplete = true;
            }
            else
            {
                _activeCts?.Dispose();
                _activeCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
                runCts = _activeCts;
                _isRunning = true;

                if (needSpeech)
                {
                    _speechState = _speechState with
                    {
                        Stage = OnboardingDownloadStage.Pending,
                        CanRetry = false,
                        CanCancel = true,
                        StatusDetail = "Waiting to download\u2026",
                    };
                }

                if (needPolish && _polishState is not null)
                {
                    _polishState = _polishState with
                    {
                        Stage = OnboardingDownloadStage.Pending,
                        CanRetry = false,
                        CanCancel = true,
                        StatusDetail = "Waiting to download\u2026",
                    };
                }
            }
        }

        if (alreadyComplete || runCts is null)
        {
            return PublishSnapshot();
        }

        var preflight = Preflight();
        if (!preflight.Succeeded)
        {
            lock (_sync)
            {
                _isRunning = false;
                _failureMessage = preflight.Message;
                if (needSpeech)
                {
                    _speechState = _speechState with
                    {
                        Stage = OnboardingDownloadStage.Failed,
                        CanRetry = true,
                        CanCancel = false,
                        StatusDetail = preflight.Message,
                    };
                }

                if (needPolish && _polishState is not null)
                {
                    _polishState = _polishState with
                    {
                        Stage = OnboardingDownloadStage.Failed,
                        CanRetry = true,
                        CanCancel = false,
                        StatusDetail = preflight.Message,
                    };
                }
            }

            return PublishSnapshot();
        }

        PublishSnapshot();

        try
        {
            if (_plan.ParallelDownloads)
            {
                var tasks = new List<Task>(2);
                if (needSpeech)
                {
                    tasks.Add(DownloadSlotAsync(isSpeech: true, runCts.Token));
                }

                if (needPolish)
                {
                    tasks.Add(DownloadSlotAsync(isSpeech: false, runCts.Token));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            else
            {
                if (needSpeech)
                {
                    await DownloadSlotAsync(isSpeech: true, runCts.Token).ConfigureAwait(false);
                }

                if (needPolish && !runCts.Token.IsCancellationRequested)
                {
                    await DownloadSlotAsync(isSpeech: false, runCts.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _isRunning = false;
                if (runCts.IsCancellationRequested)
                {
                    MarkPendingOrActiveCancelledLocked(ref _speechState);
                    if (_polishState is { } currentPolish)
                    {
                        MarkPendingOrActiveCancelledLocked(ref currentPolish);
                        _polishState = currentPolish;
                    }
                }
            }
        }

        return PublishSnapshot();
    }

    public Task<OnboardingDownloadSnapshot> RetryFailedAsync(CancellationToken externalCancellation = default) =>
        RunAsync(externalCancellation);

    public void Cancel()
    {
        lock (_sync)
        {
            _activeCts?.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    private async Task DownloadSlotAsync(bool isSpeech, CancellationToken cancellationToken)
    {
        string modelId;
        long plannedBytes;
        lock (_sync)
        {
            var current = isSpeech ? _speechState : _polishState!;
            modelId = current.ModelId;
            plannedBytes = current.TotalBytes;
            UpdateSlotLocked(isSpeech, current with
            {
                Stage = OnboardingDownloadStage.Downloading,
                CanRetry = false,
                CanCancel = true,
                StatusDetail = "Downloading\u2026",
            });
        }

        PublishSnapshot();

        var observer = new SlotProgressObserver(this, isSpeech, plannedBytes);
        ModelDeliveryResult result;
        try
        {
            result = await _provisionAsync(modelId, observer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new ModelDeliveryResult(false, ModelDeliveryFailure.Cancelled);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            result = new ModelDeliveryResult(false, ModelDeliveryFailure.NetworkUnavailable);
        }

        if (!result.Succeeded)
        {
            var stage = result.Failure switch
            {
                ModelDeliveryFailure.Cancelled => OnboardingDownloadStage.Cancelled,
                ModelDeliveryFailure.InvalidManifest
                    or ModelDeliveryFailure.UntrustedManifest
                    or ModelDeliveryFailure.UnsupportedManifest
                    or ModelDeliveryFailure.AppVersionTooOld => OnboardingDownloadStage.CannotDownload,
                _ => OnboardingDownloadStage.Failed,
            };
            var detail = _failureFormatter(result);

            lock (_sync)
            {
                var current = isSpeech ? _speechState : _polishState!;
                UpdateSlotLocked(isSpeech, current with
                {
                    Stage = stage,
                    CanRetry = stage is OnboardingDownloadStage.Failed or OnboardingDownloadStage.Cancelled,
                    CanCancel = false,
                    StatusDetail = detail,
                });
                _failureMessage ??= detail;
            }

            PublishSnapshot();
            return;
        }

        lock (_sync)
        {
            var current = isSpeech ? _speechState : _polishState!;
            var finalTotal = current.TotalBytes > 0 ? current.TotalBytes : current.CompletedBytes;
            UpdateSlotLocked(isSpeech, current with
            {
                Stage = OnboardingDownloadStage.Activating,
                CompletedBytes = finalTotal,
                TotalBytes = finalTotal,
                Percent = 100d,
                CanRetry = false,
                CanCancel = false,
                StatusDetail = "Activating\u2026",
            });
        }

        PublishSnapshot();

        var activationCallback = isSpeech ? _onSpeechInstalledAsync : _onPolishInstalledAsync;
        if (activationCallback is not null && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await activationCallback(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                var failureMessage = isSpeech
                    ? "Downloaded the speech model, but local transcription could not start. Retry to try again."
                    : "Downloaded the AI polish model, but local polish could not start. Retry to try again.";
                lock (_sync)
                {
                    var current = isSpeech ? _speechState : _polishState!;
                    UpdateSlotLocked(isSpeech, current with
                    {
                        Stage = OnboardingDownloadStage.Failed,
                        CanRetry = true,
                        CanCancel = false,
                        StatusDetail = failureMessage,
                    });
                    _failureMessage ??= failureMessage;
                }

                PublishSnapshot();
                return;
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                var current = isSpeech ? _speechState : _polishState!;
                var finalTotal = current.TotalBytes > 0 ? current.TotalBytes : current.CompletedBytes;
                UpdateSlotLocked(isSpeech, current with
                {
                    Stage = OnboardingDownloadStage.Installed,
                    CompletedBytes = finalTotal,
                    TotalBytes = finalTotal,
                    Percent = 100d,
                    CanRetry = false,
                    CanCancel = false,
                    StatusDetail = "Ready",
                });
            }

            PublishSnapshot();
        }
    }

    private void OnSlotProgress(bool isSpeech, ModelDeliveryEvent deliveryEvent)
    {
        if (deliveryEvent.TotalBytes is not > 0 || deliveryEvent.CompletedBytes is null)
        {
            return;
        }

        var totalBytes = deliveryEvent.TotalBytes.Value;
        var completedBytes = Math.Clamp(deliveryEvent.CompletedBytes.Value, 0L, totalBytes);
        var percent = Math.Clamp(100.0 * completedBytes / totalBytes, 0d, 100d);

        lock (_sync)
        {
            var current = isSpeech ? _speechState : _polishState!;
            if (current.Stage is not OnboardingDownloadStage.Downloading)
            {
                return;
            }

            var detail = string.Format(
                CultureInfo.InvariantCulture,
                "Downloading\u2026 {0} of {1} MB",
                Megabytes(completedBytes),
                Megabytes(totalBytes));
            UpdateSlotLocked(isSpeech, current with
            {
                CompletedBytes = completedBytes,
                TotalBytes = totalBytes,
                Percent = percent,
                StatusDetail = detail,
            });
        }

        PublishSnapshot();
    }

    private void UpdateSlotLocked(bool isSpeech, OnboardingModelDownloadState updated)
    {
        if (isSpeech)
        {
            _speechState = updated;
        }
        else
        {
            _polishState = updated;
        }
    }

    private static void MarkPendingOrActiveCancelledLocked(ref OnboardingModelDownloadState state)
    {
        if (state.Stage is OnboardingDownloadStage.Pending or OnboardingDownloadStage.Downloading or OnboardingDownloadStage.Activating)
        {
            state = state with
            {
                Stage = OnboardingDownloadStage.Cancelled,
                CanRetry = true,
                CanCancel = false,
                StatusDetail = "Download paused. Retry to resume where it stopped.",
            };
        }
    }

    private OnboardingDownloadSnapshot PublishSnapshot()
    {
        OnboardingDownloadSnapshot snapshot;
        lock (_sync)
        {
            snapshot = BuildSnapshotLocked();
        }

        _onSnapshotChanged?.Invoke(snapshot);
        return snapshot;
    }

    private OnboardingDownloadSnapshot BuildSnapshotLocked()
    {
        var speechTotal = Math.Max(_speechState.TotalBytes, _speechState.CompletedBytes);
        var polishTotal = _polishState is null ? 0L : Math.Max(_polishState.TotalBytes, _polishState.CompletedBytes);
        var totalBytes = speechTotal + polishTotal;
        var completedBytes = (_speechState.Stage == OnboardingDownloadStage.Installed ? speechTotal : _speechState.CompletedBytes) +
            (_polishState is null ? 0L : (_polishState.Stage == OnboardingDownloadStage.Installed ? polishTotal : _polishState.CompletedBytes));

        var combinedPercent = totalBytes > 0
            ? Math.Clamp(100.0 * completedBytes / totalBytes, 0d, 100d)
            : (_speechState.Stage == OnboardingDownloadStage.Installed ? 100d : 0d);

        var isSpeechReady = _speechState.Stage == OnboardingDownloadStage.Installed;
        var isPolishReady = _polishState is null || _polishState.Stage == OnboardingDownloadStage.Installed;

        var provisional = new OnboardingDownloadSnapshot(
            Plan: _plan,
            Speech: _speechState,
            Polish: _polishState,
            CombinedPercent: combinedPercent,
            IsRunning: _isRunning,
            IsSpeechReady: isSpeechReady,
            IsPolishReady: isPolishReady,
            BackgroundPillSummary: null,
            FailureMessage: _failureMessage);

        return provisional with
        {
            BackgroundPillSummary = FormatBackgroundPillSummary(provisional),
        };
    }

    private static long Megabytes(long bytes) =>
        Math.Max(1L, (bytes + (1024L * 1024L - 1L)) / (1024L * 1024L));

    private static string DefaultFailureSentence(ModelDeliveryResult result) => result.Failure switch
    {
        ModelDeliveryFailure.NetworkUnavailable =>
            "Could not reach the model server. Check your connection and try again; the download resumes where it stopped.",
        ModelDeliveryFailure.SourceRejected =>
            "The model server refused the request. Try again later.",
        ModelDeliveryFailure.InsufficientDisk =>
            $"Not enough free disk space: about {Megabytes(result.RequiredBytes)} MB needed, {Megabytes(result.AvailableBytes)} MB free.",
        ModelDeliveryFailure.IntegrityMismatch =>
            "A downloaded file did not match its published checksum, so it was discarded. Try again.",
        ModelDeliveryFailure.StorageUnavailable =>
            "The model folder could not be written. Check that this PC's app data folder is not read-only.",
        ModelDeliveryFailure.AppVersionTooOld =>
            "This model needs a newer EnviousWispr. Check for updates first.",
        ModelDeliveryFailure.Cancelled =>
            "Download paused. It resumes from where it stopped when you try again.",
        _ => "This build's model manifest is not valid. Reinstall EnviousWispr.",
    };

    private sealed class SlotProgressObserver(
        OnboardingDownloadCoordinator owner,
        bool isSpeech,
        long plannedBytes) : IModelDeliveryObserver
    {
        private DateTimeOffset _lastUpdate = DateTimeOffset.MinValue;
        private readonly long _plannedBytes = plannedBytes;

        public void Observe(ModelDeliveryEvent deliveryEvent)
        {
            var milestone = deliveryEvent.Code is not ModelDeliveryEventCode.DownloadStarted;
            var now = DateTimeOffset.UtcNow;
            if (!milestone && now - _lastUpdate < TimeSpan.FromMilliseconds(200))
            {
                return;
            }

            _lastUpdate = now;
            long? total = deliveryEvent.TotalBytes is > 0
                ? deliveryEvent.TotalBytes
                : _plannedBytes > 0 ? _plannedBytes : null;
            owner.OnSlotProgress(isSpeech, deliveryEvent with { TotalBytes = total });
        }
    }
}
