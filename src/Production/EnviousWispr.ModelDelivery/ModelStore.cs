using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EnviousWispr.ModelDelivery;

public sealed class ModelStore
{
    private const string ManifestFileName = ".model-manifest.json";
    private const string LicenseFileName = ".license-notice.txt";
    private const string ActiveFileName = "active.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _rootDirectory;
    private readonly HttpClient _httpClient;
    private readonly ModelManifestVerifier _verifier;
    private readonly IDiskSpaceProbe _diskSpaceProbe;
    private readonly IModelDeliveryObserver _observer;
    private readonly ModelDeliveryOptions _options;
    private readonly Version _appVersion;

    public ModelStore(
        string rootDirectory,
        HttpClient httpClient,
        ModelManifestVerifier verifier,
        Version appVersion,
        IDiskSpaceProbe? diskSpaceProbe = null,
        IModelDeliveryObserver? observer = null,
        ModelDeliveryOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(appVersion);

        _rootDirectory = Path.GetFullPath(rootDirectory);
        _httpClient = httpClient;
        _verifier = verifier;
        _appVersion = appVersion;
        _diskSpaceProbe = diskSpaceProbe ?? new WindowsDiskSpaceProbe();
        _observer = observer ?? NullModelDeliveryObserver.Instance;
        _options = options ?? new ModelDeliveryOptions();
        if (_options.DiskReserveBytes < 0 || _options.MaximumAttemptsPerSource is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public async Task<ModelDeliveryResult> InstallAsync(
        ReadOnlyMemory<byte> signedManifestEnvelope,
        bool activate = true,
        CancellationToken cancellationToken = default)
    {
        var verification = _verifier.Verify(signedManifestEnvelope.Span);
        if (!verification.Succeeded)
        {
            return Fail(MapVerificationFailure(verification.Status));
        }

        return await InstallAsync(verification.Manifest!, activate, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ModelDeliveryResult> InstallAsync(
        VerifiedModelManifest manifest,
        bool activate = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var currentVerification = _verifier.Reverify(manifest);
        if (!currentVerification.Succeeded)
        {
            return Fail(MapVerificationFailure(currentVerification.Status));
        }

        manifest = currentVerification.Manifest!;
        if (!Version.TryParse(manifest.Payload.MinimumAppVersion, out var minimumVersion) ||
            _appVersion < minimumVersion)
        {
            return Fail(ModelDeliveryFailure.AppVersionTooOld);
        }

        _observer.Observe(new(DateTimeOffset.UtcNow, ModelDeliveryEventCode.ManifestAccepted));
        var gate = StoreLocks.GetOrAdd(LockKey(manifest.Payload.ModelId), _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }

        try
        {
            await using var processLock = await AcquireProcessLockAsync(
                manifest.Payload.ModelId,
                cancellationToken).ConfigureAwait(false);
            return await InstallUnderLockAsync(manifest, activate, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(ModelDeliveryFailure.StorageUnavailable);
        }
        catch (IOException)
        {
            return Fail(ModelDeliveryFailure.StorageUnavailable);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ModelDeliveryResult> MigrateLegacyAsync(
        ReadOnlyMemory<byte> signedManifestEnvelope,
        bool activate = true,
        CancellationToken cancellationToken = default)
    {
        var verification = _verifier.Verify(signedManifestEnvelope.Span);
        if (!verification.Succeeded)
        {
            return Fail(MapVerificationFailure(verification.Status));
        }

        var manifest = verification.Manifest!;
        if (!Version.TryParse(manifest.Payload.MinimumAppVersion, out var minimumVersion) ||
            _appVersion < minimumVersion)
        {
            return Fail(ModelDeliveryFailure.AppVersionTooOld);
        }

        var gate = StoreLocks.GetOrAdd(LockKey(manifest.Payload.ModelId), _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }

        try
        {
            await using var processLock = await AcquireProcessLockAsync(
                manifest.Payload.ModelId,
                cancellationToken).ConfigureAwait(false);
            var modelRoot = ModelRoot(manifest.Payload.ModelId);
            foreach (var artifact in manifest.Payload.Files)
            {
                var legacyPath = SafeCombine(modelRoot, artifact.RelativePath);
                if (!await MatchesAsync(legacyPath, artifact, cancellationToken).ConfigureAwait(false))
                {
                    return Fail(ModelDeliveryFailure.IntegrityMismatch);
                }
            }

            var required = manifest.Payload.Files.Sum(file => file.SizeBytes);
            var available = _diskSpaceProbe.GetAvailableBytes(_rootDirectory);
            if (!HasSufficientDisk(required, available))
            {
                return Fail(ModelDeliveryFailure.InsufficientDisk, required, available);
            }

            var staging = StagingDirectory(manifest);
            RecreateDirectory(staging);
            foreach (var artifact in manifest.Payload.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = SafeCombine(modelRoot, artifact.RelativePath);
                var destination = SafeCombine(staging, artifact.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await CopyFileAsync(source, destination, cancellationToken).ConfigureAwait(false);
            }

            var admitted = await AdmitAsync(manifest, staging, activate, cancellationToken)
                .ConfigureAwait(false);
            if (!admitted.Succeeded)
            {
                return admitted;
            }

            foreach (var artifact in manifest.Payload.Files)
            {
                File.Delete(SafeCombine(modelRoot, artifact.RelativePath));
            }

            RemoveEmptyLegacyDirectories(modelRoot, manifest.Payload.Files);
            return admitted;
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(ModelDeliveryFailure.StorageUnavailable);
        }
        catch (IOException)
        {
            return Fail(ModelDeliveryFailure.StorageUnavailable);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ModelDeliveryResult> ActivateAsync(
        string modelId,
        string version,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(modelId, version);
        var gate = StoreLocks.GetOrAdd(LockKey(modelId), _ => new SemaphoreSlim(1, 1));
        var acquired = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            await using var processLock = await AcquireProcessLockAsync(modelId, cancellationToken)
                .ConfigureAwait(false);
            var candidates = VersionDirectories(modelId, version);
            foreach (var candidate in candidates.OrderByDescending(path => path, StringComparer.Ordinal))
            {
                var installed = await VerifyInstalledDirectoryAsync(candidate, cancellationToken)
                    .ConfigureAwait(false);
                if (installed is not null)
                {
                    await WriteActivePointerAsync(installed, cancellationToken).ConfigureAwait(false);
                    _observer.Observe(new(DateTimeOffset.UtcNow, ModelDeliveryEventCode.ModelActivated));
                    return new(true, Installed: installed);
                }
            }

            return Fail(ModelDeliveryFailure.VersionNotInstalled);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(ModelDeliveryFailure.StorageUnavailable);
        }
        catch (IOException)
        {
            return Fail(ModelDeliveryFailure.StorageUnavailable);
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
            }
        }
    }

    public async Task<ModelDeliveryResult> OpenActiveOfflineAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(modelId, "0.0.0");
        try
        {
            var pointerPath = Path.Combine(ModelRoot(modelId), ActiveFileName);
            if (!File.Exists(pointerPath))
            {
                return Fail(ModelDeliveryFailure.VersionNotInstalled);
            }

            var pointer = JsonSerializer.Deserialize<ActiveModelPointer>(
                await File.ReadAllBytesAsync(pointerPath, cancellationToken).ConfigureAwait(false),
                JsonOptions);
            if (pointer is null ||
                pointer.SchemaVersion != 1 ||
                !string.Equals(pointer.ModelId, modelId, StringComparison.Ordinal) ||
                !ModelManifestVerifier.IsSemanticVersion(pointer.Version) ||
                !ModelManifestVerifier.IsManifestDigest(pointer.ManifestDigest))
            {
                return Fail(ModelDeliveryFailure.IntegrityMismatch);
            }

            var directory = FinalDirectory(pointer.ModelId, pointer.Version, pointer.ManifestDigest);
            var installed = await VerifyInstalledDirectoryAsync(directory, cancellationToken)
                .ConfigureAwait(false);
            return installed is null ||
                !string.Equals(installed.ManifestDigest, pointer.ManifestDigest, StringComparison.Ordinal)
                    ? Fail(ModelDeliveryFailure.IntegrityMismatch)
                    : new ModelDeliveryResult(true, Installed: installed);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (JsonException)
        {
            return Fail(ModelDeliveryFailure.IntegrityMismatch);
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(ModelDeliveryFailure.StorageUnavailable);
        }
        catch (IOException)
        {
            return Fail(ModelDeliveryFailure.StorageUnavailable);
        }
    }

    public async Task<IReadOnlyList<InstalledModelVersion>> ListInstalledAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        if (modelId is not null)
        {
            ValidateIdentity(modelId, "0.0.0");
        }

        var installed = new List<InstalledModelVersion>();
        try
        {
            if (!Directory.Exists(_rootDirectory))
            {
                return installed;
            }

            var modelRoots = modelId is null
                ? Directory.GetDirectories(_rootDirectory)
                    .Where(path => ModelManifestVerifier.IsSafeModelId(Path.GetFileName(path)))
                    .ToArray()
                : [ModelRoot(modelId)];
            foreach (var modelRoot in modelRoots)
            {
                var versionsRoot = Path.Combine(modelRoot, "versions");
                if (!Directory.Exists(versionsRoot))
                {
                    continue;
                }

                foreach (var versionDirectory in Directory.GetDirectories(versionsRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var digestDirectory in Directory.GetDirectories(versionDirectory))
                    {
                        var candidate = await VerifyInstalledDirectoryAsync(
                            digestDirectory,
                            cancellationToken).ConfigureAwait(false);
                        if (candidate is not null)
                        {
                            installed.Add(candidate);
                        }
                    }
                }
            }

            return installed
                .OrderBy(item => item.ModelId, StringComparer.Ordinal)
                .ThenBy(item => item.Version, StringComparer.Ordinal)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public async Task<ModelDeliveryResult> RemoveVersionAsync(
        string modelId,
        string version,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(modelId, version);
        var gate = StoreLocks.GetOrAdd(LockKey(modelId), _ => new SemaphoreSlim(1, 1));
        var acquired = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            await using var processLock = await AcquireProcessLockAsync(modelId, cancellationToken)
                .ConfigureAwait(false);
            var modelRoot = ModelRoot(modelId);
            var activePath = Path.Combine(modelRoot, ActiveFileName);
            if (File.Exists(activePath))
            {
                var pointer = JsonSerializer.Deserialize<ActiveModelPointer>(
                    await File.ReadAllBytesAsync(activePath, cancellationToken).ConfigureAwait(false),
                    JsonOptions);
                if (string.Equals(pointer?.Version, version, StringComparison.Ordinal))
                {
                    File.Delete(activePath);
                }
            }

            var versionRoot = Path.Combine(modelRoot, "versions", version);
            if (!Directory.Exists(versionRoot))
            {
                return Fail(ModelDeliveryFailure.VersionNotInstalled);
            }

            Directory.Delete(versionRoot, recursive: true);
            _observer.Observe(new(DateTimeOffset.UtcNow, ModelDeliveryEventCode.ModelRemoved));
            return new(true);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(ModelDeliveryFailure.StorageUnavailable);
        }
        catch (IOException)
        {
            return Fail(ModelDeliveryFailure.StorageUnavailable);
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
            }
        }
    }

    public async Task<ModelDeliveryResult> CleanupAsync(
        string modelId,
        int inactiveVersionsToKeep = 1,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(modelId, "0.0.0");
        ArgumentOutOfRangeException.ThrowIfNegative(inactiveVersionsToKeep);

        var gate = StoreLocks.GetOrAdd(LockKey(modelId), _ => new SemaphoreSlim(1, 1));
        var acquired = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            await using var processLock = await AcquireProcessLockAsync(modelId, cancellationToken)
                .ConfigureAwait(false);
            var modelRoot = ModelRoot(modelId);
            var stagingRoot = Path.Combine(modelRoot, ".staging");
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }

            string? activeVersion = null;
            var activePath = Path.Combine(modelRoot, ActiveFileName);
            if (File.Exists(activePath))
            {
                var pointer = JsonSerializer.Deserialize<ActiveModelPointer>(
                    await File.ReadAllBytesAsync(activePath, cancellationToken).ConfigureAwait(false),
                    JsonOptions);
                activeVersion = pointer?.Version;
            }

            var versionsRoot = Path.Combine(modelRoot, "versions");
            if (Directory.Exists(versionsRoot))
            {
                string? activeDigest = null;
                if (File.Exists(activePath))
                {
                    var pointer = JsonSerializer.Deserialize<ActiveModelPointer>(
                        await File.ReadAllBytesAsync(activePath, cancellationToken).ConfigureAwait(false),
                        JsonOptions);
                    activeDigest = pointer?.ManifestDigest;
                }

                var inactive = Directory.GetDirectories(versionsRoot)
                    .SelectMany(Directory.GetDirectories)
                    .Where(path =>
                        !string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), activeVersion, StringComparison.Ordinal) ||
                        !string.Equals(Path.GetFileName(path), activeDigest, StringComparison.Ordinal))
                    .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path))
                    .Skip(inactiveVersionsToKeep)
                    .ToArray();
                foreach (var path in inactive)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.Delete(path, recursive: true);
                }

                foreach (var versionDirectory in Directory.GetDirectories(versionsRoot))
                {
                    if (!Directory.EnumerateFileSystemEntries(versionDirectory).Any())
                    {
                        Directory.Delete(versionDirectory);
                    }
                }
            }

            _observer.Observe(new(DateTimeOffset.UtcNow, ModelDeliveryEventCode.CleanupCompleted));
            return new(true);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(ModelDeliveryFailure.StorageUnavailable);
        }
        catch (IOException)
        {
            return Fail(ModelDeliveryFailure.StorageUnavailable);
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
            }
        }
    }

    private async Task<ModelDeliveryResult> InstallUnderLockAsync(
        VerifiedModelManifest manifest,
        bool activate,
        CancellationToken cancellationToken)
    {
        var finalDirectory = FinalDirectory(
            manifest.Payload.ModelId,
            manifest.Payload.Version,
            manifest.ManifestDigest);
        if (Directory.Exists(finalDirectory))
        {
            var existing = await VerifyInstalledDirectoryAsync(finalDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (activate)
                {
                    await WriteActivePointerAsync(existing, cancellationToken).ConfigureAwait(false);
                }

                return new(true, Installed: existing);
            }

            Directory.Delete(finalDirectory, recursive: true);
        }

        var staging = StagingDirectory(manifest);
        Directory.CreateDirectory(staging);
        var requiredBytes = RemainingBytes(manifest, staging);
        var availableBytes = _diskSpaceProbe.GetAvailableBytes(_rootDirectory);
        if (!HasSufficientDisk(requiredBytes, availableBytes))
        {
            return Fail(ModelDeliveryFailure.InsufficientDisk, requiredBytes, availableBytes);
        }

        long completedBytes = 0;
        var totalBytes = manifest.Payload.Files.Sum(file => file.SizeBytes);
        foreach (var artifact in manifest.Payload.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var downloaded = await DownloadArtifactAsync(
                staging,
                artifact,
                completedBytes,
                totalBytes,
                cancellationToken).ConfigureAwait(false);
            if (!downloaded.Succeeded)
            {
                return downloaded;
            }

            completedBytes += artifact.SizeBytes;
        }

        return await AdmitAsync(manifest, staging, activate, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModelDeliveryResult> DownloadArtifactAsync(
        string staging,
        ModelArtifact artifact,
        long completedBeforeArtifact,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var finalPath = SafeCombine(staging, artifact.RelativePath);
        if (await MatchesAsync(finalPath, artifact, cancellationToken).ConfigureAwait(false))
        {
            return new(true);
        }

        if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
        }

        var partialPath = finalPath + ".partial";
        var resumePath = finalPath + ".resume.json";
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        if (artifact.IsSharded &&
            await TryDownloadPartsAsync(staging, artifact, completedBeforeArtifact, totalBytes, cancellationToken)
                .ConfigureAwait(false))
        {
            return new(true);
        }

        var integrityFailureObserved = false;
        var permanentSourceFailureObserved = false;

        foreach (var source in artifact.Sources)
        {
            for (var attempt = 1; attempt <= _options.MaximumAttemptsPerSource; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan? retryAfter = null;
                try
                {
                    var outcome = await DownloadAttemptAsync(
                        source,
                        artifact,
                        partialPath,
                        resumePath,
                        completedBeforeArtifact,
                        totalBytes,
                        cancellationToken).ConfigureAwait(false);
                    retryAfter = outcome.RetryAfter;
                    if (outcome.Result == DownloadAttemptResult.Complete)
                    {
                        if (!await MatchesAsync(partialPath, artifact, cancellationToken).ConfigureAwait(false))
                        {
                            File.Delete(partialPath);
                            DeleteIfExists(resumePath);
                            _observer.Observe(new(
                                DateTimeOffset.UtcNow,
                                ModelDeliveryEventCode.SourceFailed,
                                ModelDeliveryFailure.IntegrityMismatch));
                            integrityFailureObserved = true;
                            break;
                        }

                        File.Move(partialPath, finalPath, overwrite: true);
                        DeleteIfExists(resumePath);
                        _observer.Observe(new(
                            DateTimeOffset.UtcNow,
                            ModelDeliveryEventCode.ArtifactVerified,
                            CompletedBytes: completedBeforeArtifact + artifact.SizeBytes,
                            TotalBytes: totalBytes));
                        return new(true);
                    }

                    if (outcome.Result == DownloadAttemptResult.PermanentFailure)
                    {
                        permanentSourceFailureObserved = true;
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // A request inactivity timeout is transient.
                }
                catch (HttpRequestException)
                {
                    // A transport failure is transient within the bounded source budget.
                }

                _observer.Observe(new(
                    DateTimeOffset.UtcNow,
                    ModelDeliveryEventCode.SourceFailed,
                    ModelDeliveryFailure.NetworkUnavailable));
                if (attempt < _options.MaximumAttemptsPerSource)
                {
                    await Task.Delay(
                        retryAfter ?? _options.DelayForAttempt(attempt),
                        cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        return Fail(integrityFailureObserved
            ? ModelDeliveryFailure.IntegrityMismatch
            : permanentSourceFailureObserved
                ? ModelDeliveryFailure.SourceRejected
                : ModelDeliveryFailure.NetworkUnavailable);
    }

    /// <summary>
    /// Fetches every part of a sharded artefact, reassembles them in order, and verifies the whole.
    /// </summary>
    /// <remarks>
    /// Each part rides the ordinary artefact path - the same Range and If-Range resume, the same
    /// per-source budget - under a synthetic name, so a part behaves like a small file. A false
    /// return means "get it the whole-file way instead": the shard layer exists to make delivery
    /// faster, and it must never make it fail where the whole-file sources would have succeeded.
    /// Anything left behind is removed before the fallback runs so a stale concatenation can never
    /// be mistaken for a resumable whole-file download.
    /// </remarks>
    private async Task<bool> TryDownloadPartsAsync(
        string staging,
        ModelArtifact artifact,
        long completedBeforeArtifact,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var finalPath = SafeCombine(staging, artifact.RelativePath);
        var partialPath = finalPath + ".partial";
        var partPaths = new List<string>();
        long offset = 0;
        var complete = true;
        foreach (var (part, index) in artifact.Parts!.Select((part, index) => (part, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var synthetic = new ModelArtifact(
                $"{artifact.RelativePath}.part{index}",
                part.SizeBytes,
                part.Sha256,
                part.Sources);
            partPaths.Add(SafeCombine(staging, synthetic.RelativePath));
            var downloaded = await DownloadArtifactAsync(
                staging,
                synthetic,
                completedBeforeArtifact + offset,
                totalBytes,
                cancellationToken).ConfigureAwait(false);
            if (!downloaded.Succeeded)
            {
                complete = false;
                break;
            }

            offset += part.SizeBytes;
        }

        if (complete)
        {
            DeleteIfExists(partialPath);
            DeleteIfExists(finalPath + ".resume.json");
            await using (var output = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true))
            {
                foreach (var partPath in partPaths)
                {
                    await using var input = new FileStream(
                        partPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 128 * 1024,
                        useAsync: true);
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }

            if (await MatchesAsync(partialPath, artifact, cancellationToken).ConfigureAwait(false))
            {
                File.Move(partialPath, finalPath, overwrite: true);
                foreach (var partPath in partPaths)
                {
                    DeleteIfExists(partPath);
                }

                _observer.Observe(new(
                    DateTimeOffset.UtcNow,
                    ModelDeliveryEventCode.ArtifactVerified,
                    CompletedBytes: completedBeforeArtifact + artifact.SizeBytes,
                    TotalBytes: totalBytes));
                return true;
            }

            // THE PARTS EACH MATCHED AND THE WHOLE DID NOT, which means the manifest describes
            // slices of a different file. Nothing here can be trusted; the whole-file path decides.
            _observer.Observe(new(
                DateTimeOffset.UtcNow,
                ModelDeliveryEventCode.SourceFailed,
                ModelDeliveryFailure.IntegrityMismatch));
        }

        DeleteIfExists(partialPath);
        foreach (var partPath in partPaths)
        {
            DeleteIfExists(partPath);
            DeleteIfExists(partPath + ".partial");
            DeleteIfExists(partPath + ".resume.json");
        }

        return false;
    }

    private async Task<DownloadAttemptOutcome> DownloadAttemptAsync(
        Uri source,
        ModelArtifact artifact,
        string partialPath,
        string resumePath,
        long completedBeforeArtifact,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (offset > artifact.SizeBytes)
        {
            File.Delete(partialPath);
            DeleteIfExists(resumePath);
            offset = 0;
        }

        ResumeMetadata? resume = null;
        if (offset > 0 && File.Exists(resumePath))
        {
            try
            {
                resume = JsonSerializer.Deserialize<ResumeMetadata>(
                    await File.ReadAllBytesAsync(resumePath, cancellationToken).ConfigureAwait(false),
                    JsonOptions);
            }
            catch (JsonException)
            {
                resume = null;
            }
        }

        if (offset > 0 &&
            (resume is null || !string.Equals(resume.Source, source.AbsoluteUri, StringComparison.Ordinal)))
        {
            File.Delete(partialPath);
            DeleteIfExists(resumePath);
            offset = 0;
            resume = null;
        }

        if (offset > 0 &&
            string.IsNullOrWhiteSpace(resume?.ETag) &&
            resume?.LastModified is null)
        {
            File.Delete(partialPath);
            DeleteIfExists(resumePath);
            offset = 0;
            resume = null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            if (!string.IsNullOrWhiteSpace(resume?.ETag))
            {
                request.Headers.TryAddWithoutValidation("If-Range", resume.ETag);
            }
            else if (resume?.LastModified is not null)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(resume.LastModified.Value);
            }

            _observer.Observe(new(
                DateTimeOffset.UtcNow,
                ModelDeliveryEventCode.DownloadResumed,
                CompletedBytes: completedBeforeArtifact + offset,
                TotalBytes: totalBytes));
        }
        else
        {
            _observer.Observe(new(
                DateTimeOffset.UtcNow,
                ModelDeliveryEventCode.DownloadStarted,
                CompletedBytes: completedBeforeArtifact,
                TotalBytes: totalBytes));
        }

        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(_options.EffectiveRequestTimeout);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            requestTimeout.Token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return new(offset == artifact.SizeBytes
                ? DownloadAttemptResult.Complete
                : DownloadAttemptResult.PermanentFailure);
        }

        if (IsTransientStatus(response.StatusCode))
        {
            return new(DownloadAttemptResult.TransientFailure, RetryAfter(response));
        }

        if (!response.IsSuccessStatusCode)
        {
            return new(DownloadAttemptResult.PermanentFailure);
        }

        var append = response.StatusCode == HttpStatusCode.PartialContent && offset > 0;
        if (append && response.Content.Headers.ContentRange?.From != offset)
        {
            return new(DownloadAttemptResult.PermanentFailure);
        }

        if (!append)
        {
            offset = 0;
        }

        var metadata = new ResumeMetadata(
            source.AbsoluteUri,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified);
        await WriteAtomicAsync(
            resumePath,
            JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new FileStream(
            partialPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        var buffer = new byte[128 * 1024];
        long written = offset;
        while (true)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(_options.EffectiveRequestTimeout);
            var read = await input.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            written = checked(written + read);
            if (written > artifact.SizeBytes)
            {
                return new(DownloadAttemptResult.PermanentFailure);
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            _observer.Observe(new(
                DateTimeOffset.UtcNow,
                ModelDeliveryEventCode.DownloadStarted,
                CompletedBytes: completedBeforeArtifact + written,
                TotalBytes: totalBytes));
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new(written == artifact.SizeBytes
            ? DownloadAttemptResult.Complete
            : DownloadAttemptResult.TransientFailure);
    }

    private async Task<ModelDeliveryResult> AdmitAsync(
        VerifiedModelManifest manifest,
        string staging,
        bool activate,
        CancellationToken cancellationToken)
    {
        foreach (var artifact in manifest.Payload.Files)
        {
            if (!await MatchesAsync(SafeCombine(staging, artifact.RelativePath), artifact, cancellationToken)
                .ConfigureAwait(false))
            {
                return Fail(ModelDeliveryFailure.IntegrityMismatch);
            }
        }

        var expectedFiles = manifest.Payload.Files
            .Select(file => NormalizeRelativePath(file.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unexpected = Directory.GetFiles(staging, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(staging, path)))
            .Where(path => !expectedFiles.Contains(path))
            .ToArray();
        foreach (var path in unexpected)
        {
            File.Delete(SafeCombine(staging, path));
        }

        await WriteAtomicAsync(
            Path.Combine(staging, ManifestFileName),
            manifest.EnvelopeBytes,
            cancellationToken).ConfigureAwait(false);
        var licenseText = $"{manifest.Payload.License.Name}{Environment.NewLine}" +
            $"{manifest.Payload.License.Url}{Environment.NewLine}{Environment.NewLine}" +
            manifest.Payload.License.Notice.Trim() + Environment.NewLine;
        await WriteAtomicAsync(
            Path.Combine(staging, LicenseFileName),
            Encoding.UTF8.GetBytes(licenseText),
            cancellationToken).ConfigureAwait(false);

        var finalDirectory = FinalDirectory(
            manifest.Payload.ModelId,
            manifest.Payload.Version,
            manifest.ManifestDigest);
        Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
        if (!Directory.Exists(finalDirectory))
        {
            Directory.Move(staging, finalDirectory);
        }
        else
        {
            Directory.Delete(staging, recursive: true);
        }

        var installed = new InstalledModelVersion(
            manifest.Payload.ModelId,
            manifest.Payload.Version,
            manifest.ManifestDigest,
            finalDirectory,
            manifest.Payload.License);
        if (activate)
        {
            await WriteActivePointerAsync(installed, cancellationToken).ConfigureAwait(false);
        }

        _observer.Observe(new(DateTimeOffset.UtcNow, ModelDeliveryEventCode.ModelAdmitted));
        return new(true, Installed: installed);
    }

    private async Task<InstalledModelVersion?> VerifyInstalledDirectoryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var envelope = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var verification = _verifier.VerifyStored(envelope);
        if (!verification.Succeeded)
        {
            return null;
        }

        var manifest = verification.Manifest!;
        var expectedDirectory = FinalDirectory(
            manifest.Payload.ModelId,
            manifest.Payload.Version,
            manifest.ManifestDigest);
        if (!string.Equals(
                Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(expectedDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var artifact in manifest.Payload.Files)
        {
            if (!await MatchesAsync(SafeCombine(directory, artifact.RelativePath), artifact, cancellationToken)
                .ConfigureAwait(false))
            {
                return null;
            }
        }

        var expectedFiles = manifest.Payload.Files
            .Select(file => NormalizeRelativePath(file.RelativePath))
            .Append(ManifestFileName)
            .Append(LicenseFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(directory, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualFiles.SetEquals(expectedFiles))
        {
            return null;
        }

        var expectedLicenseText = $"{manifest.Payload.License.Name}{Environment.NewLine}" +
            $"{manifest.Payload.License.Url}{Environment.NewLine}{Environment.NewLine}" +
            manifest.Payload.License.Notice.Trim() + Environment.NewLine;
        if (!string.Equals(
                await File.ReadAllTextAsync(Path.Combine(directory, LicenseFileName), cancellationToken)
                    .ConfigureAwait(false),
                expectedLicenseText,
                StringComparison.Ordinal))
        {
            return null;
        }

        return new InstalledModelVersion(
            manifest.Payload.ModelId,
            manifest.Payload.Version,
            manifest.ManifestDigest,
            directory,
            manifest.Payload.License);
    }

    private async Task WriteActivePointerAsync(
        InstalledModelVersion installed,
        CancellationToken cancellationToken)
    {
        var pointer = new ActiveModelPointer(
            1,
            installed.ModelId,
            installed.Version,
            installed.ManifestDigest);
        await WriteAtomicAsync(
            Path.Combine(ModelRoot(installed.ModelId), ActiveFileName),
            JsonSerializer.SerializeToUtf8Bytes(pointer, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        _observer.Observe(new(DateTimeOffset.UtcNow, ModelDeliveryEventCode.ModelActivated));
    }

    private static async Task<bool> MatchesAsync(
        string path,
        ModelArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != artifact.SizeBytes)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexString(hash),
            artifact.Sha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAtomicAsync(
        string destination,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes.ToArray(), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            DeleteIfExists(temporary);
        }
    }

    private async Task<FileStream> AcquireProcessLockAsync(
        string modelId,
        CancellationToken cancellationToken)
    {
        var modelRoot = ModelRoot(modelId);
        Directory.CreateDirectory(modelRoot);
        var lockPath = Path.Combine(modelRoot, ".delivery.lock");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    useAsync: true);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private string ModelRoot(string modelId) => SafeCombine(_rootDirectory, modelId);

    private string StagingDirectory(VerifiedModelManifest manifest) => SafeCombine(
        ModelRoot(manifest.Payload.ModelId),
        $".staging/{manifest.Payload.Version}-{manifest.ManifestDigest}");

    private string FinalDirectory(string modelId, string version, string digest) =>
        SafeCombine(ModelRoot(modelId), $"versions/{version}/{digest}");

    private string[] VersionDirectories(string modelId, string version)
    {
        var root = Path.Combine(ModelRoot(modelId), "versions", version);
        return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
    }

    // A SHARDED FILE NEEDS ROOM FOR ITS PARTS AND ITS WHOLE AT ONCE, for the moment between the
    // last part arriving and the reassembled file being verified. Counted here so the disk check
    // refuses up front rather than the concatenation failing at the end of a long download.
    private static long RemainingBytes(VerifiedModelManifest manifest, string staging) =>
        manifest.Payload.Files.Sum(file =>
            Math.Max(0, file.SizeBytes - Math.Min(file.SizeBytes, PartialOrCompleteLength(staging, file))) +
            (file.IsSharded && !File.Exists(SafeCombine(staging, file.RelativePath)) ? file.SizeBytes : 0));

    private static long PartialOrCompleteLength(string staging, ModelArtifact artifact)
    {
        var final = SafeCombine(staging, artifact.RelativePath);
        if (File.Exists(final))
        {
            return new FileInfo(final).Length;
        }

        var partial = final + ".partial";
        return File.Exists(partial) ? new FileInfo(partial).Length : 0;
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("A model path escaped its store root.");
        }

        return combined;
    }

    private string LockKey(string modelId) =>
        Path.GetFullPath(Path.Combine(_rootDirectory, modelId));

    private static void ValidateIdentity(string modelId, string version)
    {
        if (!ModelManifestVerifier.IsSafeModelId(modelId) ||
            !ModelManifestVerifier.IsSemanticVersion(version))
        {
            throw new ArgumentException("Model identity is invalid.");
        }
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/');

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private static void RemoveEmptyLegacyDirectories(
        string modelRoot,
        IReadOnlyList<ModelArtifact> artifacts)
    {
        foreach (var directory in artifacts
            .Select(file => Path.GetDirectoryName(SafeCombine(modelRoot, file.RelativePath)))
            .Where(path => path is not null && !string.Equals(path, modelRoot, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path!.Length))
        {
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode is 425 or 500 or 502 or 503 or 504 or 522 or 524;

    private bool HasSufficientDisk(long requiredBytes, long availableBytes) =>
        requiredBytes <= long.MaxValue - _options.DiskReserveBytes &&
        availableBytes >= requiredBytes + _options.DiskReserveBytes;

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ??
            (header?.Date is null ? null : header.Date.Value - DateTimeOffset.UtcNow);
        if (delay is null)
        {
            return null;
        }

        return TimeSpan.FromMilliseconds(Math.Clamp(
            delay.Value.TotalMilliseconds,
            0,
            TimeSpan.FromSeconds(10).TotalMilliseconds));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private ModelDeliveryResult Fail(
        ModelDeliveryFailure failure,
        long requiredBytes = 0,
        long availableBytes = 0)
    {
        _observer.Observe(new(
            DateTimeOffset.UtcNow,
            ModelDeliveryEventCode.OperationFailed,
            failure));
        return new(false, failure, RequiredBytes: requiredBytes, AvailableBytes: availableBytes);
    }

    private ModelDeliveryResult Cancelled()
    {
        _observer.Observe(new(
            DateTimeOffset.UtcNow,
            ModelDeliveryEventCode.OperationCancelled,
            ModelDeliveryFailure.Cancelled));
        return new(false, ModelDeliveryFailure.Cancelled);
    }

    private static ModelDeliveryFailure MapVerificationFailure(ManifestVerificationStatus status) => status switch
    {
        ManifestVerificationStatus.UntrustedKey or ManifestVerificationStatus.InvalidSignature =>
            ModelDeliveryFailure.UntrustedManifest,
        ManifestVerificationStatus.UnsupportedSchema => ModelDeliveryFailure.UnsupportedManifest,
        _ => ModelDeliveryFailure.InvalidManifest,
    };

    private sealed record ResumeMetadata(string Source, string? ETag, DateTimeOffset? LastModified);

    private sealed record ActiveModelPointer(
        int SchemaVersion,
        string ModelId,
        string Version,
        string ManifestDigest);

    private enum DownloadAttemptResult
    {
        Complete,
        TransientFailure,
        PermanentFailure,
    }

    private readonly record struct DownloadAttemptOutcome(
        DownloadAttemptResult Result,
        TimeSpan? RetryAfter = null);
}
