# Session behaviour

## RULE: main-stays-clean
`main` is never edited in place. Every change uses its own branch and worktree. After an authorized
merge, remove the worktree. Restore the checkout to `main` at session start and leave it there.

## RULE: never-ask-if-done
Keep working. Saurabh decides when to stop. Never ask whether the work or the session is finished.

## RULE: plain-english-to-saurabh
Saurabh is the founder, not an engineer. No class names, file paths, protocol jargon, or acronyms in chat.
Those belong in tool calls and commits. Every message opens with `<Task> — <STATUS>` on its own first line,
where status is one of DONE, WORKING, WAITING ON <what>, or NEED YOU.

## RULE: this-repository-is-public
Everything committed here is world-readable, including this file. Never commit a machine path, a personal
name beyond Saurabh's, a credential, a customer record, or internal strategy. Diagnostics carry shape,
never dictated content.

## RULE: promote-by-editing-not-appending
Before adding a heading, grep the existing knowledge and rules for one that already owns the fact, and
tighten that in place. A new entry is for a fact nothing covers.

Rules contain actions, and the test is whether the instruction got SHORTER. Incident evidence goes in
`LOG.md`. Rule files balloon one correct paragraph at a time.

## RULE: winddown-four-steps
On "wrap up" or "wind down", complete all four. Never stop after one.

1. Promote durable learnings into the owning knowledge or rule file.
2. Close finished GitHub issues; status-comment the open ones.
3. Add a `LOG.md` entry if the session produced a decision, a defect found by something other than its
   author, or a fact that would cost somebody a day to rediscover.
4. Ship the branch and reviewed pull request. Merge and verify `main` only when Saurabh requested that
   exact merge.

## RULE: one-release-per-session
At most one release per session. Batch post-release bugs into the next planned release unless the bug
loses a user's dictation or crashes the app.
