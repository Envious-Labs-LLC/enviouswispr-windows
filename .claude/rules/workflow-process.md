# Workflow process

How a change moves from idea to merged.

## RULE: the-shape
1. **Prior context.** Refresh the issue, its comments, open pull requests, the branch, and `main`. Read
   `LOG.md`. Classify the task as code, tracker cleanup, already landed, or experiment. Say what you found.
2. **Intent check.** State the user benefit in a person's words, and the scope. For a new coordinator or a
   new project reference, say `This lives on X because Y; the alternative was Z but <trade-off>`.
   **Stop for approval.**
3. **Prior art.** Query the catalog, check `catalog_gap`, then read the macOS source. Record what you
   found, what mechanism each hit covers, and what you are deliberately doing differently.
4. **Plan** for anything larger than a single isolated change. Grounding precedes design; never design
   first and justify after.
5. **Review to clean** before writing code (see below). Self-audit privacy, cancellation, failure
   fallback, and public-repository safety first.
6. **Build.** Larger work is built in chunks, each reviewed against its own diff before the next starts.
7. **Validate.** The portable gate, plus the observed Windows user path.
8. **Ship.** Branch, pull request, review gate, merge, verify `main`.

## RULE: port-the-proof-before-simplifying-it
When replacing proven WPF behaviour, first port its outcome and its causal comments unchanged. Simplify
only after reproducing and falsifying the recorded reason. Never reconstruct the shape and summarise the
reason away.

## RULE: an-experiment-stops-at-a-decision
Experimental work gets one smoke run and one full benchmark, then stops for a decision, unless it clearly
improves the target without material regression.

## RULE: enumerate-then-ask
For a finite source set, enumerate it from the producing code BEFORE review, and hand the reviewer the
inventory plus your disposition for each item. Never send a reviewer in cold to build the first inventory.

## RULE: self-review-before-any-reviewer
Reach your own all-clear first: scope drift, naming, debug residue, incomplete renames, wrong premises.

Before committing any renamed, added, or signature-changed identifier, search the OLD and the NEW name
across source, tests, docs, scripts, and workflow files, plus every root file. Search the VALUE and
anything derived from it: checksums, generated files, and the generator that rebuilds them.

- Sweep the axis the change MOVED, not the axis you already thought about.
- Name the TWIN of every site you change: the other branch, the other caller, the replay path beside the
  live path. Fix both in one edit.

## RULE: fix-the-path-that-runs-first
After fixing a defect, ask which code runs BEFORE the code you changed and whether it carries the same
assumption. Gates, validators, and pre-flights are the usual answers, and none of them is in your diff.

Where a mechanism offers several routes to one effect, fixing any one produces the same green as fixing
all of them. Count the routes and name them in the claim. Ask which route no test can reach; that is where
the stale version is.

## RULE: two-rounds-of-one-shape-means-stop
When review returns a new MEMBER of a set you already named, stop describing the set and enumerate it from
the producing code. Enumerating from the findings already in hand yields a list blind to whatever the
rounds happened not to hit. Only a new AXIS earns another round.

Where a fix rests on a structural claim, have the machine PRINT the structure rather than reading for it.

## RULE: findings-ship-fixes
Every review finding arrives with its exact fix, never diagnosis alone. Adjudicate each one: adopt, modify,
or reject with repository evidence. Never blind-paste and never silently drop one.

## RULE: classify-every-finding
Mark each REPRODUCIBLE or HYPOTHETICAL before writing a fix. Reproducible means you can state the input a
real user produces. Fix a hypothetical only when it is trivial AND the failure would be silent; otherwise
record it as a known limit.

Reproducible is necessary, not sufficient. Also ask whether the behaviour is intended.

## RULE: merge-ready-means-all-of-these
Merge-ready means the release build, the portable gate, the observed Windows path, review, and pull-request
CI are all green. After an authorized merge, verify `main` and close the issue. Any code edit after
validation invalidates validation.

## RULE: the-gate-is-the-approval
Once a change clears its gates, merge it yourself, including your own work. Never ask for permission to
merge and never park finished work awaiting a founder merge (founder standing instruction, 2026-08-30).
If something blocks the merge, surface the exact command for Saurabh to run once.
