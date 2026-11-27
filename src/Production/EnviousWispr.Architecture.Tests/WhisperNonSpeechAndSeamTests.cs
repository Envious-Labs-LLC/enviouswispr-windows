using System.Reflection;
using System.Text.RegularExpressions;
using EnviousWispr.ASR;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Input;

namespace EnviousWispr.Architecture.Tests;

[Collection("SystemClipboard")]
public sealed class WhisperNonSpeechAndSeamTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData(" [BLANK_AUDIO] ", "")]
    [InlineData("[blank audio]", "")]
    [InlineData("(blank audio)", "")]
    [InlineData("[MUSIC]", "")]
    [InlineData("(music playing)", "")]
    [InlineData("[SILENCE]", "")]
    [InlineData("(silence)", "")]
    [InlineData("[NO_SPEECH]", "")]
    [InlineData("(no speech)", "")]
    [InlineData("[INAUDIBLE]", "")]
    [InlineData("(inaudible)", "")]
    [InlineData("[APPLAUSE]", "")]
    [InlineData("(applause)", "")]
    [InlineData("[LAUGHTER]", "")]
    [InlineData("(laughter)", "")]
    [InlineData("[SOUND]", "")]
    [InlineData("[NOISE]", "")]
    [InlineData("[BACKGROUND_NOISE]", "")]
    [InlineData("[COUGHING]", "")]
    [InlineData("(coughs)", "")]
    [InlineData("[BREATHING]", "")]
    [InlineData("(sighs)", "")]
    [InlineData("[TYPING]", "")]
    [InlineData("[CLICKING]", "")]
    [InlineData("[STATIC]", "")]
    [InlineData("[BEEP]", "")]
    [InlineData("[BELL]", "")]
    [InlineData("[FOOTSTEPS]", "")]
    [InlineData("<|nospeech|>", "")]
    [InlineData("<|startoftranscript|><|en|><|transcribe|>Hello world.<|endoftext|>", "Hello world.")]
    [InlineData("\u266A la la la \u266A", "")]
    [InlineData("\u266A", "")]
    [InlineData(" [BLANK_AUDIO] Hello [MUSIC] world. ", "Hello world.")]
    [InlineData("[BLANK_AUDIO], Hello there.", "Hello there.")]
    [InlineData("[BLANK_AUDIO].", "")]
    public void WhisperNonSpeechFilterStripsNonSpeechMarkersCleanly(string raw, string expected) =>
        Assert.Equal(expected, WhisperNonSpeechFilter.Clean(raw));

    [Theory]
    [InlineData("[1] first item", "[1] first item")]
    [InlineData("[TODO] check later", "[TODO] check later")]
    [InlineData("Read the manual (see note).", "Read the manual (see note).")]
    [InlineData("I heard music and laughter in silence.", "I heard music and laughter in silence.")]
    [InlineData("Wait for blank audio calibration.", "Wait for blank audio calibration.")]
    [InlineData(", thanks.", ", thanks.")]
    [InlineData("[BLANK_AUDIO] .NET", ".NET")]
    [InlineData("[BLANK_AUDIO] - first item", "- first item")]
    [InlineData("(silence) ...wait", "...wait")]
    [InlineData("[BLANK_AUDIO],5", ",5")]
    public void WhisperNonSpeechFilterPreservesDictatedBracketsAndSpokenWords(string raw, string expected) =>
        Assert.Equal(expected, WhisperNonSpeechFilter.Clean(raw));

    [Fact]
    public void WhisperAndParakeetFiltersUseSourceGeneratedBoundedRegexesWithoutCompiledOption()
    {
        foreach (var type in new[] { typeof(WhisperNonSpeechFilter), typeof(ParakeetTranscriptionEngine) })
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (var field in type.GetFields(flags))
            {
                if (field.FieldType == typeof(Regex) && field.GetValue(null) is Regex regex)
                {
                    Assert.False((regex.Options & RegexOptions.Compiled) != 0, $"{type.Name}.{field.Name} must not use RegexOptions.Compiled.");
                }
            }

            foreach (var method in type.GetMethods(flags))
            {
                if (method.ReturnType == typeof(Regex) && method.GetParameters().Length == 0)
                {
                    var regex = (Regex)method.Invoke(null, null)!;
                    Assert.False((regex.Options & RegexOptions.Compiled) != 0, $"{type.Name}.{method.Name}() must not use RegexOptions.Compiled.");
                    Assert.NotEqual(Regex.InfiniteMatchTimeout, regex.MatchTimeout);
                }
            }
        }
    }

    [Fact]
    public void WhisperCleanAndJoinSegmentsDropsBlankAudioAndNormalizesSpacesPreservingQuietSpeech()
    {
        var assembled = WhisperTranscriptionEngine.CleanAndJoinSegments(
        [
            new WhisperTranscriptionEngine.WhisperSegmentPiece(" [BLANK_AUDIO]", 0.95f),
            new WhisperTranscriptionEngine.WhisperSegmentPiece("...", 0.91f),
            new WhisperTranscriptionEngine.WhisperSegmentPiece("Quiet sentence.", 0.87f),
            new WhisperTranscriptionEngine.WhisperSegmentPiece("Second sentence.", 0.02f),
            new WhisperTranscriptionEngine.WhisperSegmentPiece(", attached clause.", 0.03f),
            new WhisperTranscriptionEngine.WhisperSegmentPiece(" [MUSIC] ", 0.92f),
        ]);

        Assert.Equal("Quiet sentence. Second sentence., attached clause.", assembled);
    }

    [Fact]
    public void LivePreviewTextFormatterStripsNonSpeechMarkersBeforeBounding()
    {
        Assert.Equal(
            string.Empty,
            LivePreviewTextFormatter.Format(
                " [BLANK_AUDIO] <|nospeech|> (silence) ",
                wordCorrectionEnabled: true,
                customWords: [],
                EnglishSpelling.American,
                detectedLanguage: "en",
                recognitionLanguage: null,
                "whisper"));

        Assert.Equal(
            "Hello world",
            LivePreviewTextFormatter.Format(
                " [BLANK_AUDIO] Hello (music) world ",
                wordCorrectionEnabled: false,
                customWords: [],
                EnglishSpelling.American,
                detectedLanguage: "en",
                recognitionLanguage: null,
                "whisper"));
    }

    [Fact]
    public async Task LivePreviewControllerSuppressesWhitespaceAndDuplicateFramesWhilePreservingRealWords()
    {
        var session = DictationSessionId.Create();
        var engine = new ScriptedPreviewEngine(
        [
            " [BLANK_AUDIO] ",
            "   ",
            "Hello world",
            "Hello world",
            "[MUSIC]",
            "Hello world again",
        ]);
        var audio = new FixedAudioSource(session);
        var effects = new CapturingPreviewEffects { Engine = engine, Audio = audio };
        var clock = new Deterministic.ManualClock();
        var logger = new CapturingLogger();
        var controller = new LivePreviewController(effects, logger, clock);

        await controller.StartAsync(session);

        // Pass 1: "[BLANK_AUDIO]" -> suppressed
        await clock.WhenRegistered(1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(effects.Texts);
        clock.Advance(LivePreviewCadence.Interval);

        // Pass 2: "   " -> suppressed
        await clock.WhenRegistered(2).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(effects.Texts);
        clock.Advance(LivePreviewCadence.Interval);

        // Pass 3: "Hello world" -> shown (1)
        await effects.WaitForCountAsync(1);
        Assert.Equal(["Hello world"], effects.Texts);
        await clock.WhenRegistered(3).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(LivePreviewCadence.Interval);

        // Pass 4: duplicate "Hello world" -> skipped
        await clock.WhenRegistered(4).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["Hello world"], effects.Texts);
        clock.Advance(LivePreviewCadence.Interval);

        // Pass 5: transient "[MUSIC]" -> suppressed, does not wipe "Hello world"
        await clock.WhenRegistered(5).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["Hello world"], effects.Texts);
        clock.Advance(LivePreviewCadence.Interval);

        // Pass 6: "Hello world again" -> shown (2)
        await effects.WaitForCountAsync(2);
        await controller.StopAsync();

        Assert.Equal(["Hello world", "Hello world again"], effects.Texts);
    }

    [Theory]
    [InlineData(" that.Is", "that. Is")]
    [InlineData(" should.You", "should. You")]
    [InlineData(" done.We are ready.", "done. We are ready.")]
    [InlineData(" done.I'm ready.", "done. I'm ready.")]
    [InlineData(" done.I agree.", "done. I agree.")]
    [InlineData(" that's.Why", "that's. Why")]
    [InlineData(" He said \"hi\"", "He said \"hi\"")]
    [InlineData(" check (see note)", "check (see note)")]
    [InlineData(" test [1] and {x}", "test [1] and {x}")]
    [InlineData(" Hello , world !", "Hello, world!")]
    [InlineData(" don 't stop.", "don't stop.")]
    public void ParakeetTokenNormalizationRepairsSpacingAndPreservesOpeningDelimiters(string raw, string expected) =>
        Assert.Equal(expected, ParakeetTranscriptionEngine.NormalizeDecodedTokenText(raw));

    [Theory]
    [InlineData("3.14")]
    [InlineData("v0.19.0")]
    [InlineData("example.com")]
    [InlineData("https://Example.Com/Path")]
    [InlineData("Sam.Smith@Example.com")]
    [InlineData("Console.WriteLine")]
    [InlineData("EnviousWispr.App")]
    [InlineData("fooBar.Baz")]
    [InlineData("my_obj.Bar")]
    [InlineData("obj.Run()")]
    [InlineData("obj.Items[0]")]
    [InlineData("wait...What")]
    [InlineData("U.S.A.")]
    [InlineData("U.S.")]
    [InlineData("e.g.")]
    [InlineData("i.e.")]
    [InlineData("e.g.That")]
    [InlineData("etc.More")]
    [InlineData("mr.Smith")]
    public void ParakeetPostPeriodRepairProtectsDecimalsVersionsDomainsEmailsCodeEllipsesAndAbbreviations(string raw) =>
        Assert.Equal(raw, ParakeetTranscriptionEngine.NormalizeDecodedTokenText(" " + raw));

    [Fact]
    public async Task DeliveryRequiresCaretEvidenceAndRepairsChromiumSeamWithoutDoublingSpaces()
    {
        var target = new TargetWindowId(100, 200, "focus-ce");
        var noContext = new TargetContextResult(
            TargetContextStatus.Available,
            new CaretContext(
                target,
                "focus-ce",
                TextTargetKind.Browser,
                Left: string.Empty,
                Selection: string.Empty,
                Right: string.Empty,
                LeftReachedDocumentStart: false,
                RightReachedDocumentEnd: false,
                HasTextContext: false,
                SupportsDirectValueWrite: false,
                DirectValueWriteAtEnd: false,
                BaselineValue: "We had a great experience."));
        var adapter = new SequenceAdapter(noContext);
        var delivery = new ContextAwareTextDelivery(adapter);

        // When HasTextContext is false (even with BaselineValue present), never guess caret offset or prepend a leading space.
        var firstNoContext = await delivery.DeliverAsync(MakeRequest("We had a great experience.", target));
        var secondNoContext = await delivery.DeliverAsync(MakeRequest("As you know.", target));
        Assert.Equal(CursorRepairDisposition.FallbackPayload, firstNoContext.RepairDisposition);
        Assert.Equal(CursorRepairDisposition.FallbackPayload, secondNoContext.RepairDisposition);
        Assert.Equal("We had a great experience. ", adapter.CommittedTexts[0]);
        Assert.Equal("As you know. ", adapter.CommittedTexts[1]);

        // When Chromium UIA TextPattern provides real caret context after stripping trailing space, ContextApplied inserts one leading space.
        adapter.NextContext = new TargetContextResult(
            TargetContextStatus.Available,
            new CaretContext(
                target,
                "focus-ce",
                TextTargetKind.Browser,
                Left: "We had a great experience.",
                Selection: string.Empty,
                Right: string.Empty,
                LeftReachedDocumentStart: true,
                RightReachedDocumentEnd: true,
                HasTextContext: true,
                SupportsDirectValueWrite: false,
                DirectValueWriteAtEnd: false));
        var repaired = await delivery.DeliverAsync(MakeRequest("As you know.", target));
        Assert.Equal(CursorRepairDisposition.ContextApplied, repaired.RepairDisposition);
        Assert.Equal(" As you know. ", adapter.CommittedTexts[2]);

        // When Chromium preserves trailing NBSP (&nbsp;), ContextApplied must NOT double the space.
        adapter.NextContext = new TargetContextResult(
            TargetContextStatus.Available,
            new CaretContext(
                target,
                "focus-ce",
                TextTargetKind.Browser,
                Left: "We had a great experience.\u00A0",
                Selection: string.Empty,
                Right: string.Empty,
                LeftReachedDocumentStart: true,
                RightReachedDocumentEnd: true,
                HasTextContext: true,
                SupportsDirectValueWrite: false,
                DirectValueWriteAtEnd: false));
        var nbsp = await delivery.DeliverAsync(MakeRequest("Right now.", target));
        Assert.Equal(CursorRepairDisposition.ContextApplied, nbsp.RepairDisposition);
        Assert.Equal("Right now. ", adapter.CommittedTexts[3]);
    }

    [ClipboardFact]
    public async Task ConcurrentClipboardSnapshotsSerializeCleanlyWithoutCrashing()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            Exception? failure = null;
            WindowsClipboardPaste.ClipboardSnapshot? snapshot = null;
            var done = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try
                {
                    barrier.Task.GetAwaiter().GetResult();
                    snapshot = WindowsClipboardPaste.TrySnapshotClipboard();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    done.Set();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            done.Wait();
            Assert.Null(failure);
            Assert.NotNull(snapshot);
        })).ToArray();

        barrier.SetResult();
        await Task.WhenAll(tasks);
    }

    private static TextDeliveryRequest MakeRequest(string text, TargetWindowId target) => new(
        new ProcessedText(DictationSessionId.Create(), text),
        target,
        "en",
        TextDeliveryOptions.Default,
        SnippetExpanded: false);

    private sealed class SequenceAdapter(TargetContextResult initialContext) : ITextTargetAdapter
    {
        public TargetContextResult NextContext { get; set; } = initialContext;
        public List<string> CommittedTexts { get; } = [];

        public Task<TargetContextResult> CaptureContextAsync(TargetWindowId target, TextDeliveryOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(NextContext);

        public Task<TextCommitResult> CommitAsync(TextCommitRequest request, CancellationToken cancellationToken = default)
        {
            CommittedTexts.Add(request.Text.Text);
            return Task.FromResult(new TextCommitResult(TextDeliveryRoute.ClipboardPaste, Delivered: true, ClipboardFallback: false, ClipboardRestored: true));
        }

        public Task<TextCommitResult> CopyOnlyAsync(ProcessedText text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TextCommitResult(TextDeliveryRoute.ClipboardOnly, Delivered: true, ClipboardFallback: false, ClipboardRestored: false));
    }

    private sealed class ScriptedPreviewEngine(string[] texts) : ILivePreviewEngine
    {
        private int _index;
        public string EngineId => "whisper";
        public Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeWorkerResult(true, RuntimeWorkerState.Ready));
        public Task<LivePreviewUpdate> PreviewAsync(AudioSnapshot snapshot, long sequence, CancellationToken cancellationToken = default)
        {
            var idx = Math.Min(_index++, texts.Length - 1);
            return Task.FromResult(new LivePreviewUpdate(snapshot.SessionId.Value, sequence, true, texts[idx], "en"));
        }
        public Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeWorkerResult(true, RuntimeWorkerState.Stopped));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedAudioSource(DictationSessionId session) : IAudioSnapshotSource
    {
        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration) =>
            new(session, new float[16_000], 16_000, 1);
    }

    private sealed class CapturingPreviewEffects : ILivePreviewEffects
    {
        private readonly List<string> _texts = [];
        private readonly Deterministic.Milestone _milestone = new();
        public bool Enabled => true;
        public required ILivePreviewEngine? Engine { get; init; }
        public AppErrorCode? EngineUnavailableReason => null;
        public required IAudioSnapshotSource? Audio { get; init; }
        public DictationSessionId? RecordingSessionId => null;
        public IReadOnlyList<string> Texts { get { lock (_texts) return _texts.ToArray(); } }
        public Task WaitForCountAsync(int count) => _milestone.WhenAtLeast(count).WaitAsync(TimeSpan.FromSeconds(10));
        public void ShowPreview(LivePreviewFrame frame)
        {
            lock (_texts) _texts.Add(frame.Text);
            _milestone.Increment();
        }
        public void ClearPreview() { }
    }

    private sealed class CapturingLogger : IAppLogger
    {
        public void Write(AppLogEntry entry) { }
    }
}
