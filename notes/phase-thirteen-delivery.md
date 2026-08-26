# Phase 13: context-aware text delivery

## Contracts

- READ 2026-08-26 — `docs/plans/windows-master-plan.md` Phase 13 requires bounded surrounding-text
  reads, cursor repair, UI Automation insertion, clipboard fallback and restoration, scoped native
  input, exact target validation, compatibility policy, and safe refusals across the Windows app
  matrix.
- READ 2026-08-26 — `.claude/knowledge/pipeline.md` freezes the target when recording begins, applies
  cursor repair after deterministic and optional model processing, and forbids a focus change from
  redirecting private text.
- READ 2026-08-26 — the shipped macOS cursor-repair source defines the reusable seam rules. Windows
  preserves the legacy one-trailing-space payload whenever context is absent or unsafe, refuses
  inside-word and unanchored-right repairs, deduplicates only complete left tokens, drops only an
  immediate duplicate full stop, suppresses leading space for terminals, and suppresses trailing
  space in URL bars.
- READ 2026-08-26 — Windows `TextPattern` is read-only, `ValuePattern` can replace a complete writable
  value, `IsPassword` identifies protected fields, `SendInput` is subject to UIPI, and clipboard
  restoration must use a sequence-number guard. These platform constraints shape the delivery
  routes rather than being hidden behind a generic paste abstraction.

## Implementation

- MEASURED 2026-08-26 — recording freezes the foreground window handle, owning process ID, focused
  UI Automation runtime ID, and delivery options. Delivery revalidates all three identities and the
  bounded caret seam before committing, then performs one final bounded re-read immediately before
  native input.
- MEASURED 2026-08-26 — surrounding reads are capped at 256 characters per side and reject oversized
  selections. Password state is checked before any text pattern or value is read. Invalid native
  read/write bounds are rejected before UI Automation runs.
- MEASURED 2026-08-26 — a writable standard edit at the document end may use complete-value
  `ValuePattern` replacement only below the 16,384-character cap and only when the full result can be
  read back exactly. An unverified write does not risk a duplicate paste; the text stays in the
  in-memory recovery floor.
- MEASURED 2026-08-26 — all other compatible targets use a dedicated STA clipboard worker and an
  exact Win64 `SendInput` layout. Every clipboard format is snapshotted before mutation; unsupported
  formats cause a safe refusal. Restore happens only after the paste delay and only when the clipboard
  sequence still belongs to EnviousWispr, so a concurrent user or application clipboard change wins.
- MEASURED 2026-08-26 — protected, elevated, inaccessible, changed, fullscreen-game, unsafe-terminal,
  held-key, blocked-input, and clipboard-unavailable paths never silently redirect insertion. When
  possible they leave the complete legacy payload on the clipboard; otherwise the pipeline retains
  the processed text in memory.
- MEASURED 2026-08-26 — the WinUI composition now enters the delivery session state after final ASR,
  deterministic cleanup, and optional polish; delivers only to the frozen target; emits content-free
  route/failure diagnostics; completes and resets the session; and exposes a specific local status for
  every safe refusal.

## Validation

- MEASURED 2026-08-26 — `scripts/validate.ps1` passed with zero warnings/errors: preserved founder
  proof 34/34, production architecture 243/243, WinUI x64 Release, and all delivery UAT harnesses.
  Coverage includes legacy fallback, English and unsegmented scripts, punctuation and token seams,
  UTF scalar boundaries, terminal and URL policy, exact target/caret identity, recovery ownership,
  option bounds, and the Win64 native input ABI.
- MEASURED 2026-08-26 — a controlled standard edit used verified UI Automation value replacement and
  produced the exact synthetic expected value.
- MEASURED 2026-08-26 — a dedicated Notepad file used guarded clipboard paste, restored the prior
  clipboard, and visibly produced the exact synthetic expected value. No pre-existing Notepad document
  was edited.
- MEASURED 2026-08-26 — a loopback-only page in visible Chrome froze as `Browser`, exposed bounded
  text context, used guarded clipboard paste, restored the clipboard, and its live DOM value matched
  the synthetic expected value. The temporary tab and loopback server were closed afterward.
- MEASURED 2026-08-26 — controlled process-shaped Office and chat targets froze as `Office` and
  `Chat`, respectively; each exposed bounded context and completed guarded paste with clipboard
  restoration. The temporary executable aliases and exact test processes were removed afterward.
- MEASURED 2026-08-26 — a protected native edit was classified before any text read, returned
  `ProtectedField`, chose clipboard-only recovery, and visibly remained empty. A fullscreen
  non-editable target returned `UnsupportedTarget` and received no insertion.
- MEASURED 2026-08-26 — a medium-integrity EnviousWispr delivery process targeting an exact
  high-integrity native edit returned `ElevatedTarget`, chose clipboard-only recovery, and the target
  visibly remained exactly at its seed value. Only the exact test PIDs were terminated.
- MEASURED 2026-08-26 — multiline terminal delivery returns `UnsafeMultilineTarget` and never calls
  native paste in policy coverage. Live terminal injection was intentionally not performed because
  the desktop safety contract prohibits automating PowerShell, Command Prompt, and Windows Terminal.
- UNOBSERVED 2026-08-26 — the installed desktop Word is an unlicensed product and disables editing;
  a blank synthetic document refused normal typing. Licensed native Word editing therefore remains
  unobserved, while the Office classification and delivery route are proven with the controlled
  process-shaped target. Real chat drafts were also not modified or sent.
- MEASURED 2026-08-26 — the founder-tested WPF proof was not modified. No connection was made to port
  8081 or to any unrelated model server.

## Primary platform references

- [SendInput and UIPI](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
- [UI Automation IsPassword property](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationelementidentifiers.ispasswordproperty)
- [UI Automation TextPattern overview](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-textpattern-overview)
- [Clipboard sequence number](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber)
