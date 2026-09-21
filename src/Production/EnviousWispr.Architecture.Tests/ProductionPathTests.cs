using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Reliability;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// What the production session does at the moments the shell used to prove with substitutes: an
/// exit during the engine's work, a lock during the polish, a shutdown during an escape recovery.
/// Every proof runs the app's own composition (<see cref="ComposedSessionWorld"/>); the recovery
/// store, where it matters, is the production one on a file.
/// </summary>
public sealed class ProductionPathTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ExitDuringAsrUsesProductionCancellation()
    {
        // THE SHELL'S EXIT POLICY, THROUGH THE PRODUCTION PATH. The engine is inside a transcription;
        // the shell cancels processing and asks for the shutdown, as its lifetime does. The cancel
        // reaches the engine on the executor's own processing token; the engine honours it; the
        // finalisation ends as a timed-out dictation recovered safely - nothing is delivered, nothing
        // is pasted into an app that is leaving - and the shutdown, waiting for that, is clean.
        var world = ComposedSessionWorld.Create("hello world");
        await world.PressAsync();
        world.Engine.Hold = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(Patience);
        Assert.False(world.Engine.Token!.Value.IsCancellationRequested);

        world.Coordinator.CancelProcessing();
        var shutdown = world.Coordinator.ShutdownAsync(Patience);

        Assert.True(world.Engine.Token.Value.IsCancellationRequested, "the exit's cancel reached the engine on the production token");
        var result = await release.WaitAsync(Patience);
        var report = await shutdown.WaitAsync(Patience);
        Assert.True(report.Clean);
        Assert.Equal(1, world.Engine.Calls);
        Assert.Empty(world.Delivery.Requests);
        Assert.Contains(AppEventCode.DictationSessionRecovered, world.Log.Events);
        Assert.Contains(world.View.Statuses, status => status.Text.Contains("recovered safely", StringComparison.Ordinal));
        Assert.Null(world.Controller.CurrentSession);
        Assert.Equal(1, world.TearDowns);
        Assert.True(world.Capture.Disposed);
        Assert.NotEqual(SessionCommandDisposition.Applied, result.Disposition);
    }

    [Fact]
    public async Task AFaultedDeliveryIsLoggedByItsStageAndKindAndTheWordsAreKept()
    {
        // THE DELIVERY NAMES A DEFECT; THE COMPOSED SESSION LOGS IT AS ONE (plan-2 step 13). The
        // route answers DeliveryFaulted - a null inside the commit - and the production effects write
        // TextDeliveryFailed with the DeliveryFaulted code, the stage and the fault's family, and
        // nothing else about it; the pill says the text is held; the recovery copy is not cleared,
        // so the words are on Home. A build that mapped every failure to "unsupported target" fails
        // on the code; one that logged the type name would fail the privacy dictionary.
        var world = ComposedSessionWorld.Create("hello world");
        world.Delivery.Answer = new DeliveryResult(
            default,
            Delivered: false,
            ClipboardFallback: false,
            RefusalReason: TextDeliveryRefusalReason.DeliveryFaulted,
            Fault: new DeliveryFault(DeliveryStage.Commit, DeliveryFaultKind.NullReference, nameof(NullReferenceException)));
        await world.PressAsync();

        var released = await world.Coordinator.SubmitAsync(PushToTalkSignal.Released).WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, released.Disposition);
        var failed = Assert.Single(world.Log.Entries, entry => entry.Event == AppEventCode.TextDeliveryFailed);
        Assert.Equal(AppErrorCode.DeliveryFaulted, failed.ErrorCode);
        Assert.Equal(AppFailureCategory.TextDelivery, failed.Failure);
        Assert.Equal(DeliveryStage.Commit, failed.DeliveryStage);
        Assert.Equal(DeliveryFaultKind.NullReference, failed.Fault);
        Assert.DoesNotContain(world.Log.Entries, entry => entry.ErrorCode == AppErrorCode.DeliveryUnsupportedTarget);
        Assert.Contains(world.View.Deliveries, delivery => delivery.Delivered.Text == "Text delivery failed unexpectedly. Text is held safely in memory");
        Assert.Equal(["hello world"], world.RecoveryStore.Saved);
        Assert.Equal(0, world.RecoveryStore.Cleared);
        Assert.True(world.Persistence.HasPendingRecovery, "the words wait on Home");
        Assert.Equal(1, world.Delivery.Deliveries);
    }

    [Theory]
    [InlineData(TextDeliveryRefusalReason.AccessibilityUnavailable, true, AppEventCode.TextDeliveryRefused, AppErrorCode.DeliveryAccessibilityUnavailable, "Windows accessibility did not answer, so the text was copied only. Press Ctrl+V")]
    [InlineData(TextDeliveryRefusalReason.AccessibilityUnavailable, false, AppEventCode.TextDeliveryFailed, AppErrorCode.DeliveryAccessibilityUnavailable, "Windows accessibility did not answer. Text is held safely in memory")]
    [InlineData(TextDeliveryRefusalReason.DirectWriteUnverified, false, AppEventCode.TextDeliveryFailed, AppErrorCode.DeliveryUnverified, "Insertion could not be verified. Text is held safely in memory")]
    [InlineData(TextDeliveryRefusalReason.Cancelled, false, AppEventCode.TextDeliveryFailed, AppErrorCode.DeliveryCancelled, "Text delivery was cancelled. Text is held safely in memory")]
    [InlineData(TextDeliveryRefusalReason.DeliveryDisposed, false, AppEventCode.TextDeliveryFailed, AppErrorCode.DeliveryDisposed, "Text delivery was no longer available. Text is held safely in memory")]
    [InlineData(TextDeliveryRefusalReason.DeliveryFaulted, false, AppEventCode.TextDeliveryFailed, AppErrorCode.DeliveryFaulted, "Text delivery failed unexpectedly. Text is held safely in memory")]
    [InlineData(TextDeliveryRefusalReason.ProtectedField, true, AppEventCode.TextDeliveryRefused, AppErrorCode.DeliveryProtectedField, "Protected field: copied only. Paste manually if intended")]
    [InlineData(TextDeliveryRefusalReason.UnsupportedTarget, true, AppEventCode.TextDeliveryRefused, AppErrorCode.DeliveryUnsupportedTarget, "Automatic paste is unsafe here, so the text was copied only")]
    [InlineData(TextDeliveryRefusalReason.ClipboardUnavailable, false, AppEventCode.TextDeliveryFailed, AppErrorCode.DeliveryClipboardUnavailable, "Clipboard unavailable. Text is held safely in memory")]
    public async Task EveryUndeliveredEndingKeepsItsNameInTheLogAndOnThePill(
        TextDeliveryRefusalReason reason,
        bool clipboardFallback,
        AppEventCode expectedEvent,
        AppErrorCode expectedCode,
        string expectedSentence)
    {
        // THE SAME NAME IN THREE PLACES (plan-2 step 13): the result's refusal, the log's error code
        // and the pill's sentence agree on which way the words did not land, through the production
        // effects. Accessibility that did not answer, an unverified insertion, a cancellation, a
        // delivery no longer available and a fault each read as themselves; none of them reads as
        // "unsupported target", and no two share a sentence.
        var world = ComposedSessionWorld.Create("hello world");
        world.Delivery.Answer = new DeliveryResult(
            default,
            Delivered: false,
            ClipboardFallback: clipboardFallback,
            clipboardFallback ? TextDeliveryRoute.ClipboardOnly : TextDeliveryRoute.None,
            reason);
        await world.PressAsync();

        var released = await world.Coordinator.SubmitAsync(PushToTalkSignal.Released).WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, released.Disposition);
        var ending = Assert.Single(world.Log.Entries, entry => entry.Event is AppEventCode.TextDeliveryRefused or AppEventCode.TextDeliveryFailed or AppEventCode.TextDeliveryCompleted or AppEventCode.TextDeliveryClipboardFallback);
        Assert.Equal(expectedEvent, ending.Event);
        Assert.Equal(expectedCode, ending.ErrorCode);
        Assert.Equal(AppFailureCategory.TextDelivery, ending.Failure);
        Assert.Null(ending.Fault);
        Assert.Null(ending.DeliveryStage);
        Assert.Equal(expectedSentence, Assert.Single(world.View.Deliveries).Delivered.Text);
    }

    [Fact]
    public async Task LockDuringPolishPreservesLastGoodText()
    {
        // THE LOCK ARRIVES WHILE THE POLISH IS RUNNING. The lock's cancel reaches the polish provider
        // on the production token; the deterministic pass's words - the last good text, written to
        // the recovery copy before the polish was tried - are what survive: nothing polished is
        // invented, and the words are on Home for the next launch.
        var world = ComposedSessionWorld.Create("um hello world");
        var polish = new HeldPolish();
        world.Polish = new PolishSetup(polish, UsesLocalRuntime: true, RuntimeResourceKind.Cpu);
        await world.PressAsync();
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await polish.Entered.Task.WaitAsync(Patience);
        Assert.Equal(["hello world"], world.RecoveryStore.Saved);

        var locked = world.Coordinator.InterruptAsync(SystemLifecycleTransition.SessionLocked);

        Assert.True(polish.Token!.Value.IsCancellationRequested, "the lock's cancel reached the polish on the production token");
        var released = await release.WaitAsync(Patience);
        var lockResult = await locked.WaitAsync(Patience);
        // The cut polish ended the release before the lock's turn came; the lock then found no
        // dictation to interrupt and was ignored - its work was the cancel, already done.
        Assert.Equal(SessionCommandDisposition.Ignored, lockResult.Disposition);
        Assert.Equal(["hello world"], world.RecoveryStore.Saved);
        Assert.True(world.Persistence.HasPendingRecovery, "the last good text waits on Home");
        Assert.DoesNotContain(world.Delivery.Requests, request => request.Text.Text.Contains("polished", StringComparison.Ordinal));
        Assert.DoesNotContain(world.HistoryStore.Added, entry => entry.Text.Contains("polished", StringComparison.Ordinal));
        Assert.Equal(1, polish.Calls);
        Assert.Null(world.Controller.CurrentSession);
        Assert.NotEqual(SessionCommandDisposition.Ignored, released.Disposition);
    }

    [Fact]
    public async Task ALockWhileRecordingLeavesItsOwnPreservationTranscriptionUncancelled()
    {
        // NOTHING IS IN FLIGHT WHEN THE LOCK ARRIVES: the recording is open and the queue idle, so the
        // interruption runs at once and preserves the take by transcribing it. The interruption's
        // cancel is for the finalisation it was queued behind - there was none - and the transcription
        // it starts itself runs on an uncancelled token to the end: the words are kept for recovery.
        var world = ComposedSessionWorld.Create("hello world");
        await world.PressAsync();
        world.Engine.Hold = true;

        var locked = world.Coordinator.InterruptAsync(SystemLifecycleTransition.SessionLocked);
        await world.Engine.Entered.Task.WaitAsync(Patience);

        Assert.False(world.Engine.Token!.Value.IsCancellationRequested, "the interruption cancelled its own preservation transcription");
        Assert.Contains(world.View.Statuses, status => status.Text.Contains("Windows locked", StringComparison.Ordinal));
        world.Engine.Release();
        Assert.Equal(SessionCommandDisposition.Applied, (await locked.WaitAsync(Patience)).Disposition);
        Assert.Equal(1, world.Engine.Calls);
        Assert.Equal(["hello world"], world.RecoveryStore.Saved);
        Assert.Null(world.Controller.CurrentSession);
    }

    [Fact]
    public async Task ShutdownDuringRecoveryDoesNotTouchDisposedStores()
    {
        // AN ESCAPE RECOVERY IS TRANSCRIBING WHEN THE SHUTDOWN COMES. The recovery store is the
        // production one on a file. The shutdown does not tear anything down beside the recovery and
        // does not dispose the store under it; when the engine answers, the words land in the store,
        // and a store opened afresh on the file reads them. Only then is the store disposed.
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "recovery.bin");
        var store = new WindowsRecoveryTextStore(path);
        var world = ComposedSessionWorld.Create("hello world", null, new FakeRunState(), Guid.NewGuid(), store);
        world.Dictation = DictationPreferences.Default with { EscapeRecoveryEnabled = true };
        await world.PressAsync();
        world.Engine.Hold = true;
        var cancel = world.Coordinator.SubmitAsync(PushToTalkSignal.Cancelled);
        await world.Engine.Entered.Task.WaitAsync(Patience);

        var report = await world.Coordinator.ShutdownAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Patience);

        Assert.Equal(ShutdownOutcome.Unclean, report.Outcome);
        Assert.True(report.CommandOutstanding);
        Assert.Null(report.Teardown);
        Assert.Equal(0, world.TearDowns);
        Assert.False(world.Capture.Disposed);

        world.Engine.Release();
        Assert.Equal(SessionCommandDisposition.Applied, (await cancel.WaitAsync(Patience)).Disposition);
        Assert.Empty(world.Delivery.Requests);
        var loaded = await store.LoadAsync();
        Assert.Equal(RecoveryTextLoadStatus.Found, loaded.Status);
        Assert.Equal("hello world", loaded.Record?.Text);
        Assert.Equal(0, world.TearDowns);

        store.Dispose();
        using var reopened = new WindowsRecoveryTextStore(path);
        Assert.Equal("hello world", (await reopened.LoadAsync()).Record?.Text);
    }

    /// <summary>A polish provider that holds until released and honours the token it is handed.</summary>
    private sealed class HeldPolish : IPolishProvider
    {
        public string ProviderId => "held";

        public int Calls { get; private set; }

        public CancellationToken? Token { get; private set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PolishResult> TryPolishAsync(PolishRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Token = cancellationToken;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new PolishResult(request.Input with { Text = request.Input.Text + " polished" }, PolishAttemptStatus.Polished);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
