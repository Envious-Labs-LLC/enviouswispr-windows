# Phase 7 Whisper final-ASR evidence

Measured on the founder's Windows rig on 2026-08-26. This phase remains in progress because multilingual
accuracy does not yet meet the acceptance guardrail and the CPU path is functional but not interactive.

## Implemented so far

- Production ASR now has a `WhisperTranscriptionEngine` behind the shared `ITranscriptionEngine` contract.
  It accepts 16 kHz mono float audio, keeps the model resident, serializes inference, returns detected
  language and bounded token timings, and never prints transcript text.
- The managed adapter is pinned to Whisper.net 1.9.1, whose native submodule is whisper.cpp commit
  `23ee03506a91ac3d3f0071b40e66a430eebdfa1d` (upstream v1.8.6). The worker packages the matching CPU and
  CUDA 13 Windows runtimes rather than resolving an unpinned system library.
- The selected model is `ggml-large-v3-turbo-q5_0.bin` from `ggerganov/whisper.cpp`, Hub revision
  `5359861c739e955e79d9a303bcbc70fb988958b1`. The observed 574,041,195-byte file matched SHA-256
  `394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2` before use.
- CPU and CUDA model selection is explicit. Automatic selection prefers NVIDIA CUDA and otherwise chooses
  the quantized CPU model. DirectML is not claimed as a whisper.cpp provider.
- Whisper runs in the existing isolated final-ASR worker. Cancellation or timeout removes only the exact
  worker process, so the current upstream limitation around mid-compute abort callbacks cannot leave a
  hidden inference running.
- Existing settings already model Parakeet, Whisper, and Automatic. Selecting Whisper now composes the
  Whisper worker; Automatic remains Parakeet until Phase 7 evidence is strong enough to change product
  selection policy. Development UAT can override the saved choice with `ENVIOUSWISPR_ASR_ENGINE=Whisper`
  without rewriting a user's settings file.

## Automated and runtime evidence

- Production tests cover complete/incomplete model packs, CPU/CUDA selection, rejected DirectML claims,
  constructor-time CPU fallback, typed missing-model failure, and detected-language preservation.
- `tools/whisper-uat` uses the same memory-map and worker protocol as the app. Its output contains provider,
  timing, counts, language-match booleans, WER, and pass/fail only. It does not emit transcript text, audio,
  model paths, or hardware identifiers.
- English fixtures passed phrase, language, timestamp, and non-empty checks on CPU and CUDA. The real Q5
  timings after one warm-up were:

| Provider | 10 s | 20 s | 91.467 s | Result |
|---|---:|---:|---:|---|
| CPU, 8 threads | 11,481 ms | 12,523 ms | 31,818 ms | accurate checks passed; not interactive |
| NVIDIA CUDA 13 | 124 ms | 217 ms | 612 ms | accurate checks passed; all below one second |

- Cancellation removed the exact active worker in 584-614 ms on both providers.
- A Release x64 WinUI run selected Whisper through the development-only environment override, started its
  own CUDA worker, exercised the registered F8 capture path, and completed final transcription in 289 ms.
  Closing the shell also removed that exact worker. This proves the native shell-to-worker path with
  content-free diagnostics; it is not evidence of spoken multilingual accuracy.
- Multilingual fixtures are public MInDS-14 row-zero recordings, committed with source revision, reference
  text, SHA-256, and CC-BY-4.0 attribution in `tools/whisper-uat/fixtures/manifest.json`.

| Language | CPU result | CUDA result | Decision |
|---|---:|---:|---|
| French | correct language, 0% WER | correct language, 0% WER | pass |
| German | wrong language, 100% WER | wrong language, 112.5% WER | fail |
| Spanish | not yet rerun on CPU | correct language, 52.38% WER | fail |

## Required evidence still missing

- German and Spanish accuracy failures exceed the 35% WER guardrail. Phase 7 cannot claim representative
  multilingual parity. The next bounded work should compare the unquantized model and fixed-language decode
  against these same fixtures before changing decode settings or the product claim.
- The CPU Q5 path is accurate on the English and French samples but takes 11-32 seconds. It is a functional
  safety fallback, not a useful default on the observed desktop CPU.
- The full-precision 1.5 GiB model is not installed or measured. Its SHA-pinned delivery belongs with the
  model-delivery phase after accuracy proves that the extra size is justified.
- AMD/Intel GPU acceleration is unobserved. The pinned wrapper supports Vulkan, but the project has not yet
  shipped or capability-probed that runtime.
- CUDA UAT currently uses a locally configured CUDA 13 runtime directory. A distributable, licensed,
  hash-pinned CUDA dependency set is still Phase 16/19 work.
- A physical-key, spoken multilingual run through the WinUI shell remains unobserved.
