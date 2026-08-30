# Phase 18 privacy-safe observability evidence

Measured on the founder's Windows rig on 2026-08-26. The durable privacy contract and field allowlist live
in `docs/privacy/observability.md`.

## Implemented

- The former local JSONL logger now serializes a sealed `PrivacySafeDiagnosticRecord`, not arbitrary app
  objects. Provider, engine, hardware class, event, failure, and error are enums. Duration is bounded to one
  day. Invalid enum values are normalized or removed before any local write, export, or transport queue.
- Crash events contain only `UnhandledFailure`, a failure category, and optional typed error code. No
  exception message, stack trace, source path, window title, or target identity enters the product stream.
- Runtime selection emits only Parakeet/Whisper and a coarse Unknown/CPU-only/GPU-present/NVIDIA-CUDA
  class. It does not emit processor name, adapter name, memory, driver, model ID, device ID, or install ID.
- Settings schema 7 adds machine-local controls for local diagnostics, 1–90 day retention, and anonymous
  sharing. Local retention defaults to 14 days; sharing defaults off. Portable profiles preserve the current
  machine's observability choices rather than importing another machine's consent.
- Local writes start only after saved settings have been loaded and applied. Opt-out stops new local writes;
  retention pruning still removes expired prior records. The file is capped at 5 MiB and retains the newest
  valid records near 4 MiB under pressure.
- Export strictly reparses every source line with unknown-field rejection, applies retention, reserializes
  only the allowlisted record, and promotes atomically. It never copies the raw source file.
- The bounded HTTP queue starts sending only after explicit sharing consent and a vetted endpoint. HTTPS is
  required except for an explicit process-local loopback UAT. URL credentials, query, and fragment are
  refused. Transport errors and queue pressure cannot block dictation or shutdown.
- Settings and Help disclose the exact fields and exclusions. In this development build the sharing toggle
  is disabled because no production endpoint is embedded; Phase 22 owns the endpoint and server policy.

## Automated and native evidence

- `powershell -ExecutionPolicy Bypass -File scripts/validate.ps1` passed after the final changes: preserved
  proof 34/34, production 315/315, and every Release build including the new observability UAT completed
  with zero warnings and zero errors.
- Production tests cover strict field shape, arbitrary-string removal by construction, duration and enum
  normalization, local opt-out, retention pruning, schema migration, machine-local profile behavior,
  endpoint policy, pre-consent silence, post-consent typed transport, and malicious extra-field export.
- The loopback transport UAT observed zero requests before consent and exactly one request after consent.
  Its six fields were a subset of the allowlist. A raw transcript-shaped injection was added to the local
  source; export returned two valid records and contained neither the sentinel nor transcript/audio/
  clipboard fields.
- An isolated native Release x64 run exposed accessible controls for local retention, local opt-out,
  anonymous sharing, and export. UI Automation showed the sharing control disabled and the explanation
  `No telemetry upload channel is configured in this development build`.
- The same native run emitted only seven typed local records: application start, settings creation, coarse
  Parakeet/NVIDIA-CUDA selection, hotkey readiness, shell shown/closed, and clean shutdown. Settings stored
  schema 7, 14-day local retention, and sharing false. The run marker ended `cleanShutdown=true` and no app
  or runtime worker remained.

## Privacy review result

The product-controlled local writer, exporter, and HTTP request body have no string field capable of
carrying dictated text, audio, keys, clipboard, surrounding context, paths, model IDs, or stable device
identifiers. Deliberate unknown-field injection is dropped by reparse rather than copied. Anonymous sharing
is both consent-gated and endpoint-gated.

Exact installed local-polish UAT later exposed one low-cardinality omission: EG-1's canonical provider ID was
not mapped to `DiagnosticProvider.EgOne`, so its readiness and attempt records omitted the provider field.
The mapping is now shared and typed, recognizes canonical and legacy EG-1 IDs, refuses unknown values, and is
covered for every provider. Founder.11 emitted provider-tagged EG-1 and Ollama readiness/start/completion records
without adding transcript, model ID, endpoint, path, prompt, or response fields.

This does not claim that a future server is policy-free: a network receiver inherently sees connection
metadata such as source IP and TLS timing. Production server minimization, access controls, deletion,
incident response, sampling, regional/data-processing terms, and vetted endpoint ownership remain Phase 22
release inputs.

## Still unobserved or intentionally unavailable

- No production telemetry endpoint exists in this build, so no external network request was made.
- The native file picker was not used to create an export; the same export service was proven directly and
  through the loopback UAT, while the native accessible export control was observed.
- Native local opt-out persistence was not clicked in this pass; settings/storage and logger behavior are
  covered by automated tests.
- A fault before settings can be loaded cannot be sent through the product-controlled stream because saved
  opt-out has not yet been established. Windows may create its own OS crash record; pre-settings crash
  policy remains a release decision rather than bypassing consent.

No transcript, audio, credential, clipboard, user file, external provider, system privacy/security setting,
protected port 8081 runtime, or unrelated model server was read or changed during this phase.
