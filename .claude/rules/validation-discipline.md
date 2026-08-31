# Validation discipline

Validation claims name the exact command, revision, machine class, and observed result.

## Required layers

- Build: every project compiles in Release on Windows.
- Unit: pure contracts, state transitions, parsing, and failure behavior.
- Deterministic parity: shared fixtures for cleanup, inverse text normalization, punctuation, and emoji.
- Integration: audio, engine adapters, provider fallbacks, storage, delivery, and cancellation.
- Runtime engines: real model packs on representative CPU and available GPU providers.
- UI automation: onboarding, settings, tray, overlay, history, and accessibility.
- Native UAT: physical hotkey, live microphone, real focus changes, and paste into representative apps.
- Release: clean-machine install, update, rollback, uninstall, data preservation, and signature checks.

The portable gate is:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/validate.ps1
```

The model-dependent gate is:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/validate.ps1 -IncludeLocalRuntime
```

Model-dependent checks may be skipped only when assets are absent, and the handoff must call that out. A
filtered green test is never described as full runtime proof. Final handoff lists the branch, pull request,
commands run, native user paths observed, and remaining risks.

## Observe it, do not assert it

User-facing behavior is proven by driving the real application in a logged-on interactive desktop session,
never from a background service session. A WinUI app launched from a service session exits before it draws,
so a green headless run is evidence about the code and no evidence at all about what the user sees.

Founder standing instruction, 2026-08-30:

- Every session has eyes and hands on the Windows development machine. Use them rather than describing what
  the change should look like.
- Behavior that unfolds over time is captured as a screen recording and attached to the pull request or
  issue. Prose about what happened is not the evidence; the recording is.
- Prefer a synthetic end-to-end run over a human checklist. Reserve the human only for what a synthetic
  sender cannot produce, such as a global gesture the operating system refuses to accept from software.

The `tools/*-uat` harnesses are the shipped vehicles for this, and `tools/app-journey-uat` covers the whole
dictation journey. Extend an existing harness before adding another.

## RULE: green-means-the-outcome-happened
Assert a terminal outcome, never the absence of one known failure, and never that a request was sent.

- A test that configures its own precondition must assert the precondition landed. A silent configuration
  failure exercises the DEFAULT and reports pass having tested nothing.
- A parameter every fixture overrides is a parameter no test checks.
- A value tested at its NOMINAL constant is tested at the one value the real system never supplies. Sweep
  the range the value actually takes, from below the minimum to above the maximum.
- Reaching a function is not reaching its branch, and the two look identical from a green run.
- Assert the subject, never a marker beside it. Ask whether the entry is written BY the thing under test.
- Read the test name as a promise, then ask what the weakest input satisfying the assertion looks like.
- A counter incremented and never asserted is an instrument nobody reads.

## RULE: measure-with-the-real-thing
Every number comes from re-running the real artifact. Never reimplement a matcher or a gate to measure it,
and never carry a prior session's number forward.

Identifiers are measurements: derive a commit id, never type or recall one. Counts of code sites are
measurements: paste the command and its output. A measurement tool FAILS CLOSED, because a half-failed
measurer that still prints a number looks like a result.

A local performance number is also a measurement of the machine. Compare same machine to same machine.

## RULE: know-what-your-tools-return-when-they-cannot-tell
Treat "could not determine" as failure, never as absence. Every trap below is a three-valued tool read by
a two-valued caller, and the third value collapses into "no".

| Shape | The third answer, collapsed to "absent" | Do instead |
|---|---|---|
| A search that skips binary or ignored files | blindness is data-dependent and silent | use an explicit tool and scope for any sweep whose emptiness you will act on |
| `git diff HEAD` | omits untracked files, so it is blind to everything the change ADDS | pair it with a list of untracked files |
| `\|\| echo "<negative>"` | substitutes a confident answer in the shape you expected | let it fail, or branch on the count |
| a truncating limit or `head -N` | truncation applies BEFORE your filter, so it looks like absence | filter at the source |
| any ancestry test for "is my work merged" | a squash merge writes a NEW commit, so the answer is always no | ask the pull request its state, then grep the merged tree for a string the change added |
| a status rollup right after a push | fires on stale rows from a superseded run | gate on stability across polls for your own commit |
| a poll loop | comparing against the value you want to ESCAPE exits on an empty probe | compare against the value you EXPECT |
| a buffered subprocess log | a crash loses the whole buffer, so the log reads as an earlier failure | run it unbuffered |
| a captured value read from a file | some tools print their FAILURE to stdout, so nonempty is not success | assert the input exists; branch on content, never on emptiness |
| a capability reached by a DEFAULTED argument | absence has no call-site token, so no search finds it | make the default fail closed and read the callers off the first run |
| a pipeline | returns its LAST command's status | assign first, then branch |
| a cross-process visibility check | measures a pair; a probe process is not the signed app | make the SUBJECT log the outcome on every path |

Before believing any sweep's silence, run the pattern against a case you KNOW is present.

Two further traps outrank all of these, because they return a well-formed value that is simply about the
wrong thing, and no guard shape catches them:

- A default that is PLAUSIBLE rather than a sentinel. It propagates and surfaces elsewhere as a mystery.
- An unrecognised input falling into the DEFAULT case. Declining to classify is an action. Ask where an
  unhandled input ENDS UP, not whether you handled it.

## RULE: three-ways-a-correct-tool-still-answers-wrongly
- **Wrong scope.** The pattern is right and the set is incomplete. Ask what is NOT in your scope and who
  decided that. A wrong pattern looks odd; a wrong scope looks clean and complete.
- **Wrong authority.** A source authoritative inside a scope nobody stated.
- **Accretion.** The same question answered differently in two places in one file. Free detector: before
  trusting a scope, search the file for its own other answers.

Two free tells. An answer IDENTICAL across targets with no reason to agree is a property of the
instrument. And existence is not function: an attribute present but constant, a name present but never
called, a list present but capped.

## RULE: an-expectation-built-with-the-thing-under-test-cannot-fail
When asserting that a transformation PRESERVES something, build the expected value from a literal. If your
expected value passes through any of the machinery the subject passes through, the test can only prove the
two agree, never that either is right.

## RULE: a-partial-check-looks-like-a-complete-one
Enumerate what "the outcome" CONTAINS before writing the comparison. When a second finding lands on the
same check, stop adding clauses and assert the whole property. The oracle must be something the subject
did not write.

## RULE: a-single-threaded-test-cannot-prove-atomicity
A contract test passes identically against an atomic primitive and a check-then-act race. To test
atomicity, race it: many concurrent attempts at one destination, asserting exactly one winner and zero
corrupt results. Prefer a primitive that fails atomically over detect-and-undo.

## RULE: a-test-seam-on-a-guard-is-a-bypass
A guard's own parameters are constants. Making one settable "for testing" is an unlogged escape anyone can
reach. Test by patching a private copy, never by giving the live guard a knob. Prove a seam's removal with
the exploit, not with the suite.

## RULE: knowing-a-class-does-not-protect-you-inside-the-file-you-are-editing
After naming a defect class, sweep for it in what you are writing right now. Naming a class raises your
confidence about it, which is the opposite of what should happen while you are still typing. The interval
is minutes, not months.

The test for a proposed defence: would it still work if the author had never read this rule? If not, it is
a reminder, not a control.
