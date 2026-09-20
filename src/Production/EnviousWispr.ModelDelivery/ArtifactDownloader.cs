namespace EnviousWispr.ModelDelivery;

/// <summary>Gets one artifact into the staging directory: which sources, how many times, what a failure means.</summary>
/// <remarks>
/// THE POLICY ABOVE THE TRANSPORT AND BELOW THE STORE. The transport makes one attempt against one
/// source; this decides the order of sources, the attempts each is allowed, the delay between them
/// (the source's own Retry-After when it gave one), what an integrity mismatch, a refusal and a
/// transport failure each mean for the artifact, and that a sharded artifact is tried part by part
/// first and the whole-file way second. The store above decides admission, activation and cleanup,
/// and rechecks admission after every artifact has landed; nothing here touches the active version.
///
/// A FAILED INTEGRITY CHECK LEAVES NOTHING BEHIND THAT COULD BE MISTAKEN FOR PROGRESS. A complete
/// partial that does not hash is deleted with its resume record; a shard assembly that does not
/// hash is deleted with every part, so a stale concatenation can never be resumed as a whole file.
/// </remarks>
public sealed class ArtifactDownloader
{
    private readonly ArtifactDownloadTransport _transport;
    private readonly IModelDeliveryObserver _observer;
    private readonly ModelDeliveryOptions _options;

    public ArtifactDownloader(ArtifactDownloadTransport transport, ModelDeliveryOptions options, IModelDeliveryObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        _transport = transport;
        _options = options;
        _observer = observer ?? NullModelDeliveryObserver.Instance;
    }

    /// <summary>Gets the artifact into <paramref name="staging"/>, or says why not.</summary>
    /// <param name="completedBeforeArtifact">Bytes of earlier artifacts, for the progress events.</param>
    /// <param name="totalBytes">The whole installation's size, for the progress events.</param>
    public async Task<ModelDeliveryResult> DownloadArtifactAsync(
        string staging,
        ModelArtifact artifact,
        long completedBeforeArtifact,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var finalPath = DeliveryFiles.SafeCombine(staging, artifact.RelativePath);
        if (await DeliveryFiles.MatchesAsync(finalPath, artifact, cancellationToken).ConfigureAwait(false))
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
                    var outcome = await _transport.DownloadAttemptAsync(
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
                        if (!await DeliveryFiles.MatchesAsync(partialPath, artifact, cancellationToken).ConfigureAwait(false))
                        {
                            File.Delete(partialPath);
                            DeliveryFiles.DeleteIfExists(resumePath);
                            _observer.Observe(new(
                                DateTimeOffset.UtcNow,
                                ModelDeliveryEventCode.SourceFailed,
                                ModelDeliveryFailure.IntegrityMismatch));
                            integrityFailureObserved = true;
                            break;
                        }

                        File.Move(partialPath, finalPath, overwrite: true);
                        DeliveryFiles.DeleteIfExists(resumePath);
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
        var finalPath = DeliveryFiles.SafeCombine(staging, artifact.RelativePath);
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
            partPaths.Add(DeliveryFiles.SafeCombine(staging, synthetic.RelativePath));
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
            DeliveryFiles.DeleteIfExists(partialPath);
            DeliveryFiles.DeleteIfExists(finalPath + ".resume.json");
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

            if (await DeliveryFiles.MatchesAsync(partialPath, artifact, cancellationToken).ConfigureAwait(false))
            {
                File.Move(partialPath, finalPath, overwrite: true);
                foreach (var partPath in partPaths)
                {
                    DeliveryFiles.DeleteIfExists(partPath);
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

        DeliveryFiles.DeleteIfExists(partialPath);
        foreach (var partPath in partPaths)
        {
            DeliveryFiles.DeleteIfExists(partPath);
            DeliveryFiles.DeleteIfExists(partPath + ".partial");
            DeliveryFiles.DeleteIfExists(partPath + ".resume.json");
        }

        return false;
    }

    private ModelDeliveryResult Fail(ModelDeliveryFailure failure)
    {
        _observer.Observe(new(
            DateTimeOffset.UtcNow,
            ModelDeliveryEventCode.OperationFailed,
            failure));
        return new(false, failure);
    }
}
