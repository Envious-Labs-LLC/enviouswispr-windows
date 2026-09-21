# Dictation pipeline contract

## Final text order

The order is part of the product behavior and must not drift casually:

1. Capture audio and freeze the target app when recording begins.
2. Run the selected final ASR engine.
3. Apply custom-word correction.
4. Remove configured filler words and false starts.
5. Convert spoken punctuation and spoken emoji commands.
6. Apply deterministic inverse text normalization for numbers, dates, times, currency, email, and URLs.
7. Optionally polish through EG-1, Ollama, or the selected cloud provider.
8. Restore protected emoji and deterministic tokens that the model was not allowed to alter.
9. Apply cursor-aware insertion repair when safe context is available.
10. Deliver to the frozen target and record the local history result according to user settings.

Every stage has a typed input, typed output, timeout or cancellation policy, and content-free diagnostic.
On timeout the stage is asked to stop and the pipeline continues with the last valid text. If a stage's
previous invocation is still running, execution is bypassed: the receipt is Busy, the input is preserved as
the fallback, and the result is degraded. Skipped denotes a disabled or inapplicable stage.
If a stage fails, return the last valid text. Optional polish failure returns deterministic text.

## Live preview

Preview consumes audio snapshots through a separate small multilingual Whisper engine. It may revise its
own display, but its text never enters final processing, history, analytics, or delivery. Stop and release
preview resources before final ASR begins. Preview failure must not fail recording. When enabled, the
Reading Well pill displays the latest preview and grows from one to five lines; Capsule and Level Rail
remain wordless designs.

## Session behavior

- Press or hold starts one session. Release stops capture and begins final processing.
- A second activation cannot create overlapping capture or duplicate paste.
- Escape cancels safely and delivers nothing.
- No-speech and empty-output paths are normal outcomes with clear, quiet feedback.
- Device removal, model failure, or accelerator failure preserves captured audio long enough for a safe
  fallback when possible.
- Focus changes during recording do not silently redirect private text to an unintended window.
- **One owner of session transitions** (`DictationSessionCoordinator`, since #148 steps 1 and 11): a
  key press or release, the auto-stop's release, the recording watchdog's timeout, and Windows locking
  or suspending are all commands on one queue, run one at a time by `DictationSessionExecutor`. A press
  is refused (`Busy`) while anything holds the session; a release or cancel that arrives while one is
  running is kept and run after it, not dropped; a second terminal is ignored because the recording it
  would end is already ending. An interruption (lock or suspend) is queued whatever is ahead of it; if the
  command ahead has not finished in five seconds, "recovery is still pending" is reported at that
  moment, while that command carries on, and the interruption is skipped when its turn comes - the
  shell's old five-second wait for its session gate, kept as a deadline on the coordinator's clock.
  With nothing in flight, an interruption does nothing. The update check holds the session through
  the coordinator (`TryHold`), so a press during a download is `Busy`. **Shutdown is a quiescence
  protocol** (plan-2 step 8, `ShutdownAsync(budget)` → `ShutdownReport`): admission closes before the
  first await and delivery closes with it (a finalisation that has not yet issued its delivery keeps
  the words for recovery); the command running now, every expiry notification and every hold are
  given the one budget; only once nothing is using the session does the executor's teardown run under
  it, with what is left of the budget - **one deadline handed down as its remainder** to the watchdog,
  then the three background owners, then (only behind owners that all finished) the session's
  disposal in the executor's own order (`DisposeSessionAsync`: the shell's observers off the capture,
  the controller and the capture it owns disposed by the executor itself, the shell's references let
  go, the delivery route disposed - the shell's parts one operation each behind the port, a step that
  throws recorded and the next still run), each reported (`SessionTeardownReport`, with
  `DisposalFaulted` apart from completion; a stop given zero still cancels and observes, and the
  disposal is issued even with nothing left and waited for only as long as remains). What did not finish is named in the report
  (`CommandOutstanding`, `ExpiriesOutstanding`, `ExpiryFaulted`, `HoldsOutstanding`) and **nothing is
  torn down beside it**; the command ends on its own terms later, and the shell disposes the engines,
  the polish provider, the arbiter, the owners, the stores and the run-state store only when `SessionQuiescent` says nothing
  uses them (the lifetime below). Delivery's closure and its admission are one
  decision under one lock in the runner: a delivery admitted is issued at once and settles inside the
  command; one not yet admitted when the closure lands is never issued. A second call shares the
  first's completion. Cancelling the finalisation in flight is the shell's exit policy, made before it
  asks for the shutdown. **The exit itself is `ApplicationLifetime`** (plan-2 step 9,
  `App/Composition/ApplicationLifetime.cs`, proved in `ApplicationLifetimeTests` and by a child process,
  `EnviousWispr.ExitProbe`): one twenty-second budget from the first step - admission closed before the
  first await, then the presentation drain (the window's gate closed, its microphone test, model discovery and history I/O stopped and joined, the settings write in flight kept; step 12) - through the shell closing, the local polish runtime's abort
  (`AbortPolishRuntime`: a kill of its process issued and the handles closed before the step
  returns; the exit is not observed, and a polish in flight loses its connection - what the provider
  does next, a retry that may find the runtime gone and try to start it again, its fallback, or the
  exit policy's cancellation landing first, is the finaliser's, and the provider's disposal later
  stops whatever it then owns), the exit policy, the input sources, the session's
  shutdown under what is left - asked for only behind a finished drain, since a presentation
  operation still inside the gate (the Quick Add's read through the delivery adapter, the mishearing
  suggestion inside the polish provider) may hold what the session's teardown or the disposals
  would dispose, and cancellation does not interrupt accessibility work under way: a drain that
  outlives its wait leaves the session as it is, retains everything and ends the host - the Quiesce joins - the launch's tail (`_startup`: everything after the tray, cancelled through its
  own token by the exit policy, reading `Leaving` after every await so no engine or session is built
  once the exit has begun), a model delivery (`_modelDelivery`: its download cancelled by the exit
  policy, its engine replacement refused once leaving), the polish warm-up, the system-ending note
  (the run-state write a Windows session ending fires; joined so the store is never closed under
  it) and the heartbeat - the disposals, the run's
  completion and the log; every step joined under the remainder and named in the `ExitReport` if it
  did not finish or threw. The session's dependencies (engines, polish provider, arbiter, owners,
  stores) run only behind a quiescent session with nothing outstanding, in dependency order, and the
  first disposal that does not finish stops the rest. The run's completion is the last of the run's
  work: written only by an exit with nothing outstanding, nothing failed and a clean session, under the remainder, with a token cancelled when it runs out (a
  write not begun by the deadline never begins) and through a `PublicationFence` the store commits under
  and the exit abandons under (`Core/Reliability/PublicationFence.cs`): the record is never replaced
  after the exit stopped waiting, and if it was replaced first the exit reports the run completed. The
  store also refuses any write once the record on disk belongs to another launch, and the single-instance
  lock is held to the process's end so no other launch begins one before this completion lands. After
  the completion only the two handles that wrote it are closed - the store, only once nothing that
  writes to it is outstanding (a heartbeat, a completion, a session's last edge), and the log, always -
  both reported. `ApplicationCleanShutdown` mirrors the record - it says the completion reached disk,
  not that the exit concluded: a writer's tail or the log's closing that outlives the budget leaves
  `ApplicationExitEscalated` beside it (see `diagnostics.md`). Diagnostic closure is not the run's work:
  a log that will not close makes the report unclean and ends the host, but a completion already
  committed stands. The verdict is taken after the log closes. Anything retained or outstanding means the host is told to end (`IHostTerminator`, exit
  code 70, `ApplicationExitEscalated` logged first): arbitrary in-process work cannot be joined by force,
  so the process ends with it still owned and the next launch reads an interrupted run; a timeout is
  never called clean. A step that blocks its thread can never be joined, so a watchdog timer on the
  clock's own thread stands two seconds behind the budget (`WatchGrace`: twenty seconds to conclude,
  twenty-two before the host is ended regardless) and ends the host from there, naming the step; its log
  line is attempted, not waited for. Every path out (tray, window, update, the shell's disposal) shares the same
  two cached tasks, so no step runs twice; a Windows session ending is a notification only (the
  run-state note is written and the process may be killed after it), not a path out. The shell keeps only its UI-thread unsubscriptions, the
  window's destruction and the steps' bodies; a gate reads `LifetimeParts()` to hold the session's
  dependencies to the guarded lists. The
  executor owns the order of the background work around a recording (watchdog, preview, auto-stop,
  streaming), the three-minute processing deadline - armed before the background work is stopped so
  it covers the preview's worker being waited for, cancelled by a lock, a suspend or the exit
  through the coordinator, released after any recovery - and the finalisation call itself. Since plan-2
  step 3 the executor also admits a press (pending recovery first, then the machine's memory and
  disk through the injected resource probe and `SystemResourceAdmissionPolicy`: critical memory
  refuses the take, low disk only tells the persistence owner not to write the recovery copy),
  carries whether Escape recovers the words for the session it started, and recovers a failed
  command (stop the background work, abort and reset the controller once, record and show it). The
  finalisation runner decides the language the delivery route is told (`DeliveryLanguagePolicy`:
  nothing for the final Parakeet model, which reports a language it did not detect). The shell keeps
  the probe's construction, Windows' notifications and rendering. The joins themselves - runner,
  executor, coordinator and their two effects adapters - are built by `App/Composition/
  SessionComposition.cs` (plan-2 step 4), and the long-lived owners around them (persistence,
  finaliser, preview, streaming, watchdog, auto-stop, and the one queue a key, the auto-stop and the
  watchdog all submit through) by `RuntimeComposition.cs` (step 5); the architecture tests compile
  and drive both. Since step 7 each background owner's stop can be given a deadline
  (`StopAsync(TimeSpan)` → `StopOutcome`): the loop is cancelled and joined for that long, and a loop
  still running past it is reported `StillRunning` and **stays owned** - its token source undisposed,
  the engine under it not stopped, the fields kept - so the next stop joins the same work; a timeout
  is never treated as a termination. The deadline covers the whole stop - the gate, the loop and
  the engine's own stop, which the preview owns as a task and joins again rather than issuing twice;
  an engine that refuses its stop (its worker still there) keeps the preview owned too. The preview
  closes its screen before the join and hands the window frames that carry their own validity
  (`LivePreviewFrame.IsCurrent`), asked at the draw, so a frame answered late or already queued for
  the window draws nothing after the closure. A start while the last loop is still owned is refused
  (no preview, no head start, no auto-stop for that recording) rather than run beside it; the
  watchdog retires a watch it replaces and joins it with the next stop. The stop is published before
  the gate is waited for, so a stop that runs out of budget waiting still closed the screen and
  cancelled the loop; a stop's outcome is carried by the unbounded overload too, and a disposal
  whose stop the engine refused leaves the owner in place for the next attempt. The timers' stops
  join the loop that posted, never the command it queued; the auto-stop's release carries the
  recording it was for (`SessionCommand.ForSession`, `SubmitAsync(signal, forSession)`), the
  executor ignores it when it runs if that recording has ended, and the coordinator's terminal
  coalescing knows the recording in flight (from the commands' own results), so a stale named
  release waiting in the queue never swallows the key that ends the recording after it.
  `SessionBackgroundWork.StopAsync(deadline)` gives each owner the deadline and returns a
  `BackgroundStopReport`; the unbounded `StopAsync()` the executor uses today is unchanged, and
  step 8's shutdown is what supplies the deadline.
- **How the macOS app owns the same workflow** (read from its source by the Mac session on 2026-09-20;
  its owners are `.claude/knowledge/session-lifecycle.md`, `pipeline-mechanics.md` and `live-preview.md`
  in the macOS repository). One recording-session kernel is the single state machine every dictation
  runs through: idle, arming, live, stopping, delivering (transcribing, then finalizing); the ending is
  a separate declared outcome, and a transition is a method that refuses an illegal move rather than
  asserting. The kernel prepares, records, stops, transcribes and finalizes; ASR and everything after
  it (text chain, storage, paste cascade) are injected seams filled by a wiring file. The shell only
  presses start, stop and cancel and observes state. Live preview is outside the kernel. Deadlines are
  owned where the work is - each post-ASR step computes its own budget from its input, ITN runs before
  polish so a polish timeout delivers post-ITN text - and there is no single global processing
  deadline; the kernel bounds only the retry it owns. Cancellation is a declared outcome legal only from
  certain phases; finalizing is the safe point no cancel or fresh interruption reaches, so delivery
  always completes once it starts. Quit does not wait for an in-flight transcription: the safety net is
  persisted-audio recovery on the next launch, and teardown is bounded by being small and synchronous.
  Its own advice for a platform wanting more: make quit-mid-take a declared outcome the kernel
  concludes, and bound the drain by the same per-limb budgets. Views receive typed models built by
  coordinators and call back; decisions live in coordinator and setup types, tested with fakes.

  What Windows takes from that, for the work after the regrade of #148: the session executor's
  effects port should carry effects, with the start/stop order of the preview, streaming and timers
  and the processing deadline owned in Pipeline; finalizing should be a safe point; and a quit during
  a take should be a declared outcome rather than a teardown beside unfinished work. What Windows keeps
  deliberately: the speech runtime out of process (the Mac's is in-process), and the three delivery
  routes.

- **Production-path proofs** (plan-2 step 10): every proof of what the session does under a press, a
  release, a lock, an exit or a shutdown builds `ComposedSessionWorld` (tests) - `RuntimeComposition` and
  `SessionComposition` over the real coordinator, executor, controller, runner, finalizer, persistence
  and background owners, fakes only at the leaves - rather than a hand-built shell adapter or
  finalisation; `SessionShutdownTests`, `PreviewStartupDecouplingTests` and `ProductionPathTests` are
  all on it, the last with the production recovery store on a file where the store matters. What
  portable tests cannot establish is recorded natively: `scripts/native-journeys.ps1` runs
  NativeExitClosesMicrophoneAndWorkers and NativeDeliveryExercisesThreeRoutes (the journey harness's
  `--target-mode edit|caret-start|password`, the route read from where the words landed relative to the
  target's seed text or from the log's refusal), and `docs/reliability/native-journey-evidence.md`
  records what a run observed on which build. A lock arriving after admission has closed no longer
  cancels the finalisation the shutdown is waiting for: the interruption's cancel is made only for an
  interruption that will be admitted.

## Session ownership inventory

Every effect and callback the shell supplies to the session, and every entry point by which the
shell reaches it, traced from the production construction site to the body that runs. Each body is
classified as one of: **composition** (choosing and joining concrete parts, or handing a reference),
**translation** (one native event turned into one command on the coordinator's queue), **rendering**
(one status, notice or frame dispatched to the window), **observation** (one content-free log line
or one value read), or **one external operation** (one store write, one flag on the hook, one
disposal). The Pipeline owner of each session decision is named in the last table. Line numbers are
as of the commit that wrote this section (`git log -1 -- .claude/knowledge/pipeline.md`); the
anchors are the member names, which is what to search for once they drift.

### Entry points: what the shell submits and where the decision is made

| Native source | Shell body | What the body does | Where the decision is |
| --- | --- | --- | --- |
| Hotkey press/release/cancel (`WindowsPushToTalkHook.Signalled`, subscribed at `App/App.xaml.cs:1378`) | `OnPushToTalkSignalled` `App/App.xaml.cs:1927` | QuickAdd: one log line and `HandleQuickAddAsync` (not a session; see below). Every other signal: `_runtime.SubmitAsync(args.Signal)` `App/App.xaml.cs:1936`. **Translation.** | `SessionRuntime.SubmitAsync` `App/Composition/RuntimeComposition.cs:160` → `SessionQueue.HandAsync` `App/Composition/RuntimeComposition.cs:84` (refuses while the shell is leaving or before a coordinator exists; otherwise one submit and one log line) → `DictationSessionCoordinator.SubmitAsync` `Pipeline/DictationSessionCoordinator.cs:454` (admission: Busy, queued terminal, coalesced terminal, refused after closure; the press captures its target through `CaptureStartContext` handed in at `App/Composition/SessionComposition.cs:89`) → `DictationSessionExecutor.ExecuteAsync` `Pipeline/DictationSessionExecutor.cs:303` → `ExecutePushToTalkAsync` `Pipeline/DictationSessionExecutor.cs:453` (recovered text waiting, resource admission, watchdog stop, press/release/cancel/Escape-as-release, background start, finalisation, cancel/failure reset). |
| Auto-stop's release, watchdog's timeout (loops in Pipeline, `RecordingTimers.cs`) | `RecordingTimerEffects.Post` `App/Composition/RuntimeEffects.cs:18`, `RecordingTimerEffects.RecordingTimedOut` `App/Composition/RuntimeEffects.cs:20` | One signal each onto the shared queue, the auto-stop's carrying the recording it was for. **Translation.** | `SessionQueue.HandAsync` / `SessionQueue.TimeOut` `App/Composition/RuntimeComposition.cs:109` → `DictationSessionCoordinator.TimeOutAsync` `Pipeline/DictationSessionCoordinator.cs:311` → `DictationSessionExecutor.TimeOutAsync` `Pipeline/DictationSessionExecutor.cs:424`. When the loops start and stop, and their durations' use, is `SessionBackgroundWork` (below). |
| Windows suspend, lock, resume, unlock, session ending (`SystemLifecycleMonitor.Transitioned`, subscribed at `App/App.xaml.cs:732`) | `OnSystemLifecycleTransitioned` `App/App.xaml.cs:743` | One log line for the transition (under the dictation's scope when one is in flight); for a session ending, one fire-and-forget run-state write `App/App.xaml.cs:766`; for suspend or lock, `coordinator.InterruptAsync(transition)` `App/App.xaml.cs:789` unless the shell is leaving; for resume or unlock, one status. **Translation + observation + rendering.** Which transitions count as an interruption is the one decision here, and it chooses a command, not what the command does. | `DictationSessionCoordinator.InterruptAsync` `Pipeline/DictationSessionCoordinator.cs:277` (admit whatever is queued, cancel only the finalisation that was in flight before admission, five-second patience: `ExpireIfLateAsync` `Pipeline/DictationSessionCoordinator.cs:555` → `DictationSessionExecutor.ExpireAsync` `Pipeline/DictationSessionExecutor.cs:209`, one status through `ShowInterruptionPending`) → `DictationSessionExecutor.InterruptAsync` `Pipeline/DictationSessionExecutor.cs:320` (release and finalise a recording as a key would, reset anything else, nothing when idle). |
| UAT journey fixture (environment switches only) | `RunPublicFixtureJourneyUatAsync` `App/App.xaml.cs:2101` | Waits for the harness's start event, then `_runtime.SubmitAsync(Pressed)` `App/App.xaml.cs:2140`, the fixture's hold, and `SubmitAsync(Released` or `Cancelled)` `App/App.xaml.cs:2155`: a scripted key. **Translation.** | The same queue and executor as a key. |
| Update check / update apply (window buttons) | `OnUpdateCheckRequested` `App/App.xaml.cs:845`, `OnUpdateApplyRequested` `App/App.xaml.cs:873` | `coordinator.TryHold()` (`App/App.xaml.cs:804` and `:833`) around the download or the restart; a refused hold renders `BusyDictating`. The apply path sets the leaving flag inside the hold and closes admission `App/App.xaml.cs:911` before the exit runs. **One external operation (the hold) + rendering; the close is the shell's exit policy, made through the coordinator's own gate.** | `DictationSessionCoordinator.TryHold` `Pipeline/DictationSessionCoordinator.cs:239` (refused when closed, pending, running or held; a press under a hold is Busy; a terminal waits) and `Close` `Pipeline/DictationSessionCoordinator.cs:319`. |
| Exit. Four callers, all through `PrepareForExitAsync` `App/App.xaml.cs:1052` → `Lifetime().ExitAsync()`: the window closing (`OnWindowClosed` `App/App.xaml.cs:507`), the tray's Exit (`ExitFromTrayAsync` `App/App.xaml.cs:1030`), the update restart (`OnUpdateApplyRequested`) and the shell's own `DisposeAsync` `App/App.xaml.cs:1066`. A Windows session ending is not one of them: it is a notification only (the lifecycle row above), and the process may be killed after it. | `LifetimeParts()` `App/App.xaml.cs:1088` handed to `ApplicationLifetime.PrepareCoreAsync` `App/Composition/ApplicationLifetime.cs:248` and `ExitCoreAsync` `App/Composition/ApplicationLifetime.cs:266` | Each part is one step body. Preparation: `CloseAdmission: () => _sessionCoordinator?.Close()` `App/App.xaml.cs:1089`; `DrainPresentation: () => _presentation?.DrainAsync()` `App/App.xaml.cs:1090` → `WindowPresentationSession.DrainAsync` `Presentation/WindowPresentationSession.cs:155` (the window's gate closed, its work joined, the settings write finished: the presentation's own quiescence, owned in Presentation); `ShellClosing` `App/App.xaml.cs:1091` (`ShutdownProductWindows` - the window's timers and players - and one log line); `AbortPolishRuntime: () => (_polishProvider as EgOnePolishProvider)?.TerminateRuntimeImmediately()` `App/App.xaml.cs:1096` → `EgOnePolishProvider.TerminateRuntimeImmediately` `LLM/EgOnePolishProvider.cs:68` → `EgOneServerManager.TerminateImmediately` `LLM/EgOneServerManager.cs:303`: the endpoint forgotten, the owned process taken from its owner, a kill of it and its tree issued if it has not exited, its handle and job disposed, all before the step returns - one external operation. Not done here: observing the process's exit, or deciding what the finalisation gets - a polish in flight loses its connection, and what the provider does next (its one retry `LLM/EgOnePolishProvider.cs:149`, which goes through its readiness check and may try to start the runtime again, its fallback, or the exit policy's cancellation landing first) is the finaliser's; the provider's disposal later stops whatever it then owns. Its place in the order - after the shell closes, before the exit policy and the session's shutdown - is the lifetime's (`App/Composition/ApplicationLifetime.cs:258`). Exit: `CancelProcessing` `App/App.xaml.cs:1102` (the exit policy: cut a transcription in flight, and the polish warm-up); `ReleaseInputs` (unsubscribe and dispose the lifecycle monitor `App/App.xaml.cs:1113` and the hook `App/App.xaml.cs:1122`); `ShutDownSession: budget => coordinator.ShutdownAsync(budget)` `App/App.xaml.cs:1128`; `Quiesce` (join the polish warm-up, cancel and join the heartbeat); `DisposeSessionDependencies` (the engines, the polish provider, the arbiter, the owners, the stores, one disposal each; the session itself is not here - see the teardown trace); `DisposeShell` (presentation, activation channel, tray, then the coordinator's own disposal `App/App.xaml.cs:1259`); `CompleteRun` (one run-state write under the fence), `CloseRunState`, `DisposeLogger`. **Composition of steps; each body is one disposal, one join or one external operation.** The order, the budget, the remainder handed to each step, the quiescence gate before the dependencies (`App/Composition/ApplicationLifetime.cs:314`), retention and escalation are `ApplicationLifetime`'s. The lifetime's own two callbacks: the watchdog timer `App/Composition/ApplicationLifetime.cs:238` → `OnDeadline` `App/Composition/ApplicationLifetime.cs:498` (a report naming the step it is inside, one attempted log line, the terminator) and `TerminateOnce` `App/Composition/ApplicationLifetime.cs:536` → `IHostTerminator.Terminate` → `App.HostTerminator` `App/App.xaml.cs:1306`: `Environment.Exit(70)`, one external operation. | `DictationSessionCoordinator.ShutdownAsync` `Pipeline/DictationSessionCoordinator.cs:344` → `ShutdownCoreAsync` `Pipeline/DictationSessionCoordinator.cs:353`: close, wait for the command and the expiries, wait for the holds, take the gate, then `DictationSessionExecutor.TearDownAsync` `Pipeline/DictationSessionExecutor.cs:250` under the remainder. |

Not session entry points, listed because they read session state or borrow what the session's
teardown disposes: `HandleQuickAddAsync` `App/App.xaml.cs:1939` refuses
while a recording is in flight, then takes a presentation lease (`WindowPresentationSession.TryEnter`
`Presentation/WindowPresentationSession.cs:145`,
refused once the drain has begun) and only under it borrows the delivery adapter for the foreground
selection (`App/App.xaml.cs:1975`, under the lease's `Closing`
token) and asks `SelectionAcquisitionPolicy.Decide` (Core) with `IsProcessing`
`App/App.xaml.cs:1997`; a drain that begins while
the read is out ends it before the clipboard borrow or the window `App/App.xaml.cs:1979`.
The exit's first step joins that lease (`DrainAsync`) before the session is asked to shut down, and
a drain that outlives its wait means the session is not asked at all `App/Composition/ApplicationLifetime.cs:292`
- nothing of it disposed, everything retained, the host ended - so the adapter is never disposed
under the read even when cancellation could not interrupt it (proved by
`TheSessionIsNotAskedToShutDownBehindADrainThatDidNotFinish` with the production coordinator,
`AShellOperationsLeaseIsJoinedByTheDrain` for the join, and the source gate
`TheQuickAddBorrowsTheAdapterOnlyUnderAPresentationLease`). The dialog it ends on is queued through
`OpenQuickAddUnlessLeaving` `App/App.xaml.cs:2050`,
whose callback reads `Leaving` `App/App.xaml.cs:2063`
- the flags or the presentation's durable closure - when it runs, as does `ShowMainWindow`'s
`App/App.xaml.cs:1001`; the lease's end and the queued callback's run are two
moments, and the check is at the second. It never submits to the session; `OnSpeedCheckRequested` `App/App.xaml.cs:621`
declines to measure under a recording; `OnAudioDevicesChanged` `App/App.xaml.cs:799`
and `PrepareForExitAsync` open the dictation's log scope from `_sessionController?.CurrentSession`
(observation); `OnRecoveryCleared` `App/App.xaml.cs:816`
forwards the window's clear to the persistence owner; startup hands the loaded recovery record to the
persistence owner once, `_sessionPersistence.AdoptStartupRecovery(recovery)` `App/App.xaml.cs:382`
→ `SessionPersistence.AdoptStartupRecovery` `Pipeline/SessionPersistence.cs:117`
(the owner sets its own pending state from it); `OnAudioLevelChanged`
`App/App.xaml.cs:1439` is the level meter (rendering).
`OnMishearingSuggestionsRequested` `App/App.xaml.cs:581`
is the other borrower of something the exit disposes - the polish provider, asked for alias
suggestions - and is admitted the same way: a presentation lease, the request under `lease.Closing`
`App/App.xaml.cs:604`,
no rendering once the drain has begun; the provider is disposed under `DisposeSessionDependencies`,
which the lifetime runs only behind a finished drain. The Windows session-ending note
`App/App.xaml.cs:766`
is kept as a task and joined by the lifetime's "system ending note" step under `Quiesce`
`App/App.xaml.cs:1156`, ahead of the run-state store's closing,
which the lifetime runs only when nothing is outstanding.

Two more borrowers of what the exit disposes, both tracked tasks the exit policy cancels and
`Quiesce` joins (`App/App.xaml.cs:1136`,
`App/App.xaml.cs:1137`):
the launch's tail, `CompleteStartupAsync` `App/App.xaml.cs:368`
- everything after the tray is shown, from which point an exit can begin: the recovery read
(`_recoveryTextStore.LoadAsync` under `_startupCancellation`), the engines, the model delivery
notice, the session's construction - which reads `Leaving` after every await so nothing is built
once the exit has begun; and a model delivery, `DeliverModelsAsync` `App/App.ModelDelivery.cs:165`
→ `DeliverModelsCoreAsync` `App/App.ModelDelivery.cs:177` - the download under
`_modelDownload`, which the exit policy cancels, then the engines torn down and rebuilt only while
not leaving, checked before the teardown and after each build. Both are **composition** (the shell
choosing and building its parts) whose place in the exit is the lifetime's; the source gate
`TheLaunchAndAModelDeliveryAreTrackedCancelledAndJoinedByTheLifetime` holds them to it.

The controller is built at `App/App.xaml.cs:1338` with two
closures: `deliveryOptions: () => TextDeliveryOptions.Default with { CopyInsteadOfPaste = … }`
`App/App.xaml.cs:1341` - a settings read, evaluated by
`PushToTalkSessionController.CaptureStartContext` `Pipeline/PushToTalkSessionController.cs:80`
at the instant of the key - and `preferredAudioDevice`, a settings value. The window's own bodies on
the status path: `App.OnSessionStatusChanged` `App/App.xaml.cs:951`
mirrors a status to the tray (rendering); `MainWindow.SetSessionStatus` `App/MainWindow.xaml.cs:585`
renders the pill and the overlay and, for a Recording status, cancels a microphone test in progress
(`CancelMicrophoneTest`: the window's own test, not the session's capture - a dictation outranks a
test); `MainWindow.ReportDeliveryAndMaybeOfferLanguage` `App/MainWindow.xaml.cs:727`
asks `LanguageLockSuggester.Observe` (Core; its counts live in the settings' offer history) whether
to put a language offer on the pill instead of the delivery sentence - a presentation decision about
the pill, made in Core - and, when it shows one, fires `RememberLanguageOffersAsync`
`App/MainWindow.xaml.cs:748` → `UpdateSettingsAsync`
`App/MainWindow.xaml.cs:3436` →
`SettingsPresenter.SaveAsync` (one settings write through the presentation's writer, under its
lease; silent on refusal). Nothing of it reaches the session.

### `SessionShell` (construction site `App/App.xaml.cs:1363`; record `App/Composition/SessionComposition.cs:26`)

| Member | Supplied body | Classification |
| --- | --- | --- |
| `View` | `new WindowSessionView(this)` `App/App.xaml.cs:2319`: `ShowStatus`, `ShowNotice`, `ReportDelivery` each dispatch one window call; `ShowMainWindow` shows the window. | Rendering. |
| `AttachedSession` | `() => _sessionController?.CurrentSession` `App/App.xaml.cs:1365` - the shell's reference to its controller, null once its teardown has let go of it. | Observation. |
| `Dictation` | `() => _settings.Preferences.Dictation`. | Observation. |
| `Engine`, `Delivery` | `() => _transcriptionEngine`, `() => _textDelivery` - the parts the shell composed (`App/App.xaml.cs:1337`). | Composition. |
| `Options` | `() => new FinalizationOptions(_customWords, _deterministicTextOptions, CurrentPolishSetup())` - three settings reads packaged. | Composition. |
| `CloudPolishProviderName`, `RunId` | `() => _cloudPolishConsent?.ProviderName`, `() => _runId`. | Observation. |
| `RecordingActive` | `active => _pushToTalkHook?.SetRecordingActive(active)`. | One external operation (a flag on the hook). |
| `ArchiveAudio` | `audio => ArchiveDictationAudio(audio)` `App/App.xaml.cs:2384` - `[Conditional("DEBUG")]`, a file write with its own retention, failures swallowed; compiled out of release. | One external operation. |
| `DetachCaptureObservers` | `DetachCaptureObservers` `App/App.xaml.cs:2340` - `_audioCapture.LevelChanged -= OnAudioLevelChanged`. | One unsubscription. |
| `ReleaseSession` | `ReleaseSession` `App/App.xaml.cs:2349` - the controller and capture references set to null, after the executor has disposed both. | Reference bookkeeping; one operation. |
| `DisposeDeliveryRoute` | `DisposeDeliveryRoute` `App/App.xaml.cs:2356` - the adapter and route references let go, `WindowsTextTargetAdapter.Dispose()`. | One disposal. |

### `IDictationSessionEffects` → `SessionEffects` (`App/Composition/SessionEffects.cs:17`, constructed at `App/Composition/SessionComposition.cs:85`)

| Member | Body | Classification | The decision it serves, in Pipeline |
| --- | --- | --- | --- |
| `EscapeRecoveryEnabled` | `parts.Shell.Dictation().EscapeRecoveryEnabled`. | Observation. | Read once as a recording starts, kept as `_escapeRecoveryForSession` `Pipeline/DictationSessionExecutor.cs:538`; whether Escape releases or cancels is the executor's `Pipeline/DictationSessionExecutor.cs:511`. |
| `RecordResourcePressure` | One `AppLogEntry`. | Observation. | Admission: `SystemResourceAdmissionPolicy.Evaluate(_resources.Probe())` `Pipeline/DictationSessionExecutor.cs:487`. |
| `ShowRecoveredTextWaiting`, `ShowMemoryCritical`, `ShowDiskLow` | Notices and statuses on the view, then the window. | Rendering. | The same admission. |
| `RecordTransition` `App/Composition/SessionEffects.cs:58` | Sets the hook's recording flag through `RecordingActive` for a start and for each ending kind; one log line for the transition, plus the capture's open and stream timings on a start. | One external operation + observation. The `switch` maps a transition kind to a log code and a flag value; it chooses nothing about the session. | The transition was made by the controller under the executor's `switch` on the signal `Pipeline/DictationSessionExecutor.cs:512`. |
| `RecordingSettings` `App/Composition/SessionEffects.cs:106` | The watchdog duration (`RecordingLimits.WatchdogDuration()`, production limit or a bounded UAT override) and the dictation preferences, as values. | Observation. | `SessionBackgroundWork.StartAsync` `Pipeline/SessionBackgroundWork.cs:85` decides the order (watchdog, preview, auto-stop, streaming) and the watchdog arms the deadline. |
| `ShowInterruptionPreserving`, `ShowTransitionStatus`, `ShowSessionRecovered`, `ShowInterruptionPending`, `ShowRecordingTimedOut` | One status each; `SessionStatus` maps a transition kind to a sentence. | Rendering. | Interruption, recovery and time-out are the executor's (`InterruptAsync`, `RecoverFailedSessionAsync` `Pipeline/DictationSessionExecutor.cs:404`, `TimeOutAsync`); the five-second patience is the coordinator's (`InterruptionPatience` `Pipeline/DictationSessionCoordinator.cs:178`, `ExpireIfLateAsync` → the executor's `ExpireAsync`). |
| `RecordSessionFailure`, `RecordSessionRecovered`, `RecordInterruptionFailure`, `RecordRecordingTimedOut` | One log line each. | Observation. | As above. |
| `RecordDictationEdgeAsync` `App/Composition/SessionEffects.cs:164` | One run-state store write of whether a dictation is in flight (`AttachedSession() is not null`), for the run in progress; a false return or an exception is one log line and swallowed. | One external operation. | When the edge is written - the `finally` of every command - is the executor's (`Pipeline/DictationSessionExecutor.cs:373`, `Pipeline/DictationSessionExecutor.cs:449`, `Pipeline/DictationSessionExecutor.cs:588` - the interruption's, the time-out's and the key's `finally`). |
| `DetachCaptureObservers`, `ReleaseSession`, `DisposeDeliveryRoute` `App/Composition/SessionEffects.cs:204` | Forward to the three shell delegates above. | Forwarding; the bodies are the `SessionShell` rows. | Called only from `DictationSessionExecutor.DisposeSessionAsync` `Pipeline/DictationSessionExecutor.cs:270`, in its order, around the executor's own `_controller.DisposeAsync()`. |
| `RecordTeardownFailure` `App/Composition/SessionEffects.cs:210` | One log line. | Observation. | Which step threw, and that the next still runs, is the executor's (`TryStep` `Pipeline/DictationSessionExecutor.cs:289`); the fault is reported as `SessionTeardownReport.DisposalFaulted`. |

### `ISessionFinalizationEffects` → `SessionFinalizationEffects` (`App/Composition/SessionFinalizationEffects.cs:16`, constructed at `App/Composition/SessionComposition.cs:77`)

| Member | Body | Classification |
| --- | --- | --- |
| `Engine`, `Delivery`, `CurrentOptions`, `ArchiveAudio` | The `SessionShell` delegates above. | Composition / observation / one external operation. |
| `RecordTranscriptionUnavailable`, `RecordTranscriptionStarted`, `RecordTranscriptionFinished`, `RecordTranscriptionFailed`, `RecordDeliveryStarted`, `RecordDictationCompleted` | One log line each. | Observation. |
| `RecordDelivery` `App/Composition/SessionFinalizationEffects.cs:78` | One log line; the `switch` maps the delivery's refusal reason to an event code and an error code, and copies the fault's stage and family. | Observation. The delivery's outcome was decided by the route (`ContextAwareTextDelivery`). |
| `ShowTranscriptionUnavailable`, `ShowTranscribing`, `ShowTranscriptionFailed`, `ShowDelivering`, `ShowEscapeRecoveryFinished`, `ReportDelivery` | One status each on the view. | Rendering. |
| `ShowHeldStatus` `App/Composition/SessionFinalizationEffects.cs:131` | One status; chooses the sentence and the pill action from the `FinalizationReport` it is handed (`PolishFallbackStatus` words a polish fallback). | Rendering. The report's outcome was decided by the runner. |

The finalisation itself - engine present, transcribe (with any streaming head start), record, process,
polish, deliver or hold, complete and reset - is `SessionFinalizationRunner.RunAsync`
`Pipeline/SessionFinalizationRunner.cs:205` in the order it writes, under the deadline the
executor armed `Pipeline/DictationSessionExecutor.cs:609`
(`MaximumFinalProcessingDuration` `Pipeline/DictationSessionExecutor.cs:125`),
and delivery's closure is the runner's own lock `Pipeline/SessionFinalizationRunner.cs:146`. 

`ITranscriptFinalizationEffects` → `TranscriptFinalizationEffects` `App/Composition/RuntimeEffects.cs:68`
(constructed in `RuntimeComposition.Compose` `App/Composition/RuntimeComposition.cs:185`, handed to `TranscriptFinalizer` and `PolishExecutor`), member by member:

| Member | Body | Classification |
| --- | --- | --- |
| `RecordDeterministicProcessingStarted` `App/Composition/RuntimeEffects.cs:70` | One log line. | Observation. |
| `EmitStageReceipts` `App/Composition/RuntimeEffects.cs:81` | One log line per stage receipt it is handed, filtered to the half (emoji restoration or the rest) the caller names; the category is read off the receipt's status. | Observation. Which stages ran, and their outcomes, were decided by `DeterministicTextPipeline` (PostProcessing). |
| `SaveRecoveryTextAsync` `App/Composition/RuntimeEffects.cs:105` | Forwards to `SessionPersistence.SaveRecoveryTextAsync` `Pipeline/SessionPersistence.cs:131`. | Forwarding; the owner decides whether the copy may be written (`CanPersistRecovery`). |
| `RecordPolishStarted` `App/Composition/RuntimeEffects.cs:108`, `RecordPolishFinished` `App/Composition/RuntimeEffects.cs:114`, `RecordPolishRefused` `App/Composition/RuntimeEffects.cs:127`, `RecordDeterministicProcessingFinished` `App/Composition/RuntimeEffects.cs:136` | One log line each; the event and category are read off the result handed in. | Observation. Admission, the resource wait and the fallback are `PolishExecutor` `Pipeline/PolishExecutor.cs:35`; the order of the text pass is `TranscriptFinalizer` `Pipeline/TranscriptFinalizer.cs:60`. |

### Runtime effects and `RuntimeShell` (construction site `App/App.xaml.cs:170`; record `App/Composition/RuntimeComposition.cs:42`)

| Adapter / member | Body | Classification | Owner of the decision |
| --- | --- | --- | --- |
| `RecordingTimerEffects` `App/Composition/RuntimeEffects.cs:14` | `Audio` reads the capture; `Post` and `RecordingTimedOut` hand one signal each to the queue. | Observation + translation. | `RecordingWatchdog` and `AutoStopMonitor` (`Pipeline/RecordingTimers.cs`) own the watch, the silence rule and the retire-and-join; the executor owns what the command does. |
| `StreamingTranscriptionEffects` `App/Composition/RuntimeEffects.cs:24` | Reads `LivePreviewEnabled`, `Engine`, `Audio`. | Observation. | `StreamingTranscriptionController` decides whether to yield to preview and how the head start is used. |
| `LivePreviewEffects` `App/Composition/RuntimeEffects.cs:34` | Reads `Enabled`, `Engine`, `EngineUnavailableReason`, `Audio`, `RecordingSessionId`; `ShowPreview`/`ClearPreview` hand one frame to the view. | Observation + rendering. | `LivePreviewController` `Pipeline/LivePreviewController.cs:74` owns the cadence, the start refusal while a loop is owned, the screen's closure before the join, and the bounded stop. |
| `SessionPersistenceEffects` `App/Composition/RuntimeEffects.cs:52` | `ShowPendingRecovery` (record to the window, then show the window), `ClearRecoveredText`, `NotifyHistoryChanged`. | Rendering. | `SessionPersistence` `Pipeline/SessionPersistence.cs:75` owns pending recovery, whether the copy may be written, the clear, and the history write. |
| `RuntimeShell.View` | `new WindowRuntimeView(this)` `App/App.xaml.cs:2287`: one dispatch each; `ShowPreview` asks the frame's `IsCurrent()` at the draw. | Rendering. | - |
| `LivePreviewEnabled`, `History`, `CustomWords` | Settings reads. | Observation. | - |
| `Audio`, `Engine`, `PreviewEngine`, `PreviewUnavailableReason`, `RecordingSessionId`, `Coordinator` | References to what the shell composed, read at the call. | Composition. | - |
| `Leaving` | `() => _exitRequested \|\| _disposed`. | Observation. Read by `SessionQueue` to refuse a key once the exit has begun - the shell's handler always refused it - before the coordinator's own closure, which the exit's first step makes. | - |

### The teardown, traced

1. `ApplicationLifetime.ExitCoreAsync` `App/Composition/ApplicationLifetime.cs:292` calls the shell's `ShutDownSession` part → `coordinator.ShutdownAsync(budget)` `App/App.xaml.cs:1128`.
2. `DictationSessionCoordinator.ShutdownCoreAsync` `Pipeline/DictationSessionCoordinator.cs:353`: `Close()`; wait for the command and the expiry notifications; wait for the holds; if either wait ran out, report `Unclean` with `Teardown: null` and tear nothing down; take the session gate; `_executor.TearDownAsync(Remaining())` `Pipeline/DictationSessionCoordinator.cs:402`.
3. `DictationSessionExecutor.TearDownAsync` `Pipeline/DictationSessionExecutor.cs:250`: stop the watchdog under the remainder, stop the three background owners under the remainder (`SessionBackgroundWork.StopAsync(deadline)` `Pipeline/SessionBackgroundWork.cs:109`: streaming, auto-stop, preview); if any is still running, report it with `Disposal: null` and dispose nothing; else issue `DisposeSessionAsync()` and join it under the remainder `Pipeline/DictationSessionExecutor.cs:264` - issued even when nothing is left, waited for only as long as remains.
4. `DictationSessionExecutor.DisposeSessionAsync` `Pipeline/DictationSessionExecutor.cs:270` - the disposal's order, in Pipeline: (a) `_effects.DetachCaptureObservers()`; (b) `_controller.DisposeAsync()` `Pipeline/DictationSessionExecutor.cs:275` → `PushToTalkSessionController.DisposeAsync` `Pipeline/PushToTalkSessionController.cs:395`: cancel a recording still open, then dispose the capture it owns, then its gate; (c) `_effects.ReleaseSession()`; (d) `_effects.DisposeDeliveryRoute()`. A step that throws is recorded through `RecordTeardownFailure` and the next still runs; the report says `DisposalFaulted`, which leaves the shutdown quiescent but not `Clean` `Pipeline/ShutdownReport.cs:69`.
5. The shell's three parts, through `SessionEffects` `App/Composition/SessionEffects.cs:204` → `SessionShell` `App/App.xaml.cs:1374`: `App.DetachCaptureObservers` `App/App.xaml.cs:2340` (one unsubscription), `App.ReleaseSession` `App/App.xaml.cs:2349` (two references to null), `App.DisposeDeliveryRoute` `App/App.xaml.cs:2356` (two references to null, one `Dispose()`). No wait, no order, no deadline, no check of what is in flight, no fault handling - those are steps 2 to 4.
6. Back in `ApplicationLifetime`: the session's dependencies (engines, provider, arbiter, owners, stores) are disposed only when `SessionQuiescent` is true and nothing is outstanding `App/Composition/ApplicationLifetime.cs:314`; otherwise they are retained and the host is ended. A shell that never composed a coordinator never built a controller, a capture or a route either (all four are built in `ConfigurePushToTalk` behind the hook), so there is no teardown outside the session.

### Bodies that combine operations or read session state - named, with why each is not a session decision

- `SessionQueue.HandAsync` `App/Composition/RuntimeComposition.cs:84`: two guards (leaving, no coordinator), one submit, one log line, one catch that logs. The leaving guard refuses a key once the exit has begun, before the exit's first step closes admission; it is a refusal, not a choice of what the session does, and the coordinator's own closure makes the same refusal a moment later.
- `OnSystemLifecycleTransitioned` `App/App.xaml.cs:743`: a log line, a run-state note for a session ending, then either an interruption or a resumed status. The `switch` on the transition is native translation (which Windows event is which); the choice of suspend and lock as interruptions is what the coordinator's `InterruptAsync` is for, and the leaving guard is the same refusal as above.
- `OnUpdateApplyRequested` `App/App.xaml.cs:873`: holds the session, marks leaving, attempts the restart, closes admission inside the hold, then exits. It decides the shell is leaving - its own fact - and expresses it through the coordinator's hold and closure; it drives no transition.
- `SessionEffects.RecordTransition` `App/Composition/SessionEffects.cs:58`: a flag on the hook and up to three log lines, selected by the kind of a transition already made.
- `SessionEffects.RecordDictationEdgeAsync` `App/Composition/SessionEffects.cs:164`: reads whether a session is attached and writes it; the value is the controller's, the moment is the executor's.
- `App.DisposeDeliveryRoute` `App/App.xaml.cs:2356`: two references let go and one disposal, so the route is gone whether or not the disposal throws; the executor records the throw.
- `SessionFinalizationEffects.ShowHeldStatus` and `RecordDelivery`: sentence and log-code selection from a finished report; nothing they choose reaches the session.
- `MainWindow.SetSessionStatus`: renders, and cancels the window's own microphone test on a Recording status - a rule about the window's test, not about the session.

No App body orders two session steps, holds a deadline, keeps recovery state, decides an abort or a
reset, or invokes the finalisation.

### Pipeline owners of each session decision

| Decision | Owner |
| --- | --- |
| Admission and serialisation (Busy, queued terminal, coalescing, refusal after closure, holds) | `DictationSessionCoordinator.SubmitAsync` `Pipeline/DictationSessionCoordinator.cs:454`, `TryHold` `Pipeline/DictationSessionCoordinator.cs:239`, `Close` `Pipeline/DictationSessionCoordinator.cs:319` |
| Resource admission of a press (memory refuses, disk withholds the recovery copy) | `DictationSessionExecutor.ExecutePushToTalkAsync` `Pipeline/DictationSessionExecutor.cs:487` with `SystemResourceAdmissionPolicy` (Core) |
| Background ordering (watchdog, preview, auto-stop, streaming; start and stop order; bounded stop) | `SessionBackgroundWork` `Pipeline/SessionBackgroundWork.cs:57` |
| Deadlines and cancellation | Recording: `RecordingWatchdog` armed by `SessionBackgroundWork.StartAsync`. Finalisation: `MaximumFinalProcessingDuration` armed in `DictationSessionExecutor.FinalizeAsync` `Pipeline/DictationSessionExecutor.cs:602`, cancelled by the coordinator's `InterruptAsync` for an admitted interruption or by `CancelProcessing` `Pipeline/DictationSessionCoordinator.cs:304` (the shell's exit policy), released in the command's `finally` (`ReleaseProcessingDeadline`, `Pipeline/DictationSessionExecutor.cs:372` and `Pipeline/DictationSessionExecutor.cs:587`). Shutdown: `StopBudget` handed down from `ShutdownAsync` through `TearDownAsync` and `StopAsync(deadline)`. Interruption patience: `DictationSessionCoordinator.InterruptionPatience`. |
| Recovery state (pending recovery, whether the copy may be written, clear, history write) | `SessionPersistence` `Pipeline/SessionPersistence.cs:75` |
| Abort and reset (a failed or timed-out command; a stale terminal ignored) | `DictationSessionExecutor.RecoverFailedSessionAsync` `Pipeline/DictationSessionExecutor.cs:404`, `TimeOutAsync` `Pipeline/DictationSessionExecutor.cs:424`, `ForSession` check `Pipeline/DictationSessionExecutor.cs:471` |
| Finalisation invocation and its steps | `DictationSessionExecutor.FinalizeAsync` `Pipeline/DictationSessionExecutor.cs:602` → `SessionFinalizationRunner.RunAsync` `Pipeline/SessionFinalizationRunner.cs:205` |
| Session teardown sequencing and its gate | `DictationSessionCoordinator.ShutdownCoreAsync` → `DictationSessionExecutor.TearDownAsync` → `DisposeSessionAsync` (observers off, controller and capture, references, route; faults recorded and reported); the shell's parts are the three single operations in step 5 above |
| The local polish runtime's abort at exit | `ApplicationLifetime` (`AbortPolishRuntime`, after the shell closes and before the exit policy); the shell's body is one call into `EgOneServerManager.TerminateImmediately` |
| Exit sequencing around the session (before: admission, drain, inputs; after: quiescence gate, dependencies, run completion, escalation) | `ApplicationLifetime` `App/Composition/ApplicationLifetime.cs:266` (App/Composition, tested without a window) |

## Deterministic parity

The Windows deterministic corpus is ported from macOS behavior as platform-neutral fixtures, including
emoji, punctuation, casing, custom words, numbers, dates, currency, URLs, and failure cases. Any intended
platform difference is documented beside the fixture rather than hidden in implementation code.
