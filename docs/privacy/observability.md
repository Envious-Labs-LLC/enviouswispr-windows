# Privacy-safe observability contract

This document defines the only operational diagnostics EnviousWispr for Windows may retain locally,
export, or send to an Envious Labs telemetry endpoint. The code boundary is
`PrivacySafeDiagnosticRecord`; fields not represented by that sealed typed record cannot enter the local
JSONL writer, exporter, or HTTP transport.

## Consent and destinations

- Local content-free diagnostics default on with 14-day retention. The user can choose 1–90 days or turn
  new local writes off. Expired records are pruned at startup/settings application even when local writes
  are disabled.
- Anonymous operational sharing defaults off. It begins only after the user explicitly enables and saves
  it and only when the build has a configured endpoint that passes the endpoint policy.
- Production telemetry endpoints must use HTTPS, contain no URL credentials, query, or fragment. Plain
  HTTP is accepted only for an explicitly enabled loopback UAT process.
- Consent is machine-local. Portable profiles never import or export local-diagnostic or telemetry choices.
- Turning sharing off stops new records from entering the bounded upload queue. Telemetry failure, timeout,
  queue pressure, or shutdown cannot block dictation, delivery, settings, or app exit.
- Any network receiver inherently observes connection metadata such as source IP and TLS timing even though
  those values are not product fields. Phase 22 must define server-side minimization, access, and retention
  before a production endpoint is approved.

## Allowed data dictionary

| Field | Type and bound | Purpose |
|---|---|---|
| `timestamp` | UTC/local-offset timestamp | Order lifecycle and failure events. |
| `event` | `AppEventCode` enum | Content-free lifecycle, crash, performance-stage, recovery, and delivery outcome. |
| `failure` | `AppFailureCategory` enum | Low-cardinality failure grouping. |
| `elapsedMilliseconds` | optional integer, 0–86,400,000 | Bounded stage/runtime duration. Values outside the bound are removed. |
| `provider` | optional `DiagnosticProvider` enum | EG-1, Ollama, OpenAI, Anthropic, or Gemini only. No endpoint/model string. |
| `errorCode` | optional `AppErrorCode` enum | Typed product failure, never exception text. |
| `engine` | optional `DiagnosticEngineChoice` enum | Parakeet or Whisper only. |
| `hardwareClass` | optional `DiagnosticHardwareClass` enum | Unknown, CPU-only, GPU-present, or NVIDIA-CUDA. No device name, vendor/product ID, memory size, driver version, or fingerprint. |
| `stage` | optional `DeterministicTextStage` enum | Which deterministic cleanup step a record is about: custom words, filler and false starts, spoken emoji, inverse text normalization, or emoji restoration. |
| `stageStatus` | optional `DeterministicStageStatus` enum | Completed, Skipped, TimedOut, or Failed. A skipped step is the answer to most questions asked of this pipeline, so it is reported rather than omitted. |
| `changed` | optional boolean | Whether that step altered the text. One bit, never what changed. |
| `runtimeSelection` | optional `DiagnosticRuntimeSelectionReason` enum | Which processing path a run ended up on and what put it there: the graphics card, the processor because no card was available, the processor because the user asked for it, or the processor because the card was chosen and would not start. Also the three ways selection can fail. No device name, no driver version, and never the exception text behind a failed start. |

There is no account ID, install ID, session ID, advertising ID, IP field, device name, username, path,
model ID, locale, transcript length, audio length, target-app name, or free-form string field.

`stage`, `stageStatus`, and `changed` describe the pipeline, never its contents. They are two fixed
enum members and one bit, so none of them can carry a word somebody said, and `changed` says only THAT
text was altered.

## Forbidden data

The following must never enter an `AppLogEntry`, `PrivacySafeDiagnosticRecord`, diagnostic export, or
telemetry request:

- dictated audio, preview text, raw transcript, deterministic transcript, or polished transcript;
- API keys, credentials, authorization headers, prompts, model responses, or provider response bodies;
- clipboard contents, surrounding text, selection, focused-control value, window title, process path, or
  target-app identity;
- microphone/device names and IDs, model/file paths, model IDs, user profile paths, or stable machine IDs;
- exception messages, stack traces, HTTP response bodies, or arbitrary provider strings.

Crashes are represented only by `UnhandledFailure`, a typed failure category, and an optional typed error
code. Windows may separately create operating-system crash records outside this product-controlled stream.

## Local retention and export

- The active JSONL file is capped at 5 MiB and trims to the newest valid records near 4 MiB.
- Retention pruning strictly reparses each line into `PrivacySafeDiagnosticRecord` and drops expired,
  malformed, extra-field, or unknown-enum records.
- Export never copies the raw diagnostic file. It reparses with unknown-member rejection, applies the
  current retention window, reserializes only allowed typed records, and atomically promotes the result.
- Exporting to the active source path is refused. A failed export leaves the existing destination intact or
  replaces it only with a fully written valid file.

## Review and release gate

Automated redaction tests deliberately inject transcript-shaped content into the raw source and require it
to disappear from export. The loopback UAT requires zero requests before consent and exactly one typed
request after consent. Any future field addition, crash SDK, endpoint, install identifier, sampling scheme,
or correlation token requires a new privacy review and matching allowlist/redaction tests before release.

No production telemetry endpoint is embedded in the current development build. Phase 22 owns the vetted
beta endpoint and operating policy; the Phase 18 implementation intentionally remains unavailable for
upload until that input exists.
