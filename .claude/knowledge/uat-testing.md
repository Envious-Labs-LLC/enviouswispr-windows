# Driving the real app in a test

**when:** the journey harness, synthetic input, `SendInput`, a UAT verdict, "did the hotkey work", "the
harness says". Read `diagnostics.md` with this: the log it describes is the oracle everything here reads.

The method is the macOS harness's ("WisprEyes"), arrived at there by brute force over a summer and ported
here after this machine reproduced its founding failure verbatim. **Make something happen, then read what
the app said about itself.** Every failure below came from trusting the harness's own account instead.

## RULE: the-app-log-is-the-oracle-and-the-injector-has-no-vote
A verdict comes from `app.jsonl` (`HotkeyReady`, `DictationRecordingStarted`, `TextDeliveryCompleted`,
`ApplicationCleanShutdown`) or from the controlled delivery target. It never comes from the harness's
account of what it did. `SendKey` returns void; `SendInput`'s count is about insertion, not arrival, and a
value that is present will be read as "the OS said it worked" at 2am. The named-event start
(`ENVIOUSWISPR_UAT_JOURNEY_START_EVENT`) needs no injection at all and is the default take; injected
input is ONE narrow declared capability (`--synthetic-hotkey`), not the foundation. macOS has no
zero-injection start and said this design is the one it would build if starting again.

## RULE: assert-the-payload-before-the-syscall
Two preconditions in the injector wrapper, before anything leaves the process: the Win64 `INPUT` is 40
bytes, and the payload is not empty. Nothing downstream - return value, hooks, desktop, foreground,
timing - can expose a well-formed request for zero movement, because by the time any of them sees it, it
is a legitimate event. See FACT: six-explanations-one-non-event below for the night this cost.

## RULE: instrument-invalid-is-not-a-product-verdict
`JourneyExpectationException.Instrument(...)` ends the run with exit code **3** and the words
`INSTRUMENT INVALID`. A key the resolver would not guess, a payload that failed its precondition, a
profile it could not read, a control window that meant nothing: none of these say anything about the app,
and a runner must never average them into a pass rate. Exit **2** is a product expectation not met. A red
row for a scenario the harness could not STAGE is worse than a skip, because it accuses correct code.

## RULE: resolve-the-binding-or-refuse-never-guess
Ported from the macOS `ptt_binding` contract. Three states, not two: **absent** (no settings file in the
isolated profile) resolves through the SAME default the app applies, `DictationPreferences.Default`,
because a fresh profile really is running F8 with nothing on disk; **valid** is a gesture the app's own
parser accepts and its own key map (`WindowsVirtualKeyMap`, via `InternalsVisibleTo`) can name;
**malformed** is refused, because it says nothing about what the app is listening for. A resolver that
falls back to a hardcoded key presses something nothing is listening for and files the FAIL against the
product - on macOS, 2026-08-10, on the one branch where a hotkey FAIL was most likely to be believed.
Configuration and drivability are different questions: the resolver owns the first; a chord it refuses is
refused because the harness declared it undrivable, and the message says which.

## RULE: a-positive-control-needs-its-negative-half-and-the-window-has-to-mean-something
`HotkeyReady → DictationRecordingStarted` after a press proves the marker appears when you press. It does
not prove the marker is absent when you do not, and a marker that can arrive for another reason turns every
run green. So the synthetic take holds still for a quiet window first and requires the marker ABSENT, then
presses and requires it PRESENT. And "absent for N" only certifies anything once the same marker has been
watched arriving in well under N - the run measures press-to-recording latency and refuses to count the
absence if the window was not comfortably longer. That check is also a drift alarm for start-up latency.

## RULE: declare-the-boundary-before-a-scenario-finds-it
Single non-modifier keys are drivable: the hook is event-driven and a 0 ms synthetic press lands. A
modifier-only tap (`Ctrl+Win`, #66) completes on a **40 ms poll** (`HotkeyEdgeTracker` tick), so an
instant synthetic press can go down and up between two ticks and be seen by nothing - silently,
intermittently, looking exactly like a flaky hotkey. macOS has the twin: three synthetic presses inside its
500 ms chain window read as double-then-single EVERY time, written down as a hard limit. If a chord is ever
driven synthetically: hold well past the tick with margin, budget from the observed TAIL not the mean, and
log the app-side observed hold against what the injector asked for - a disagreement is the instrument.

## RULE: the-harness-must-not-reach-the-founders-real-data-or-play-sound
Isolated profile, isolated credential suffix, its own controlled target, clipboard snapshot and restore. It
refuses to run while an unowned `EnviousWispr.App` exists, because a second low-level hook would also
receive the injected key and start a REAL take into whatever is focused. **The refusal is by process
name**; a differently-named build holds the same hook and is invisible to it (macOS identifies by
executable path for that reason). And the machine is in the founder's home: the fixture is the microphone,
and the modes that play audio through the speakers are for a free room, not the small hours.

## RULE: read-the-log-during-ordinary-use-before-building-a-driver
The founder dictates all day and `app.jsonl` carries the whole chain. A staged take proves the path runs;
an unstaged one proves it on input nobody chose. Check the log for the feature's marker before writing a
driver for it.

## FACT: one-hotkey-route-verified-by-reading-2026-09-05
`WindowsPushToTalkHook` detects every press through `WH_KEYBOARD_LL` → `HotkeyEdgeTracker.Process`.
`RegisterHotKey` appears only in `ProbeConflict()` - register, unregister, return - and never receives a
keystroke. The hook marshals `Flags` and never inspects it: **no `LLKHF_INJECTED` filter.** So a synthetic
press takes exactly the path a finger takes, and there is no product decision to make about accepting
synthetic input. `architecture.md` described the intent ("`RegisterHotKey` where possible"); the code
chose the hook because push-to-talk needs the key-UP edge. A peer reading the doc inferred two routes.
Settled by reading the 425-line file, which is cheaper than the dual logging that would have measured it.

## FACT: six-explanations-one-non-event-2026-09-05
Two sessions (2026-09-04, 05) concluded injected input was inert on this PC and, in order, blamed
`GameInputSvc`'s invisible foreground window, the tool sandbox, screen lock, UIPI, and hook latency; a
peer added "read insertion as arrival". **The probe had never injected anything.** It built `INPUT` by
PowerShell nested assignment (`$mv.mi.dx = 40` writes to a COPY of a value type), so the struct sent was
`dx=0 dy=0 dwFlags=0` - proven by dumping the bytes, not by story. `SendInput` inserted one valid request
for nothing and returned 1, correctly. A second helper used a 48-byte struct where Win64 needs 40 and was
rejected outright. With a C#-built payload: 0 ms time-to-land, 3/3, no hooks; own LL hooks saw the events
flagged INJECTED; `SetCursorPos` immediate. Input desktop `Default`, session 1, unlocked, no clip, no
third-party DLL in the process.

**What was right and what was lucky, kept apart.** The bisection with own LL hooks told the truth because
that probe happened to build its struct in C#; "install hooks to diagnose injection" is the wrong lesson.
The right ones: build P/Invoke payloads where they mutate in place and ASSERT them (RULE above); ruling
something out is not finding the cause; find the artifact that is TRUE in one world and FALSE in the
other instead of re-running the failing action in a changed environment - `OpenInputDesktop` said
`Default` in one call, where a behavioural retest can succeed for a reason you did not change. And a
half-result that looks like progress (the service DID hold foreground) is not to be counted.
Ref: #77 correction, #53, #66 comments of 2026-09-05; probes under the session scratchpad.
