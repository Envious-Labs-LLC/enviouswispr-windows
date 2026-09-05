using System.Globalization;
using System.Reflection;
using EnviousWispr.ASR;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.ModelDelivery;

namespace EnviousWispr.App;

/// <summary>What the window shows about the speech model: a sentence, optional progress, and which buttons apply.</summary>
public sealed record ModelDeliveryPresentation(
    string Text,
    double? Percent = null,
    bool CanDownload = false,
    bool CanCancel = false);

/// <summary>
/// The first-run path a new install was missing: find the model this configuration needs, and if it
/// is absent, fetch, verify and activate it through the store.
/// </summary>
/// <remarks>
/// THE STORE WAS BUILT, TESTED, AND CONSTRUCTED NOWHERE. A fresh install could say "model is not
/// installed" and offer nothing; the only fix was copying files by hand from another computer.
/// This file is the wiring, and only the wiring - manifests are bundled, admission is the store's,
/// and the engine is brought up by the same <c>ConfigureTranscriptionAsync</c> that runs at launch,
/// so a downloaded model and a pre-installed one take the same path from here on. Ref: #92.
/// </remarks>
public partial class App
{
    private static readonly HttpClient ModelDeliveryHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? _modelDownload;
    private IReadOnlyList<string> _missingModelIds = [];

    private string ModelStoreRoot => Path.Combine(_dataDirectory, "models");

    private ModelStore CreateModelStore() => new(
        ModelStoreRoot,
        ModelDeliveryHttpClient,
        new ModelManifestVerifier(new Dictionary<string, string>()),
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0),
        observer: new ModelDeliveryProgressObserver(this));

    /// <summary>
    /// Where a model is loaded from, or null when this configuration cannot run it.
    /// </summary>
    /// <remarks>
    /// The order is <see cref="InstalledModelLocator"/>'s: an explicit override, then the version the
    /// store admitted, then a hand-copied directory when it is COMPLETE, then the development checkout.
    /// </remarks>
    private async Task<string?> ResolveModelDirectoryAsync(
        string modelId,
        string environmentVariable = "ENVIOUSWISPR_MODEL_DIRECTORY")
    {
        var active = await CreateModelStore().OpenActiveOfflineAsync(modelId).ConfigureAwait(true);
        var development = DevelopmentModelDirectory(modelId);
        return InstalledModelLocator.Resolve(
            Environment.GetEnvironmentVariable(environmentVariable),
            active.Succeeded ? active.Installed?.DirectoryPath : null,
            Path.Combine(ModelStoreRoot, modelId),
            development,
            directory => ModelIsComplete(modelId, directory));
    }

    private static string? DevelopmentModelDirectory(string modelId)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        return directory is null ? null : Path.Combine(directory.FullName, "models", modelId);
    }

    private static bool ModelIsComplete(string modelId, string directory)
    {
        if (string.Equals(modelId, ParakeetTranscriptionEngine.ModelId, StringComparison.Ordinal))
        {
            var parakeet = new LocalParakeetModelProbe().Probe(directory);
            return parakeet.Int8Complete || parakeet.Fp32Complete;
        }

        var whisper = new LocalWhisperModelProbe().Probe(directory);
        return string.Equals(modelId, WhisperTranscriptionEngine.PreviewModelId, StringComparison.Ordinal)
            ? whisper.PreviewSmallComplete
            : whisper.QuantizedComplete || whisper.FullPrecisionComplete;
    }

    /// <summary>The models this configuration needs and does not have, final engine first.</summary>
    private async Task<IReadOnlyList<string>> MissingModelIdsAsync(UserPreferences preferences)
    {
        var wanted = new List<string>
        {
            preferences.Dictation.FinalEngine == FinalAsrEngine.Whisper
                ? WhisperTranscriptionEngine.ModelId
                : ParakeetTranscriptionEngine.ModelId,
        };
        if (preferences.LivePreviewEnabled)
        {
            wanted.Add(WhisperTranscriptionEngine.PreviewModelId);
        }

        var missing = new List<string>();
        foreach (var modelId in wanted)
        {
            var variable = modelId == WhisperTranscriptionEngine.PreviewModelId
                ? "ENVIOUSWISPR_PREVIEW_MODEL_DIRECTORY"
                : "ENVIOUSWISPR_MODEL_DIRECTORY";
            if (await ResolveModelDirectoryAsync(modelId, variable).ConfigureAwait(true) is null)
            {
                missing.Add(modelId);
            }
        }

        return missing;
    }

    /// <summary>Tells the window what the speech model situation is, after transcription was configured.</summary>
    private async Task PresentModelDeliveryAsync()
    {
        if (_modelDownload is not null)
        {
            return;
        }

        _missingModelIds = await MissingModelIdsAsync(_settings.Preferences).ConfigureAwait(true);
        if (_missingModelIds.Count == 0)
        {
            _window?.SetModelDelivery(new("The speech model this build pins is installed and verified."));
            return;
        }

        var installable = _missingModelIds.Where(id => BundledModelManifests.TryRead(id) is not null).ToArray();
        if (installable.Length == 0)
        {
            _window?.SetModelDelivery(new(
                "This build cannot download the model it needs. Reinstall EnviousWispr."));
            return;
        }

        var totalBytes = installable.Sum(id =>
            BundledModelManifests.Load(id, new ModelManifestVerifier(new Dictionary<string, string>()))
                .Manifest?.Payload.Files.Sum(file => file.SizeBytes) ?? 0);
        _window?.SetModelDelivery(new(
            $"{DescribeModels(installable)} is not installed on this PC. About {Megabytes(totalBytes)} MB to download, verified file by file.",
            CanDownload: true));
    }

    private async void OnModelDownloadRequested()
    {
        if (_modelDownload is not null || _exitRequested || _disposed)
        {
            return;
        }

        if (_sessionController?.CurrentSession is not null)
        {
            _window?.SetModelDelivery(new("Finish the current dictation, then download.", CanDownload: true));
            return;
        }

        var wanted = _missingModelIds.Where(id => BundledModelManifests.TryRead(id) is not null).ToArray();
        if (wanted.Length == 0)
        {
            await PresentModelDeliveryAsync().ConfigureAwait(true);
            return;
        }

        using var download = new CancellationTokenSource();
        _modelDownload = download;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ModelDeliveryStarted));
        _window?.SetModelDelivery(new($"Downloading {DescribeModels(wanted)}…", Percent: 0, CanCancel: true));
        try
        {
            var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
            var provisioner = new ModelProvisioner(
                CreateModelStore(),
                modelId => BundledModelManifests.Load(modelId, verifier));
            foreach (var modelId in wanted)
            {
                var result = await provisioner.ProvisionAsync(modelId, download.Token).ConfigureAwait(true);
                if (!result.Succeeded)
                {
                    _logger.Write(new AppLogEntry(
                        DateTimeOffset.UtcNow,
                        AppEventCode.ModelDeliveryFailed,
                        AppFailureCategory.ModelDelivery,
                        clock.ElapsedMilliseconds));
                    _modelDownload = null;
                    _window?.SetModelDelivery(new(FailureSentence(result), CanDownload: true));
                    return;
                }
            }

            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.ModelDeliveryCompleted,
                ElapsedMilliseconds: clock.ElapsedMilliseconds));
        }
        finally
        {
            _modelDownload = null;
        }

        // THE DOWNLOADED MODEL TAKES THE LAUNCH PATH. Tearing the engines down and re-running the
        // same configuration the app runs at startup means there is exactly one way a model becomes
        // "ready", and a download cannot drift from it.
        _window?.SetModelDelivery(new("Download verified. Starting local transcription…"));
        await TeardownTranscriptionAsync().ConfigureAwait(true);
        await ConfigureTranscriptionAsync(_settings.Preferences.Dictation.FinalEngine).ConfigureAwait(true);
        await PresentModelDeliveryAsync().ConfigureAwait(true);
    }

    private void OnModelDownloadCancelRequested() => _modelDownload?.Cancel();

    private async Task TeardownTranscriptionAsync()
    {
        if (_previewEngine is not null)
        {
            await _previewEngine.DisposeAsync().ConfigureAwait(true);
            _previewEngine = null;
        }

        if (_transcriptionEngine is not null)
        {
            await _transcriptionEngine.DisposeAsync().ConfigureAwait(true);
            _transcriptionEngine = null;
        }
    }

    private void ReportModelDeliveryProgress(ModelDeliveryEvent deliveryEvent)
    {
        if (deliveryEvent.TotalBytes is not > 0 || deliveryEvent.CompletedBytes is null)
        {
            return;
        }

        var percent = Math.Clamp(100.0 * deliveryEvent.CompletedBytes.Value / deliveryEvent.TotalBytes.Value, 0, 100);
        var completedMb = Megabytes(deliveryEvent.CompletedBytes.Value);
        var totalMb = Megabytes(deliveryEvent.TotalBytes.Value);
        _window?.DispatcherQueue.TryEnqueue(() =>
            _window.SetModelDelivery(new(
                $"Downloading… {completedMb} of {totalMb} MB, verified as it arrives.",
                Percent: percent,
                CanCancel: true)));
    }

    private static string DescribeModels(IReadOnlyList<string> modelIds)
    {
        var names = modelIds.Select(ModelDisplayName).ToArray();
        return names.Length switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => string.Join(", ", names[..^1]) + " and " + names[^1],
        };
    }

    private static string ModelDisplayName(string modelId) => modelId switch
    {
        ParakeetTranscriptionEngine.ModelId => "The Parakeet speech model",
        WhisperTranscriptionEngine.ModelId => "The Whisper speech model",
        WhisperTranscriptionEngine.PreviewModelId => "the Live Preview model",
        _ => modelId,
    };

    private static string Megabytes(long bytes) =>
        ((bytes + (1024 * 1024) - 1) / (1024 * 1024)).ToString(CultureInfo.CurrentCulture);

    /// <summary>
    /// One sentence per failure, each naming what the person can do about it.
    /// </summary>
    private static string FailureSentence(ModelDeliveryResult result) => result.Failure switch
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
            "Download cancelled. It resumes from where it stopped when you try again.",
        _ => "This build's model manifest is not valid. Reinstall EnviousWispr.",
    };

    private sealed class ModelDeliveryProgressObserver(App app) : IModelDeliveryObserver
    {
        private DateTimeOffset _lastReport = DateTimeOffset.MinValue;

        public void Observe(ModelDeliveryEvent deliveryEvent)
        {
            // THE STORE REPORTS EVERY 128 KB CHUNK. A window repainted that often is a window doing
            // nothing else, so progress reaches it about four times a second and on every milestone.
            var milestone = deliveryEvent.Code is not ModelDeliveryEventCode.DownloadStarted;
            var now = DateTimeOffset.UtcNow;
            if (!milestone && now - _lastReport < TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            _lastReport = now;
            app.ReportModelDeliveryProgress(deliveryEvent);
        }
    }
}
