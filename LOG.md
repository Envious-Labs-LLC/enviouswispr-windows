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

## 2026-09-24 / 25: a sprint of macOS features, and what review found underneath them

Eight pull requests merged: #205 (#163), #208 and #209 (#206 parts 1 and 2), #210 (UK spelling), #212 (Transcribe a
File, #211), #214 (the Ollama model catalogue, #213) and #215 (the live preview model bench, #127). Two more are
finished, green and rebased, and waiting only on a native keyboard check: #206 part 3 (the last-dictation shortcuts)
and #207. Injected input did not reach Windows for most of the sprint - a system service held the foreground, so
`SendInput` succeeded and the idle timer never moved - which is why those two wait; UI Automation, window messages
and the app's journey route carried every other native check.

### Decisions

- **A file job holds the session for the whole file, and a record press during it is refused on the pill**
  ("Transcribing a file. Please wait."). This is macOS's founder decision of 2026-09-04, adopted after the first
  design - hold the engine one piece at a time so a dictation could go first - was shown by review to drop the
  press silently instead. Never queued: a queued press opens the microphone after the key is let go.
- **The app never starts Ollama.** Ollama's Windows desktop app applies its own saved network-exposure setting to
  the server it starts, which the app cannot read reliably, and it can stop a server another tool started. The page
  says how to start it instead. Recorded as a deliberate difference in `mac-parity-audit.md`.
- **Live preview is chosen per tier by measurement, and the first numbers are in** (#127). On a free card every
  candidate is far inside the 2.5 s cadence; on the processor the shipped Whisper Small runs within about 12% of
  it, and Parakeet int8 costs 40 to 256 ms at near-Small accuracy on whole clips. Candidate tiers are for the
  founder; the laptop processor, longer windows and partial-preview accuracy are the open measurements.

### Defects that survived to be found by something else

- **A session hold was granted while a recording was live.** The press hands the session gate back once capture has
  started, so an update check, a last-dictation reuse or a file job could take the session mid-recording, and the
  key's release then waited behind it with the microphone open. It predates this sprint; review of Transcribe a
  File found it. `TryHold` now refuses while a recording is in flight, and every hold names its holder.
- **The Ollama client followed redirects.** The loopback check was on the first URL only, so a redirect could take a
  request anywhere. Discovery and polish had carried this since they were written; review of the catalogue found it.
- **The live-region gate read one file of a window.** A status line written from `MainWindow.TranscribeFile.cs` was
  invisible to it, so a screen reader was never told of progress and the gate passed. It now reads every partial
  file of the class.
- **A late answer applied in the context it was not asked in** - the one class that took four review rounds on the
  Ollama block: a refresh drawn after a newer one, a removal sent to the endpoint the page was loading rather than
  the one its rows came from, a picker listing applied after the provider changed. Fixed as a class once the second
  round showed its shape: the window draws current state, a change carries the list the person clicked in, the
  picker applies a listing only in its own context.
- **The benchmark's first card figures measured someone else's work.** Another process held the card at 100%, and
  Whisper Small read 3.5 s a pass on an RTX 4090. The bench now refuses a busy card by name.

### Facts that would cost a day

- **The Win32 Open dialog a WinUI app shows lives in another process**, so a search for it by the app's process id
  finds nothing; it is a child of the app's window in the UI Automation tree. Its file-name box is exposed as a
  bare `Pane` with no Value pattern: set it with `WM_SETTEXT` on the `Edit` inside `ComboBoxEx32` id 1148, and read
  it back with `WM_GETTEXTLENGTH`, not `GetWindowTextLength`, which returns 0 across processes. Open is `BM_CLICK` on
  control id 1.
- **A `TextBlock` with `AutomationProperties.Name` reports that name, not its text**, to UI Automation - a transcript
  labelled "Transcript" read as ten characters. Read the text through the Text pattern.
- **Under the Windows display driver model, `nvidia-smi` reports no per-process video memory** (N/A); a card figure
  for one process is an estimate from the card's total.

## 2026-09-21: plan 2, from 6.5 to 8.5

### The reviewer's own plan, landed, and its own regrade

The regrade that closed the refactor gave the architecture 6.5 and wrote the route to 8.5: seven
conditions with weights, sixteen required steps. Every step landed today as one pull request,
reviewed by the same reviewer in adversarial rounds until approved, then merged - #183 to #202
- and the reviewer then regraded merged main against its own table: **8.5, every condition at its
full weight, ARCHITECTURE SOUND.**

**Where the points were.** Session ownership (0.60): the decisions a dictation needs - admission,
the background order, the deadline, recovery, the finalisation, and now the session's own disposal
sequence - are the executor's in Pipeline, and every effect and callback the shell supplies is
inventoried in `pipeline.md` from its construction site to its body. Bounded shutdown (0.50): a
quiescence protocol with one budget, nothing disposed beside a user of it, the host ended when
something will not finish. Production wiring tested (0.35): the compositions the app runs are the
ones the tests drive, and now the exit itself runs as one composition over the production session.
Presentation (0.25), delivery identity (0.15), contracts (0.10), names (0.05).

**A per-PR approval is not a regrade.** The first formal regrade on merged main came back at 8.3
after every PR had been approved at a cumulative 8.5. Reading the conditions afresh found three
things a per-PR review had assumed: the ordinary release discarded the preview's stop result, so a
worker that refused to stop could still hold its resource when the final transcription began; no
test executed the whole exit as one composition; three lifecycle comments contradicted their code.
The reviewer was right on all three. Closing the first took three rounds of its own, each catching
a real defect in the new abort path - transcribing beside a worker nobody had seen leave, an
aborted runtime that was terminal by design and so silently ended preview for the rest of the run,
and a remaining budget read twice between a check and the call that used it. **When a grade is the
goal, the grade is the regrade on main, and it should be run before claiming the number.**

**The review's alternate-caller audit found what the ownership audit could not.** Once every
supplied effect was inventoried, the reviewer followed every other caller of what the exit disposes:
Quick Add reading through the delivery adapter with no lease; mishearing suggestions inside the
polish provider; the launch's tail and a model delivery building engines after an exit had begun;
a Windows session-ending write nobody joined; queued window callbacks running after their lease
had ended. Six rounds, one or two borrowers each, each real. What ended it was not a scope
statement this time but the accounting closing: everything the exit disposes now has a lease, a
tracked task or a gate the exit joins first, and the session is asked to shut down only behind a
finished drain.

**Two defects the reviewer caught in code written that day.** A launch cancelled by the exit policy
propagated its cancellation into an `async void` entry point; a model delivery that refused
synchronously cleared its own tracking before the assignment stored it, so every later download
was refused. Both would have shipped without the second reader.

**Cost.** One long session; roughly forty reviewer rounds across twenty PRs; the native journeys run
on every build that touched the exit or the delivery path (seven verdicts each time, recorded with
the build they were taken on). The reviewer did not write and did not merge.

**What the reviewer names beyond 8.5, ranked.** The shell's real `LifetimeParts()` assembly made
executable through the seam the tests use, with native forced-refusal and escalation journeys
recorded; the installed-model repository boundary (the plan's optional steps 17-19, not attempted);
and engine configuration, provisioning and the larger window features moved into owners of their
own. And what is still the founder's to see: a lock and a sleep during a recording, and a preview
worker that refuses to stop - neither can be raised by the harness on this machine.

---

## 2026-09-20 (day): the refactor plan, finished

### Twenty-two steps, one author, one reviewer, thirty-odd rounds

The plan from the overnight review is landed in full. The main lane (session coordinator through
interruption and shutdown, steps 1-11) took the morning and afternoon; the presentation lane
(steps 15-20) and the model-delivery lane (21-22) took the evening. Every step was one pull request,
written here, reviewed by the second author in an adversarial round or several, merged once its
gates and the review cleared. Twelve PRs today, #170 to #180, on top of the eight before.

**What the reviewer found that the tests did not, and the shape of it.** Step 11 took five rounds.
Each caught something real about shutdown: a hold outliving disposal; a command whose gate wait
ended just before admission closed and ran anyway; a status notification that could land after the
shell had torn down what it touched, and a stop that reported clean when one had thrown; a command
torn down beside the shutdown faulting its caller twice on the way out; a reassessment that
extended the shutdown budget to nearly three intervals. None of these was a test failure. Every one
was an interleaving that a test with one actor cannot produce and a reviewer reading the two lock
sections side by side can. The pattern that held across all of them: **commit under one lock, after
owning the resource, and decide torn-down by state rather than by exception type.**

**A source gate is a chase with no end, and the end has to be stated.** Step 15 moved the settings
transaction out of the window and replaced a Roslyn assertion that read three assignments off the
window's source with a behaviour test on the new presenter. The reviewer then found, over six
rounds, six ways to rewrite the window so the choice on screen was not the one saved while every
gate stayed green: a templated group counted as one card; a substring that mentions a control and
ignores it; an assignment, a deconstruction, a ref alias, a sibling declaration between the
snapshot and its hand-over; a block that does work before the call; an object initializer; a lambda
nothing invokes. Each was closed, and each closure invited the next. It ended when the pull request
said where the gate stops - it pins the shape of one method's hand-over and does not prove the
method runs - and the reviewer approved the scope rather than the absence of further shapes. The
lesson is not that the gate was wrong to tighten; it is that a gate over source needs its scope
written down, or the review becomes a proof that no gate over source is complete.

**The threading rule that a plan line can carry.** "Never pass control-reading closures into
background transactions" was one line in the plan. The reviewer found the one place the window
broke it - a strictness picker read inside the writer's gate, on whatever thread the wait ended on
- which had been there before the refactor began. It is the kind of defect that never fires in a
test that drives a window one call at a time, and it was found by reading the plan's rule against
every lambda handed to the presenter.

**Model delivery separated in two moves with the bodies untouched.** The transport (one attempt
against one source) and the downloader (sources, attempts, delays, what a failure means, shards
first) came out of the store byte-for-byte, with the store keeping the lock, admission and
activation. The reviewer's contribution there was to the tests: a cancel test that could pass
without the cancellation reaching the read; an oversized-response test that could pass while the
whole response was read; a constructor guard that quietly refused inputs the old code accepted. The
tests now say what they prove.

**Cost.** One session. Around thirty reviewer rounds across twelve PRs; the reviewer does not merge
and did not write, which kept one author on the window at a time, as the plan asked.

**What is still the founder's to see.** Every settings page the presentation lane touched needs a
hand on it once: a Save, a theme click and a restart, a provider key and a model refresh, the
microphone test with a dictation pressed during it, delete/keep/clear on History and the
recovery-copy deletion, a word and a snippet added and removed, a word-list paste with a conflict
offer. And the two interruptions no harness on this machine can raise: a lock and a sleep during a
recording.

---

## 2026-09-19 / 20 (overnight)

### An adversarial review of the architecture, and the plan it produced

An independent reviewer was asked to find what is wrong with the architecture - god objects, a
monolith under a folder structure, code that reads as generated without judgement - and to prove
each claim from the files. It read 62 of them and returned: the library boundaries are real and
machine-enforced, and the critical workflow lives in the two classes least able to be tested. The
application object held fourteen jobs across 4,100 lines; the settings window twelve across 4,800.

**The most useful finding was one the project already knew.** The one blocking item - a key-up that
arrives while the press is still starting is thrown away by a zero-timeout gate probe - was already
filed, and its structural cause already diagnosed on 2026-09-03. A second, unprompted diagnosis
reaching the same conclusion is worth more than a new one.

**The second most useful contradicted the project brain.** `CLAUDE.md` said, under "know this cold",
that Windows delivers text through two routes. The code had three since 2026-08-26: a direct write
through UI Automation runs first. A contract that omits a mutation path hides exactly the path that
most needs validating. Corrected in both files that carried it.

The same reviewer, resumed rather than re-briefed, then wrote a 22-step plan under the project's own
constraints. The reviewer is now the gate for every step: it reviews, it does not write. That
division was set by the founder mid-session after two steps had been implemented from briefs; those
two stand, reviewed, and the third such draft was taken over rather than discarded.

### Eight steps landed, and what each review round caught

Every step went through the reviewer, most through two rounds, one through three. What the rounds
caught is the record, because each was a thing the author had convinced themself of:

- A press queued behind an update download would have opened the microphone minutes after the
  finger left the key. A press now takes the gate at admission or is refused, as the old probe did.
- The old hook captured the target window synchronously inside the key callback; a queue adds a
  thread hop after that point, and a person can change windows inside it. The author had waved this
  off as equivalent to the old hop; it was not. The press now captures its target at admission.
- A stage's own cancellation - neither the caller's nor the deadline's - aborted the whole text
  pipeline instead of falling back. Pre-existing, and preserved by a test row.
- The processing deadline had moved from after failure recovery to before it. Lock/suspend recovery
  cancels that deadline from its own callback and must still find it during the recovery.
- The polish vocabulary was read from live settings at the provider call, inside the resource lease;
  the extraction had captured it when the recording ended. A word taught during transcription used
  to reach the next polish. It does again.
- Two clock reads collapsed into one moves a retention decision that lands on an expiry boundary.

None of these was visible from a green suite. Every one was found by an equivalence audit of the old
body against the new, line by line, which is the review shape that earns its cost on an extraction.

### main went red, and the command that let it

A test asserted that a cleanup stage with a 50 ms deadline completed. On a cold hosted runner the
first pass through that stage pays its compilation and the deadline expires. The test was about which
receipts reach which emission and had no business asserting timing.

That is the small part. The pull request's final commit had a red check and merged anyway, because the
merge command was `gh pr checks <n> --watch | tail -1 && gh pr merge <n>` - a pipeline returns its
last command's status, so the red watch never broke the chain. `validation-discipline.md` lists that
trap by name, and naming a class raises confidence about it, which is the opposite of what should
happen while still typing. The rule now carries the exact command shape with no pipe in it.

Earlier the same night, `--auto` had merged a pull request instantly while its last run was still in
progress, because the rule said to pass it and the repository has no required check. Two merge
defects in one session, both in the mechanism that was supposed to be the guard, both fixed in the
rule rather than in memory.

### Instruments that printed the wrong answer

Three, all caught before they mattered, all worth writing down because each printed a confident
result: a .NET file write with a relative path from the PowerShell tool landed under the process
directory rather than the shell's, so a "deliberate violation" was never applied and the control
passed; `dotnet test --no-build` after restoring a violated source ran the violated binary and
reported ten consecutive failures on a correct tree; and a quick tap that lands after the press has
already finished looks identical, from outside the process, to one that overlapped it - so the app
now writes a line when a signal actually waited, and the journey demands that line or declares itself
invalid rather than green.

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
