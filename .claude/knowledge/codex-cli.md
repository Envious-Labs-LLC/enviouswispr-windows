# Running Codex ("Astra") as an independent validator

**when:** `codex exec`, Astra, independent review, validate the plan, second opinion, a review that came
back empty, a background job that will not finish.

Ported 2026-09-05 from the macOS project's `~/.claude/knowledge/ai/codex-cli.md` and
`dev/async-watchers.md`, whose method this is. Their enforcement lives in hooks under `~/.claude/` that do
not exist here; what follows is the DECISION PROCEDURE, plus what this machine measured for itself.

## RULE: feed the prompt deliberately, and never let stdin be ambiguous
`codex exec` with a positional prompt and an open stdin **hangs forever**, and it looks exactly like a slow
review: empty output, near-zero CPU. Measured here 2026-09-05 - 51 minutes at 0 CPU-seconds before anyone
checked. The macOS side bans `</dev/null` outright because THEIR prompt arrives on stdin, so closing it
feeds an empty prompt and the run exits 0 with the model never invoked.

**Both hazards are avoided the same way: pipe a real file.**

```bash
cat prompt.md | codex exec -m gpt-6-astra -c tools.web_search=true \
  --sandbox danger-full-access --skip-git-repo-check -o verdict.md
```

Measured exception, recorded because it contradicts the macOS rule and was verified here: a POSITIONAL
prompt plus `</dev/null` does work on this machine (codex-cli 0.153.4) - three reviews, 20k+ tokens,
findings citing real `file:line`. It is not the shape to write, because it is one refactor away from the
silent-empty-prompt failure the moment the prompt moves to stdin.

## RULE: put every `-c` flag BEFORE the subcommand
`-c` exists at both levels and Codex does not merge them: a subcommand-level `-c` silently discards every
top-level one. Sanity check with `grep "reasoning effort:" <outfile>` - it should read what you intended.

## RULE: web search is OFF unless you turn it on, and a planning prompt must ask for it
`-c tools.web_search=true`, before the subcommand. **Three plan validations on this machine ran without
it** (2026-09-05, the Store plan) and their Microsoft Learn citations cannot be distinguished from recall.

Every planning prompt carries an explicit known-problem pass:

> Before analysing our code, search the web for whether this is a KNOWN problem with a KNOWN fix. Name the
> fix, its canonical source, and whether we already do it. Only then answer the repo-specific questions.

**The tell that a question needs it: is the subject OURS?** A defect in our own C# is ours. MSIX, the Store,
Windows virtualization, a vendor SDK, a CI runner - somebody else's, with other users and a documented
answer. Repo grounding tells you whether a known fix APPLIES; it can never tell you the fix exists.
**Never write "justify from the repo, not from general practice."** It reads as rigour and functions as a
gag on the outside world; if the intent is "do not guess", write that.

**Check WHICH hosts were reached, never whether any host was.** The macOS side measured a run that reached
the web, cited a real vendor page for an unrelated sub-question, and missed the documented one-line fix for
the symptom under investigation. So for a Store or packaging question, require a `learn.microsoft.com` or
Store-policy host to appear in the citations:
`grep -aoE "https?://[a-zA-Z0-9./_-]+" <outfile> | sort -u`. Vendor and docs hosts absent on a
somebody-else's-subject question means the pass did not happen, whatever any detector says. And when a
citation does come back, **a technique existing and a technique being TABLE STAKES are different claims,
and only the second decides whether to build** - check whose domain the citation is from.

## RULE: a verdict with an empty read set is a failed attempt, not a verdict
The first Store-plan validation here returned `REJECT - validation blocked` having read **nothing**: every
tool call had failed `os error 206` (see PROC below) and the model wrote a verdict-shaped answer about its
own inability. Re-armed rather than counted, correctly - but nothing in the output marks it as different
from a real REJECT, and the conclusion field is the only part that looks authoritative.

So prove the reviewer READ something, not only - per the disposable-worktree proof - that it wrote nothing:

```bash
grep -aoE "https?://[^ )\"]+|[A-Za-z0-9_./-]+\.(cs|csproj|md|ps1|xaml)" <outfile> | sort -u
```

Require the files you expected to be named. An empty or irrelevant read set means the run failed; re-arm it.
Same class as a green suite that skipped its lanes: the part that looks authoritative is the part that is
wrong. (Method from the macOS project, 2026-09-05.)

## RULE: a stall is indistinguishable from success by exit code
`codex exec` can wedge with the process alive, 0% CPU and output frozen at the echoed prompt, indefinitely -
and **killing a wedged run reports exit 0**. So waiting on the exit notification cannot work.

- Watch the **artifact**, never the process: the answer file appearing (`-o <file>`), or the transcript's
  size/mtime advancing. Progress is OUTPUT GROWTH, not parent CPU - a Codex parent waiting on the API or on
  its children idles at 0 CPU while the stream grows (false alarm measured here 2026-09-05 at 270 KB).
- A stall is output mtime flat **AND** CPU flat over a window (macOS uses 240 s), resampled before killing.
- **Never grep the transcript for a completion token.** Your own prompt is echoed into it the moment the run
  starts, so a watcher looking for a word your prompt contains fires immediately on a half-finished run.
- **Never `tail`-pipe a background command whose progress you intend to watch**: the pipe emits nothing
  until it exits, so a working run looks identical to a dead one. That is a third distinct reason a watcher
  looks silent, alongside one that cannot fire and one filtered wrong.
- No wall-clock kill timer on a reasoning payload; thinking time is variable and a timer decapitates deep
  runs, a worse failure than the hang it guards against.

## RULE: give every prompt a bounded output contract, on both sides
Name the exact sections wanted, cap the length, and end with a literal stop instruction. **Bound what it
READS too**: name the files, forbid unscoped search (`never run grep -r, or rg without a file argument`),
and offer the cheap escape - *if you want a file I did not list, write it down as UNCHECKED*. A run that
never converges is not a stall: the output keeps growing and no detector fires. If a run dies with no answer,
prove the tool is fine with a throwaway `PONG` exec before blaming infrastructure.

## RULE: resume across rounds on the same task
Round 2+ on the same subject is `codex exec resume <UUID>` (or `--last`) with a delta, not a fresh `exec`.
A fresh exec loses the reasoning trace and re-pastes the whole prompt. Recover the id with
`grep -oiE "session id: [0-9a-f-]{36}" <outfile>`. Flag ownership: `exec` flags before `resume`, resume
flags after; `--skip-git-repo-check` is needed on the resume too when cwd is not a trusted git directory.
**Three rounds on the Store plan here were fresh execs** - they worked, and they paid for it twice over.

## RULE: reasoning effort medium or high, never low
Low reads fewer files and fills the gap with invention - it has fabricated a repo file that does not exist,
with a plausible purpose and line citations, in the same register as a real finding. Wall-clock is dominated
by repo reads, so a dramatically faster run read less rather than reasoned better.

## RULE: the cloud reviewer is a gate, not an iteration partner
Founder instruction on the macOS side, 2026-09-01, and the one process difference worth stealing: run a
LOCAL grounded pass to an explicit all-clear FIRST, then one confirming round. When that round finds
something, do not round-trip with it - take the finding to a local pass with a lens the reviewer does not
use (reachability, integration, what to DELETE), and validate every finding against the real producer of
its input before writing code.

## PROC: how a validation is run here
1. Write the plan and the prompt to files inside the checkout Codex will run from (a worktree it is not
   `cd`-ed into reads as "file missing").
2. Use a **disposable worktree** (`git worktree add ../ew-review origin/main --detach`) with
   `--sandbox danger-full-access`, then prove nothing was touched with `git status --short`. This machine's
   `--sandbox read-only` is unusable: the Windows sandbox helper is spawned with a ~37 KB payload, over the
   32,767-character command-line limit, so **every** tool call fails `os error 206` and the review comes back
   "REJECT - blocked" having read nothing. Not the env (3.6 KB) and not the prompt. A one-word smoke test
   passes because it spawns no tool.
3. Ask for a verdict line (APPROVE / APPROVE WITH CHANGES / REJECT) and findings tagged
   [BLOCKING]/[ADVISORY] with the file or source checked.
4. Read the verdict from the `-o` answer file, never from the transcript.
