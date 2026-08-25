# App build — EnviousWispr for Windows (WPF + ONNX C#)

Autopilot goal: viable push-to-talk app (hotkey → 16 kHz capture → Parakeet int8 → EG-1 polish → paste).

## 2026-08-25 — C# ASR port verified (MEASURED)

`EnviousWispr.Smoke --skip-eg1` on the S1 clips, int8 QDQ pack, intra=8/inter=1, CPU:

| Clip | C# total | Python spike (ref) |
|------|----------|--------------------|
| clip10 (10 s) | 214 ms | 324 ms |
| clip20 (20 s) | 395 ms | 1197 ms |
| clip94 (91.5 s) | 4889 ms | 7245 ms (fp32 CPU) |

Text output verified fluent/correct on all three (see smoke run). C# is 1.5–3x faster than the
Python reference on the same graphs/threads — the transposed-encoder-output bug (see below)
fixed, decode loop is a line-for-line port of `onnx_asr/asr.py` L195-231 + `models/nemo.py` L104-135.

## 2026-08-25 — ONNX Runtime C# 1.22 API (MEASURED: reflection dump + execution)

- `DenseTensor<T>` lives in **`Microsoft.ML.OnnxRuntime.Tensors`**, ctor
  `(Memory<T>, ReadOnlySpan<int> dims, bool reverseStride=false)`. No `OnnxTensor.CreateFrom`
  in 1.22; `NamedOnnxValue.CreateFromTensor(name, Tensor<T>)`.
- Provider is a SessionOptions method, not a ctor array: `so.AppendExecutionProvider_CUDA(0)`.
  Ctor 3rd arg is now `PrePackedWeightsContainer`.
- `Run(inputs)` returns **ALL** graph outputs — the decoder also emits `prednet_lengths`
  (int32) at index 1. Pass explicit output names `["outputs","output_states_1","output_states_2"]`.
- Graph I/O contract (MEASURED from InputMetadata/OutputMetadata):
  - preprocessor: `waveforms` f32 [B,S], `waveforms_lens` i64 [B] → `features` f32 [B,128,T], `features_lens` i64 [B]
  - encoder: `audio_signal` f32 [B,128,T], `length` i64 [B] → `outputs` f32 [B,1024,T'], `encoded_lengths` i64 [B]
  - decoder: `encoder_outputs` f32 [B,1024,1], `targets` **i32** [B,1], `target_length` **i32** [B],
    `input_states_1/2` f32 **[2,B,640]** → `outputs` f32 [B,1,1,**8198**] (=8193 vocab + 5 step scores),
    `prednet_lengths` i32, `output_states_1/2` f32 [2,B,640]
- **Encoder output layout is [B, dim, T]** — the decode loop wants [T, dim]; transpose
  `frames[t*D+d] = raw[d*T+t]` (Python does `encoder_out.transpose(0,2,1)`). Missing this makes
  every frame garbage and the loop emits all blanks (symptom: empty text, no error).

## 2026-08-25 — .NET/Windows gotchas (MEASURED)

- `UseWPF` projects drop `System.IO` + `System.Net.Http` from implicit usings (observed in
  `*.GlobalUsings.g.cs`). Fix once in csproj: `<Using Include="System.IO" />` +
  `<Using Include="System.Net.Http" />`.
- `System.Text.Json` is case-SENSITIVE by default — camelCase appsettings needs
  `PropertyNameCaseInsensitive = true` (bug symptom: config silently null → model dir resolved to base).
- NAudio 2.2.1: `WasapiCapture(MMDevice)` + settable `.WaveFormat` (no `(WaveFormat)` ctor);
  `WaveFormat.CreateIeeeFloatWaveFormat(rate, 1)` for the 16 kHz float32 spec.
- WPF `Clipboard.SetDataObject` has only 1- and 2-arg overloads (the 3-arg copy version is WinForms).
- C# declaration-list trap: `int a = 0, b = float.NaN;` makes BOTH int.

## 2026-08-25 — cmd.exe / bg_run gotchas (MEASURED)

- `if not exist dir mkdir dir && x` = "if dir exists, skip x" (cmd parses the && chain inside the if).
  Four "successful" no-op downloads resulted (exit 0, empty output, no files).
- `%VAR%` inside a FOR block is expanded at parse time — all iterations used the pre-loop value.
  Fix: `setlocal EnableDelayedExpansion` + `!VAR!`. Symptom: 8× 404 on `eg-1-v2--of-00008.gguf`.
- bg_run runs cmd.exe; create destination dirs from the foreground (Git Bash) first and verify.
- Unresolved anomaly (08-25 00:08-00:41): `models/` + `tools/` created by Git Bash mkdir were
  gone by the time tasks ran, though the creating command had succeeded. Recreated 00:41, stable
  since. Do not re-investigate until it recurs.
- System32 curl and Git Bash curl both work against models.enviouslabs.co (200s, MEASURED).

## 2026-08-25 — sherpa-onnx 1.13.6 Python API (MEASURED from docstrings)

`OfflineRecognizerConfig(model_config=OfflineModelConfig(transducer=OfflineTransducerModelConfig(
encoder_filename=..., decoder_filename=..., joiner_filename=...), model_type="nemo_transducer",
provider="cpu", num_threads=8, tokens=..., debug=...), decoding_method="greedy_search")`.
Old kwarg names (`model=`, `encoder=`, top-level `tokens=`) are gone.

## 2026-08-25 — corpus pass status

- onnx-int8: 453 clips, 283 s, **median 274 ms, p95 2219 ms, empty=18** (corpus-results/onnx-int8.json).
- onnx-fp32: completed 4m46s (corpus-results/onnx-fp32.json) — diff pending.
- sherpa: two API failures fixed, v2 run in flight (b91c84efd).
- EG-1 shards: downloading via tools/download-eg1.bat (single wrapper, delayed expansion fixed).
  llama.cpp b10615 win-cpu-x64 extracted at tools/llama.cpp/ (llama-server.exe present).
- Port rule: never use port 8081 (Qwen control plane); project llama.cpp servers ≥8082.
  EgOneServer.FindFreePort rejects 8081 and <8082 (MEASURED in code, not yet runtime-verified).

## Open / next

- [ ] Full smoke with EG-1 (server boot + probe GREEN + polish pipeline) once shards land.
- [ ] corpus diff: int8 vs fp32 vs sherpa (s1_corpus_diff.py).
- [ ] GUI verification: overlay + hotkey + real mic capture (needs interactive session).
- [ ] Commit src/ + notes when smoke fully green.

## 2026-08-25 — sherpa engine: dead end (recorded)

`sherpa-onnx 1.13.6` + pre-converted `sherpa-parakeet-tdt-v3-int8` pack fails at load:
`offline-transducer-model.cc:InitDecoder:303 'vocab_size' does not exist in the metadata`.
The pack's decoder ONNX lacks metadata this sherpa version requires. Not worth fixing: the
ORT C# port (Option B) is verified working and faster — sherpa was only a cross-check.
API also moved twice in 1.13.x (`model_config=` kwarg, then `from_transducer()` factory;
`OfflineRecognizer()` constructor is gone).

## 2026-08-25 — EG-1 weights found locally (user hint, MEASURED)

EG-1 was trained AND runs on this PC:
- `C:\Users\saura\eg1-overnight\` — the training workspace: `eg1-polish-prompt-v1.txt`
  contains the EXACT training prompt; **byte-verified MATCH (265 chars) vs EgOnePrompt.cs and
  the Mac's pinned prompt** (its header names EGOnePromptBuilder.swift as source of truth).
- EG-1 base = **Qwen3-4B** (lora experiments + `qwen3-4b-instruct-2507-base` fallback in the dir).
- Local builds (all Q5_K_M, exactly 2,889,511,680 bytes, Jul 16): `C:\Users\saura\eg1-v3-en-Q5_K_M.gguf`,
  `eg1-v4-twins-Q5_K_M.gguf`, `eg1-v5-Q5_K_M.gguf` (latest, 20:02). Older: `gemma4e4b-polish-q4_k_m.gguf` (Jul 2).
- App config now points at `C:\Users\saura\eg1-v5-Q5_K_M.gguf` (single file, no shards —
  EgOneServer passes the path positionally; llama.cpp loads it directly).
- Mac's remote "eg-1-v2" (8 shards, ~3.2 GB total) is a different quantization than the local
  Q5_K_M builds — FLAG: Windows app runs v5; Mac runs v2. Probe + output quality will tell if it matters.
- Port 8081 = Qwen3.8-27B control plane (llama-server b10615, `C:\AI\Qwen38\`), confirmed via
  Win32_Process CommandLine — do not touch. EG-1 test server runs on a free port ≥8082.

## 2026-08-25 (post-recovery) — checkpoint resume

- Control plane renamed: `qwen38-server.exe` (PID 25164) on 127.0.0.1:8081. HARD RULE (user):
  never kill qwen38-server.exe, never touch 8081 from project work.
- INCIDENT (mine): `taskkill //F //IM llama-server.exe` to kill my 18099/18100 test servers also
  matched + killed the old control-plane process name. The `| grep -v <pid>` only filtered OUTPUT.
  Founder restarted as qwen38-server.exe. Lesson: kill ONLY exact PIDs I spawned; never /IM.
- EG-1 server start bug FOUND + FIXED (READ→MEASURED pending smoke): b10615 llama-server rejects a
  bare POSITIONAL model path with `error: invalid argument: <path>`; identical argv works with the
  `--model` flag. EgOneServer now passes `--model <path>` and logs the full argv.
- Full-corpus int8 vs fp32 (453 clips, CPU 8t): median 274 vs 326 ms; p95 2219 vs 2094 ms;
  EMPTY 18 vs 2. int8 = faster median, fp32 = far fewer empties (quantization artifacts).
  Both selectable via asr.pack in appsettings.
- EG-1 model in use: `C:\Users\saura\eg1-v5-Q5_K_M.gguf` (Jul 16 20:02, newest local build).
  serverExe: tools\llama.cpp CPU build (keeps the 4090 free for the control plane).
