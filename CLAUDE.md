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
