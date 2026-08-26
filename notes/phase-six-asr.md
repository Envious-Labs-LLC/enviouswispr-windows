# Phase 6 Parakeet final-ASR evidence

Measured on the founder's Windows rig on 2026-08-26. This phase remains in progress because the native
CPU-only long-dictation path does not yet meet the sub-second product bar.

## Implemented so far

- Production ASR now contains the measured direct ONNX Runtime C# pipeline: NeMo 128-bin feature
  extraction, encoder-output transposition, per-frame TDT greedy decoding, vocabulary reconstruction, and
  explicit int8 CPU versus QDQ-free full-precision CUDA model selection.
- The engine implements the production `ITranscriptionEngine` contract, requires 16 kHz mono float audio,
  treats empty input as a normal empty result, keeps sessions resident, serializes inference, and exposes
  local token timestamps bounded to the original audio duration.
- ONNX `RunOptions.Terminate` is connected to cancellation. This interrupts an active encoder or decoder
  call instead of waiting for inference to return and discarding the result.
- Retryable primary-provider failure activates a lazy CPU engine and retries the same immutable captured
  audio. Results record a typed, content-free degraded error and whether fallback was used.
- Final ASR runs in `EnviousWispr.RuntimeWorker.exe`. Audio remains owned by the app and is shared through a
  short-lived named memory map; transcript content returns over the local redirected pipe and is never
  written to diagnostics. Worker loss triggers one bounded restart and retries while the map remains alive.
- The production WinUI shell now probes hardware/models, starts the isolated resident engine, sends capture
  output to final ASR on hotkey release, displays content-free state, and cancels/kills active inference on
  shutdown. Text delivery remains a later pipeline phase.
- The CUDA runtime directory and development model directory can be supplied through process environment
  variables. Installed-model lookup uses the versioned local application-data directory; no private machine
  path is committed.

## Automated evidence

- The portable production suite covers vocabulary continuity and blank-token requirements, malformed
  vocabularies, known DirectML/int8-CUDA incompatibilities, constructor-time provider fallback, runtime
  fallback on the same audio object, sticky fallback, and cancellation that never activates fallback.
- `tools/asr-uat` is model-dependent and runs from `scripts/validate.ps1 -IncludeLocalRuntime`. It emits only
  provider, duration, latency, token-count, cancellation, fallback, crash-recovery, and pass/fail metadata;
  it never prints transcript text, model paths, audio, or hardware identifiers.
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/validate.ps1 -IncludeLocalRuntime` passed
  with zero build warnings/errors, all 39 preserved proof/runtime tests, all ASR acceptance checks, and all
  86 production tests.

## Native runtime acceptance observed

Real founder clips were 10.0 s, 20.0 s, and 91.467 s. Each provider received one warm-up before the timed
run. Required phrases were checked in memory for the 10 s and 20 s clips but not emitted.

| Provider and pack | 10 s | 20 s | 91.467 s | Result |
|---|---:|---:|---:|---|
| CPU int8, 8 intra-op / 1 inter-op | 398 ms | 698 ms | 3,842 ms | accurate checks passed; long clip above bar |
| NVIDIA CUDA, fp32 QDQ-free | 53 ms | 172 ms | 436 ms | accurate checks passed; all clips below one second |

- CPU and CUDA returned non-empty long-clip text and monotonic, in-bounds token timings. Token counts were
  48/123/332 on CPU and 48/127/339 on CUDA for 10 s/20 s/long respectively.
- Cancellation was observed in 178 ms on CPU and 144 ms on CUDA.
- The isolated CUDA worker returned the expected 10 s phrase. Killing only its exact PID during long-clip
  inference caused a new worker to load and retry the parent-held audio successfully in 2,967 ms total.
- Closing an engine during active long-clip inference cancelled the request and removed the worker in 541 ms.
- Starting with a deliberately absent CUDA runtime directory selected the real CPU model, transcribed the
  expected phrase, and marked `RuntimeProviderUnavailable` plus `UsedFallback=true`.
- The Release x64 WinUI app launched its worker as a child process, accepted a generated F8 capture, showed
  `No speech detected`, and recorded `DictationTranscriptionCompleted` in 742 ms with failure category
  `None`. Closing the window removed both app and worker processes.

## Required evidence still missing

- CPU-only long dictation is 3.7-4.6 s after release on the observed rig, so Phase 6 cannot claim the master
  plan's long-clip latency exit. Prior batch chunking was measured and rejected because repeated encoder
  setup erased the gain. Issue #16 also tested capture-time incremental work using independently decoded
  8-second windows with 1-second overlap and word-anchor seam merging. It reduced simulated release work to
  254 ms on the 20-second clip and 308 ms on the 91.467-second clip, but changed 20.97% of reference words
  on the short smoke clip (the long clip changed 4.57%). That exceeds the experiment's 10% quality guardrail,
  so this implementation is rejected and was not connected to capture or the runtime worker. Closing the
  latency exit now requires a different streaming-capable export/decoder design, stronger seam alignment,
  or a product decision that defines a separate CPU-only long-dictation bar.
- The current corpus checks prove known phrases and non-empty long output, not full word-error-rate parity
  against a human reference corpus. The historical 453-clip outputs have no complete independent ground
  truth and therefore cannot honestly establish WER.
- Token times are aligned to encoder frames. Human timestamp tolerance has not been scored.
- The UI path observed generated F8 and live microphone capture with no speech. A person speaking into the
  production WinUI build and inspecting the resulting transcript remains unobserved.
