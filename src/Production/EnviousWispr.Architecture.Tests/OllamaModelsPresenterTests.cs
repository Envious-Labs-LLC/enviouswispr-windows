using EnviousWispr.Core.Polish;
using EnviousWispr.Presentation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>The Ollama models block's decisions, against a scripted host. Ref: #213.</summary>
public sealed class OllamaModelsPresenterTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>When nothing is listening and Ollama is installed, offers Open Ollama; never on a slow server, and offers Download Ollama only when not found.</summary>
    [Theory]
    [InlineData(OllamaServerState.NotListening, true, OllamaSetupAction.OpenOllama, "Open Ollama to use local or hosted models", "Not running")]
    [InlineData(OllamaServerState.NotListening, false, OllamaSetupAction.DownloadOllama, "couldn't find Ollama", "Not installed")]
    [InlineData(OllamaServerState.NotResponding, true, OllamaSetupAction.None, "isn't responding", "Not responding")]
    [InlineData(OllamaServerState.EndpointInvalid, true, OllamaSetupAction.None, "must be on this PC", "Invalid endpoint")]
    [InlineData(OllamaServerState.NoModels, true, OllamaSetupAction.DownloadRecommended, "needs a language model", "Running")]
    public async Task EachStateSaysWhatToDo(
        OllamaServerState server,
        bool found,
        OllamaSetupAction action,
        string says,
        string expectedBadge)
    {
        var host = new Host { Inventory = new OllamaInventory(server, [], found) };
        var presenter = new OllamaModelsPresenter(host);

        var view = await presenter.RefreshAsync(null).WaitAsync(Patience);

        Assert.Equal(action, view!.Setup.Action);
        Assert.Equal(expectedBadge, view.Setup.StatusBadge);
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

        var change = await presenter.DownloadAsync("qwen3:0.6b", presenter.Context!, new Collect(views)).WaitAsync(Patience);

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

        var change = await presenter.DownloadAsync("qwen3:0.6b", presenter.Context!, null).WaitAsync(Patience);

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

        var download = presenter.DownloadAsync("qwen3:0.6b", presenter.Context!, null);
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
        var context = presenter.Context!;

        await admission.CloseAsync().WaitAsync(Patience);

        Assert.Null(await presenter.RefreshAsync(null).WaitAsync(Patience));
        Assert.Null(await presenter.DownloadAsync("qwen3:0.6b", context, null).WaitAsync(Patience));
        Assert.Null(await presenter.RemoveAsync("tinyllama", context).WaitAsync(Patience));
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

        Assert.Null(await presenter.RemoveAsync("phi3", presenter.Context!).WaitAsync(Patience));
        Assert.Equal(0, host.Deletes);

        host.OnDelete = () => host.Inventory = Ready(("phi3", null, null));
        var change = await presenter.RemoveAsync("tinyllama", presenter.Context!).WaitAsync(Patience);

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
        var change = new OllamaModelChange(new OllamaModelsView(new OllamaSetupView("", OllamaSetupAction.None, null, true), [], null), changed, downloaded, !downloaded, null);

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

        var download = presenter.DownloadAsync("qwen3:0.6b", presenter.Context!, null);
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

        var change = await presenter.DownloadAsync("qwen3:0.6b", presenter.Context!, null).WaitAsync(Patience);

        Assert.StartsWith("Ollama could not download", change!.View.Notice, StringComparison.Ordinal);
        Assert.False(presenter.Changing);
        Assert.Equal(OllamaCheck.Proceed, presenter.CheckDownload("qwen3:0.6b"));
    }

    /// <summary>
    /// A download finishes after the page moved to another endpoint: its look afterwards is not even made, and the page
    /// keeps the other endpoint's list. Downloads go to the endpoint the list came from. Ref: #213 re-review.
    /// </summary>
    [Fact]
    public async Task ADownloadThatEndsAfterTheEndpointChangedLeavesTheNewEndpointsList()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Host();
        host.ByEndpoint["http://a:11434"] = Ready(("qwen3:0.6b", null, null));
        host.ByEndpoint["http://b:11434"] = Ready(("gemma2", null, null));
        host.OnPullAsync = async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return OllamaPullOutcome.Succeeded;
        };
        var presenter = new OllamaModelsPresenter(host);
        host.ByEndpoint["http://a:11434"] = Ready();
        await presenter.RefreshAsync("http://a:11434").WaitAsync(Patience);
        host.ByEndpoint["http://a:11434"] = Ready(("qwen3:0.6b", null, null));

        var download = presenter.DownloadAsync("qwen3:0.6b", presenter.Context!, null);
        await entered.Task.WaitAsync(Patience);
        Assert.Equal("http://a:11434", host.LastPullEndpoint);
        await presenter.RefreshAsync("http://b:11434").WaitAsync(Patience);
        var inspections = host.Inspections;
        release.SetResult();
        var change = await download.WaitAsync(Patience);

        Assert.Equal("http://a:11434", change!.Endpoint);
        Assert.Equal(inspections, host.Inspections);
        Assert.Contains(presenter.View.Rows, row => row is { Id: "gemma2", Installed: true });
        Assert.DoesNotContain(presenter.View.Rows, row => row is { Id: "qwen3:0.6b", Installed: true });
    }

    [Fact]
    public async Task NothingIsDownloadedOrRemovedBeforeOllamaHasBeenLookedAt()
    {
        var host = new Host { Inventory = Ready(("tinyllama", null, null)) };
        var presenter = new OllamaModelsPresenter(host);

        Assert.Null(presenter.Context);
        var never = new OllamaListContext(null, 0);
        Assert.Null(await presenter.DownloadAsync("qwen3:0.6b", never, null).WaitAsync(Patience));
        Assert.Null(await presenter.RemoveAsync("tinyllama", never).WaitAsync(Patience));
        Assert.Equal(0, host.Pulls);
        Assert.Equal(0, host.Deletes);
    }

    /// <summary>
    /// A model the app selected after a download, then removed before saving: the field still reads what the app put
    /// there, so it is repaired - comparing with the saved value called that an edit. Ref: #213 re-review.
    /// </summary>
    [Fact]
    public void AModelTheAppSelectedIsStillTheAppsToRepairAfterItIsRemoved()
    {
        var view = new OllamaModelsView(new OllamaSetupView("", OllamaSetupAction.None, null, true), [], null);
        var downloaded = new OllamaModelChange(view, "qwen3:0.6b", Downloaded: true, Removed: false, null);
        var chosen = OllamaModelsPresenter.RepairSelection(["qwen3:0.6b"], "llama3", setByApp: "llama3", downloaded);
        Assert.Equal("qwen3:0.6b", chosen);

        var removed = new OllamaModelChange(view, "qwen3:0.6b", Downloaded: false, Removed: true, null);
        Assert.Equal(string.Empty, OllamaModelsPresenter.RepairSelection([], chosen!, setByApp: chosen, removed));
    }

    /// <summary>
    /// While a look at another endpoint is out, the rows on screen belong to the old server and the page is asking a new
    /// one: nothing may be changed, and the buttons say so. Ref: #213 final review.
    /// </summary>
    [Fact]
    public async Task NothingChangesWhileTheListOnScreenBelongsToAnotherEndpoint()
    {
        var hold = new TaskCompletionSource<OllamaInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Host();
        host.ByEndpoint["http://a:11434"] = Ready(("tinyllama", null, null));
        var presenter = new OllamaModelsPresenter(host);
        await presenter.RefreshAsync("http://a:11434").WaitAsync(Patience);
        var sawA = presenter.Context!;

        host.NextInspection = hold.Task;
        var toB = presenter.RefreshAsync("http://b:11434");
        await host.Held.Task.WaitAsync(Patience);

        Assert.Null(presenter.Context);
        Assert.All(presenter.View.Rows.Where(row => row.Action != OllamaRowAction.None), row => Assert.False(row.ActionEnabled));
        Assert.Null(await presenter.RemoveAsync("tinyllama", sawA).WaitAsync(Patience));
        Assert.Null(await presenter.DownloadAsync("qwen3:0.6b", sawA, null).WaitAsync(Patience));
        Assert.Equal(0, host.Deletes);
        Assert.Equal(0, host.Pulls);

        hold.SetResult(Ready(("tinyllama", null, null)));
        await toB.WaitAsync(Patience);

        // The same model on the new server is a different list: a confirmation given for A does not carry over.
        Assert.NotEqual(sawA, presenter.Context);
        Assert.Null(await presenter.RemoveAsync("tinyllama", sawA).WaitAsync(Patience));
        Assert.Equal(0, host.Deletes);
        Assert.NotNull(await presenter.RemoveAsync("tinyllama", presenter.Context!).WaitAsync(Patience));
        Assert.Equal(1, host.Deletes);
    }

    /// <summary>A download at an endpoint the page has since left says nothing on the new endpoint's page.</summary>
    [Fact]
    public async Task ADownloadAtALeftEndpointLeavesNoNoticeOnTheNewPage()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Host();
        host.ByEndpoint["http://a:11434"] = Ready();
        host.ByEndpoint["http://b:11434"] = Ready(("gemma2", null, null));
        host.OnPullAsync = async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return OllamaPullOutcome.DiskFull;
        };
        var presenter = new OllamaModelsPresenter(host);
        await presenter.RefreshAsync("http://a:11434").WaitAsync(Patience);

        var download = presenter.DownloadAsync("qwen3:0.6b", presenter.Context!, null);
        await entered.Task.WaitAsync(Patience);
        await presenter.RefreshAsync("http://b:11434").WaitAsync(Patience);
        release.SetResult();
        await download.WaitAsync(Patience);

        Assert.Null(presenter.View.Notice);
    }

    [Fact]
    public async Task ANoticeStaysWithTheServerItWasAbout()
    {
        var host = new Host();
        host.ByEndpoint["http://a:11434"] = Ready();
        host.ByEndpoint["http://b:11434"] = Ready(("gemma2", null, null));
        host.OnPull = _ => OllamaPullOutcome.NetworkFailed;
        var presenter = new OllamaModelsPresenter(host);
        await presenter.RefreshAsync("http://a:11434").WaitAsync(Patience);
        await presenter.DownloadAsync("qwen3:0.6b", presenter.Context!, null).WaitAsync(Patience);
        Assert.NotNull(presenter.View.Notice);

        await presenter.RefreshAsync("http://b:11434").WaitAsync(Patience);

        Assert.Null(presenter.View.Notice);
    }

    [Fact]
    public async Task OpenOllamaPollsUntilServerAnswersAndTransitionsToReadyWithoutManualRefresh()
    {
        var host = new Host
        {
            Inventory = new OllamaInventory(OllamaServerState.NotListening, [], OllamaFound: true),
            OpenOllamaResult = true,
        };
        var pollAttempts = 0;
        host.OnInspect = () =>
        {
            pollAttempts++;
            if (pollAttempts >= 4)
            {
                host.Inventory = Ready(("qwen2.5:3b", 1_900_000_000, "3.1B"));
            }
        };
        var presenter = new OllamaModelsPresenter(host);
        await presenter.RefreshAsync(null).WaitAsync(Patience);
        Assert.Equal(OllamaSetupAction.OpenOllama, presenter.View.Setup.Action);

        var progressViews = new List<OllamaModelsView>();
        var finalView = await presenter.OpenOllamaAsync(
            null,
            new Collect(progressViews),
            delay: (_, _) => Task.CompletedTask).WaitAsync(Patience);

        Assert.Equal(1, host.OpenAttempts);
        Assert.Contains(progressViews, v => v.Setup.StatusBadge == "Starting" && v.Setup.Action == OllamaSetupAction.None);
        Assert.NotNull(finalView);
        Assert.Equal("Running", finalView!.Setup.StatusBadge);
        Assert.Equal(OllamaSetupAction.None, finalView.Setup.Action);
        Assert.Contains(finalView.Rows, row => row is { Id: "qwen2.5:3b", Installed: true });
        Assert.NotNull(presenter.Context);
    }

    [Fact]
    public async Task OpenOllamaStartupPollAbortsOnEpochSupersedureAndReportsFailedLaunch()
    {
        var failHost = new Host
        {
            Inventory = new OllamaInventory(OllamaServerState.NotListening, [], OllamaFound: true),
            OpenOllamaResult = false,
        };
        var failPresenter = new OllamaModelsPresenter(failHost);
        await failPresenter.RefreshAsync(null).WaitAsync(Patience);

        var failed = await failPresenter.OpenOllamaAsync(
            null,
            delay: (_, _) => Task.CompletedTask).WaitAsync(Patience);
        Assert.NotNull(failed);
        Assert.Equal(OllamaSetupAction.OpenOllama, failed!.Setup.Action);
        Assert.Contains("couldn't start Ollama automatically", failed.Notice, StringComparison.Ordinal);

        // Epoch guard: if a refresh on another endpoint happens while the startup poll is waiting, the startup poll aborts cleanly.
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Host
        {
            Inventory = new OllamaInventory(OllamaServerState.NotListening, [], OllamaFound: true),
            OpenOllamaResult = true,
        };
        host.ByEndpoint["http://b:11434"] = Ready(("gemma2", null, null));
        var presenter = new OllamaModelsPresenter(host);
        await presenter.RefreshAsync("http://a:11434").WaitAsync(Patience);

        var openTask = presenter.OpenOllamaAsync(
            "http://a:11434",
            delay: async (_, _) =>
            {
                delayEntered.TrySetResult();
                await delayRelease.Task;
            });
        await delayEntered.Task.WaitAsync(Patience);
        await presenter.RefreshAsync("http://b:11434").WaitAsync(Patience);
        delayRelease.SetResult();
        var supersededResult = await openTask.WaitAsync(Patience);

        Assert.Null(supersededResult);
        Assert.Equal("http://b:11434", presenter.Context?.Endpoint);
        Assert.Contains(presenter.View.Rows, row => row is { Id: "gemma2", Installed: true });

        // Network exposure guard: blocks launch and explains why.
        var exposedHost = new Host
        {
            Inventory = new OllamaInventory(OllamaServerState.NotListening, [], OllamaFound: true),
            LaunchSafety = OllamaLaunchSafety.BlockedByNetworkExposure,
        };
        var exposedPresenter = new OllamaModelsPresenter(exposedHost);
        await exposedPresenter.RefreshAsync(null).WaitAsync(Patience);
        var blocked = await exposedPresenter.OpenOllamaAsync(null, delay: (_, _) => Task.CompletedTask).WaitAsync(Patience);
        Assert.NotNull(blocked);
        Assert.Equal(0, exposedHost.OpenAttempts);
        Assert.Contains("expose its server to the network", blocked!.Notice, StringComparison.Ordinal);

        // Superseded startup poll must not clear a newer startup poll's Starting state.
        var firstDelayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDelayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDelayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDelayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartHost = new Host
        {
            Inventory = new OllamaInventory(OllamaServerState.NotListening, [], OllamaFound: true),
            OpenOllamaResult = true,
        };
        var restartPresenter = new OllamaModelsPresenter(restartHost);
        await restartPresenter.RefreshAsync(null).WaitAsync(Patience);

        var firstPoll = restartPresenter.OpenOllamaAsync(
            null,
            delay: async (_, _) =>
            {
                firstDelayEntered.TrySetResult();
                await firstDelayRelease.Task;
            });
        await firstDelayEntered.Task.WaitAsync(Patience);
        restartPresenter.Stop();

        var secondPoll = restartPresenter.OpenOllamaAsync(
            null,
            delay: async (_, _) =>
            {
                secondDelayEntered.TrySetResult();
                await secondDelayRelease.Task;
            });
        await secondDelayEntered.Task.WaitAsync(Patience);
        Assert.Equal("Starting", restartPresenter.View.Setup.StatusBadge);

        firstDelayRelease.SetResult();
        Assert.Null(await firstPoll.WaitAsync(Patience));
        Assert.Equal("Starting", restartPresenter.View.Setup.StatusBadge);

        restartHost.Inventory = Ready(("qwen2.5:3b", 1_900_000_000, "3.1B"));
        secondDelayRelease.SetResult();
        var secondResult = await secondPoll.WaitAsync(Patience);
        Assert.NotNull(secondResult);
        Assert.Equal("Running", secondResult!.Setup.StatusBadge);
    }

    [Fact]
    public void HostedModelsNeverBecomeAutomaticDefaultsAfterRemovalOrInRepair()
    {
        var view = new OllamaModelsView(new OllamaSetupView("", OllamaSetupAction.None, null, true), [], null);
        var removed = new OllamaModelChange(view, "qwen2.5:3b", Downloaded: false, Removed: true, null);

        // When only a hosted model remains after removing a local model, RepairSelection clears to empty rather than defaulting to the cloud.
        Assert.Equal(
            string.Empty,
            OllamaModelsPresenter.RepairSelection(["gpt-oss:120b-cloud"], "qwen2.5:3b", setByApp: "qwen2.5:3b", removed));

        // When the user explicitly adds a hosted model, RepairSelection selects the hosted model they just added.
        var addedHosted = new OllamaModelChange(view, "glm-5.1-cloud", Downloaded: true, Removed: false, null);
        Assert.Equal(
            "glm-5.1-cloud",
            OllamaModelsPresenter.RepairSelection(["glm-5.1-cloud"], string.Empty, setByApp: string.Empty, addedHosted));
    }

    [Fact]
    public async Task SilentPollRefreshesStateWithoutFlashingCheckingOrOverwritingActivePull()
    {
        var host = new Host
        {
            Inventory = new OllamaInventory(OllamaServerState.NotListening, [], OllamaFound: true),
        };
        var presenter = new OllamaModelsPresenter(host);
        await presenter.RefreshAsync(null).WaitAsync(Patience);
        Assert.Equal("Not running", presenter.View.Setup.StatusBadge);

        // External launch detected by silent poll: transitions to Running without flashing Checking.
        host.Inventory = Ready(("qwen2.5:3b", 1_900_000_000, "3.1B"));
        var polledUp = await presenter.PollAsync(null).WaitAsync(Patience);
        Assert.NotNull(polledUp);
        Assert.Equal("Running", polledUp!.Setup.StatusBadge);
        Assert.Contains(polledUp.Rows, row => row is { Id: "qwen2.5:3b", Installed: true });

        // Active pull in flight: PollAsync must not inspect or overwrite the active pull progress.
        var pullEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pullRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnPullAsync = async (progress, _) =>
        {
            progress.Report(new OllamaPullUpdate(OllamaPullPhase.Downloading, "layer1", 50, 100));
            pullEntered.SetResult();
            await pullRelease.Task;
            return OllamaPullOutcome.Succeeded;
        };
        var downloadTask = presenter.DownloadAsync("qwen3:0.6b", presenter.Context!, null);
        await pullEntered.Task.WaitAsync(Patience);
        var inspectionsBeforePoll = host.Inspections;

        var pollDuringPull = await presenter.PollAsync(null).WaitAsync(Patience);
        Assert.Null(pollDuringPull);
        Assert.Equal(inspectionsBeforePoll, host.Inspections);
        Assert.Contains(presenter.View.Rows, row => row is { Id: "qwen3:0.6b", Downloading: true });

        pullRelease.SetResult();
        await downloadTask.WaitAsync(Patience);
    }

    [Fact]
    public async Task LocalAndHostedModelsAreGroupedClearlyAndHostedRowsHideRemoveAndSupportOneClickAdd()
    {
        var host = new Host
        {
            Inventory = new OllamaInventory(
                OllamaServerState.Ready,
                [
                    new OllamaInstalledModel("qwen2.5:3b", 1_900_000_000, "3.1B", IsRemote: false),
                    new OllamaInstalledModel("gpt-oss:120b-cloud", null, "120B", IsRemote: true),
                ],
                OllamaFound: true,
                CloudCatalogIds: ["gpt-oss:120b", "glm-5.1", "qwen2.5:3b"]),
        };
        var presenter = new OllamaModelsPresenter(host);

        var view = (await presenter.RefreshAsync(null).WaitAsync(Patience))!;

        Assert.NotEmpty(view.LocalRows);
        Assert.All(view.LocalRows, row =>
        {
            Assert.False(row.IsRemote);
            Assert.Equal(OllamaModelsPresenter.LocalGroupTitle, row.GroupTitle);
        });
        Assert.Equal(3, view.HostedRows.Count);
        Assert.All(view.HostedRows, row =>
        {
            Assert.True(row.IsRemote);
            Assert.Equal(OllamaModelsPresenter.HostedGroupTitle, row.GroupTitle);
        });

        // Installed hosted model deduplicates against catalog "gpt-oss:120b" and hides Remove.
        var installedHosted = Assert.Single(view.HostedRows, row => row.Id == "gpt-oss:120b-cloud");
        Assert.True(installedHosted.Installed);
        Assert.Equal(OllamaRowAction.None, installedHosted.Action);
        Assert.Equal(OllamaCheck.NotOffered, presenter.CheckRemove("gpt-oss:120b-cloud"));

        // Local "qwen2.5:3b" and hosted "qwen2.5:3b" remain distinct in their respective groups.
        Assert.Contains(view.LocalRows, row => row is { Id: "qwen2.5:3b", Installed: true, IsRemote: false });
        Assert.Contains(view.HostedRows, row => row is { Id: "qwen2.5:3b", Installed: false, IsRemote: true, Action: OllamaRowAction.Add });

        // One-click Add on uninstalled hosted model shows "Adding..." and installs the resolved -cloud tag.
        var addViews = new List<OllamaModelsView>();
        host.OnAddHosted = progress =>
        {
            progress.Report(new OllamaPullUpdate(OllamaPullPhase.Starting));
            host.Inventory = new OllamaInventory(
                OllamaServerState.Ready,
                [
                    new OllamaInstalledModel("qwen2.5:3b", 1_900_000_000, "3.1B", IsRemote: false),
                    new OllamaInstalledModel("gpt-oss:120b-cloud", null, "120B", IsRemote: true),
                    new OllamaInstalledModel("glm-5.1-cloud", null, null, IsRemote: true),
                ],
                OllamaFound: true,
                CloudCatalogIds: ["gpt-oss:120b", "glm-5.1", "qwen2.5:3b"]);
            return new OllamaHostedAddResult(OllamaHostedAddOutcome.Succeeded, "glm-5.1-cloud");
        };

        var addChange = await presenter.AddHostedAsync("glm-5.1", presenter.Context!, new Collect(addViews)).WaitAsync(Patience);

        Assert.Contains(
            addViews.SelectMany(v => v.HostedRows),
            row => row is { Id: "glm-5.1", Downloading: true, Action: OllamaRowAction.Stop, ProgressText: "Adding..." });
        Assert.NotNull(addChange);
        Assert.True(addChange!.Downloaded);
        Assert.Equal("glm-5.1-cloud", addChange.Changed);
        Assert.Contains("glm-5.1 is added.", addChange.View.Notice, StringComparison.Ordinal);
        Assert.Contains(addChange.View.HostedRows, row => row is { Id: "glm-5.1-cloud", Installed: true, Action: OllamaRowAction.None });
    }

    [Fact]
    public async Task SetSearchQueryFiltersUninstalledRowsAndRestoresOnClear()
    {
        var host = new Host
        {
            Inventory = new OllamaInventory(
                OllamaServerState.Ready,
                [new OllamaInstalledModel("qwen2.5:3b", 1_900_000_000, "3.1B")],
                OllamaFound: true,
                CloudCatalogIds: ["glm-5.1", "gpt-oss:120b"]),
        };
        var presenter = new OllamaModelsPresenter(host);
        var fullView = (await presenter.RefreshAsync(null).WaitAsync(Patience))!;

        var filtered = presenter.SetSearchQuery("glm-5.1");
        Assert.Equal("glm-5.1", filtered.SearchQuery);
        Assert.Empty(filtered.LocalRows);
        Assert.Single(filtered.HostedRows);
        Assert.Equal("glm-5.1", filtered.HostedRows[0].Id);

        var localFiltered = presenter.SetSearchQuery("Qwen 2.5");
        Assert.Equal(2, localFiltered.LocalRows.Count);
        Assert.Empty(localFiltered.HostedRows);

        var restored = presenter.SetSearchQuery(string.Empty);
        Assert.Equal(fullView.Rows.Count, restored.Rows.Count);
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

        public Action? OnInspect { get; set; }

        public Func<IProgress<OllamaPullUpdate>, OllamaHostedAddResult>? OnAddHosted { get; set; }

        public bool OpenOllamaResult { get; set; } = true;

        public OllamaLaunchSafety LaunchSafety { get; set; } = OllamaLaunchSafety.SafeLocalOnly;

        public int OpenAttempts { get; private set; }

        public int Inspections { get; private set; }

        public int Pulls { get; private set; }

        public int Deletes { get; private set; }

        public Task<OllamaInventory>? NextInspection { get; set; }

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Dictionary<string, OllamaInventory> ByEndpoint { get; } = new(StringComparer.Ordinal);

        public OllamaLaunchSafety CheckLaunchSafety() => LaunchSafety;

        public bool TryOpenOllama()
        {
            OpenAttempts++;
            return OpenOllamaResult;
        }

        public Task<OllamaInventory> InspectAsync(string? endpoint, CancellationToken cancellationToken)
        {
            Inspections++;
            OnInspect?.Invoke();
            if (endpoint is not null && ByEndpoint.TryGetValue(endpoint, out var there))
            {
                return Task.FromResult(there);
            }

            if (NextInspection is { } held)
            {
                NextInspection = null;
                Held.TrySetResult();
                return held;
            }

            return Task.FromResult(Inventory);
        }

        public string? LastPullEndpoint { get; private set; }

        public async Task<OllamaPullOutcome> PullAsync(string? endpoint, string modelId, IProgress<OllamaPullUpdate> progress, CancellationToken cancellationToken)
        {
            Pulls++;
            LastPullEndpoint = endpoint;
            if (OnPullAsync is { } slow)
            {
                return await slow(progress, cancellationToken);
            }

            return OnPull?.Invoke(progress) ?? OllamaPullOutcome.Succeeded;
        }

        public Task<OllamaHostedAddResult> ResolveAndAddHostedModelAsync(string? endpoint, string advertisedId, IProgress<OllamaPullUpdate> progress, CancellationToken cancellationToken) =>
            Task.FromResult(OnAddHosted?.Invoke(progress) ?? new OllamaHostedAddResult(OllamaHostedAddOutcome.Succeeded, $"{advertisedId}-cloud"));

        public Task<OllamaWarmupOutcome> WarmUpAsync(string? endpoint, string modelId, CancellationToken cancellationToken) =>
            Task.FromResult(OllamaWarmupOutcome.Warmed);

        public Task<OllamaDeleteOutcome> DeleteAsync(string? endpoint, string modelId, CancellationToken cancellationToken)
        {
            Deletes++;
            OnDelete?.Invoke();
            return Task.FromResult(OllamaDeleteOutcome.Deleted);
        }
    }
}
