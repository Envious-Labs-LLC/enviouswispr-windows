# Log

What changed in this project, and why, newest first.

This is the reasoning behind the work, not a changelog of releases. It records
decisions, defects worth remembering, and the measurements that settled them, so
a reader arriving later does not have to rediscover them. Commit messages carry
the same reasoning at the level of one change; this carries it at the level of a
day.

## Format

One entry per working session. Each entry names what changed, what it cost, and
what was learned that outlives the change. Findings that were caught and fixed
inside a session are not reportable on their own; a gate doing its job is the
expected case. What earns a line here is a decision, a defect that survived to be
found by something other than the person who wrote it, or a fact that would cost
somebody a day to rediscover.

No machine paths, no personal data, no credentials. This file is public.

---

## 2026-08-30 (evening)

### The app knew, and did not say

Five separate defects this evening were one defect wearing different clothes: a
thing that reported a fact it had never checked.

The deterministic pipeline returns a receipt per cleanup stage - which stage,
completed or skipped, whether it changed the text, what it cost - and the app
logged one summary line and binned all five. So "do custom words work" could not
be answered from a dictation: 23 ms looks identical whether five stages ran or all
five were skipped. Live Preview refused to build its engine and returned silently
from two places, so switching it on produced a toggle that stayed on, no preview,
and no trace. The streaming head start's catch bound its exception, never read it,
and asserted the same failure category 59 times running.

Fixing that surfaced three more of the same shape in the tools built to prove the
fix. A disclosure gate that searched a whole file for a word, which a passing
mention satisfies. An allowlist that could only ever grow. A subset assertion
checking eight field names while eleven were being written, passing the whole
time. Each is now closed by a check rather than by care: reflection compares the
two record shapes both ways, the data dictionary's table rows are parsed and
compared as a set in both directions, and every remaining allowlist is derived
from the type it describes.

**The privacy gate that proves no dictated content crosses the network was built
by the validation script and run by nothing, here or in CI. It had never
executed.** It runs in the default lane now.

### Whisper fabricates, and macOS solved it a year ago

Whisper invented whole sentences on live microphone input: six takes, six
fabrications, in one case eleven appended words in a register the speaker never
used. The same engine scores WER 0 on the clean English and French file fixtures
already in this repository.

Three findings, in the order they were needed.

**The provider is not the variable.** The same six recordings fabricate
identically on CUDA and CPU, three of them word for word. So a GPU change fixes
nothing about output, and ten clean controlled takes were clean because of the
recording conditions.

**Not silence - real non-speech audio.** One file produced the same leading words
across three independent decodings by two engines on two providers. Something was
audible before the speech and it peaked louder than the speech did. Given that
input, Parakeet returns a fragment and Whisper returns a grammatical sentence. A
person notices the first and may not notice the second.

**macOS already ships the answer, and the catalog could not say so.** The failure
is named in the macOS source as trailing phantom-phrase hallucination, with a
107-clip benchmark behind the design: VAD-derived clip boundaries so only speech
is decoded, chunking above 30 s, and 500 ms of trailing silence padding without
which abruptly-ending audio loses its last words. Windows has none of the three
and sends the whole buffer, silence and noise included, to the decoder.

The cross-platform catalog was queried first, as the project brain requires. Its
`hallucination-protection` rows describe polish-output guards on every platform
and say nothing about ASR-level suppression, so the query answered truthfully
about a different mechanism and read as "nothing exists". Most of a day went into
researching decoder thresholds from first principles. The order is now written
down: catalog, then `catalog_gap`, then grep the macOS source, then research.

### A fallback working is how a 100x regression hides

Whisper took 11 seconds to transcribe a 6.6 second dictation. The twelve CUDA and
cuDNN runtime libraries were absent from the development machine - the wheels were
installed with their headers and not one runtime DLL, consistent with a disk
cleanup. The engine looked for its GPU dependencies, correctly did not find them,
correctly fell back, and said nothing.

Restoring them: 11,015-11,712 ms becomes 75-203 ms on identical files, roughly
110x to 150x, with word error rate unchanged. Whisper on GPU is now faster than
Parakeet on CPU. In the app on a live microphone, a whole dictation went from
11,496 ms to 471 ms.

No code changed. The defect that remains is that none of it was reported.

### Recording evidence you cannot account for is not evidence

Six recordings that reproduced the fabrication were discarded rather than promoted
to fixtures. Transcribing them with the faithful engine, to answer a privacy
question mechanically rather than by asking somebody to listen, instead found
content in three of them that nobody could identify. A fixture nobody can explain
is a confound in every measurement that uses it, and this repository is public.

They were replaced by ten deliberately controlled takes - quiet room verified
before starting, two seconds of silence at each end, levels within 0.9 dB across
the set - of which nine pass admission: the faithful engine must return exactly
the spoken sentence and nothing else. If it hears only the sentence, the file
contains only the sentence, and anything extra the other engine produces is its
own.

## 2026-08-30

### Main finally holds the product

`main` had never contained the application. It held seven documentation commits,
and the whole product lived on a stack of thirty branches, each merging into the
one below it, none reaching `main`. Only one pull request had ever targeted
`main`, from the very first day.

All 323 commits landed on `main` in one merge. Twenty-nine superseded pull
requests were closed after checking, commit by commit, that `main` already
contained each one. Thirty-three branches were deleted. Two remain: `main`, and
`codex/phase6-cpu-incremental`, kept because rejecting that spike was a decision
somebody made and the branch is the evidence.

Work now happens in a worktree on a branch, merges to `main`, and the worktree is
removed. `main` stays buildable and is never edited in place.

### Three audio meters were dead, for three unrelated reasons

Every level meter in the app read flat while dictation transcribed perfectly.
Three separate faults, each found by a different instrument, each needing a
different fix.

**The settings meter starved the render pass.** Capture reports a level per audio
buffer, about two hundred times a second, and every one posted its own callback
to the UI thread. Layout and render run on that same queue. Every callback ran,
every property assignment was accepted on the correct live element, and no frame
was ever produced. The tell was that the one thing on that page written *after*
the flood stopped, the verdict sentence, was the only thing that ever appeared.
It now posts one frame per fifty milliseconds and keeps the loudest level of each
frame rather than the first, because a rate limit alone chooses at random with
respect to loudness and loses the attack of every consonant.

**The recording pill had two clocks in series at the same period.** Its timer was
set to the meter's sample interval, and the level history has its own gate
rejecting anything early. Windows quantises timer callbacks to about 15.6 ms, so
a fifty millisecond timer fires at 46.9, reliably three milliseconds short, every
tick. The rail drew its first sample and never again. The timer now polls at half
the interval so the history's gate is the single pacer.

**The capture published a level for empty packets.** The recorder delivers a
zero-length buffer on roughly half its callbacks, each within a millisecond of a
real one, and each was published as a measurement of silence over the top of a
true reading. Anything reading the latest level read zero on virtually every
look. Measured across ten consecutive packets of a real dictation: 516, 0, 640,
0, 640, 0, 640, 0, 640, 0 bytes, all flagged not-silent, with the full packets
carrying a coherent rising attack. An empty buffer is not a measurement, and now
returns before anything else happens.

**What is worth carrying.** Two of these were invisible to a camera and one was
invisible to a reviewer. And the settings meter was visibly working while still
under-reading by roughly half; that was only discovered because an unrelated fix
upstream moved a number somebody was still measuring. Continuing to measure after
something starts working is what caught it.

### The product stopped accusing itself

Home carried a warning reading "did not close properly last time" and, past one
occurrence, a running count of how many times. On one machine that count reached
nineteen, almost all of it a build script stopping the app to release a file
lock.

Nothing in the app can tell a fault from a closed laptop, a Restart chosen from
the Start menu, a log off, or Task Manager. All four leave exactly the trace a
crash leaves, which is the absence of a clean-exit flag. So the tally was not
evidence of anything, and it was the headline of a first-screen warning.

Deleting it outright was also wrong, and that took a reviewer to catch. Recovery
text is written only *after* transcription completes, so a stop during a
dictation leaves nothing to restore and reads exactly like an idle restart. That
is the one case where somebody must be told, because their words are gone.

The run state now records whether a dictation was in flight, written at every
place a dictation can end: the push-to-talk handler, the recording watchdog, and
Windows lock or suspend recovery. Writing it in only the first left the flag
stuck true after the other two, which would have rebuilt the same false alarm in
a new shape. The interrupted-run count no longer travels on the public start
result, so putting it back on screen is a compile error rather than a review
finding.

### One build, one copy, one receipt

`scripts/one-build.ps1` is now the only way the app is built and launched on a
test machine. It stops any running instance first, because a locked DLL makes the
build skip its copy and report success anyway; builds to one place; mirrors to
one launch folder; and refuses to launch unless the two hash identically.

This exists because six build folders had accumulated on one machine. One of them
auto-started at logon and installed a global hook that swallowed every F8 press,
which produced a false defect report two days earlier when a keybind field looked
broken and was not. Duplicate builds do not merely waste disk. They invalidate
evidence, and no amount of care in the measurement survives not knowing which
binary produced it.

### Method notes worth keeping

Recorded because each of these cost a round, and each was caught rather than
shipped.

- **A measurement that reads perfectly constant is more likely aimed wrong than
  the thing being measured being frozen.** Three separate measurement passes
  landed on a heading, a moved button, and a desktop wallpaper before this became
  a rule. Assert the target is alive and locate it fresh before every run.
- **An instrument that reads the last value cannot distinguish a working meter
  from a dead one**, because the last sample of any take is silence. Record the
  maximum, and read post-layout values on the following frame rather than
  immediately after assignment.
- **A test that cannot fail is not a test.** Mutating a fix back and re-running
  is cheap and turns a test somebody believes in into one they have watched work.
  Two of four tests written in one sitting here passed on the broken code.
- **A green rerun is how a real latent boundary gets recorded as runner noise.**
  A CI timeout that passes on retry is still worth naming.
