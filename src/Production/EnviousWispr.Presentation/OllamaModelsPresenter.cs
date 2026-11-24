using System.Globalization;
using EnviousWispr.Core.Polish;

namespace EnviousWispr.Presentation;

/// <summary>What answered at the Ollama endpoint.</summary>
public enum OllamaServerState
{
    Ready,
    NoModels,
    Starting,

    /// <summary>Nothing is listening at the endpoint.</summary>
    NotListening,

    /// <summary>Something is there and slow, or answering wrongly.</summary>
    NotResponding,
    EndpointInvalid,
}

public sealed record OllamaInstalledModel(
    string Id,
    long? SizeBytes,
    string? ParameterSize,
    bool IsRemote = false,
    bool? SupportsThinking = null);

/// <summary>One look at Ollama: what answered, what is installed, whether its app was found on this PC, and advertised cloud models.</summary>
public sealed record OllamaInventory(
    OllamaServerState Server,
    IReadOnlyList<OllamaInstalledModel> Models,
    bool OllamaFound,
    IReadOnlyList<string>? CloudCatalogIds = null);

/// <summary>The app's side of the Ollama models block: the wire client, the launcher, and the log. Core types only.</summary>
public interface IOllamaModelHost
{
    /// <summary>The model the running polish provider uses this launch, or null. It cannot be removed from the page.</summary>
    string? ActiveModelId { get; }

    Task<OllamaInventory> InspectAsync(string? endpoint, CancellationToken cancellationToken);

    Task<OllamaPullOutcome> PullAsync(string? endpoint, string modelId, IProgress<OllamaPullUpdate> progress, CancellationToken cancellationToken);

    Task<OllamaDeleteOutcome> DeleteAsync(string? endpoint, string modelId, CancellationToken cancellationToken);

    OllamaLaunchSafety CheckLaunchSafety() => OllamaLaunchSafety.SafeLocalOnly;

    bool TryOpenOllama() => false;

    Task<OllamaHostedAddResult> ResolveAndAddHostedModelAsync(
        string? endpoint,
        string advertisedId,
        IProgress<OllamaPullUpdate> progress,
        CancellationToken cancellationToken) =>
        Task.FromResult(new OllamaHostedAddResult(OllamaHostedAddOutcome.Unreachable));

    Task<OllamaWarmupOutcome> WarmUpAsync(
        string? endpoint,
        string modelId,
        CancellationToken cancellationToken) =>
        Task.FromResult(OllamaWarmupOutcome.SkippedUnknownModel);
}

public enum OllamaSetupAction
{
    None,
    OpenOllama,
    DownloadOllama,
    DownloadRecommended,
}

/// <summary>The block's one line about Ollama itself, and the one thing to do about it.</summary>
public sealed record OllamaSetupView(
    string Sentence,
    OllamaSetupAction Action,
    string? ActionLabel,
    bool ShowsModels,
    string StatusBadge = "Checking");

public enum OllamaRowAction
{
    None,
    Download,
    Add,
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
    string? ProgressText,
    bool IsRemote = false,
    string GroupTitle = OllamaModelsPresenter.LocalGroupTitle);

public sealed record OllamaModelsView(
    OllamaSetupView Setup,
    IReadOnlyList<OllamaModelRow> Rows,
    string? Notice,
    string SearchQuery = "")
{
    public IReadOnlyList<OllamaModelRow> LocalRows =>
        Rows.Where(row => !row.IsRemote).ToArray();

    public IReadOnlyList<OllamaModelRow> HostedRows =>
        Rows.Where(row => row.IsRemote).ToArray();
}

public enum OllamaCheck
{
    Proceed,

    /// <summary>Not recommended: the person is asked first, as macOS asks.</summary>
    Confirm,

    /// <summary>Another download or removal is running.</summary>
    Busy,

    /// <summary>Not one of the catalogue's models: the page offers nothing for it.</summary>
    NotOffered,

    /// <summary>The running polish provider uses it.</summary>
    InUse,
}

/// <summary>Which list a question was asked about: the endpoint it came from, and its version.</summary>
public sealed record OllamaListContext(string? Endpoint, int Version);

/// <summary>How a download or removal went, and the page as it stands after it.</summary>
/// <param name="Changed">The model the change was about; the picker repairs against it.</param>
/// <param name="Endpoint">The endpoint the change was made against. The window repairs the picker only while the page still names it.</param>
public sealed record OllamaModelChange(OllamaModelsView View, string? Changed, bool Downloaded, bool Removed, string? Endpoint);

/// <summary>The Ollama models block of the AI Polish page: setup, the catalogue, downloads, hosted additions, and removals. Ref: #71, #213.</summary>
public sealed class OllamaModelsPresenter
{
    public const string LocalGroupTitle = "On this PC";
    public const string HostedGroupTitle = "Runs on Ollama's servers";
    public const string PrivacySummary = "Local models stay on this PC; hosted models run on Ollama's servers and send transcript text to Ollama.";

    private static readonly TimeSpan DefaultStartupPollDelay = TimeSpan.FromMilliseconds(500);
    private const int DefaultStartupPollAttempts = 20;

    private readonly IOllamaModelHost _host;
    private readonly PresentationAdmission _admission;
    private readonly object _lock = new();
    private readonly Dictionary<string, (long Completed, long Total)> _layers = new(StringComparer.Ordinal);
    private OllamaInventory? _inventory;
    private string? _notice;
    private string? _downloading;
    private bool _downloadingIsHosted;
    private OllamaPullPhase _phase;
    private bool _quiet;
    private bool _starting;
    private string _searchQuery = string.Empty;
    private CancellationTokenSource? _change;
    private int _inspections;
    private int _statusEpoch;

    /// <summary>The endpoint the page last asked about. A look at any other is not made after a change, and not kept.</summary>
    private string? _requested;
    private bool _requestedKnown;

    /// <summary>The endpoint the list on the page came from, set only when a list arrives. Downloads and removals go here.</summary>
    private string? _listEndpoint;
    private bool _listKnown;
    private int _listVersion;

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

    public string SearchQuery
    {
        get
        {
            lock (_lock)
            {
                return _searchQuery;
            }
        }
    }

    /// <summary>
    /// The list on the page, or null while there is none or a look at another endpoint is still out - a moment in which
    /// the rows on screen belong to one server and the page is asking another, so nothing may be changed.
    /// </summary>
    public OllamaListContext? Context
    {
        get
        {
            lock (_lock)
            {
                return Settled() ? new OllamaListContext(_listEndpoint, _listVersion) : null;
            }
        }
    }

    /// <summary>Whether a download, hosted add, removal, or startup poll is running.</summary>
    public bool Changing
    {
        get
        {
            lock (_lock)
            {
                return _change is not null || _starting;
            }
        }
    }

    public OllamaModelsView SetSearchQuery(string? query)
    {
        lock (_lock)
        {
            _searchQuery = query?.Trim() ?? string.Empty;
            return Render();
        }
    }

    /// <summary>
    /// Looks at Ollama at <paramref name="endpoint"/>, which becomes the endpoint the block is about. Null when a later
    /// look overtook this one, or the window is closing. The window draws <see cref="View"/> when it draws, never an
    /// older answer it was handed.
    /// </summary>
    public async Task<OllamaModelsView?> RefreshAsync(string? endpoint)
    {
        if (!_admission.TryEnter(out var lease))
        {
            return null;
        }

        using (lease)
        {
            try
            {
                var (_, current) = await InspectAsync(endpoint, claim: true, lease.Closing).ConfigureAwait(false);
                if (!current)
                {
                    return null;
                }

                lock (_lock)
                {
                    return Render();
                }
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Silent background poll: refreshes Ollama daemon and model state without contending with active pulls, removals,
    /// or startup polls, and without flashing transient loading state. Returns null when skipped or superseded.
    /// </summary>
    public async Task<OllamaModelsView?> PollAsync(string? endpoint)
    {
        if (!_admission.TryEnter(out var lease))
        {
            return null;
        }

        using (lease)
        {
            int ticket;
            int epochBefore;
            lock (_lock)
            {
                if (_change is not null || _starting)
                {
                    return null;
                }

                if (!_requestedKnown)
                {
                    _requested = endpoint;
                    _requestedKnown = true;
                }
                else if (!IsRequested(endpoint))
                {
                    return null;
                }

                ticket = ++_inspections;
                epochBefore = _statusEpoch;
            }

            OllamaInventory inventory;
            try
            {
                inventory = await _host.InspectAsync(endpoint, lease.Closing).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                return null;
            }

            lock (_lock)
            {
                if (ticket != _inspections ||
                    _statusEpoch != epochBefore ||
                    _change is not null ||
                    _starting ||
                    !IsRequested(endpoint))
                {
                    return null;
                }

                var changed = !InventoryEquals(_inventory, inventory);
                _inventory = inventory;
                if (!_listKnown || !string.Equals(_listEndpoint, endpoint, StringComparison.Ordinal))
                {
                    _notice = null;
                    _listEndpoint = endpoint;
                    _listKnown = true;
                    _listVersion++;
                }
                else if (changed)
                {
                    _listVersion++;
                }

                return Render();
            }
        }
    }

    /// <summary>
    /// Launches Ollama when it is installed and not listening, enters transient <see cref="OllamaServerState.Starting"/>,
    /// and runs an epoch-guarded startup poll (default 500 ms x 20 attempts) so the UI transitions automatically to
    /// <see cref="OllamaServerState.NoModels"/> or <see cref="OllamaServerState.Ready"/> as soon as Ollama answers.
    /// </summary>
    public async Task<OllamaModelsView?> OpenOllamaAsync(
        string? endpoint,
        IProgress<OllamaModelsView>? progress = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int maxAttempts = DefaultStartupPollAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAttempts, 0);
        if (!_admission.TryEnter(out var lease))
        {
            return null;
        }

        using (lease)
        {
            int startEpoch;
            lock (_lock)
            {
                if (_starting || _change is not null)
                {
                    return null;
                }

                if (_inventory is { Server: not OllamaServerState.NotListening } ||
                    _inventory is { OllamaFound: false })
                {
                    return null;
                }

                _requested = endpoint;
                _requestedKnown = true;
                _statusEpoch++;
                if (_host.CheckLaunchSafety() != OllamaLaunchSafety.SafeLocalOnly)
                {
                    _notice = "Ollama is configured to expose its server to the network. Turn off network exposure in Ollama or start Ollama from the Start menu, then choose Check again.";
                    return Render();
                }

                if (!_host.TryOpenOllama())
                {
                    _notice = "EnviousWispr couldn't start Ollama automatically. Start Ollama from the Start menu, then choose Check again.";
                    return Render();
                }

                _starting = true;
                _notice = null;
                startEpoch = ++_statusEpoch;
            }

            progress?.Report(View);
            var wait = delay ?? Task.Delay;
            try
            {
                for (var attempt = 0; attempt < maxAttempts; attempt++)
                {
                    await wait(DefaultStartupPollDelay, lease.Closing).ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (_statusEpoch != startEpoch)
                        {
                            return null;
                        }

                        if (_change is not null || !IsRequested(endpoint))
                        {
                            _starting = false;
                            return null;
                        }
                    }

                    var inventory = await _host.InspectAsync(endpoint, lease.Closing).ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (_statusEpoch != startEpoch)
                        {
                            return null;
                        }

                        if (_change is not null || !IsRequested(endpoint))
                        {
                            _starting = false;
                            return null;
                        }

                        if (inventory.Server is OllamaServerState.Ready or OllamaServerState.NoModels or OllamaServerState.EndpointInvalid)
                        {
                            _starting = false;
                            _inventory = inventory;
                            _listEndpoint = endpoint;
                            _listKnown = true;
                            _listVersion++;
                            _statusEpoch++;
                            var readyView = Render();
                            progress?.Report(readyView);
                            return readyView;
                        }
                    }
                }

                lock (_lock)
                {
                    if (_statusEpoch != startEpoch)
                    {
                        return null;
                    }

                    if (_change is not null || !IsRequested(endpoint))
                    {
                        _starting = false;
                        return null;
                    }

                    _starting = false;
                    _notice = "Ollama is taking longer than usual to start. Wait a moment, then choose Check again.";
                    _statusEpoch++;
                    var timeoutView = Render();
                    progress?.Report(timeoutView);
                    return timeoutView;
                }
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                lock (_lock)
                {
                    if (_statusEpoch == startEpoch)
                    {
                        _starting = false;
                    }
                }

                return null;
            }
        }
    }

    /// <summary>
    /// Warms up a local Ollama model in memory ahead of the next dictation. Skips remote and unknown models.
    /// </summary>
    public async Task<OllamaWarmupOutcome> WarmUpAsync(string? endpoint, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return OllamaWarmupOutcome.SkippedUnknownModel;
        }

        if (!_admission.TryEnter(out var lease))
        {
            return OllamaWarmupOutcome.Cancelled;
        }

        using (lease)
        {
            lock (_lock)
            {
                if (_inventory is not { Server: OllamaServerState.Ready } ready)
                {
                    return OllamaWarmupOutcome.SkippedUnknownModel;
                }

                var matched = ready.Models.FirstOrDefault(model => OllamaModelCatalog.SameModel(model.Id, modelId));
                if (matched is null)
                {
                    return OllamaWarmupOutcome.SkippedUnknownModel;
                }

                if (matched.IsRemote)
                {
                    return OllamaWarmupOutcome.SkippedRemote;
                }
            }

            try
            {
                return await _host.WarmUpAsync(endpoint, modelId, lease.Closing).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                return OllamaWarmupOutcome.Cancelled;
            }
        }
    }

    /// <summary>Whether a download may start now, and whether to ask the person first.</summary>
    public OllamaCheck CheckDownload(string modelId)
    {
        lock (_lock)
        {
            if (_change is not null || _starting)
            {
                return OllamaCheck.Busy;
            }

            return OllamaModelCatalog.Offered(modelId) is not { } entry
                ? OllamaCheck.NotOffered
                : entry.Verdict == OllamaModelVerdict.NotRecommended ? OllamaCheck.Confirm : OllamaCheck.Proceed;
        }
    }

    /// <summary>Whether a hosted model Add may start now.</summary>
    public OllamaCheck CheckAddHosted(string advertisedId)
    {
        lock (_lock)
        {
            if (_change is not null || _starting)
            {
                return OllamaCheck.Busy;
            }

            if (string.IsNullOrWhiteSpace(advertisedId))
            {
                return OllamaCheck.NotOffered;
            }

            var offered = _inventory?.CloudCatalogIds?.Any(id => OllamaModelCatalog.SameHostedModel(id, advertisedId)) == true;
            return offered ? OllamaCheck.Proceed : OllamaCheck.NotOffered;
        }
    }

    /// <summary>The confirmation for a model that failed every test, in macOS's words.</summary>
    public static (string Title, string Message, string Proceed) NotRecommendedQuestion(string modelId) =>
        ("This model did not pass any of our cleanup tests.",
         $"{NameOf(modelId)} failed every dictation cleanup test we ran. You can still download it.",
         "Download anyway");

    /// <summary>Whether a removal may start now; only a local catalogue model the running provider does not use.</summary>
    public OllamaCheck CheckRemove(string modelId)
    {
        lock (_lock)
        {
            if (_change is not null || _starting)
            {
                return OllamaCheck.Busy;
            }

            // Hosted models never offer Remove.
            if (_inventory?.Models.FirstOrDefault(model => OllamaModelCatalog.SameModel(model.Id, modelId)) is { IsRemote: true })
            {
                return OllamaCheck.NotOffered;
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

    /// <summary>
    /// Downloads a catalogue model from the endpoint the block's list came from. Null when refused (busy, closing, not
    /// offered, or Ollama never looked at).
    /// </summary>
    public async Task<OllamaModelChange?> DownloadAsync(string modelId, OllamaListContext context, IProgress<OllamaModelsView>? progress)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (OllamaModelCatalog.Offered(modelId) is null || Context != context || !_admission.TryEnter(out var lease))
        {
            return null;
        }

        using (lease)
        {
            if (!TryBegin(context, out var change))
            {
                return null;
            }

            OllamaModelChange? result;
            try
            {
                result = await DownloadInsideAsync(context.Endpoint, modelId, progress, lease, change).ConfigureAwait(false);
            }
            finally
            {
                End(change);
            }

            // DRAWN AGAIN ONCE THE CHANGE IS GIVEN BACK: drawn inside it, every Download button came back disabled.
            return result is null ? null : result with { View = View };
        }
    }

    /// <summary>
    /// Resolves and adds an advertised Ollama cloud model (<c>-cloud</c> / <c>:cloud</c>) at the endpoint the block's
    /// list came from. Null when refused.
    /// </summary>
    public async Task<OllamaModelChange?> AddHostedAsync(string advertisedId, OllamaListContext context, IProgress<OllamaModelsView>? progress)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(advertisedId) || Context != context || !_admission.TryEnter(out var lease))
        {
            return null;
        }

        using (lease)
        {
            if (!TryBegin(context, out var change))
            {
                return null;
            }

            OllamaModelChange? result;
            try
            {
                result = await AddHostedInsideAsync(context.Endpoint, advertisedId.Trim(), progress, lease, change).ConfigureAwait(false);
            }
            finally
            {
                End(change);
            }

            return result is null ? null : result with { View = View };
        }
    }

    private async Task<OllamaModelChange?> DownloadInsideAsync(
        string? endpoint,
        string modelId,
        IProgress<OllamaModelsView>? progress,
        PresentationAdmission.Lease lease,
        CancellationTokenSource change)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lease.Closing, change.Token);
        lock (_lock)
        {
            _downloading = modelId;
            _downloadingIsHosted = false;
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
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or OperationCanceledException))
        {
            // The host answers every ending itself; anything it throws anyway is an ending too, never an escape.
            outcome = OllamaPullOutcome.Refused;
        }
        finally
        {
            lock (_lock)
            {
                _downloading = null;
                _downloadingIsHosted = false;
                _layers.Clear();
            }
        }

        if (lease.Closing.IsCancellationRequested)
        {
            return null;
        }

        // LOOK AGAIN WHATEVER HAPPENED. A stopped download may have finished in the moment it was stopped, and a
        // succeeded one is only on the list once Ollama lists it.
        var (after, _) = await InspectQuietlyAsync(endpoint, lease.Closing).ConfigureAwait(false);
        lock (_lock)
        {
            var installed = _inventory is { Server: OllamaServerState.Ready } now &&
                now.Models.Any(model => !model.IsRemote && OllamaModelCatalog.SameModel(model.Id, modelId));
            if (IsRequested(endpoint))
            {
                // A NOTICE ABOUT ANOTHER SERVER IS NOT SHOWN: the page has moved to a different endpoint since.
                _notice = DownloadNotice(outcome, modelId, refreshed: after is not null);
            }

            return new OllamaModelChange(Render(), modelId, Downloaded: installed, Removed: false, endpoint);
        }
    }

    private async Task<OllamaModelChange?> AddHostedInsideAsync(
        string? endpoint,
        string advertisedId,
        IProgress<OllamaModelsView>? progress,
        PresentationAdmission.Lease lease,
        CancellationTokenSource change)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lease.Closing, change.Token);
        lock (_lock)
        {
            _downloading = advertisedId;
            _downloadingIsHosted = true;
            _phase = OllamaPullPhase.Starting;
            _quiet = false;
            _layers.Clear();
            _notice = null;
        }

        progress?.Report(View);
        OllamaHostedAddResult outcome;
        try
        {
            outcome = await _host.ResolveAndAddHostedModelAsync(endpoint, advertisedId, new FoldIntoView(this, progress), stop.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            outcome = new OllamaHostedAddResult(OllamaHostedAddOutcome.Cancelled);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or OperationCanceledException))
        {
            outcome = new OllamaHostedAddResult(OllamaHostedAddOutcome.PullFailed);
        }
        finally
        {
            lock (_lock)
            {
                _downloading = null;
                _downloadingIsHosted = false;
                _layers.Clear();
            }
        }

        if (lease.Closing.IsCancellationRequested)
        {
            return null;
        }

        var (after, _) = await InspectQuietlyAsync(endpoint, lease.Closing).ConfigureAwait(false);
        lock (_lock)
        {
            var matched = _inventory is { Server: OllamaServerState.Ready } now
                ? now.Models.FirstOrDefault(model =>
                    model.IsRemote &&
                    (OllamaModelCatalog.SameHostedModel(model.Id, advertisedId) ||
                     (outcome.ResolvedModelId is not null && OllamaModelCatalog.SameModel(model.Id, outcome.ResolvedModelId))))
                : null;
            var changedId = matched?.Id ?? outcome.ResolvedModelId ?? advertisedId;
            if (IsRequested(endpoint))
            {
                _notice = HostedAddNotice(outcome.Outcome, advertisedId, refreshed: after is not null);
            }

            return new OllamaModelChange(Render(), changedId, Downloaded: matched is not null, Removed: false, endpoint);
        }
    }

    /// <summary>Stops the download or hosted add in flight, if there is one.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_downloading is not null)
            {
                _change?.Cancel();
            }

            if (_starting)
            {
                _starting = false;
                _statusEpoch++;
            }
        }
    }

    /// <summary>Removes a catalogue model at the endpoint the block's list came from, after the window asked. Null when refused.</summary>
    public async Task<OllamaModelChange?> RemoveAsync(string modelId, OllamaListContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CheckRemove(modelId) != OllamaCheck.Confirm || Context != context || !_admission.TryEnter(out var lease))
        {
            return null;
        }

        using (lease)
        {
            if (!TryBegin(context, out var change))
            {
                return null;
            }

            OllamaModelChange? result;
            try
            {
                result = await RemoveInsideAsync(context.Endpoint, modelId, lease).ConfigureAwait(false);
            }
            finally
            {
                End(change);
            }

            return result is null ? null : result with { View = View };
        }
    }

    private async Task<OllamaModelChange?> RemoveInsideAsync(string? endpoint, string modelId, PresentationAdmission.Lease lease)
    {
        OllamaDeleteOutcome outcome;
        try
        {
            outcome = await _host.DeleteAsync(endpoint, modelId, lease.Closing).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or OperationCanceledException))
        {
            outcome = OllamaDeleteOutcome.Refused;
        }

        var (after, _) = await InspectQuietlyAsync(endpoint, lease.Closing).ConfigureAwait(false);
        lock (_lock)
        {
            var gone = _inventory is { Server: OllamaServerState.Ready or OllamaServerState.NoModels } now &&
                !now.Models.Any(model => OllamaModelCatalog.SameModel(model.Id, modelId));
            _notice = !IsRequested(endpoint) ? _notice : outcome switch
            {
                OllamaDeleteOutcome.Deleted or OllamaDeleteOutcome.NotFound => after is null
                    ? $"{NameOf(modelId)} was removed. The list could not be refreshed; choose Check again."
                    : $"{NameOf(modelId)} was removed.",
                OllamaDeleteOutcome.ServerUnavailable => "Ollama stopped answering. Start it and try again.",
                _ => $"Ollama could not remove {NameOf(modelId)}. Try again, or remove it in Ollama.",
            };
            return new OllamaModelChange(Render(), modelId, Downloaded: false, Removed: gone, endpoint);
        }
    }

    /// <summary>
    /// What the model field should say after a download or a removal, or null to leave it as it is. Made only from a
    /// listing that succeeded (the caller's to check); never overwrites a model that is installed, nor one the person
    /// typed.
    /// </summary>
    public static string? RepairSelection(IReadOnlyList<string> installed, string field, string? setByApp, OllamaModelChange change)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(change);
        var current = field.Trim();
        string? Installed(string id) =>
            installed.FirstOrDefault(model => OllamaModelCatalog.SameModel(model, id));
        string? Fallback() =>
            Installed(OllamaModelCatalog.RecommendedModelId) ??
            OllamaModelCatalog.OrderedByVerdict(
                    installed
                        .Where(model => !OllamaModelCatalog.LooksLikeHostedCloudModel(model))
                        .OrderBy(model => model, StringComparer.OrdinalIgnoreCase),
                    model => model)
                .FirstOrDefault();

        // A FIELD THAT NAMES AN INSTALLED MODEL IS RIGHT, whatever just happened - a removed model reinstalled by
        // another tool before this listing is installed again.
        if (current.Length > 0 && Installed(current) is not null)
        {
            return null;
        }

        // Empty, or still what the app last put there: the app may choose again. Anything else was typed.
        var untouched = current.Length == 0 || OllamaModelCatalog.SameModel(current, setByApp);
        if (!untouched)
        {
            return null;
        }

        if (change.Downloaded && change.Changed is { } downloaded)
        {
            // The model just downloaded - not whichever sorts first.
            return Installed(downloaded) ?? Fallback();
        }

        if (change.Removed && change.Changed is { } removed && OllamaModelCatalog.SameModel(current, removed))
        {
            // The field named what is gone: something installed, or empty when nothing is.
            return Fallback() ?? string.Empty;
        }

        return null;
    }

    private bool TryBegin(OllamaListContext context, out CancellationTokenSource change)
    {
        lock (_lock)
        {
            if (_change is not null || _starting || !Settled() || _listVersion != context.Version)
            {
                change = null!;
                return false;
            }

            _statusEpoch++;
            _change = change = new CancellationTokenSource();
            return true;
        }
    }

    private void End(CancellationTokenSource change)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_change, change))
            {
                _change = null;
                _statusEpoch++;
            }
        }

        change.Dispose();
    }

    private async Task<(OllamaInventory? Inventory, bool Current)> InspectAsync(string? endpoint, bool claim, CancellationToken closing)
    {
        int ticket;
        lock (_lock)
        {
            if (claim)
            {
                _requested = endpoint;
                _requestedKnown = true;
                _starting = false;
                _statusEpoch++;
            }
            else if (!IsRequested(endpoint))
            {
                return (null, false);
            }

            ticket = ++_inspections;
        }

        var inventory = await _host.InspectAsync(endpoint, closing).ConfigureAwait(false);
        lock (_lock)
        {
            if (ticket != _inspections || !IsRequested(endpoint))
            {
                return (inventory, false);
            }

            _inventory = inventory;
            _statusEpoch++;
            if (!_listKnown || !string.Equals(_listEndpoint, endpoint, StringComparison.Ordinal))
            {
                // A NEW SERVER'S LIST STARTS WITHOUT THE OLD ONE'S NEWS.
                _notice = null;
                _listEndpoint = endpoint;
                _listKnown = true;
                _listVersion++;
            }

            return (inventory, true);
        }
    }

    private bool IsRequested(string? endpoint) =>
        _requestedKnown && string.Equals(endpoint, _requested, StringComparison.Ordinal);

    /// <summary>A list is on the page, and it came from the endpoint the page is asking about.</summary>
    private bool Settled() => _listKnown && IsRequested(_listEndpoint);

    /// <summary>The look after a change: null when Ollama did not answer with a list, which the notice then says.</summary>
    private async Task<(OllamaInventory? Inventory, bool Current)> InspectQuietlyAsync(string? endpoint, CancellationToken closing)
    {
        try
        {
            var (inventory, current) = await InspectAsync(endpoint, claim: false, closing).ConfigureAwait(false);
            return (inventory?.Server is OllamaServerState.Ready or OllamaServerState.NoModels ? inventory : null, current);
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested)
        {
            return (null, false);
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

    private static string HostedAddNotice(OllamaHostedAddOutcome outcome, string advertisedId, bool refreshed) => outcome switch
    {
        OllamaHostedAddOutcome.Succeeded => refreshed
            ? $"{advertisedId} is added. Choose it above, then Save."
            : $"{advertisedId} is added. The list could not be refreshed; choose Check again.",
        OllamaHostedAddOutcome.Cancelled => "Adding the model was stopped.",
        OllamaHostedAddOutcome.Ambiguous => "Ollama returned two valid names for this model. Add it in Ollama, then refresh this list.",
        OllamaHostedAddOutcome.NoUsableName => "Ollama listed this model but did not return a name EnviousWispr can add. Try again after updating Ollama.",
        OllamaHostedAddOutcome.Unreachable => "EnviousWispr could not confirm this model's name with Ollama. Try again in a moment.",
        OllamaHostedAddOutcome.ServerUnavailable => "Ollama stopped answering. Start it and try again.",
        OllamaHostedAddOutcome.EndpointInvalid => "The Ollama endpoint must be on this PC.",
        _ => "Couldn't add the model. Check your internet connection and try again.",
    };

    private OllamaModelsView Render()
    {
        var setup = SetupFor(_inventory, _starting);
        if (!setup.ShowsModels || _inventory is null)
        {
            return new OllamaModelsView(setup, [], _notice, _searchQuery);
        }

        var busy = _change is not null || _starting || !Settled();
        var localInstalled = _inventory.Models.Where(model => !model.IsRemote).ToArray();
        var hostedInstalled = _inventory.Models.Where(model => model.IsRemote).ToArray();

        var installedLocalRows = OllamaModelCatalog.OrderedByVerdict(
                localInstalled.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase),
                model => model.Id)
            .Select(model => InstalledLocalRow(model, busy));
        var suggestedLocalRows = OllamaModelCatalog.OrderedByVerdict(OllamaModelCatalog.Entries, entry => entry.Id)
            .Where(entry => !localInstalled.Any(model => OllamaModelCatalog.SameModel(model.Id, entry.Id)))
            .Select(entry => SuggestedLocalRow(entry, busy));

        var installedHostedKeys = hostedInstalled
            .Select(model => OllamaModelCatalog.HostedCatalogKey(model.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installedHostedRows = hostedInstalled
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(InstalledHostedRow);
        var suggestedHostedRows = (_inventory.CloudCatalogIds ?? [])
            .Where(advertisedId => !installedHostedKeys.Contains(OllamaModelCatalog.HostedCatalogKey(advertisedId)))
            .Select(advertisedId => SuggestedHostedRow(advertisedId, busy));

        var allRows = installedLocalRows
            .Concat(suggestedLocalRows)
            .Concat(installedHostedRows)
            .Concat(suggestedHostedRows)
            .Where(row => row.Downloading || OllamaModelCatalog.MatchesSearch(_searchQuery, row.Id, row.Name))
            .ToArray();

        return new OllamaModelsView(setup, allRows, _notice, _searchQuery);
    }

    private OllamaModelRow InstalledLocalRow(OllamaInstalledModel model, bool busy)
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
            ProgressText: null,
            IsRemote: false,
            GroupTitle: LocalGroupTitle);
    }

    private OllamaModelRow SuggestedLocalRow(OllamaCatalogEntry entry, bool busy)
    {
        var detail = $"{entry.Parameters} parameters · {entry.DownloadSize} download";
        if (!_downloadingIsHosted && OllamaModelCatalog.SameModel(_downloading, entry.Id))
        {
            var (progress, text) = DownloadProgress();
            return new OllamaModelRow(
                entry.Id, entry.DisplayName, entry.Verdict, OllamaModelCatalog.Label(entry.Verdict), Sentence(entry.Note), detail,
                Installed: false, OllamaRowAction.Stop, "Stop", $"Stop downloading {entry.DisplayName}",
                ActionEnabled: true, Downloading: true, progress, text,
                IsRemote: false, GroupTitle: LocalGroupTitle);
        }

        return new OllamaModelRow(
            entry.Id, entry.DisplayName, entry.Verdict, OllamaModelCatalog.Label(entry.Verdict), Sentence(entry.Note), detail,
            Installed: false, OllamaRowAction.Download, "Download", $"Download {entry.DisplayName}",
            ActionEnabled: !busy && _inventory?.Server is OllamaServerState.Ready or OllamaServerState.NoModels,
            Downloading: false, Progress: null, ProgressText: null,
            IsRemote: false, GroupTitle: LocalGroupTitle);
    }

    private OllamaModelRow InstalledHostedRow(OllamaInstalledModel model)
    {
        var inUse = OllamaModelCatalog.SameModel(_host.ActiveModelId, model.Id);
        var detail = inUse
            ? "Runs on Ollama's servers · polishing your dictation now"
            : "Installed · runs on Ollama's servers";
        // Hosted rows keep their exact tag name verbatim and never offer Remove. Ref: macOS #1956.
        return new OllamaModelRow(
            model.Id,
            model.Id,
            OllamaModelVerdict.NotTested,
            HostedGroupTitle,
            string.Empty,
            detail,
            Installed: true,
            OllamaRowAction.None,
            ActionLabel: null,
            ActionName: null,
            ActionEnabled: false,
            Downloading: false,
            Progress: null,
            ProgressText: null,
            IsRemote: true,
            GroupTitle: HostedGroupTitle);
    }

    private OllamaModelRow SuggestedHostedRow(string advertisedId, bool busy)
    {
        const string detail = "Runs on Ollama's servers · sends transcript text to Ollama";
        if (_downloadingIsHosted && string.Equals(_downloading, advertisedId, StringComparison.OrdinalIgnoreCase))
        {
            return new OllamaModelRow(
                advertisedId,
                advertisedId,
                OllamaModelVerdict.NotTested,
                HostedGroupTitle,
                string.Empty,
                detail,
                Installed: false,
                OllamaRowAction.Stop,
                "Stop",
                $"Stop adding {advertisedId}",
                ActionEnabled: true,
                Downloading: true,
                Progress: null,
                ProgressText: "Adding...",
                IsRemote: true,
                GroupTitle: HostedGroupTitle);
        }

        return new OllamaModelRow(
            advertisedId,
            advertisedId,
            OllamaModelVerdict.NotTested,
            HostedGroupTitle,
            string.Empty,
            detail,
            Installed: false,
            OllamaRowAction.Add,
            "Add",
            $"Add {advertisedId}",
            ActionEnabled: !busy && _inventory?.Server is OllamaServerState.Ready or OllamaServerState.NoModels,
            Downloading: false,
            Progress: null,
            ProgressText: null,
            IsRemote: true,
            GroupTitle: HostedGroupTitle);
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

    private static OllamaSetupView SetupFor(OllamaInventory? inventory, bool starting)
    {
        if (starting || inventory is { Server: OllamaServerState.Starting })
        {
            return new("Starting Ollama...", OllamaSetupAction.None, null, ShowsModels: false, StatusBadge: "Starting");
        }

        var recommended = OllamaModelCatalog.Offered(OllamaModelCatalog.RecommendedModelId)!;
        return inventory switch
        {
            null => new("Checking Ollama...", OllamaSetupAction.None, null, ShowsModels: false, StatusBadge: "Checking"),
            { Server: OllamaServerState.EndpointInvalid } =>
                new("The Ollama endpoint must be on this PC, such as http://127.0.0.1:11434.", OllamaSetupAction.None, null, false, StatusBadge: "Invalid endpoint"),
            { Server: OllamaServerState.NotResponding } =>
                new("Ollama isn't responding at this endpoint. Check that it's running, then choose Check again.", OllamaSetupAction.None, null, false, StatusBadge: "Not responding"),
            { Server: OllamaServerState.NotListening, OllamaFound: true } =>
                new("Ollama is installed on this PC, but it isn't running. Open Ollama to use local or hosted models.",
                    OllamaSetupAction.OpenOllama, "Open Ollama", false, StatusBadge: "Not running"),
            { Server: OllamaServerState.NotListening } =>
                new("EnviousWispr couldn't find Ollama on this PC. Ollama runs AI models on your PC: no API keys, completely free. After installing and starting it, choose Check again.",
                    OllamaSetupAction.DownloadOllama, "Download Ollama", false, StatusBadge: "Not installed"),
            { Server: OllamaServerState.NoModels } =>
                new($"Ollama needs a language model to polish your text. {recommended.DisplayName} did best in our tests: about {recommended.DownloadSize.TrimStart('~')}, and it runs entirely on your PC.",
                    OllamaSetupAction.DownloadRecommended, $"Download {recommended.DisplayName}", true, StatusBadge: "Running"),
            _ => new(
                ReadySentence(inventory),
                OllamaSetupAction.None, null, true, StatusBadge: "Running"),
        };
    }

    private static string ReadySentence(OllamaInventory inventory)
    {
        var localCount = inventory.Models.Count(model => !model.IsRemote);
        var hostedCount = inventory.Models.Count(model => model.IsRemote);
        if (hostedCount == 0)
        {
            return localCount == 1
                ? "Ollama is running with 1 model on this PC."
                : $"Ollama is running with {localCount.ToString(CultureInfo.CurrentCulture)} models on this PC.";
        }

        if (localCount == 0)
        {
            return hostedCount == 1
                ? "Ollama is running with 1 hosted model on Ollama's servers."
                : $"Ollama is running with {hostedCount.ToString(CultureInfo.CurrentCulture)} hosted models on Ollama's servers.";
        }

        return $"Ollama is running with {localCount.ToString(CultureInfo.CurrentCulture)} local model{(localCount == 1 ? string.Empty : "s")} on this PC and {hostedCount.ToString(CultureInfo.CurrentCulture)} hosted model{(hostedCount == 1 ? string.Empty : "s")} on Ollama's servers.";
    }

    private static bool InventoryEquals(OllamaInventory? left, OllamaInventory? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left.Server != right.Server || left.OllamaFound != right.OllamaFound)
        {
            return false;
        }

        if (!left.Models.SequenceEqual(right.Models))
        {
            return false;
        }

        var leftCloud = left.CloudCatalogIds ?? [];
        var rightCloud = right.CloudCatalogIds ?? [];
        return leftCloud.SequenceEqual(rightCloud, StringComparer.Ordinal);
    }

    private static string NameOf(string modelId) => OllamaModelCatalog.Offered(modelId)?.DisplayName ?? modelId;

    private static string SizeOf(string modelId) =>
        OllamaModelCatalog.Offered(modelId)?.DownloadSize.TrimStart('~') ?? "a few GB";

    /// <summary>macOS writes the notes to follow a dash; here each stands on its own line, so it starts as a sentence.</summary>
    private static string Sentence(string note) =>
        note.Length == 0 ? note : string.Concat(char.ToUpper(note[0], CultureInfo.CurrentCulture).ToString(), note.AsSpan(1));

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
