using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.App.Composition;

/// <summary>The shell's leaf reads and notifications the session's wiring needs; nothing here sequences anything.</summary>
/// <remarks>
/// EVERY MEMBER IS A VALUE READ AT THE CALL OR ONE NOTIFICATION, and the review of this step reads each
/// body the shell supplies against that rule. The reads change under the session (an engine loads, a
/// word is taught, a setting is saved), which is why they are reads and not values. The one exception
/// is <see cref="TearDownSession"/>, the shell's session teardown, which a later step replaces with
/// owners of its own; it is named here so its presence is a declared debt, not a hidden one.
/// </remarks>
public sealed record SessionShell(
    ISessionView View,
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
/// here; this file decides how they are joined. The test project compiles this file as its own, so
/// the joins a test drives are the joins the app runs, not a copy of them.
/// </remarks>
public sealed record SessionCompositionParts(
    PushToTalkSessionController Controller,
    IAudioCapture Capture,
    SessionBackgroundWork Background,
    TranscriptFinalizer Finalizer,
    SessionPersistence Persistence,
    StreamingTranscriptionController Streaming,
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
        var runner = new SessionFinalizationRunner(
            parts.Controller,
            parts.Finalizer,
            parts.Persistence,
            parts.Streaming,
            new SessionFinalizationEffects(parts),
            parts.Clock);
        var executor = new DictationSessionExecutor(
            parts.Controller,
            parts.Background,
            runner,
            parts.Persistence,
            parts.Resources,
            new SessionEffects(parts));
        // Built beside the controller so a press captures its target and delivery choice from the
        // same provider the controller would have asked, at the instant of the key, before the
        // queue's first hop.
        return new DictationSessionCoordinator(executor, parts.Controller.CaptureStartContext, parts.Clock);
    }
}
