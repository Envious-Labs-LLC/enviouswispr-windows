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
- `scripts/audit-public-release.ps1` fails closed on missing public documents, notice drift, private machine
  paths, secret-shaped content, or tracked model, private audio, and key material. The only audio exception is
  the exact reviewed public Whisper UAT fixture directory.
- Canonical validation now runs the public audit. GitHub private vulnerability reporting, Dependabot security
  updates, secret scanning, push protection, and validity checks are enabled and independently verifiable with
  `-VerifyGitHubSecurity`.
- The public README now describes the production architecture and measured readiness without founder-machine
  paths. Historical notes and helper defaults were made portable while preserving their evidence.

## Validation

- The public audit passed over 356 tracked files and verified all 30 production NuGet packages.
- `git diff --cached --check`, PowerShell parsing for changed scripts, and Python AST parsing for changed spike
  helpers passed.
- Canonical validation passed with zero build warnings or errors: 34 preserved-proof contract tests and 350
  production architecture/foundation tests passed, all production and UAT projects built, and the bundled
  runtime worker launch assertion passed.
- Local model runtime tests were not requested by canonical validation and are not claimed here.

## Native UAT attempt and remaining blockers

An isolated production WinUI app and its Parakeet worker launched successfully. The Windows UI-control helper
discovered the single `EnviousWispr` window but failed twice to bind it because the foreground window did not
report a process identifier. Per the helper's safety rules, no UI input was sent. Only the processes created by
this attempt were stopped. The production global-hotkey-to-record-to-transcribe-to-polish-to-insert journey
therefore remains unobserved and needs a deterministic production-specific native UAT path.

Phase 23 is not complete. Signed install, update, rollback, uninstall, SmartScreen, endpoint-security behavior,
model and CUDA artifact licensing, accessibility review, representative hardware coverage, private-beta daily
use, public support operations, website claims, final end-to-end UAT, and explicit founder approval remain open.
