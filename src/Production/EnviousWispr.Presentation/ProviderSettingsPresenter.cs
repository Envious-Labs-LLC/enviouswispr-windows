using System.ComponentModel;
using System.Security;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>How a request to store a provider key went.</summary>
public enum ApiKeySaveOutcome
{
    /// <summary>The chosen provider runs on this PC or is off; it has no key to store.</summary>
    NotACloudProvider,

    /// <summary>Nothing was typed; nothing was stored.</summary>
    EmptyKey,

    /// <summary>Stored in Windows Credential Manager.</summary>
    Saved,

    /// <summary>Windows Credential Manager refused; nothing was written anywhere.</summary>
    StorageUnavailable,
}

/// <summary>Whether a key removal should be offered at all, before the window asks for confirmation.</summary>
public enum ApiKeyRemovalCheck
{
    NotACloudProvider,
    NothingStored,
    Confirm,
}

/// <summary>How a confirmed key removal went.</summary>
public enum ApiKeyRemoveOutcome
{
    Removed,
    StorageUnavailable,
}

/// <summary>What a model discovery came back with, in provider-neutral terms.</summary>
public enum PolishModelDiscoveryStatus
{
    Ready,
    OllamaNotReady,
    MissingCredential,
    CredentialUnavailable,
    KeyRejected,
    ProviderUnavailable,
    InvalidResponse,
}

public sealed record PolishModelDiscovery(PolishModelDiscoveryStatus Status, IReadOnlyList<string> ModelIds);

/// <summary>Where the presenter's model knowledge comes from; the app adapts the concrete clients behind it.</summary>
/// <remarks>
/// A PORT, SO THE DECISIONS CAN BE TESTED WITHOUT AN HTTP CLIENT. The recommended model per provider
/// and the "does this id look like this provider's" rule live beside the clients that know the
/// providers; the presenter asks rather than copies, so there is one answer.
/// </remarks>
public interface IPolishModelSource
{
    /// <summary>The recommended model for a provider that has one, or null.</summary>
    string? RecommendedModel(PolishProvider provider);

    /// <summary>Whether a typed model id is plausibly one of this provider's.</summary>
    bool ModelIdBelongsTo(string? modelId, PolishProvider provider);

    /// <summary>Lists what the provider offers. Sends the stored credential only, never a transcript.</summary>
    Task<PolishModelDiscovery> DiscoverAsync(PolishProvider provider, string? ollamaEndpoint, CancellationToken cancellationToken);
}

/// <summary>What the model controls should show after a refresh.</summary>
/// <param name="Models">The picker's choices.</param>
/// <param name="SelectedIndex">Which of them matches the model id in force, or -1.</param>
/// <param name="ModelToApply">A model id to write into the free-text field, or null to leave it as typed.</param>
/// <param name="Discovery">What discovery reported, or null when the provider has nothing to discover.</param>
public sealed record PolishModelChoices(
    PolishProvider Provider,
    IReadOnlyList<string> Models,
    int SelectedIndex,
    string? ModelToApply,
    PolishModelDiscovery? Discovery);

/// <summary>What a provider offers, as listed for one refresh.</summary>
/// <param name="Ticket">Which refresh asked; the page checks it is still the latest right before applying.</param>
/// <param name="Discovery">What discovery reported, or null when the provider has nothing to discover.</param>
public sealed record PolishModelListing(
    int Ticket,
    PolishProvider Provider,
    IReadOnlyList<string> Models,
    PolishModelDiscovery? Discovery);

/// <summary>The decisions the Polish page makes about providers, their keys and their models, without the page.</summary>
/// <remarks>
/// THE SECRET NEVER LEAVES THE STORE THROUGH HERE. The presenter stores and deletes; it reads only a
/// status. Discovery sends the stored credential to the provider's model listing and nothing else -
/// no transcript, no generation request - which is the consent line product invariant 4 draws.
///
/// A LATE ANSWER IS THROWN AWAY. Discovery takes as long as the provider takes, and a person can
/// change provider twice in that time; every refresh carries a ticket, a listing overtaken by the
/// time it returns is answered with null, and the page asks <see cref="IsCurrent"/> once more on its
/// own thread right before it applies one, so the page shows the provider chosen last.
///
/// THE FIELD IS READ WHEN THE LISTING IS APPLIED, NOT WHEN IT WAS ASKED FOR. Listing and choosing are
/// two steps for that reason: a model typed while the listing was out is honoured.
///
/// A DISCOVERY RUNS INSIDE THE PRESENTATION'S GATE. The exit disposes the model source once the
/// presentation has closed; a discovery still out at that moment would lose its client mid-call, so
/// each takes a lease and runs under the closing token, and one refused or stopped by the close
/// answers null, as an overtaken one does.
/// </remarks>
public sealed class ProviderSettingsPresenter
{
    private readonly IApiKeyStore _keys;
    private readonly IPolishModelSource _models;
    private readonly PresentationAdmission _admission;
    private int _discoveryVersion;

    /// <param name="admission">The presentation's gate; one of this presenter's own, never closed, when it stands alone.</param>
    public ProviderSettingsPresenter(IApiKeyStore keys, IPolishModelSource models, PresentationAdmission? admission = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(models);
        _keys = keys;
        _models = models;
        _admission = admission ?? new PresentationAdmission();
    }

    public static bool IsCloudProvider(PolishProvider provider) =>
        provider is PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini;

    /// <summary>Whether the provider takes a model id at all: local Ollama and the three cloud providers do.</summary>
    public static bool UsesAModel(PolishProvider provider) =>
        provider is PolishProvider.Ollama or PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini;

    /// <summary>Whether a key is stored for the provider, without revealing it.</summary>
    public ApiKeyReadStatus KeyStatus(PolishProvider provider) => _keys.GetStatus(provider);

    /// <summary>Stores a key for a cloud provider. The value is trimmed; blank is refused before the store is touched.</summary>
    public ApiKeySaveOutcome SaveKey(PolishProvider provider, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsCloudProvider(provider))
        {
            return ApiKeySaveOutcome.NotACloudProvider;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return ApiKeySaveOutcome.EmptyKey;
        }

        try
        {
            _keys.Store(provider, trimmed);
            return ApiKeySaveOutcome.Saved;
        }
        catch (Exception exception) when (IsCredentialStorageFailure(exception))
        {
            return ApiKeySaveOutcome.StorageUnavailable;
        }
    }

    /// <summary>Whether there is a key to remove; the window asks the person only when there is.</summary>
    public ApiKeyRemovalCheck CheckKeyRemoval(PolishProvider provider)
    {
        if (!IsCloudProvider(provider))
        {
            return ApiKeyRemovalCheck.NotACloudProvider;
        }

        return _keys.GetStatus(provider) == ApiKeyReadStatus.Missing
            ? ApiKeyRemovalCheck.NothingStored
            : ApiKeyRemovalCheck.Confirm;
    }

    /// <summary>Removes the stored key, after the window has confirmed.</summary>
    public ApiKeyRemoveOutcome RemoveKey(PolishProvider provider)
    {
        try
        {
            _keys.Delete(provider);
            return ApiKeyRemoveOutcome.Removed;
        }
        catch (Exception exception) when (IsCredentialStorageFailure(exception))
        {
            return ApiKeyRemoveOutcome.StorageUnavailable;
        }
    }

    /// <summary>Lists what the provider offers, discovering where the provider allows it.</summary>
    /// <returns>The listing, or null when a later refresh has overtaken this one by the time it returns, or the presentation is closing.</returns>
    public async Task<PolishModelListing?> ListModelsAsync(
        PolishProvider provider,
        string? ollamaEndpoint,
        CancellationToken cancellationToken = default)
    {
        if (!_admission.TryEnter(out var lease))
        {
            return null;
        }

        using (lease)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Closing);
            try
            {
                return await ListInsideAsync(provider, ollamaEndpoint, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    private async Task<PolishModelListing?> ListInsideAsync(PolishProvider provider, string? ollamaEndpoint, CancellationToken cancellationToken)
    {
        var ticket = Interlocked.Increment(ref _discoveryVersion);

        IReadOnlyList<string> models = provider switch
        {
            PolishProvider.EgOne => ["eg-1"],
            _ when IsCloudProvider(provider) => [_models.RecommendedModel(provider)!],
            _ => [],
        };

        PolishModelDiscovery? discovery = null;
        if (provider == PolishProvider.Ollama)
        {
            discovery = await _models.DiscoverAsync(provider, ollamaEndpoint, cancellationToken).ConfigureAwait(false);
            models = discovery.ModelIds;
        }
        else if (IsCloudProvider(provider))
        {
            discovery = await _models.DiscoverAsync(provider, null, cancellationToken).ConfigureAwait(false);
            // THE RECOMMENDATION STAYS WHEN THE ACCOUNT LISTS NOTHING, so the picker is never empty for
            // a provider that certainly has a model; a refused or failed listing keeps it too.
            if (discovery.Status == PolishModelDiscoveryStatus.Ready && discovery.ModelIds.Count > 0)
            {
                models = discovery.ModelIds;
            }
        }

        return IsCurrent(ticket) ? new PolishModelListing(ticket, provider, models, discovery) : null;
    }

    /// <summary>Whether a listing is still the latest asked for. The page asks again on its own thread, right before it applies one.</summary>
    /// <remarks>
    /// ASKED TWICE, ON PURPOSE. The listing answers its own question when it returns, but the page
    /// applies it on the UI thread some time later, and a person can change provider in between; the
    /// second question is asked there, with no wait between the answer and the controls.
    /// </remarks>
    public bool IsCurrent(int ticket) => ticket == Volatile.Read(ref _discoveryVersion);

    /// <summary>Works out what the model controls should show, from a listing and the field as it stands now.</summary>
    /// <param name="currentModelId">The model id in the free-text field at the moment of applying - not when the listing was asked for.</param>
    /// <param name="chooseDefault">Whether a field that names no plausible model should be filled with the first choice.</param>
    /// <remarks>
    /// PURE, AND READ AT THE MOMENT OF APPLYING. A model typed while the listing was out is the one
    /// the person wants; deciding against the text as it was when the request left would overwrite it.
    /// </remarks>
    public PolishModelChoices Choose(PolishModelListing listing, string currentModelId, bool chooseDefault)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(currentModelId);
        var models = listing.Models;
        var current = currentModelId.Trim();
        var selectedIndex = IndexOf(models, current, listing.Provider);
        string? modelToApply = null;
        if (chooseDefault && selectedIndex < 0 && models.Count > 0)
        {
            // A CLOUD FIELD THAT ALREADY NAMES ONE OF THAT PROVIDER'S MODELS IS LEFT ALONE, even when
            // the listing does not include it: somebody typed a model the catalog does not show.
            // Anything else - blank, or a model from another provider - takes the first choice.
            var shouldChoose = IsCloudProvider(listing.Provider)
                ? !_models.ModelIdBelongsTo(current, listing.Provider)
                : current.Length == 0 || selectedIndex < 0;
            if (shouldChoose)
            {
                modelToApply = models[0];
                selectedIndex = 0;
            }
        }

        return new PolishModelChoices(listing.Provider, models, selectedIndex, modelToApply, listing.Discovery);
    }

    /// <summary>
    /// Where the field's model sits in the choices. For Ollama, <c>foo</c> and <c>foo:latest</c> are one model, as
    /// Ollama itself treats them: compared literally, a valid choice read as missing and was "repaired". Ref: #213.
    /// </summary>
    private static int IndexOf(IReadOnlyList<string> models, string current, PolishProvider provider)
    {
        for (var i = 0; i < models.Count; i++)
        {
            if (provider == PolishProvider.Ollama
                    ? Core.Polish.OllamaModelCatalog.SameModel(models[i], current)
                    : string.Equals(models[i], current, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The failures Windows Credential Manager reports when it cannot be used, as opposed to a bug.</summary>
    private static bool IsCredentialStorageFailure(Exception exception) => exception is
        Win32Exception or
        UnauthorizedAccessException or
        SecurityException or
        ArgumentException;
}
