using EnviousWispr.Core.Polish;
using EnviousWispr.Presentation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>The Ollama models block's decisions, against a scripted host. Ref: #213.</summary>
public sealed class OllamaModelsPresenterTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>The app never starts Ollama: when nothing answers it says how, and offers the download only when Ollama was not found.</summary>
    [Theory]
    [InlineData(OllamaServerState.NotListening, true, OllamaSetupAction.None, "Start Ollama from the Start menu")]
    [InlineData(OllamaServerState.NotListening, false, OllamaSetupAction.DownloadOllama, "couldn't find Ollama")]
    [InlineData(OllamaServerState.NotResponding, true, OllamaSetupAction.None, "isn't responding")]
    [InlineData(OllamaServerState.EndpointInvalid, true, OllamaSetupAction.None, "must be on this PC")]
    [InlineData(OllamaServerState.NoModels, true, OllamaSetupAction.DownloadRecommended, "needs a language model")]
    public async Task EachStateSaysWhatToDo(
        OllamaServerState server,
        bool found,
        OllamaSetupAction action,
        string says)
    {
        var host = new Host { Inventory = new OllamaInventory(server, [], found) };
        var presenter = new OllamaModelsPresenter(host);

        var view = await presenter.RefreshAsync(null).WaitAsync(Patience);

        Assert.Equal(action, view!.Setup.Action);
        Assert.Contains(says, view.Setup.Sentence, StringComparison.Ordinal);
        Assert.Equal(server == OllamaServerState.NoModels, view.Setup.ShowsModels);
        Assert.Equal(server == OllamaServerState.NoModels ? OllamaModelCatalog.Entries.Count : 0, view.Rows.Count);
    }

    /// <summary>Installed first and by band, then what can be downloaded; a model under its other spelling is not offered twice.</summary>
    [Fact]
    public async Task InstalledModelsComeFirstAndAreNotOfferedAgain()
    {
        var host = new Host
        {
            Inventory = Ready(("tinyllama:latest", 700_000_000, "1.1B"), ("my-own", null, null), ("qwen2.5:7b", 4_700_000_000, "7.6B")),
            ActiveModelId = "qwen2.5:7b",
        };
        var presenter = new OllamaModelsPresenter(host);

        var view = (await presenter.RefreshAsync(null).WaitAsync(Patience))!;

        Assert.Equal(["qwen2.5:7b", "my-own", "tinyllama:latest"], view.Rows.Take(3).Select(row => row.Id));
        Assert.All(view.Rows.Take(3), row => Assert.True(row.Installed));
        Assert.DoesNotContain(view.Rows.Skip(3), row => OllamaModelCatalog.SameModel(row.Id, "tinyllama") || OllamaModelCatalog.SameModel(row.Id, "qwen2.5:7b"));
        Assert.Equal(OllamaModelCatalog.Entries.Count - 2, view.Rows.Count - 3);

        var inUse = view.Rows[0];
        Assert.Equal(OllamaRowAction.None, inUse.Action);
        Assert.Contains("polishing your dictation now", inUse.Detail, StringComparison.Ordinal);
        Assert.Equal(OllamaRowAction.None, view.Rows[1].Action);
        Assert.Equal("Not tested by us", view.Rows[1].VerdictLabel);
        Assert.Equal(OllamaRowAction.Remove, view.Rows[2].Action);
        Assert.Equal("Remove TinyLlama", view.Rows[2].ActionName);
        Assert.Equal("Failed every test we ran", view.Rows[2].Note);

        Assert.Equal(OllamaCheck.InUse, presenter.CheckRemove("qwen2.5:7b"));
        Assert.Equal(OllamaCheck.NotOffered, presenter.CheckRemove("my-own"));
        Assert.Equal(OllamaCheck.Confirm, presenter.CheckRemove("tinyllama"));
        Assert.Equal(OllamaCheck.Confirm, presenter.CheckDownload("phi3"));
        Assert.Equal(OllamaCheck.Proceed, presenter.CheckDownload("qwen3:0.6b"));
        Assert.Equal(OllamaCheck.NotOffered, presenter.CheckDownload("my-own"));
    }

    [Fact]
    public async Task ADownloadAddsUpItsLayersSaysWhenItIsQuietAndEndsWithTheListRefreshed()
    {
        var host = new Host { Inventory = Ready() };
        var presenter = new OllamaModelsPresenter(host);
        await presenter.RefreshAsync(null).WaitAsync(Patience);
        var views = new List<OllamaModelsView>();
        host.OnPull = progress =>
        {
            progress.Report(new OllamaPullUpdate(OllamaPullPhase.Downloading, "a", 50, 100));
            progress.Report(new OllamaPullUpdate(OllamaPullPhase.Downloading, "b", 0, 300));
            Assert.Equal(OllamaCheck.Busy, presenter.CheckDownload("qwen2.5:3b"));
            progress.Report(new OllamaPullUpdate(OllamaPullPhase.Verifying, Quiet: true));
            host.Inventory = Ready(("qwen3:0.6b", 523_000_000, "0.6B"));
            return OllamaPullOutcome.Succeeded;
        };

        var change = await presenter.DownloadAsync(null, "qwen3:0.6b", new Collect(views)).WaitAsync(Patience);

        var downloading = views.Select(view => view.Rows.Single(row => row.Id == "qwen3:0.6b")).ToArray();
        Assert.Contains(downloading, row => row is { Downloading: true, Action: OllamaRowAction.Stop, ProgressText: "Downloading... 12%" });
        Assert.Contains(downloading, row => row.ProgressText?.StartsWith("Still checking", StringComparison.Ordinal) == true);
        Assert.All(views.SelectMany(view => view.Rows).Where(row => row.Action == OllamaRowAction.Download), row => Assert.False(row.ActionEnabled));
        Assert.NotNull(change);
        Assert.True(change.Downloaded);
        Assert.Equal("qwen3:0.6b", change.Changed);
        Assert.StartsWith("Qwen 3 (0.6B) is downloaded", change.View.Notice, StringComparison.Ordinal);
        Assert.True(change.View.Rows.Single(row => row.Id == "qwen3:0.6b").Installed);
        // THE FINAL VIEW IS DRAWN AFTER THE CHANGE IS GIVEN BACK: drawn inside it, every button came back disabled.
        Assert.All(change.View.Rows.Where(row => row.Action == OllamaRowAction.Download), row => Assert.True(row.ActionEnabled));
        Assert.Equal(OllamaCheck.Proceed, presenter.CheckDownload("qwen2.5:3b"));
    }

    [Theory]
    [InlineData(OllamaPullOutcome.DiskFull, "Not enough disk space. Qwen 3 (0.6B) needs about 523 MB free.")]
    [InlineData(OllamaPullOutcome.NetworkFailed, "Download failed. Check your internet connection and try again.")]
    [InlineData(OllamaPullOutcome.Interrupted, "Download was interrupted. Try again to pick up where it left off.")]
    public async Task AFailedDownloadSaysWhyAndTheBlockIsFreeAgain(OllamaPullOutcome outcome, string notice)
    {
        var host = new Host { Inventory = Ready(), OnPull = _ => outcome };
        var presenter = new OllamaModelsPresenter(host);
        await presenter.RefreshAsync(null).WaitAsync(Patience);

        var change = await presenter.DownloadAsync(null, "qwen3:0.6b", null).WaitAsync(Patience);

        Assert.False(change!.Downloaded);
        Assert.Equal(notice, change.View.Notice);
        Assert.Equal(OllamaCheck.Proceed, presenter.CheckDownload("qwen3:0.6b"));
    }

    /// <summary>Stop cancels the host's call; the list is looked at again, because a stop can lose the race to success.</summary>
    [Fact]
    public async Task StopEndsTheDownloadAndTheListIsLookedAtAgain()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Host { Inventory = Ready() };
        host.OnPullAsync = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return OllamaPullOutcome.Succeeded;
        };
        var presenter = new OllamaModelsPresenter(host);
        await presenter.RefreshAsync(null).WaitAsync(Patience);
        var inspections = host.Inspections;

        var download = presenter.DownloadAsync(null, "qwen3:0.6b", null);
        await entered.Task.WaitAsync(Patience);
        presenter.Stop();
        var change = await download.WaitAsync(Patience);

        Assert.StartsWith("Download stopped", change!.View.Notice, StringComparison.Ordinal);
        Assert.Equal(inspections + 1, host.Inspections);
    }

    [Fact]
    public async Task NothingStartsOnceTheWindowIsClosing()
    {
        var host = new Host { Inventory = Ready(("tinyllama", null, null)) };
        using var admission = new PresentationAdmission();
        var presenter = new OllamaModelsPresenter(host, admission);
        await presenter.RefreshAsync(null).WaitAsync(Patience);

        await admission.CloseAsync().WaitAsync(Patience);

        Assert.Null(await presenter.RefreshAsync(null).WaitAsync(Patience));
        Assert.Null(await presenter.DownloadAsync(null, "qwen3:0.6b", null).WaitAsync(Patience));
        Assert.Null(await presenter.RemoveAsync(null, "tinyllama").WaitAsync(Patience));
        Assert.Equal(0, host.Pulls);
        Assert.Equal(0, host.Deletes);
        Assert.Equal(OllamaCheck.Confirm, presenter.CheckRemove("tinyllama"));
    }

    [Fact]
    public async Task ARemovalIsRefusedForTheModelInUseAndReportsWhenItIsGone()
    {
        var host = new Host { Inventory = Ready(("tinyllama", null, null), ("phi3", null, null)), ActiveModelId = "phi3:latest" };
        var presenter = new OllamaModelsPresenter(host);
        await presenter.RefreshAsync(null).WaitAsync(Patience);

        Assert.Null(await presenter.RemoveAsync(null, "phi3").WaitAsync(Patience));
        Assert.Equal(0, host.Deletes);

        host.OnDelete = () => host.Inventory = Ready(("phi3", null, null));
        var change = await presenter.RemoveAsync(null, "tinyllama").WaitAsync(Patience);

        Assert.True(change!.Removed);
        Assert.Equal("TinyLlama was removed.", change.View.Notice);
        Assert.Equal(1, host.Deletes);
    }

    [Theory]
    // After a download: an empty field, or the untouched saved model that is missing, takes the model just downloaded.
    [InlineData(true, "", null, "qwen3:0.6b", new[] { "deepseek-r1:14b", "qwen3:0.6b" }, "qwen3:0.6b")]
    [InlineData(true, "llama3", "llama3", "qwen3:0.6b", new[] { "deepseek-r1:14b", "qwen3:0.6b" }, "qwen3:0.6b")]
    // An installed model is right, and a typed one is the person's.
    [InlineData(true, "deepseek-r1:14b", null, "qwen3:0.6b", new[] { "deepseek-r1:14b", "qwen3:0.6b" }, null)]
    [InlineData(true, "something-typed", "llama3", "qwen3:0.6b", new[] { "deepseek-r1:14b", "qwen3:0.6b" }, null)]
    // After a removal of the model the field named: the recommendation, then the best band, then empty.
    [InlineData(false, "Qwen3:0.6b:latest", "qwen3:0.6b", "qwen3:0.6b", new[] { "deepseek-r1:14b", "qwen2.5:3b" }, "qwen2.5:3b")]
    [InlineData(false, "qwen3:0.6b", "qwen3:0.6b", "qwen3:0.6b", new[] { "tinyllama", "gemma2:2b" }, "gemma2:2b")]
    [InlineData(false, "qwen3:0.6b", "qwen3:0.6b", "qwen3:0.6b", new string[0], "")]
    // Reinstalled by another tool before this listing: installed again, so left alone.
    [InlineData(false, "qwen3:0.6b", "qwen3:0.6b", "qwen3:0.6b", new[] { "qwen3:0.6b", "qwen2.5:3b" }, null)]
    [InlineData(false, "deepseek-r1:14b", null, "qwen3:0.6b", new[] { "deepseek-r1:14b" }, null)]
    public void TheFieldIsRepairedOnlyWhereTheChangeLeftItNamingNothing(
        bool downloaded,
        string field,
        string? saved,
        string changed,
        string[] installed,
        string? expected)
    {
        var change = new OllamaModelChange(new OllamaModelsView(new OllamaSetupView("", OllamaSetupAction.None, null, true), [], null), changed, downloaded, !downloaded);

        Assert.Equal(expected, OllamaModelsPresenter.RepairSelection(installed, field, saved, change));
    }

    /// <summary>A slow look after a change never overwrites a newer look that finished first. Ref: #213 review.</summary>
    [Fact]
    public async Task ASlowLookAfterADownloadDoesNotOverwriteANewerOne()
    {
        var hold = new TaskCompletionSource<OllamaInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Host { Inventory = Ready() };
        var presenter = new OllamaModelsPresenter(host);
        await presenter.RefreshAsync(null).WaitAsync(Patience);
        host.NextInspection = hold.Task;

        var download = presenter.DownloadAsync(null, "qwen3:0.6b", null);
        await host.Held.Task.WaitAsync(Patience);
        host.Inventory = Ready(("gemma2", null, null));
        Assert.NotNull(await presenter.RefreshAsync(null).WaitAsync(Patience));
        hold.SetResult(Ready(("qwen3:0.6b", null, null)));
        await download.WaitAsync(Patience);

        Assert.Contains(presenter.View.Rows, row => row is { Id: "gemma2", Installed: true });
        Assert.DoesNotContain(presenter.View.Rows, row => row is { Id: "qwen3:0.6b", Installed: true });
    }

    /// <summary>Whatever the host throws, the block is free again afterwards. Ref: #213 review.</summary>
    [Fact]
    public async Task AHostThatThrowsStillGivesTheBlockBack()
    {
        var host = new Host { Inventory = Ready(), OnPull = _ => throw new InvalidOperationException("boom") };
        var presenter = new OllamaModelsPresenter(host);
        await presenter.RefreshAsync(null).WaitAsync(Patience);

        var change = await presenter.DownloadAsync(null, "qwen3:0.6b", null).WaitAsync(Patience);

        Assert.StartsWith("Ollama could not download", change!.View.Notice, StringComparison.Ordinal);
        Assert.False(presenter.Changing);
        Assert.Equal(OllamaCheck.Proceed, presenter.CheckDownload("qwen3:0.6b"));
    }

    private static OllamaInventory Ready(params (string Id, long? Size, string? Parameters)[] models) =>
        new(models.Length == 0 ? OllamaServerState.NoModels : OllamaServerState.Ready,
            models.Select(model => new OllamaInstalledModel(model.Id, model.Size, model.Parameters)).ToArray(),
            OllamaFound: true);

    private sealed class Collect(List<OllamaModelsView> into) : IProgress<OllamaModelsView>
    {
        public void Report(OllamaModelsView value) => into.Add(value);
    }

    private sealed class Host : IOllamaModelHost
    {
        public OllamaInventory Inventory { get; set; } = new(OllamaServerState.NoModels, [], OllamaFound: true);

        public string? ActiveModelId { get; set; }

        public Func<IProgress<OllamaPullUpdate>, OllamaPullOutcome>? OnPull { get; set; }

        public Func<IProgress<OllamaPullUpdate>, CancellationToken, Task<OllamaPullOutcome>>? OnPullAsync { get; set; }

        public Action? OnDelete { get; set; }

        public int Inspections { get; private set; }

        public int Pulls { get; private set; }

        public int Deletes { get; private set; }

        public Task<OllamaInventory>? NextInspection { get; set; }

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OllamaInventory> InspectAsync(string? endpoint, CancellationToken cancellationToken)
        {
            Inspections++;
            if (NextInspection is { } held)
            {
                NextInspection = null;
                Held.TrySetResult();
                return held;
            }

            return Task.FromResult(Inventory);
        }

        public async Task<OllamaPullOutcome> PullAsync(string? endpoint, string modelId, IProgress<OllamaPullUpdate> progress, CancellationToken cancellationToken)
        {
            Pulls++;
            if (OnPullAsync is { } slow)
            {
                return await slow(progress, cancellationToken);
            }

            return OnPull?.Invoke(progress) ?? OllamaPullOutcome.Succeeded;
        }

        public Task<OllamaDeleteOutcome> DeleteAsync(string? endpoint, string modelId, CancellationToken cancellationToken)
        {
            Deletes++;
            OnDelete?.Invoke();
            return Task.FromResult(OllamaDeleteOutcome.Deleted);
        }
    }
}
