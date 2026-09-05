namespace EnviousWispr.ModelDelivery;

/// <summary>
/// Takes a model id to an installed, activated model: manifest from the bundle, bytes from the
/// sources it pins, admission through the store.
/// </summary>
/// <remarks>
/// THIS IS THE PIECE THAT WAS MISSING. <see cref="ModelStore"/> was complete and tested and
/// constructed nowhere in the application, so a fresh install could detect that its speech model
/// was absent and had no way to obtain one. Ref: #92.
///
/// It is deliberately thin: the store already owns verification, resumption, disk checks and
/// activation. What this adds is the one step the store cannot take - finding the manifest for a
/// model id - and it takes that step from a function so a test can hand it any manifest it likes.
/// The application hands it <see cref="BundledModelManifests.Load"/>; nothing here fetches a
/// manifest over the network, matching the macOS contract's invariant 4.
/// </remarks>
public sealed class ModelProvisioner
{
    private readonly ModelStore _store;
    private readonly Func<string, ManifestVerificationResult> _manifestFor;

    public ModelProvisioner(ModelStore store, Func<string, ManifestVerificationResult> manifestFor)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifestFor);
        _store = store;
        _manifestFor = manifestFor;
    }

    public async Task<ModelDeliveryResult> ProvisionAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var verification = _manifestFor(modelId);
        if (!verification.Succeeded)
        {
            return new(false, FailureFor(verification.Status));
        }

        // A MANIFEST FOR A DIFFERENT MODEL IS REFUSED EVEN THOUGH IT VERIFIES. The digest proves the
        // document is intact, not that it is the one that was asked for; a manifest saved under the
        // wrong name would otherwise install the wrong model and the app would report success.
        if (!string.Equals(verification.Manifest!.Payload.ModelId, modelId, StringComparison.Ordinal))
        {
            return new(false, ModelDeliveryFailure.InvalidManifest);
        }

        return await _store.InstallAsync(verification.Manifest, activate: true, cancellationToken)
            .ConfigureAwait(false);
    }

    public static ModelDeliveryFailure FailureFor(ManifestVerificationStatus status) => status switch
    {
        ManifestVerificationStatus.Verified => ModelDeliveryFailure.None,
        ManifestVerificationStatus.Unreachable => ModelDeliveryFailure.NetworkUnavailable,
        ManifestVerificationStatus.UntrustedKey or ManifestVerificationStatus.InvalidSignature =>
            ModelDeliveryFailure.UntrustedManifest,
        ManifestVerificationStatus.UnsupportedSchema => ModelDeliveryFailure.UnsupportedManifest,
        _ => ModelDeliveryFailure.InvalidManifest,
    };
}
