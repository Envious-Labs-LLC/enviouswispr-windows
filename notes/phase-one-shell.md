# Phase 1 production shell evidence

Measured on the founder's Windows rig on 2026-08-25. This note records evidence; the current product
contracts remain under `.claude/knowledge/`.

## Build and automated validation

- The production app and module graph build with .NET SDK 10.0.400 for `Release|x64` with zero warnings
  and zero errors. The combined proof-and-production solution also builds in its default Release
  configuration with zero warnings and zero errors.
- The canonical `scripts/validate.ps1` gate passed the founder-tested WPF proof build, the smoke build,
  the WinUI production build, 34 portable proof tests, and the production architecture tests.
- The local model-dependent proof tests were not rerun during this phase because Phase 1 does not alter
  the founder-tested dictation implementation or its model assets.

## Native Windows acceptance

- The unpackaged, self-contained x64 executable opened as a real WinUI 3 window titled
  `EnviousWispr` and rendered the Phase 1 production shell.
- First launch displayed `Settings state: Created safely` and `Production-shell launches: 1`.
- Starting the executable again while the first window remained open produced no second window. The
  typed JSONL diagnostics recorded `DuplicateInstanceRejected`.
- After a normal window close, the next launch displayed `Settings state: Restored` and
  `Production-shell launches: 2`.
- Both observed windows closed normally. Diagnostics recorded `ShellClosed` for each completed run.
- The persisted settings file contained schema version 1, launch count 2, and the onboarding flag only.
- The diagnostics file contained typed timestamp, event, failure-category, and optional elapsed-time
  fields only. It contained no transcript, clipboard, prompt, surrounding text, or arbitrary message.

## Scope boundary

This phase establishes the production toolchain, dependency graph, lifecycle, local settings,
content-free diagnostics, and WinUI shell. Dictation remains in the founder-tested WPF proof until each
replacement capability passes its own native Windows validation.
