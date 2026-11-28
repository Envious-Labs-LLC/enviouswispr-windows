using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Pipeline;

public sealed record RecoveryReplayStatusCallbacks(
    Action ShowRecovering,
    Action<int> ShowRecovered,
    Action ShowDiscarded,
    Action ShowReady)
{
    public static RecoveryReplayStatusCallbacks FromStatusSink(Action<DictationStatus> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return new(
            ShowRecovering: () => sink(DictationStatus.Recovering()),
            ShowRecovered: count => sink(DictationStatus.Success(
                count == 1
                    ? "Recovered last recording to History"
                    : $"Recovered {count} recordings to History")),
            ShowDiscarded: () => sink(DictationStatus.Quiet("Last recording recovery discarded")),
            ShowReady: () => sink(DictationStatus.Quiet("Ready")));
    }
}

/// <summary>
/// Replays orphaned DPAPI-encrypted recovery spools sequentially on startup through ASR,
/// <see cref="TranscriptFinalizer"/>, and <see cref="SessionPersistence"/> without auto-pasting,
/// yielding cleanly whenever a live dictation begins or the user clicks Discard.
/// </summary>
public sealed class StartupRecoveryReplayCoordinator : IDisposable
{
    private readonly IRecoverySpoolStore _spoolStore;
    private readonly Func<ITranscriptionEngine?> _engineAccessor;
    private readonly TranscriptFinalizer _finalizer;
    private readonly SessionPersistence _persistence;
    private readonly Func<FinalizationOptions> _optionsAccessor;
    private readonly Func<HistoryPreferences> _historyPreferencesAccessor;
    private readonly Func<DictationPreferences> _dictationPreferencesAccessor;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _clock;
    private readonly RecoveryReplayStatusCallbacks _statusCallbacks;
    private readonly Func<bool> _isLeaving;
    private readonly int _minimumSpokenSamples;
    private readonly Action<DictationPreferences>? _applyLanguageSnapshot;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private string? _activeSpoolPath;
    private CancellationTokenSource? _activeItemCts;
    private string? _discardedPath;
    private int _yieldRequested;
    private volatile bool _isRunning;
    private Task? _activeReplayTask;

    public StartupRecoveryReplayCoordinator(
        IRecoverySpoolStore spoolStore,
        Func<ITranscriptionEngine?> engineAccessor,
        TranscriptFinalizer finalizer,
        SessionPersistence persistence,
        Func<FinalizationOptions> optionsAccessor,
        Func<HistoryPreferences> historyPreferencesAccessor,
        Func<DictationPreferences> dictationPreferencesAccessor,
        IAppLogger logger,
        TimeProvider clock,
        RecoveryReplayStatusCallbacks statusCallbacks,
        Func<bool> isLeaving,
        int minimumSpokenSamples = RecoverySpoolConstants.MinimumSpokenSamples,
        Action<DictationPreferences>? applyLanguageSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(spoolStore);
        ArgumentNullException.ThrowIfNull(engineAccessor);
        ArgumentNullException.ThrowIfNull(finalizer);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ArgumentNullException.ThrowIfNull(historyPreferencesAccessor);
        ArgumentNullException.ThrowIfNull(dictationPreferencesAccessor);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(statusCallbacks);
        ArgumentNullException.ThrowIfNull(isLeaving);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumSpokenSamples);

        _spoolStore = spoolStore;
        _engineAccessor = engineAccessor;
        _finalizer = finalizer;
        _persistence = persistence;
        _optionsAccessor = optionsAccessor;
        _historyPreferencesAccessor = historyPreferencesAccessor;
        _dictationPreferencesAccessor = dictationPreferencesAccessor;
        _logger = logger;
        _clock = clock;
        _statusCallbacks = statusCallbacks;
        _isLeaving = isLeaving;
        _minimumSpokenSamples = minimumSpokenSamples;
        _applyLanguageSnapshot = applyLanguageSnapshot;
    }

    public bool IsRunning => _isRunning;

    public bool IsYielded => Volatile.Read(ref _yieldRequested) != 0;

    public void Dispose() => _runGate.Dispose();

    /// <summary>
    /// Checks for orphaned spools and replays qualifying ones sequentially.
    /// Zero-cost no-op when no orphan spools exist on disk.
    /// </summary>
    public async Task<RecoverySpoolReplaySummary> ReplayPendingSpoolsAsync(CancellationToken cancellationToken = default)
    {
        var initialOrphans = _spoolStore.ListOrphanSpools();
        if (initialOrphans.Count == 0 || _isLeaving() || cancellationToken.IsCancellationRequested)
        {
            return new RecoverySpoolReplaySummary(
                initialOrphans.Count,
                ReplayedCount: 0,
                SavedCount: 0,
                DiscardedCount: 0,
                FailedCount: 0,
                YieldedToLiveDictation: false);
        }

        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var task = ExecuteReplayBatchAsync(cancellationToken);
        Volatile.Write(ref _activeReplayTask, task);
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _activeReplayTask, null);
            _runGate.Release();
        }
    }

    /// <summary>
    /// Requests that startup replay pause immediately (preempting between items cleanly)
    /// so a live user dictation gets priority access to the speech engine without contention.
    /// </summary>
    public async Task YieldForLiveDictationAsync(TimeSpan? waitBudget = null)
    {
        Interlocked.Exchange(ref _yieldRequested, 1);
        CancelActiveItem();
        if (Volatile.Read(ref _activeReplayTask) is { } running && !running.IsCompleted)
        {
            var timeout = waitBudget ?? TimeSpan.FromSeconds(2);
            try
            {
                await running.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Signals a yield flag for callers that check synchronously on hotkey press without awaiting,
    /// cancelling any active recovery ASR item while leaving its encrypted spool intact on disk.
    /// </summary>
    public void RequestYieldBetweenItems()
    {
        Interlocked.Exchange(ref _yieldRequested, 1);
        CancelActiveItem();
    }

    /// <summary>
    /// Discards the currently replaying spool (or next pending spool) immediately on user Discard action.
    /// </summary>
    public async Task<bool> DiscardActiveOrNextAsync()
    {
        var path = Volatile.Read(ref _activeSpoolPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            var orphans = _spoolStore.ListOrphanSpools();
            path = orphans.Count > 0 ? orphans[0].FilePath : null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        Volatile.Write(ref _discardedPath, path);
        CancelActiveItem();
        var deleted = await _spoolStore.DeleteFileAsync(path).ConfigureAwait(false);
        _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.RecoveryTextCleared));
        if (!_isLeaving())
        {
            _statusCallbacks.ShowDiscarded();
        }

        return deleted;
    }

    private async Task<RecoverySpoolReplaySummary> ExecuteReplayBatchAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _yieldRequested, 0);
        Volatile.Write(ref _discardedPath, null);
        var orphans = _spoolStore.ListOrphanSpools();
        if (orphans.Count == 0)
        {
            return new RecoverySpoolReplaySummary(0, 0, 0, 0, 0, false);
        }

        _isRunning = true;
        var replayed = 0;
        var saved = 0;
        var discarded = 0;
        var failed = 0;
        var yielded = false;
        var showedRecoveringPill = false;

        try
        {
            _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.ApplicationRunRecovered));

            foreach (var orphan in orphans)
            {
                if (_isLeaving() || cancellationToken.IsCancellationRequested || IsYielded)
                {
                    yielded = IsYielded;
                    break;
                }

                if (string.Equals(Volatile.Read(ref _discardedPath), orphan.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    discarded++;
                    continue;
                }

                Volatile.Write(ref _activeSpoolPath, orphan.FilePath);
                Volatile.Write(ref _discardedPath, null);

                using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Volatile.Write(ref _activeItemCts, itemCts);

                try
                {
                    var read = await _spoolStore.ReadAsync(orphan.FilePath, itemCts.Token).ConfigureAwait(false);
                    if (WasDiscarded(orphan.FilePath))
                    {
                        await _spoolStore.DeleteFileAsync(orphan.FilePath).ConfigureAwait(false);
                        discarded++;
                        continue;
                    }

                    if (!read.Succeeded || read.Header is null)
                    {
                        await _spoolStore.DeleteFileAsync(orphan.FilePath).ConfigureAwait(false);
                        failed++;
                        continue;
                    }

                    if (read.Samples.Length < _minimumSpokenSamples)
                    {
                        await _spoolStore.DeleteFileAsync(orphan.FilePath).ConfigureAwait(false);
                        continue;
                    }

                    var engine = _engineAccessor();
                    if (engine is null)
                    {
                        // Engine is not ready/installed yet; keep encrypted spool untouched on disk.
                        break;
                    }

                    showedRecoveringPill = true;
                    _statusCallbacks.ShowRecovering();

                    var languagePrefs = _dictationPreferencesAccessor() with
                    {
                        WhisperLanguage = string.IsNullOrWhiteSpace(read.Header.LanguageCode)
                            ? LanguageCatalog.AutomaticCode
                            : read.Header.LanguageCode,
                    };
                    _applyLanguageSnapshot?.Invoke(languagePrefs);

                    var audio = new CapturedAudio(
                        read.Header.SessionId,
                        read.Samples,
                        read.Header.SampleRate,
                        read.Header.Channels);

                    using var scope = DictationScope.Begin(read.Header.SessionId.Value);
                    var transcript = await engine.TranscribeAsync(audio, itemCts.Token).ConfigureAwait(false);
                    if (WasDiscarded(orphan.FilePath))
                    {
                        await _spoolStore.DeleteFileAsync(orphan.FilePath).ConfigureAwait(false);
                        discarded++;
                        continue;
                    }

                    var opts = _optionsAccessor();
                    var finalized = await _finalizer.FinalizeAsync(
                        transcript,
                        opts.CustomWords,
                        opts.TextOptions,
                        opts.Snippets,
                        opts.Polish,
                        targetAppName: null,
                        opts.EnabledVocabularyPackIds,
                        opts.VocabularyPackOverrides,
                        itemCts.Token).ConfigureAwait(false);
                    if (WasDiscarded(orphan.FilePath))
                    {
                        await _spoolStore.DeleteFileAsync(orphan.FilePath).ConfigureAwait(false);
                        discarded++;
                        continue;
                    }

                    replayed++;
                    if (string.IsNullOrWhiteSpace(finalized.Processed.Output.Text))
                    {
                        await _spoolStore.DeleteFileAsync(orphan.FilePath).ConfigureAwait(false);
                        continue;
                    }

                    // NEVER auto-paste recovered startup spools: store in History (or Home recovery if History is off).
                    var language = DeliveryLanguagePolicy.For(transcript) ?? read.Header.LanguageCode;
                    var savedEntryId = await _persistence.SaveHistoryAsync(
                        transcript,
                        finalized.Processed.Output.Text,
                        HistoryWriteIntent.Held(finalized.WasPolished),
                        originalText: finalized.Processed.DeterministicText,
                        languageCode: language,
                        polishProviderId: finalized.WasPolished ? opts.Polish?.Provider.ProviderId : null,
                        polishModelId: finalized.WasPolished ? opts.Polish?.ModelId : null,
                        audio: audio).ConfigureAwait(false);

                    var persisted = savedEntryId is not null;
                    if (!persisted && !_historyPreferencesAccessor().IsEnabled)
                    {
                        await _persistence.SaveRecoveryTextAsync(finalized.Processed.Output, CancellationToken.None).ConfigureAwait(false);
                        _persistence.ShowPendingRecovery();
                        persisted = true;
                    }

                    if (persisted)
                    {
                        await _spoolStore.DeleteFileAsync(orphan.FilePath).ConfigureAwait(false);
                        _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.DictationSessionRecovered));
                        saved++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (OperationCanceledException) when (WasDiscarded(orphan.FilePath))
                {
                    await _spoolStore.DeleteFileAsync(orphan.FilePath).ConfigureAwait(false);
                    discarded++;
                }
                catch (OperationCanceledException) when (IsYielded)
                {
                    // Yielded cleanly mid-item; preserve encrypted spool on disk for subsequent recovery.
                    yielded = true;
                    break;
                }
                catch (OperationCanceledException) when (_isLeaving() || cancellationToken.IsCancellationRequested)
                {
                    // Leaving cleanly; keep encrypted spool intact for next launch.
                    break;
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
                {
                    failed++;
                    _logger.Write(new AppLogEntry(
                        _clock.GetUtcNow(),
                        AppEventCode.DictationTranscriptionFailed,
                        AppFailureCategory.Recovery,
                        ErrorCode: ex is TranscriptionEngineException tee ? tee.Error.Code : AppErrorCode.TranscriptionFailed));
                }
                finally
                {
                    Volatile.Write(ref _activeItemCts, null);
                    Volatile.Write(ref _activeSpoolPath, null);
                }
            }
        }
        finally
        {
            _isRunning = false;
            Volatile.Write(ref _activeItemCts, null);
            Volatile.Write(ref _activeSpoolPath, null);
            if (showedRecoveringPill && !_isLeaving() && !yielded && !cancellationToken.IsCancellationRequested)
            {
                if (saved > 0)
                {
                    _statusCallbacks.ShowRecovered(saved);
                }
                else
                {
                    _statusCallbacks.ShowReady();
                }
            }
        }

        return new RecoverySpoolReplaySummary(
            orphans.Count,
            replayed,
            saved,
            discarded,
            failed,
            yielded);
    }

    private bool WasDiscarded(string filePath) =>
        string.Equals(Volatile.Read(ref _discardedPath), filePath, StringComparison.OrdinalIgnoreCase);

    private void CancelActiveItem()
    {
        var cts = Volatile.Read(ref _activeItemCts);
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
