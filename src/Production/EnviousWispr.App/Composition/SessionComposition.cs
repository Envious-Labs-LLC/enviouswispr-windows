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
/// EVERY MEMBER IS A VALUE READ AT THE CALL, ONE NOTIFICATION, OR ONE DISPOSAL LIST. The reads change
/// under the session (an engine loads, a word is taught, a setting is saved), which is why they are
/// reads and not values. <see cref="TearDownSession"/> is the shell's own disposals - the capture's
/// event, the controller, the delivery route - and nothing about when: the executor's teardown runs
/// it only once the session is quiescent and the background work has stopped under the shutdown's
/// budget (<c>DictationSessionExecutor.TearDownAsync</c>), and a budget that runs out first runs
/// nothing. <see cref="AttachedSession"/> is the shell's own view of the session in flight - null
/// once its teardown has let go of the controller, which a disposed controller does not say for
/// itself. The inventory of every member's body, and of every other callback the shell supplies, is
/// the "Session ownership inventory" in <c>.claude/knowledge/pipeline.md</c>.
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
    Func<Task> TearDownSession);

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
        // Built beside the controller so a press captures its target and delivery choice from the
        // same provider the controller would have asked, at the instant of the key, before the
        // queue's first hop.
        return new DictationSessionCoordinator(executor, parts.Controller.CaptureStartContext, parts.Clock);
    }
}
