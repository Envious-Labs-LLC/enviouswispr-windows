# GitHub

Repository: `Envious-Labs-LLC/enviouswispr-windows`. **Public.** Default branch `main`.

## FACT: main-is-not-protected
Measured 2026-08-30: no ruleset, and branch protection returns "Branch not protected". Nothing mechanically
blocks a direct push to `main`, and no check is required before a merge.

**So the discipline is the only guard, and it is therefore not optional.** Never push to `main`. Never
merge with CI red. Do not read a green pull request as proof a gate passed; there is no gate.

Recheck before relying on this. If protection is added later, this entry is stale and must be updated
rather than worked around.

## FACT: ci
One workflow, `ci.yml`, one job, `build-and-test`, which runs the portable validation gate. A pull request
is not ready while that job is red.

## RULE: comment-bodies-come-from-a-file
Write every issue, pull request, and comment body to a file and pass it with `--body-file`. Never inline
with `--body "…"`.

Shell quoting must never be able to alter technical text. Where it can, the comment posts successfully with
a hole in it and the only tell is easy to miss. This is not a judgement call.

## RULE: read-an-issue-in-full
Read with `gh issue view <N> --json title,body,state,labels,comments`. Never bare `--comments`, which in a
pipe omits the body and metadata and prints nothing for zero comments, so a piped read looks like an empty
issue.

## RULE: a-closing-keyword-ignores-every-qualifier
GitHub closes greedily. `Closes #N Phase 1` closes the whole issue. **A negation is just another ignored
qualifier: `Does NOT fix #1780` CLOSES #1780.**

Broader and more common than the disclaimer case: writing ordinary prose ABOUT an issue produces the
adjacency. `This is the fix #2143 names` reads as a closing reference.

- Use `Part of #N` or `Updates #N` unless closure is intended. Safe forms put the reference FIRST or drop
  the verb.
- **Defence is a check, not knowledge.** Before merging, verify
  `gh pr view <N> --json closingIssuesReferences` lists exactly what you intend. It LAGS a body edit, so
  re-run until it settles.

A victim looks like an issue closed with no commit and no comment. Reopen it and add a status comment.

## RULE: every-issue-carries-priority-and-type
Every new issue gets a priority and a type. Review findings include the exact proposed fix, never the
diagnosis alone.

## RULE: the-pull-request-workflow
Branch from `main` in its own worktree, implement, commit, push, open the pull request, clear review, then
merge and delete the branch. Always pass `--auto` so the merge waits for CI rather than no-opping while
checks run.

**The gate IS the approval.** Merge your own work once it clears; never ask, and never leave finished work
open waiting to be asked (founder standing instruction, 2026-08-30).

## RULE: write-to-a-live-surface-alone-in-its-own-call
Any command that posts to an issue or a pull request runs alone, never chained, and never with its output
suppressed. A probe chained in front of a real comment is not denied; it SUCCEEDS and posts.

Never chain anything onto a commit either. Write the message to a file, stage in one call, commit from the
file in the next.

## RULE: two-confusing-but-harmless-events
- A merge printing `fatal: 'main' is already used by worktree` is local cleanup noise. The merge succeeded.
  Confirm the pull request state and do not retry.
- A push rejected for a path your change does not touch usually means a stale branch base. Fetch, rebase,
  re-run the gate. Never reach for a bypass first.
