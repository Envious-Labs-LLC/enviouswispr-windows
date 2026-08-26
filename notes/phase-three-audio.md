# Phase 3 audio evidence

Measured on the founder's Windows rig on 2026-08-25. This phase remains in progress until the complete
hardware and fault-recovery exit in `docs/plans/windows-master-plan.md` is satisfied.

## Implemented so far

- The production Audio module uses `NAudio.Wasapi` 3.0.1 and the current `WasapiRecorder` builder API in
  shared mode. Default capture opts into Windows automatic default-device stream routing.
- Active capture endpoints have stable typed identifiers, display names for local UI, default state, and
  event-based add/remove/state/default change notifications. Device names do not enter normal diagnostics.
- The capture contract rejects overlap, supports cancellation, emits peak/RMS levels, and returns 16 kHz
  mono float samples. An unexpected stop returns buffered samples with a typed interruption error so a
  later pipeline can retry without losing the take.
- A pure converter covers IEEE single-precision and signed 16/24/32-bit PCM, channel mixing, clipping,
  level calculation, and linear conversion to 16 kHz mono.
- `tools/audio-uat` is a content-free native harness built by the canonical validation command.

## Automated evidence

- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/validate.ps1` passed with zero build
  warnings/errors, 34 portable proof tests, and 23 production tests.
- Converter tests cover empty input, stereo mixing, clipping, 48 kHz downsampling, 8 kHz upsampling,
  signed PCM normalization, peak/RMS values, and malformed-frame rejection.
- Device-catalog integration verifies that every returned endpoint has a stable identifier and local
  display name, is active, and that at most one endpoint is the current default.

## Native Windows acceptance observed

- Windows exposed one active capture endpoint and one default capture endpoint. The connected endpoint is
  `Microphone (Logitech BRIO)`, which Windows reports as a USB audio endpoint.
- Default-route capture completed with 31,649 samples, 1,978 ms of 16 kHz mono audio, 396 level events,
  and no error.
- Explicit selected-device capture completed with 15,649 samples, 978 ms of 16 kHz mono audio, 196 level
  events, and no error.
- A second start during active capture was rejected with the typed `CaptureAlreadyActive` error. Cancel
  then completed and left capture inactive.
- The harness printed counts, format, duration, outcome, and aggregate levels only. It did not print or
  persist audio samples.

## Required evidence still missing

- This rig has no active built-in microphone endpoint and no active Bluetooth microphone endpoint, so
  those two physical-device paths are unobserved.
- Automatic route transfer during a real default-device change and buffered-audio preservation during a
  physical device removal remain unobserved because only one capture endpoint is connected.
- Fault-injected tests for unexpected-stop preservation and notification transitions still need a
  controllable backend seam before Phase 3 can close.
