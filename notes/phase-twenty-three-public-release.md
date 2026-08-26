# Phase 23 public-release evidence

Date: 2026-08-26

## Implemented public-repository controls

- The repository now carries the complete GNU GPL version 3 text, privacy, security, support, public-release,
  and generated third-party dependency documentation. The local license text exactly matched GNU's official
  plain-text source after line-ending normalization on this date. Founder and legal approval of the chosen
  licensing and public claims remains required before release.
- `scripts/generate-third-party-notices.ps1` resolves the exact production WinUI app and runtime-worker NuGet
  graphs. It rejects missing or unsafe license metadata and generated an inventory of 30 packages. Separately
  delivered models, CUDA components, and EG-1 still require reviewed license records in their signed manifests.
- The separate model/native inventory records authoritative upstream source evidence for Parakeet, Whisper,
  EG-1's Qwen base, llama.cpp, CUDA, cuDNN, and MINDS-14. Its structural gate passes with eight explicit pending
  decisions, while `-RequireApproved` fails until exact provenance, payloads, notices, and legal approvals exist.
- `scripts/audit-public-release.ps1` fails closed on missing public documents, notice drift, private machine
  paths, secret-shaped content, or tracked model, private audio, and key material. The only audio exception is
  the exact reviewed public Whisper UAT fixture directory.
- Canonical validation now runs the public audit. GitHub private vulnerability reporting, Dependabot security
  updates, secret scanning, push protection, and validity checks are enabled and independently verifiable with
  `-VerifyGitHubSecurity`.
- The public README now describes the production architecture and measured readiness without founder-machine
  paths. Historical notes and helper defaults were made portable while preserving their evidence.

## Validation

- The public audit passed over 364 tracked files and verified all 30 production NuGet packages.
- The expanded public audit validates the eight-record model/native inventory without treating source evidence
  as legal approval.
- `git diff --cached --check`, PowerShell parsing for changed scripts, and Python AST parsing for changed spike
  helpers passed.
- Canonical validation passed with zero build warnings or errors: 34 preserved-proof contract tests and 350
  production architecture/foundation tests passed, all production and UAT projects built, and the bundled
  runtime worker launch assertion passed.
- Local model runtime tests were not requested by canonical validation and are not claimed here.

## Native UAT evidence and remaining blockers

An isolated production WinUI app and its Parakeet worker launched successfully. The Windows UI-control helper
discovered the single `EnviousWispr` window but failed twice to bind it because the foreground window did not
report a process identifier. Per the helper's safety rules, no UI input was sent. Only the processes created by
this attempt were stopped.

The resulting deterministic production-specific harness then passed three times, in 12,206 ms, 8,827 ms, and
8,910 ms, on this machine. The real shell and exactly one owned final-ASR worker became ready, the
SHA-256-admitted PolyAI MINDS-14 French public fixture flowed through quantized Whisper on CUDA with polish
disabled, and the expected
phrase appeared in the controlled native edit field. The app exited normally and the harness observed zero
remaining owned workers, app processes, or target processes. The result contained only typed fields and
booleans; its isolated profile and temporary target observation were deleted.

This automated result substitutes reviewed fixture capture and named press/release events. Physical WASAPI
microphone capture and physical global-hotkey registration from a non-EnviousWispr app remain unobserved.

Phase 23 is not complete. The physical journey above, signed install, update, rollback, uninstall, SmartScreen,
endpoint-security behavior, model and CUDA artifact licensing, accessibility review, representative hardware
coverage, private-beta daily use, public support operations, website claims, final release-candidate UAT, and
explicit founder approval remain open.
