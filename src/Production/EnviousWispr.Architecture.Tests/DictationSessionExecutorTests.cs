using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The executor decides; the shell acts. These tests drive the real state machine with a fake microphone
/// and a fake shell that writes down every effect in the order it was asked for, so the order of effects
/// the old handler had is asserted rather than assumed.
/// </summary>
public sealed class DictationSessionExecutorTests
{
    [Fact]
    public async Task ADeniedAdmissionNeverOpensTheMicrophone()
    {
        var (executor, capture, effects, _) = Build(admission: new DictationAdmissionResult(
            DictationAdmissionStatus.LowMemory, CanStart: false, CanPersistRecovery: false));

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Null(result.Session);
        Assert.Equal(0, capture.StartCount);
        Assert.Equal(["EvaluateAdmission", "ShowMemoryCritical", "ReleaseProcessingDeadline", "RecordDictationEdge"], effects.Trace);
    }

    [Fact]
    public async Task PendingRecoveryBlocksAPressBeforeAdmissionIsEvenAsked()
    {
        var (executor, capture, effects, _) = Build();
        effects.HasPendingRecovery = true;

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Null(result.Session);
        Assert.Equal(0, capture.StartCount);
        Assert.Equal(["ShowRecoveredTextWaiting", "ReleaseProcessingDeadline", "RecordDictationEdge"], effects.Trace);
    }

    [Fact]
    public async Task LowDiskWarnsAndStillRecords()
    {
        var (executor, capture, effects, controller) = Build(admission: new DictationAdmissionResult(
            DictationAdmissionStatus.LowDisk, CanStart: true, CanPersistRecovery: false));
        effects.EscapeRecoveryEnabled = true;

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(1, capture.StartCount);
        Assert.Equal(DictationSessionState.Recording, controller.CurrentSession?.State);
        Assert.True(effects.EscapeRecoveryForSession, "the recording carries the setting it was started with");
        Assert.Equal(
            ["EvaluateAdmission", "ShowDiskLow", "RecordTransition:Started", "OnRecordingStarted", "ShowTransitionStatus:Started", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
        Assert.Equal(controller.CurrentSession?.Id, effects.StartedSession);
        Assert.Same(controller.CurrentSession, result.Session);
    }

    [Fact]
    public async Task AReleaseStopsTheWatchdogFirstAndFinalisesExactlyOnce()
    {
        var (executor, capture, effects, controller) = Build();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();

        var result = await executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(0, capture.CancelCount);
        Assert.Equal(
            ["StopRecordingWatchdog", "RecordTransition:FinalizeReady", "Finalize:recoveryOnly=False", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
        Assert.Single(effects.Finalized);
        Assert.Equal([0.2f], effects.Finalized[0].Samples.ToArray());
        Assert.Equal(DictationSessionState.Finalizing, result.Session?.State);
    }

    [Fact]
    public async Task EscapeWithRecoveryOnReleasesAndFinalisesForRecoveryOnly()
    {
        var (executor, capture, effects, _) = Build();
        effects.EscapeRecoveryEnabled = true;
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();

        var result = await executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Cancelled), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(0, capture.CancelCount);
        Assert.Equal(
            ["StopRecordingWatchdog", "RecordTransition:FinalizeReady", "Finalize:recoveryOnly=True", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task EscapeWithRecoveryOffCancelsStopsBackgroundWorkAndResets()
    {
        var (executor, capture, effects, controller) = Build();
        effects.EscapeRecoveryEnabled = false;
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();

        var result = await executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Cancelled), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal(DictationSessionState.Cancelled, result.Session?.State);
        Assert.Equal(0, capture.StopCount);
        Assert.Equal(1, capture.CancelCount);
        Assert.Null(controller.CurrentSession);
        Assert.False(effects.EscapeRecoveryForSession);
        Assert.Equal(
            ["StopRecordingWatchdog", "RecordTransition:Cancelled", "StopBackgroundWork", "ShowTransitionStatus:Cancelled", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task AReleaseWithNothingRecordingIsIgnoredAndStillShowsAStatus()
    {
        var (executor, _, effects, _) = Build();

        var result = await executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Null(result.Session);
        Assert.Equal(
            ["StopRecordingWatchdog", "RecordTransition:Ignored", "ShowTransitionStatus:Ignored", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task ATransitionThatThrowsIsRecordedAndRecoveredAsAFailure()
    {
        var (executor, capture, effects, _) = Build();
        capture.StartResultFactory = _ => throw new InvalidOperationException("synthetic microphone failure");

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Equal(
            ["EvaluateAdmission", "RecordSessionFailure", "RecoverFailedSession:InvalidTransition:Failed", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task ATransitionThatTimesOutIsRecoveredAsATimeout()
    {
        var (executor, capture, effects, _) = Build();
        capture.StartResultFactory = _ => throw new OperationCanceledException("synthetic deadline");

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Equal(
            ["EvaluateAdmission", "RecoverFailedSession:SessionTimedOut:TimedOut", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task AFinalisationThatThrowsIsRecoveredBeforeItsDeadlineIsReleased()
    {
        var (executor, _, effects, controller) = Build();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        var recording = controller.CurrentSession;
        effects.Trace.Clear();
        effects.FinalizeThrows = true;

        var result = await executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Equal(recording?.Id, result.Session?.Id);
        // THE ORDER IS THE POINT: recovery runs while the deadline is still armed, then the deadline is
        // released, then the edge - exactly as the shell always did it. Lock/suspend and shutdown cancel
        // that deadline from their own callbacks and must still find it during the recovery.
        Assert.Equal(
            ["StopRecordingWatchdog", "RecordTransition:FinalizeReady", "Finalize:recoveryOnly=False", "RecordSessionFailure", "RecoverFailedSession:InvalidTransition:Failed", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
        Assert.True(effects.DeadlineArmedDuringRecovery, "recovery ran after the deadline had already been released");
    }

    private static SessionCommand Press() => new(
        PushToTalkSignal.Pressed,
        new RecordingStartContext(new TargetWindowId(101), TextDeliveryOptions.Default));

    private static (DictationSessionExecutor Executor, FakeAudioCapture Capture, FakeEffects Effects, PushToTalkSessionController Controller) Build(
        DictationAdmissionResult? admission = null)
    {
        var capture = new FakeAudioCapture();
        var controller = new PushToTalkSessionController(
            capture,
            new FakeTargetProvider(101),
            minimumHoldDuration: TimeSpan.Zero);
        var effects = new FakeEffects
        {
            Admission = admission ?? new DictationAdmissionResult(
                DictationAdmissionStatus.Ready, CanStart: true, CanPersistRecovery: true),
        };
        return (new DictationSessionExecutor(controller, effects), capture, effects, controller);
    }

    private sealed class FakeEffects : IDictationSessionEffects
    {
        public List<string> Trace { get; } = [];

        public List<CapturedAudio> Finalized { get; } = [];

        public DictationSessionId? StartedSession { get; private set; }

        public required DictationAdmissionResult Admission { get; init; }

        public bool FinalizeThrows { get; set; }

        public bool DeadlineArmed { get; private set; }

        public bool DeadlineArmedDuringRecovery { get; private set; }

        public bool HasPendingRecovery { get; set; }

        public bool EscapeRecoveryEnabled { get; set; }

        public bool EscapeRecoveryForSession { get; set; }

        public DictationAdmissionResult EvaluateAdmission()
        {
            Trace.Add("EvaluateAdmission");
            return Admission;
        }

        public void ShowRecoveredTextWaiting() => Trace.Add("ShowRecoveredTextWaiting");

        public void ShowMemoryCritical() => Trace.Add("ShowMemoryCritical");

        public void ShowDiskLow() => Trace.Add("ShowDiskLow");

        public Task StopRecordingWatchdogAsync()
        {
            Trace.Add("StopRecordingWatchdog");
            return Task.CompletedTask;
        }

        public void RecordTransition(SessionTransitionResult result) => Trace.Add($"RecordTransition:{result.Kind}");

        public Task OnRecordingStartedAsync(DictationSessionId sessionId)
        {
            Trace.Add("OnRecordingStarted");
            StartedSession = sessionId;
            return Task.CompletedTask;
        }

        public Task FinalizeAsync(DictationSessionId sessionId, CapturedAudio audio, bool recoveryOnly)
        {
            Trace.Add($"Finalize:recoveryOnly={recoveryOnly}");
            DeadlineArmed = true;
            Finalized.Add(audio);
            if (FinalizeThrows)
            {
                throw new InvalidOperationException("synthetic finalisation failure");
            }

            return Task.CompletedTask;
        }

        public Task StopBackgroundWorkAsync()
        {
            Trace.Add("StopBackgroundWork");
            return Task.CompletedTask;
        }

        public void ShowTransitionStatus(SessionTransitionResult result) => Trace.Add($"ShowTransitionStatus:{result.Kind}");

        public void RecordSessionFailure() => Trace.Add("RecordSessionFailure");

        public void ReleaseProcessingDeadline()
        {
            Trace.Add("ReleaseProcessingDeadline");
            DeadlineArmed = false;
        }

        public Task RecoverFailedSessionAsync(AppError failure, SessionFailureKind kind)
        {
            Trace.Add($"RecoverFailedSession:{failure.Code}:{kind}");
            DeadlineArmedDuringRecovery = DeadlineArmed;
            return Task.CompletedTask;
        }

        public Task RecordDictationEdgeAsync()
        {
            Trace.Add("RecordDictationEdge");
            return Task.CompletedTask;
        }

        public void RecordInterruptionFailure() => Trace.Add("RecordInterruptionFailure");

        public void ShowInterruptionPending() => Trace.Add("ShowInterruptionPending");

        public void ShowInterruptionPreserving(SystemLifecycleTransition transition) => Trace.Add($"ShowInterruptionPreserving:{transition}");

        public void RecordRecordingTimedOut(AppError failure) => Trace.Add($"RecordRecordingTimedOut:{failure.Code}");

        public void ShowRecordingTimedOut() => Trace.Add("ShowRecordingTimedOut");
    }

    private sealed class FakeTargetProvider(nint window) : IForegroundTargetProvider
    {
        public TargetWindowId? CaptureForegroundTarget() => new TargetWindowId(window);
    }

    private sealed class FakeAudioCapture : IAudioCapture
    {
        private static readonly float[] OneSample = [0.2f];
        private DictationSessionId _sessionId;

        public event EventHandler<AudioLevel>? LevelChanged
        {
            add { }
            remove { }
        }

        public bool IsCapturing { get; private set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int CancelCount { get; private set; }

        public Func<AudioCaptureRequest, AudioOperationResult>? StartResultFactory { get; set; }

        public Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            StartCount++;
            _sessionId = request.SessionId;
            var result = StartResultFactory?.Invoke(request) ?? new AudioOperationResult(Succeeded: true);
            IsCapturing = result.Succeeded;
            return Task.FromResult(result);
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            IsCapturing = false;
            return Task.FromResult(new CapturedAudio(_sessionId, OneSample, SampleRate: 16_000, Channels: 1));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            CancelCount++;
            IsCapturing = false;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
