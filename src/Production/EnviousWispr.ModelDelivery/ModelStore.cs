using System.Collections.Concurrent;
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
    private readonly ArtifactDownloader _downloader;
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
        _verifier = verifier;
        _appVersion = appVersion;
        _diskSpaceProbe = diskSpaceProbe ?? new WindowsDiskSpaceProbe();
        _observer = observer ?? NullModelDeliveryObserver.Instance;
        _options = options ?? new ModelDeliveryOptions();
        if (_options.DiskReserveBytes < 0 || _options.MaximumAttemptsPerSource is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        // THE TRANSFER LIVES BESIDE THE STORE, NOT IN IT. The transport makes one attempt against one
        // source; the downloader decides sources, attempts, delays and what a failure means for the
        // artifact; the store keeps the lock, the staging admission, activation, migration and cleanup.
        _downloader = new ArtifactDownloader(
            new ArtifactDownloadTransport(httpClient, _options.EffectiveRequestTimeout, _observer),
            _options,
            _observer);
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
                var legacyPath = DeliveryFiles.SafeCombine(modelRoot, artifact.RelativePath);
                if (!await DeliveryFiles.MatchesAsync(legacyPath, artifact, cancellationToken).ConfigureAwait(false))
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
                var source = DeliveryFiles.SafeCombine(modelRoot, artifact.RelativePath);
                var destination = DeliveryFiles.SafeCombine(staging, artifact.RelativePath);
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
                File.Delete(DeliveryFiles.SafeCombine(modelRoot, artifact.RelativePath));
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
            var downloaded = await _downloader.DownloadArtifactAsync(
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

    private async Task<ModelDeliveryResult> AdmitAsync(
        VerifiedModelManifest manifest,
        string staging,
        bool activate,
        CancellationToken cancellationToken)
    {
        foreach (var artifact in manifest.Payload.Files)
        {
            if (!await DeliveryFiles.MatchesAsync(DeliveryFiles.SafeCombine(staging, artifact.RelativePath), artifact, cancellationToken)
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
            File.Delete(DeliveryFiles.SafeCombine(staging, path));
        }

        await DeliveryFiles.WriteAtomicAsync(
            Path.Combine(staging, ManifestFileName),
            manifest.EnvelopeBytes,
            cancellationToken).ConfigureAwait(false);
        var licenseText = $"{manifest.Payload.License.Name}{Environment.NewLine}" +
            $"{manifest.Payload.License.Url}{Environment.NewLine}{Environment.NewLine}" +
            manifest.Payload.License.Notice.Trim() + Environment.NewLine;
        await DeliveryFiles.WriteAtomicAsync(
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
            if (!await DeliveryFiles.MatchesAsync(DeliveryFiles.SafeCombine(directory, artifact.RelativePath), artifact, cancellationToken)
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
        await DeliveryFiles.WriteAtomicAsync(
            Path.Combine(ModelRoot(installed.ModelId), ActiveFileName),
            JsonSerializer.SerializeToUtf8Bytes(pointer, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        _observer.Observe(new(DateTimeOffset.UtcNow, ModelDeliveryEventCode.ModelActivated));
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

    private string ModelRoot(string modelId) => DeliveryFiles.SafeCombine(_rootDirectory, modelId);

    private string StagingDirectory(VerifiedModelManifest manifest) => DeliveryFiles.SafeCombine(
        ModelRoot(manifest.Payload.ModelId),
        $".staging/{manifest.Payload.Version}-{manifest.ManifestDigest}");

    private string FinalDirectory(string modelId, string version, string digest) =>
        DeliveryFiles.SafeCombine(ModelRoot(modelId), $"versions/{version}/{digest}");

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
            (file.IsSharded && !File.Exists(DeliveryFiles.SafeCombine(staging, file.RelativePath)) ? file.SizeBytes : 0));

    private static long PartialOrCompleteLength(string staging, ModelArtifact artifact)
    {
        var final = DeliveryFiles.SafeCombine(staging, artifact.RelativePath);
        if (File.Exists(final))
        {
            return new FileInfo(final).Length;
        }

        var partial = final + ".partial";
        return File.Exists(partial) ? new FileInfo(partial).Length : 0;
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
            .Select(file => Path.GetDirectoryName(DeliveryFiles.SafeCombine(modelRoot, file.RelativePath)))
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

    private bool HasSufficientDisk(long requiredBytes, long availableBytes) =>
        requiredBytes <= long.MaxValue - _options.DiskReserveBytes &&
        availableBytes >= requiredBytes + _options.DiskReserveBytes;

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

    private sealed record ActiveModelPointer(
        int SchemaVersion,
        string ModelId,
        string Version,
        string ManifestDigest);

}
