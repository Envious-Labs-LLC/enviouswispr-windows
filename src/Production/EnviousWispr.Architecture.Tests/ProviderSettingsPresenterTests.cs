using System.ComponentModel;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The Polish page's provider decisions without the page: which providers take a key, what a blank
/// key means, what a refusing credential store means, which model the field takes when the provider
/// changes, and that a discovery overtaken by a later one is thrown away.
/// </summary>
public sealed class ProviderSettingsPresenterTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(PolishProvider.None)]
    [InlineData(PolishProvider.EgOne)]
    [InlineData(PolishProvider.Ollama)]
    public void ALocalOrAbsentProviderHasNoKeyToStoreOrRemove(PolishProvider provider)
    {
        var (presenter, keys, _) = Build();

        Assert.Equal(ApiKeySaveOutcome.NotACloudProvider, presenter.SaveKey(provider, "sk-anything"));
        Assert.Equal(ApiKeyRemovalCheck.NotACloudProvider, presenter.CheckKeyRemoval(provider));
        Assert.Empty(keys.Stored);
    }

    [Fact]
    public void ABlankKeyIsRefusedBeforeTheStoreIsTouched()
    {
        var (presenter, keys, _) = Build();

        Assert.Equal(ApiKeySaveOutcome.EmptyKey, presenter.SaveKey(PolishProvider.OpenAI, "   "));
        Assert.Empty(keys.Stored);
        Assert.Equal(0, keys.StoreCalls);
    }

    [Fact]
    public void AKeyIsStoredTrimmedAndReportedAsPresentWithoutBeingRevealed()
    {
        var (presenter, keys, _) = Build();

        Assert.Equal(ApiKeySaveOutcome.Saved, presenter.SaveKey(PolishProvider.Anthropic, "  sk-ant-123  "));

        Assert.Equal("sk-ant-123", keys.Stored[PolishProvider.Anthropic]);
        Assert.Equal(ApiKeyReadStatus.Found, presenter.KeyStatus(PolishProvider.Anthropic));
        Assert.Equal(ApiKeyReadStatus.Missing, presenter.KeyStatus(PolishProvider.OpenAI));
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(ArgumentException))]
    public void ACredentialStoreThatRefusesIsReportedAsUnavailableForSaveAndRemove(Type failure)
    {
        var (presenter, keys, _) = Build();
        keys.FailWith = (Exception)Activator.CreateInstance(failure)!;

        Assert.Equal(ApiKeySaveOutcome.StorageUnavailable, presenter.SaveKey(PolishProvider.Gemini, "key"));
        Assert.Equal(ApiKeyRemoveOutcome.StorageUnavailable, presenter.RemoveKey(PolishProvider.Gemini));
        Assert.Empty(keys.Stored);
    }

    [Fact]
    public void AnUnexpectedStoreFailureIsNotSwallowed()
    {
        var (presenter, keys, _) = Build();
        keys.FailWith = new InvalidOperationException("a bug");

        Assert.Throws<InvalidOperationException>(() => presenter.SaveKey(PolishProvider.Gemini, "key"));
    }

    [Fact]
    public void RemovalIsOfferedOnlyWhenAKeyIsStoredAndThenRemovesIt()
    {
        var (presenter, keys, _) = Build();
        Assert.Equal(ApiKeyRemovalCheck.NothingStored, presenter.CheckKeyRemoval(PolishProvider.OpenAI));

        presenter.SaveKey(PolishProvider.OpenAI, "sk-1");
        Assert.Equal(ApiKeyRemovalCheck.Confirm, presenter.CheckKeyRemoval(PolishProvider.OpenAI));

        Assert.Equal(ApiKeyRemoveOutcome.Removed, presenter.RemoveKey(PolishProvider.OpenAI));
        Assert.False(keys.Stored.ContainsKey(PolishProvider.OpenAI));
        Assert.Equal(ApiKeyReadStatus.Missing, presenter.KeyStatus(PolishProvider.OpenAI));
    }

    [Fact]
    public async Task AProviderWithNoModelsDiscoversNothingAndOffersNothing()
    {
        var (presenter, _, models) = Build();

        var none = await RefreshAsync(presenter, PolishProvider.None, null, "gpt-4o-mini", chooseDefault: true).WaitAsync(Patience);
        var egOne = await RefreshAsync(presenter, PolishProvider.EgOne, null, string.Empty, chooseDefault: true).WaitAsync(Patience);

        Assert.Empty(none!.Models);
        Assert.Null(none.Discovery);
        Assert.Equal(["eg-1"], egOne!.Models);
        Assert.Equal("eg-1", egOne.ModelToApply);
        Assert.Equal(0, egOne.SelectedIndex);
        Assert.Equal(0, models.Discoveries);
    }

    [Fact]
    public async Task AMissingCloudKeyKeepsTheRecommendedModelAndSaysWhy()
    {
        var (presenter, _, models) = Build();
        models.Answer = new PolishModelDiscovery(PolishModelDiscoveryStatus.MissingCredential, []);

        var choices = await RefreshAsync(presenter, PolishProvider.OpenAI, null, string.Empty, chooseDefault: true).WaitAsync(Patience);

        Assert.Equal(["gpt-recommended"], choices!.Models);
        Assert.Equal("gpt-recommended", choices.ModelToApply);
        Assert.Equal(PolishModelDiscoveryStatus.MissingCredential, choices.Discovery!.Status);
    }

    [Fact]
    public async Task ACloudListingReplacesTheRecommendationAndLeavesAModelOfThatProviderAlone()
    {
        var (presenter, _, models) = Build();
        models.Answer = new PolishModelDiscovery(PolishModelDiscoveryStatus.Ready, ["gpt-a", "gpt-b"]);

        // A typed model of this provider that the listing does not show is kept as typed.
        var kept = await RefreshAsync(presenter, PolishProvider.OpenAI, null, "gpt-custom", chooseDefault: true).WaitAsync(Patience);
        Assert.Equal(["gpt-a", "gpt-b"], kept!.Models);
        Assert.Null(kept.ModelToApply);
        Assert.Equal(-1, kept.SelectedIndex);

        // A model of another provider is replaced by the first choice.
        var replaced = await RefreshAsync(presenter, PolishProvider.OpenAI, null, "claude-haiku", chooseDefault: true).WaitAsync(Patience);
        Assert.Equal("gpt-a", replaced!.ModelToApply);
        Assert.Equal(0, replaced.SelectedIndex);

        // A model in the listing is selected, whatever its case.
        var matched = await RefreshAsync(presenter, PolishProvider.OpenAI, null, " GPT-B ", chooseDefault: true).WaitAsync(Patience);
        Assert.Null(matched!.ModelToApply);
        Assert.Equal(1, matched.SelectedIndex);
    }

    [Fact]
    public async Task ACloudListingWithNothingCompatibleKeepsTheRecommendation()
    {
        var (presenter, _, models) = Build();
        models.Answer = new PolishModelDiscovery(PolishModelDiscoveryStatus.Ready, []);

        var choices = await RefreshAsync(presenter, PolishProvider.Gemini, null, string.Empty, chooseDefault: false).WaitAsync(Patience);

        Assert.Equal(["gemini-recommended"], choices!.Models);
        Assert.Null(choices.ModelToApply);
        Assert.Equal(-1, choices.SelectedIndex);
    }

    [Fact]
    public async Task OllamaDiscoveryUsesTheEndpointAndChoosesALocalModelOnlyWhenTheFieldNamesNone()
    {
        var (presenter, _, models) = Build();
        models.Answer = new PolishModelDiscovery(PolishModelDiscoveryStatus.Ready, ["llama3", "phi4"]);

        var blank = await RefreshAsync(presenter, PolishProvider.Ollama, "http://127.0.0.1:11434", string.Empty, chooseDefault: true).WaitAsync(Patience);
        Assert.Equal("http://127.0.0.1:11434", models.LastEndpoint);
        Assert.Equal("llama3", blank!.ModelToApply);

        var typed = await RefreshAsync(presenter, PolishProvider.Ollama, null, "phi4", chooseDefault: true).WaitAsync(Patience);
        Assert.Null(typed!.ModelToApply);
        Assert.Equal(1, typed.SelectedIndex);

        var unknown = await RefreshAsync(presenter, PolishProvider.Ollama, null, "mistral", chooseDefault: true).WaitAsync(Patience);
        Assert.Equal("llama3", unknown!.ModelToApply);

        models.Answer = new PolishModelDiscovery(PolishModelDiscoveryStatus.OllamaNotReady, []);
        var down = await RefreshAsync(presenter, PolishProvider.Ollama, null, "phi4", chooseDefault: true).WaitAsync(Patience);
        Assert.Empty(down!.Models);
        Assert.Null(down.ModelToApply);
        Assert.Equal(PolishModelDiscoveryStatus.OllamaNotReady, down.Discovery!.Status);
    }

    /// <summary>Ollama treats foo and foo:latest as one model; so does the picker, or a valid choice is "repaired" away. Ref: #213.</summary>
    [Fact]
    public async Task AnOllamaFieldNamingTheModelUnderItsOtherSpellingIsLeftAlone()
    {
        var (presenter, _, models) = Build();
        models.Answer = new PolishModelDiscovery(PolishModelDiscoveryStatus.Ready, ["deepseek-r1:14b", "llama3.2:latest"]);

        var choices = await RefreshAsync(presenter, PolishProvider.Ollama, null, "Llama3.2", chooseDefault: true).WaitAsync(Patience);

        Assert.Null(choices!.ModelToApply);
        Assert.Equal(1, choices.SelectedIndex);
    }

    [Fact]
    public async Task AModelTypedWhileTheListingWasOutIsHonoured()
    {
        // THE FIELD IS READ WHEN THE LISTING IS APPLIED. The request left with the field holding the
        // old provider's model; by the time the listing is applied the person has typed one of the
        // new provider's, and that is what the decision is made against.
        var (presenter, _, models) = Build();
        models.Hold = true;
        var listing = presenter.ListModelsAsync(PolishProvider.OpenAI, null);
        await models.Entered.Task.WaitAsync(Patience);
        models.Answer = new PolishModelDiscovery(PolishModelDiscoveryStatus.Ready, ["gpt-a"]);
        models.Release.SetResult();
        var listed = await listing.WaitAsync(Patience);

        var choices = presenter.Choose(listed!, "gpt-typed-meanwhile", chooseDefault: true);

        Assert.Null(choices.ModelToApply);
        Assert.Equal(-1, choices.SelectedIndex);
    }

    [Fact]
    public async Task AListingCurrentWhenItReturnedIsRefusedIfAnotherRefreshBeganBeforeItWasApplied()
    {
        // THE SECOND QUESTION. Listing A returns current; before the page applies it, the person
        // changes provider and listing B completes. Asked again on the page's thread, A is no longer
        // the latest and is not applied; B is.
        var (presenter, _, models) = Build();
        var a = await presenter.ListModelsAsync(PolishProvider.OpenAI, null).WaitAsync(Patience);
        Assert.NotNull(a);
        Assert.True(presenter.IsCurrent(a.Ticket));

        var b = await presenter.ListModelsAsync(PolishProvider.None, null).WaitAsync(Patience);

        Assert.False(presenter.IsCurrent(a.Ticket));
        Assert.True(presenter.IsCurrent(b!.Ticket));
    }

    [Fact]
    public async Task ADiscoveryOvertakenByALaterOneIsThrownAway()
    {
        // THE PROVIDER CHANGED WHILE THE FIRST LISTING WAS STILL OUT. The second answers first; when
        // the first finally comes back it is answered with nothing, so the page shows the provider
        // chosen last rather than whichever provider's listing happened to arrive last.
        var (presenter, _, models) = Build();
        models.Hold = true;
        var first = RefreshAsync(presenter, PolishProvider.OpenAI, null, string.Empty, chooseDefault: true);
        await models.Entered.Task.WaitAsync(Patience);

        models.Hold = false;
        models.Answer = new PolishModelDiscovery(PolishModelDiscoveryStatus.Ready, ["claude-x"]);
        var second = await RefreshAsync(presenter, PolishProvider.Anthropic, null, string.Empty, chooseDefault: true).WaitAsync(Patience);
        Assert.Equal(["claude-x"], second!.Models);

        models.Release.SetResult();
        Assert.Null(await first.WaitAsync(Patience));
    }

    /// <summary>The two steps the page takes, as one: list, then choose against the field as it stands.</summary>
    private static async Task<PolishModelChoices?> RefreshAsync(
        ProviderSettingsPresenter presenter,
        PolishProvider provider,
        string? ollamaEndpoint,
        string currentModelId,
        bool chooseDefault)
    {
        var listing = await presenter.ListModelsAsync(provider, ollamaEndpoint);
        return listing is null || !presenter.IsCurrent(listing.Ticket)
            ? null
            : presenter.Choose(listing, currentModelId, chooseDefault);
    }

    private static (ProviderSettingsPresenter Presenter, FakeKeys Keys, FakeModels Models) Build()
    {
        var keys = new FakeKeys();
        var models = new FakeModels();
        return (new ProviderSettingsPresenter(keys, models), keys, models);
    }

    private sealed class FakeKeys : IApiKeyStore
    {
        public Dictionary<PolishProvider, string> Stored { get; } = [];

        public Exception? FailWith { get; set; }

        public int StoreCalls { get; private set; }

        public ApiKeyReadResult Read(PolishProvider provider) =>
            Stored.TryGetValue(provider, out var value) ? ApiKeyReadResult.Found(value) : ApiKeyReadResult.Missing;

        public void Store(PolishProvider provider, string value)
        {
            StoreCalls++;
            if (FailWith is { } failure)
            {
                throw failure;
            }

            Stored[provider] = value;
        }

        public void Delete(PolishProvider provider)
        {
            if (FailWith is { } failure)
            {
                throw failure;
            }

            Stored.Remove(provider);
        }
    }

    private sealed class FakeModels : IPolishModelSource
    {
        public PolishModelDiscovery Answer { get; set; } = new(PolishModelDiscoveryStatus.Ready, []);

        public bool Hold { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Discoveries { get; private set; }

        public string? LastEndpoint { get; private set; }

        public string? RecommendedModel(PolishProvider provider) => provider switch
        {
            PolishProvider.OpenAI => "gpt-recommended",
            PolishProvider.Anthropic => "claude-recommended",
            PolishProvider.Gemini => "gemini-recommended",
            _ => null,
        };

        public bool ModelIdBelongsTo(string? modelId, PolishProvider provider) => provider switch
        {
            PolishProvider.OpenAI => modelId?.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) == true,
            PolishProvider.Anthropic => modelId?.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) == true,
            PolishProvider.Gemini => modelId?.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) == true,
            _ => false,
        };

        public async Task<PolishModelDiscovery> DiscoverAsync(PolishProvider provider, string? ollamaEndpoint, CancellationToken cancellationToken)
        {
            Discoveries++;
            LastEndpoint = ollamaEndpoint;
            Entered.TrySetResult();
            if (Hold)
            {
                await Release.Task;
            }

            return Answer;
        }
    }
}
