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
  then the three background owners, then (only behind owners that all finished) the shell's disposal of
  the capture, the controller and the delivery route, each reported (`SessionTeardownReport`; a stop
  given zero still cancels and observes). What did not finish is named in the report
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
  first await, then the presentation drain (the window's gate closed, its microphone test, model discovery and history I/O stopped and joined, the settings write in flight kept; step 12) - through the shell closing, the input sources, the session's
  shutdown under what is left, the polish warm-up and heartbeat joins, the disposals, the run's
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
  line is attempted, not waited for. Every path out (tray, window, update, system ending) shares the same
  two cached tasks, so no step runs twice. The shell keeps only its UI-thread unsubscriptions, the
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
| Hotkey press/release/cancel (`WindowsPushToTalkHook.Signalled`, subscribed at `App/App.xaml.cs:1319`) | `OnPushToTalkSignalled` `App/App.xaml.cs:1868` | QuickAdd: one log line and `HandleQuickAddAsync` (not a session; see below). Every other signal: `_runtime.SubmitAsync(args.Signal)` `App/App.xaml.cs:1877`. **Translation.** | `SessionRuntime.SubmitAsync` `App/Composition/RuntimeComposition.cs:160` → `SessionQueue.HandAsync` `App/Composition/RuntimeComposition.cs:84` (refuses while the shell is leaving or before a coordinator exists; otherwise one submit and one log line) → `DictationSessionCoordinator.SubmitAsync` `Pipeline/DictationSessionCoordinator.cs:454` (admission: Busy, queued terminal, coalesced terminal, refused after closure; the press captures its target through `CaptureStartContext` handed in at `App/Composition/SessionComposition.cs:86`) → `DictationSessionExecutor.ExecuteAsync` `Pipeline/DictationSessionExecutor.cs:247` → `ExecutePushToTalkAsync` `Pipeline/DictationSessionExecutor.cs:397` (recovered text waiting, resource admission, watchdog stop, press/release/cancel/Escape-as-release, background start, finalisation, cancel/failure reset). |
| Auto-stop's release, watchdog's timeout (loops in Pipeline, `RecordingTimers.cs`) | `RecordingTimerEffects.Post` `App/Composition/RuntimeEffects.cs:18`, `RecordingTimerEffects.RecordingTimedOut` `App/Composition/RuntimeEffects.cs:20` | One signal each onto the shared queue, the auto-stop's carrying the recording it was for. **Translation.** | `SessionQueue.HandAsync` / `SessionQueue.TimeOut` `App/Composition/RuntimeComposition.cs:109` → `DictationSessionCoordinator.TimeOutAsync` `Pipeline/DictationSessionCoordinator.cs:311` → `DictationSessionExecutor.TimeOutAsync` `Pipeline/DictationSessionExecutor.cs:368`. When the loops start and stop, and their durations' use, is `SessionBackgroundWork` (below). |
| Windows suspend, lock, resume, unlock, session ending (`SystemLifecycleMonitor.Transitioned`, subscribed at `App/App.xaml.cs:684`) | `OnSystemLifecycleTransitioned` `App/App.xaml.cs:695` | One log line for the transition (under the dictation's scope when one is in flight); for a session ending, one fire-and-forget run-state write `App/App.xaml.cs:716`; for suspend or lock, `coordinator.InterruptAsync(transition)` `App/App.xaml.cs:739` unless the shell is leaving; for resume or unlock, one status. **Translation + observation + rendering.** Which transitions count as an interruption is the one decision here, and it chooses a command, not what the command does. | `DictationSessionCoordinator.InterruptAsync` `Pipeline/DictationSessionCoordinator.cs:277` (admit whatever is queued, cancel only the finalisation that was in flight before admission, five-second patience → `ExpireAsync`) → `DictationSessionExecutor.InterruptAsync` `Pipeline/DictationSessionExecutor.cs:264` (release and finalise a recording as a key would, reset anything else, nothing when idle). |
| UAT journey fixture (environment switches only) | `RunPublicFixtureJourneyUatAsync` `App/App.xaml.cs:1996` | Waits for the harness's start event, then `_runtime.SubmitAsync(Pressed)` `App/App.xaml.cs:2035`, the fixture's hold, and `SubmitAsync(Released` or `Cancelled)` `App/App.xaml.cs:2050`: a scripted key. **Translation.** | The same queue and executor as a key. |
| Update check / update apply (window buttons) | `OnUpdateCheckRequested` `App/App.xaml.cs:795`, `OnUpdateApplyRequested` `App/App.xaml.cs:823` | `coordinator.TryHold()` (`App/App.xaml.cs:804` and `:833`) around the download or the restart; a refused hold renders `BusyDictating`. The apply path sets the leaving flag inside the hold and closes admission `App/App.xaml.cs:861` before the exit runs. **One external operation (the hold) + rendering; the close is the shell's exit policy, made through the coordinator's own gate.** | `DictationSessionCoordinator.TryHold` `Pipeline/DictationSessionCoordinator.cs:239` (refused when closed, pending, running or held; a press under a hold is Busy; a terminal waits) and `Close` `Pipeline/DictationSessionCoordinator.cs:319`. |
| Exit (tray, window close, update restart, system ending; every path shares `PrepareForExitAsync` `App/App.xaml.cs:997`) | `LifetimeParts()` `App/App.xaml.cs:1033` handed to `ApplicationLifetime.ExitCoreAsync` `App/Composition/ApplicationLifetime.cs:252` | Each part is one step body: `CloseAdmission: () => _sessionCoordinator?.Close()` `App/App.xaml.cs:1034`; `CancelProcessing` `App/App.xaml.cs:1047` (the exit policy: cut a transcription in flight, and the polish warm-up); `ReleaseInputs` (unsubscribe and dispose the lifecycle monitor `App/App.xaml.cs:1056` and the hook `App/App.xaml.cs:1065`); `ShutDownSession: budget => coordinator.ShutdownAsync(budget)` `App/App.xaml.cs:1071`; `Quiesce` (join the polish warm-up, cancel and join the heartbeat); `DisposeSessionDependencies` (the "session teardown" step `App/App.xaml.cs:1112` runs `TearDownSessionAsync` itself only when no coordinator was ever composed - a shell that failed to build its hook - and otherwise only reads `_sessionTornDownCleanly`; then the engines, the polish provider, the arbiter, the owners, the stores, one disposal each); `DisposeShell` (presentation, activation channel, tray, then the coordinator's own disposal `App/App.xaml.cs:1187`); `CompleteRun`, `CloseRunState`, `DisposeLogger`. **Composition of steps; each body is one disposal or one join.** The order, the budget, the remainder handed to each step, the quiescence gate before the dependencies (`App/Composition/ApplicationLifetime.cs:295`), retention and escalation are `ApplicationLifetime`'s. | `DictationSessionCoordinator.ShutdownAsync` `Pipeline/DictationSessionCoordinator.cs:344` → `ShutdownCoreAsync` `Pipeline/DictationSessionCoordinator.cs:353`: close, wait for the command and the expiries, wait for the holds, take the gate, then `DictationSessionExecutor.TearDownAsync` `Pipeline/DictationSessionExecutor.cs:230` under the remainder. |

Not session entry points, listed because they read session state: `HandleQuickAddAsync`
`App/App.xaml.cs:1880` refuses while a recording is in flight and asks
`SelectionAcquisitionPolicy.Decide` (Core) with `IsProcessing` `App/App.xaml.cs:1914`
- it never submits to the session; `OnSpeedCheckRequested` `App/App.xaml.cs:573`
declines to measure under a recording; `OnAudioDevicesChanged` `App/App.xaml.cs:749`
and `PrepareForExitAsync` open the dictation's log scope from `_sessionController?.CurrentSession`
(observation); `OnRecoveryCleared` `App/App.xaml.cs:766`
forwards the window's clear to the persistence owner; `OnAudioLevelChanged`
`App/App.xaml.cs:1380` is the level meter (rendering).

### `SessionShell` (construction site `App/App.xaml.cs:1306`; record `App/Composition/SessionComposition.cs:25`)

| Member | Supplied body | Classification |
| --- | --- | --- |
| `View` | `new WindowSessionView(this)` `App/App.xaml.cs:2214`: `ShowStatus`, `ShowNotice`, `ReportDelivery` each dispatch one window call; `ShowMainWindow` shows the window. | Rendering. |
| `AttachedSession` | `() => _sessionController?.CurrentSession` `App/App.xaml.cs:1308` - the shell's reference to its controller, null once its teardown has let go of it. | Observation. |
| `Dictation` | `() => _settings.Preferences.Dictation`. | Observation. |
| `Engine`, `Delivery` | `() => _transcriptionEngine`, `() => _textDelivery` - the parts the shell composed (`App/App.xaml.cs:1280`). | Composition. |
| `Options` | `() => new FinalizationOptions(_customWords, _deterministicTextOptions, CurrentPolishSetup())` - three settings reads packaged. | Composition. |
| `CloudPolishProviderName`, `RunId` | `() => _cloudPolishConsent?.ProviderName`, `() => _runId`. | Observation. |
| `RecordingActive` | `active => _pushToTalkHook?.SetRecordingActive(active)`. | One external operation (a flag on the hook). |
| `ArchiveAudio` | `audio => ArchiveDictationAudio(audio)` `App/App.xaml.cs:2284` - `[Conditional("DEBUG")]`, a file write with its own retention, failures swallowed; compiled out of release. | One external operation. |
| `TearDownSession` | `TearDownSessionAsync` `App/App.xaml.cs:2234` - see the teardown trace below. | A list of three disposals; no timing, no ordering decision. |

### `IDictationSessionEffects` → `SessionEffects` (`App/Composition/SessionEffects.cs:17`, constructed at `App/Composition/SessionComposition.cs:82`)

| Member | Body | Classification | The decision it serves, in Pipeline |
| --- | --- | --- | --- |
| `EscapeRecoveryEnabled` | `parts.Shell.Dictation().EscapeRecoveryEnabled`. | Observation. | Read once as a recording starts, kept as `_escapeRecoveryForSession` `Pipeline/DictationSessionExecutor.cs:482`; whether Escape releases or cancels is the executor's `Pipeline/DictationSessionExecutor.cs:455`. |
| `RecordResourcePressure` | One `AppLogEntry`. | Observation. | Admission: `SystemResourceAdmissionPolicy.Evaluate(_resources.Probe())` `Pipeline/DictationSessionExecutor.cs:431`. |
| `ShowRecoveredTextWaiting`, `ShowMemoryCritical`, `ShowDiskLow` | Notices and statuses on the view, then the window. | Rendering. | The same admission. |
| `RecordTransition` `App/Composition/SessionEffects.cs:58` | Sets the hook's recording flag through `RecordingActive` for a start and for each ending kind; one log line for the transition, plus the capture's open and stream timings on a start. | One external operation + observation. The `switch` maps a transition kind to a log code and a flag value; it chooses nothing about the session. | The transition was made by the controller under the executor's `switch` on the signal `Pipeline/DictationSessionExecutor.cs:456`. |
| `RecordingSettings` `App/Composition/SessionEffects.cs:106` | The watchdog duration (`RecordingLimits.WatchdogDuration()`, production limit or a bounded UAT override) and the dictation preferences, as values. | Observation. | `SessionBackgroundWork.StartAsync` `Pipeline/SessionBackgroundWork.cs:85` decides the order (watchdog, preview, auto-stop, streaming) and the watchdog arms the deadline. |
| `ShowInterruptionPreserving`, `ShowTransitionStatus`, `ShowSessionRecovered`, `ShowInterruptionPending`, `ShowRecordingTimedOut` | One status each; `SessionStatus` maps a transition kind to a sentence. | Rendering. | Interruption, recovery and time-out are the executor's (`InterruptAsync`, `RecoverFailedSessionAsync` `Pipeline/DictationSessionExecutor.cs:348`, `TimeOutAsync`); the five-second patience is the coordinator's `ExpireAsync` route `Pipeline/DictationSessionCoordinator.cs:178`. |
| `RecordSessionFailure`, `RecordSessionRecovered`, `RecordInterruptionFailure`, `RecordRecordingTimedOut` | One log line each. | Observation. | As above. |
| `RecordDictationEdgeAsync` `App/Composition/SessionEffects.cs:164` | One run-state store write of whether a dictation is in flight (`AttachedSession() is not null`), for the run in progress; a false return or an exception is one log line and swallowed. | One external operation. | When the edge is written - the `finally` of every command - is the executor's (`Pipeline/DictationSessionExecutor.cs:317`, `:393`, `:532` - the interruption's, the time-out's and the key's `finally`). |
| `TearDownSessionAsync` `App/Composition/SessionEffects.cs:204` | Forwards to the shell delegate. | Forwarding. | Called only from `DictationSessionExecutor.TearDownAsync` `Pipeline/DictationSessionExecutor.cs:243`. |

### `ISessionFinalizationEffects` → `SessionFinalizationEffects` (`App/Composition/SessionFinalizationEffects.cs:16`, constructed at `App/Composition/SessionComposition.cs:74`)

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
executor armed `Pipeline/DictationSessionExecutor.cs:553`
(`MaximumFinalProcessingDuration` `Pipeline/DictationSessionExecutor.cs:112`),
and delivery's closure is the runner's own lock `Pipeline/SessionFinalizationRunner.cs:146`. The text pipeline
and polish effects (`TranscriptFinalizationEffects` `App/Composition/RuntimeEffects.cs:68`)
are log lines and one recovery write forwarded to `SessionPersistence`.

### Runtime effects and `RuntimeShell` (construction site `App/App.xaml.cs:168`; record `App/Composition/RuntimeComposition.cs:42`)

| Adapter / member | Body | Classification | Owner of the decision |
| --- | --- | --- | --- |
| `RecordingTimerEffects` `App/Composition/RuntimeEffects.cs:14` | `Audio` reads the capture; `Post` and `RecordingTimedOut` hand one signal each to the queue. | Observation + translation. | `RecordingWatchdog` and `AutoStopMonitor` (`Pipeline/RecordingTimers.cs`) own the watch, the silence rule and the retire-and-join; the executor owns what the command does. |
| `StreamingTranscriptionEffects` `App/Composition/RuntimeEffects.cs:24` | Reads `LivePreviewEnabled`, `Engine`, `Audio`. | Observation. | `StreamingTranscriptionController` decides whether to yield to preview and how the head start is used. |
| `LivePreviewEffects` `App/Composition/RuntimeEffects.cs:34` | Reads `Enabled`, `Engine`, `EngineUnavailableReason`, `Audio`, `RecordingSessionId`; `ShowPreview`/`ClearPreview` hand one frame to the view. | Observation + rendering. | `LivePreviewController` `Pipeline/LivePreviewController.cs:74` owns the cadence, the start refusal while a loop is owned, the screen's closure before the join, and the bounded stop. |
| `SessionPersistenceEffects` `App/Composition/RuntimeEffects.cs:52` | `ShowPendingRecovery` (record to the window, then show the window), `ClearRecoveredText`, `NotifyHistoryChanged`. | Rendering. | `SessionPersistence` `Pipeline/SessionPersistence.cs:75` owns pending recovery, whether the copy may be written, the clear, and the history write. |
| `RuntimeShell.View` | `new WindowRuntimeView(this)` `App/App.xaml.cs:2182`: one dispatch each; `ShowPreview` asks the frame's `IsCurrent()` at the draw. | Rendering. | - |
| `LivePreviewEnabled`, `History`, `CustomWords` | Settings reads. | Observation. | - |
| `Audio`, `Engine`, `PreviewEngine`, `PreviewUnavailableReason`, `RecordingSessionId`, `Coordinator` | References to what the shell composed, read at the call. | Composition. | - |
| `Leaving` | `() => _exitRequested \|\| _disposed`. | Observation. Read by `SessionQueue` to refuse a key once the exit has begun - the shell's handler always refused it - before the coordinator's own closure, which the exit's first step makes. | - |

### The teardown, traced

1. `ApplicationLifetime.ExitCoreAsync` `App/Composition/ApplicationLifetime.cs:273` calls the shell's `ShutDownSession` part → `coordinator.ShutdownAsync(budget)` `App/App.xaml.cs:1071`.
2. `DictationSessionCoordinator.ShutdownCoreAsync` `Pipeline/DictationSessionCoordinator.cs:353`: `Close()`; wait for the command and the expiry notifications; wait for the holds; if either wait ran out, report `Unclean` with `Teardown: null` and tear nothing down; take the session gate; `_executor.TearDownAsync(Remaining())` `Pipeline/DictationSessionCoordinator.cs:402`.
3. `DictationSessionExecutor.TearDownAsync` `Pipeline/DictationSessionExecutor.cs:230`: stop the watchdog under the remainder, stop the three background owners under the remainder (`SessionBackgroundWork.StopAsync(deadline)` `Pipeline/SessionBackgroundWork.cs:109`: streaming, auto-stop, preview); if any is still running, report it with `Shell: null` and do not call the shell; else `BoundedJoin.JoinAsync(_effects.TearDownSessionAsync(), budget.Left)` `Pipeline/DictationSessionExecutor.cs:243`.
4. `SessionEffects.TearDownSessionAsync` `App/Composition/SessionEffects.cs:204` → `SessionShell.TearDownSession` = `App.TearDownSessionAsync` `App/App.xaml.cs:1317`.
5. `App.TearDownSessionAsync` `App/App.xaml.cs:2234`: unsubscribe the capture's level event; `_sessionController.DisposeAsync()` through `TryCleanupAsync` (one log line on failure) and null the controller and capture references; `_textTargetAdapter.Dispose()` through `TryCleanup`; null the adapter and route; record `_sessionTornDownCleanly`. Three disposals in the only order that works (the controller owns the capture; the route owns nothing); no wait, no deadline, no check of what is in flight - those were made in steps 2 and 3 before this ran.
6. Back in `ApplicationLifetime`: the session's dependencies (engines, provider, arbiter, owners, stores) are disposed only when `SessionQuiescent` is true and nothing is outstanding `App/Composition/ApplicationLifetime.cs:295`; otherwise they are retained and the host is ended.

### Bodies that combine operations or read session state - named, with why each is not a session decision

- `SessionQueue.HandAsync` `App/Composition/RuntimeComposition.cs:84`: two guards (leaving, no coordinator), one submit, one log line, one catch that logs. The leaving guard refuses a key once the exit has begun, before the exit's first step closes admission; it is a refusal, not a choice of what the session does, and the coordinator's own closure makes the same refusal a moment later.
- `OnSystemLifecycleTransitioned` `App/App.xaml.cs:695`: a log line, a run-state note for a session ending, then either an interruption or a resumed status. The `switch` on the transition is native translation (which Windows event is which); the choice of suspend and lock as interruptions is what the coordinator's `InterruptAsync` is for, and the leaving guard is the same refusal as above.
- `OnUpdateApplyRequested` `App/App.xaml.cs:823`: holds the session, marks leaving, attempts the restart, closes admission inside the hold, then exits. It decides the shell is leaving - its own fact - and expresses it through the coordinator's hold and closure; it drives no transition.
- `SessionEffects.RecordTransition` `App/Composition/SessionEffects.cs:58`: a flag on the hook and up to three log lines, selected by the kind of a transition already made.
- `SessionEffects.RecordDictationEdgeAsync` `App/Composition/SessionEffects.cs:164`: reads whether a session is attached and writes it; the value is the controller's, the moment is the executor's.
- `App.TearDownSessionAsync` `App/App.xaml.cs:2234`: three disposals, as traced above.
- The lifetime's "session teardown" step `App/App.xaml.cs:1112`: runs the disposals itself only for a shell that never composed a coordinator (the hook could not be built, so no session ever existed); with a coordinator it only reads the flag the teardown recorded.
- `SessionFinalizationEffects.ShowHeldStatus` and `RecordDelivery`: sentence and log-code selection from a finished report; nothing they choose reaches the session.

No App body orders two session steps, holds a deadline, keeps recovery state, decides an abort or a
reset, or invokes the finalisation.

### Pipeline owners of each session decision

| Decision | Owner |
| --- | --- |
| Admission and serialisation (Busy, queued terminal, coalescing, refusal after closure, holds) | `DictationSessionCoordinator.SubmitAsync` `Pipeline/DictationSessionCoordinator.cs:454`, `TryHold` `Pipeline/DictationSessionCoordinator.cs:239`, `Close` `Pipeline/DictationSessionCoordinator.cs:319` |
| Resource admission of a press (memory refuses, disk withholds the recovery copy) | `DictationSessionExecutor.ExecutePushToTalkAsync` `Pipeline/DictationSessionExecutor.cs:431` with `SystemResourceAdmissionPolicy` (Core) |
| Background ordering (watchdog, preview, auto-stop, streaming; start and stop order; bounded stop) | `SessionBackgroundWork` `Pipeline/SessionBackgroundWork.cs:57` |
| Deadlines and cancellation | Recording: `RecordingWatchdog` armed by `SessionBackgroundWork.StartAsync`. Finalisation: `MaximumFinalProcessingDuration` armed in `DictationSessionExecutor.FinalizeAsync` `Pipeline/DictationSessionExecutor.cs:546`, cancelled by the coordinator's `InterruptAsync` for an admitted interruption or by `CancelProcessing` `Pipeline/DictationSessionCoordinator.cs:304` (the shell's exit policy), released in the command's `finally` (`ReleaseProcessingDeadline`, `Pipeline/DictationSessionExecutor.cs:316` and `:531`). Shutdown: `StopBudget` handed down from `ShutdownAsync` through `TearDownAsync` and `StopAsync(deadline)`. Interruption patience: `DictationSessionCoordinator.InterruptionPatience`. |
| Recovery state (pending recovery, whether the copy may be written, clear, history write) | `SessionPersistence` `Pipeline/SessionPersistence.cs:75` |
| Abort and reset (a failed or timed-out command; a stale terminal ignored) | `DictationSessionExecutor.RecoverFailedSessionAsync` `Pipeline/DictationSessionExecutor.cs:348`, `TimeOutAsync` `Pipeline/DictationSessionExecutor.cs:368`, `ForSession` check `Pipeline/DictationSessionExecutor.cs:415` |
| Finalisation invocation and its steps | `DictationSessionExecutor.FinalizeAsync` `Pipeline/DictationSessionExecutor.cs:546` → `SessionFinalizationRunner.RunAsync` `Pipeline/SessionFinalizationRunner.cs:205` |
| Session teardown sequencing and its gate | `DictationSessionCoordinator.ShutdownCoreAsync` → `DictationSessionExecutor.TearDownAsync`; the shell's part is the disposal list in step 5 above |
| Exit sequencing around the session (before: admission, drain, inputs; after: quiescence gate, dependencies, run completion, escalation) | `ApplicationLifetime` `App/Composition/ApplicationLifetime.cs:252` (App/Composition, tested without a window) |

## Deterministic parity

The Windows deterministic corpus is ported from macOS behavior as platform-neutral fixtures, including
emoji, punctuation, casing, custom words, numbers, dates, currency, URLs, and failure cases. Any intended
platform difference is documented beside the fixture rather than hidden in implementation code.
