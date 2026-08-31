# Tools and apps

Safety core. Every prohibition here is resident on purpose.

## RULE: act-immediately-never-ask-permission-to-test
Never ask Saurabh before closing the dev app, running tests, or rebuilding. Begin, then say what you did
in the past tense. "Should I close the app to test?" is refused, not rephrased.

A WAIT is a permission request with the request left implicit. Stopping until his activity ends seeks the
same approval without forming a question, so no guard can see it.

The only occupancy question that survives is whether ANOTHER session holds the app. Never Saurabh.

## RULE: identify-a-process-by-path-never-by-name
Never kill by bare process name. A Release build can answer the same global hotkey as the dev build.
Identify every candidate by process id and executable path, always.

Before taking the app slot, probe for running candidates. Get the start time before any ownership claim,
because a start time is falsifiable against your own history and an executable path is not. An empty
process table is not evidence of a release: taking the slot BEGINS by creating that absence. If ownership
is unclear or several candidates match, stop.

## RULE: a-harness-that-acts-must-refuse-not-choose
A harness that kills or deploys is worse in kind than one that reports wrongly, because it acts on the
guess, unattended.

- When more than one instance matches, return invalid with the count and the candidates.
- Every kill names a process id you verified in the same call. Prefer one you own over one you re-derived.
- A harness that mutates the launch environment must restore it.

## RULE: never-unattended-recursive-delete
Never write an unattended recursive delete over a path a user could have touched. The set of ways a mount
or a link can be in the way is not enumerable by testing the path. Enumerate entries and delete them
individually, and assert the world afterwards rather than trusting an exit code.

## RULE: cwd-is-sticky
Shell working directory persists across calls. Never change directory for a one-off; pass the path to the
command. A wrong directory tests one tree while your edits land in another, which produces a false green.
Before any reported build, test, commit, or push, print the directory and branch in the same call.

A data path is a path too. A checker that loads its baseline by relative path verifies against whichever
checkout happens to be current.

## RULE: watch-the-artifact-never-the-process
A process-name liveness check can match its own command line, so its death branch is unreachable. Poll for
the answer file being non-empty; detect death as the output not growing across several checks.

Before writing any watcher, ask whether its question has an answer in the failure case. "Have the runs
finished" has no truth value when no run was ever created, so the loop waits forever exactly when
something has gone wrong. Add a second question with a defined answer.

Gate on stability, never on an expected count. "Complete and unchanged across N polls" has no parameter to
get wrong. Keep any count as a loud flag, never as the gate.

## RULE: reproduce-the-regression-before-building-the-guard
Before adding a guard, gate, threshold, or detector, reproduce the failure it is meant to catch and name
what arms it. A guard nobody arms is not a guard, and a guard never observed failing is a comment.

## RULE: never-weaken-a-guard
When a check or a hook blocks, fix the cause. Never bypass, never disable a test, never comment out an
assertion. If a guard fires on innocent work, move the text to a file and pass the file; changing the
channel keeps the meaning, and rewording hides what the command still does.
