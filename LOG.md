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

## 2026-09-03 (morning)

### A feature that had never once run, and the fallback that hid it

The streaming head start transcribes finished parts of a recording while somebody
is still speaking, so releasing the key finishes work already mostly done. It had
never worked. Every recording abandoned it about half a second in, and no log this
project has ever written contained a committed segment.

The cause was one cast. The loop wants the whole recording rather than a rolling
window - a commit is a range measured from the start, so a window would make those
indices mean something different on every poll - and it says so by asking for the
largest span there is. Both implementations of that request turned the span into a
sample count through a checked conversion, and the largest span times the sample
rate does not fit. It threw on the first poll, every time, since the day it shipped.
A year-long span overflowed too, so this was never one magic value mishandled.

**It was invisible because giving up is correct.** Any failure abandons the head
start and the release transcribes the whole take. Nothing on screen was ever wrong,
nothing was ever lost, and the fallback is good enough that nobody noticed for as
long as the feature has existed. The wait after release halved once it worked:
444-445 ms to 201-217 ms, three runs each way.

**Nothing could have caught it.** The journey harness holds a recording for a
fraction of the 500 ms poll interval, so no run it has ever made reached the first
poll. A guard that cannot reach the code it guards is not a guard, and its greenness
says nothing. It now holds one long enough and asserts the outcome AND the absence
together - committed and used must appear, abandoned must not - because either half
alone passes against the defect.

### A success word on a truncated run, and the fix that failed for the right reason

`dotnet test` prints `Passed!` when the test host dies mid-run, beside a total for
whatever finished. Five times in one session: totals of 1064, 1049, 1064 and 1069
against a complete 1127, each under that word. Only the exit code disagreed.

The gate now refuses an aborted run on its output text and compares what ran against
what the assembly contains, discovered rather than written down - one entry per
theory case, so it matches the run's own total exactly. A floor would not do, because
a floor still lets tests vanish quietly beneath it.

**And the first version of that fix was wrong in a way worth keeping.** It captured
the runner's output with a stderr merge under a preference that makes errors fatal,
so a native command writing anything to the error stream raised a terminating error
at the moment of capture - before any of the new checks could run. A crashing host
therefore produced a runtime error pointing at the capture instead of the sentence
written for exactly that case. It failed for the right reason with the wrong
explanation, and the explanation was the entire value of the change.

**The same trap is documented fifty lines away in that file**, about the Python
probe, and knowing that did not prevent reintroducing it. Naming a defect class
raises confidence about it, which is the opposite of what should happen while still
typing.

### Two numbers measured instead of argued, and one route closed

Inverse text normalisation was filed as load-sensitive after a single CI timeout. It
is deterministic and reachable: four hundred spoken numbers take about half a second,
eight hundred about a second and a half, and sixteen hundred fail every time on a
quiet machine. **The expensive passage comes back completely unchanged** - the whole
cost is patterns scanning and failing.

Atomic grouping is one of the three routes the issue proposes and the cheapest, so it
went first. It is fifteen per cent faster at four hundred words and **moves the
failure point not at all**. It was byte-for-byte correct against a 3756-row pinned
oracle, so it was shippable, and it was thrown away rather than banked as progress: a
constant factor that leaves the cliff where it was does not earn new complexity in the
component that is meant to be the reliability floor.

**A wrong instrument nearly produced a wrong number.** The first readings threw where
warm ones complete in about a second and a half; the first call in a process pays for
compilation and can cross the per-call guard on its own. Any measurement here must
discard the first run.

### A probe that asked about the wrong library, and could not be caught by running it

Live Preview runs one speech engine and decided whether to use the graphics card by
checking whether a DIFFERENT engine's runtime files were present. A machine with a
working card and none of those files was put on the processor for a reason that had
nothing to do with the thing running there.

**Both probes are true on the development machine**, so the fix changes nothing
locally and no amount of running it would have shown anything. The machine where the
two disagree is the test. Worth keeping as a shape: a condition can be wrong in a way
that is invisible everywhere except the configuration nobody has.

### Looking at copy changed the copy

The spoken-emoji switch never said that the word "emoji" is required, so turning it
on and saying a phrase does nothing and reads as broken - a tester with the source
open reached exactly that conclusion. The switch now carries the rule and an example.

The first version ended the example with the glyph followed by a full stop, and the
emoji's own advance width leaves a visible gap there, so it rendered as though a
space had been typed. The example now sits mid-sentence where that width falls
naturally. **The glyph renders in colour rather than as the hollow box a missing
codepoint gives, and nothing in this repository can decide that.** It was looked at,
at real density, on the page where it ships.

### A finding that reshaped an issue rather than closing it

The recording-signal gate is also the update gate. Three call sites take the same
primitive with a zero timeout, and only one of them is wrong: dropping a recording
signal is the defect, refusing an update while dictating is correct. The update path
holds that gate across a full download.

So "one ordered queue for recording signals", as proposed, would let a record-key
press queue behind a download and start a recording on its own minutes later -
trading one wrong behaviour for another. **The state machine is a prerequisite for
the queue rather than a sibling of it**, which reverses the order the issue implies.
Recorded and left alone: what should happen when somebody presses record during an
update is a product decision, and this is the code where being wrong costs somebody
their keyboard or leaves a microphone open.

## 2026-09-03 (overnight)

### The most convincing parity gap this project has produced, and it was wrong

Whisper sometimes returns sentences nobody said. macOS solves it by decoding only
the parts of a recording where somebody was speaking, and its source carries the
benchmark that settled the design: trailing phantom-phrase hallucination on 3 of
107 clips against 14 of 107 for the approach it replaced. Windows did none of it
and handed the decoder the whole buffer. On real dictations from this project's
own machine, that buffer is about 8.6 seconds holding about 3.5 seconds of speech,
so roughly 55% of what the decoder was asked to transcribe was never speech.

Every step of that is true, and the port makes the output worse.

Eleven archived dictations, the same sentence each, decoded five ways. The decoder
was proved deterministic first — an identical re-run returned all eleven
transcripts byte for byte — so the differences below are the change rather than
noise.

| What was decoded | Changed | Direction |
|---|---|---|
| Trimmed to the detected speech span | 6 of 11 | 2 better, 3 worse, 1 both |
| Same span via a seek rather than a cut | 5 of 11 | 2 better, 3 worse |
| Whole capture plus a half-second tail pad | 0 of 11 | no effect |
| Capture cut at the last word, no pad | 2 of 11 | 2 better, 0 worse |
| Capture cut at the last word, with pad | 2 of 11 | identical to above |

Gating cost capitalisation and words: `Testing` became `testing` on two takes, and
one turned the product's own name into `EnVyUs whisper`. Nothing downstream puts a
lowered first word back — swept the deterministic pipeline and the delivery path,
there is no sentence-case restoration anywhere — so it reaches the document lowered.

Seeking is not a way out. It was tried precisely because it is closer to what the
other platform does than cutting an array is; it agreed with the cut on 8 of the
11 and carried the same regressions. The mechanism is not the variable.

**The tail pad's own reason does not reproduce here either.** The other platform
says abruptly-ending audio loses its last one to three words. Every archived take
was recorded with two deliberate seconds of trailing silence, so the pad had
nothing to do on them — which is why each recording was CUT at its last word to
build the condition being described. Cut that way, nothing was lost, and the pad
changed nothing.

**What was measured is the cost and only the cost.** The recordings that actually
fabricated had rolled off the twenty-file archive bound and no longer existed, so
the benefit side is unmeasured. That is exactly why it must not ship: a change that
demonstrably harms the ordinary path cannot be adopted on an unmeasured benefit.

The lesson outlives the feature. This file already records that parity work fails
toward building something that was already present. This is the same bias from a
new direction — toward building something genuinely absent that still should not be
built. **A capability being present on the other platform, absent here, and backed
by a benchmark there is not evidence it helps here.** The runtimes differ, and only
running it here settles it.

### A surface was being filled by searching for words in a sentence

The panel that reports which speech engine is loaded was updated by testing the
status text for `ready`, `model is not installed`, `transcription is unavailable`
and `worker could not start` — the same shape as the pill-appearance mapping that
was deleted for one surface over.

Four unrelated messages reached it, counted rather than assumed: a Windows resume,
a finished Escape Recovery, and both cleanup-provider health lines. Each overwrote
the engine line with a sentence about something else.

It fails silently the other way too, and that direction is what forced the fix
rather than a follow-up. A new sentence about a failed graphics card contains none
of the four phrases, so the panel would have kept stale text while the notice said
the card had failed. A status now carries the answer from its call site.

Found by trying to add a sentence, not by looking for a defect. The general shape:
**when a mechanism selects on text, adding text is what exposes it**, and the
adding is more likely than the auditing.

### Live preview was not slow, it was impossible

The loop waited a full interval and only then started work, so the period was the
interval plus the cost of a pass rather than the larger of the two. On the measured
take the first update was predicted at 5419 ms and observed at 5421 ms — two
milliseconds apart, so nothing was intermittent — and the second was due after the
recording had already ended.

Measured on the machine, three runs each way: one update per take before, two
after, on a five-second hold. The harness could not have caught it: it reported
live preview as a yes-or-no, which stayed true for the whole period when the
feature showed one frozen fragment and stopped. **Existence is not function, and a
boolean is the shape that hides the difference.** It now reports the count.

A number was wrong on the way: the first simulation test asserted three updates
because that is what the fix felt like. Walking it gives two. Writing the test as a
walk over both schemes rather than an assertion of a remembered figure is what made
the wrong number fail instead of ship.

### The gate printed a success word on a truncated run

Three times in one session the test host crashed during teardown and `dotnet test`
printed `Passed!` beside a count roughly fifty tests short of a complete run. Only
the exit code disagreed, and the gate reads the exit code, so it failed correctly.

Recorded rather than dismissed as runner noise because two of the three aborts
stopped at the identical count, and because a summary that says `Passed!` while
fifty tests did not run is one careless parse away from a false green. Filed with
the proposed fix: fail on the abort text as well as the exit code, and assert the
suite size.

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
