using System.Globalization;
using EnviousWispr.Core.Polish;

namespace EnviousWispr.Presentation;

/// <summary>What answered at the Ollama endpoint.</summary>
public enum OllamaServerState
{
    Ready,
    NoModels,

    /// <summary>Nothing is listening: the one state in which starting Ollama may be offered.</summary>
    NotListening,

    /// <summary>Something is there and slow, or answering wrongly. Never grounds to start a second server.</summary>
    NotResponding,
    EndpointInvalid,
}

/// <summary>Whether the app may start Ollama itself, and if not, why not.</summary>
/// <remarks>
/// STARTING OLLAMA IS GUARDED BECAUSE ITS DESKTOP APP IS NOT GENTLE. On start it cleans up earlier servers and can
/// terminate one another tool started; it merges its own settings into the server's environment; and a child of an
/// elevated process runs elevated. So the app starts it only in the plain case - the default endpoint, nothing
/// listening, no Ollama process already there, not elevated, OLLAMA_HOST unset or on this PC - and otherwise says
/// what to do by hand. Ref: #213 design review.
/// </remarks>
public enum OllamaStartability
{
    Startable,

    /// <summary>No launcher where the Windows installer puts it or on PATH. Not proof it is not installed.</summary>
    LauncherNotFound,
    CustomEndpoint,

    /// <summary>OLLAMA_HOST would make the server listen beyond this PC.</summary>
    ExposedHost,
    Elevated,

    /// <summary>An Ollama process exists but the endpoint does not answer; starting another would fight it.</summary>
    AlreadyRunning,
}

public sealed record OllamaInstalledModel(string Id, long? SizeBytes, string? ParameterSize);

/// <summary>One look at Ollama: what answered, what is installed, and whether the app may start it.</summary>
public sealed record OllamaInventory(
    OllamaServerState Server,
    IReadOnlyList<OllamaInstalledModel> Models,
    OllamaStartability Start);

public enum OllamaStartOutcome
{
    Started,

    /// <summary>The guards said no when asked again right before launching.</summary>
    NotAllowed,
    LaunchFailed,

    /// <summary>Launched, and nothing answered in time.</summary>
    NoAnswer,
}

/// <summary>The app's side of the Ollama models block: the wire client, the launcher, and the log. Core types only.</summary>
public interface IOllamaModelHost
{
    /// <summary>The model the running polish provider uses this launch, or null. It cannot be removed from the page.</summary>
    string? ActiveModelId { get; }

    Task<OllamaInventory> InspectAsync(string? endpoint, CancellationToken cancellationToken);

    Task<OllamaStartOutcome> StartAsync(string? endpoint, CancellationToken cancellationToken);

    Task<OllamaPullOutcome> PullAsync(string? endpoint, string modelId, IProgress<OllamaPullUpdate> progress, CancellationToken cancellationToken);

    Task<OllamaDeleteOutcome> DeleteAsync(string? endpoint, string modelId, CancellationToken cancellationToken);
}

public enum OllamaSetupAction
{
    None,
    DownloadOllama,
    StartOllama,
    DownloadRecommended,
}

/// <summary>The block's one line about Ollama itself, and the one thing to do about it.</summary>
public sealed record OllamaSetupView(string Sentence, OllamaSetupAction Action, string? ActionLabel, bool ShowsModels);

public enum OllamaRowAction
{
    None,
    Download,
    Remove,
    Stop,
}

/// <summary>One model on the page.</summary>
/// <param name="ActionName">What a screen reader says for the button: the verb and the model, never the verb alone.</param>
/// <param name="Progress">0 to 1 while this model downloads, null otherwise, and null while Ollama has not said how big it is.</param>
public sealed record OllamaModelRow(
    string Id,
    string Name,
    OllamaModelVerdict Verdict,
    string VerdictLabel,
    string Note,
    string Detail,
    bool Installed,
    OllamaRowAction Action,
    string? ActionLabel,
    string? ActionName,
    bool ActionEnabled,
    bool Downloading,
    double? Progress,
    string? ProgressText);

public sealed record OllamaModelsView(OllamaSetupView Setup, IReadOnlyList<OllamaModelRow> Rows, string? Notice);

public enum OllamaCheck
{
    Proceed,

    /// <summary>Not recommended: the person is asked first, as macOS asks.</summary>
    Confirm,

    /// <summary>Another download, removal or start is running.</summary>
    Busy,

    /// <summary>Not one of the catalogue's models: the page offers nothing for it.</summary>
    NotOffered,

    /// <summary>The running polish provider uses it.</summary>
    InUse,
}

/// <summary>How a download or removal went, and the page as it stands after it.</summary>
/// <param name="Changed">The model the change was about, when it happened; the picker repairs against it.</param>
public sealed record OllamaModelChange(OllamaModelsView View, string? Changed, bool Downloaded, bool Removed);

/// <summary>The Ollama models block of the AI Polish page: setup, the catalogue, downloads and removals. Ref: #213.</summary>
/// <remarks>
/// ONE CHANGE AT A TIME. A download, a removal and a start each take the block for their whole run, and anything
/// asked for meanwhile is Busy - as on macOS, where every row's button is disabled while one download runs. Each
/// runs inside the presentation's admission, so the exit stops it and waits, and each ends by looking at Ollama
/// again: a stopped download may have finished anyway, and the list shows what Ollama has, not what was hoped.
///
/// THE WINDOW RENDERS, THIS DECIDES. Every sentence, label and ordering is made here; the window draws a view and
/// wires its buttons to these methods. Calls are made from the window's thread; progress arrives on whatever thread
/// the host reports it and is folded in under a lock.
/// </remarks>
public sealed class OllamaModelsPresenter
{
    private readonly IOllamaModelHost _host;
    private readonly PresentationAdmission _admission;
    private readonly object _lock = new();
    private readonly Dictionary<string, (long Completed, long Total)> _layers = new(StringComparer.Ordinal);
    private OllamaInventory? _inventory;
    private string? _notice;
    private string? _downloading;
    private OllamaPullPhase _phase;
    private bool _quiet;
    private CancellationTokenSource? _change;
    private int _inspections;

    public OllamaModelsPresenter(IOllamaModelHost host, PresentationAdmission? admission = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _admission = admission ?? new PresentationAdmission();
    }

    /// <summary>The block as it stands.</summary>
    public OllamaModelsView View
    {
        get
        {
            lock (_lock)
            {
                return Render();
            }
        }
    }

    /// <summary>Looks at Ollama again. Null when a later look overtook this one, or the window is closing.</summary>
    public async Task<OllamaModelsView?> RefreshAsync(string? endpoint)
    {
        if (!_admission.TryEnter(out var lease))
        {
            return null;
        }

        using (lease)
        {
            var ticket = Interlocked.Increment(ref _inspections);
            try
            {
                var inventory = await _host.InspectAsync(endpoint, lease.Closing).ConfigureAwait(false);
                lock (_lock)
                {
                    if (ticket != _inspections)
                    {
                        return null;
                    }

                    _inventory = inventory;
                    return Render();
                }
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    /// <summary>Whether a download may start now, and whether to ask the person first.</summary>
    public OllamaCheck CheckDownload(string modelId)
    {
        lock (_lock)
        {
            if (_change is not null)
            {
                return OllamaCheck.Busy;
            }

            return OllamaModelCatalog.Offered(modelId) is not { } entry
                ? OllamaCheck.NotOffered
                : entry.Verdict == OllamaModelVerdict.NotRecommended ? OllamaCheck.Confirm : OllamaCheck.Proceed;
        }
    }

    /// <summary>The confirmation for a model that failed every test, in macOS's words.</summary>
    public static (string Title, string Message, string Proceed) NotRecommendedQuestion(string modelId) =>
        ("This model did not pass any of our cleanup tests.",
         $"{NameOf(modelId)} failed every dictation cleanup test we ran. You can still download it.",
         "Download anyway");

    /// <summary>Whether a removal may start now; only a catalogue model the running provider does not use.</summary>
    public OllamaCheck CheckRemove(string modelId)
    {
        lock (_lock)
        {
            if (_change is not null)
            {
                return OllamaCheck.Busy;
            }

            if (OllamaModelCatalog.Offered(modelId) is null)
            {
                return OllamaCheck.NotOffered;
            }

            return OllamaModelCatalog.SameModel(_host.ActiveModelId, modelId) ? OllamaCheck.InUse : OllamaCheck.Confirm;
        }
    }

    /// <summary>
    /// The removal question. It names what else loses the model and promises no particular amount of space back:
    /// Ollama keeps layers another model shares.
    /// </summary>
    public static (string Title, string Message, string Proceed) RemoveQuestion(string modelId) =>
        ($"Remove {NameOf(modelId)}?",
         "It is removed from Ollama on this PC, so other apps that use it lose it too. You can download it again later.",
         "Remove");

    /// <summary>Downloads a catalogue model. Null when refused (busy, closing, not offered).</summary>
    public async Task<OllamaModelChange?> DownloadAsync(string? endpoint, string modelId, IProgress<OllamaModelsView>? progress)
    {
        if (OllamaModelCatalog.Offered(modelId) is null || !_admission.TryEnter(out var lease))
        {
            return null;
        }

        using (lease)
        {
            if (!TryBegin(out var change))
            {
                return null;
            }

            return await DownloadInsideAsync(endpoint, modelId, progress, lease, change).ConfigureAwait(false);
        }
    }

    private async Task<OllamaModelChange?> DownloadInsideAsync(
        string? endpoint,
        string modelId,
        IProgress<OllamaModelsView>? progress,
        PresentationAdmission.Lease lease,
        CancellationTokenSource change)
    {
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(lease.Closing, change.Token))
        {
            lock (_lock)
            {
                _downloading = modelId;
                _phase = OllamaPullPhase.Starting;
                _quiet = false;
                _layers.Clear();
                _notice = null;
            }

            progress?.Report(View);
            OllamaPullOutcome outcome;
            try
            {
                outcome = await _host.PullAsync(endpoint, modelId, new FoldIntoView(this, progress), stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                outcome = OllamaPullOutcome.Cancelled;
            }

            lock (_lock)
            {
                _downloading = null;
                _layers.Clear();
            }

            if (lease.Closing.IsCancellationRequested)
            {
                End(change);
                return null;
            }

            // LOOK AGAIN WHATEVER HAPPENED. A stopped download may have finished in the moment it was stopped, and a
            // succeeded one is only on the list once Ollama lists it.
            var after = await InspectQuietlyAsync(endpoint, lease.Closing).ConfigureAwait(false);
            var installed = after is not null && after.Models.Any(model => OllamaModelCatalog.SameModel(model.Id, modelId));
            lock (_lock)
            {
                if (after is not null)
                {
                    _inventory = after;
                }

                _notice = DownloadNotice(outcome, modelId, refreshed: after is not null);
                End(change);
                return new OllamaModelChange(Render(), modelId, Downloaded: installed, Removed: false);
            }
        }
    }

    /// <summary>Stops the download in flight, if there is one. Ollama may keep what it has, to resume next time.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_downloading is not null)
            {
                _change?.Cancel();
            }
        }
    }

    /// <summary>Removes a catalogue model, after the window asked. Null when refused.</summary>
    public async Task<OllamaModelChange?> RemoveAsync(string? endpoint, string modelId)
    {
        if (CheckRemove(modelId) != OllamaCheck.Confirm || !_admission.TryEnter(out var lease))
        {
            return null;
        }

        using (lease)
        {
            if (!TryBegin(out var change))
            {
                return null;
            }

            OllamaDeleteOutcome outcome;
            try
            {
                outcome = await _host.DeleteAsync(endpoint, modelId, lease.Closing).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                End(change);
                return null;
            }

            var after = await InspectQuietlyAsync(endpoint, lease.Closing).ConfigureAwait(false);
            var gone = after is not null && !after.Models.Any(model => OllamaModelCatalog.SameModel(model.Id, modelId));
            lock (_lock)
            {
                if (after is not null)
                {
                    _inventory = after;
                }

                _notice = outcome switch
                {
                    OllamaDeleteOutcome.Deleted or OllamaDeleteOutcome.NotFound => after is null
                        ? $"{NameOf(modelId)} was removed. The list could not be refreshed; choose Check again."
                        : $"{NameOf(modelId)} was removed.",
                    OllamaDeleteOutcome.ServerUnavailable => "Ollama stopped answering. Start it and try again.",
                    _ => $"Ollama could not remove {NameOf(modelId)}. Try again, or remove it in Ollama.",
                };
                End(change);
                return new OllamaModelChange(Render(), modelId, Downloaded: false, Removed: gone);
            }
        }
    }

    /// <summary>Starts Ollama, when the guards allow it; then looks again. Null when refused.</summary>
    public async Task<OllamaModelsView?> StartAsync(string? endpoint)
    {
        lock (_lock)
        {
            if (_inventory is not { Server: OllamaServerState.NotListening, Start: OllamaStartability.Startable })
            {
                return null;
            }
        }

        if (!_admission.TryEnter(out var lease))
        {
            return null;
        }

        using (lease)
        {
            if (!TryBegin(out var change))
            {
                return null;
            }

            lock (_lock)
            {
                _notice = "Starting Ollama...";
            }

            OllamaStartOutcome outcome;
            try
            {
                outcome = await _host.StartAsync(endpoint, lease.Closing).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                End(change);
                return null;
            }

            var after = await InspectQuietlyAsync(endpoint, lease.Closing).ConfigureAwait(false);
            lock (_lock)
            {
                if (after is not null)
                {
                    _inventory = after;
                }

                _notice = outcome switch
                {
                    OllamaStartOutcome.Started => null,
                    OllamaStartOutcome.NoAnswer => "Ollama was started but has not answered yet. Wait a moment, then choose Check again.",
                    OllamaStartOutcome.NotAllowed => null,
                    _ => "Couldn't start Ollama. Start it from the Start menu, then choose Check again.",
                };
                End(change);
                return Render();
            }
        }
    }

    /// <summary>
    /// What the model field should say after a download or a removal, or null to leave it as it is. Made only from a
    /// listing that succeeded, and it never overwrites a choice the person made, valid or not.
    /// </summary>
    /// <param name="installed">The picker's fresh listing of installed models.</param>
    /// <param name="field">The model field as it reads now.</param>
    public static string? RepairSelection(IReadOnlyList<string> installed, string field, OllamaModelChange change)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(change);
        var current = field.Trim();
        string? Installed(string id) => installed.FirstOrDefault(model => OllamaModelCatalog.SameModel(model, id));
        string? Fallback() =>
            Installed(OllamaModelCatalog.RecommendedModelId) ??
            OllamaModelCatalog.OrderedByVerdict(installed.OrderBy(model => model, StringComparer.OrdinalIgnoreCase), model => model)
                .FirstOrDefault();

        if (change.Downloaded && change.Changed is { } downloaded)
        {
            // ONLY AN EMPTY FIELD IS FILLED, and with the model just downloaded - not whichever sorts first.
            return current.Length == 0 ? Installed(downloaded) ?? Fallback() : null;
        }

        if (change.Removed && change.Changed is { } removed && OllamaModelCatalog.SameModel(current, removed))
        {
            // The field named what is gone. Something that is installed, or empty when nothing is.
            return Fallback() ?? string.Empty;
        }

        return null;
    }

    private bool TryBegin(out CancellationTokenSource change)
    {
        lock (_lock)
        {
            if (_change is not null)
            {
                change = null!;
                return false;
            }

            _change = change = new CancellationTokenSource();
            return true;
        }
    }

    private void End(CancellationTokenSource? change)
    {
        if (change is null)
        {
            return;
        }

        lock (_lock)
        {
            if (ReferenceEquals(_change, change))
            {
                _change = null;
            }
        }

        change.Dispose();
    }

    private async Task<OllamaInventory?> InspectQuietlyAsync(string? endpoint, CancellationToken closing)
    {
        try
        {
            Interlocked.Increment(ref _inspections);
            var inventory = await _host.InspectAsync(endpoint, closing).ConfigureAwait(false);
            return inventory.Server is OllamaServerState.Ready or OllamaServerState.NoModels ? inventory : null;
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested)
        {
            return null;
        }
    }

    private void Fold(OllamaPullUpdate update)
    {
        lock (_lock)
        {
            _phase = update.Phase;
            _quiet = update.Quiet;
            if (update is { Digest: { } digest, Total: > 0 and var total })
            {
                _layers[digest] = (Math.Min(update.Completed ?? 0, total), total);
            }
        }
    }

    private static string DownloadNotice(OllamaPullOutcome outcome, string modelId, bool refreshed) => outcome switch
    {
        OllamaPullOutcome.Succeeded => refreshed
            ? $"{NameOf(modelId)} is downloaded. Choose it above, then Save."
            : $"{NameOf(modelId)} is downloaded. The list could not be refreshed; choose Check again.",
        OllamaPullOutcome.Cancelled => "Download stopped. Downloading again reuses what was already fetched, where Ollama can.",
        OllamaPullOutcome.Interrupted => "Download was interrupted. Try again to pick up where it left off.",
        OllamaPullOutcome.DiskFull => $"Not enough disk space. {NameOf(modelId)} needs about {SizeOf(modelId)} free.",
        OllamaPullOutcome.NetworkFailed => "Download failed. Check your internet connection and try again.",
        OllamaPullOutcome.NotFound => "Ollama could not find that model. Updating Ollama may help.",
        OllamaPullOutcome.ServerUnavailable => "Ollama stopped answering. Start it and try again.",
        OllamaPullOutcome.EndpointInvalid => "The Ollama endpoint must be on this PC.",
        _ => "Ollama could not download that model. Try again, or update Ollama.",
    };

    private OllamaModelsView Render()
    {
        var setup = SetupFor(_inventory);
        if (!setup.ShowsModels || _inventory is null)
        {
            return new OllamaModelsView(setup, [], _notice);
        }

        var busy = _change is not null;
        var installed = OllamaModelCatalog.OrderedByVerdict(
                _inventory.Models.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase),
                model => model.Id)
            .Select(model => InstalledRow(model, busy));
        var suggested = OllamaModelCatalog.OrderedByVerdict(OllamaModelCatalog.Entries, entry => entry.Id)
            .Where(entry => !_inventory.Models.Any(model => OllamaModelCatalog.SameModel(model.Id, entry.Id)))
            .Select(entry => SuggestedRow(entry, busy));
        return new OllamaModelsView(setup, [.. installed, .. suggested], _notice);
    }

    private OllamaModelRow InstalledRow(OllamaInstalledModel model, bool busy)
    {
        var (verdict, note) = OllamaModelCatalog.VerdictFor(model.Id);
        var offered = OllamaModelCatalog.Offered(model.Id);
        var inUse = OllamaModelCatalog.SameModel(_host.ActiveModelId, model.Id);
        var name = offered?.DisplayName ?? model.Id;
        string[] parts =
        [
            .. model.ParameterSize is { Length: > 0 } parameters ? [$"{parameters} parameters"] : Array.Empty<string>(),
            .. model.SizeBytes is { } bytes ? [$"{Bytes(bytes)} on this PC"] : Array.Empty<string>(),
            .. inUse ? ["polishing your dictation now"] : Array.Empty<string>(),
        ];
        var removable = offered is not null && !inUse;
        return new OllamaModelRow(
            model.Id,
            name,
            verdict,
            OllamaModelCatalog.Label(verdict),
            Sentence(note),
            string.Join(" · ", parts),
            Installed: true,
            removable ? OllamaRowAction.Remove : OllamaRowAction.None,
            removable ? "Remove" : null,
            removable ? $"Remove {name}" : null,
            ActionEnabled: removable && !busy,
            Downloading: false,
            Progress: null,
            ProgressText: null);
    }

    private OllamaModelRow SuggestedRow(OllamaCatalogEntry entry, bool busy)
    {
        var detail = $"{entry.Parameters} parameters · {entry.DownloadSize} download";
        if (OllamaModelCatalog.SameModel(_downloading, entry.Id))
        {
            var (progress, text) = DownloadProgress();
            return new OllamaModelRow(
                entry.Id, entry.DisplayName, entry.Verdict, OllamaModelCatalog.Label(entry.Verdict), Sentence(entry.Note), detail,
                Installed: false, OllamaRowAction.Stop, "Stop", $"Stop downloading {entry.DisplayName}",
                ActionEnabled: true, Downloading: true, progress, text);
        }

        return new OllamaModelRow(
            entry.Id, entry.DisplayName, entry.Verdict, OllamaModelCatalog.Label(entry.Verdict), Sentence(entry.Note), detail,
            Installed: false, OllamaRowAction.Download, "Download", $"Download {entry.DisplayName}",
            ActionEnabled: !busy && _inventory?.Server is OllamaServerState.Ready or OllamaServerState.NoModels,
            Downloading: false, Progress: null, ProgressText: null);
    }

    private (double? Progress, string Text) DownloadProgress()
    {
        long completed = 0;
        long total = 0;
        foreach (var (done, size) in _layers.Values)
        {
            completed += done;
            total += size;
        }

        double? fraction = total > 0 ? Math.Clamp(completed / (double)total, 0, 1) : null;
        var text = _phase switch
        {
            _ when _quiet && _phase is OllamaPullPhase.Verifying or OllamaPullPhase.Finishing =>
                "Still checking the download. Large models take a while.",
            _ when _quiet => "Still working. Ollama has not reported progress for a minute.",
            OllamaPullPhase.Verifying => "Checking the download...",
            OllamaPullPhase.Finishing => "Finishing...",
            OllamaPullPhase.Downloading when fraction is { } value =>
                $"Downloading... {Math.Floor(value * 100).ToString(CultureInfo.CurrentCulture)}%",
            _ => "Starting download...",
        };
        return (fraction, text);
    }

    private static OllamaSetupView SetupFor(OllamaInventory? inventory)
    {
        var recommended = OllamaModelCatalog.Offered(OllamaModelCatalog.RecommendedModelId)!;
        return inventory switch
        {
            null => new("Checking Ollama...", OllamaSetupAction.None, null, ShowsModels: false),
            { Server: OllamaServerState.EndpointInvalid } =>
                new("The Ollama endpoint must be on this PC, such as http://127.0.0.1:11434.", OllamaSetupAction.None, null, false),
            { Server: OllamaServerState.NotResponding } =>
                new("Ollama isn't responding at this endpoint. Check that it's running, then choose Check again.", OllamaSetupAction.None, null, false),
            { Server: OllamaServerState.NotListening, Start: OllamaStartability.Startable } =>
                new("Ollama is installed but isn't running yet.", OllamaSetupAction.StartOllama, "Start Ollama", false),
            { Server: OllamaServerState.NotListening, Start: OllamaStartability.LauncherNotFound } =>
                new("EnviousWispr couldn't find Ollama on this PC. Ollama runs AI models on your PC: no API keys, completely free. After installing it, choose Check again.",
                    OllamaSetupAction.DownloadOllama, "Download Ollama", false),
            { Server: OllamaServerState.NotListening, Start: OllamaStartability.ExposedHost } =>
                new("Ollama isn't running. Your OLLAMA_HOST setting would make it listen beyond this PC, so EnviousWispr won't start it for you. Start it yourself, then choose Check again.",
                    OllamaSetupAction.None, null, false),
            { Server: OllamaServerState.NotListening, Start: OllamaStartability.Elevated } =>
                new("Ollama isn't running. EnviousWispr is running as administrator, so it won't start Ollama for you. Start it yourself, then choose Check again.",
                    OllamaSetupAction.None, null, false),
            { Server: OllamaServerState.NotListening, Start: OllamaStartability.AlreadyRunning } =>
                new("Ollama is running but not answering at this endpoint. Restart Ollama, then choose Check again.", OllamaSetupAction.None, null, false),
            { Server: OllamaServerState.NotListening } =>
                new("Nothing is answering at this endpoint. Start Ollama there yourself, then choose Check again.", OllamaSetupAction.None, null, false),
            { Server: OllamaServerState.NoModels } =>
                new($"Ollama needs a language model to polish your text. {recommended.DisplayName} did best in our tests: about {recommended.DownloadSize.TrimStart('~')}, and it runs entirely on your PC.",
                    OllamaSetupAction.DownloadRecommended, $"Download {recommended.DisplayName}", true),
            _ => new(
                inventory.Models.Count == 1 ? "Ollama is running with 1 model on this PC." : $"Ollama is running with {inventory.Models.Count.ToString(CultureInfo.CurrentCulture)} models on this PC.",
                OllamaSetupAction.None, null, true),
        };
    }

    /// <summary>macOS writes the notes to follow a dash; here each stands on its own line, so it starts as a sentence.</summary>
    private static string Sentence(string note) =>
        note.Length == 0 ? note : string.Concat(char.ToUpper(note[0], CultureInfo.CurrentCulture).ToString(), note.AsSpan(1));

    private static string NameOf(string modelId) => OllamaModelCatalog.Offered(modelId)?.DisplayName ?? modelId;

    private static string SizeOf(string modelId) =>
        OllamaModelCatalog.Offered(modelId)?.DownloadSize.TrimStart('~') ?? "a few GB";

    private static string Bytes(long bytes) => bytes >= 1_000_000_000
        ? $"{(bytes / 1e9).ToString("0.0", CultureInfo.CurrentCulture)} GB"
        : $"{Math.Max(1, bytes / 1_000_000).ToString(CultureInfo.CurrentCulture)} MB";

    /// <summary>Folds each progress line in, then hands the window the block as it now stands.</summary>
    private sealed class FoldIntoView(OllamaModelsPresenter owner, IProgress<OllamaModelsView>? view) : IProgress<OllamaPullUpdate>
    {
        public void Report(OllamaPullUpdate value)
        {
            owner.Fold(value);
            view?.Report(owner.View);
        }
    }
}
