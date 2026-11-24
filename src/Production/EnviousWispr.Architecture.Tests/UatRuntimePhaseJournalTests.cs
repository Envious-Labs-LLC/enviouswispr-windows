using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class UatRuntimePhaseJournalTests
{
    private static readonly string[] SchemaFields =
    [
        "coveredSamples",
        "endSample",
        "exitCode",
        "frequency",
        "generationId",
        "parentProcessId",
        "phase",
        "processId",
        "remainingSamples",
        "requestId",
        "sampleRate",
        "schemaVersion",
        "sessionId",
        "silenceSamples",
        "speechSamples",
        "startSample",
        "timestamp",
        "workerProcessId",
    ];

    [Theory]
    [InlineData("destination")]
    [InlineData("profile")]
    [InlineData("suffix")]
    [InlineData("wrong-profile")]
    [InlineData("wrong-destination")]
    [InlineData("wrong-suffix")]
    public void IncompleteOrUnboundAdmissionProducesNoJournal(string missing)
    {
        using var run = new Run();
        var destination = missing == "destination" ? null : missing == "wrong-destination" ? run.Profile : run.Destination;
        var profile = missing == "profile" ? null : missing == "wrong-profile" ? run.Root : run.Profile;
        var suffix = missing == "suffix" ? null : missing == "wrong-suffix" ? "journey-" + Guid.NewGuid().ToString("N") : run.Suffix;
        Assert.Null(UatRuntimePhaseJournal.TryCreate(destination, profile, suffix));
        Assert.False(Directory.Exists(run.Destination));
    }

    [Theory]
    [InlineData("missing-generation")]
    [InlineData("empty-generation")]
    [InlineData("wrong-parent")]
    [InlineData("missing-parent")]
    [InlineData("missing-profile")]
    [InlineData("missing-suffix")]
    public void ChildIndependentlyRefusesInheritedButIncompleteAdmission(string missing)
    {
        using var run = new Run();
        var generation = missing == "missing-generation" ? null : missing == "empty-generation" ? Guid.Empty.ToString("N") : Guid.NewGuid().ToString("N");
        var parent = missing == "missing-parent" ? null : (missing == "wrong-parent" ? Environment.ProcessId + 1 : Environment.ProcessId).ToString(CultureInfo.InvariantCulture);
        Assert.Null(UatRuntimePhaseJournal.TryCreateForChild(
            run.Destination,
            missing == "missing-profile" ? null : run.Profile,
            missing == "missing-suffix" ? null : run.Suffix,
            generation,
            parent,
            Environment.ProcessId));
        Assert.False(Directory.Exists(run.Destination));
    }

    [Fact]
    public void FixedSchemaContainsOnlyTransientIdentitiesPhasesAndClocks()
    {
        using var run = new Run();
        var journal = run.Journal();
        var session = Guid.NewGuid();
        journal.Record(UatRuntimePhase.OrdinaryReleaseEntered, session);
        var row = Assert.Single(run.Read());
        Assert.Equal(
            SchemaFields,
            row.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.Equal(session, row.GetProperty("sessionId").GetGuid());
        Assert.True(row.GetProperty("timestamp").GetInt64() > 0);
        Assert.True(row.GetProperty("frequency").GetInt64() > 0);
        Assert.Equal("ordinaryReleaseEntered", row.GetProperty("phase").GetString());
        Assert.False(Directory.Exists(Path.Combine(run.Profile, "diagnostics")));
    }

    [Theory]
    [InlineData(UatNativeOperation.Preprocessor, "preprocessorEntered", "preprocessorReturned", "preprocessorFailed")]
    [InlineData(UatNativeOperation.Encoder, "encoderEntered", "encoderReturned", "encoderFailed")]
    [InlineData(UatNativeOperation.Decoder, "decoderEntered", "decoderReturned", "decoderFailed")]
    [InlineData(UatNativeOperation.WhisperProcessor, "whisperProcessorEntered", "whisperProcessorReturned", "whisperProcessorFailed")]
    public void NativeReturnAndIncompleteScopeHaveDistinctEvidence(
        UatNativeOperation operation,
        string entered,
        string returned,
        string failed)
    {
        using var run = new Run();
        var journal = run.ChildJournal();
        var session = Guid.NewGuid();
        var request = Guid.NewGuid();
        using (journal.BeginRequest(session, request))
        {
            using (var call = UatRuntimePhaseJournal.BeginNativeCall(operation))
            {
                call.Returned();
            }

            using (UatRuntimePhaseJournal.BeginNativeCall(operation))
            {
            }
        }

        var rows = run.Read();
        Assert.Equal(new[] { entered, returned, entered, failed }, rows.Select(r => r.GetProperty("phase").GetString()));
        Assert.All(rows, r =>
        {
            Assert.Equal(session, r.GetProperty("sessionId").GetGuid());
            Assert.Equal(request, r.GetProperty("requestId").GetGuid());
        });
    }

    [Fact]
    public async Task RequestScopeFlowsAcrossAwaitAndRestoresNestedAndParallelContexts()
    {
        using var run = new Run();
        var journal = run.ChildJournal();
        var sessions = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await Task.WhenAll(sessions.Select(async session =>
        {
            using (journal.BeginRequest(session, session))
            {
                await Task.Yield();
                using (var call = UatRuntimePhaseJournal.BeginNativeCall(UatNativeOperation.Encoder))
                {
                    call.Returned();
                }

                using (journal.BeginRequest(Guid.NewGuid(), Guid.NewGuid()))
                {
                    await Task.Yield();
                    using var call = UatRuntimePhaseJournal.BeginNativeCall(UatNativeOperation.Decoder);
                    call.Returned();
                }

                using (var call = UatRuntimePhaseJournal.BeginNativeCall(UatNativeOperation.Preprocessor))
                {
                    call.Returned();
                }
            }
        }));
        var before = run.Read().Count;
        using (var call = UatRuntimePhaseJournal.BeginNativeCall(UatNativeOperation.Encoder))
        {
            call.Returned();
        }

        Assert.Equal(before, run.Read().Count);
        foreach (var session in sessions)
        {
            var rows = run.Read().Where(r => r.GetProperty("sessionId").GetGuid() == session).ToArray();
            Assert.Equal(4, rows.Length);
            Assert.All(rows, r => Assert.Equal(session, r.GetProperty("requestId").GetGuid()));
        }

        Assert.Equal(12, before);
    }

    [Fact]
    public void WriteFailureDoesNotEscapeOrBecomeACompletionEvent()
    {
        using var run = new Run();
        var journal = run.ChildJournal();
        File.WriteAllText(run.Destination, "Owned file deliberately prevents the directory.");
        using (journal.BeginRequest(Guid.NewGuid(), Guid.NewGuid()))
        using (var call = UatRuntimePhaseJournal.BeginNativeCall(UatNativeOperation.Encoder))
        {
            call.Returned();
        }

        Assert.True(File.Exists(run.Destination));
        Assert.Empty(run.Read());
    }

    [Fact]
    public async Task RealSupervisorCorrelationDoesNotTurnStubResponsesIntoNativeEvidence()
    {
        using var run = new Run();
        var path = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.RuntimeWorker.exe");
        Assert.True(File.Exists(path));
        await using var supervisor = new RuntimeWorkerSupervisor(path, ["--test-transcribe-stub"], phaseJournal: run.Journal());
        Assert.True((await supervisor.StartAsync(TimeSpan.FromSeconds(10))).Succeeded);
        using var worker = Process.GetProcessById(supervisor.WorkerProcessId!.Value);
        var session = Guid.NewGuid();
        var response = await supervisor.TranscribeAsync(new RuntimeWorkerTranscriptionRequest(session, "unused-test-audio", 1), TimeSpan.FromSeconds(10));
        Assert.NotNull(response);
        Assert.Equal("complete", response.Status);
        Assert.True((await supervisor.StopAsync()).Succeeded);
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var rows = run.Read();
        var issue = Assert.Single(rows, r => r.GetProperty("phase").GetString() == "parentRequestIssued");
        var consumed = Assert.Single(rows, r => r.GetProperty("phase").GetString() == "responseConsumed");
        Assert.Equal(session, issue.GetProperty("sessionId").GetGuid());
        Assert.Equal(issue.GetProperty("requestId").GetGuid(), consumed.GetProperty("requestId").GetGuid());
        Assert.Equal(issue.GetProperty("generationId").GetGuid(), consumed.GetProperty("generationId").GetGuid());
        var exit = Assert.Single(rows, r => r.GetProperty("phase").GetString() == "workerExitObserved");
        Assert.Equal(issue.GetProperty("generationId").GetGuid(), exit.GetProperty("generationId").GetGuid());
        Assert.True(worker.HasExited);
        Assert.Equal(worker.Id, exit.GetProperty("workerProcessId").GetInt32());
        Assert.Equal(JsonValueKind.Number, exit.GetProperty("exitCode").ValueKind);
        Assert.DoesNotContain(rows, r => r.GetProperty("phase").GetString() is "realRequestAdmitted" or "encoderEntered" or "whisperProcessorEntered");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyAnOwnedCancellationSourceEmitsCancellationRequested(bool start)
    {
        using var run = new Run();
        var controller = new StreamingTranscriptionController(new EmptyEffects(), new QuietLogger(), TimeProvider.System, run.Journal());
        using var scope = DictationScope.Begin(Guid.NewGuid());
        if (start)
        {
            controller.Start(new DictationSessionId(DictationScope.Current!.Value));
        }

        Assert.Equal(StopOutcome.Completed, await controller.StopAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(start ? 1 : 0, run.Read().Count(r => r.GetProperty("phase").GetString() == "cancellationRequested"));
        Assert.Equal(StopOutcome.Completed, await controller.StopAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(start ? 1 : 0, run.Read().Count(r => r.GetProperty("phase").GetString() == "cancellationRequested"));
    }

    [Theory]
    [InlineData("match", UatRuntimePhase.ResponseConsumed)]
    [InlineData("missing", UatRuntimePhase.ResponseFailureConsumed)]
    [InlineData("mismatch", UatRuntimePhase.ResponseFailureConsumed)]
    [InlineData("failed", UatRuntimePhase.ResponseFailureConsumed)]
    public void ProductionConsumptionClassifierRequiresMatchingTranscriptSession(string kind, UatRuntimePhase expected)
    {
        var session = Guid.NewGuid();
        var transcript = kind == "missing"
            ? null
            : new RuntimeWorkerTranscript(
                kind == "mismatch" ? Guid.NewGuid() : session,
                string.Empty,
                "test",
                [],
                false);
        var response = new RuntimeWorkerResponse(1, Guid.NewGuid(), kind == "failed" ? "failed" : "complete", transcript);
        Assert.Equal(expected, RuntimeWorkerSupervisor.ConsumptionPhase(response, session));
    }

    [Fact]
    public void InvalidSampleBoundaryOrCoverageFieldsAreRefused()
    {
        using var run = new Run();
        var journal = run.Journal();
        var session = Guid.NewGuid();
        journal.Record(UatRuntimePhase.StreamingSegmentAdmitted, session, startSample: -1);
        journal.Record(UatRuntimePhase.StreamingSegmentAdmitted, session, endSample: -1);
        journal.Record(UatRuntimePhase.StreamingSegmentAdmitted, session, startSample: 320, endSample: 160);
        journal.Record(UatRuntimePhase.StreamingSegmentAdmitted, session, coveredSamples: -1);
        journal.Record(UatRuntimePhase.StreamingSegmentAdmitted, session, remainingSamples: -1);
        journal.Record(UatRuntimePhase.StreamingSegmentAdmitted, session, speechSamples: -1);
        journal.Record(UatRuntimePhase.StreamingSegmentAdmitted, session, silenceSamples: -1);
        journal.Record(UatRuntimePhase.StreamingSegmentAdmitted, session, sampleRate: 0);
        journal.Record(UatRuntimePhase.StreamingSegmentAdmitted, session, sampleRate: -16_000);

        Assert.Empty(run.Read());
    }

    [Fact]
    public async Task StreamingPhasesCorrelateRequestIdsAndRecordSampleCoverageAcrossSegmentAndTail()
    {
        using var run = new Run();
        var journal = run.Journal();
        var session = DictationSessionId.Create();
        var clock = new Deterministic.ManualClock();
        var prefix = FakeAudioCapture.Script((false, 200), (true, 3000), (false, 1200));
        var whole = FakeAudioCapture.Script((false, 200), (true, 3000), (false, 1200), (true, 800));
        var correlatedIds = new List<Guid>();
        var engine = new CorrelatingEngine(correlatedIds);
        var controller = new StreamingTranscriptionController(
            new StaticSnapshotEffects(engine, new AudioSnapshot(session, prefix, 16_000, 1)),
            new QuietLogger(),
            clock,
            journal);

        controller.Start(session);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await clock.WhenRegistered(2).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StopOutcome.Completed, await controller.CompleteOrdinaryAsync(() => TimeSpan.FromSeconds(5), CancellationToken.None));

        var transcript = await controller.TranscribeUsingAnyHeadStartAsync(
            engine,
            new CapturedAudio(session, whole, 16_000, 1),
            CancellationToken.None);

        Assert.Equal("part-1 part-2", transcript.Text);
        Assert.Equal(2, correlatedIds.Count);

        var rows = run.Read();
        var segAdmitted = Assert.Single(rows, r => r.GetProperty("phase").GetString() == "streamingSegmentAdmitted");
        var segReturned = Assert.Single(rows, r => r.GetProperty("phase").GetString() == "streamingSegmentReturned");
        var stopEntered = Assert.Single(rows, r => r.GetProperty("phase").GetString() == "stopEntered");
        var stopCompleted = Assert.Single(rows, r => r.GetProperty("phase").GetString() == "stopCompleted");
        var tailDispatched = Assert.Single(rows, r => r.GetProperty("phase").GetString() == "finalTailDispatched");
        var tailReturned = Assert.Single(rows, r => r.GetProperty("phase").GetString() == "finalTailReturned");

        Assert.Equal(correlatedIds[0], segAdmitted.GetProperty("requestId").GetGuid());
        Assert.Equal(correlatedIds[0], segReturned.GetProperty("requestId").GetGuid());
        Assert.Equal(0, segAdmitted.GetProperty("startSample").GetInt32());
        Assert.Equal(prefix.Length, segAdmitted.GetProperty("endSample").GetInt32());
        Assert.Equal(0, segAdmitted.GetProperty("coveredSamples").GetInt32());
        Assert.Equal(prefix.Length, segReturned.GetProperty("coveredSamples").GetInt32());
        Assert.True(segAdmitted.GetProperty("speechSamples").GetInt32() > 0);
        Assert.True(segAdmitted.GetProperty("silenceSamples").GetInt32() > 0);
        Assert.Equal(16_000, segAdmitted.GetProperty("sampleRate").GetInt32());

        Assert.Equal(prefix.Length, stopEntered.GetProperty("coveredSamples").GetInt32());
        Assert.Equal(prefix.Length, stopCompleted.GetProperty("coveredSamples").GetInt32());

        Assert.Equal(correlatedIds[1], tailDispatched.GetProperty("requestId").GetGuid());
        Assert.Equal(correlatedIds[1], tailReturned.GetProperty("requestId").GetGuid());
        Assert.Equal(prefix.Length, tailDispatched.GetProperty("startSample").GetInt32());
        Assert.Equal(whole.Length, tailDispatched.GetProperty("endSample").GetInt32());
        Assert.Equal(prefix.Length, tailDispatched.GetProperty("coveredSamples").GetInt32());
        Assert.Equal(whole.Length - prefix.Length, tailDispatched.GetProperty("remainingSamples").GetInt32());
        Assert.Equal(whole.Length, tailReturned.GetProperty("coveredSamples").GetInt32());
        Assert.Equal(0, tailReturned.GetProperty("remainingSamples").GetInt32());
    }

    [Fact]
    public void RequestCorrelationScopeUpdatesRequestIdAcrossRetryAttempts()
    {
        using var correlation = UatRuntimePhaseJournal.BeginRequestCorrelation(out var initialId);
        Assert.NotEqual(Guid.Empty, initialId);
        Assert.Equal(initialId, correlation.RequestId);

        Assert.True(UatRuntimePhaseJournal.TryTakeCorrelatedRequestId(out var firstAttemptId));
        Assert.Equal(initialId, firstAttemptId);
        Assert.Equal(initialId, correlation.RequestId);

        Assert.True(UatRuntimePhaseJournal.TryTakeCorrelatedRequestId(out var retryAttemptId));
        Assert.NotEqual(Guid.Empty, retryAttemptId);
        Assert.NotEqual(initialId, retryAttemptId);
        Assert.Equal(retryAttemptId, correlation.RequestId);
    }

    private sealed class StaticSnapshotEffects(ITranscriptionEngine engine, AudioSnapshot snapshot) : IStreamingTranscriptionEffects
    {
        public bool LivePreviewEnabled => false;
        public ITranscriptionEngine? Engine => engine;
        public IAudioSnapshotSource? Audio { get; } = new SingleSnapshotSource(snapshot);
    }

    private sealed class SingleSnapshotSource(AudioSnapshot snapshot) : IAudioSnapshotSource
    {
        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration) => snapshot;
    }

    private sealed class CorrelatingEngine(List<Guid> correlatedIds) : ITranscriptionEngine
    {
        private int _calls;

        public string EngineId => "correlating";

        public Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
        {
            Assert.True(UatRuntimePhaseJournal.TryTakeCorrelatedRequestId(out var id));
            Assert.NotEqual(Guid.Empty, id);
            correlatedIds.Add(id);
            _calls++;
            return Task.FromResult(new Transcript(audio.SessionId, $"part-{_calls}", EngineId, []));
        }
    }

    private sealed class EmptyEffects : IStreamingTranscriptionEffects
    {
        public bool LivePreviewEnabled => false;
        public ITranscriptionEngine? Engine { get; } = new UnusedEngine();
        public IAudioSnapshotSource? Audio { get; } = new EmptySnapshots();
    }

    private sealed class EmptySnapshots : IAudioSnapshotSource
    {
        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration) => null;
    }

    private sealed class UnusedEngine : ITranscriptionEngine
    {
        public string EngineId => "test";
        public Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Empty snapshot must never reach transcription.");
    }

    private sealed class QuietLogger : IAppLogger
    {
        public void Write(AppLogEntry entry)
        {
        }
    }

    private sealed class Run : IDisposable
    {
        private readonly Guid _id = Guid.NewGuid();
        public string Root { get; }
        public string Profile => Path.Combine(Root, "profile");
        public string Destination => Path.Combine(Root, "runtime-phases");
        public string Suffix => "journey-" + _id.ToString("N");

        public Run()
        {
            Root = Path.Combine(Path.GetTempPath(), "EnviousWispr-AppJourney-Uat-" + _id.ToString("N"));
            Directory.CreateDirectory(Profile);
        }

        public UatRuntimePhaseJournal Journal() =>
            Assert.IsType<UatRuntimePhaseJournal>(UatRuntimePhaseJournal.TryCreate(Destination, Profile, Suffix));

        public UatRuntimePhaseJournal ChildJournal() =>
            Assert.IsType<UatRuntimePhaseJournal>(UatRuntimePhaseJournal.TryCreateForChild(
                Destination,
                Profile,
                Suffix,
                Guid.NewGuid().ToString("N"),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                Environment.ProcessId));

        public List<JsonElement> Read() => !Directory.Exists(Destination)
            ? []
            : Directory.GetFiles(Destination, "*.jsonl")
                .SelectMany(File.ReadAllLines)
                .Select(line =>
                {
                    using var doc = JsonDocument.Parse(line);
                    return doc.RootElement.Clone();
                })
                .ToList();

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
