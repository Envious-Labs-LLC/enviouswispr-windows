# Validation discipline

Validation claims name the exact command, revision, machine class, and observed result.

## Required layers

- Build: every project compiles in Release on Windows.
- Unit: pure contracts, state transitions, parsing, and failure behavior.
- Deterministic parity: shared fixtures for cleanup, inverse text normalization, punctuation, and emoji.
- Integration: audio, engine adapters, provider fallbacks, storage, delivery, and cancellation.
- Runtime engines: real model packs on representative CPU and available GPU providers.
- UI automation: onboarding, settings, tray, overlay, history, and accessibility.
- Native UAT: physical hotkey, live microphone, real focus changes, and paste into representative apps.
- Release: clean-machine install, update, rollback, uninstall, data preservation, and signature checks.

The portable gate is:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/validate.ps1
```

The model-dependent gate is:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/validate.ps1 -IncludeLocalRuntime
```

Model-dependent checks may be skipped only when assets are absent, and the handoff must call that out. A
filtered green test is never described as full runtime proof. Final handoff lists the branch, pull request,
commands run, native user paths observed, and remaining risks.
