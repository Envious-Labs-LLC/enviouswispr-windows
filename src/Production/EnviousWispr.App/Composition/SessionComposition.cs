using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.App.Composition;

/// <summary>The shell's leaf reads and notifications the session's wiring needs; nothing here sequences anything.</summary>
/// <remarks>
/// EVERY MEMBER IS A VALUE READ AT THE CALL, ONE NOTIFICATION, OR ONE OPERATION. The reads change
/// under the session (an engine loads, a word is taught, a setting is saved), which is why they are
/// reads and not values. The three teardown members are the shell's parts of the session's disposal,
/// one operation each, called in the executor's order (<c>DictationSessionExecutor.DisposeSessionAsync</c>:
/// observers off the capture, the controller disposed by the executor itself, the references let go,
/// the route disposed) once the session is quiescent and the background work has stopped under the
/// shutdown's budget; the shell decides nothing about when. <see cref="AttachedSession"/> is the
/// shell's own view of the session in flight - null once <see cref="ReleaseSession"/> has let go of
/// the controller, which a disposed controller does not say for itself. The inventory of every
/// member's body, and of every other callback the shell supplies, is the "Session ownership
/// inventory" in <c>.claude/knowledge/pipeline.md</c>.
/// </remarks>
public sealed record SessionShell(
    ISessionView View,
    Func<DictationSessionSnapshot?> AttachedSession,
    Func<DictationPreferences> Dictation,
    Func<ITranscriptionEngine?> Engine,
    Func<ITextDelivery?> Delivery,
    Func<FinalizationOptions> Options,
    Func<string?> CloudPolishProviderName,
    Func<Guid?> RunId,
    Action<bool> RecordingActive,
    Action<CapturedAudio> ArchiveAudio,
    Action DetachCaptureObservers,
    Action ReleaseSession,
    Action DisposeDeliveryRoute);

/// <summary>Everything the session's production wiring is built from.</summary>
/// <remarks>
/// THE SHELL CHOOSES THE CONCRETE THINGS - which capture, which probe, which stores - and hands them
/// here with the long-lived owners <see cref="RuntimeComposition"/> built; this file decides how
/// they are joined. The test project compiles this file as its own, so
/// the joins a test drives are the joins the app runs, not a copy of them.
/// </remarks>
public sealed record SessionCompositionParts(
    PushToTalkSessionController Controller,
    IAudioCapture Capture,
    SessionRuntime Runtime,
    ISystemResourceProbe Resources,
    IApplicationRunStateStore RunState,
    IAppLogger Logger,
    SessionShell Shell,
    TimeProvider Clock);

/// <summary>Joins the session's owners into the coordinator the shell submits to.</summary>
/// <remarks>
/// ONE PLACE THE APP AND ITS TESTS BOTH CALL. Before this the runner, the executor and the coordinator
/// were built inline in the shell, behind a window, so the only proof that the production joins held
/// was a journey through the built app. Now the same method builds them for a test with fakes at the
/// leaves - a fake capture, a fake engine, a fake delivery route, a fake window - and the test drives
/// the coordinator the app would have.
/// </remarks>
public static class SessionComposition
{
    public static DictationSessionCoordinator Compose(SessionCompositionParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var runtime = parts.Runtime;
        var runner = new SessionFinalizationRunner(
            parts.Controller,
            runtime.Finalizer,
            runtime.Persistence,
            runtime.Streaming,
            new SessionFinalizationEffects(parts),
            parts.Clock);
        var executor = new DictationSessionExecutor(
            parts.Controller,
            runtime.Background(),
            runner,
            runtime.Persistence,
            parts.Resources,
            new SessionEffects(parts));
        RecordingFlag.Follow(parts.Controller, parts.Shell.RecordingActive);
        // Built beside the controller so a press captures its target and delivery choice from the
        // same provider the controller would have asked, at the instant of the key, before the
        // queue's first hop.
        return new DictationSessionCoordinator(executor, parts.Controller.CaptureStartContext, parts.Clock);
    }
}

/// <summary>Tells the hook whether a recording is running, read from the controller's own session changes. Ref: #86.</summary>
/// <remarks>
/// THE CONTROLLER IS THE ONE PLACE EVERY ENDING PASSES: a release, a cancel, a failed stop, and the watchdog's and the
/// lifecycle recovery's abort all change its session and raise <see cref="PushToTalkSessionController.SessionChanged"/>.
/// The flag used to be set from the transitions the executor recorded, and the two aborts record none, so after a
/// timeout the hook swallowed Escape everywhere until the next recording. A reset raises nothing, and needs nothing:
/// it only follows an ending, which already said false. Only a change is passed on, so the hook hears each edge once.
/// </remarks>
internal sealed class RecordingFlag
{
    private readonly Action<bool> _recordingActive;
    private readonly Lock _sync = new();
    private bool _active;

    private RecordingFlag(Action<bool> recordingActive) => _recordingActive = recordingActive;

    public static void Follow(PushToTalkSessionController controller, Action<bool> recordingActive)
    {
        var flag = new RecordingFlag(recordingActive);
        controller.SessionChanged += (_, session) => flag.Set(session.State == DictationSessionState.Recording);
    }

    private void Set(bool active)
    {
        lock (_sync)
        {
            if (active == _active)
            {
                return;
            }

            _active = active;
            _recordingActive(active);
        }
    }
}
