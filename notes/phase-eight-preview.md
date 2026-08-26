# Phase 8 live-preview evidence

Measured on the founder's Windows rig on 2026-08-26. This phase remains in progress because the current
measurements cover one high-end desktop, not the target laptop matrix, and controlled spoken
multilingual UI evidence is still missing.

## Implemented so far

- Live preview has its own `ILivePreviewEngine` and `LivePreviewUpdate` contract. Preview text is a
  display-only type; it never becomes a final `Transcript` in the app, pipeline, history, diagnostics, or
  delivery path.
- WASAPI capture exposes a read-only, duration-bounded snapshot without consuming or mutating the final
  capture buffer. The preview loop uses at most the latest 20 seconds and ignores snapshots shorter than
  500 ms.
- The preview model is multilingual `ggml-small-q5_1.bin` from `ggerganov/whisper.cpp`, revision
  `5359861c739e955e79d9a303bcbc70fb988958b1`. The observed 190,085,487-byte file matched SHA-256
  `ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb` before use. The model remains in
  the ignored local model directory and is not committed.
- Preview starts only when the dedicated model is present. Its isolated worker runs at Windows
  `BelowNormal` priority and owns a typed CPU or accelerator lease. Missing model, busy resource, startup
  failure, or update failure disables preview without changing capture or final transcription.
- Release and cancellation stop the preview loop, remove its exact worker, release its lease, clear the
  display, and only then allow final ASR to begin.
- Diagnostics contain event names, failure categories, and elapsed milliseconds only. They do not contain
  preview text, final text, audio, model paths, or hardware identifiers.

## Runtime evidence

The same isolated worker and public fixtures used for Phase 7 measured the preview model after one warm-up:

| Provider | 3.754 s French | 5.851 s German | 10 s English | 20 s English | 91.467 s English |
|---|---:|---:|---:|---:|---:|
| CPU, 8 threads | 2,139 ms | 2,129 ms | 2,278 ms | 2,487 ms | 6,921 ms |
| NVIDIA CUDA 13 | 65 ms | 82 ms | 165 ms | 355 ms | 1,002 ms |

- French and German language detection and fixture WER passed on CPU and CUDA. The existing Spanish
  MInDS-14 fixture remained at 52.38% WER; preview is not being used to weaken the final-ASR accuracy gate.
- Cancellation removed the exact preview worker in 585-591 ms.
- A Release x64 WinUI run used a synthetic held F8 input with the real WASAPI microphone path. Preview
  started 350-363 ms after recording began and produced updates at a 2.5-second cadence. CUDA update
  inference took 57-221 ms across the observed runs.
- During recording, the app owned two isolated workers: the final Whisper worker at `Normal` priority and
  the preview worker at `BelowNormal` priority. On release, only the preview worker disappeared. Content-free
  diagnostics recorded `LivePreviewStopped` 63-70 ms after capture finalization and before
  `DictationTranscriptionStarted`; final CUDA transcription then completed in 76-254 ms.
- Closing the WinUI shell removed both exact workers. No EnviousWispr worker remained.

## Automated evidence

- Tests cover bounded non-destructive audio snapshots, typed display-only updates, disabled preview,
  resource contention, resource release before final ASR, failed startup, and cancelled startup.
- The small-model mode is durable in `tools/whisper-uat` through `--preview-small` and emits only counts,
  booleans, timings, language matches, and WER.

## Required evidence still missing

- CPU cadence is approximately 2.1-2.5 seconds on this 24-core desktop. The master-plan laptop
  responsiveness exit is unobserved and may require a smaller model, fewer threads, a longer cadence, or
  disabling preview on low-power hardware after measurement.
- The native WinUI test used synthetic F8 input and ambient microphone audio. A physical key with
  controlled spoken English and multilingual phrases, plus a visible non-empty preview assertion, remains
  unobserved.
- Preview enable/disable and cadence controls are not yet exposed in settings. The current safe behavior is
  automatic enablement only when the dedicated model pack is installed.
- AMD, Intel integrated graphics, CPU-only laptops, suspend/resume, device removal, and prolonged thermal
  behavior remain unobserved.
