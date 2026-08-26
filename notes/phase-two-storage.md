# Phase 2 core and storage evidence

Measured on the founder's Windows rig on 2026-08-25. The current contracts under `.claude/knowledge/`
remain authoritative.

## Implemented boundary

- Core now owns platform-neutral settings, provider selection without credential fields, reusable custom
  words and snippets, typed content-free errors, and immutable dictation-session snapshots.
- The current local settings schema is version 2. Phase 1 schema version 1 migrates deterministically,
  preserving launch count and onboarding state while adding documented defaults.
- Local writes use a temporary file in the destination directory followed by replacement. Migration and
  reset first preserve the prior bytes as `settings.json.previous`.
- A future settings schema returns a typed `NewerSchema` error and is not rewritten by application
  startup.
- Portable profile export includes user preferences, custom words, and snippets. It deliberately excludes
  machine-local launch/onboarding state, provider credentials, transcript history, diagnostics, audio,
  clipboard data, and surrounding text.

## Automated evidence

- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/validate.ps1` passed with zero build
  warnings/errors, 34 portable proof tests, and 16 production architecture/storage tests.
- Storage tests cover current-schema round trip, invalid JSON, invalid semantic values, v1 migration,
  future-version preservation, recoverable reset, atomic temporary-file cleanup, export/import round trip,
  invalid import, future import, machine-local state preservation, provider secret-field exclusion, and
  content-free session contracts.
- The local model-dependent proof tests were not rerun because Phase 2 does not change the preserved
  dictation implementation or model assets.

## Native Windows acceptance

- The final Phase 2 self-contained x64 WinUI executable launched with a staged schema-1 settings document.
- The visible shell reported `Settings state: Migrated safely` and `Production-shell launches: 5`.
- The persisted current document reported schema version 2 and launch count 5.
- `settings.json.previous` retained the exact schema-1 document with launch count 4.
- Typed JSONL diagnostics recorded `SettingsMigrated`, `ShellShown`, and `ShellClosed` without content
  fields. The window closed normally and no EnviousWispr process remained.

## Remaining boundary

Phase 2 supplies storage contracts and services, not the final settings editor or import/export UI. Those
user journeys remain in Phase 14. Provider credentials remain exclusively a Phase 11 Windows Credential
Manager concern and are structurally absent from these documents.
