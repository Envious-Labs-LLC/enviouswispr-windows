# EnviousWispr Windows Edition project brain

EnviousWispr Windows Edition is the native Windows sibling of the shipping macOS dictation product. Its
job is simple: hold a key, speak, release, and get accurate, optionally polished text in the app that was
focused when recording began.

## How this project works

Saurabh is founder; Claude is CTO and implementer, alongside Codex which also writes into this codebase.
Every update to him is exec-level plain English, one question at a time. No class names, file paths, or
engineering acronyms in chat — those belong in tool calls and commits.

**Two authors, one canonical tree.** Read files off disk rather than trusting an earlier read, and check
`git status` before asserting the state of anything.

He does not need to be asked for permission. Once a change clears its gates, ship it.

## Objective: Reach, Not Revenue

**North Star:** 100,000 users, notoriety, market disruption. Free is the core strategy, not a gap. No
paywalls; users bring their own keys and use everything free. Monetization is deferred until massive
scale. Judge every plan on traction, credibility and word-of-mouth toward 100k. Never apply a revenue
rubric, and never treat "free" as a weakness.

## Current truth

- The repository contains a founder-tested WPF and .NET 8 proof on Windows 11 x64.
- The proof already demonstrates native audio capture, a global hotkey, Parakeet ASR, EG-1 polish, an
  overlay, tray behavior, startup behavior, clipboard-safe insertion, and CPU fallback.
- It is evidence to preserve, not a claim of feature parity or the final production architecture.
- The production target is C# on the current .NET LTS with WinUI 3. Native C or C++ libraries are allowed
  behind narrow interfaces for model runtimes.
- Windows 11 x64 ships first. ARM64 follows after the x64 release is stable.

## Product invariants

1. Dictated audio stays on the user's computer.
2. Envious Labs receives operational metadata only, never dictated content.
3. Deterministic processing is the reliability floor. AI is an optional limb, not the heart.
4. A cloud polishing provider receives text only when the user selects it and supplies their own key.
5. Failure returns the best last successful text instead of losing the dictation.
6. Live preview is display-only and can never change the final transcript.
7. Automatic hardware selection is the default. Manual engine and device choices remain available.
8. Public release waits for full agreed Windows parity, not just a convincing demo.

Detail and the full contract: [`.claude/knowledge/product-contract.md`](.claude/knowledge/product-contract.md).
What is present, partial and absent against macOS:
[`.claude/knowledge/mac-parity-audit.md`](.claude/knowledge/mac-parity-audit.md) — **read it before calling
any gap a defect**, and note it expires, so its absences are claims that need re-checking.

## Principles

- **Heart and limbs.** The critical path — hotkey, capture, ASR, text finalization, insertion — must never
  fail. Everything else is a limb: it enhances behind a deadline and a fallback, and on failure returns the
  last SUCCESSFUL text, never nothing.
- **Deterministic processing is the reliability floor.** AI is a limb, never the heart.
- **Production-grade from day one.** No band-aid fixes, no shortcuts trading today's speed for tomorrow's
  debt.

## Compatibility (know this cold)

- **Windows 11 x64 ships first.** ARM64 follows once x64 is stable.
- **The production target is C# on the current .NET LTS with WinUI 3.** The shipped proof is WPF on
  .NET 8. Native C or C++ is allowed behind narrow interfaces for model runtimes.
- **A WinUI app launched from a service session exits before it draws.** Anything user-facing is proven in
  a logged-on interactive desktop session or it is not proven.
- **Insertion borrows the clipboard and gives it back.** It is snapshotted before a paste route uses it and
  restored afterwards, and every delivery reports which route ran.
- **Windows delivers text through TWO routes, not the five macOS uses, and that is deliberate.** A
  synthesised paste keystroke, and clipboard-only when that is refused. The catalog records this as
  `deliberately-different`; it is not a parity gap and must not be "fixed" toward the macOS shape.

## Rules

Every file in `.claude/rules/` without a `paths:` block at its top loads on every session. Those are
instructions, not background, and they are already in context before you read this list.

| Rule | Owns |
|---|---|
| [grounding-discipline](.claude/rules/grounding-discipline.md) | where to look, in what order, and what a hit does and does not settle |
| [workflow-process](.claude/rules/workflow-process.md) | how a change moves from idea to merge-ready |
| [validation-discipline](.claude/rules/validation-discipline.md) | what green is allowed to mean |
| [tools-and-apps](.claude/rules/tools-and-apps.md) | the safety core: destructive actions, shared resources, watchers, guards |
| [session-behavior](.claude/rules/session-behavior.md) | how a session opens, reports, and winds down |
| [github](.claude/rules/github.md) | issues, pull requests, and the fact that `main` is unprotected |

A rule file carrying a `paths:` block is scoped to those paths and arrives only when one is in play.
Rules beat documents when they conflict.

## Read before work

Read `.claude/knowledge/INDEX.md`, then every matching contract or rule it lists. Use `notes/` when a
decision needs the measurements or experiment history behind it.

## Cross-platform product catalog

**Before reimplementing a macOS behaviour here, or claiming a feature is missing, QUERY THE CATALOG.**
`~/.claude/knowledge/enviouswispr/catalog.db` holds what EnviousWispr actually does, feature by feature,
across macOS, Windows and Android. macOS is the reference: it is launched and shipping. Every row cites the
file and symbol that grounds it; where documentation and code disagreed, the code won and the disagreement
is a `discrepancy` row.

```bash
C=~/.claude/knowledge/enviouswispr/catalog.db
sqlite3 -header -column $C "SELECT status, summary FROM feature_platform WHERE feature_slug='multi-route-paste';"
sqlite3 -header -column $C "SELECT surface, exact_text FROM user_copy WHERE feature_slug='<slug>';"
sqlite3 -header -column $C "SELECT decision_text, reason, decision_date FROM decision;"   -- settled questions
sqlite3 -header -column $C "SELECT kind, gap FROM catalog_gap WHERE feature_slug='<slug>';"
```

**Check `catalog_gap` first.** It records where the catalog is too thin to rebuild from, is overstated, or
missed something. A feature with a `not-reimplementable` row needs the source read as well.

**`deliberately-different` IS AN ANSWER, NOT A GAP.** Windows carries 10 of them (measured 2026-08-30),
each with its reason in `difference_reason`. They exist because the macOS mechanism has no cause on
Windows: `soft-onset-protection` guards a problem this platform does not have, `warm-engine` was measured
and rejected, `multi-route-paste` collapses five macOS routes into two. **Never close one toward the macOS
shape.** Read the reason, and if you disagree, say so as a product argument rather than a parity fix.

```bash
sqlite3 -header -column $C "SELECT feature_slug, difference_reason FROM feature_platform \
  WHERE platform_key='windows' AND status='deliberately-different';"
```

**`absent` can mean BUILT AND RETIRED, with the reason attached.** Windows carries 13. Read the `decision`
rows before rebuilding one as a parity gap.

**WHEN THE CATALOG DOES NOT ANSWER, READ THE macOS SOURCE. Never research from scratch.** The catalog is
an index, not the product, and a thin row reads exactly like an absent capability.
`~/Developer/EnviousLabs/EnviousWispr/Sources/` is the shipping app and
`~/Developer/EnviousLabs/EnviousWispr/.claude/knowledge/` is 101 files of measured findings, including the
dead ends that source code cannot record.

**A HIT ABOUT A NEIGHBOURING MECHANISM IS NOT AN ANSWER.** Verify the row covers the mechanism under
investigation. The trap is not silence; it is a well-formed answer under a name matching your symptom.

Order: catalog, `catalog_gap`, the macOS source and its knowledge, then research. Reaching step four with
the first three unanswered means the thing is genuinely new.

Ref 2026-08-30, cost most of a day: a `hallucination-protection` hit described polish-output guards, so
ASR-level suppression read as absent. The answer was in the macOS speech backend with a 107-clip benchmark
beside it. Full account in `LOG.md`.

Rebuild and contribute: `~/.claude/knowledge/enviouswispr/README.md`. The database is an artifact; the truth
is `schema.sql` and `data/*.sql`, which are plain text. Never hand-edit the binary.

## Canonical validation

From PowerShell at the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/validate.ps1
```

Use `-IncludeLocalRuntime` only on a machine with the required local model packs. The default command is
the portable build and contract gate used by CI.

**A build succeeding is not a test result, and a green portable gate is not the user path.** The gate
proves the code compiles and its contracts hold. It proves nothing about what a person sees. Drive the real
application in a logged-on interactive desktop session, and attach the recording.
Owner: [`.claude/rules/validation-discipline.md`](.claude/rules/validation-discipline.md).

## Delivery workflow

GitHub Issues are the durable task system. Every implementation ends with relevant validation, a pushed
branch, and an updated pull request. Once a change clears its gates, merge it. Do not park finished work
waiting to be asked (founder standing instruction, 2026-08-30).

Work happens in a git worktree on its own branch, merges to `main`, and the worktree is removed. `main`
stays buildable and is never edited in place.

`LOG.md` carries the reasoning behind the work at the level of a working session: decisions, defects that
survived to be found by something other than their author, and facts that would cost somebody a day to
rediscover. Add an entry when a session produced one of those. A finding caught and fixed inside a session
is not one; a gate doing its job is the expected case. The file is public, so no machine paths, no personal
data, no credentials.
