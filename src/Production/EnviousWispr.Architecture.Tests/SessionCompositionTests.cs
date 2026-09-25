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
using EnviousWispr.Services.Reliability;

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
        var world = ComposedSessionWorld.Create("hello world");

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
        var world = ComposedSessionWorld.Create("hello world");
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
    public async Task ComposedShutdownNeverTearsDownBesideACommandAndTheCommandEndsOnItsOwnTerms()
    {
        // THE TRANSCRIPTION OUTLIVES THE SHUTDOWN'S BUDGET. Nothing is torn down beside it: the
        // session's disposal does not run, the controller is intact, and the report names the command
        // as outstanding. When the engine answers at last the command ends on its own terms - the
        // words kept for recovery, since delivery closed with admission - and the run-state edge it
        // writes says the dictation is over, because it is.
        var clock = new Deterministic.ManualClock();
        var world = ComposedSessionWorld.Create("hello world", clock);
        world.Engine.HoldIgnoringCancel = true;
        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var budget = TimeSpan.FromSeconds(10);
        var shutdown = world.Coordinator.ShutdownAsync(budget);
        await clock.WhenRegistered(1).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(budget);
        var report = await shutdown.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ShutdownOutcome.Unclean, report.Outcome);
        Assert.True(report.CommandOutstanding);
        Assert.Null(report.Teardown);
        Assert.Equal(0, world.TearDowns);
        Assert.NotNull(world.Controller.CurrentSession);
        Assert.Equal([true], world.RunState.Edges);

        world.Engine.AllowExit.SetResult();
        var released = await release.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionCommandDisposition.Applied, released.Disposition);
        Assert.Empty(world.Delivery.Requests);
        Assert.Equal(["hello world"], world.RecoveryStore.Saved);
        Assert.Null(world.Controller.CurrentSession);
        Assert.Equal([true, false], world.RunState.Edges);
        Assert.Equal(0, world.TearDowns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACommandThatOutlivesAnUncleanShutdownWritesItsFinalEdgeOnlyIfTheStoreWasKept(bool storeDisposedBesideTheCommand)
    {
        // THE RUN-STATE STORE IS ONE OF THE THINGS AN OUTSTANDING COMMAND STILL USES. The production
        // store, on a file; a transcription outlives the shutdown's budget; when the engine answers,
        // the command's last act is the edge that says the dictation is over. With the store kept -
        // as the shell keeps it behind an unclean report - the next launch reads a run that was
        // interrupted but NOT dictating. With the store disposed beside the command, as the shell
        // used to, the edge is lost and the next launch warns of words that were in fact recovered.
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "run-state.json");
        var store = new JsonApplicationRunStateStore(path);
        var run = await store.BeginRunAsync(DateTimeOffset.UtcNow);
        var clock = new Deterministic.ManualClock();
        var world = ComposedSessionWorld.Create("hello world", clock, store, run.RunId);
        world.Engine.HoldIgnoringCancel = true;
        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var budget = TimeSpan.FromSeconds(10);
        var shutdown = world.Coordinator.ShutdownAsync(budget);
        await clock.WhenRegistered(1).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(budget);
        var report = await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ShutdownOutcome.Unclean, report.Outcome);
        Assert.False(report.SessionQuiescent);

        if (storeDisposedBesideTheCommand)
        {
            store.Dispose();
        }

        world.Engine.AllowExit.SetResult();
        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Equal(["hello world"], world.RecoveryStore.Saved);
        Assert.Equal(!storeDisposedBesideTheCommand, world.Log.Events.All(code => code != AppEventCode.ApplicationRunStateEdgeFailed));
        store.Dispose();

        using var nextLaunch = new JsonApplicationRunStateStore(path);
        var next = await nextLaunch.BeginRunAsync(DateTimeOffset.UtcNow);
        Assert.Equal(RunStateLoadStatus.PreviousRunInterrupted, next.Status);
        Assert.Equal(storeDisposedBesideTheCommand, next.PreviousRunWasDictating);
    }

    [Fact]
    public async Task ComposedShutdownTearsDownUnderTheSessionOnceTheCommandIsOverAndTheEdgeSaysSo()
    {
        // THE TRANSCRIPTION FINISHES INSIDE THE BUDGET. The teardown then runs under the session -
        // the shell lets go of its controller inside it - and the shutdown is clean; the edge the
        // command wrote before the teardown says the dictation is over.
        var clock = new Deterministic.ManualClock();
        var world = ComposedSessionWorld.Create("hello world", clock);
        world.Engine.HoldIgnoringCancel = true;
        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var shutdown = world.Coordinator.ShutdownAsync(TimeSpan.FromSeconds(10));
        await clock.WhenRegistered(1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(shutdown.IsCompleted);
        world.Engine.AllowExit.SetResult();
        var released = await release.WaitAsync(TimeSpan.FromSeconds(10));
        var report = await shutdown.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionCommandDisposition.Applied, released.Disposition);
        Assert.True(report.Clean);
        Assert.Equal(1, world.TearDowns);
        Assert.Empty(world.Delivery.Requests);
        Assert.Equal([true, false], world.RunState.Edges);
    }

    /// <summary>A recording the watchdog ends tells the hook it is over, as a recording the key ends does. Ref: #86.</summary>
    /// <remarks>
    /// THE TIMEOUT ABORTS AND RESETS WITHOUT A TRANSITION PASSING THE EFFECTS, and the hook's flag was set only from
    /// transitions - so after a timeout the hook went on believing a recording ran: Escape was swallowed in every
    /// application, Quick Add and the last-dictation keys stood aside, and in Toggle mode the next tap sent a stop
    /// for nothing. The flag now follows the controller's own session changes, which every ending passes through.
    /// </remarks>
    [Fact]
    public async Task AWatchdogTimeoutTellsTheHookTheRecordingIsOver()
    {
        var clock = new Deterministic.ManualClock();
        var world = ComposedSessionWorld.Create("hello world", clock);
        world.Capture.Take = new float[16_000];

        await world.SubmitAsync(PushToTalkSignal.Pressed);
        Assert.Equal(DictationSessionState.Recording, world.Controller.CurrentSession?.State);
        Assert.Equal([true], world.RecordingActive);
        clock.Advance(RecordingLimits.WatchdogDuration());

        await Eventually(() => world.Controller.CurrentSession is null, "the watchdog's timeout to reset the session");
        await Eventually(() => world.Coordinator.PendingCount == 0, "the coordinator to drain");
        Assert.Contains(world.View.Statuses, status => status.Text == "Recording timed out and was cancelled safely");
        Assert.Equal([true, false], world.RecordingActive);
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
        var world = ComposedSessionWorld.Create("hello world", clock);
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
    public async Task AReleaseALoopPostedForAnEarlierRecordingDoesNotEndTheNextOne()
    {
        // THE AUTO-STOP'S RELEASE NAMES ITS RECORDING, and the executor checks the name when the
        // command runs, not when it was queued. A release posted for a take that has ended - by a
        // loop a bounded stop left behind, resuming late - reaches the composed queue while the
        // next recording is live, and is ignored.
        var world = ComposedSessionWorld.Create("hello world");
        await world.SubmitAsync(PushToTalkSignal.Pressed);
        var earlier = world.Controller.CurrentSession!.Id;
        await world.SubmitAsync(PushToTalkSignal.Released);
        Assert.Single(world.Delivery.Requests);

        await world.SubmitAsync(PushToTalkSignal.Pressed);
        var current = world.Controller.CurrentSession!.Id;
        Assert.NotEqual(earlier, current);

        // The composed route the auto-stop's effects take, with the earlier recording's name on it.
        await world.Runtime.Queue.HandAsync(PushToTalkSignal.Released, earlier);

        Assert.Equal(DictationSessionState.Recording, world.Controller.CurrentSession?.State);
        Assert.Equal(current, world.Controller.CurrentSession?.Id);
        Assert.Single(world.Delivery.Requests);
        // The ignored command still wrote the edge, as every command the executor runs does - and
        // wrote it true: the recording it left alone is still in flight.
        Assert.Equal([true, false, true, true], world.RunState.Edges);

        await world.SubmitAsync(PushToTalkSignal.Released);
        Assert.Equal(2, world.Delivery.Requests.Count);
    }

    [Fact]
    public async Task AStaleReleaseWaitingInTheQueueDoesNotSwallowTheKeyThatEndsTheNextRecording()
    {
        // THE STALE RELEASE IS PENDING WHEN THE REAL ONE ARRIVES. The next recording's press is still
        // opening the microphone; a release posted for the earlier recording is admitted behind it;
        // the key's release for this recording arrives before the stale one has run. A terminal that
        // names a recording stands in only for that recording, so the key's release is admitted too:
        // the stale one is ignored when it runs, and the key's ends the take.
        var world = ComposedSessionWorld.Create("hello world");
        await world.SubmitAsync(PushToTalkSignal.Pressed);
        var earlier = world.Controller.CurrentSession!.Id;
        await world.SubmitAsync(PushToTalkSignal.Released);
        Assert.Single(world.Delivery.Requests);

        world.Capture.HoldStart = true;
        var press = world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await world.Capture.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stale = world.Coordinator.SubmitAsync(PushToTalkSignal.Released, earlier);
        var real = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        Assert.Equal(3, world.Coordinator.PendingCount);
        world.Capture.AllowStartExit.SetResult();

        Assert.Equal(SessionCommandDisposition.Applied, (await press.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Equal(SessionCommandDisposition.Ignored, (await stale.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Equal(SessionCommandDisposition.Applied, (await real.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Equal(2, world.Delivery.Requests.Count);
        Assert.Null(world.Controller.CurrentSession);
    }

    [Fact]
    public async Task AStaleTimeoutWaitingInTheQueueDoesNotSwallowTheKeyThatEndsTheNextRecording()
    {
        // THE TIMEOUT NAMES ITS RECORDING TOO. A timeout armed for the earlier recording, queued
        // behind the next recording's press, stands in only for that earlier recording; the key's
        // release for the next one is admitted, the timeout is ignored when it runs, and the key's
        // release ends the take.
        var world = ComposedSessionWorld.Create("hello world");
        await world.SubmitAsync(PushToTalkSignal.Pressed);
        var earlier = world.Controller.CurrentSession!.Id;
        await world.SubmitAsync(PushToTalkSignal.Released);

        world.Capture.HoldStart = true;
        var press = world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await world.Capture.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stale = world.Coordinator.TimeOutAsync(earlier);
        var real = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        Assert.Equal(3, world.Coordinator.PendingCount);
        world.Capture.AllowStartExit.SetResult();

        Assert.Equal(SessionCommandDisposition.Applied, (await press.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Equal(SessionCommandDisposition.Ignored, (await stale.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Equal(SessionCommandDisposition.Applied, (await real.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Equal(2, world.Delivery.Requests.Count);
        Assert.Null(world.Controller.CurrentSession);
    }

    [Fact]
    public async Task PreviewTextNeverReachesFinalizationHistoryOrDelivery()
    {
        // THE PREVIEW IS A SCREEN, NOT A SOURCE. Its engine answers every pass with words the final
        // engine never says; those words reach the window and nothing else - not the recovery copy,
        // not history, not the delivery route - and the screen is cleared when the recording ends.
        var world = ComposedSessionWorld.Create("hello world");
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
        var world = ComposedSessionWorld.Create("first take", clock);
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
        var world = ComposedSessionWorld.Create("um hello world");
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
        var world = ComposedSessionWorld.Create("envy wisper is here");
        world.Engine.BeforeReturning = () => world.CustomWords.Add(new CustomWordEntry("envy wisper", "EnviousWispr"));

        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await world.Coordinator.SubmitAsync(PushToTalkSignal.Released);

        Assert.Equal("EnviousWispr is here", world.Delivery.Requests.Single().Text.Text);
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
}
