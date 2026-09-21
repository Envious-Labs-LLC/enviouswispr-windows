using EnviousWispr.App.Composition;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Drives the session the app runs: the coordinator, executor, runner and their effects built by the
/// same <see cref="SessionComposition"/> the shell calls, with fakes only at the leaves - the
/// microphone, the engine, the delivery route, the stores, the window.
/// </summary>
/// <remarks>
/// THE FILES UNDER App/Composition ARE COMPILED INTO THIS PROJECT, not copied, so a join that
/// changes in the app changes here. Until now the production joins were built inline behind a
/// window, and the only proof they held was a journey through the built app; a component test
/// with its own adapters could not tell when the shell handed the runner something else.
/// </remarks>
public sealed class SessionCompositionTests
{
    [Fact]
    public async Task ComposedSessionReachesDeliveryAndReset()
    {
        var world = World.Create("hello world");

        var pressed = await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        Assert.Equal(SessionCommandDisposition.Applied, pressed.Disposition);
        Assert.Equal(DictationSessionState.Recording, world.Controller.CurrentSession?.State);
        var released = await world.Coordinator.SubmitAsync(PushToTalkSignal.Released);

        Assert.Equal(SessionCommandDisposition.Applied, released.Disposition);
        var request = Assert.Single(world.Delivery.Requests);
        Assert.Equal("hello world", request.Text.Text);
        Assert.Equal(new TargetWindowId(101), request.Target);
        Assert.Null(world.Controller.CurrentSession);
        // The shell's leaves saw what the shell's adapters used to hand them: the hook told the
        // recording is on then off, the run-state edge written at both ends, the window told the
        // outcome through the reported delivery and the status.
        Assert.Equal([true, false], world.RecordingActive);
        Assert.Equal([true, false], world.RunState.Edges);
        Assert.Contains(world.View.Statuses, status => status.State == DictationOverlayState.Recording);
        Assert.Contains(world.View.Statuses, status => status.Text == "Transcribing locally...");
        Assert.Contains(world.View.Statuses, status => status.Text == "Delivering to the app you started in...");
        var (delivered, language) = Assert.Single(world.View.Deliveries);
        Assert.Equal(DictationOverlayState.Success, delivered.State);
        Assert.Equal("en", language);
        Assert.Equal("Delivering to the app you started in...", world.View.Statuses[^1].Text);
        Assert.Single(world.Archived);
    }

    [Fact]
    public async Task ComposedLifecycleCancellationReachesRunner()
    {
        // THE ENGINE IS SLOW AND WINDOWS LOCKS. The interruption goes through the composed
        // coordinator, which cancels the processing in flight before it queues; the token the
        // executor armed is the one the runner handed the engine, so the engine sees the cancel.
        var world = World.Create("hello world");
        world.Engine.Hold = true;
        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(world.Coordinator.IsProcessing);

        var interruption = await world.Coordinator.InterruptAsync(SystemLifecycleTransition.SessionLocked)
            .WaitAsync(TimeSpan.FromSeconds(10));
        var released = await release.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(world.Engine.Token!.Value.IsCancellationRequested, "the engine's token is the cancelled deadline");
        Assert.Equal(SessionCommandDisposition.Failed, released.Disposition);
        Assert.Empty(world.Delivery.Requests);
        Assert.Null(world.Controller.CurrentSession);
        Assert.False(world.Coordinator.IsProcessing);
        Assert.NotEqual(SessionCommandDisposition.Failed, interruption.Disposition);
        Assert.Equal([true, false, false], world.RunState.Edges);
    }

    [Fact]
    public async Task ComposedEdgeAfterTheTeardownSaysNoDictationIsInFlight()
    {
        // THE TRANSCRIPTION OUTLIVES BOTH OF THE SHUTDOWN'S WAITS. The teardown disposes the
        // controller beside it - which keeps the session it was disposed under - and lets go of it;
        // the command's finally then writes the run-state edge. The shell reads its own reference,
        // gone by then; the composition must say the same: nothing is in flight.
        var clock = new Deterministic.ManualClock();
        var world = World.Create("hello world", clock);
        world.Engine.HoldIgnoringCancel = true;
        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var drain = TimeSpan.FromSeconds(10);
        var shutdown = world.Coordinator.ShutdownAsync(drain);
        await clock.WhenRegistered(1).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(drain);
        await clock.WhenRegistered(2).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(drain);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, world.TearDowns);
        Assert.NotNull(world.Controller.CurrentSession);
        world.Engine.AllowExit.SetResult();

        var released = await release.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionCommandDisposition.Failed, released.Disposition);
        Assert.Equal([true, false], world.RunState.Edges);
    }

    [Fact]
    public async Task ComposedEdgeDuringTheTeardownAfterItLetGoSaysNoDictationIsInFlight()
    {
        // THE COMMAND ENDS INSIDE THE TEARDOWN, after it has disposed and let go of the controller
        // but before it returns - the shell disposes its delivery adapter in that interval. The
        // shell's reference is already gone there, so the edge written is false.
        var clock = new Deterministic.ManualClock();
        var world = World.Create("hello world", clock);
        world.Engine.HoldIgnoringCancel = true;
        world.HoldTeardownAfterLettingGo = true;
        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var drain = TimeSpan.FromSeconds(10);
        var shutdown = world.Coordinator.ShutdownAsync(drain);
        await clock.WhenRegistered(1).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(drain);
        await clock.WhenRegistered(2).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(drain);
        await world.TeardownLetGo.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(shutdown.IsCompleted, "the teardown is held before it returns");
        Assert.NotNull(world.Controller.CurrentSession);
        world.Engine.AllowExit.SetResult();
        var released = await release.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionCommandDisposition.Failed, released.Disposition);
        Assert.Equal([true, false], world.RunState.Edges);
        world.AllowTeardownExit.SetResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AutoStopAndWatchdogReachSameAdmissionQueue()
    {
        // THE AUTO-STOP'S RELEASE AND THE WATCHDOG'S TIMEOUT ARE COMMANDS ON THE ONE QUEUE the key
        // uses, through the composed timer effects. Each is held mid-command and, while held, is
        // shown to occupy the coordinator's one terminal slot: the coordinator counts it as pending,
        // and a competing key release and a competing timeout are both refused as Ignored by that
        // coordinator. Released, each ends as exactly one terminal outcome, with one run-state edge -
        // which only a command run by the coordinator's executor writes. Both timers run on a manual
        // clock.
        var clock = new Deterministic.ManualClock();
        var world = World.Create("hello world", clock);
        world.Dictation = DictationPreferences.Default with
        {
            RecordingMode = DictationRecordingMode.Toggle,
            AutoStopEnabled = true,
            AutoStopSilenceSeconds = 2,
        };

        // THE AUTO-STOP. A take of speech then enough silence, seen on its first poll; its release
        // runs into an engine that is held.
        world.Engine.Hold = true;
        world.Capture.Take = FakeAudioCapture.SpeechThenSilence(1_000, 3_000);
        await world.SubmitAsync(PushToTalkSignal.Pressed);
        Assert.Equal(DictationSessionState.Recording, world.Controller.CurrentSession?.State);
        Assert.Equal(0, world.Coordinator.PendingCount);
        await clock.WhenRegistered(1).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await world.Engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(world.Log.Events, code => code == AppEventCode.AutoStopTriggered);
        Assert.Equal(1, world.Coordinator.PendingCount);
        Assert.True(world.Coordinator.IsProcessing, "the auto-stop's release is the coordinator's running command");
        var competingRelease = await world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        var competingTimeout = await world.Coordinator.TimeOutAsync(world.Controller.CurrentSession!.Id);
        Assert.Equal(SessionCommandDisposition.Ignored, competingRelease.Disposition);
        Assert.Equal(SessionCommandDisposition.Ignored, competingTimeout.Disposition);
        world.Engine.Release();

        await Eventually(() => world.Controller.CurrentSession is null, "the session to reset");
        await Eventually(() => world.Coordinator.PendingCount == 0, "the coordinator to drain");
        Assert.Equal("hello world", Assert.Single(world.Delivery.Requests).Text.Text);
        Assert.Equal([true, false], world.RunState.Edges);
        // THE COMMAND THE TIMER QUEUED STOPPED THE TIMER, and completed: the auto-stop's stop did not
        // wait on the command that was stopping it.
        Assert.False(world.Runtime.AutoStop.IsRunning, "the release the auto-stop queued stopped the auto-stop");

        // THE WATCHDOG. A recording nobody ends, timed out at the limit; its timeout runs into a
        // preview engine whose stop is held, inside the background stop the recovery makes.
        world.Engine.Hold = false;
        world.PreviewEngine.HoldStop = true;
        world.Capture.Take = new float[16_000];
        await world.SubmitAsync(PushToTalkSignal.Pressed);
        var timedOut = world.Controller.CurrentSession!.Id;
        Assert.Equal(DictationSessionState.Recording, world.Controller.CurrentSession?.State);
        clock.Advance(RecordingLimits.WatchdogDuration());
        await world.PreviewEngine.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, world.Coordinator.PendingCount);
        Assert.Equal(DictationSessionState.Recording, world.Controller.CurrentSession?.State);
        competingRelease = await world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        competingTimeout = await world.Coordinator.TimeOutAsync(timedOut);
        Assert.Equal(SessionCommandDisposition.Ignored, competingRelease.Disposition);
        Assert.Equal(SessionCommandDisposition.Ignored, competingTimeout.Disposition);
        world.PreviewEngine.AllowStopExit.SetResult();

        await Eventually(() => world.Controller.CurrentSession is null, "the watchdog's timeout to reset the session");
        await Eventually(() => world.Coordinator.PendingCount == 0, "the coordinator to drain");
        Assert.False(world.Runtime.Watchdog.IsArmed, "the timeout the watchdog queued disarmed the watchdog");
        Assert.Contains(world.View.Statuses, status => status.Text == "Recording timed out and was cancelled safely");
        Assert.Single(world.Delivery.Requests);
        Assert.Equal([true, false, true, false], world.RunState.Edges);
    }

    [Fact]
    public async Task PreviewTextNeverReachesFinalizationHistoryOrDelivery()
    {
        // THE PREVIEW IS A SCREEN, NOT A SOURCE. Its engine answers every pass with words the final
        // engine never says; those words reach the window and nothing else - not the recovery copy,
        // not history, not the delivery route - and the screen is cleared when the recording ends.
        var world = World.Create("hello world");
        world.LivePreviewEnabled = true;

        await world.SubmitAsync(PushToTalkSignal.Pressed);
        await Eventually(() => world.RuntimeView.Previews.Contains("preview words"), "the preview to reach the window");
        await world.SubmitAsync(PushToTalkSignal.Released);

        Assert.True(world.PreviewEngine.Passes >= 1);
        Assert.Equal("hello world", world.Delivery.Requests.Single().Text.Text);
        Assert.Equal(["hello world"], world.RecoveryStore.Saved);
        Assert.Equal("hello world", world.HistoryStore.Added.Single().Text);
        Assert.Null(world.RuntimeView.Previews[^1]);
        Assert.DoesNotContain(world.View.Statuses, status => status.Text.Contains("preview", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LiveReadsAreTakenBetweenCommandsNotCapturedAtComposition()
    {
        // THE RUNTIME IS BUILT ONCE AND OUTLIVES EVERY SETTING AND ENGINE. Between two recordings the
        // preview is switched on, the capture, the engine and the history preference are replaced and
        // a word is taught; the second recording must use every replacement - and once the shell has
        // let go of its coordinator, a key reaches nothing.
        var clock = new Deterministic.ManualClock();
        var world = World.Create("first take", clock);
        var firstEngine = world.Engine;
        await world.SubmitAsync(PushToTalkSignal.Pressed);
        await world.SubmitAsync(PushToTalkSignal.Released);
        Assert.Equal("first take", world.Delivery.Requests.Single().Text.Text);
        Assert.Single(world.HistoryStore.Added);
        Assert.DoesNotContain("preview words", world.RuntimeView.Previews);
        var firstEngineCalls = firstEngine.Calls;
        var firstCaptureSnapshots = world.Capture.Snapshots;

        // RECORDING TWO, PREVIEW STILL OFF: streaming runs, and it runs on the replaced capture and
        // the replaced engine. A take of speech, a pause, more speech - the first segment is committed
        // by the streaming loop before the release, through the replacement engine.
        var secondEngine = new FakeEngine("second take");
        var secondAudio = new FakeAudioCapture();
        world.EngineRef = secondEngine;
        world.Audio = secondAudio;
        // The replaced capture was never started by this controller, so it stamps its snapshots with
        // the session in flight, as a started one would.
        secondAudio.SessionSource = () => world.Controller.CurrentSession?.Id;
        secondAudio.Take = FakeAudioCapture.Script((false, 200), (true, 3_000), (false, 1_200), (true, 500));
        await world.SubmitAsync(PushToTalkSignal.Pressed);
        await clock.WhenRegistered(1).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Eventually(() => secondEngine.Calls >= 1, "the streaming loop to transcribe a segment through the replacement engine");
        Assert.True(secondAudio.Snapshots > 0, "the streaming loop sampled the replaced capture");
        Assert.Equal(firstEngineCalls, firstEngine.Calls);
        Assert.Equal(firstCaptureSnapshots, world.Capture.Snapshots);
        await world.SubmitAsync(PushToTalkSignal.Released);
        Assert.Equal("second take", world.Delivery.Requests[^1].Text.Text[..11]);

        // RECORDING THREE, PREVIEW ON: streaming stands down (no engine call before the release), the
        // preview samples the replaced capture, history is off, the taught word reaches the polish.
        var thirdEngine = new FakeEngine("envy wisper is here");
        var polish = new FailingPolish();
        world.LivePreviewEnabled = true;
        world.EngineRef = thirdEngine;
        world.History = HistoryPreferences.Default with { IsEnabled = false };
        world.CustomWords.Add(new CustomWordEntry("envy wisper", "EnviousWispr"));
        world.Polish = new PolishSetup(polish, UsesLocalRuntime: true, RuntimeResourceKind.Cpu);

        await world.SubmitAsync(PushToTalkSignal.Pressed);
        // Streaming's start ran inside the awaited press and, reading the switch, started no loop -
        // as distinct from a loop waiting on its first poll, which the clock would never grant here.
        Assert.False(world.Runtime.Streaming.IsRunning, "streaming stood down under the preview");
        await Eventually(() => world.RuntimeView.Previews.Contains("preview words"), "the preview to reach the window");
        Assert.Equal(0, thirdEngine.Calls);
        await world.SubmitAsync(PushToTalkSignal.Released);

        Assert.Equal("EnviousWispr is here", world.Delivery.Requests[^1].Text.Text);
        Assert.Equal(1, thirdEngine.Calls);
        Assert.Equal(firstCaptureSnapshots, world.Capture.Snapshots);
        Assert.Equal(2, world.HistoryStore.Added.Count);
        Assert.DoesNotContain(world.HistoryStore.Added, entry => entry.Text.Contains("EnviousWispr", StringComparison.Ordinal));
        Assert.Contains("EnviousWispr", Assert.Single(polish.Requests).Vocabulary ?? []);

        world.Detached = true;
        var edges = world.RunState.Edges.Count;
        await world.SubmitAsync(PushToTalkSignal.Pressed);
        Assert.Null(world.Controller.CurrentSession);
        Assert.Equal(edges, world.RunState.Edges.Count);
    }

    [Fact]
    public async Task PolishFailureRetainsDeterministicRecoveryText()
    {
        // THE POLISH PROVIDER IS OFFLINE. The deterministic pass's words are the recovery copy, written
        // through the composed persistence owner before the polish is tried, and they are what is
        // delivered; a failed polish loses nothing.
        var world = World.Create("um hello world");
        var polish = new FailingPolish { Trace = world.Trace };
        world.Polish = new PolishSetup(polish, UsesLocalRuntime: true, RuntimeResourceKind.Cpu);

        await world.SubmitAsync(PushToTalkSignal.Pressed);
        await world.SubmitAsync(PushToTalkSignal.Released);

        // The provider was asked, with the deterministic words, and answered that it was unavailable;
        // the recovery copy of those words was already written when it was asked.
        Assert.Equal("hello world", Assert.Single(polish.Requests).Input.Text);
        Assert.Equal(["SaveRecovery:hello world", "Polish:hello world"], world.Trace);
        Assert.Contains(world.Log.Events, code => code == AppEventCode.PolishDegraded);
        Assert.Equal(["hello world"], world.RecoveryStore.Saved);
        Assert.Equal("hello world", world.Delivery.Requests.Single().Text.Text);
        var entry = world.HistoryStore.Added.Single();
        Assert.Equal("hello world", entry.Text);
        Assert.False(entry.WasPolished);
        Assert.True(entry.WasDelivered);
        Assert.Equal(1, world.RuntimeView.HistoryChanges);
        Assert.Equal(1, world.RecoveryStore.Cleared);
    }

    [Fact]
    public async Task ComposedOptionsAreReadAfterTranscription()
    {
        // A WORD TAUGHT WHILE THE ENGINE WAS WORKING REACHES THIS DICTATION. The shell's options are
        // a read at the call, not a value captured when the session was built; the composed runner
        // asks after the engine returns.
        var world = World.Create("envy wisper is here");
        world.Engine.BeforeReturning = () => world.CustomWords.Add(new CustomWordEntry("envy wisper", "EnviousWispr"));

        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await world.Coordinator.SubmitAsync(PushToTalkSignal.Released);

        Assert.Equal("EnviousWispr is here", world.Delivery.Requests.Single().Text.Text);
    }

    private sealed class World
    {
        public required DictationSessionCoordinator Coordinator { get; init; }

        public required PushToTalkSessionController Controller { get; init; }

        public required FakeEngine Engine { get; init; }

        public required FakeDelivery Delivery { get; init; }

        public required FakeView View { get; init; }

        public required FakeRunState RunState { get; init; }

        public required List<bool> RecordingActive { get; init; }

        public required List<CapturedAudio> Archived { get; init; }

        public required List<CustomWordEntry> CustomWords { get; init; }

        /// <summary>How many times the shell's teardown ran.</summary>
        public int TearDowns { get; private set; }

        /// <summary>Whether the teardown pauses after it has let go of the controller, before returning.</summary>
        public bool HoldTeardownAfterLettingGo { get; set; }

        public TaskCompletionSource TeardownLetGo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowTeardownExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The shell's own reference to its controller: let go of by the teardown, as the shell's field is.</summary>
        private PushToTalkSessionController? _attached;

        public required FakeAudioCapture Capture { get; init; }

        public required FakeRuntimeView RuntimeView { get; init; }

        public required FakeRecoveryStore RecoveryStore { get; init; }

        public required FakeHistoryStore HistoryStore { get; init; }

        public required FakePreviewEngine PreviewEngine { get; init; }

        /// <summary>The dictation preferences the shell would read; settable between commands, as a save is.</summary>
        public DictationPreferences Dictation { get; set; } = DictationPreferences.Default;

        public bool LivePreviewEnabled { get; set; }

        public PolishSetup? Polish { get; set; }

        /// <summary>The engine the shell would hand over; replaceable between commands, as a reload is.</summary>
        public ITranscriptionEngine EngineRef { get; set; } = null!;

        /// <summary>The capture the shell would sample; replaceable between commands, as a hook restart is.</summary>
        public IAudioSnapshotSource Audio { get; set; } = null!;

        public HistoryPreferences History { get; set; } = HistoryPreferences.Default;

        /// <summary>Whether the shell has let go of its coordinator.</summary>
        public bool Detached { get; set; }

        public static World Create(string spoken, TimeProvider? clock = null)
        {
            var log = new RecordingLogger();
            clock ??= TimeProvider.System;
            var capture = new FakeAudioCapture();
            var controller = new PushToTalkSessionController(capture, new FakeTargetProvider(101), minimumHoldDuration: TimeSpan.Zero);
            var engine = new FakeEngine(spoken);
            var previewEngine = new FakePreviewEngine();
            var delivery = new FakeDelivery();
            var view = new FakeView();
            var runtimeView = new FakeRuntimeView();
            var runState = new FakeRunState();
            var recoveryStore = new FakeRecoveryStore();
            var historyStore = new FakeHistoryStore();
            var trace = new List<string>();
            var recordingActive = new List<bool>();
            var archived = new List<CapturedAudio>();
            var words = new List<CustomWordEntry>();
            var runId = Guid.NewGuid();
            World? world = null;

            // THE LONG-LIVED OWNERS FIRST, AS THE SHELL BUILDS THEM: the reads reach the world the way
            // the shell's reach its fields, at the call, so a preference set between commands is seen.
            var runtime = RuntimeComposition.Compose(new RuntimeCompositionParts(
                recoveryStore,
                historyStore,
                PatientPipeline.Create(),
                new FakeAdmission(),
                log,
                new RuntimeShell(
                    runtimeView,
                    LivePreviewEnabled: () => world!.LivePreviewEnabled,
                    History: () => world!.History,
                    CustomWords: () => words.ToArray(),
                    Audio: () => world!.Audio,
                    Engine: () => world!.EngineRef,
                    PreviewEngine: () => previewEngine,
                    PreviewUnavailableReason: () => null,
                    RecordingSessionId: () => controller.CurrentSession?.Id,
                    Coordinator: () => world is { Detached: false } ? world.Coordinator : null,
                    Leaving: () => false),
                clock));

            var coordinator = SessionComposition.Compose(new SessionCompositionParts(
                controller,
                capture,
                runtime,
                new HealthyMachine(),
                runState,
                log,
                new SessionShell(
                    view,
                    AttachedSession: () => world!._attached?.CurrentSession,
                    Dictation: () => world!.Dictation,
                    Engine: () => world!.EngineRef,
                    Delivery: () => delivery,
                    Options: () => new FinalizationOptions(words.ToArray(), new DeterministicTextOptions(true, true, true, true), world!.Polish),
                    CloudPolishProviderName: () => null,
                    RunId: () => runId,
                    RecordingActive: recordingActive.Add,
                    ArchiveAudio: archived.Add,
                    // The shell's teardown disposes the controller beside a command that outlived
                    // the shutdown's waits and lets go of its reference before disposing the rest;
                    // the composed test does the same, and can pause in that interval.
                    TearDownSession: async () =>
                    {
                        await controller.DisposeAsync();
                        world!._attached = null;
                        world.TeardownLetGo.TrySetResult();
                        if (world.HoldTeardownAfterLettingGo)
                        {
                            await world.AllowTeardownExit.Task;
                        }

                        world.TearDowns++;
                    }),
                clock));

            world = new World
            {
                Coordinator = coordinator,
                Controller = controller,
                Engine = engine,
                Delivery = delivery,
                View = view,
                RunState = runState,
                RecordingActive = recordingActive,
                Archived = archived,
                CustomWords = words,
                Capture = capture,
                RuntimeView = runtimeView,
                RecoveryStore = recoveryStore,
                HistoryStore = historyStore,
                PreviewEngine = previewEngine,
                Runtime = runtime,
                Log = log,
            };
            world._attached = controller;
            world.EngineRef = engine;
            world.Audio = capture;
            recoveryStore.Trace = world.Trace;
            return world;
        }

        public required SessionRuntime Runtime { get; init; }

        public required RecordingLogger Log { get; init; }

        /// <summary>The order of the leaf operations that matter to a proof, appended by the fakes that share it.</summary>
        public List<string> Trace { get; } = [];

        /// <summary>A key through the runtime's queue, the route the hook and the auto-stop share.</summary>
        public Task SubmitAsync(PushToTalkSignal signal) => Runtime.SubmitAsync(signal);
    }

    /// <summary>Waits, briefly, for something a fire-and-forget route will have done.</summary>
    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeRuntimeView : IRuntimeView
    {
        public List<string?> Previews { get; } = [];

        public List<RecoveryTextLoadResult> Recovered { get; } = [];

        public int Cleared { get; private set; }

        public int HistoryChanges { get; private set; }

        public int MainWindowShown { get; private set; }

        public void ShowPreview(LivePreviewFrame? frame) => Previews.Add(frame?.Text);

        public void ShowRecoveredText(RecoveryTextLoadResult result) => Recovered.Add(result);

        public void ClearRecoveredText() => Cleared++;

        public void NotifyHistoryChanged() => HistoryChanges++;

        public void ShowMainWindow() => MainWindowShown++;
    }

    /// <summary>A preview engine that answers every snapshot with the same words, and counts them.</summary>
    private sealed class FakePreviewEngine : ILivePreviewEngine
    {
        public string EngineId => "preview";

        public string Words { get; set; } = "preview words";

        public int Passes { get; private set; }

        public Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeWorkerResult(true, RuntimeWorkerState.Ready));

        public Task<LivePreviewUpdate> PreviewAsync(AudioSnapshot snapshot, long sequence, CancellationToken cancellationToken = default)
        {
            Passes++;
            return Task.FromResult(new LivePreviewUpdate(snapshot.SessionId.Value, sequence, true, Words));
        }

        public bool HoldStop { get; set; }

        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowStopExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default)
        {
            if (HoldStop)
            {
                StopEntered.TrySetResult();
                await AllowStopExit.Task;
            }

            return new RuntimeWorkerResult(true, RuntimeWorkerState.Stopped);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A polish provider that fails every request the way an offline Ollama does.</summary>
    private sealed class FailingPolish : IPolishProvider
    {
        public string ProviderId => "ollama";

        public List<PolishRequest> Requests { get; } = [];

        public List<string>? Trace { get; set; }

        public Task<PolishResult> TryPolishAsync(PolishRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Trace?.Add($"Polish:{request.Input.Text}");
            return Task.FromResult(new PolishResult(
                request.Input,
                PolishAttemptStatus.Unavailable,
                new AppError(AppErrorCode.PolishProviderUnavailable, AppErrorStage.LocalPolish, CanRetry: true)));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeView : ISessionView
    {
        public List<DictationStatus> Statuses { get; } = [];

        public List<(string Title, string Message, bool IsError)> Notices { get; } = [];

        public List<(DictationStatus Delivered, string? Language)> Deliveries { get; } = [];

        public int MainWindowShown { get; private set; }

        public void ShowStatus(DictationStatus status) => Statuses.Add(status);

        public void ShowNotice(string title, string message, bool isError = false) => Notices.Add((title, message, isError));

        public void ShowMainWindow() => MainWindowShown++;

        public void ReportDelivery(DictationStatus delivered, string? detectedLanguage) => Deliveries.Add((delivered, detectedLanguage));
    }

    private sealed class FakeRunState : IApplicationRunStateStore
    {
        public List<bool> Edges { get; } = [];

        public Task<ApplicationRunStartResult> BeginRunAsync(DateTimeOffset timestamp, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> HeartbeatAsync(Guid runId, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> SetDictationActiveAsync(Guid runId, bool active, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
        {
            Edges.Add(active);
            return Task.FromResult(true);
        }

        public Task<bool> NoteSystemEndingAsync(Guid runId, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> CompleteRunAsync(Guid runId, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    /// <summary>A speech engine that can be held mid-call and honours the cancel it is handed.</summary>
    private sealed class FakeEngine(string spoken) : ITranscriptionEngine
    {
        public string EngineId => "whisper";

        public bool Hold { get; set; }

        /// <summary>Held until released, whatever the token says: a worker that does not answer a cancel.</summary>
        public bool HoldIgnoringCancel { get; set; }

        public TaskCompletionSource AllowExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets a held call return; a held call cancelled first stays cancelled.</summary>
        public void Release() => _released.TrySetResult();

        public CancellationToken? Token { get; private set; }

        public Action? BeforeReturning { get; set; }

        public int Calls { get; private set; }

        public async Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
        {
            Calls++;
            Token = cancellationToken;
            Entered.TrySetResult();
            if (HoldIgnoringCancel)
            {
                await AllowExit.Task;
            }
            else if (Hold)
            {
                await _released.Task.WaitAsync(cancellationToken);
            }

            BeforeReturning?.Invoke();
            return new Transcript(audio.SessionId, spoken, EngineId, DetectedLanguage: "en");
        }
    }

    private sealed class FakeDelivery : ITextDelivery
    {
        public List<TextDeliveryRequest> Requests { get; } = [];

        public Task<DeliveryResult> DeliverAsync(TextDeliveryRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new DeliveryResult(request.Text.SessionId, Delivered: true, ClipboardFallback: false, TextDeliveryRoute.ClipboardPaste));
        }
    }

    private sealed class FakeAdmission : IRuntimeResourceAdmission
    {
        public Task<RuntimeResourceAcquireResult> AcquireAsync(RuntimeResourceKind resource, RuntimeWorkloadKind workload, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeResourceAcquireResult(Succeeded: true, new NoLease()));

        private sealed class NoLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRecoveryStore : IRecoveryTextStore
    {
        public List<string> Saved { get; } = [];

        public List<string>? Trace { get; set; }

        public int Cleared { get; private set; }

        public Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing));

        public Task<bool> SaveAsync(RecoveryTextRecord record, CancellationToken cancellationToken = default)
        {
            Saved.Add(record.Text);
            Trace?.Add($"SaveRecovery:{record.Text}");
            return Task.FromResult(true);
        }

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default)
        {
            Cleared++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeHistoryStore : IHistoryStore
    {
        public List<DictationHistoryEntry> Added { get; } = [];

        public Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryLoadResult(Added, HistoryLoadStatus.Loaded));

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Added.Add(entry);
            return Task.FromResult(new HistoryOperationResult(true));
        }

        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));
    }

    private sealed class HealthyMachine : ISystemResourceProbe
    {
        public SystemResourceSnapshot Probe() => new(
            AvailableDiskBytes: 10L * 1024 * 1024 * 1024,
            AvailablePhysicalMemoryBytes: 8UL * 1024 * 1024 * 1024,
            MemoryLoadPercent: 40);
    }

    private sealed class RecordingLogger : IAppLogger
    {
        private readonly List<AppEventCode> _events = [];

        public IReadOnlyList<AppEventCode> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToArray();
                }
            }
        }

        public void Write(AppLogEntry entry)
        {
            lock (_events)
            {
                _events.Add(entry.Event);
            }
        }
    }

    private sealed class FakeTargetProvider(nint window) : IForegroundTargetProvider
    {
        public TargetWindowId? CaptureForegroundTarget() => new TargetWindowId(window);
    }

    private sealed class FakeAudioCapture : IAudioCapture, IAudioSnapshotSource
    {
        private static readonly float[] OneSample = [0.2f];
        private DictationSessionId _sessionId;

        /// <summary>What a snapshot returns: silence unless a take is scripted.</summary>
        public float[] Take { get; set; } = new float[16_000];

        public int Snapshots { get; private set; }

        /// <summary>Where a snapshot's session comes from; a capture never started answers for the session it is told about.</summary>
        public Func<DictationSessionId?>? SessionSource { get; set; }

        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration)
        {
            Snapshots++;
            return new(SessionSource?.Invoke() ?? _sessionId, Take, 16_000, 1);
        }

        /// <summary>A take of speech followed by the silence the auto-stop waits for.</summary>
        public static float[] SpeechThenSilence(int speechMilliseconds, int silenceMilliseconds) =>
            Script((true, speechMilliseconds), (false, silenceMilliseconds));

        /// <summary>A take built from runs of speech and silence, in order.</summary>
        public static float[] Script(params (bool IsSpeech, int Milliseconds)[] parts)
        {
            var samples = new float[parts.Sum(part => 16_000 * part.Milliseconds / 1000)];
            var cursor = 0;
            foreach (var (isSpeech, milliseconds) in parts)
            {
                var length = 16_000 * milliseconds / 1000;
                for (var i = 0; i < length; i++)
                {
                    var amplitude = isSpeech ? 0.2f : 0.001f;
                    samples[cursor + i] = (i % 2 == 0) ? amplitude : -amplitude;
                }

                cursor += length;
            }

            return samples;
        }

        public event EventHandler<AudioLevel>? LevelChanged
        {
            add { }
            remove { }
        }

        public bool IsCapturing { get; private set; }

        public Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            _sessionId = request.SessionId;
            IsCapturing = true;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new CapturedAudio(_sessionId, OneSample, SampleRate: 16_000, Channels: 1));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
