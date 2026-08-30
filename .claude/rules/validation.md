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

## Observe it, do not assert it

User-facing behavior is proven by driving the real application in a logged-on interactive desktop session,
never from a background service session. A WinUI app launched from a service session exits before it draws,
so a green headless run is evidence about the code and no evidence at all about what the user sees.

Founder standing instruction, 2026-08-30:

- Every session has eyes and hands on the Windows development machine. Use them rather than describing what
  the change should look like.
- Behavior that unfolds over time is captured as a screen recording and attached to the pull request or
  issue. Prose about what happened is not the evidence; the recording is.
- Prefer a synthetic end-to-end run over a human checklist. Reserve the human only for what a synthetic
  sender cannot produce, such as a global gesture the operating system refuses to accept from software.

The `tools/*-uat` harnesses are the shipped vehicles for this, and `tools/app-journey-uat` covers the whole
dictation journey. Extend an existing harness before adding another.
