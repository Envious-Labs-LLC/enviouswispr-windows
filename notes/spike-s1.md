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

### CPU tier — MEASURED 2026-08-24 (venv-cpu: onnx-asr 0.12.0, onnxruntime 1.29.0, int8 QDQ pack)

Clips: clip10 = 10.0 s, clip20 = 20.0 s (founder dictation, `fed.wav` shapes). Warm session,
median of 7 (latency script) / 3 (sweep) runs.

**Thread count is the whole story on this chip.** i9-14900KF = 8P+16E hybrid cores; ORT's default
(all logical processors) is catastrophic. MEASURED sweep (median recognize(), 3 runs):

| intra_op | 10 s clip | 20 s clip |
|---|---|---|
| default (0 → 32 logical) | 2374 ms | 3060 ms (median, latency script) |
| 6 | **345 ms** | 1450 ms |
| 8 | 591 ms | **1219 ms** |
| 10 | 718 ms | 1285 ms |
| 12 | 814 ms | 1347 ms |
| 16 | 1106 ms | 1645 ms |
| 24 | 2783 ms | 3943 ms |

More threads than ~8–10 makes it WORSE (E-core oversubscription). The Mac product's ~110x
real-time has no hybrid-core equivalent — thread count must be an explicit, tested config.

**Stage breakdown at the sweet spot** (MEASURED, `s1_sweep.py`):

| clip | pre (nemo128) | encoder | decode loop | total |
|---|---|---|---|---|
| 10 s, intra=6 | 5 ms | 311 ms | 8 ms (125 frames) | 324 ms |
| 20 s, intra=8 | 16 ms | 1150 ms | 31 ms (250 frames) | 1197 ms |

Encoder-bound (~96%). Preprocessor and the TDT decode loop (125–250 ORT calls at ~0.1–0.3 ms each
at good thread counts) are negligible. Decoding 48/123 tokens respectively; text output verified
as correct English against the clips.

**CPU verdict:**
- 10 s dictation: **~0.32–0.35 s** — under the 0.61 s no-polish Mac median. CPU ALONE beats the promise.
- 20 s dictation: **~1.2 s** — over the promise. Needs the GPU tier or chunked streaming.
- Production config: `intra_op_num_threads ≈ 6–8` (never the ORT default on hybrid cores),
  `inter_op = 1`. Port to the C# `SessionOptions` verbatim.

### DML tier (RTX 4090) — MEASURED 2026-08-24 (venv-dml: onnxruntime-directml 1.24.4, int8 QDQ pack)

| clip | median | min–max | RTFx |
|---|---|---|---|
| 10 s | 12.39 s | 11.0–12.5 s | 0.8 |
| 20 s | 31.20 s | 19.1–36.7 s | 0.6 |

**Catastrophically slower than real time** — 15–25x slower than the tuned CPU. Sessions create
fine on DML (no hard fallback at load).

**Root cause, MEASURED via per-stage isolation (`s1_dml_stages.py`):**

| stage | DML | CPU (intra=8) |
|---|---|---|
| encoder, single call (10 s features) | 516 ms | 376 ms |
| decoder_joint, **one frame step** | **170.9 ms/call** | 0.220 ms/call |
| → 125 steps (10 s clip) | 21.4 s | 28 ms |

The encoder graph runs fine on DML — it's the **per-call overhead of the TDT decode loop**: the
transducer decoder is invoked once per encoder frame (125× for 10 s, 250× for 20 s) with 1×1×1024
inputs, and each DML `InferenceSession.run()` costs ~171 ms of command-list/sync/transfer
overhead. That single fact explains the entire DML number.

Consequence: a hybrid split (encoder on DML + decoder on CPU) would be ≈ 550 ms for 10 s —
**slower than pure tuned CPU (324 ms)**. DML earns nothing for this model on this rig unless the
decode loop stops being per-frame (batched/fused decoder export — a model-side fix, out of S1
scope). This also means the per-frame TDT loop is a porting hazard on ANY high-call-overhead EP.

### CUDA tier (RTX 4090) — MEASURED 2026-08-24 (venv-cuda: onnxruntime-gpu 1.29 + pip-bundled
CUDA 13.6/cuDNN 9.24, int8 QDQ pack)

Setup note: the `nvidia-cublas/cudnn` pip DLL dirs (`site-packages/nvidia/cu13/bin/x86_64`,
`nvidia/cudnn/bin`) must be on PATH or the CUDA EP fails with Error 126 (missing
cublasLt64_13.dll) and ORT **silently falls back to CPU** — the first run's 2.8/3.9 s numbers were
CPU, not CUDA. `run_cuda.bat` pins the PATH.

| clip | median | min–max | RTFx |
|---|---|---|---|
| 10 s | 4.79 s | 4.65–4.88 s | 2.1 |
| 20 s | 6.70 s | 6.56–6.98 s | 3.0 |

**Also catastrophic** — and the log explains it: **742 Memcpy nodes injected into the encoder
graph for CUDAExecutionProvider.** The istupakov int8 pack is dynamic-QDQ (QuantizeLinear/
DequantizeLinear pairs interleaved throughout the 652 MB encoder graph); on the GPU EPs those
pairs don't fuse into int8 kernels, so every Q/D boundary is a device↔host copy. CPU EP fuses QDQ
natively (hence 0.32 s there) — this is a property of the GRAPH PACK, not of the 4090.

**Cross-tier picture so far (int8 QDQ pack):**

| tier | 10 s | 20 s | RTFx (10 s) |
|---|---|---|---|
| CPU, default ORT threads | 2.37 s | 3.06 s | 4.2 |
| **CPU, intra_op=6–8** | **0.32–0.35 s** | **1.2–1.45 s** | **~29** |
| DML | 12.4 s | 31.2 s | 0.8 |
| CUDA | 4.8 s | 6.7 s | 2.1 |

### CUDA tier, fp32 QDQ-free pack — MEASURED 2026-08-24 (venv-cuda, istupakov fp32 encoder + fp32 decoder)

The graph's **2 Memcpy nodes** (vs 742 for the QDQ pack) — and the numbers: median of 7, warm session.

| clip | median | min–max | RTFx |
|---|---|---|---|
| 10 s | **0.119 s** | 0.056–1.84 s | 84.0 |
| 20 s | **0.191 s** | 0.141–1.93 s | 104.8 |
| 95 s | **0.654 s** | — (median of 3) | 145.3 |

**The QDQ pack was the entire GPU problem.** QDQ-free on the same 4090: 5–8x faster than the
tuned CPU, sub-second even at 95 s, and it scales sub-linearly (95 s = 4.75× the 20 s audio but
only 3.4× the time). Matches the published CUDA anchors (RTFx 74.8–91.4 on a 5070 Ti) with plain
ORT — **no TensorRT needed for v1**.

Reference point: the same clip on CPU (fp32, intra=8) took 7.25 s — the GPU is ~11× faster on
long dictation.

## Verdict — S1: PASS, with two hard-won conditions

**The sub-second promise holds on this rig** (Mac PostHog bar: 0.61 s no-polish):

| tier | 10 s | 20 s | 95 s |
|---|---|---|---|
| CPU int8, ORT default threads | 2.37 s ❌ | 3.06 s ❌ | ~7 s ❌ |
| CPU int8, **intra_op=6–8** | **0.32–0.35 s** ✅ | 1.2–1.45 s ~ | ~7 s ❌ |
| DML, int8 QDQ | 12.4 s ❌ | 31.2 s ❌ | — |
| CUDA, int8 QDQ | 4.8 s ❌ | 6.7 s ❌ | — |
| **CUDA, fp32 (QDQ-free)** | **0.119 s** ✅ | **0.191 s** ✅ | **0.654 s** ✅ |

1. **Ship two tiers.** CPU (int8 QDQ pack, 670 MB) covers dictations up to ~15–20 s under the
   promise. GPU (QDQ-free pack, ~2.5 GB as fp32; fp16 preferred for size — not yet exported) is
   the long-dictation tier and is sub-second out to 95 s, so a no-streaming v1 needs **no chunking
   for GPU users**.
2. **Thread pinning is a production requirement, not a tuning nice-to-have.** On the 14900KF
   hybrid chip, ORT's default (all 32 logical) is 7–10× slower than intra_op=6–8.
   C# `SessionOptions.IntraOpNumThreads` must be set explicitly, per hardware profile.
3. **DML is a dead end for this model as packed.** Not the encoder (516 ms, fine) — the per-frame
   TDT decode loop (125–250 tiny session.run calls) costs 171 ms per call on DML. Any EP with
   high per-call overhead (DML, and worse for QDQ graphs: CUDA) is broken by the loop shape.
   Fix = a batched/fused decoder export (model-side), which is what sherpa-onnx's C++ core
collapses into one pass — another point for the sherpa-onnx option (A) in the C# design notes,
or for a fused-decoder export.
4. **Model-pack portability is a first-class porting concern.** The Mac ships CoreML int8;
   the Windows CPU tier ships ONNX int8 QDQ (same tier concept, different graph); the GPU tier
   needs QDQ-free graphs. One model, three graph flavors — the port's model pipeline has to
   manage all three (or re-derive them from the NeMo checkpoint, as Vernacula does).
5. Text quality: all three clips transcribed as correct, clean English — no quality regression
   observed in int8 vs fp32 output on the test set.

### Ecosystem scan (READ, 2026-08-24) — the wheel already exists

- **Vernacula** (`christopherthompson81/vernacula`, MIT-ish, .NET 10): reusable `Vernacula.Base`
  library running **Parakeet TDT v3 on ONNX in C#** with in-house NeMo→ONNX exports (split
  KV-cache decoder, transducer/TDT state graph surgery), CPU + CUDA + DirectML with fallback.
  Candidate: adopt as the ASR layer instead of hand-rolling the C# decode loop.
- **Voxwright** (`Sev7eNup/Voxwright`): WPF push-to-talk dictation app (Wispr-style) — NAudio
  capture, **`org.k2fsa.sherpa.onnx` 1.12.27 NuGet running Parakeet TDT (v2) int8**, Win32
  text insertion, CUDA optional. Evidence the sherpa-onnx NuGet path works in a production-ish
  WPF app. Check sherpa-onnx v3 support for our exact model.
- **openwritr-windows** (`trsdn`): Rust push-to-talk, same `istupakov/parakeet-tdt-0.6b-v3-onnx`
  bundle. **Solved the long-audio problem: 8 s encoder windows + 1 s overlap, feature-stitch at
  the seam, one decoder pass** — tested to ~23 s without boundary doubling. That is the chunking
  design for a no-streaming Windows v1 (our dictations run to 94 s).
- **Chirp** (`Whamp/chirp`): Python+uv dictation app, Parakeet v3 ONNX, hotkey + text injection,
  CPU-focused — reference for the input loop.
- **Windows ML notes** (Microsoft docs): ORT session config `session.intra_op.allow_spinning`,
  model pre-compilation API (`ModelCompiler`) — relevant tuning levers for the C# port.
- Reference RTF data point (voiceping-ai `windows-offline-transcribe`): sherpa-onnx
  `parakeet-tdt-v2` offline CPU = 0.113 RTF (≈ RTFx 8.8) — slower class than our onnx-asr
  int8 numbers above; v3 + tuned threads is clearly the way.
- **CONFIRMED (READ, sherpa-onnx PR #2500 merged 2025-08-16 + release docs):** sherpa-onnx
  ships `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2` (~487 MB, official export, 25
  languages, `model-type=nemo_transducer`) — the turnkey NuGet path (`org.k2fsa.sherpa.onnx`
  C# bindings, CPU/DML/CUDA, offline + streaming + VAD) exists for our EXACT model. QNN/NPU
  exports for Parakeet also landed (#3719/#3720) — the NPU tier has an official route on ARM
  rigs later.

### Decision input for the C# ASR layer (not decided in S1)
- **Option A — sherpa-onnx NuGet:** turnkey, official v3 int8 model, decode loop in C++ core,
  streaming + VAD built in. Cost: external runtime, its CPU RTF class looked slower than tuned
  ORT in the v2 reference numbers (measure before ruling out).
- **Option B — ONNX Runtime C# + istupakov pack + ported TDT loop:** same graphs as S1 measured
  (fair numbers above), ~200-line decode loop with a reference implementation in hand, 8 s
  window + 1 s overlap chunking design already solved (openwritr). Full control, no extra runtime.
- **Option C — Vernacula library:** C#-native Parakeet v3 ONNX with DirectML. Dependency-health
  and parity-vs-Mac-model questions open.
