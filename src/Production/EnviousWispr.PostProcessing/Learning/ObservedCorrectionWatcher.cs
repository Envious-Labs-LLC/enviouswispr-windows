using System.Diagnostics;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.PostProcessing.Learning;

/// <summary>
/// Orchestrates one post-paste edit observation session at a time:
/// guards generation/toggle-off/next-dictation, aligns edits, filters candidates,
/// fast-paths repeat fixes via <see cref="RepeatShapeGuard"/>, invokes the active <see cref="ICorrectionJudge"/>,
/// and forwards accepted fixes to <see cref="ILearnedCorrectionCoordinator"/>.
/// </summary>
public sealed class ObservedCorrectionWatcher
{
    public const int MaxJudgeCallsPerPaste = 2;
    public const double WeakEvidenceThreshold = 0.95;

    private sealed record HeldCorrection(
        string PairKey,
        LearnTargetState ExpectedTarget,
        LearnVia Via);

    private sealed class Watch
    {
        public required ulong Generation { get; init; }
        public required PasteCompletedEvent Paste { get; init; }
        public CancellationTokenSource JudgeCts { get; } = new();
        public ulong Revision { get; set; }
        public HashSet<string> SettledSnapshots { get; } = new(StringComparer.Ordinal);
        public int SettledBursts { get; set; }
        public int JudgeCalls { get; set; }
        public HashSet<string> SentPairKeys { get; } = new(StringComparer.Ordinal);
        public HashSet<string> BlockedSeenKeys { get; } = new(StringComparer.Ordinal);
        public List<HeldCorrection> WeakHeld { get; } = [];
        public HashSet<string> WeakRefusedKeys { get; } = new(StringComparer.Ordinal);
        public string? StrongRegion { get; set; }
        public ulong StrongRevision { get; set; }
        public bool Ended { get; set; }
        public bool Cancelled { get; set; }
        public bool IsLive => !Ended && !Cancelled;

        public void CancelJudge()
        {
            try
            {
                JudgeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private readonly Func<bool> _isLearnFromEditsOn;
    private readonly Func<ICorrectionJudge?> _selectJudge;
    private readonly IPastedRegionObserver _observer;
    private readonly Func<IReadOnlyList<CustomWordEntry>> _userWords;
    private readonly Func<IReadOnlyList<CustomWordEntry>> _packTerms;
    private readonly ILearnedCorrectionCoordinator _coordinator;
    private readonly ILearnMemoryStore _memory;
    private readonly IAppLogger? _logger;
    private readonly object _sync = new();

    private Watch? _watch;
    private ulong _generation;

    public ObservedCorrectionWatcher(
        Func<bool> isLearnFromEditsOn,
        Func<ICorrectionJudge?> selectJudge,
        IPastedRegionObserver observer,
        Func<IReadOnlyList<CustomWordEntry>> userWords,
        Func<IReadOnlyList<CustomWordEntry>> packTerms,
        ILearnedCorrectionCoordinator coordinator,
        ILearnMemoryStore memory,
        IAppLogger? logger = null)
    {
        _isLearnFromEditsOn = isLearnFromEditsOn ?? throw new ArgumentNullException(nameof(isLearnFromEditsOn));
        _selectJudge = selectJudge ?? throw new ArgumentNullException(nameof(selectJudge));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _userWords = userWords ?? throw new ArgumentNullException(nameof(userWords));
        _packTerms = packTerms ?? throw new ArgumentNullException(nameof(packTerms));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _logger = logger;
    }

    public bool IsWatching
    {
        get
        {
            lock (_sync)
            {
                return _watch?.IsLive ?? false;
            }
        }
    }

    public void OnPasteCompleted(PasteCompletedEvent paste)
    {
        ArgumentNullException.ThrowIfNull(paste);

        if (!_isLearnFromEditsOn())
        {
            Log(AppEventCode.LearnSkipped);
            return;
        }

        ulong gen;
        lock (_sync)
        {
            if (_watch?.IsLive == true)
            {
                Log(AppEventCode.LearnSkipped);
                return;
            }

            _generation++;
            gen = _generation;
            _watch = new Watch
            {
                Generation = gen,
                Paste = paste,
            };
        }

        _ = Task.Run(() => BeginAsync(gen));
    }

    public void OnRecordingStarted()
    {
        Watch? liveWatch;
        lock (_sync)
        {
            liveWatch = _watch;
            if (liveWatch is null || liveWatch.Cancelled || liveWatch.Ended)
            {
                return;
            }
        }

        if (_observer.IsObserving)
        {
            _observer.Finish(PastedRegionEndReason.NextDictationStarted);
            return;
        }

        lock (_sync)
        {
            if (_watch is { } w && w.Generation == liveWatch.Generation && !w.Ended)
            {
                w.Cancelled = true;
                w.CancelJudge();
            }
        }

        _observer.CancelObservation();
        Log(AppEventCode.LearnObservationEnded);
    }

    public void OnLearnFromEditsChanged(bool isOn)
    {
        if (isOn)
        {
            return;
        }

        ulong? gen = null;
        lock (_sync)
        {
            if (_watch is not null)
            {
                gen = _watch.Generation;
            }
        }

        if (gen is { } g)
        {
            CancelForToggleOff(g);
        }
    }

    private async Task BeginAsync(ulong gen)
    {
        try
        {
            Watch? w;
            lock (_sync)
            {
                w = _watch;
                if (w is null || w.Generation != gen || !w.IsLive)
                {
                    return;
                }
            }

            if (!_isLearnFromEditsOn())
            {
                Skip(gen);
                return;
            }

            var judge = _selectJudge();
            if (judge is null)
            {
                Skip(gen);
                return;
            }

            var caps = await judge.GetCapabilitiesAsync().ConfigureAwait(false);
            if (!caps.CanRunOnThisDevice)
            {
                Skip(gen);
                return;
            }

            lock (_sync)
            {
                if (_watch is null || _watch.Generation != gen || !_watch.IsLive)
                {
                    return;
                }
            }

            if (!_isLearnFromEditsOn())
            {
                Skip(gen);
                return;
            }

            await _observer.StartObservationAsync(
                w.Paste,
                ev => HandleObserverEvent(ev, gen, judge),
                _ => Skip(gen)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            Skip(gen);
        }
    }

    private void Skip(ulong gen)
    {
        lock (_sync)
        {
            if (_watch?.Generation != gen)
            {
                return;
            }

            _watch.Cancelled = true;
            _watch.CancelJudge();
            _watch = null;
        }

        Log(AppEventCode.LearnSkipped);
    }

    private void CancelForToggleOff(ulong gen)
    {
        var shouldLog = false;
        lock (_sync)
        {
            if (_watch is null || _watch.Generation != gen || _watch.Cancelled)
            {
                return;
            }

            _watch.Cancelled = true;
            _watch.CancelJudge();
            if (!_watch.Ended)
            {
                shouldLog = true;
            }
        }

        if (shouldLog)
        {
            _observer.CancelObservation();
            Log(AppEventCode.LearnSkipped);
        }
    }

    private void HandleObserverEvent(PastedRegionEvent ev, ulong gen, ICorrectionJudge judge)
    {
        if (!_isLearnFromEditsOn())
        {
            CancelForToggleOff(gen);
            return;
        }

        switch (ev)
        {
            case PastedRegionEvent.Changed:
                lock (_sync)
                {
                    if (_watch is not null && _watch.Generation == gen && _watch.IsLive)
                    {
                        _watch.Revision++;
                        _watch.StrongRegion = null;
                    }
                }

                break;

            case PastedRegionEvent.Settled settled:
                HandleSettled(settled.Region, settled.Evidence, gen, judge);
                break;

            case PastedRegionEvent.Ended:
                var emitEnd = false;
                lock (_sync)
                {
                    if (_watch is not null && _watch.Generation == gen && !_watch.Ended)
                    {
                        _watch.Ended = true;
                        emitEnd = true;
                    }
                }

                if (emitEnd)
                {
                    Log(AppEventCode.LearnObservationEnded);
                }

                break;
        }
    }

    private void HandleSettled(string region, SettleEvidence evidence, ulong gen, ICorrectionJudge judge)
    {
        PasteCompletedEvent paste = null!;
        ulong revision = 0;
        bool judgeable = false;
        bool alreadySettledSnapshot = false;
        List<(string Original, string Replacement, LearnTargetState Target, LearnVia Via)>? weakSaves = null;
        ulong weakGen = 0;
        ulong weakRevision = 0;

        lock (_sync)
        {
            if (_watch is null || _watch.Generation != gen || !_watch.IsLive)
            {
                return;
            }

            if (evidence == SettleEvidence.Strong)
            {
                if (_watch.WeakHeld.Count > 0 || _watch.WeakRefusedKeys.Count > 0)
                {
                    weakSaves = ReleaseWeakHeldLocked(_watch, region);
                    weakGen = _watch.Generation;
                    weakRevision = _watch.Revision;
                }

                if (_watch.SettledSnapshots.Contains(region))
                {
                    _watch.StrongRegion = region;
                    _watch.StrongRevision = _watch.Revision;
                    alreadySettledSnapshot = true;
                }
            }

            if (!alreadySettledSnapshot)
            {
                if (!_watch.SettledSnapshots.Add(region))
                {
                    alreadySettledSnapshot = true;
                }
                else
                {
                    judgeable = _watch.SettledBursts < MaxJudgeCallsPerPaste;
                    if (judgeable)
                    {
                        _watch.SettledBursts++;
                    }

                    paste = _watch.Paste;
                    revision = _watch.Revision;
                }
            }
        }

        if (weakSaves is { Count: > 0 })
        {
            foreach (var save in weakSaves)
            {
                TriggerSave(save.Original, save.Replacement, save.Target, save.Via, weakGen, weakRevision);
            }
        }

        if (alreadySettledSnapshot)
        {
            return;
        }

        var alignment = EditAlignment.Align(paste.PastedText, region);
        if (alignment.LimitExceeded || alignment.Runs.Count == 0)
        {
            return;
        }

        var inputs = new CorrectionCandidateFilter.Inputs(
            _userWords(),
            _packTerms(),
            _memory.BlockedKeys);
        var filtered = CorrectionCandidateFilter.Filter(alignment.Runs, inputs);

        var judgedPool = new List<CorrectionCandidateFilter.Filtered>();
        var immediateRepeats = new List<(CorrectionCandidateFilter.Filtered Filter, LearnTargetState Target)>();

        lock (_sync)
        {
            if (_watch is null || _watch.Generation != gen || !_watch.IsLive || _watch.Revision != revision)
            {
                return;
            }

            foreach (var item in filtered)
            {
                if (_watch.SentPairKeys.Contains(item.PairKey))
                {
                    continue;
                }

                if (item.Disposition is CorrectionCandidateFilter.Disposition.Ineligible(CorrectionCandidateFilter.IneligibleReason.BlockedByUser))
                {
                    if (_watch.BlockedSeenKeys.Add(item.PairKey))
                    {
                        Log(AppEventCode.LearnBlockedSeen);
                    }

                    continue;
                }

                if (item.Disposition is CorrectionCandidateFilter.Disposition.Candidate(var target))
                {
                    if (_memory.IsRepeat(item.PairKey) &&
                        RepeatShapeGuard.Passes(item.Run.CoreReplacement, item.Run.OriginalStart, paste.PastedText))
                    {
                        _watch.SentPairKeys.Add(item.PairKey);
                        if (evidence == SettleEvidence.Strong)
                        {
                            immediateRepeats.Add((item, target));
                        }
                        else
                        {
                            _watch.WeakHeld.Add(new HeldCorrection(item.PairKey, target, LearnVia.Repeat));
                        }
                    }
                    else
                    {
                        judgedPool.Add(item);
                    }
                }
            }
        }

        foreach (var (item, target) in immediateRepeats)
        {
            TriggerSave(item.Run.CoreOriginal, item.Run.CoreReplacement, target, LearnVia.Repeat, gen, revision);
        }

        if (!judgeable || judgedPool.Count == 0)
        {
            return;
        }

        var prepared = CorrectionCandidateFilter.Prepare(judgedPool);
        if (prepared.Candidates.Count == 0)
        {
            return;
        }

        CancellationToken judgeToken;
        lock (_sync)
        {
            if (_watch is null || _watch.Generation != gen || !_watch.IsLive ||
                _watch.Revision != revision || _watch.JudgeCalls >= MaxJudgeCallsPerPaste)
            {
                return;
            }

            _watch.JudgeCalls++;
            judgeToken = _watch.JudgeCts.Token;
            foreach (var entry in prepared.ById.Values)
            {
                _watch.SentPairKeys.Add(entry.PairKey);
            }
        }

        var context = BuildContextExcerpt(paste.PastedText);
        var request = new CorrectionJudgeRequest(prepared.Candidates, context, paste.LanguageCode);
        _ = Task.Run(() => AskJudgeAsync(judge, request, prepared, region, evidence, gen, revision, judgeToken));
    }

    private async Task AskJudgeAsync(
        ICorrectionJudge judge,
        CorrectionJudgeRequest request,
        CorrectionCandidateFilter.Prepared prepared,
        string region,
        SettleEvidence initialEvidence,
        ulong gen,
        ulong revision,
        CancellationToken judgeToken)
    {
        try
        {
            if (!StillWanted(gen, revision) || judgeToken.IsCancellationRequested)
            {
                return;
            }

            var timer = Stopwatch.StartNew();
            var outcome = await judge.JudgeAsync(request, judgeToken).ConfigureAwait(false);
            timer.Stop();
            if (!StillWanted(gen, revision) || judgeToken.IsCancellationRequested)
            {
                return;
            }

            var evidence = initialEvidence;
            lock (_sync)
            {
                if (initialEvidence == SettleEvidence.Weak &&
                    _watch is { } w &&
                    string.Equals(w.StrongRegion, region, StringComparison.Ordinal) &&
                    w.StrongRevision == revision)
                {
                    evidence = SettleEvidence.Strong;
                }
            }

            var diagnosticProvider = outcome is CorrectionJudgeOutcome.Verdict v
                ? DiagnosticProviderIds.FromProviderId(v.ProviderId)
                : null;
            _logger?.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.LearnJudged,
                ElapsedMilliseconds: timer.ElapsedMilliseconds,
                Provider: diagnosticProvider));
            if (outcome is not CorrectionJudgeOutcome.Verdict(var decisions, _))
            {
                return;
            }

            var saves = new List<(string Original, string Replacement, LearnTargetState Target)>();
            lock (_sync)
            {
                if (_watch is null || _watch.Generation != gen || _watch.Cancelled || _watch.Revision != revision)
                {
                    return;
                }

                foreach (var decision in decisions)
                {
                    if (!prepared.ById.TryGetValue(decision.Id, out var filtered) ||
                        filtered.Disposition is not CorrectionCandidateFilter.Disposition.Candidate(var target))
                    {
                        continue;
                    }

                    if (!decision.Verdict.IsVocabularyCorrection())
                    {
                        if (evidence == SettleEvidence.Strong)
                        {
                            _memory.RecordJudgeRefusal(filtered.PairKey);
                        }
                        else
                        {
                            _watch.WeakRefusedKeys.Add(filtered.PairKey);
                        }

                        continue;
                    }

                    if (Accepts(decision, evidence))
                    {
                        saves.Add((filtered.Run.CoreOriginal, filtered.Run.CoreReplacement, target));
                    }
                    else if (evidence == SettleEvidence.Weak)
                    {
                        _watch.WeakHeld.Add(new HeldCorrection(filtered.PairKey, target, LearnVia.Judge));
                    }
                }
            }

            foreach (var (original, replacement, target) in saves)
            {
                if (!StillWanted(gen, revision))
                {
                    return;
                }

                TriggerSave(original, replacement, target, LearnVia.Judge, gen, revision);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private List<(string Original, string Replacement, LearnTargetState Target, LearnVia Via)> ReleaseWeakHeldLocked(Watch w, string region)
    {
        var alignment = EditAlignment.Align(w.Paste.PastedText, region);
        var filtered = CorrectionCandidateFilter.Filter(
            alignment.Runs,
            new CorrectionCandidateFilter.Inputs(_userWords(), _packTerms(), _memory.BlockedKeys));
        var current = filtered.ToDictionary(f => f.PairKey, f => f, StringComparer.Ordinal);

        foreach (var refusedKey in w.WeakRefusedKeys)
        {
            if (current.ContainsKey(refusedKey))
            {
                _memory.RecordJudgeRefusal(refusedKey);
            }
        }

        w.WeakRefusedKeys.Clear();

        var toSave = new List<(string Original, string Replacement, LearnTargetState Target, LearnVia Via)>();
        foreach (var held in w.WeakHeld)
        {
            if (!current.TryGetValue(held.PairKey, out var item))
            {
                continue;
            }

            if (held.Via == LearnVia.Judge ||
                RepeatShapeGuard.Passes(item.Run.CoreReplacement, item.Run.OriginalStart, w.Paste.PastedText))
            {
                toSave.Add((item.Run.CoreOriginal, item.Run.CoreReplacement, held.ExpectedTarget, held.Via));
            }
        }

        w.WeakHeld.Clear();
        return toSave;
    }

    private void TriggerSave(
        string original,
        string corrected,
        LearnTargetState expectedTarget,
        LearnVia via,
        ulong gen,
        ulong revision)
    {
        _ = Task.Run(async () =>
        {
            var outcome = await _coordinator.LearnAsync(
                original,
                corrected,
                expectedTarget,
                via,
                stillWanted: () => StillWanted(gen, revision)).ConfigureAwait(false);
            if (outcome is LearnedCorrectionOutcome.Blocked)
            {
                var pairKey = CorrectionPairKey.Make(original, corrected);
                var emit = false;
                lock (_sync)
                {
                    if (_watch?.Generation == gen && _watch.BlockedSeenKeys.Add(pairKey))
                    {
                        emit = true;
                    }
                }

                if (emit)
                {
                    Log(AppEventCode.LearnBlockedSeen);
                }
            }
        });
    }

    private bool StillWanted(ulong gen, ulong revision)
    {
        if (!_isLearnFromEditsOn())
        {
            return false;
        }

        lock (_sync)
        {
            return _watch is not null &&
                   _watch.Generation == gen &&
                   !_watch.Cancelled &&
                   _watch.Revision == revision;
        }
    }

    public static bool Accepts(CorrectionJudgeDecision decision, SettleEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!decision.Verdict.IsVocabularyCorrection())
        {
            return false;
        }

        if (evidence == SettleEvidence.Strong || decision.Probability is not { } p)
        {
            return true;
        }

        return double.IsFinite(p) && p >= WeakEvidenceThreshold && p <= 1.0;
    }

    private static string BuildContextExcerpt(string pastedText) =>
        pastedText.Length <= CorrectionJudgeRequest.MaxContextUtf16
            ? pastedText
            : pastedText[..CorrectionJudgeRequest.MaxContextUtf16];

    private void Log(AppEventCode code) =>
        _logger?.Write(new AppLogEntry(DateTimeOffset.UtcNow, code));
}
