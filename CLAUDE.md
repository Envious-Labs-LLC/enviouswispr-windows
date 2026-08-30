# EnviousWispr Windows Edition project brain

EnviousWispr Windows Edition is the native Windows sibling of the shipping macOS dictation product. Its
job is simple: hold a key, speak, release, and get accurate, optionally polished text in the app that was
focused when recording began.

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

**A feature with status `absent` was often BUILT AND RETIRED, with the reason attached.** Do not rebuild one
as missing parity without reading its decision rows.

Rebuild and contribute: `~/.claude/knowledge/enviouswispr/README.md`. The database is an artifact; the truth
is `schema.sql` and `data/*.sql`, which are plain text. Never hand-edit the binary.

## Canonical validation

From PowerShell at the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/validate.ps1
```

Use `-IncludeLocalRuntime` only on a machine with the required local model packs. The default command is
the portable build and contract gate used by CI.

## Delivery workflow

GitHub Issues are the durable task system. Every implementation ends with relevant validation, a pushed
branch, and an updated pull request. Codex may submit work but must not merge unless Saurabh explicitly
requests that exact merge.
