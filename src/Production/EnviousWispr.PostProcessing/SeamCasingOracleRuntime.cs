using System.Collections.Concurrent;
using System.Threading.Channels;
using EnviousWispr.Core.Dictation;
using WeCantSpell.Hunspell;

namespace EnviousWispr.PostProcessing;

/// <summary>
/// Thread-safe lazy runtime owner of the 12-language Hunspell dictionaries, ArmB2 context veto,
/// and startup / background serialized warmup without stalling paste.
/// </summary>
public sealed class SeamCasingOracleRuntime : ISeamCasingOracle
{
    private enum LanguagePhase
    {
        Warming,
        Ready,
        Unavailable,
    }

    private sealed class LanguageSlot
    {
        public readonly object Gate = new();
        public LanguagePhase Phase = LanguagePhase.Warming;
        public bool IsLoading;
        public WordList? Dictionary;
        public CaseRepairDecision UnavailableReason = CaseRepairDecision.SkippedOracleWarming;
        public TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static SeamCasingOracleRuntime Shared { get; } = new();

    private readonly ConcurrentDictionary<string, LanguageSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _warmupQueue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _warmupStartLock = new();
    private readonly Lazy<ArmBContextVeto?> _armB;
    private readonly Func<string, WordList?> _dictionaryLoader;
    private bool _warmupLoopStarted;
    private ISeamCasingOracle? _testingOverride;

    public SeamCasingOracleRuntime()
        : this(LoadBundledDictionary, static () => ArmBContextVeto.Shared)
    {
    }

    internal SeamCasingOracleRuntime(
        Func<string, WordList?> dictionaryLoader,
        Func<ArmBContextVeto?> armBProvider)
    {
        _dictionaryLoader = dictionaryLoader;
        _armB = new Lazy<ArmBContextVeto?>(armBProvider, LazyThreadSafetyMode.ExecutionAndPublication);
        NonBlockingOracle = new NonBlockingOracleView(this);
    }

    /// <summary>Non-blocking view for paste callers: triggers background warmup on cold cache and fails closed with <see cref="CaseRepairDecision.SkippedOracleWarming"/>.</summary>
    public ISeamCasingOracle NonBlockingOracle { get; }

    public bool SupportsLanguage(string? languageCode) =>
        _testingOverride?.SupportsLanguage(languageCode) ?? SeamCasingPolicies.PolicyFor(languageCode) is not null;

    public SeamCasingPolicy? PolicyFor(string? languageCode) =>
        _testingOverride?.PolicyFor(languageCode) ?? SeamCasingPolicies.PolicyFor(languageCode);

    public bool IsLanguageReady(string? languageCode)
    {
        var baseCode = SeamCasingPolicies.NormalizeBaseCode(languageCode);
        if (baseCode is null)
        {
            return false;
        }

        if (!_slots.TryGetValue(baseCode, out var slot))
        {
            return false;
        }

        lock (slot.Gate)
        {
            return slot.Phase == LanguagePhase.Ready;
        }
    }

    public void Prewarm()
    {
        _ = _armB.Value;
        _ = LanguageIdentifier.Warm();
        WarmLanguage("en");
    }

    public bool WarmLanguage(string languageCode)
    {
        var baseCode = SeamCasingPolicies.NormalizeBaseCode(languageCode);
        if (baseCode is null || SeamCasingPolicies.PolicyFor(baseCode) is null)
        {
            return false;
        }

        _ = _armB.Value;
        var slot = GetOrQueueSlot(baseCode);
        lock (slot.Gate)
        {
            if (slot.Phase == LanguagePhase.Ready)
            {
                return true;
            }

            if (slot.Phase == LanguagePhase.Unavailable)
            {
                return false;
            }
        }

        LoadIntoSlot(baseCode, slot);
        lock (slot.Gate)
        {
            return slot.Phase == LanguagePhase.Ready;
        }
    }

    public void BeginBackgroundWarmup(string? languageCode)
    {
        var baseCode = SeamCasingPolicies.NormalizeBaseCode(languageCode);
        if (baseCode is null || SeamCasingPolicies.PolicyFor(baseCode) is null)
        {
            return;
        }

        _ = GetOrQueueSlot(baseCode);
    }

    public async Task<bool> WaitForLanguageReadyAsync(
        string? languageCode,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (_testingOverride is not null)
        {
            return true;
        }

        var baseCode = SeamCasingPolicies.NormalizeBaseCode(languageCode);
        if (baseCode is null || SeamCasingPolicies.PolicyFor(baseCode) is null)
        {
            return false;
        }

        var slot = GetOrQueueSlot(baseCode);
        Task<bool> waitTask;
        lock (slot.Gate)
        {
            if (slot.Phase != LanguagePhase.Warming)
            {
                return slot.Phase == LanguagePhase.Ready;
            }

            waitTask = slot.Completion.Task;
        }

        if (timeout == TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                return await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await waitTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public SeamCasingVerdict Evaluate(string word, string leftContext, string payload, string? languageCode)
    {
        if (_testingOverride is { } overrideOracle)
        {
            return overrideOracle.Evaluate(word, leftContext, payload, languageCode);
        }

        return EvaluateCore(word, leftContext, payload, languageCode, allowSynchronousLoad: true);
    }

    internal void InstallForTesting(ISeamCasingOracle oracle)
    {
        ArgumentNullException.ThrowIfNull(oracle);
        _testingOverride = oracle;
    }

    internal void ResetForTesting()
    {
        _testingOverride = null;
    }

    private SeamCasingVerdict EvaluateCore(
        string word,
        string leftContext,
        string payload,
        string? languageCode,
        bool allowSynchronousLoad)
    {
        var baseCode = SeamCasingPolicies.NormalizeBaseCode(languageCode);
        if (baseCode is null || SeamCasingPolicies.PolicyFor(baseCode) is null)
        {
            return SeamCasingVerdict.Keep(CaseRepairDecision.SkippedLanguageNotSupported);
        }

        var armB = _armB.Value;
        if (armB is null || !armB.SupportsLanguage(baseCode))
        {
            return SeamCasingVerdict.Unavailable(CaseRepairDecision.SkippedOracleUnavailable);
        }

        var slot = GetOrQueueSlot(baseCode);
        WordList? dict = null;
        LanguagePhase phase;
        CaseRepairDecision unavailableReason;

        lock (slot.Gate)
        {
            phase = slot.Phase;
            dict = slot.Dictionary;
            unavailableReason = slot.UnavailableReason;
        }

        if (phase == LanguagePhase.Warming && allowSynchronousLoad)
        {
            LoadIntoSlot(baseCode, slot);
            lock (slot.Gate)
            {
                phase = slot.Phase;
                dict = slot.Dictionary;
                unavailableReason = slot.UnavailableReason;
            }
        }

        if (phase == LanguagePhase.Warming)
        {
            return SeamCasingVerdict.Unavailable(CaseRepairDecision.SkippedOracleWarming);
        }

        if (phase == LanguagePhase.Unavailable || dict is null)
        {
            return SeamCasingVerdict.Unavailable(unavailableReason);
        }

        var lower = SeamCasingPolicies.ToLowerLocale(word, baseCode);
        if (!dict.Check(lower))
        {
            return SeamCasingVerdict.Keep(CaseRepairDecision.SkippedNotOrdinaryWord);
        }

        if (armB.ShouldKeepCapital(baseCode, leftContext, payload))
        {
            return SeamCasingVerdict.Keep(CaseRepairDecision.SkippedOracleKeep);
        }

        return SeamCasingVerdict.MayLower;
    }

    private LanguageSlot GetOrQueueSlot(string baseCode)
    {
        var created = false;
        var slot = _slots.GetOrAdd(baseCode, _ =>
        {
            created = true;
            return new LanguageSlot();
        });

        if (created)
        {
            EnsureWarmupLoopStarted();
            _warmupQueue.Writer.TryWrite(baseCode);
        }

        return slot;
    }

    private void EnsureWarmupLoopStarted()
    {
        lock (_warmupStartLock)
        {
            if (_warmupLoopStarted)
            {
                return;
            }

            _warmupLoopStarted = true;
        }

        _ = Task.Run(async () =>
        {
            await foreach (var lang in _warmupQueue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (_slots.TryGetValue(lang, out var slot))
                {
                    _ = _armB.Value;
                    LoadIntoSlot(lang, slot);
                }
            }
        });
    }

    private void LoadIntoSlot(string baseCode, LanguageSlot slot)
    {
        Task<bool>? existingLoad = null;
        lock (slot.Gate)
        {
            if (slot.Phase != LanguagePhase.Warming)
            {
                return;
            }

            if (slot.IsLoading)
            {
                existingLoad = slot.Completion.Task;
            }
            else
            {
                slot.IsLoading = true;
            }
        }

        if (existingLoad is not null)
        {
            _ = existingLoad.GetAwaiter().GetResult();
            return;
        }

        WordList? loadedDictionary = null;
        var ready = false;
        try
        {
            var wordList = _dictionaryLoader(baseCode);
            if (wordList is not null && ValidateSentinel(wordList))
            {
                loadedDictionary = wordList;
                ready = true;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            ready = false;
        }

        lock (slot.Gate)
        {
            if (slot.Phase == LanguagePhase.Warming)
            {
                if (ready && loadedDictionary is not null)
                {
                    slot.Dictionary = loadedDictionary;
                    slot.Phase = LanguagePhase.Ready;
                }
                else
                {
                    slot.Phase = LanguagePhase.Unavailable;
                    slot.UnavailableReason = CaseRepairDecision.SkippedOracleUnavailable;
                }
            }

            slot.IsLoading = false;
        }

        slot.Completion.TrySetResult(ready);
    }

    private static bool ValidateSentinel(WordList wordList)
    {
        // Pure-alpha per-load sentinel: digits can fail open in some spell engines, while pure
        // lowercase nonce strings verify the dictionary rejects garbage rather than answering yes to all inputs.
        var suffix = Random.Shared.Next(0, 1 << 24);
        Span<char> chars = stackalloc char[14];
        "zqx".CopyTo(chars);
        for (var i = 0; i < 8; i++)
        {
            chars[3 + i] = (char)('a' + ((suffix >> (i * 3)) & 0x7) + (i % 13));
        }

        "vkj".CopyTo(chars[11..]);
        var sentinel = new string(chars);
        return !wordList.Check(sentinel);
    }

    private static WordList? LoadBundledDictionary(string baseCode)
    {
        using var dicStream = CasingResources.TryOpenBrotliDecompressedStream($"{baseCode}.dic.br");
        using var affStream = CasingResources.TryOpenBrotliDecompressedStream($"{baseCode}.aff.br");
        if (dicStream is null || affStream is null)
        {
            return null;
        }

        return WordList.CreateFromStreams(dicStream, affStream);
    }

    private sealed class NonBlockingOracleView(SeamCasingOracleRuntime runtime) : ISeamCasingOracle
    {
        public bool SupportsLanguage(string? languageCode) =>
            runtime.SupportsLanguage(languageCode);

        public SeamCasingPolicy? PolicyFor(string? languageCode) =>
            runtime.PolicyFor(languageCode);

        public SeamCasingVerdict Evaluate(string word, string leftContext, string payload, string? languageCode) =>
            runtime._testingOverride?.Evaluate(word, leftContext, payload, languageCode)
            ?? runtime.EvaluateCore(word, leftContext, payload, languageCode, allowSynchronousLoad: false);
    }
}
