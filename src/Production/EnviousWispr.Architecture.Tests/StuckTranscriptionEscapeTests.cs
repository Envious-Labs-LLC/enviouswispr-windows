using System.Diagnostics;
using EnviousWispr.App.Composition;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.Runtime;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Proves that pressing Escape during in-flight transcription cancels immediately,
/// never reports "Timed out" or a lost-dictation recovery banner, restores Ready state,
/// lets the very next dictation succeed cleanly, and disarms Escape once Idle or Delivering.
/// </summary>
public sealed class StuckTranscriptionEscapeTests
{
    private const uint EscapeKey = 0x1B;
    private const uint F8Key = 0x77;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task EscapeDuringHungTranscriptionCancelsImmediatelyAndNeverReportsTimeout()
    {
        var world = ComposedSessionWorld.Create("spoken words that should be thrown away");
        world.Engine.Hold = true;

        var pressed = await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        Assert.Equal(SessionCommandDisposition.Applied, pressed.Disposition);
        Assert.Equal(DictationSessionState.Recording, world.Controller.CurrentSession?.State);

        var releaseTask = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        // deadline-fallback: wait for the engine to enter transcription before pressing Escape.
        await world.Engine.Entered.Task.WaitAsync(Patience);
        Assert.True(world.Coordinator.IsProcessing, "Coordinator must be processing while ASR is decoding");

        // The speech engine is hung decoding. The person presses Escape to discard the stuck take.
        var cancelTask = world.Coordinator.SubmitAsync(PushToTalkSignal.Cancelled);

        // deadline-fallback: both tasks must complete immediately upon Escape cancellation.
        var cancelResult = await cancelTask.WaitAsync(Patience);
        // deadline-fallback: the in-flight release shares completion with the Escape cancellation.
        var releaseResult = await releaseTask.WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, cancelResult.Disposition);
        Assert.Equal(SessionCommandDisposition.Applied, releaseResult.Disposition);
        Assert.True(world.Engine.Token!.Value.IsCancellationRequested, "In-flight transcription token must be cancelled immediately");

        // No text delivered
        Assert.Empty(world.Delivery.Requests);

        // State restored cleanly to Ready (controller idle)
        Assert.Null(world.Controller.CurrentSession);
        Assert.False(world.Coordinator.IsProcessing);
        Assert.False(world.CancelArmed[^1], "Cancel must be disarmed after Escape cancels transcription");

        // Must show Cancelled status, log DictationCancelled, and NEVER report Timed Out or session failure
        Assert.Contains(AppEventCode.DictationCancelled, world.Log.Events);
        Assert.Contains(world.View.Statuses, s => s.Text.Contains("Cancelled", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(world.View.Statuses, s => s.Text.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(world.View.Statuses, s => s.Text.Contains("failed", StringComparison.OrdinalIgnoreCase));

        // Lost-dictation banner must NOT be shown
        Assert.Empty(world.RecoveryStore.Saved);
        Assert.False(world.Persistence.HasPendingRecovery);
        Assert.DoesNotContain(world.View.Statuses, s => s.Text.Contains("recovered", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NextDictationSucceedsCleanlyAfterStuckTranscriptionEscape()
    {
        var world = ComposedSessionWorld.Create("first take");
        world.Engine.Hold = true;

        // 1. Stuck dictation cancelled by Escape
        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        var releaseTask = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        // deadline-fallback: wait for the engine to enter transcription before pressing Escape.
        await world.Engine.Entered.Task.WaitAsync(Patience);

        var cancelTask = world.Coordinator.SubmitAsync(PushToTalkSignal.Cancelled);
        // deadline-fallback: wait for the Escape cancellation to finish.
        await cancelTask.WaitAsync(Patience);
        // deadline-fallback: wait for the release task to finish.
        await releaseTask.WaitAsync(Patience);

        Assert.Null(world.Controller.CurrentSession);
        Assert.False(world.Coordinator.IsProcessing);

        // 2. Next dictation: unhung engine, fresh take
        world.Engine.Hold = false;
        var press2 = await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        Assert.Equal(SessionCommandDisposition.Applied, press2.Disposition);
        Assert.Equal(DictationSessionState.Recording, world.Controller.CurrentSession?.State);

        var release2 = await world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        Assert.Equal(SessionCommandDisposition.Applied, release2.Disposition);

        // Clean delivery of second take
        var request = Assert.Single(world.Delivery.Requests);
        Assert.Equal("first take", request.Text.Text);
        Assert.Null(world.Controller.CurrentSession);
        Assert.False(world.Coordinator.IsProcessing);
    }

    [Fact]
    public async Task EscapeDuringFinalizingStartupBeforeTranscriptionStartsCancelsCleanly()
    {
        var world = ComposedSessionWorld.Create("spoken words");
        world.LivePreviewEnabled = true;
        world.PreviewEngine.HoldStop = true;

        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        var releaseTask = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        // deadline-fallback: wait for FinalizeAsync to enter preview stop before _finalization.RunAsync starts.
        await world.PreviewEngine.StopEntered.Task.WaitAsync(Patience);
        Assert.Equal(DictationSessionState.Finalizing, world.Controller.CurrentSession?.State);

        var cancelTask = world.Coordinator.SubmitAsync(PushToTalkSignal.Cancelled);
        world.PreviewEngine.AllowStopExit.SetResult();

        // deadline-fallback: wait for the Escape cancellation to complete.
        var cancelResult = await cancelTask.WaitAsync(Patience);
        // deadline-fallback: wait for the release task to complete.
        var releaseResult = await releaseTask.WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, cancelResult.Disposition);
        Assert.Equal(SessionCommandDisposition.Applied, releaseResult.Disposition);
        Assert.Equal(0, world.Engine.Calls);
        Assert.Empty(world.Delivery.Requests);
        Assert.Null(world.Controller.CurrentSession);
        Assert.Contains(world.View.Statuses, s => s.Text.Contains("Cancelled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EscapeDuringTranscriptionWithEscapeRecoveryEnabledHoldsWordsInsteadOfDiscarding()
    {
        var world = ComposedSessionWorld.Create("words kept by escape recovery");
        world.Dictation = world.Dictation with { EscapeRecoveryEnabled = true };
        world.Engine.Hold = true;

        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        var releaseTask = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        // deadline-fallback: wait for the engine to enter transcription before pressing Escape.
        await world.Engine.Entered.Task.WaitAsync(Patience);

        // First Escape with EscapeRecoveryEnabled promotes the in-flight take to Escape Recovery
        // rather than cancelling the transcription token.
        var escapeTask = world.Coordinator.SubmitAsync(PushToTalkSignal.Cancelled);
        Assert.False(world.Engine.Token!.Value.IsCancellationRequested);

        world.Engine.Release();
        // deadline-fallback: wait for Escape Recovery finalization to complete.
        var escapeResult = await escapeTask.WaitAsync(Patience);
        // deadline-fallback: wait for the release task to complete.
        var releaseResult = await releaseTask.WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, escapeResult.Disposition);
        Assert.Equal(SessionCommandDisposition.Applied, releaseResult.Disposition);
        Assert.Empty(world.Delivery.Requests);
        Assert.Equal(["words kept by escape recovery"], world.RecoveryStore.Saved);
        Assert.True(world.Persistence.HasPendingRecovery);
    }

    [Fact]
    public async Task EscapeOnceDeliveryBeginsIsIgnoredAndDeliveryCompletes()
    {
        var world = ComposedSessionWorld.Create("delivered words");
        world.Delivery.Hold = true;

        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        var releaseTask = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        // deadline-fallback: wait for delivery to begin after transcription has completed.
        await world.Delivery.Entered.Task.WaitAsync(Patience);

        // Once transcription finishes and delivery starts, cancel must already be disarmed.
        Assert.False(world.CancelArmed[^1], "Cancel must be disarmed as soon as transcription completes");
        Assert.Equal(DictationSessionState.Delivering, world.Controller.CurrentSession?.State);

        // A late Cancelled signal arriving during delivery is ignored rather than aborting delivery.
        var lateCancel = await world.Coordinator.SubmitAsync(PushToTalkSignal.Cancelled);
        Assert.Equal(SessionCommandDisposition.Ignored, lateCancel.Disposition);

        world.Delivery.AllowExit.SetResult();
        // deadline-fallback: wait for the release and delivery to finish.
        var releaseResult = await releaseTask.WaitAsync(Patience);
        Assert.Equal(SessionCommandDisposition.Applied, releaseResult.Disposition);
        var request = Assert.Single(world.Delivery.Requests);
        Assert.Equal("delivered words", request.Text.Text);
    }

    [Fact]
    public void HotkeyEdgeTrackerArmsEscapeDuringRecordingAndTranscribingAndDisarmsWhenIdle()
    {
        var tracker = new HotkeyEdgeTracker(
            new HotkeyBinding(F8Key, HotkeyModifiers.None),
            new HotkeyBinding(EscapeKey, HotkeyModifiers.None),
            new HotkeyBinding('W', HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.PushToTalk);

        // 1. Idle: Escape must NOT be consumed
        var idleDown = tracker.Process(EscapeKey, isKeyDown: true, HotkeyModifiers.None);
        Assert.False(idleDown.Consume, "Escape in Idle must pass through to the active window");
        Assert.Null(idleDown.Signal);

        // 2. Recording active: Escape must be consumed and return Cancelled
        tracker.SetRecordingActive(true);
        tracker.SetCancelArmed(true);
        var recDown = tracker.Process(EscapeKey, isKeyDown: true, HotkeyModifiers.None);
        Assert.True(recDown.Consume, "Escape while recording must be consumed");
        Assert.Equal(PushToTalkSignal.Cancelled, recDown.Signal);
        var recUp = tracker.Process(EscapeKey, isKeyDown: false, HotkeyModifiers.None);
        Assert.True(recUp.Consume, "Escape release after cancel must be consumed");

        // 3. Transcribing: Recording is no longer active, but cancel stays armed!
        tracker.SetRecordingActive(false);
        tracker.SetCancelArmed(true);
        var transcribingDown = tracker.Process(EscapeKey, isKeyDown: true, HotkeyModifiers.None);
        Assert.True(transcribingDown.Consume, "Escape while transcribing must be consumed");
        Assert.Equal(PushToTalkSignal.Cancelled, transcribingDown.Signal);
        var transcribingUp = tracker.Process(EscapeKey, isKeyDown: false, HotkeyModifiers.None);
        Assert.True(transcribingUp.Consume);

        // 4. Disarmed (e.g. Entering Delivering or Idle): Escape must NOT be consumed
        tracker.SetCancelArmed(false);
        var disarmedDown = tracker.Process(EscapeKey, isKeyDown: true, HotkeyModifiers.None);
        Assert.False(disarmedDown.Consume, "Escape when disarmed must pass through to the active window");
        Assert.Null(disarmedDown.Signal);
    }

    [Fact]
    public async Task ComposedSessionArmsCancelDuringRecordingAndTranscribingAndDisarmsOnDeliveryAndIdle()
    {
        var world = ComposedSessionWorld.Create("delivery test text");

        // Initially Idle
        Assert.Empty(world.CancelArmed);

        // Press to talk starts recording
        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        Assert.True(world.CancelArmed[^1], "Cancel must be armed on recording start");

        // Release finishes recording and enters transcribing then delivering then idle
        await world.Coordinator.SubmitAsync(PushToTalkSignal.Released);

        // After completion, cancel must end up disarmed (false)
        Assert.False(world.CancelArmed[^1], "Cancel must be disarmed when dictation finishes");
        Assert.Contains(true, world.CancelArmed);
        Assert.Contains(false, world.CancelArmed);
    }

    [Fact]
    public async Task WorkerSupervisorCleanlyAbortsWedgedWorkerOnCancellation()
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.RuntimeWorker.exe");
        Assert.True(File.Exists(workerPath), $"Worker apphost missing: {workerPath}");

        await using var supervisor = new RuntimeWorkerSupervisor(
            workerPath,
            ["--test-transcribe-stub", "--transcribe-delay-ms", "60000"],
            maximumRestarts: 1);

        var startResult = await supervisor.StartAsync(TimeSpan.FromSeconds(5));
        Assert.True(startResult.Succeeded);
        var initialProcessId = supervisor.WorkerProcessId!.Value;
        using var initialWorker = Process.GetProcessById(initialProcessId);
        _ = initialWorker.SafeHandle;

        using var cts = new CancellationTokenSource();
        var request = new RuntimeWorkerTranscriptionRequest(
            Guid.NewGuid(),
            "unused-test-audio",
            SampleCount: 1);

        var transcribeTask = supervisor.TranscribeAsync(request, TimeSpan.FromSeconds(30), cts.Token);
        await WaitForWorkerReadyInRequestAsync(supervisor, transcribeTask);
        Assert.False(transcribeTask.IsCompleted);

        // Simulate Escape cancellation of stuck transcription
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await transcribeTask);
        Assert.Equal(RuntimeWorkerState.Stopped, supervisor.State);
        Assert.Null(supervisor.WorkerProcessId);
        Assert.Equal(1, supervisor.HandlesClosed);

        // deadline-fallback: the wedged worker process must be terminated and observed exited.
        Assert.True(initialWorker.WaitForExit(TimeSpan.FromSeconds(5)), "Wedged worker process must be terminated");

        // Second consecutive Escape cancellation on the default maximumRestarts: 1 budget
        using var cts2 = new CancellationTokenSource();
        var transcribeTask2 = supervisor.TranscribeAsync(request, TimeSpan.FromSeconds(30), cts2.Token);
        await WaitForWorkerReadyInRequestAsync(supervisor, transcribeTask2);
        Assert.False(transcribeTask2.IsCompleted);
        cts2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await transcribeTask2);
        Assert.Equal(RuntimeWorkerState.Stopped, supervisor.State);

        // A third request still restarts lazily because caller cancellations do not spend the crash budget
        using var cts3 = new CancellationTokenSource();
        var transcribeTask3 = supervisor.TranscribeAsync(request, TimeSpan.FromSeconds(30), cts3.Token);
        await WaitForWorkerReadyInRequestAsync(supervisor, transcribeTask3);
        Assert.Equal(RuntimeWorkerState.Ready, supervisor.State);
        Assert.NotNull(supervisor.WorkerProcessId);
        cts3.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await transcribeTask3);
    }

    private static async Task WaitForWorkerReadyInRequestAsync(
        RuntimeWorkerSupervisor supervisor,
        Task<RuntimeWorkerResponse?> requestTask)
    {
        var deadline = Stopwatch.StartNew();
        while (supervisor.State != RuntimeWorkerState.Ready || supervisor.WorkerProcessId is null)
        {
            Assert.True(deadline.Elapsed < Patience, "Worker did not reach Ready state for transcription request in time.");
            Assert.False(requestTask.IsCompleted, "Transcription request ended before worker reached Ready state.");
            // deadline-fallback: poll interval bounded by Patience while waiting for external worker process readiness.
            await Task.Delay(20);
        }

        // settle: allow the post-health transcribe line to flush into the ready worker's stdin.
        await Task.Delay(50);
    }
}
