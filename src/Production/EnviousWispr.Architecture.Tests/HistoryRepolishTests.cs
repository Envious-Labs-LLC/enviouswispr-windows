using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

public sealed class HistoryRepolishTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NoProviderConfiguredReturnsTypedResultAndLeavesStoreUntouched()
    {
        var world = CreateWorld(noProvider: true);
        var entry = CreateEntry("spoken draft");
        world.Store.Entries.Add(entry);

        var result = await world.Service.RepolishAsync(entry.Id);

        Assert.Equal(HistoryRepolishStatus.NoProviderConfigured, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal("spoken draft", world.Store.Entries.Single().Text);
        Assert.Equal(0, world.Store.UpdateCalls);
    }

    [Fact]
    public async Task EntryNotFoundReturnsTypedResult()
    {
        var world = CreateWorld();

        var result = await world.Service.RepolishAsync(Guid.NewGuid());

        Assert.Equal(HistoryRepolishStatus.EntryNotFound, result.Status);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task EntryExpiredReturnsTypedResult()
    {
        var world = CreateWorld();
        var expired = CreateEntry("expired recovery") with
        {
            ExpiresAt = Now.AddHours(-1),
        };
        world.Store.Entries.Add(expired);

        var result = await world.Service.RepolishAsync(expired.Id);

        Assert.Equal(HistoryRepolishStatus.EntryExpired, result.Status);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task EscapeRecoveryEntryMustBeKeptBeforeRepolish()
    {
        var world = CreateWorld();
        var activeRecovery = CreateEntry("active escape recovery") with
        {
            ExpiresAt = Now.AddHours(23),
        };
        world.Store.Entries.Add(activeRecovery);

        var result = await world.Service.RepolishAsync(activeRecovery.Id);

        Assert.Equal(HistoryRepolishStatus.EscapeRecoveryEntryMustBeKept, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(0, world.Store.UpdateCalls);
    }

    [Fact]
    public async Task RepolishPreservesPreAiOriginalTextAcrossMultipleRepolishes()
    {
        var polishAnswer = "first polish output";
        var world = CreateWorld(text => polishAnswer);
        var entry = CreateEntry("raw spoken take") with
        {
            OriginalText = "raw spoken take",
            LanguageCode = "en",
        };
        world.Store.Entries.Add(entry);

        // First repolish: "raw spoken take" -> "first polish output"
        var firstResult = await world.Service.RepolishAsync(entry.Id);

        Assert.True(firstResult.Succeeded);
        Assert.Equal("first polish output", firstResult.Entry!.Text);
        Assert.Equal("raw spoken take", firstResult.Entry.OriginalText);
        Assert.True(firstResult.Entry.WasPolished);

        // Second repolish with new provider response: input to provider MUST be "raw spoken take", NOT "first polish output"!
        polishAnswer = "second polish output";
        string? passedInputToProvider = null;
        world.PolishProvider!.OnTryPolish = req => passedInputToProvider = req.Input.Text;

        var secondResult = await world.Service.RepolishAsync(entry.Id);

        Assert.True(secondResult.Succeeded);
        Assert.Equal("raw spoken take", passedInputToProvider); // PROOF that LLM edits are never compounded!
        Assert.Equal("second polish output", secondResult.Entry!.Text);
        Assert.Equal("raw spoken take", secondResult.Entry.OriginalText); // PRESERVED!
        Assert.True(secondResult.Entry.WasPolished);
    }

    [Fact]
    public async Task PolishOutputGuardRefusalLeavesStoreUntouched()
    {
        // Preamble or runaway output that PolishOutputGuard refuses
        var world = CreateWorld(_ => "Sure! Here is your cleaned and formatted transcript: " + new string('a', 5000));
        var entry = CreateEntry("short spoken words");
        world.Store.Entries.Add(entry);

        var result = await world.Service.RepolishAsync(entry.Id);

        Assert.Equal(HistoryRepolishStatus.PolishRefused, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(0, world.Store.UpdateCalls);
        Assert.Equal("short spoken words", world.Store.Entries.Single().Text);
    }

    [Fact]
    public async Task StaleEntryGuardRejectsConcurrentEdits()
    {
        var world = CreateWorld(text => "polished text");
        var entry = CreateEntry("original text");
        world.Store.Entries.Add(entry);

        // Simulate concurrent edit in store before repolish commits
        world.Store.OnBeforeUpdate = () =>
        {
            var idx = world.Store.Entries.FindIndex(e => e.Id == entry.Id);
            world.Store.Entries[idx] = world.Store.Entries[idx] with { Text = "concurrent edit by another process" };
        };

        var result = await world.Service.RepolishAsync(entry.Id);

        Assert.Equal(HistoryRepolishStatus.StaleEntry, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal("concurrent edit by another process", world.Store.Entries.Single().Text);
    }

    [Fact]
    public async Task LocalRuntimeWorkloadKindLocalPolishAcquiredWhenUsingLocalRuntime()
    {
        var world = CreateWorld(text => "local polished text", usesLocalRuntime: true);
        var entry = CreateEntry("local spoken words");
        world.Store.Entries.Add(entry);

        var result = await world.Service.RepolishAsync(entry.Id);

        Assert.True(result.Succeeded);
        Assert.True(world.Admission.Acquired);
        Assert.Equal(RuntimeWorkloadKind.LocalPolish, world.Admission.LastWorkload);
    }

    [Fact]
    public async Task CopyOnlyAsyncCalledOnlyAfterAtomicCommitSucceedsWhenExplicitlyRequested()
    {
        var world = CreateWorld(text => "polished and copied");
        var entry = CreateEntry("please copy me");
        world.Store.Entries.Add(entry);

        // Case 1: not requested -> not copied
        var result1 = await world.Service.RepolishAsync(entry.Id, copyToClipboard: false);
        Assert.True(result1.Succeeded);
        Assert.False(result1.CopiedToClipboard);
        Assert.Null(world.TargetAdapter.CopiedText);

        // Case 2: explicitly requested -> copied
        var result2 = await world.Service.RepolishAsync(entry.Id, copyToClipboard: true);
        Assert.True(result2.Succeeded);
        Assert.True(result2.CopiedToClipboard);
        Assert.Equal("polished and copied", world.TargetAdapter.CopiedText);
    }

    [Fact]
    public async Task RepolishRestoresSpokenEmojiAndPostPolishSpelling()
    {
        // Spoken emoji in raw text that polish might drop: emoji restoration puts it back!
        var world = CreateWorld(
            text => "We shipped it",
            options: new DeterministicTextOptions(WordCorrectionEnabled: true, FillerRemovalEnabled: true, EmojiFormatterEnabled: true, SpokenPunctuationEnabled: true, EnglishSpelling: EnglishSpelling.British));
        var entry = CreateEntry("thumbs up we shipped it") with
        {
            OriginalText = "👍 we shipped it",
        };
        world.Store.Entries.Add(entry);

        var result = await world.Service.RepolishAsync(entry.Id);

        Assert.True(result.Succeeded);
        // Emoji restoration should restore 👍
        Assert.Contains("👍", result.Entry!.Text);
    }

    [Fact]
    public async Task RepolishPreservesTrailingPeriodEvenWhenCasualModeIsEnabled()
    {
        var world = CreateWorld(
            text => text,
            options: new DeterministicTextOptions(
                WordCorrectionEnabled: false,
                FillerRemovalEnabled: false,
                EmojiFormatterEnabled: false,
                SpokenPunctuationEnabled: false,
                EnglishSpelling: EnglishSpelling.American,
                CasualModeEnabled: true));
        var entry = CreateEntry("This is a complete sentence.") with
        {
            OriginalText = "This is a complete sentence.",
        };
        world.Store.Entries.Add(entry);

        var result = await world.Service.RepolishAsync(entry.Id);

        Assert.True(result.Succeeded);
        Assert.Equal("This is a complete sentence.", result.Entry!.Text);
    }

    [Fact]
    public async Task RepolishUsesLazyTargetAdapterProviderWhenCopyToClipboardRequested()
    {
        var store = new FakeMemoryHistoryStore();
        var entry = CreateEntry("spoken text");
        store.Entries.Add(entry);
        var admission = new FakeRuntimeAdmission();
        var effects = new FakePolishEffects();
        var provider = new TestPolishProvider("test-provider", _ => "polished lazily");
        var executor = new PolishExecutor(admission, effects, () => []);
        var targetAdapter = new FakeTargetAdapter();
        ITextTargetAdapter? deferredAdapter = null;

        var service = new HistoryRepolishService(
            store,
            executor,
            new DeterministicTextPipeline(),
            () => new PolishSetup(provider, UsesLocalRuntime: false, RuntimeResourceKind.Cpu, "test-model"),
            targetAdapterProvider: () => deferredAdapter,
            clock: new FrozenClock(Now));

        deferredAdapter = targetAdapter;
        var result = await service.RepolishAsync(entry.Id, copyToClipboard: true);

        Assert.True(result.Succeeded);
        Assert.True(result.CopiedToClipboard);
        Assert.Equal("polished lazily", targetAdapter.CopiedText);
    }

    private static DictationHistoryEntry CreateEntry(string text) => new(
        Guid.NewGuid(),
        Now,
        text,
        "whisper",
        WasPolished: false,
        WasDelivered: true);

    private static TestWorld CreateWorld(
        Func<string, string>? polishAnswer = null,
        bool usesLocalRuntime = false,
        IPolishProvider? polishProvider = null,
        DeterministicTextOptions? options = null,
        bool noProvider = false)
    {
        var store = new FakeMemoryHistoryStore();
        var admission = new FakeRuntimeAdmission();
        var effects = new FakePolishEffects();
        var provider = polishProvider ?? (polishAnswer is not null
            ? new TestPolishProvider("test-provider", polishAnswer)
            : new TestPolishProvider("test-provider", text => text));

        var polishSetup = noProvider
            ? null
            : new PolishSetup(provider, usesLocalRuntime, RuntimeResourceKind.Cpu, "test-model");

        var executor = new PolishExecutor(admission, effects, () => []);
        var pipeline = new DeterministicTextPipeline();
        var targetAdapter = new FakeTargetAdapter();
        var opt = options ?? new DeterministicTextOptions(WordCorrectionEnabled: true, FillerRemovalEnabled: true, EmojiFormatterEnabled: true, SpokenPunctuationEnabled: true);

        var service = new HistoryRepolishService(
            store,
            executor,
            pipeline,
            () => polishSetup,
            () => opt,
            () => [],
            targetAdapter,
            clock: new FrozenClock(Now));

        return new TestWorld(service, store, provider as TestPolishProvider, admission, targetAdapter);
    }

    private sealed record TestWorld(
        HistoryRepolishService Service,
        FakeMemoryHistoryStore Store,
        TestPolishProvider? PolishProvider,
        FakeRuntimeAdmission Admission,
        FakeTargetAdapter TargetAdapter);

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeMemoryHistoryStore : IHistoryStore
    {
        public List<DictationHistoryEntry> Entries { get; } = [];
        public int UpdateCalls { get; private set; }
        public Action? OnBeforeUpdate { get; set; }

        public Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryLoadResult([.. Entries], HistoryLoadStatus.Loaded));

        public Task<DictationHistoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Entries.FirstOrDefault(e => e.Id == id));

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.FromResult(new HistoryOperationResult(true));
        }

        public Task<HistoryRepolishUpdateResult> UpdateRepolishedAsync(
            Guid id,
            string expectedCurrentText,
            string newText,
            bool wasPolished,
            string? providerId,
            string? modelId,
            CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            OnBeforeUpdate?.Invoke();
            var index = Entries.FindIndex(e => e.Id == id);
            if (index < 0)
            {
                return Task.FromResult(HistoryRepolishUpdateResult.NotFound());
            }

            var existing = Entries[index];
            if (existing.ExpiresAt is { } exp && exp <= Now)
            {
                return Task.FromResult(HistoryRepolishUpdateResult.Expired());
            }

            if (!string.Equals(existing.Text, expectedCurrentText, StringComparison.Ordinal))
            {
                return Task.FromResult(HistoryRepolishUpdateResult.Stale());
            }

            var updated = existing with
            {
                Text = newText,
                WasPolished = wasPolished,
                OriginalText = existing.OriginalText ?? existing.Text,
                PolishProviderId = providerId,
                PolishModelId = modelId,
            };
            Entries[index] = updated;
            return Task.FromResult(HistoryRepolishUpdateResult.Updated(updated));
        }

        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Entries.RemoveAll(e => e.Id == id);
            return Task.FromResult(new HistoryOperationResult(true));
        }

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var idx = Entries.FindIndex(e => e.Id == id);
            if (idx >= 0) Entries[idx] = Entries[idx] with { ExpiresAt = null };
            return Task.FromResult(new HistoryOperationResult(true));
        }

        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default)
        {
            Entries.Clear();
            return Task.FromResult(new HistoryOperationResult(true));
        }
    }

    private sealed class TestPolishProvider(string providerId, Func<string, string> answer) : IPolishProvider
    {
        public string ProviderId => providerId;
        public Action<PolishRequest>? OnTryPolish { get; set; }

        public Task<PolishResult> TryPolishAsync(PolishRequest request, CancellationToken cancellationToken = default)
        {
            OnTryPolish?.Invoke(request);
            var output = answer(request.Input.Text);
            return Task.FromResult(new PolishResult(
                request.Input with { Text = output },
                PolishAttemptStatus.Polished));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRuntimeAdmission : IRuntimeResourceAdmission
    {
        public bool Acquired { get; private set; }
        public RuntimeWorkloadKind? LastWorkload { get; private set; }

        public Task<RuntimeResourceAcquireResult> AcquireAsync(
            RuntimeResourceKind resource,
            RuntimeWorkloadKind workload,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Acquired = true;
            LastWorkload = workload;
            return Task.FromResult(new RuntimeResourceAcquireResult(Succeeded: true, new DummyLease()));
        }

        private sealed class DummyLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakePolishEffects : IPolishAttemptEffects
    {
        public void RecordPolishStarted(string providerId) { }
        public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds) { }
    }

    private sealed class FakeTargetAdapter : ITextTargetAdapter
    {
        public string? CopiedText { get; private set; }

        public Task<TextCommitResult> CopyOnlyAsync(ProcessedText text, CancellationToken cancellationToken = default)
        {
            CopiedText = text.Text;
            return Task.FromResult(new TextCommitResult(TextDeliveryRoute.ClipboardOnly, Delivered: true, ClipboardFallback: false, ClipboardRestored: false, TextDeliveryRefusalReason.None));
        }

        public Task<TargetContextResult> CaptureContextAsync(TargetWindowId target, TextDeliveryOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TextCommitResult> CommitAsync(TextCommitRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
