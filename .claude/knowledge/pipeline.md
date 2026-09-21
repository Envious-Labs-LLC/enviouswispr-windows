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
  the coordinator (`TryHold`), so a press during a download is `Busy`. Shutdown closes admission before its
  first await, gives the running command ten seconds, and runs the shell's session teardown as the last
  thing under the session (or after a further ten seconds, beside a command that would not finish). The
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

## Deterministic parity

The Windows deterministic corpus is ported from macOS behavior as platform-neutral fixtures, including
emoji, punctuation, casing, custom words, numbers, dates, currency, URLs, and failure cases. Any intended
platform difference is documented beside the fixture rather than hidden in implementation code.
