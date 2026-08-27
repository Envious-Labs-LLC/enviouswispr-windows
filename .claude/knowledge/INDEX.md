# Knowledge router

Read only the files relevant to the task, but read each selected file completely.

| Task | Required files |
|---|---|
| Any product or scope decision | `product-contract.md` |
| Architecture, dependencies, or platform choice | `architecture.md`, `product-contract.md` |
| Dictation behavior, deterministic cleanup, emoji, or fallback | `pipeline.md`, `product-contract.md` |
| Anything the user SEES — a screen, a control, a colour, a size, copy on a surface | `design-system.md` |
| Installer, signing, updates, or release channels | `distribution.md`, `product-contract.md` |
| Any shipped implementation | `../rules/workflow.md`, `../rules/validation.md`, plus matching contracts above |
| Parity with the macOS app, or "does Windows have X yet" | `mac-parity-audit.md` |
| Historical measurements or experiments | Matching file under `../../notes/` |

The forward-looking source of truth is this knowledge folder. `notes/` contains dated evidence. Code and
tests establish current implementation. When they disagree, stop, identify the drift, and resolve it in
the same change or a linked GitHub issue.
