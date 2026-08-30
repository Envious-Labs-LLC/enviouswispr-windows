# Session state, 2026-08-28

Written before a compaction so the next session starts from facts rather than from a summary.

## Where the work is

Branch `codex/ui-design-system`, open as PR #62.

**`d5f0a28` is the last PUSHED commit and it is RED**, on one test: a `$` anchor that cannot match a
CRLF checkout. **`6a5f263` fixes it, is committed locally, and is NOT PUSHED** because the
push-rate guard refused a seventh push this session. That guard is correct: six of those pushes
were CI rounds spent on compile and analyser errors. Do not reach for `SKIP_PUSH_CHECKS`.
The founder was given the command.

**BUILD AND TEST ON THE WINDOWS MACHINE OVER SSH. DO NOT USE CI AS THE COMPILER.**
Measured 2026-08-28: reset the rig checkout, copy the changed files, build, and run all 716 tests
takes **about 30 seconds** end to end. A CI round is ten minutes. Six CI rounds were spent this
session on compile and analyser errors before this was tried, because a "do not build on that
machine" commitment made under the CPU-failure theory outlived the theory, and outlived fourteen
clean rebuilds on that same machine during the load test.

Recipe, from the Mac. The rig checkout is `C:\Users\saura\agent-workspace\enviouswispr-windows`.

1. On the rig, park any local edits recoverably, fetch, and reset hard to `origin/<branch>`.
2. `scp` only the files that differ from the pushed head, straight into that checkout.
3. Run `C:\Users\saura\verify-here.ps1`, which builds the app and runs the architecture suite.

Two things the runner must do or it lies to you. **Set `PriorityClass = 'High'`** on the started
process, or it lands on the efficiency cores. **Gate on `Build succeeded` in the LOG, never on the
exit code** — `Start-Process -PassThru` with redirected output returns an empty `ExitCode` here, so
an `-ne 0` test is true for a build that plainly succeeded and had already linked the app.

Known local-only failure on that machine: `WindowsCredentialApiKeyStoreTests` fails with
"Credential Manager delete failed". It is environmental, it predates today's work, and CI does not
show it. Everything else passed: **715 of 716 on the rig**, with the CRLF fix in place.

Every project targets `net10.0-windows10.0.26100.0` and `TreatWarningsAsErrors` is on with
`latest-recommended` analysers, so an analyser suggestion is a build failure. Nothing on the Mac can
compile or analyse this code.

`scripts/check-cs-escapes.py` closes one narrow slice of that gap locally: it reads C# the way the
compiler does and reports any invalid escape in a regular string literal. It fires on the literal
that broke the build and reports zero across every tracked C# file. **It is not armed** - wiring it
into `scripts/validate.ps1` before the builds is #72.

**4 tests SKIP on CI** - the macOS parity checks, because `macos-source/` is a local snapshot that
is not committed. A green CI is not evidence about parity.

## Everything pending is now a GitHub issue

Filed 2026-08-28 in `Envious-Labs-LLC/enviouswispr-windows`, so nothing below depends on this file
surviving: #63 pill cannot offer an action, #64 pill has one severity, #65 pill reads its own
message to decide its appearance, #66 make Ctrl+Win the default, #67 the old build stealing F8,
#68 SSH work lands on the efficiency cores, #69 the UAT harness crashes to report a failure,
#70 the blue screens, #71 parity blocked on hardware or a decision, #72 arm the escape checker.

## Shipped this session

- **Page icons.** A page header now reads its glyph off its own sidebar row, so the two cannot
  disagree. Five had drifted and two wore another page's icon.
- **Keybind clashes** are named as they are made, in the field, by the same detector Save uses.
  Save always refused a clash, so nothing broken could ever have been written; the silence until
  Save was the defect.
- **The gesture engine compiles and its tests pass.** Hold, double tap, single tap to stop, triple
  tap to cancel.

## The Windows rig: the CPU claim is RETRACTED

An earlier version of this file said the i9-14900KF was computing wrongly. That claim was wrong and
the evidence for it did not survive contact with the machine's own records. Anyone reading a
downstream artifact that repeats it should treat this section as the correction.

**What the crash records actually say.** Both blue screens bucket as
`NOBLOB_HYPERVISOR_ERROR_Unhandled_PageFault_<offset>_IMAGE_hvix64.exe`, an unhandled page fault
inside the Microsoft hypervisor, with parameter 4 at `ffffe70001205aa0` and `ffffe70001205ad0`
nineteen hours apart. Two faults 0x30 apart is a repeatable code path, not random corruption.

**Three readings argue against the CPU**, all from `.claude/scripts`-free read-only probes:
microcode revision `0x133`, so Intel's Vmin Shift mitigation is present; zero machine-check WHEA
records, only two informational ones from May and July; clocks at the stock 3200 base and memory at
its rated 5600.

**The Windows updates are also ruled out.** The first two crashes were 11:12:48 and 11:20:11 on
8/27. KB5122385, KB5120997 and KB5120998 did not begin installing until 13:02 that day.

**The build crashes are a SEPARATE problem from the blue screens.** Every `Internal CLR error
(0x80131506)` faults at offset `0x2278F` in `coreclr.dll` 10.0.1126.37416, across four different
executables and three days, exception `0xc0000005`. A fixed offset across four processes is a
deterministic runtime path. The argument that retired the hardware theory was mine and it was
wrong: "a compiler is deterministic" does not hold for a parallel build, and the .NET 10 runtime
has open reports of exactly this shape. Ref: peer session `aliensv-nifty-cupcake`, 2026-08-28.

**Live suspect, not yet established: Intel Extreme Tuning Utility.** `XTU3SERVICE` runs and the
kernel driver `iocbios2.sys` v7.12.0.18 is loaded. It exists to reach model specific registers and
I/O ports from ring 0, which is the only class of thing on this machine that could fault a
hypervisor. **Do not cite XtuService.exe's own crashes as evidence for it** - those carry
`0xe0434352` in `KERNELBASE.dll`, the standard wrapper for an unhandled managed exception, which
says the service has a C# bug and says nothing about the driver.

**Founder decision 2026-08-28: load test first, change nothing.** Done. Twenty minutes of verified
all-core load containing none of our code, then fourteen full rebuild-and-test rounds. **No crash.**
That is weak evidence and not a clean bill of health: four crashes across two days is about one per
six to twelve hours. Full numbers on #70.

**One correlation from that run, recorded as an observation and not a result:** 3 CLR faults in 4
attempts when builds ran at default priority on the efficiency cores, 0 in 14 at high priority on
the performance cores. One variable changed and it was not meant as a treatment.

**Three earlier load-test attempts each reported work the machine had not done**, and the lesson is
mechanical: gate on CPU-seconds per wall-second, re-check it at EVERY heartbeat rather than once at
the start, and never use blank frames as an encoder load because x264 finishes them almost
instantly. One run printed `alive=0` for six consecutive minutes and then claimed it had survived
twelve minutes of load.

## Anything launched over SSH runs on the efficiency cores only

Measured 2026-08-28 while building the load test. Windows puts processes started by a background
service into EcoQoS, so they are scheduled onto the E-cores. Under a load designed to use every
thread, processors 16-31 sat at 91-100% and processors 0-15 sat at 0-26%.

**Consequence: every build this project has run over SSH has been on the slow cores.** Setting
`PriorityClass = 'High'` on the started process opts back out - the same load then reached 28 of 32
threads busy at 4.1 GHz.

**Consequence for measurement: percentage counters disagreed with each other and with reality.**
`Win32_PerfFormattedData` read 56%, `% Processor Utility` read 71%, `% Processor Time` read 51%,
while the honest instrument - CPU-seconds consumed per wall-second, summed over the worker
processes - read 14.2 of 32. Three attempts at a load test reported a busy-looking machine that was
not busy. **Gate a load test on CPU-seconds, never on a percentage.**

## What the founder asked for last, and where it stands

> All sorts of keys. Double tap to record, triple tap to cancel, push to talk, double tap goes into
> toggle mode. **Ctrl+Windows is the new default.**

- **Gesture engine** - `HotkeyGesturePolicy`. Hold to talk, double-tap for hands-free, one tap to
  stop, three taps to cancel. Done, 21 tests, CI green.
- **Hold threshold** is the mechanism that makes all of it possible, and it is ZERO for an ordinary
  key so F8 pays nothing. It is also why hands-free works this time: a hold was never a tap, so its
  release finalises instantly. The earlier attempt made every dictation wait and was rightly reverted.
- **`Ctrl+Win` parses.** Two modifiers are a binding, one is not, any pair containing Alt is refused.
- **Wiring** - done in `f66e074`. A modifier set is translated into one synthetic key, and a timer
  runs only for a modifier binding.
- **THE DEFAULT IS STILL F8, DELIBERATELY.** Changing it needs verification against a real keyboard.

## Open defects

1. **RETRACTED 2026-08-28: "you cannot type the currently-bound key into a keybind field".** It was
   never our code. A second EnviousWispr at `C:\Users\saura\Apps\EnviousWispr-Windows-Test`
   auto-starts at logon with `"hotkey": "F8"` in its `appsettings.json` and owns F8 as a global
   hotkey, so F8 never reached any window. Confirmed with our app not running at all: a plain
   WinForms window receives F7 and does not receive F8. The stuck-flag hypothesis is also dead on
   its own terms, killed by an F9-then-F8 ordering on a fresh process. Ref: peer session, 2026-08-28.
2. **Two bindings can be set to the same gesture with no warning.** Measured: Ctrl+Alt+W accepted
   into the Recording field while it was still the Add-a-word binding, both fields reading the same
   thing, no warning and no error styling. Unfixed. Validation logic, so it needs no rig.

## Critic findings against macOS, still unbuilt

From reading `macos-source/.../PillDefinition.swift`. Recorded in `mac-parity-audit.md`:
the pill cannot offer an ACTION (macOS offers Discard and Grant); no `advisory` severity, so a setup
problem is shown as an error and blames the app; no `distress` severity; and the pill's appearance is
inferred from the message TEXT, so rewording a sentence can make it vanish.

## The peer session

`aliensv-nifty-cupcake` over Remote Control. It runs the app from a COPY of the build output so it
never locks the build tree. It has verified: keyboard safety (Ctrl+C/V/Alt+Tab unaffected), the lone
modifier binds in the keybind field, all four new icons, and Save settings pinned at a full 48 across
**eight** pages (not seven - it swept all twenty).

Outstanding for it: the recording-pill theme (needs app theme set OPPOSITE to machine theme, or the
test means nothing), and a fourteen-page light-theme pass.

## Standing hazards worth carrying forward

**A gate that passes on its first run is unproven.** Six separate gates this session could never
fail, and every one was caught by breaking something on purpose. Two of them were written the same
hour, by the author of this line, in the file whose entire subject is that class.

**Ask which set a PERSON touches, and check that one first.** A setting passes through four
vocabularies: what can be saved, what is listened for, which gesture route it takes, what the
screen can produce. Three agreeing is the most convincing possible reason not to check the fourth.

**A SWITCH ARM'S LAST ELEMENT CLOSES WITH A BRACKET, NOT A COMMA.** Three separate rounds today
were lost to a pattern anchored on a trailing comma silently skipping every help page: once in a
test, once in a rewrite that missed four arms, once in a gate regex. Every pattern that matches a
tuple's final element must accept both closers, and the tell is always the same - a count that
looks plausible because the majority case matched.

**Do not repeat a value inside the construct whose label already carries it.** Copying each page's
tag into its own switch arm broke an unrelated gate that slices an arm at the next tag literal. The
copy said nothing the case label did not already say.

**A RESTORE THAT CANNOT ACCEPT ITS MOST LIKELY INPUT SILENTLY DOES NOTHING AND REPORTS SUCCESS.**
Measured on the peer's harness 2026-08-28: it snapshots the clipboard and puts it back, but an
empty clipboard snapshots as an empty string, `Set-Clipboard` refuses an empty string, and the
restore no-opped while the surrounding code reported it had run. The session then asserted a
restore it had never performed. **Ask of every save-and-restore what its EMPTY case does**, and
verify the world afterwards rather than the return value.

**A failing test can mean the code is right.** `AShortcutBuiltOnTheBoundSetStartsNothing` called a
helper that asserts something is pending, in a scenario whose whole point is that nothing is. Read
what a red row is asserting before assuming the subject moved.
