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

- The expanded public audit passed over 372 tracked files, admitted exactly 11 manifest-listed public audio
  fixtures by size and SHA-256, rejected unlisted WAV files, and verified all 30 production NuGet packages.
- The expanded public audit validates the eight-record model/native inventory without treating source evidence
  as legal approval.
- Native keyboard UAT completed onboarding, every product page, representative selectors and toggles, and the
  full Settings tab order through Save settings. A later native pass completed the portable-profile Save As
  and Open dialogs. The diagnostics-export picker, destructive-history, external-link, Narrator, High Contrast,
  and separate 100%/200% scale paths remain unobserved.
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

The strengthened deterministic harness then passed again in 10,394 ms while also requiring the content-free
recording, capture-finalized, transcription, deterministic-processing, delivery, and clean-shutdown event
sequence.

A separate strict acoustic mode exercised the installed global hook with synthetic F8 edges and used normal
production WASAPI capture while the reviewed French fixture played through the default speakers. The harness
verified the fixture SHA-256, decoded and amplified it only in memory, and deleted its isolated profile and
temporary target observation. Independent privacy-safe audio measurements confirmed that the active physical
microphone responded to speaker playback: average RMS rose from approximately 0.0029 at baseline to 0.0065
during a public test phrase.

The acoustic lexical gate did not pass on this hardware. In the strongest reviewed-fixture attempt, content-free
diagnostics showed `HotkeyReady`, recording start, capture finalization, Whisper completion, deterministic
processing completion, text-delivery completion, and clean shutdown; the controlled target grew from 5 to 150
characters, but the known public word `adresse` was absent. The harness correctly returned non-zero. This is
evidence that the production global-hook, WASAPI, worker, processing, and delivery stages ran, but it is not
faithful microphone-dictation proof and is not recorded as a pass.

The default automated result still substitutes reviewed fixture capture and named press/release events. The
strict acoustic mode adds real WASAPI and the installed global hook, but its key edges and speaker source are
synthetic and the lexical assertion failed on the tested speaker/webcam path. A person speaking into the
physical microphone while holding the configured key from a non-EnviousWispr app remains unobserved.

The acoustic harness now also admits a pinned English `en-US/train/0` MINDS-14 fixture from the same
CC-BY-4.0 dataset revision. Its deterministic production path passed with Parakeet CPU: the real shell and
worker became ready, the reviewed fixture crossed capture, final ASR, deterministic processing, and controlled
Windows delivery, the public word `account` was observed, and the app and worker exited cleanly. The corresponding
speaker-to-physical-microphone mode completed recording, capture finalization, Parakeet transcription, and
deterministic processing twice, but delivered no text because the speaker/webcam acoustic capture produced an
empty transcript on this setup. The first attempt also exposed and corrected a harness-only fixed playback timeout;
the timeout is now derived from admitted PCM duration. This strengthens the reproducible English success path but
does not convert synthetic speaker playback into human-spoken microphone acceptance.

The same English row was also added to the pinned Whisper language corpus rather than being excluded to keep a
gate green. Fixed-language Whisper CPU detected English, but produced 7 word edits across the 12-word reference
(58.33% WER), so it fails the existing 35% per-row guardrail. English Whisper quality on this public case is now
an explicit blocker alongside the previously documented German and Spanish rows.

A deterministic native failure matrix now exercises the remaining production-journey failure classes. An
allowlisted `AccessDenied` audio fault traveled through the installed global hook and normal session controller;
the app refused to enter recording, delivered nothing, retained the typed error code in content-free diagnostics,
and shut down cleanly with no owned worker left behind. A separate isolated copy of the production payload with
only its owned runtime-worker executable omitted kept the shell usable, reported the worker-startup failure, and
exited cleanly with no child process. Finally, the controlled frozen target was closed after recording began;
Whisper CUDA and deterministic processing completed, delivery was refused as `DeliveryTargetChanged`, no text
reached the target, the pre-test clipboard was restored, and the app and worker exited cleanly. The normal
successful production journey passed again after the fault instrumentation. This proves deterministic handling;
it does not replace a clean-machine observation of the real Windows microphone privacy dialog or policy denial.

A later isolated founder-local pass completed onboarding and used the actual Windows Save As and Open dialogs
to round-trip a schema-7 portable profile. Live Preview was deliberately changed after export, and import restored
the exported value while preserving the profile file and machine-local state. Closing the main window then left
the exact app process responsive with no main window handle. The actual notification-area menu exposed Open,
Settings, and Exit; Open restored the same native window and Exit ended the isolated process cleanly. No product
defect was found in either path. The profile and settings lived under a bounded
`%LOCALAPPDATA%\Temp\EnviousWispr-ProfilePicker-Uat-<run>` directory and contained only default/synthetic data;
the execution policy blocked recursive deletion outside the workspace, so that directory remains eligible for
normal OS temporary-file cleanup.

After recording this evidence, canonical validation passed again: the public audit covered 379 tracked files
and 12 reviewed public audio fixtures; all 34 preserved-proof tests and all 377 production tests passed, every
Release project and UAT harness built,
and the build reported zero warnings and zero errors. Local model runtime tests were not requested by this gate.

Phase 23 is not complete. The physical journey above, signed install, update, rollback, uninstall, SmartScreen,
endpoint-security behavior, model and CUDA artifact licensing, accessibility review, representative hardware
coverage, private-beta daily use, public support operations, website claims, final release-candidate UAT, and
explicit founder approval remain open.
