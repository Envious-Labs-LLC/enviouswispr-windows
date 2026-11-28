using System.Buffers.Binary;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Unit and composed integration tests covering Wave 1A front-half hardening (`F1`, `F12`, `F14`,
/// `F17`, `F2`, `F9`, `F10`, `F15`, `F5`), verifying both positive contracts and refusal/negative paths.
/// </summary>
public sealed class Wave1HotkeyAndAudioHardeningTests
{
    private const uint LeftControl = 0xA2;
    private const uint RightControl = 0xA3;
    private const uint LeftAlt = 0xA4;
    private const uint LeftShift = 0xA0;
    private const uint LeftWindows = 0x5B;
    private const uint LetterC = 0x43;
    private const uint LetterV = 0x56;
    private const uint LetterW = 0x57;
    private const uint F8 = 0x77;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    // ---- F1: stuck-state reset, _heldOrdinaryKeys poisoning, physical key reconciliation ----

    [Fact]
    public void ResetHeldKeysClearsOrphanedOrdinaryKeysAndModifiersSoCtrlWinWorksAgain()
    {
        var held = new HeldModifierKeys();
        var tracker = new HotkeyEdgeTracker(
            new HotkeyBinding(0, HotkeyModifiers.Control | HotkeyModifiers.Windows),
            new HotkeyBinding(HotkeyEdgeTracker.EscapeVirtualKey, HotkeyModifiers.None),
            new HotkeyBinding(LetterW, HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.PushToTalk);

        // Simulate orphaned key-down of 'C' and held modifiers (e.g. UAC / Win+L swallowed key-ups).
        held.Observe(LeftControl, isKeyDown: true, _ => false);
        held.Observe(LeftWindows, isKeyDown: true, key => key == LeftControl);
        tracker.Process(LetterC, isKeyDown: true, HotkeyModifiers.Control);
        Assert.True(held.HasAny);
        Assert.True(tracker.HasHeldState);

        // MenuKeyMask (0xE8) reaching tracker must never poison _heldOrdinaryKeys either.
        tracker.Process(MenuKeyMask.VirtualKeyUnassigned, isKeyDown: true, HotkeyModifiers.None);

        held.Clear();
        tracker.ResetHeldKeys();

        Assert.False(held.HasAny);
        Assert.False(tracker.HasHeldState);

        // Fresh Ctrl+Win press must now engage cleanly without poisoned _heldOrdinaryKeys blocking it.
        var ctrlMods = held.Observe(LeftControl, isKeyDown: true, _ => false);
        tracker.Process(LeftControl, isKeyDown: true, HotkeyModifiers.None, ctrlMods);
        var winMods = held.Observe(LeftWindows, isKeyDown: true, key => key == LeftControl);
        var started = tracker.Process(LeftWindows, isKeyDown: true, HotkeyModifiers.Control, winMods);
        Assert.Equal(PushToTalkSignal.Pressed, started.Signal);
    }

    [Fact]
    public void PhysicalReconciliationPurgesOrphanedUnconsumedModifiersAndKeysWhilePreservingLiveHolds()
    {
        var clock = new Deterministic.ManualClock();
        var held = new HeldModifierKeys(clock);
        var tracker = new HotkeyEdgeTracker(F8, HotkeyModifiers.None);

        held.Observe(LeftControl, isKeyDown: true, _ => false);
        held.Observe(LeftShift, isKeyDown: true, key => key == LeftControl);
        tracker.Process(LetterC, isKeyDown: true, HotkeyModifiers.Control);
        Assert.True(tracker.HasHeldState);

        // Within 100ms grace period right after key-down, even false GetAsyncKeyState must NOT purge modifier.
        clock.Advance(TimeSpan.FromMilliseconds(40));
        Assert.Empty(held.Reconcile(_ => false, out _));

        // Negative guard: while GetAsyncKeyState says all tracked keys are still physically down after grace, keep them.
        clock.Advance(TimeSpan.FromMilliseconds(80));
        var noneMissingMods = held.Reconcile(_ => true, out _);
        Assert.Empty(noneMissingMods);
        tracker.ReconcileHeldKeys(_ => true);
        Assert.True(tracker.HasHeldState);

        // Now simulate lost WM_KEYUP past grace period: only Shift remains down; Control and ordinary 'C' are physically up.
        var purgedModifiers = held.Reconcile(vk => vk == LeftShift, out var observedMods);
        Assert.Equal([LeftControl], purgedModifiers);
        Assert.Equal(HotkeyModifiers.Shift, observedMods);

        tracker.ReconcileHeldKeys(vk => vk == LeftShift);
        Assert.False(tracker.HasHeldState);
    }

    [Fact]
    public void ResetHeldKeysCanPreserveActiveRecordingWhenKeybindCaptureBegins()
    {
        var tracker = new HotkeyEdgeTracker(F8, HotkeyModifiers.None);
        var pressed = tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);
        Assert.Equal(PushToTalkSignal.Pressed, pressed.Signal);

        tracker.Process(LetterC, isKeyDown: true, HotkeyModifiers.None);
        tracker.ResetHeldKeys(preserveActiveRecording: true);

        // Orphaned non-recording state is cleared while active F8 recording still emits Released on key-up.
        Assert.False(tracker.HasHeldState);
        var released = tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);
        Assert.Equal(PushToTalkSignal.Released, released.Signal);
    }

    [Fact]
    public void ReconcileHeldKeysDoesNotDropConsumedRecordOrPasteLastHoldsWhileResetClearsAll()
    {
        const HotkeyModifiers pasteMods = HotkeyModifiers.Alt | HotkeyModifiers.Shift;
        var tracker = new HotkeyEdgeTracker(
            new HotkeyBinding(F8, HotkeyModifiers.None),
            new HotkeyBinding(HotkeyEdgeTracker.EscapeVirtualKey, HotkeyModifiers.None),
            new HotkeyBinding(LetterW, HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.PushToTalk,
            typesCharacter: (_, _) => false,
            pasteLast: new HotkeyBinding(LetterV, pasteMods));

        // Consumed F8 PTT down -> GetAsyncKeyState reports false because WH_KEYBOARD_LL swallowed WM_KEYDOWN.
        var f8Down = tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);
        Assert.True(f8Down.Consume);
        Assert.Equal(PushToTalkSignal.Pressed, f8Down.Signal);

        tracker.ReconcileHeldKeys(_ => false);

        var f8Up = tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);
        Assert.True(f8Up.Consume);
        Assert.Equal(PushToTalkSignal.Released, f8Up.Signal);

        // Consumed PasteLast down -> ReconcileHeldKeys(_ => false) must not disarm it before key-up.
        var vDown = tracker.Process(LetterV, isKeyDown: true, pasteMods);
        Assert.True(vDown.Consume);
        Assert.Null(vDown.Signal);

        tracker.ReconcileHeldKeys(_ => false);

        var vUp = tracker.Process(LetterV, isKeyDown: false, HotkeyModifiers.None);
        Assert.True(vUp.Consume);
        Assert.Equal(PushToTalkSignal.PasteLast, vUp.Signal);

        // ResetHeldKeys on lock/suspend/capture still clears consumed holds cleanly.
        _ = tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);
        tracker.ResetHeldKeys();
        var afterResetUp = tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);
        Assert.False(afterResetUp.Consume);
        Assert.Null(afterResetUp.Signal);
    }

    // ---- F12: Toggle mode honored on Ctrl+Win & modifier-only bindings ----

    [Fact]
    public void ToggleModeGesturePolicyStartsAndDoesNotStopOnReleaseForHoldOrSingleTap()
    {
        var ms = (int value) => TimeSpan.FromMilliseconds(value);
        var policy = new HotkeyGesturePolicy(RightControl, needsHoldThreshold: true, toggleMode: true);

        // Hold past 200ms threshold: starts on press, promotes via ToggleStarted at threshold, stays running on release,
        // and stops cleanly on the next hold+release.
        Assert.Equal(HotkeyGestureOutcome.HoldStarted, policy.Process(RightControl, isKeyDown: true, ms(0)));
        Assert.Equal(HotkeyGestureOutcome.ToggleStarted, policy.Elapsed(ms(250)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(RightControl, isKeyDown: false, ms(300)));
        Assert.True(policy.IsRecordingHandsFree);
        Assert.Null(policy.NextDeadline);

        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(RightControl, isKeyDown: true, ms(700)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Elapsed(ms(950)));
        Assert.Equal(HotkeyGestureOutcome.ToggleStopped, policy.Process(RightControl, isKeyDown: false, ms(1_000)));
        Assert.False(policy.IsRecordingHandsFree);
        Assert.Null(policy.NextDeadline);

        // Brief single tap (< 200ms) in Toggle mode: does NOT discard at MultiTapWindow; next tap stops after MultiTapWindow.
        Assert.Equal(HotkeyGestureOutcome.HoldStarted, policy.Process(RightControl, isKeyDown: true, ms(1_200)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(RightControl, isKeyDown: false, ms(1_260)));
        Assert.Equal(HotkeyGestureOutcome.ToggleStarted, policy.Elapsed(ms(1_700)));
        Assert.True(policy.IsRecordingHandsFree);

        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(RightControl, isKeyDown: true, ms(2_000)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(RightControl, isKeyDown: false, ms(2_050)));
        Assert.Equal(HotkeyGestureOutcome.ToggleStopped, policy.Elapsed(ms(2_400)));
        Assert.False(policy.IsRecordingHandsFree);

        // Negative control: pressing RightControl+C typing chord still aborts and discards immediately.
        Assert.Equal(HotkeyGestureOutcome.HoldStarted, policy.Process(RightControl, isKeyDown: true, ms(2_500)));
        Assert.Equal(HotkeyGestureOutcome.Discarded, policy.Process(LetterC, isKeyDown: true, ms(2_530)));
        policy.Process(RightControl, isKeyDown: false, ms(2_560));
    }

    [Fact]
    public void CtrlWinInToggleModePromotesTapAndKeepsRecordingUntilNextPress()
    {
        var tracker = new HotkeyEdgeTracker(
            new HotkeyBinding(0, HotkeyModifiers.Control | HotkeyModifiers.Windows),
            new HotkeyBinding(HotkeyEdgeTracker.EscapeVirtualKey, HotkeyModifiers.None),
            new HotkeyBinding(LetterW, HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.Toggle);
        var held = new HeldModifierKeys();

        // Hold > 200ms then release: starts immediately, promotes at 200ms, does NOT stop on release.
        var m1 = held.Observe(LeftControl, isKeyDown: true, _ => false);
        tracker.Process(LeftControl, isKeyDown: true, HotkeyModifiers.None, m1);
        var m2 = held.Observe(LeftWindows, isKeyDown: true, vk => vk == LeftControl);
        var started = tracker.Process(LeftWindows, isKeyDown: true, HotkeyModifiers.Control, m2);
        Assert.Equal(PushToTalkSignal.Pressed, started.Signal);
        Assert.True(started.IsHandsFree);
        tracker.SetRecordingActive(true);

        Thread.Sleep(HotkeyGesturePolicy.ModifierHoldThreshold + TimeSpan.FromMilliseconds(40));
        Assert.Null(tracker.TickDecision());

        var m3 = held.Observe(LeftWindows, isKeyDown: false, _ => true);
        var up1 = tracker.Process(LeftWindows, isKeyDown: false, HotkeyModifiers.Control | HotkeyModifiers.Windows, m3);
        var m4 = held.Observe(LeftControl, isKeyDown: false, vk => vk == LeftControl);
        var up2 = tracker.Process(LeftControl, isKeyDown: false, HotkeyModifiers.Control, m4);
        Assert.Null(up1.Signal);
        Assert.Null(up2.Signal);

        // Next Ctrl+Win hold+release stops Toggle recording cleanly.
        var m5 = held.Observe(LeftControl, isKeyDown: true, _ => false);
        tracker.Process(LeftControl, isKeyDown: true, HotkeyModifiers.None, m5);
        var m6 = held.Observe(LeftWindows, isKeyDown: true, vk => vk == LeftControl);
        tracker.Process(LeftWindows, isKeyDown: true, HotkeyModifiers.Control, m6);
        Thread.Sleep(HotkeyGesturePolicy.ModifierHoldThreshold + TimeSpan.FromMilliseconds(40));
        Assert.Null(tracker.TickDecision());

        var m7 = held.Observe(LeftWindows, isKeyDown: false, _ => true);
        var stopDecision = tracker.Process(LeftWindows, isKeyDown: false, HotkeyModifiers.Control | HotkeyModifiers.Windows, m7);
        var m8 = held.Observe(LeftControl, isKeyDown: false, vk => vk == LeftControl);
        tracker.Process(LeftControl, isKeyDown: false, HotkeyModifiers.Control, m8);
        Assert.Equal(PushToTalkSignal.Released, stopDecision.Signal);
    }

    // ---- F14: PasteLast fires on chord key-up, not key-down ----

    [Fact]
    public void PasteLastArmsOnKeyDownAndEmitsSignalOnChordKeyUp()
    {
        const HotkeyModifiers chordMods = HotkeyModifiers.Alt | HotkeyModifiers.Shift;
        var tracker = new HotkeyEdgeTracker(
            new HotkeyBinding(F8, HotkeyModifiers.None),
            new HotkeyBinding(HotkeyEdgeTracker.EscapeVirtualKey, HotkeyModifiers.None),
            new HotkeyBinding(LetterW, HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.PushToTalk,
            typesCharacter: (_, _) => false,
            pasteLast: new HotkeyBinding(LetterV, chordMods),
            copyLast: new HotkeyBinding(LetterC, HotkeyModifiers.Control | HotkeyModifiers.Shift));

        var down = tracker.Process(LetterV, isKeyDown: true, chordMods);
        Assert.True(down.Consume);
        Assert.Null(down.Signal);
        Assert.True(down.MaskMenuKey);

        // Key repeat must also be consumed silently without double-triggering.
        var repeat = tracker.Process(LetterV, isKeyDown: true, chordMods);
        Assert.True(repeat.Consume);
        Assert.Null(repeat.Signal);

        // Key-up emits PasteLast once and clears held state; releasing an unarmed key later does not re-fire.
        var up = tracker.Process(LetterV, isKeyDown: false, HotkeyModifiers.None);
        Assert.True(up.Consume);
        Assert.Equal(PushToTalkSignal.PasteLast, up.Signal);
        Assert.True(up.MaskMenuKey);
        Assert.False(tracker.HasHeldState);

        var spuriousUp = tracker.Process(LetterV, isKeyDown: false, HotkeyModifiers.None);
        Assert.False(spuriousUp.Consume);
        Assert.Null(spuriousUp.Signal);
    }

    // ---- F17: Windows reserved system shortcut conflicts & safe non-AltGr Alt modifier-sets ----

    [Fact]
    public void ReservedSystemShortcutDetectorClassifiesBlockedWarningAndSafeChords()
    {
        Assert.True(HotkeyGestureParser.IsSupportedModifierSet(HotkeyModifiers.Alt | HotkeyModifiers.Windows));
        Assert.True(HotkeyGestureParser.IsSupportedModifierSet(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows));
        Assert.False(HotkeyGestureParser.IsSupportedModifierSet(HotkeyModifiers.Alt));
        Assert.False(HotkeyGestureParser.IsSupportedModifierSet(HotkeyModifiers.Control | HotkeyModifiers.Alt));
        Assert.False(HotkeyGestureParser.IsSupportedModifierSet(HotkeyModifiers.Alt | HotkeyModifiers.Shift));

        var altWin = HotkeyGestureParser.Parse("Alt+Win");
        Assert.True(altWin.Succeeded);
        Assert.Equal("Alt+Win", altWin.Gesture!.Value.ToString());

        Assert.False(HotkeyGestureParser.Parse("Alt").Succeeded);
        Assert.False(HotkeyGestureParser.Parse("Ctrl+Alt").Succeeded);

        var blockedWinL = HotkeyConflictDetector.FindSystemConflict("Recording", "Win+L");
        Assert.NotNull(blockedWinL);
        Assert.Equal(SystemShortcutConflictSeverity.Blocked, blockedWinL.Value.Severity);

        var blockedAltTab = HotkeyConflictDetector.FindSystemConflict("Cancel", "Alt+Tab");
        Assert.NotNull(blockedAltTab);
        Assert.Equal(SystemShortcutConflictSeverity.Blocked, blockedAltTab.Value.Severity);

        var warningWinShiftS = HotkeyConflictDetector.FindSystemConflict("Add-a-word", "Win+Shift+S");
        Assert.NotNull(warningWinShiftS);
        Assert.Equal(SystemShortcutConflictSeverity.Warning, warningWinShiftS.Value.Severity);

        Assert.Null(HotkeyConflictDetector.FindSystemConflict("Recording", "Alt+Win"));
        Assert.Null(HotkeyConflictDetector.FindSystemConflict("Recording", "Ctrl+Win"));

        var description = HotkeyConflictDetector.DescribeSystemConflicts(
            HotkeyConflictDetector.FindSystemConflicts(
            [
                ("Recording", "Win+L"),
                ("Cancel", "Win+Space"),
            ]));
        Assert.Contains("Win+L", description, StringComparison.Ordinal);
        Assert.Contains("Win+Space", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsPresenterBlocksReservedSystemChordsAndAllowsAltWin()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonSettingsStore(path);
            using var presenter = new SettingsPresenter(store, AppSettings.Default);

            var blocked = await presenter.SaveGeneralAsync(CreateGeneralInput(recordingShortcut: "Alt+F4"));
            Assert.Equal(GeneralSaveStatus.OverlappingShortcuts, blocked.Status);
            Assert.Contains("Alt+F4", blocked.Overlap, StringComparison.Ordinal);
            Assert.False(File.Exists(path));

            var allowed = await presenter.SaveGeneralAsync(CreateGeneralInput(recordingShortcut: "Alt+Win"));
            Assert.Equal(GeneralSaveStatus.Saved, allowed.Status);
            Assert.Equal("Alt+Win", presenter.Current.Preferences.Dictation.PushToTalkGesture);
        });
    }

    // ---- F2 (Composed Session): UnexpectedStop exits Recording cleanly / finalizes speech ----

    [Fact]
    public async Task MidTakeUnexpectedStopTransitionsOutOfRecordingAndFinalizesNonEmptyOrFailsEmpty()
    {
        // Empty audio fault -> transitions out of Recording immediately to failure/idle without transcribing.
        var emptyWorld = ComposedSessionWorld.Create("hello");
        emptyWorld.Capture.StopResultFactory = id => new CapturedAudio(
            id,
            ReadOnlyMemory<float>.Empty,
            SampleRate: 16_000,
            Channels: 1,
            Error: new AppError(AppErrorCode.AudioDeviceLost, AppErrorStage.AudioCapture, CanRetry: true));

        var pressed = await emptyWorld.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        Assert.Equal(SessionCommandDisposition.Applied, pressed.Disposition);
        Assert.Equal(DictationSessionState.Recording, emptyWorld.Controller.CurrentSession?.State);

        var emptyTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        emptyWorld.Controller.SessionChanged += (_, snapshot) =>
        {
            if (snapshot is { State: DictationSessionState.Failed or DictationSessionState.Completed })
            {
                emptyTerminal.TrySetResult();
            }
        };

        emptyWorld.Capture.RaiseUnexpectedStop(new AppError(AppErrorCode.AudioDeviceLost, AppErrorStage.AudioCapture, CanRetry: true));
        await emptyWorld.Capture.Stopped.Task.WaitAsync(Patience);
        await emptyTerminal.Task.WaitAsync(Patience);
        await emptyWorld.Coordinator.ShutdownAsync(Patience);

        Assert.Null(emptyWorld.Controller.CurrentSession);
        Assert.Equal(0, emptyWorld.Engine.Calls);
        Assert.Empty(emptyWorld.Delivery.Requests);
        Assert.Equal([true, false], emptyWorld.RecordingActive);

        // Non-empty captured speech before disconnect -> still delivers captured words cleanly!
        var speechWorld = ComposedSessionWorld.Create("partial speech survived");
        speechWorld.Capture.ReturnTake = true;
        speechWorld.Capture.Take = FakeAudioCapture.Script((IsSpeech: true, Milliseconds: 400));

        await speechWorld.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        var speechTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        speechWorld.Controller.SessionChanged += (_, snapshot) =>
        {
            if (snapshot is { State: DictationSessionState.Completed or DictationSessionState.Failed })
            {
                speechTerminal.TrySetResult();
            }
        };

        speechWorld.Capture.RaiseUnexpectedStop(new AppError(AppErrorCode.AudioDeviceLost, AppErrorStage.AudioCapture, CanRetry: true));
        await speechWorld.Capture.Stopped.Task.WaitAsync(Patience);
        await speechTerminal.Task.WaitAsync(Patience);
        await speechWorld.Coordinator.ShutdownAsync(Patience);

        Assert.Null(speechWorld.Controller.CurrentSession);
        var delivered = Assert.Single(speechWorld.Delivery.Requests);
        Assert.Equal("partial speech survived", delivered.Text.Text);
        Assert.Equal([true, false], speechWorld.RecordingActive);
    }

    // ---- F9 (Notice & Monitor): Muted Windows endpoint notice vs generic unmuted no-signal ----

    [Fact]
    public async Task SilentMicNoticeDiscriminatesMutedEndpointAtOneSecondAndMonitorsTransitions()
    {
        // Muted endpoint surfaces Calm+IsEndpointMuted at 1s (16_000), while unmuted waits for 96_000 since-start.
        var mutedNotice = SilentMicNoticePolicy.EvaluateNotice(
            new ExactZeroRun(SilentMicNoticePolicy.MutedCalmSamples, SinceTakeStart: true, IsEndpointMuted: true),
            warningsEnabled: true);
        Assert.Equal(MicSilenceLevel.Calm, mutedNotice.Level);
        Assert.True(mutedNotice.IsEndpointMuted);

        var unmutedAtOneSecond = SilentMicNoticePolicy.EvaluateNotice(
            new ExactZeroRun(SilentMicNoticePolicy.MutedCalmSamples, SinceTakeStart: true, IsEndpointMuted: false),
            warningsEnabled: true);
        Assert.Equal(MicSilenceLevel.None, unmutedAtOneSecond.Level);
        Assert.False(unmutedAtOneSecond.IsEndpointMuted);

        var clock = new Deterministic.ManualClock();
        var effects = new FakeSilentMicEffects();
        var source = new FakeSilentSampleSource
        {
            CurrentExactZeroRun = new ExactZeroRun(SilentMicNoticePolicy.MutedCalmSamples, SinceTakeStart: true, IsEndpointMuted: true),
            IsEndpointMuted = true,
        };
        await using var monitor = new SilentMicMonitor(effects, () => source, clock);
        monitor.Start(DictationSessionId.Create(), warningsEnabled: true);

        await clock.WhenRegistered(1).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await effects.WhenShown(1).WaitAsync(Patience);
        Assert.Equal([(MicSilenceLevel.Calm, true)], effects.Shown);

        // Clearing exact-zero run (sound arrives while capture stays active) clears the notice.
        source.CurrentExactZeroRun = new ExactZeroRun(0, SinceTakeStart: false);
        source.IsEndpointMuted = false;
        await clock.WhenRegistered(2).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await effects.WhenCleared(1).WaitAsync(Patience);
        Assert.Equal(1, effects.Cleared);
        await monitor.StopAsync();
    }

    // ---- F10: Discard < 500ms non-toggle holds cleanly, keep Toggle & >= 500ms ----

    [Fact]
    public async Task ShortNonToggleHoldUnder500MsDiscardsWithoutDelayWhileLongHoldAndOverrideStop()
    {
        var clock = new Deterministic.ManualClock();
        var capture = new MinimalAudioCapture();
        await using var controller = new PushToTalkSessionController(
            capture,
            new FixedTargetProvider(),
            timeProvider: clock);

        await controller.PressAsync();
        clock.Advance(TimeSpan.FromMilliseconds(350));
        var discarded = await controller.ReleaseAsync();
        Assert.Equal(SessionTransitionKind.Discarded, discarded.Kind);
        Assert.Equal(DictationSessionState.Cancelled, controller.CurrentSession?.State);
        Assert.Equal(0, capture.StopCount);
        Assert.Equal(1, capture.CancelCount);

        await controller.ResetAsync();

        await controller.PressAsync();
        clock.Advance(TimeSpan.FromMilliseconds(200));
        var toggleOverride = await controller.ReleaseAsync(releaseContext: null, allowShortHold: true);
        Assert.Equal(SessionTransitionKind.FinalizeReady, toggleOverride.Kind);
        Assert.Equal(1, capture.StopCount);

        await controller.CompleteAsync(toggleOverride.Session!.Id);
        await controller.ResetAsync();

        await controller.PressAsync();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var normalHold = await controller.ReleaseAsync();
        Assert.Equal(SessionTransitionKind.FinalizeReady, normalHold.Kind);
        Assert.Equal(2, capture.StopCount);
    }

    [Fact]
    public async Task ComposedSessionDiscardsSub500MsHoldWithoutRunningAsrAndDeliversAfter500Ms()
    {
        var clock = new Deterministic.ManualClock();
        var world = ComposedSessionWorld.CreateWithHoldMinimum("spoken words", clock);

        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        var shortRelease = await world.Coordinator.SubmitAsync(PushToTalkSignal.Released);

        Assert.Equal(SessionCommandDisposition.Applied, shortRelease.Disposition);
        Assert.Null(world.Controller.CurrentSession);
        Assert.Equal(0, world.Engine.Calls);
        Assert.Empty(world.Delivery.Requests);
        Assert.Equal([true, false], world.RecordingActive);

        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var validRelease = await world.Coordinator.SubmitAsync(PushToTalkSignal.Released);

        Assert.Equal(SessionCommandDisposition.Applied, validRelease.Disposition);
        var delivered = Assert.Single(world.Delivery.Requests);
        Assert.Equal("spoken words", delivered.Text.Text);
        Assert.Equal(1, world.Engine.Calls);
    }

    // ---- F15: Zero-conversion 16kHz mono IEEE Float fast path ----

    [Fact]
    public void ConverterPreservesClampedNative16KhzMonoFloatWithoutResampling()
    {
        float[] input = [0.25f, -0.5f, 1.5f, -1.5f];
        var bytes = new byte[input.Length * sizeof(float)];
        for (var i = 0; i < input.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(i * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(input[i]));
        }

        var result = AudioSampleConverter.ConvertToMono16Khz(
            bytes,
            new AudioBufferFormat(16_000, 1, AudioSampleEncoding.Ieee754Single));

        Assert.Equal([0.25f, -0.5f, 1f, -1f], result.Samples);
        Assert.Equal(1f, result.Peak, precision: 5);
        var expectedRms = MathF.Sqrt(((0.25f * 0.25f) + (0.5f * 0.5f) + 1f + 1f) / 4f);
        Assert.Equal(expectedRms, result.RootMeanSquare, precision: 5);
    }

    // ---- F5: 250 Hz cascaded HPF & AutoStopMonitor monotonic noise-floor tracking ----

    [Fact]
    public void VadHighPassFilterAttenuatesDcAnd50HzRumbleWhilePassing1KhzSpeech()
    {
        Assert.Equal(250.0, VadInputHighPassFilter.CutoffHz);
        Assert.Equal(2, VadInputHighPassFilter.SectionCount);

        var filter = new VadInputHighPassFilter(16_000);
        var dcSettled = 0f;
        for (var i = 0; i < 400; i++)
        {
            dcSettled = filter.ProcessSample(1.0f);
        }

        Assert.InRange(MathF.Abs(dcSettled), 0f, 0.01f);

        filter.Reset();
        var rumble = SineWave(16_000, frequencyHz: 50, samples: 4_000, amplitude: 0.5f);
        var rumbleOut = new float[rumble.Length];
        filter.Process(rumble, rumbleOut);
        var steadyRumbleRms = Rms(rumbleOut.AsSpan(2_000));
        Assert.True(steadyRumbleRms < 0.03f, $"Expected 50 Hz rumble RMS < 0.03, saw {steadyRumbleRms}");

        filter.Reset();
        var speech = SineWave(16_000, frequencyHz: 1_000, samples: 4_000, amplitude: 0.5f);
        var speechOut = new float[speech.Length];
        filter.Process(speech, speechOut);
        var steadySpeechRms = Rms(speechOut.AsSpan(2_000));
        var expectedToneRms = 0.5f / MathF.Sqrt(2f);
        Assert.True(
            steadySpeechRms > expectedToneRms * 0.84f,
            $"Expected 1 kHz speech RMS > {expectedToneRms * 0.84f}, saw {steadySpeechRms}");
    }

    [Fact]
    public void SpeechSegmenterHighPassRejectsLowFrequencyRumbleAheadOfSpeech()
    {
        var rumbleLead = SineWave(16_000, frequencyHz: 50, samples: 8_000, amplitude: 0.05f);
        var speechTone = SineWave(16_000, frequencyHz: 1_000, samples: 8_000, amplitude: 0.20f);
        var silenceTail = AlternatingTone((0.002f, Milliseconds: 500));
        var combined = rumbleLead.Concat(speechTone).Concat(silenceTail).ToArray();

        var unfiltered = new SpeechSegmenter(16_000, TimeSpan.FromMilliseconds(400), applyHighPassFilter: false);
        var unfilteredSegments = unfiltered.Segment(combined);
        Assert.Contains(unfilteredSegments, s => s.IsSpeech && s.StartSample == 0);

        var filtered = new SpeechSegmenter(16_000, TimeSpan.FromMilliseconds(400), applyHighPassFilter: true);
        var filteredSegments = filtered.Segment(combined);

        // 250 Hz HPF suppresses the 50 Hz rumble below room floor so speech detection starts at the 1 kHz tone.
        Assert.DoesNotContain(filteredSegments, s => s.IsSpeech && s.StartSample < 4_000);
        Assert.Contains(filteredSegments, s => s.IsSpeech && s.StartSample >= 7_000);
        Assert.True(filtered.TrailingSilence(combined) >= TimeSpan.FromMilliseconds(450));
    }

    [Fact]
    public async Task AutoStopMonitorTracksLowestObservedFloorSoContinuousSpeechCannotSaturateNoiseFloor()
    {
        var clock = new Deterministic.ManualClock();
        var snapshotSource = new ScriptedSnapshotSource();
        var effects = new FakeRecordingTimerEffects(snapshotSource);
        var logger = new RecordingAppLogger();
        var session = DictationSessionId.Create();
        var preferences = DictationPreferences.Default with
        {
            RecordingMode = DictationRecordingMode.Toggle,
            AutoStopEnabled = true,
            AutoStopSilenceSeconds = 2.0,
        };

        // Poll 1: quiet room baseline (0.001f at 8kHz).
        snapshotSource.Session = session;
        snapshotSource.Samples = FakeAudioCapture.Script((IsSpeech: false, Milliseconds: 500));

        await using var monitor = new AutoStopMonitor(effects, logger, clock);
        monitor.Start(session, preferences);

        await clock.WhenRegistered(1).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await snapshotSource.WhenSampled(1).WaitAsync(Patience);

        // Poll 2: continuous moderate speech (0.08f) filling the entire 4s window + quiet trailing phrase (0.01f for 2.2s).
        // Without observedNoiseFloor retention, window floor would saturate at 0.02 and 0.01f < 0.02*1.8 would falsely trigger AutoStop!
        snapshotSource.Samples = AlternatingTone(
            (0.08f, Milliseconds: 2_000),
            (0.01f, Milliseconds: 2_200));

        await clock.WhenRegistered(2).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await snapshotSource.WhenSampled(2).WaitAsync(Patience);
        Assert.Empty(effects.Posted);

        // Poll 3: actual room silence (0.001f) for 2.2s after speech -> now AutoStop cleanly triggers!
        snapshotSource.Samples = FakeAudioCapture.Script(
            (IsSpeech: true, Milliseconds: 500),
            (IsSpeech: false, Milliseconds: 2_200));

        await clock.WhenRegistered(3).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await effects.WhenPosted(1).WaitAsync(Patience);

        Assert.Equal([PushToTalkSignal.Released], effects.Posted);
        Assert.Contains(AppEventCode.AutoStopTriggered, logger.Codes);
    }

    private static float[] SineWave(int sampleRate, double frequencyHz, int samples, float amplitude)
    {
        var buffer = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            buffer[i] = amplitude * MathF.Sin((float)(2.0 * Math.PI * frequencyHz * i / sampleRate));
        }

        return buffer;
    }

    private static float[] AlternatingTone(params (float Amplitude, int Milliseconds)[] parts)
    {
        var result = new float[parts.Sum(p => 16_000 * p.Milliseconds / 1000)];
        var cursor = 0;
        foreach (var (amplitude, milliseconds) in parts)
        {
            var length = 16_000 * milliseconds / 1000;
            for (var i = 0; i < length; i++)
            {
                result[cursor + i] = (i % 2 == 0) ? amplitude : -amplitude;
            }

            cursor += length;
        }

        return result;
    }

    private static float Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    private static GeneralSettingsInput CreateGeneralInput(string recordingShortcut = "F8") =>
        new(
            RecordingShortcut: recordingShortcut,
            CancelShortcut: "Escape",
            QuickAddShortcut: "Ctrl+Alt+W",
            FinalEngineIndex: 0,
            WordCorrection: true,
            FillerRemoval: true,
            EmojiFormatter: true,
            SpokenPunctuation: true,
            WhisperLanguage: LanguageCatalog.AutomaticCode,
            EnglishSpellingIndex: 0,
            RecordingModeIndex: 0,
            EscapeRecovery: true,
            AutoStop: true,
            AutoStopSeconds: 2.0,
            PolishProviderIndex: 0,
            PolishModel: string.Empty,
            OllamaEndpoint: string.Empty,
            HistoryEnabled: true,
            HistoryRetentionDays: 30,
            ThemeIndex: 0,
            LivePreview: true,
            OverlayPositionIndex: 0,
            LevelRailPill: true,
            PlayRecordingSounds: true,
            RecordingSoundPairing: RecordingSoundPairing.WhisperTick,
            CopyInsteadOfPaste: false,
            LocalDiagnostics: false,
            DiagnosticRetentionDays: 7,
            ShareTelemetry: false,
            TelemetryAvailable: false,
            MicrophoneId: null,
            PasteLastShortcut: string.Empty,
            CopyLastShortcut: string.Empty);

    private sealed class FakeSilentMicEffects : ISilentMicNoticeEffects
    {
        private readonly Deterministic.Milestone _shownMilestone = new();
        private readonly Deterministic.Milestone _clearedMilestone = new();

        public List<(MicSilenceLevel Level, bool IsMuted)> Shown { get; } = [];
        public int Cleared { get; private set; }

        public Task WhenShown(int count) => _shownMilestone.WhenAtLeast(count);
        public Task WhenCleared(int count) => _clearedMilestone.WhenAtLeast(count);

        public void ShowSilentMicNotice(MicSilenceLevel level) =>
            ShowSilentMicNotice(level, isEndpointMuted: false);

        public void ShowSilentMicNotice(MicSilenceLevel level, bool isEndpointMuted)
        {
            Shown.Add((level, isEndpointMuted));
            _shownMilestone.Increment();
        }

        public void ClearSilentMicNotice()
        {
            Cleared++;
            _clearedMilestone.Increment();
        }
    }

    private sealed class FakeSilentSampleSource : ISilentMicSampleSource
    {
        public ExactZeroRun? CurrentExactZeroRun { get; set; }
        public bool LastCaptureWasWholeZero { get; set; }
        public bool IsEndpointMuted { get; set; }
        public bool LastCaptureWasEndpointMuted { get; set; }
    }

    private sealed class MinimalAudioCapture : IAudioCapture
    {
        private static readonly float[] OneSample = [0.2f];
        private DictationSessionId _sessionId;

        public event EventHandler<AudioLevel>? LevelChanged
        {
            add { }
            remove { }
        }

        public bool IsCapturing { get; private set; }
        public int StopCount { get; private set; }
        public int CancelCount { get; private set; }

        public Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            _sessionId = request.SessionId;
            IsCapturing = true;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
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

    private sealed class FixedTargetProvider : IForegroundTargetProvider
    {
        public TargetWindowId? CaptureForegroundTarget() => new TargetWindowId(42);
    }

    private sealed class ScriptedSnapshotSource : IAudioSnapshotSource
    {
        private readonly Deterministic.Milestone _sampled = new();

        public DictationSessionId Session { get; set; }
        public float[] Samples { get; set; } = [];

        public Task WhenSampled(int count) => _sampled.WhenAtLeast(count);

        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration)
        {
            var snapshot = new AudioSnapshot(Session, Samples, 16_000, 1);
            _sampled.Increment();
            return snapshot;
        }
    }

    private sealed class FakeRecordingTimerEffects(IAudioSnapshotSource? audio) : IRecordingTimerEffects
    {
        private readonly Deterministic.Milestone _postedMilestone = new();

        public IAudioSnapshotSource? Audio { get; } = audio;
        public List<PushToTalkSignal> Posted { get; } = [];

        public Task WhenPosted(int count) => _postedMilestone.WhenAtLeast(count);

        public void Post(PushToTalkSignal signal, DictationSessionId forSession)
        {
            Posted.Add(signal);
            _postedMilestone.Increment();
        }

        public void RecordingTimedOut(DictationSessionId sessionId)
        {
        }
    }

    private sealed class RecordingAppLogger : IAppLogger
    {
        public List<AppEventCode> Codes { get; } = [];

        public void Write(AppLogEntry entry) => Codes.Add(entry.Event);
    }
}
