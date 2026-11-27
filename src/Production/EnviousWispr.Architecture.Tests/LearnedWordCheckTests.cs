using System.Net;
using System.Text;
using System.Text.Json;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;
using EnviousWispr.PostProcessing.Learning;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class LearnedWordCheckTests : IDisposable
{
    private static readonly EgOnePolishOptions DefaultOptions = new(
        new EgOneServerOptions("server.exe", "model.gguf"),
        ModelId: "eg-1-mini");

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "ew-learned-check-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    [Fact]
    public void CustomWordCorrectorExcludesLearnedOnlyEntriesAndLearnedAliasesWhenRequested()
    {
        var manualOnly = new CustomWordEntry("manual alias", "ManualWord");
        var mixed = new CustomWordEntry(
            aliases: ["manual mix", "learned mix"],
            replacement: "MixedWord",
            learnedAliases: ["learned mix"],
            learnedAt: new DateTimeOffset(2026, 11, 26, 7, 30, 0, TimeSpan.Zero));
        var autoLearned = new CustomWordEntry(
            aliases: ["saurab"],
            replacement: "Saurabh",
            learnedAliases: ["saurab"],
            learnedAt: new DateTimeOffset(2026, 11, 26, 8, 0, 0, TimeSpan.Zero));

        var input = "Use manual alias, manual mix, learned mix, and saurab.";

        var unsplit = CustomWordCorrector.Correct(input, [manualOnly, mixed, autoLearned], packWords: null, excludeLearnedAliases: false);
        Assert.Equal("Use ManualWord, MixedWord, MixedWord, and Saurabh.", unsplit.Text);

        var split = CustomWordCorrector.Correct(input, [manualOnly, mixed, autoLearned], packWords: null, excludeLearnedAliases: true);
        Assert.Equal("Use ManualWord, MixedWord, learned mix, and saurab.", split.Text);

        var previewOff = LivePreviewTextFormatter.Format(
            input,
            wordCorrectionEnabled: true,
            [manualOnly, mixed, autoLearned],
            EnglishSpelling.American,
            excludeLearnedAliases: true);
        Assert.Equal("Use ManualWord, MixedWord, learned mix, and saurab.", previewOff);

        var packEntry = new CustomWordEntry("saurab", "PackWord");
        var reservedForCheck = CustomWordCorrector.Correct(
            "Ask saurab.",
            [autoLearned],
            [packEntry],
            excludeLearnedAliases: true,
            reserveExcludedLearnedAliases: true);
        Assert.Equal("Ask saurab.", reservedForCheck.Text);
    }

    [Fact]
    public void LearnedWordCandidatesExtractsObservedMisspellingsAndBuildsBoundedContextWindow()
    {
        var manual = new CustomWordEntry("manual", "Manual");
        var mixed = new CustomWordEntry(
            aliases: ["handwritten", "learned form"],
            replacement: "Mixed",
            learnedAliases: ["learned form"],
            learnedAt: new DateTimeOffset(2026, 11, 26, 7, 30, 0, TimeSpan.Zero));
        var auto = new CustomWordEntry(
            aliases: ["saurab"],
            replacement: "Saurabh",
            learnedAliases: ["saurab"],
            learnedAt: DateTimeOffset.UtcNow);

        var learned = LearnedWordCandidates.ExtractLearnedWords([manual, mixed, auto]);
        Assert.Equal(2, learned.Count);
        Assert.Equal(["learned form"], learned[0].ObservedMisspellings);
        Assert.Equal(["saurab"], learned[1].ObservedMisspellings);

        var prefix = string.Join(' ', Enumerable.Repeat("filler", 120));
        var longSentence = $"{prefix} please ask saurab about learned form today.";
        Assert.Empty(LearnedWordCandidates.Search(longSentence, learned, searchBudget: TimeSpan.Zero));
        var questions = LearnedWordCandidates.Search(longSentence, learned);
        Assert.Equal(2, questions.Count);
        Assert.All(questions, q =>
        {
            Assert.True(q.ContextText.Length <= LearnedWordCandidates.MaxContextUtf16);
            Assert.Contains(q.OriginalSpan, q.ContextText, StringComparison.Ordinal);
            Assert.Contains(q.Word, q.ContextRewritten, StringComparison.Ordinal);
        });
        Assert.Equal("saurab", questions[0].OriginalSpan);
        Assert.Equal("Saurabh", questions[0].Word);
        Assert.Equal("learned form", questions[1].OriginalSpan);
        Assert.Equal("Mixed", questions[1].Word);
    }

    [Fact]
    public void LearnedWordSpanApplierHandlesLongestContainmentContestedOverlapsAndScoreTieBreak()
    {
        const string text = "Sarab A V met foo bar here.";

        // Longest different-word containment wins ("Sarab A V" over "Sarab").
        var qLong = new LearnedWordCheckQuestion(1, text, 0, 9, 0, text.Length, "Saurabhav");
        var qShort = new LearnedWordCheckQuestion(2, text, 0, 5, 0, text.Length, "Saurabh");
        var resLong = LearnedWordSpanApplier.Apply(
            text,
            [qLong, qShort],
            [new LearnedWordCheckDecision(1, true), new LearnedWordCheckDecision(2, true)]);
        Assert.Equal("Saurabhav met foo bar here.", resLong.Text);
        Assert.Equal(1, resLong.Applied);
        Assert.Equal(0, resLong.Contested);

        // Partial crossing overlap without comparable scores is contested and drops both.
        var qLeft = new LearnedWordCheckQuestion(1, text, 14, 3, 0, text.Length, "Foo"); // "foo"
        var qBoth = new LearnedWordCheckQuestion(2, text, 14, 7, 0, text.Length, "FooBar"); // "foo bar"
        var qPartial = new LearnedWordCheckQuestion(3, text, 16, 5, 0, text.Length, "OBar"); // "o bar"
        var contestedRes = LearnedWordSpanApplier.Apply(
            text,
            [qLeft, qPartial],
            [new LearnedWordCheckDecision(1, true), new LearnedWordCheckDecision(3, true)],
            scoresComparable: false);
        Assert.Equal(text, contestedRes.Text);
        Assert.Equal(0, contestedRes.Applied);
        Assert.Equal(2, contestedRes.Contested);

        // With comparable scores, higher score breaks partial overlap tie cleanly.
        var scoredRes = LearnedWordSpanApplier.Apply(
            text,
            [qLeft, qPartial],
            [new LearnedWordCheckDecision(1, true, 0.9), new LearnedWordCheckDecision(3, true, 0.4)],
            scoresComparable: true);
        Assert.Equal("Sarab A V met Foo bar here.", scoredRes.Text);
        Assert.Equal(1, scoredRes.Applied);
        Assert.Equal(0, scoredRes.Contested);
        _ = qBoth;
    }

    [Fact]
    public async Task EgOneLearnedWordCheckerVerifiesJsonSchemaAndFallsBackToDeterministicWhenUnavailableOrTimedOut()
    {
        var q = new LearnedWordCheckQuestion(1, "Ask saurab and eat sandwich.", 4, 6, 0, 28, "Saurabh");
        var coldRuntime = new FakeRuntime { Endpoint = null };
        using var okHandler = new StubHandler((_, _) => Task.FromResult(
            JsonReply("""{"decisions":[{"id":1,"approved":true}]}""")));
        await using var coldProvider = new EgOnePolishProvider(coldRuntime, DefaultOptions, okHandler);
        var egOneCold = new EgOneLearnedWordChecker(() => coldProvider);
        var fallback = new FallbackLearnedWordChecker(() => egOneCold, new DeterministicLearnedWordChecker());

        // Cold EG-1 -> falls back cleanly with 0 cold-starts.
        var fbResult = await fallback.DecideAsync([q], "en");
        Assert.NotNull(fbResult);
        Assert.True(fbResult!.UsedFallback);
        Assert.Null(fbResult.ProviderId);
        Assert.True(fbResult.Decisions[0].Approved);
        Assert.Equal(0, coldRuntime.EnsureReadyCalls);

        // Warm EG-1 -> returns EG-1 decision directly.
        var warmRuntime = new FakeRuntime();
        string? capturedBody = null;
        using var warmHandler = new StubHandler(async (req, ct) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonReply("```json\n{\"decisions\":[{\"id\":1,\"approved\":false}]}\n```");
        });
        await using var warmProvider = new EgOnePolishProvider(warmRuntime, DefaultOptions, warmHandler);
        var egOneWarm = new EgOneLearnedWordChecker(() => warmProvider);
        var warmFallback = new FallbackLearnedWordChecker(() => egOneWarm, new DeterministicLearnedWordChecker());
        var warmResult = await warmFallback.DecideAsync([q], "en");
        Assert.NotNull(warmResult);
        Assert.False(warmResult!.UsedFallback);
        Assert.Equal(EgOneCorrectionJudge.ProviderId, warmResult.ProviderId);
        Assert.False(warmResult.Decisions[0].Approved);
        Assert.NotNull(capturedBody);
        Assert.Contains("learned_word_check_decisions", capturedBody!, StringComparison.Ordinal);
        Assert.Contains("Dictionary word: Saurabh", capturedBody!, StringComparison.Ordinal);

        // Hung checker hitting step deadline -> LearnedWordCheckStep leaves text unchanged without waiting.
        var neverCompleting = new DelegateChecker(async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            return null;
        });
        var step = new LearnedWordCheckStep(() => neverCompleting, TimeSpan.FromMilliseconds(50));
        var word = new CustomWordEntry(["saurab"], "Saurabh", learnedAliases: ["saurab"], learnedAt: DateTimeOffset.UtcNow);
        var outcome = await step.ProcessAsync("Ask saurab today.", [word], "en");
        Assert.Equal("Ask saurab today.", outcome.Text);
        Assert.Equal(1, outcome.Flagged);
        Assert.Equal(0, outcome.Applied);
        Assert.True(outcome.UsedFallback);
    }

    [Fact]
    public async Task TranscriptFinalizerEnforcesFixLearnedWordsTogglePreservesSnippetsAndLogsPrivacySafeEvent()
    {
        var manual = new CustomWordEntry("account", "AccountManual");
        var learned = new CustomWordEntry(
            aliases: ["proceed"],
            replacement: "ProSeed",
            learnedAliases: ["proceed"],
            learnedAt: DateTimeOffset.UtcNow);
        var snippet = new SnippetEntry("sig", "proceed without changing me");

        var pipeline = new DeterministicTextPipeline();
        var effects = new RecordingEffects();
        var checker = new DeterministicLearnedWordChecker();
        var step = new LearnedWordCheckStep(() => checker);
        var finalizer = new TranscriptFinalizer(
            pipeline,
            new PolishExecutor(new NoAdmission(), effects, () => [manual, learned]),
            effects,
            new SnippetExpansionStage(new SnippetExpander(), TimeProvider.System, () => System.Globalization.CultureInfo.InvariantCulture, _ => Task.FromResult<string?>(null), TimeSpan.FromSeconds(1)),
            step);

        var transcript = new Transcript(
            DictationSessionId.Create(),
            "open account and proceed then backslash sig",
            "parakeet",
            DetectedLanguage: "en");

        // 1. Both WordCorrectionEnabled & FixLearnedWordsEnabled ON -> manual + learned apply, snippet untouched.
        var enabledResult = await finalizer.FinalizeAsync(
            transcript,
            [manual, learned],
            new DeterministicTextOptions(
                WordCorrectionEnabled: true,
                FillerRemovalEnabled: false,
                EmojiFormatterEnabled: false,
                SpokenPunctuationEnabled: false,
                FixLearnedWordsEnabled: true),
            SnippetVocabulary.From(ReusableUserData.Empty.WithSnippets([snippet])),
            polish: null,
            CancellationToken.None);
        Assert.Contains("AccountManual", enabledResult.Processed.Output.Text, StringComparison.Ordinal);
        Assert.Contains("ProSeed", enabledResult.Processed.Output.Text, StringComparison.Ordinal);
        Assert.Contains("proceed without changing me", enabledResult.Processed.Output.Text, StringComparison.Ordinal);
        Assert.Single(effects.CheckedEvents);
        Assert.True(effects.CheckedEvents[0].Changed);

        // 2. FixLearnedWordsEnabled OFF -> manual STILL applies, learned does NOT apply.
        effects.CheckedEvents.Clear();
        var disabledResult = await finalizer.FinalizeAsync(
            transcript,
            [manual, learned],
            new DeterministicTextOptions(
                WordCorrectionEnabled: true,
                FillerRemovalEnabled: false,
                EmojiFormatterEnabled: false,
                SpokenPunctuationEnabled: false,
                FixLearnedWordsEnabled: false),
            SnippetVocabulary.From(ReusableUserData.Empty.WithSnippets([snippet])),
            polish: null,
            CancellationToken.None);
        Assert.Contains("AccountManual", disabledResult.Processed.Output.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ProSeed", disabledResult.Processed.Output.Text, StringComparison.Ordinal);
        Assert.Empty(effects.CheckedEvents);
    }

    [Fact]
    public async Task SettingsV27ToV28AndPortableProfileV20ToV21PreserveAndDefaultFixLearnedWords()
    {
        var dir = CreateTempDir();
        var settingsPath = Path.Combine(dir, "settings.json");
        await new JsonSettingsStore(settingsPath).SaveAsync(AppSettings.Default);
        var currentJson = await File.ReadAllTextAsync(settingsPath);
        var v27Json = currentJson.Replace($"\"schemaVersion\": {AppSettings.CurrentSchemaVersion}", "\"schemaVersion\": 27", StringComparison.Ordinal);
        await File.WriteAllTextAsync(settingsPath, v27Json);

        var loaded = await new JsonSettingsStore(settingsPath).LoadAsync();
        Assert.Equal(SettingsLoadStatus.Migrated, loaded.Status);
        Assert.Equal(27, loaded.SourceSchemaVersion);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.Settings.SchemaVersion);
        Assert.True(loaded.Settings.Preferences.Dictation.FixLearnedWordsEnabled);

        var profilePath = Path.Combine(dir, "profile.json");
        var portableService = new JsonPortableProfileService();
        var exported = await portableService.ExportAsync(
            new PortableProfile(
                PortableProfile.CurrentSchemaVersion,
                UserPreferences.Default,
                ReusableUserData.Empty),
            profilePath);
        Assert.True(exported.Succeeded);
        var profileJson = await File.ReadAllTextAsync(profilePath);
        var v20Json = profileJson.Replace($"\"schemaVersion\": {PortableProfile.CurrentSchemaVersion}", "\"schemaVersion\": 20", StringComparison.Ordinal);
        await File.WriteAllTextAsync(profilePath, v20Json);

        var imported = await portableService.ImportAsync(profilePath);
        Assert.Equal(PortableProfileImportStatus.Imported, imported.Status);
        Assert.Equal(PortableProfile.CurrentSchemaVersion, imported.Profile!.SchemaVersion);
        Assert.True(imported.Profile.Preferences.Dictation.FixLearnedWordsEnabled);
    }

    [Fact]
    public async Task EgOneLearnedWordCheckerReturnsNullWhenResourceAdmissionIsBusy()
    {
        var runtime = new FakeRuntime();
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("should not send when busy"));
        await using var provider = new EgOnePolishProvider(runtime, DefaultOptions, handler);
        using var arbiter = new EnviousWispr.Services.Runtime.RuntimeResourceArbiter();
        var busyLease = await arbiter.AcquireAsync(
            EnviousWispr.Core.Runtime.RuntimeResourceKind.Cpu,
            EnviousWispr.Core.Runtime.RuntimeWorkloadKind.FinalAsr,
            TimeSpan.Zero);
        Assert.True(busyLease.Succeeded);

        var checker = new EgOneLearnedWordChecker(
            () => provider,
            resourceAdmission: arbiter);
        var questions = new[]
        {
            new LearnedWordCheckQuestion(1, "Ask saurab and eat sandwich.", 4, 6, 0, 28, "Saurabh"),
        };

        var result = await checker.DecideAsync(questions, "en");

        Assert.Null(result);
        await busyLease.Lease!.DisposeAsync();
    }

    private static HttpResponseMessage JsonReply(string contentJson)
    {
        var envelope = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    finish_reason = "stop",
                    message = new { content = contentJson },
                },
            },
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class FakeRuntime : IEgOneRuntime
    {
        public EgOneEndpoint? Endpoint { get; set; } = new(18_083, "test-token", 16_384);
        public int EnsureReadyCalls { get; private set; }
        public int? OwnedProcessId => null;
        public int? SleepIdleSeconds => 10;
        public Task<EgOneEndpoint?> EnsureReadyAsync(CancellationToken cancellationToken)
        {
            EnsureReadyCalls++;
            return Task.FromResult(Endpoint);
        }
        public EgOneEndpoint? TryGetReadyEndpoint() => Endpoint;
        public Task<bool> TryPreWakeAsync(CancellationToken cancellationToken) => Task.FromResult(Endpoint is not null);
        public void TerminateImmediately() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responseFactory(request, cancellationToken);
    }

    private sealed class DelegateChecker(
        Func<IReadOnlyList<LearnedWordCheckQuestion>, string?, CancellationToken, Task<LearnedWordCheckResult?>> fn)
        : ILearnedWordChecker
    {
        public string ArmName => "delegate";
        public bool ScoresAreComparable => false;
        public Task<LearnedWordCheckResult?> DecideAsync(
            IReadOnlyList<LearnedWordCheckQuestion> questions,
            string? language,
            CancellationToken cancellationToken = default) =>
            fn(questions, language, cancellationToken);
    }

    private sealed class NoAdmission : EnviousWispr.Core.Runtime.IRuntimeResourceAdmission
    {
        public Task<EnviousWispr.Core.Runtime.RuntimeResourceAcquireResult> AcquireAsync(
            EnviousWispr.Core.Runtime.RuntimeResourceKind resource,
            EnviousWispr.Core.Runtime.RuntimeWorkloadKind workload,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EnviousWispr.Core.Runtime.RuntimeResourceAcquireResult(true, new NoLease()));

        private sealed class NoLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingEffects : ITranscriptFinalizationEffects
    {
        public List<(long ElapsedMs, DiagnosticProvider? Provider, bool Changed)> CheckedEvents { get; } = [];
        public IPolishAttemptEffects Polish => this;
        public void RecordDeterministicProcessingStarted() { }
        public void EmitStageReceipts(IReadOnlyList<DeterministicStageReceipt> receipts, bool emojiRestorationOnly) { }
        public Task SaveRecoveryTextAsync(ProcessedText output, CancellationToken cancellationToken) => Task.CompletedTask;
        public void RecordPolishStarted(string providerId) { }
        public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds) { }
        public void RecordPolishRefused() { }
        public void RecordLearnedWordCheck(long elapsedMilliseconds, DiagnosticProvider? provider, bool changed) =>
            CheckedEvents.Add((elapsedMilliseconds, provider, changed));
        public void RecordDeterministicProcessingFinished(bool degraded, long elapsedMilliseconds, bool empty = false) { }
    }
}
