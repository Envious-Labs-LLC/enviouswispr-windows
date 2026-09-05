# Knowledge router

Read only the files relevant to the task, but read each selected file completely.

| Task | Required files |
|---|---|
| Any product or scope decision | `product-contract.md` |
| Architecture, dependencies, or platform choice | `architecture.md`, `product-contract.md` |
| Dictation behavior, deterministic cleanup, emoji, or fallback | `pipeline.md`, `product-contract.md` |
| Anything the user SEES — a screen, a control, a colour, a size, copy on a surface | `design-system.md` |
| Installer, signing, updates, release channels, or how a speech model reaches a PC | `distribution.md`, `product-contract.md` |
| A crash, hang, silent exit, or any "why did it do that" | `diagnostics.md` FIRST, then the matching contract |
| Any shipped implementation | the always-on rules are already in context; add the matching contracts above |
| Parity with the macOS app, or "does Windows have X yet" | the catalog first (below), then `mac-parity-audit.md` |
| Historical measurements or experiments | Matching file under `../../notes/` |

## The cross-platform catalog answers parity questions first

`~/.claude/knowledge/enviouswispr/catalog.db` holds every feature across macOS, Windows and Android, each
row citing the file that decided it. Query it before reading a table or grepping the tree. **Never publish
a feature count here; ask the catalog.**

```bash
C=~/.claude/knowledge/enviouswispr/catalog.db
sqlite3 -header -column $C "SELECT platform_key, status, summary FROM feature_platform WHERE feature_slug='<slug>';"
sqlite3 -header -column $C "SELECT feature_slug FROM feature ORDER BY feature_slug;"   -- the slug list
sqlite3 -header -column $C "SELECT kind, gap FROM catalog_gap;"                        -- what it cannot prove
```

Read `catalog_gap` before acting. The Windows column was written by reading source, never by running the
app, and its `absent` rows rest on a grep that a differently-named feature would slip past.

The forward-looking source of truth is this knowledge folder. `notes/` contains dated evidence. Code and
tests establish current implementation. When they disagree, stop, identify the drift, and resolve it in
the same change or a linked GitHub issue.

## Pathway Triggers

The session hook greps this section and arms each row's `**when:**` words as triggers, so a matching prompt
surfaces the row without the whole index. One row per line, every row carries a `**when:**`.

- [diagnostics.md](diagnostics.md) — **when:** crash, hang, silent exit, app quit, vanished, app.jsonl, run-state, heartbeat, diagnostic log, why did it do that
- [design-system.md](design-system.md) — **when:** screen, control, colour, color, size, spacing, copy, icon, theme, anything the user sees
- [pipeline.md](pipeline.md) — **when:** dictation behaviour, deterministic cleanup, emoji, punctuation, fallback, transcript, wrong words, made up, making up, makes up, invented, hallucinat, extra words, trailing garbage, cut off, empty result, garbage at the end
- [distribution.md](distribution.md) — **when:** installer, signing, updates, release channel, Velopack, feed, SmartScreen, publisher warning, cannot install, update failed, model download, model is not installed, manifest, models.enviouslabs.co, R2, Hugging Face
- [architecture.md](architecture.md) — **when:** architecture, dependency, platform choice, project layout, new module, where does this live, out of process, worker
- [product-contract.md](product-contract.md) — **when:** product decision, scope, invariant, privacy boundary, are we allowed to, should we ship, release gate, is this a defect
- [mac-parity-audit.md](mac-parity-audit.md) — **when:** macOS parity, does Windows have, missing feature, the Mac does, on macOS, how does the Mac, deliberately different
