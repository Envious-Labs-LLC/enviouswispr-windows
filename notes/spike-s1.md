# Spike S1 — Windows ASR latency (Parakeet TDT 0.6B v3, int8)

## Status
- Running 2026-08-24 (evening). Host = the rig itself (Windows 11, i9-14900KF, RTX 4090) — the move off
  Linux made this spike locally runnable for the first time.

## Spec from the Mac source (READ, macos-source @ f9b70283)
- Backend: FluidAudio (founder fork `saurabhav88/FluidAudio` @ `bf9fe27f`) running Parakeet v3 —
  `Sources/EnviousWisprASR/ParakeetBackend.swift`.
- Model: `parakeet-tdt-0.6b-v3-coreml` (int8) from HF `FluidInference/parakeet-tdt-0.6b-v3-coreml`
  @ `aed02740`. Four compiled CoreML dirs — Encoder (445 MB weights), Decoder (23.6 MB),
  JointDecisionv3, Preprocessor — plus `parakeet_vocab.json`. Total set 483,256,769 bytes
  (`ParakeetBackend.swift` `totalDownloadMB`, `workers/parakeet-mirror/expected-manifest.json`).
- Vendor claim: "~110x real-time factor on Apple Silicon" (ParakeetBackend doc comment) — i.e. ~0.09 s
  of compute for 10 s of audio.
- Punctuation + capitalization are built into the model. No language detection: the language lock is an
  INPUT that conditions decoding, never an output (ParakeetBackend.swift `transcribe` comment).
- **Production hot path is streaming** (`Sources/EnviousWisprPipeline/ParakeetEngineAdapter.swift`):
  `startStreaming` at session start, `feedAudio` per capture buffer, `finalizeStreaming()` on release
  (with a rescue path). A one-shot batch `transcribe(audioSamples:)` also exists. Windows v1 (no
  streaming, per open question) will use the batch shape; S1 measures the batch leg.
- Capture format: 16 kHz mono float32 (`AudioCaptureManager.swift:221`, `EnviousWisprCore/Constants.swift:237`).

## Windows S1 plan
- Model: `istupakov/parakeet-tdt-0.6b-v3-onnx` — canonical ONNX pack of the same NVIDIA model
  (NeMo export + int8 QDQ quantization). Files: `encoder-model.int8.onnx` (self-contained QDQ graph,
  ~622 MB), `decoder_joint-model.int8.onnx` (~17 MB), `decoder_joint-model.onnx` (fp32, for the
  QDQ-free GPU comparison), `nemo128.onnx` (NeMo-exact log-mel, 128 bins), `vocab.txt` (8193 entries).
- Runtime: `onnx-asr` (Python, MIT) — the reference TDT inference implementation for this layout;
  avoids re-implementing the transducer decoder inside the spike. Tiers:
  - CPU: `onnxruntime` CPU EP (i9-14900KF, 32 logical)
  - GPU: `onnxruntime-directml` DML EP (RTX 4090, no CUDA dependency). CUDA EP is a follow-up if DML
    numbers are not conclusive (ORT 1.27+ wants CUDA 13; pip-installable, ~GB of downloads).
- Clips: **the founder's real dictation** from `C:\Users\saura\audio-samples` (453 recordings,
  270 MB; each dir = raw.wav + fed.wav + meta.json). `fed.wav` is what the Mac engine actually
  received (post-VAD trim) — the honest input. All 16 kHz mono PCM_16, matching the capture spec
  exactly, so no resampling is introduced.
  - `clip10.wav` ← `76F98C4A-E639-48A8-B32D-4E33F928498B` fed.wav, 10.05 s → 10.0 s, class `asr_complete`
  - `clip20.wav` ← `68B90FDC-1478-497D-AEDF-86B7EB7B5FA1` fed.wav, 21.21 s → 20.0 s, class `asr_complete`
- What is measured: the MODEL leg (warm session, 2 warmups + 7 timed runs, median). The capture leg
  (WASAPI) is a separate, small, well-understood budget and is not in this measurement; "stop → text
  lands" end-to-end will be re-measured once the Windows pipeline exists.

## Anchors (READ — not measurements)
- Mac product PostHog medians: **0.61 s** no-polish / **1.65 s** on-device polish (the promise S1 judges).
- onnx-asr published benchmarks for Parakeet TDT 0.6B V3: CPU int8 RTFx ~30.5 (Ryzen 7 9800X3D) →
  ~0.33 s per 10 s; CUDA RTFx 74.8–91.4 (RTX 5070 Ti) → ~0.11–0.13 s per 10 s; TensorRT fp16
  252–318. Sanity anchors for plausibility, not the target hardware.

## Dataset notes (MEASURED 2026-08-24, scan of all 453 fed.wav)
- Durations: min 1.0 s, **median 4.0 s**, max 94.2 s. 144 clips over 8 s.
- `asr_complete` is the majority class; `unknown` and `suspected_asr_drop` together are ~a quarter.
  (Quality telemetry for later — S2/quality work can diff Windows vs Mac on these.)
- **Design input:** clips run to 94 s while onnx-asr's single-pass input limit is ~20–30 s. A
  no-streaming Windows v1 must chunk long dictations (VAD-split or time-windowed) — the Mac's
  sliding-window streaming hides this. Feeds the "no streaming in v1" open question.

## Results
- (pending — model download + CPU/DML runs in progress)
