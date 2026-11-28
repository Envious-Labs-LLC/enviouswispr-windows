using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;
using EnviousWispr.Services.Input;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Targeted regression suite covering Wave 1B back-half heart-path hardening:
/// B9, B10, B2, B1, B14, B5, B3, B11, and B12.
/// </summary>
public sealed class Wave1BHeartHardeningTests
{
    private static readonly DeterministicTextOptions AllOff =
        new(WordCorrectionEnabled: false, FillerRemovalEnabled: false, EmojiFormatterEnabled: false, SpokenPunctuationEnabled: false);

    private static readonly DeterministicTextOptions AllOn =
        new(WordCorrectionEnabled: true, FillerRemovalEnabled: true, EmojiFormatterEnabled: true, SpokenPunctuationEnabled: true);

    [Theory]
    [InlineData("at 3 o'clock", "at 3:00")]
    [InlineData("three o'clock", "3:00")]
    [InlineData("12 o'clock", "12:00")]
    [InlineData("14 o'clock", "14 o'clock")]
    [InlineData("at 3 O\u2019Brien", "at 3 O\u2019Brien")]
    [InlineData("call 5 5 5 oh 1 2 3", "call 555-0123")]
    public void B9ItnPreservesOClockAndApostropheTokensWhileConvertingDigitOh(
        string input,
        string expected)
    {
        Assert.Equal(expected, InverseTextNormalizer.Normalize(input));
    }

    [Fact]
    public void B10FillerRemovalProtectsDigitMeasurementsNewLanguagesAndEnglishVetoUnion()
    {
        Assert.Equal("10 mm", FillerWordRemover.Remove("10 mm", "en"));
        Assert.Equal("10mm", FillerWordRemover.Remove("10mm", "en"));
        Assert.Equal("5-mm", FillerWordRemover.Remove("5-mm", "en"));
        Assert.Equal("200 ah", FillerWordRemover.Remove("200 ah", "en"));
        Assert.Equal("give me 3 copies", FillerWordRemover.Remove("give me 3 um copies", "en"));
        Assert.Equal("computer euh", FillerWordRemover.Remove("computer euh", "fr"));

        Assert.Equal("sim um claro", FillerWordRemover.Remove("sim um ah claro", "pt"));
        Assert.Equal("det \u00E4r bra", FillerWordRemover.Remove("det \u00E4r uh bra", "sv"));
        Assert.Equal("det er bra", FillerWordRemover.Remove("det er uh bra", "sv"));
        Assert.Equal("um in", FillerWordRemover.Remove("um in ah", "sl"));
        Assert.Equal("um i", FillerWordRemover.Remove("um i ah", "hr"));

        Assert.Equal("um er claro bom", FillerWordRemover.Remove("um er claro ah bom", detectedLanguage: null, englishVetoed: true));
    }

    [Fact]
    public async Task B2PipelineResolvesLanguageOnceAndPreventsEnglishRewritesOnFrenchParakeetTake()
    {
        var pipeline = new DeterministicTextPipeline();
        const string spoken = "Pour les couleurs de notre calendrier je voudrais confirmer trois rendez-vous importants thumbs up color.";
        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), spoken, "parakeet-v3", DetectedLanguage: null),
            CustomWords: [],
            Options: new DeterministicTextOptions(
                WordCorrectionEnabled: false,
                FillerRemovalEnabled: true,
                EmojiFormatterEnabled: true,
                SpokenPunctuationEnabled: true,
                EnglishSpelling: EnglishSpelling.British,
                CasualModeEnabled: false));

        var result = await pipeline.ProcessAsync(request);

        Assert.Equal("fr", result.CleanupLanguage);
        Assert.NotNull(result.ItnRoute);
        Assert.Equal(ItnRouteKind.Language, result.ItnRoute!.Kind);
        Assert.Equal("fr", result.ItnRoute.LanguageCode);

        var fillerReceipt = result.Receipts.Single(r => r.Stage == DeterministicTextStage.FillerAndFalseStarts);
        var emojiReceipt = result.Receipts.Single(r => r.Stage == DeterministicTextStage.SpokenEmoji);
        var spellingReceipt = result.Receipts.Single(r => r.Stage == DeterministicTextStage.EnglishSpelling);
        Assert.Equal(DeterministicStageStatus.Completed, fillerReceipt.Status);
        Assert.Equal(DeterministicStageStatus.Skipped, emojiReceipt.Status);
        Assert.Equal(DeterministicStageStatus.Skipped, spellingReceipt.Status);
        Assert.Contains("trois", result.Output.Text, StringComparison.Ordinal);
        Assert.Contains("thumbs up color", result.Output.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task B2PipelineTreatsEnglishAndFrenchWhisperTakesDistinctly()
    {
        var pipeline = new DeterministicTextPipeline();

        var english = await pipeline.ProcessAsync(new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "um check the color", "whisper", DetectedLanguage: "en"),
            CustomWords: [],
            Options: new DeterministicTextOptions(
                WordCorrectionEnabled: false,
                FillerRemovalEnabled: true,
                EmojiFormatterEnabled: false,
                SpokenPunctuationEnabled: false,
                EnglishSpelling: EnglishSpelling.British,
                CasualModeEnabled: false)));

        Assert.Equal("en", english.CleanupLanguage);
        Assert.Equal(ItnRouteKind.English, english.ItnRoute?.Kind);
        Assert.Equal("check the colour", english.Output.Text);

        var french = await pipeline.ProcessAsync(new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "euh trois color", "whisper", DetectedLanguage: "fr"),
            CustomWords: [],
            Options: new DeterministicTextOptions(
                WordCorrectionEnabled: false,
                FillerRemovalEnabled: true,
                EmojiFormatterEnabled: false,
                SpokenPunctuationEnabled: false,
                EnglishSpelling: EnglishSpelling.British,
                CasualModeEnabled: false)));

        Assert.Equal("fr", french.CleanupLanguage);
        Assert.Equal(ItnRouteKind.Language, french.ItnRoute?.Kind);
        Assert.Equal("euh trois color", french.Output.Text);
    }

    [Fact]
    public void B1ItnGateRoutingSpokenSymbolsStartWordsAndStreetAddressesWorkCleanly()
    {
        Assert.Equal("language_vetoed", InverseTextNormalizationGate.SkipReason("en", englishVetoed: true, backendSupportsLid: true));
        Assert.Equal("non_english", InverseTextNormalizationGate.SkipReason("fr", englishVetoed: false, backendSupportsLid: true));
        Assert.Equal("lid_backend_nil", InverseTextNormalizationGate.SkipReason(null, englishVetoed: false, backendSupportsLid: true));
        Assert.Null(InverseTextNormalizationGate.SkipReason(null, englishVetoed: false, backendSupportsLid: false));

        Assert.Equal(ItnRouteKind.English, InverseTextNormalizationGate.ResolveRoute("en", false, true).Kind);
        Assert.Equal("de", InverseTextNormalizationGate.ResolveRoute("de-DE", false, true).LanguageCode);
        var japaneseRoute = InverseTextNormalizationGate.ResolveRoute("ja", false, true);
        Assert.Equal(ItnRouteKind.Neutral, japaneseRoute.Kind);
        Assert.Equal("non_english", japaneseRoute.SkipReason);
        Assert.Equal("lid_backend_nil", InverseTextNormalizationGate.ResolveRoute(null, false, true).SkipReason);

        Assert.Equal("max.mustermann@beispiel.de", InverseTextNormalizer.NormalizeLanguageNeutral("max punkt mustermann at beispiel punkt de"));
        Assert.Equal("www.ejemplo.es", InverseTextNormalizer.NormalizeLanguageNeutral("www punto ejemplo punto es"));
        Assert.Equal("2.5.0", InverseTextNormalizer.NormalizeLanguageNeutral("2 Punkt 5 Punkt 0"));
        Assert.Equal("A-42", InverseTextNormalizer.NormalizeLanguageNeutral("A strich 42"));
        Assert.Equal("2026-04-09", InverseTextNormalizer.NormalizeLanguageNeutral("2026-4-9"));

        var symbolResult = InverseTextNormalizer.Normalize(
            "open paren alpha close paren hash sign beta ampersand gamma_delta slash epsilon backslash snippet",
            spokenPunctuation: true,
            spokenSymbols: true,
            out _);
        Assert.Equal("(alpha) #beta & gamma_delta/epsilon backslash snippet", symbolResult);

        Assert.Equal("Hallo, Welt.", InverseTextNormalizer.ApplyStartWordPunctuation("Hallo Diktiere Komma Welt Diktiere Punkt", "de", "Diktiere").Text);
        Assert.Equal("Bonjour, monde!", InverseTextNormalizer.ApplyStartWordPunctuation("Bonjour Place virgule monde Place point d'exclamation", "fr", "Place").Text);
        Assert.Equal("Hola, mundo.", InverseTextNormalizer.ApplyStartWordPunctuation("Hola A\u00F1ade coma mundo A\u00F1ade punto", "es", "A\u00F1ade").Text);
        Assert.Equal("Ciao, mondo.", InverseTextNormalizer.ApplyStartWordPunctuation("Ciao Scrivi virgola mondo Scrivi punto", "it", "Scrivi").Text);
        Assert.Equal("hello, world.", InverseTextNormalizer.ApplyStartWordPunctuation("hello mark comma world mark period", "en", "mark").Text);

        Assert.Equal("9 High Plains Road, Shelton, Connecticut 06484", InverseTextNormalizer.Normalize("9 High Plains Road Shelton Connecticut 06484"));
        Assert.Equal("520 West 45th Street, apartment 310, New York, New York 10036", InverseTextNormalizer.Normalize("five twenty West 45th Street apartment three ten New York New York 10036"));
    }

    [Fact]
    public void B14ItnStepTimeoutScalesWithInputLengthUpToFiveSeconds()
    {
        var itnStep = DeterministicTextPipeline.DefaultSteps()
            .Single(s => s.Stage == DeterministicTextStage.InverseTextNormalization);

        DeterministicTextContext MakeContext(int length) =>
            new(
                new Transcript(DictationSessionId.Create(), new string('a', length), "whisper", DetectedLanguage: "en"),
                new string('a', length),
                [],
                AllOff);

        Assert.Equal(TimeSpan.FromSeconds(0.5), itnStep.GetTimeout(MakeContext(0)));
        Assert.Equal(TimeSpan.FromSeconds(1.5), itnStep.GetTimeout(MakeContext(40_000)));
        Assert.Equal(TimeSpan.FromSeconds(5.0), itnStep.GetTimeout(MakeContext(180_000)));
        Assert.Equal(TimeSpan.FromSeconds(5.0), itnStep.GetTimeout(MakeContext(250_000)));
    }

    [Fact]
    public void B5CustomWordPass0JoinsSpelledCompoundsWhileGuardingPunctuationStopwordsAndPacks()
    {
        CustomWordEntry[] words =
        [
            new("ChatGPT", "ChatGPT"),
            new("OpenAI", "OpenAI"),
            new("API", "API"),
            new("Bank of America", "Bank of America"),
        ];

        Assert.Equal("ChatGPT", CustomWordCorrector.Correct("Chat G P T", words).Text);
        Assert.Equal("OpenAI", CustomWordCorrector.Correct("Open A I", words).Text);
        Assert.Equal("API", CustomWordCorrector.Correct("A P I", words).Text);
        Assert.Equal("Bank of America", CustomWordCorrector.Correct("bank of america", words).Text);
        Assert.Equal("open. AI", CustomWordCorrector.Correct("open. AI", words).Text);
        Assert.Equal("a PI", CustomWordCorrector.Correct("a PI", words).Text);

        var packOnlyResult = CustomWordCorrector.Correct(
            "Open A I",
            [],
            packWords: [new CustomWordEntry("OpenAI", "OpenAI")]);
        Assert.Equal("Open A I", packOnlyResult.Text);
    }

    [Fact]
    public void B3PolishChunkerEnforcesWordAndUtf8ByteBudgets()
    {
        var words350 = string.Join(' ', Enumerable.Range(0, 350).Select(i => $"word{i}."));
        var localChunks = PolishChunker.SplitByBudget(words350, PolishChunker.MaximumLocalWordsPerChunk);
        var cloudChunks = PolishChunker.SplitByBudget(words350, PolishChunker.MaximumCloudWordsPerChunk);

        Assert.True(localChunks.Count >= 2);
        Assert.All(localChunks, p => Assert.True(PolishChunker.CountWords(p.Text) <= PolishChunker.MaximumLocalWordsPerChunk));
        Assert.Single(cloudChunks);

        // Multi-byte CJK sentence with few whitespace words still respects the UTF-8 byte ceiling.
        var cjkLong = string.Concat(Enumerable.Repeat("日本語テスト文。", 400));
        Assert.True(Encoding.UTF8.GetByteCount(cjkLong) > PolishChunker.MaximumUtf8BytesPerChunk);
        var cjkChunks = PolishChunker.SplitByBudget(cjkLong, PolishChunker.MaximumCloudWordsPerChunk, PolishChunker.MaximumUtf8BytesPerChunk);
        Assert.True(cjkChunks.Count >= 2);
        Assert.All(cjkChunks, p => Assert.True(Encoding.UTF8.GetByteCount(p.Text) <= PolishChunker.MaximumUtf8BytesPerChunk));
    }

    [Fact]
    public async Task B3OllamaPolishProviderChunksLongInputsAndFallsBackAtomicallyOnChunkFailure()
    {
        var input350 = string.Join(' ', Enumerable.Range(0, 350).Select(i => $"sentence{i}."));
        var handlerSuccess = new ScriptedHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonReply("""{"models":[{"name":"llama3.2","capabilities":["completion"]}]}""");
            }

            using var doc = JsonDocument.Parse(req.Body);
            var userText = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            const string marker = "Transcript to clean:\n\n";
            var segment = userText.StartsWith(marker, StringComparison.OrdinalIgnoreCase)
                ? userText[marker.Length..]
                : userText;
            return JsonReply(JsonSerializer.Serialize(new { message = new { content = segment.ToUpperInvariant() }, done_reason = "stop" }));
        });

        await using (var provider = new OllamaPolishProvider(new OllamaPolishOptions(null, "llama3.2"), handlerSuccess))
        {
            var result = await provider.TryPolishAsync(new PolishRequest(new ProcessedText(DictationSessionId.Create(), input350), "en"));
            Assert.Equal(PolishAttemptStatus.Polished, result.Status);
            Assert.True(handlerSuccess.ChatCallCount >= 2);
            Assert.Equal(input350.ToUpperInvariant(), result.Output.Text);
        }

        var handlerRemote = new ScriptedHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonReply("""{"models":[{"name":"gpt-oss:20b-cloud","remote_host":"https://ollama.com","capabilities":["completion"]}]}""");
            }

            using var doc = JsonDocument.Parse(req.Body);
            var userText = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            const string marker = "Transcript to clean:\n\n";
            var segment = userText.StartsWith(marker, StringComparison.OrdinalIgnoreCase)
                ? userText[marker.Length..]
                : userText;
            return JsonReply(JsonSerializer.Serialize(new { message = new { content = segment.ToUpperInvariant() }, done_reason = "stop" }));
        });

        await using (var remoteProvider = new OllamaPolishProvider(new OllamaPolishOptions(null, "gpt-oss:20b-cloud"), handlerRemote))
        {
            var remoteResult = await remoteProvider.TryPolishAsync(new PolishRequest(new ProcessedText(DictationSessionId.Create(), input350), "en"));
            Assert.Equal(PolishAttemptStatus.Polished, remoteResult.Status);
            Assert.Equal(1, handlerRemote.ChatCallCount);
            Assert.Equal(input350.ToUpperInvariant(), remoteResult.Output.Text);
        }

        var chatCalls = 0;
        var handlerMiddleFail = new ScriptedHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonReply("""{"models":[{"name":"llama3.2","capabilities":["completion"]}]}""");
            }

            chatCalls++;
            if (chatCalls == 2)
            {
                return JsonReply("""{"message":{"content":""},"done_reason":"stop"}""");
            }

            using var doc = JsonDocument.Parse(req.Body);
            var userText = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            const string marker = "Transcript to clean:\n\n";
            var segment = userText.StartsWith(marker, StringComparison.OrdinalIgnoreCase)
                ? userText[marker.Length..]
                : userText;
            return JsonReply(JsonSerializer.Serialize(new { message = new { content = segment }, done_reason = "stop" }));
        });

        await using var providerFail = new OllamaPolishProvider(
            new OllamaPolishOptions(null, "llama3.2"),
            handlerMiddleFail,
            delay: (_, _) => Task.CompletedTask);
        var fallback = await providerFail.TryPolishAsync(new PolishRequest(new ProcessedText(DictationSessionId.Create(), input350), "en"));
        Assert.Equal(input350, fallback.Output.Text);
        Assert.True(fallback.UsedFallback);
    }

    [Fact]
    public async Task B11UltraShortBypassQuestionFlipSymbolDropAndLeadingMarkerRepairWork()
    {
        var effects = new RecordingEffects();
        await using var provider = new CountingPolishProvider(text => text.ToUpperInvariant());
        var snippets = new SnippetExpansionStage(
            new SnippetExpander(),
            TimeProvider.System,
            () => CultureInfo.InvariantCulture,
            _ => Task.FromResult<string?>(null),
            TimeSpan.FromMilliseconds(200));
        var finalizer = new TranscriptFinalizer(
            new DeterministicTextPipeline(),
            new PolishExecutor(new AllowAllAdmission(), new NullPolishEffects(), () => []),
            effects,
            snippets);
        var setup = new PolishSetup(provider, UsesLocalRuntime: false, RuntimeResourceKind.Cpu);

        var threeWords = await finalizer.FinalizeAsync(
            new Transcript(DictationSessionId.Create(), "one two three", "whisper", DetectedLanguage: "en"),
            [],
            AllOn,
            SnippetVocabulary.Empty,
            setup,
            CancellationToken.None);
        Assert.False(threeWords.WasPolished);
        Assert.Equal(0, provider.CallCount);

        var shortCjk = await finalizer.FinalizeAsync(
            new Transcript(DictationSessionId.Create(), "今日は良い天気", "whisper", DetectedLanguage: "ja"),
            [],
            AllOn,
            SnippetVocabulary.Empty,
            setup,
            CancellationToken.None);
        Assert.False(shortCjk.WasPolished);
        Assert.Equal(0, provider.CallCount);

        var longCjk = await finalizer.FinalizeAsync(
            new Transcript(DictationSessionId.Create(), "今日はとても良い天気ですね", "whisper", DetectedLanguage: "ja"),
            [],
            AllOn,
            SnippetVocabulary.Empty,
            setup,
            CancellationToken.None);
        Assert.True(longCjk.WasPolished);
        Assert.Equal(1, provider.CallCount);

        var qFlip = PolishOutputGuard.Review("Can you send that file?", "Yes, I will send that file.");
        Assert.Equal(PolishOutputVerdict.RefusedQuestionFlip, qFlip.Verdict);

        var slashDrop = PolishOutputGuard.Review("Run /clear before checking C:\\foo\\bar and a/b.", "Run clear before checking foo and b.");
        Assert.Equal(PolishOutputVerdict.RefusedSymbolDrop, slashDrop.Verdict);

        Assert.Equal(
            "Actually, we should ship this.",
            LeadingMarkerRepair.Repair("Actually we should ship this.", "We should ship this.", "en"));
        Assert.Equal(
            "Basically, that works well.",
            LeadingMarkerRepair.Repair("Basically that works well.", "That works well.", "en"));
        Assert.Equal(
            "Literally every test passed.",
            LeadingMarkerRepair.Repair("Literally every test passed.", "Every test passed.", "en"));
        Assert.Equal(
            "Ship the fix today.",
            LeadingMarkerRepair.Repair("Actually no wait ship the fix today.", "Ship the fix today.", "en"));
        Assert.Equal(
            "Actually, API requests succeed.",
            LeadingMarkerRepair.Repair("Actually API requests succeed.", "API requests succeed.", "en"));
        Assert.Equal(
            "We should actually ship it.",
            LeadingMarkerRepair.Repair("Actually we should ship it.", "We should actually ship it.", "en"));
        Assert.Equal(
            "Actually, I actually think so.",
            LeadingMarkerRepair.Repair("Actually I actually think so.", "I actually think so.", "en"));
        Assert.Equal(
            "On devrait partir.",
            LeadingMarkerRepair.Repair("Actually on devrait partir.", "On devrait partir.", "fr"));
    }

    [Fact]
    public async Task B3OllamaPolishProviderEnforcesSingleSharedDeadlineAcrossAllChunks()
    {
        var input350 = string.Join(' ', Enumerable.Range(0, 350).Select(i => $"chunk{i}."));
        var handler = new AsyncScriptedHttpHandler(async (req, ct) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonReply("""{"models":[{"name":"llama3.2","capabilities":["completion"]}]}""");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(90), ct);
            using var doc = JsonDocument.Parse(req.Body);
            var userText = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            const string marker = "Transcript to clean:\n\n";
            var segment = userText.StartsWith(marker, StringComparison.OrdinalIgnoreCase)
                ? userText[marker.Length..]
                : userText;
            return JsonReply(JsonSerializer.Serialize(new { message = new { content = segment }, done_reason = "stop" }));
        });

        // 2 chunks * 90 ms = 180 ms exceeds a single 130 ms shared timeout even though each chunk < 130 ms.
        await using var provider = new OllamaPolishProvider(
            new OllamaPolishOptions(null, "llama3.2", RequestTimeout: TimeSpan.FromMilliseconds(130)),
            handler);
        var result = await provider.TryPolishAsync(new PolishRequest(new ProcessedText(DictationSessionId.Create(), input350), "en"));
        Assert.Equal(PolishAttemptStatus.TimedOut, result.Status);
        Assert.Equal(input350, result.Output.Text);
    }

    [Fact]
    public void B12ClipboardHoldReleaseOrderingAndColdChromiumRetryWork()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(20), WindowsClipboardPaste.SyntheticPasteKeyHoldDuration);

        var events = new List<string>();
        var chord = WindowsClipboardPaste.BuildCtrlVInputs();
        var succeeded = WindowsClipboardPaste.SendHoldAndReleaseChord(
            chord,
            TimeSpan.FromMilliseconds(20),
            inputs =>
            {
                var isUp = (inputs[0].Union.Keyboard.Flags & 0x0002u) != 0;
                events.Add(isUp ? $"up:{inputs.Length}" : $"down:{inputs.Length}");
                return true;
            },
            wait => events.Add($"wait:{wait.TotalMilliseconds}"));

        Assert.True(succeeded);
        Assert.Equal(["down:2", "wait:20", "up:2"], events);

        var releaseEvents = new List<string>();
        Assert.Throws<InvalidOperationException>(() =>
            WindowsClipboardPaste.SendHoldAndReleaseChord(
                chord,
                TimeSpan.FromMilliseconds(20),
                inputs =>
                {
                    var isUp = (inputs[0].Union.Keyboard.Flags & 0x0002u) != 0;
                    releaseEvents.Add(isUp ? "up" : "down");
                    return true;
                },
                _ => throw new InvalidOperationException("wait aborted")));
        Assert.Equal(["down", "up"], releaseEvents);

        var retryUpCount = 0;
        var failedUpResult = WindowsClipboardPaste.SendHoldAndReleaseChord(
            chord,
            TimeSpan.FromMilliseconds(20),
            inputs =>
            {
                var isUp = (inputs[0].Union.Keyboard.Flags & 0x0002u) != 0;
                if (!isUp)
                {
                    return true;
                }

                retryUpCount++;
                return retryUpCount > 1;
            },
            _ => { });
        Assert.False(failedUpResult);
        Assert.Equal(2, retryUpCount);

        Assert.True(WindowsTextDeliveryPolicy.ShouldRetryColdChromiumFocus(null, "Chrome_WidgetWin_1"));
        Assert.False(WindowsTextDeliveryPolicy.ShouldRetryColdChromiumFocus(null, "Notepad"));
        Assert.True(WindowsTextDeliveryPolicy.ShouldRetryColdChromiumFocus(
            new WindowsTextTargetAdapter.FocusedElementFacts(1u, "rid", false, false, true, false, false, "Chrome_WidgetWin_1"),
            "Chrome_WidgetWin_1"));

        var repoRoot = FindRepoRoot();
        var keystrokesSource = File.ReadAllText(Path.Combine(repoRoot, "src", "Production", "EnviousWispr.Services", "Input", "WindowsClipboardKeystrokes.cs"));
        var pasteSource = File.ReadAllText(Path.Combine(repoRoot, "src", "Production", "EnviousWispr.Services", "Input", "WindowsClipboardPaste.cs"));
        Assert.Contains("PumpingWait", keystrokesSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep(", keystrokesSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep(", pasteSource, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Production")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private static HttpResponseMessage JsonReply(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class AsyncScriptedHttpHandler(
        Func<ScriptedHttpRequest, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(new ScriptedHttpRequest(request.Method, body), cancellationToken);
        }
    }

    private sealed class ScriptedHttpHandler(
        Func<ScriptedHttpRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int ChatCallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/chat")
            {
                ChatCallCount++;
            }

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(new ScriptedHttpRequest(request.Method, body));
        }
    }

    private sealed record ScriptedHttpRequest(HttpMethod Method, string Body);

    private sealed class CountingPolishProvider(Func<string, string> answer) : IPolishProvider
    {
        public string ProviderId => "counting";

        public int CallCount { get; private set; }

        public Task<PolishResult> TryPolishAsync(PolishRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var output = answer(request.Input.Text);
            return Task.FromResult(new PolishResult(
                new ProcessedText(request.Input.SessionId, output),
                PolishAttemptStatus.Polished));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AllowAllAdmission : IRuntimeResourceAdmission
    {
        public Task<RuntimeResourceAcquireResult> AcquireAsync(
            RuntimeResourceKind resource,
            RuntimeWorkloadKind workload,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeResourceAcquireResult(Succeeded: true, new NoopLease()));

        private sealed class NoopLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NullPolishEffects : IPolishAttemptEffects
    {
        private int _events;

        public void RecordPolishStarted(string providerId) => _events++;

        public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds) => _events++;

        public void RecordPolishRefused() => _events++;
    }

    private sealed class RecordingEffects : ITranscriptFinalizationEffects
    {
        private int _events;

        public void RecordDeterministicProcessingStarted() => _events++;

        public void EmitStageReceipts(IReadOnlyList<DeterministicStageReceipt> receipts, bool emojiRestorationOnly) => _events++;

        public Task SaveRecoveryTextAsync(ProcessedText output, CancellationToken cancellationToken)
        {
            _events++;
            return Task.CompletedTask;
        }

        public void RecordPolishStarted(string providerId) => _events++;

        public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds) => _events++;

        public void RecordPolishRefused() => _events++;

        public void RecordDeterministicProcessingFinished(bool degraded, long elapsedMilliseconds, bool empty = false) => _events++;
    }
}
