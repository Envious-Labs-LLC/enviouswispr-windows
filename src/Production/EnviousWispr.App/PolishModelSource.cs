using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using EnviousWispr.Presentation;

namespace EnviousWispr.App;

/// <summary>The app's answer to the Polish page's model questions: the cloud catalog and the local Ollama client behind one port.</summary>
/// <remarks>
/// THE WIRING IS THE APP'S, THE DECISIONS ARE THE PRESENTER'S. The catalog sends the stored credential
/// to a provider's model listing and nothing else; the Ollama client is built per call against
/// whatever endpoint the page holds, under the same loopback policy the polish provider uses, and
/// let go of when the listing is done.
/// </remarks>
internal sealed class PolishModelSource : IPolishModelSource, IDisposable
{
    private readonly CloudPolishModelCatalog _catalog;

    public PolishModelSource(IApiKeyStore apiKeyStore)
    {
        _catalog = new CloudPolishModelCatalog(apiKeyStore);
    }

    public string? RecommendedModel(PolishProvider provider) =>
        ProviderSettingsPresenter.IsCloudProvider(provider) ? CloudPolishOptions.DefaultModel(provider) : null;

    public bool ModelIdBelongsTo(string? modelId, PolishProvider provider) =>
        ProviderSettingsPresenter.IsCloudProvider(provider) && CloudPolishOptions.ModelIdLooksLikeProvider(modelId, provider);

    public async Task<PolishModelDiscovery> DiscoverAsync(
        PolishProvider provider,
        string? ollamaEndpoint,
        CancellationToken cancellationToken)
    {
        if (provider == PolishProvider.Ollama)
        {
            await using var client = new OllamaApiClient(ollamaEndpoint);
            var discovery = await client.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            return new PolishModelDiscovery(
                discovery.Health == OllamaHealth.Ready
                    ? PolishModelDiscoveryStatus.Ready
                    : PolishModelDiscoveryStatus.OllamaNotReady,
                discovery.LocalModels.Select(model => model.Id).ToArray());
        }

        var cloud = await _catalog.DiscoverAsync(provider, cancellationToken).ConfigureAwait(false);
        return new PolishModelDiscovery(
            cloud.Status switch
            {
                CloudModelCatalogStatus.Ready => PolishModelDiscoveryStatus.Ready,
                CloudModelCatalogStatus.MissingCredential => PolishModelDiscoveryStatus.MissingCredential,
                CloudModelCatalogStatus.CredentialUnavailable => PolishModelDiscoveryStatus.CredentialUnavailable,
                CloudModelCatalogStatus.KeyRejected => PolishModelDiscoveryStatus.KeyRejected,
                CloudModelCatalogStatus.ProviderUnavailable => PolishModelDiscoveryStatus.ProviderUnavailable,
                CloudModelCatalogStatus.InvalidResponse => PolishModelDiscoveryStatus.InvalidResponse,
                _ => throw new ArgumentOutOfRangeException(nameof(provider), cloud.Status, null),
            },
            cloud.ModelIds);
    }

    public void Dispose() => _catalog.Dispose();
}
