using System.Globalization;
using EnviousWispr.ASR;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Pipeline;

namespace EnviousWispr.App;

/// <summary>
/// The NVIDIA graphics runtime as a separate download: offered only where it would be used, fetched and
/// verified by the same store as the speech models, and put to work by restarting the engine on the card.
/// </summary>
/// <remarks>
/// NOT IN THE STORE PACKAGE (founder decision 2026-09-25). About 1.3 GB of NVIDIA libraries that only an
/// NVIDIA owner can use would have made the download about four times larger for everyone, so they arrive
/// the way a speech model does: a manifest compiled into the build (<c>models/manifests/cuda-runtime.json</c>),
/// bytes from the mirror, every file hashed before admission, an interrupted download resumed. Until it is
/// installed the app runs on the processor exactly as it did before, and this row says so.
/// </remarks>
public partial class App
{
    private CancellationTokenSource? _graphicsRuntimeDownload;
    private Task? _graphicsRuntimeDelivery;
    private ConfiguredTranscription? _configuredTranscription;
    private bool _graphicsRuntimeInstalled;

    // ONE RECONFIGURATION AT A TIME. A downloaded speech model and a downloaded graphics runtime both tear
    // the engines down and configure them again; two of those interleaved would leave one engine built and
    // unowned. Both take this gate around the pair.
    private readonly SemaphoreSlim _engineReconfiguration = new(1, 1);

    /// <summary>What the last configuration chose and saw, which is what the offer is asked about.</summary>
    private sealed record ConfiguredTranscription(FinalAsrEngine Engine, string ModelDirectory, HardwareSnapshot Hardware);

    /// <summary>
    /// The CUDA folder the next worker is given: the environment override, the pack the store verifies,
    /// or a hand-provisioned folder, in that order (<see cref="CudaRuntimeDirectory"/>).
    /// </summary>
    /// <remarks>
    /// THE STORE ANSWERS "IS THE PACK INSTALLED", NOT A POINTER READ, so a pack whose files changed after
    /// admission counts as absent and is offered again - and the download then replaces exactly the files
    /// that no longer match. It re-hashes the pack to say so, as it does every speech model at launch.
    /// </remarks>
    private async Task<string?> ResolveCudaRuntimeDirectoryAsync()
    {
        var active = await CreateModelStore()
            .OpenActiveOfflineAsync(CudaRuntimeDirectory.PackId)
            .ConfigureAwait(true);
        var packDirectory = active.Succeeded ? active.Installed?.DirectoryPath : null;
        _graphicsRuntimeInstalled = packDirectory is not null;
        return CudaRuntimeDirectory.ForApplication(_dataDirectory, packDirectory);
    }

    /// <summary>Shows the graphics runtime row when the offer applies or the pack is installed, and hides it otherwise.</summary>
    private void PresentGraphicsRuntime()
    {
        // A DOWNLOAD IN PROGRESS OWNS THE ROW; its progress and its outcome are what it shows.
        if (_graphicsRuntimeDownload is not null)
        {
            return;
        }

        if (_graphicsRuntimeInstalled)
        {
            // Whether the card then STARTED is the session status's to say: "Your graphics card did not start"
            // is the existing advisory for exactly that.
            _window?.SetGraphicsRuntimeDelivery(new("Graphics support for your NVIDIA card is installed and verified."));
            return;
        }

        if (!ShouldOfferGraphicsRuntime() || GraphicsRuntimeBytes() is not { } totalBytes)
        {
            _window?.SetGraphicsRuntimeDelivery(null);
            return;
        }

        _window?.SetGraphicsRuntimeDelivery(new(
            $"Dictation is running on the processor. Download graphics support (about {Gigabytes(totalBytes)} GB) " +
            "to use your NVIDIA graphics card, verified file by file.",
            CanDownload: true));
    }

    private bool ShouldOfferGraphicsRuntime()
    {
        if (_configuredTranscription is not { } configured)
        {
            return false;
        }

        var whisper = configured.Engine == FinalAsrEngine.Whisper;
        return GraphicsRuntimeOffer.ShouldOffer(
            configured.Hardware,
            configured.Engine,
            whisper ? null : new LocalParakeetModelProbe().Probe(configured.ModelDirectory),
            whisper ? new LocalWhisperModelProbe().Probe(configured.ModelDirectory) : null,
            _graphicsRuntimeInstalled);
    }

    private static long? GraphicsRuntimeBytes()
    {
        var manifest = BundledModelManifests.Load(
            CudaRuntimeDirectory.PackId,
            new ModelManifestVerifier(new Dictionary<string, string>()));
        return manifest.Succeeded ? manifest.Manifest!.Payload.Files.Sum(file => file.SizeBytes) : null;
    }

    private void OnGraphicsRuntimeDownloadRequested()
    {
        // ONE AT A TIME, READ OFF THE TASK ITSELF, as the speech model does: a delivery still running
        // refuses a second request, one that has ended does not.
        if (_graphicsRuntimeDelivery is { IsCompleted: false } || Leaving)
        {
            return;
        }

        // ONE TRACKED TASK, from the request to the engine restarted on the card: the exit policy cancels
        // its download and the lifetime joins the whole of it under Quiesce.
        _graphicsRuntimeDelivery = DeliverGraphicsRuntimeAsync();
    }

    private void OnGraphicsRuntimeDownloadCancelRequested() => _graphicsRuntimeDownload?.Cancel();

    private async Task DeliverGraphicsRuntimeAsync()
    {
        using (var download = new CancellationTokenSource())
        {
            _graphicsRuntimeDownload = download;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.GraphicsRuntimeDeliveryStarted));
            _window?.SetGraphicsRuntimeDelivery(new("Downloading graphics support…", Percent: 0, CanCancel: true));
            try
            {
                // DICTATION KEEPS WORKING ON THE PROCESSOR THROUGHOUT. Unlike a missing speech model there is
                // a running engine, and nothing here touches it until the files are verified and the
                // session is free.
                var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
                var provisioner = new ModelProvisioner(
                    CreateModelStore(new ModelDeliveryProgressObserver(ReportGraphicsRuntimeProgress)),
                    modelId => BundledModelManifests.Load(modelId, verifier));
                var result = await provisioner
                    .ProvisionAsync(CudaRuntimeDirectory.PackId, download.Token)
                    .ConfigureAwait(true);
                if (!result.Succeeded)
                {
                    _logger.Write(new AppLogEntry(
                        DateTimeOffset.UtcNow,
                        AppEventCode.GraphicsRuntimeDeliveryFailed,
                        AppFailureCategory.ModelDelivery,
                        clock.ElapsedMilliseconds));
                    _window?.SetGraphicsRuntimeDelivery(new(GraphicsRuntimeFailureSentence(result), CanDownload: true));
                    return;
                }

                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.GraphicsRuntimeDeliveryCompleted,
                    ElapsedMilliseconds: clock.ElapsedMilliseconds));
            }
            finally
            {
                _graphicsRuntimeDownload = null;
            }
        }

        if (Leaving)
        {
            return;
        }

        _window?.SetGraphicsRuntimeDelivery(new(
            "Download verified. Dictation moves to your graphics card as soon as nothing is being dictated."));
        await SwitchTranscriptionToGraphicsRuntimeAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Restarts the engine so its next worker is given the new runtime folder - under a session hold, so no
    /// dictation can start while the engine is down.
    /// </summary>
    /// <remarks>
    /// THE SAME PATH A DOWNLOADED SPEECH MODEL TAKES (tear down, then the launch configuration), with one
    /// difference that matters: a speech model arrives where there was no engine, and this arrives beside
    /// a working one the person may be dictating into. So it waits until the session can be HELD - nothing
    /// recording, nothing processing - and keeps the hold until the new engine has started. A press during
    /// those seconds is refused with a pill that says why, rather than recording into an engine that is
    /// not there.
    /// </remarks>
    private async Task SwitchTranscriptionToGraphicsRuntimeAsync()
    {
        while (!Leaving)
        {
            var attempt = _sessionCoordinator?.TryHold(SessionHolder.GraphicsRuntimeSwitch);
            if (attempt?.Hold is { } hold)
            {
                using (hold)
                {
                    if (SessionBusyStatus(SessionHolder.GraphicsRuntimeSwitch) is { } busy)
                    {
                        _window?.SetSessionStatus(busy);
                    }

                    await ReconfigureTranscriptionAsync().ConfigureAwait(true);
                }

                if (!Leaving)
                {
                    PresentGraphicsRuntime();
                }

                return;
            }

            if (attempt is null || attempt.Refusal == SessionHoldRefusal.Closed)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
        }
    }

    /// <summary>Tears the engines down and runs the launch configuration again, one caller at a time.</summary>
    private async Task ReconfigureTranscriptionAsync()
    {
        await _engineReconfiguration.WaitAsync().ConfigureAwait(true);
        try
        {
            // NOT UNDER AN EXIT: the engines are the lifetime's to dispose once leaving has begun, and none
            // are built after it.
            if (Leaving)
            {
                return;
            }

            await TeardownTranscriptionAsync().ConfigureAwait(true);
            if (Leaving)
            {
                return;
            }

            await ConfigureTranscriptionAsync(_settings.Preferences.Dictation.FinalEngine).ConfigureAwait(true);
        }
        finally
        {
            _engineReconfiguration.Release();
        }
    }

    private void ReportGraphicsRuntimeProgress(ModelDeliveryEvent deliveryEvent)
    {
        if (deliveryEvent.TotalBytes is not > 0 || deliveryEvent.CompletedBytes is null)
        {
            return;
        }

        var percent = Math.Clamp(100.0 * deliveryEvent.CompletedBytes.Value / deliveryEvent.TotalBytes.Value, 0, 100);
        var completedMb = Megabytes(deliveryEvent.CompletedBytes.Value);
        var totalMb = Megabytes(deliveryEvent.TotalBytes.Value);
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            // A LATE REPORT MUST NOT REDRAW A FINISHED DOWNLOAD. Reports are queued to the window; one that
            // runs after the delivery ended would put a progress bar back over the outcome.
            if (_graphicsRuntimeDownload is not null)
            {
                _window.SetGraphicsRuntimeDelivery(new(
                    $"Downloading graphics support… {completedMb} of {totalMb} MB, verified as it arrives.",
                    Percent: percent,
                    CanCancel: true));
            }
        });
    }

    private static string Gigabytes(long bytes) =>
        (bytes / 1_000_000_000.0).ToString("0.0", CultureInfo.CurrentCulture);

    /// <summary>One sentence per failure, each naming what the person can do, and that dictation still works.</summary>
    private static string GraphicsRuntimeFailureSentence(ModelDeliveryResult result) => result.Failure switch
    {
        ModelDeliveryFailure.NetworkUnavailable =>
            "Could not reach the download server. Dictation keeps running on the processor; try again and the download resumes where it stopped.",
        ModelDeliveryFailure.SourceRejected =>
            "The download server refused the request. Dictation keeps running on the processor; try again later.",
        ModelDeliveryFailure.InsufficientDisk =>
            $"Not enough free disk space for graphics support: about {Megabytes(result.RequiredBytes)} MB needed, {Megabytes(result.AvailableBytes)} MB free.",
        ModelDeliveryFailure.IntegrityMismatch =>
            "A downloaded file did not match its published checksum, so it was discarded. Try again.",
        ModelDeliveryFailure.StorageUnavailable =>
            "The graphics support folder could not be written. Check that this PC's app data folder is not read-only.",
        ModelDeliveryFailure.AppVersionTooOld =>
            "Graphics support needs a newer EnviousWispr. Check for updates first.",
        ModelDeliveryFailure.Cancelled =>
            "Download cancelled. Dictation keeps running on the processor; the download resumes from where it stopped when you try again.",
        _ => "This build's graphics support manifest is not valid. Reinstall EnviousWispr.",
    };
}
