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
- CPU and CUDA model selection is explicit. Automatic selection prefers NVIDIA CUDA and the measured
  quantized model, then falls back to CPU. DirectML is not claimed as a whisper.cpp provider. The
  full-precision model remains supported when it is the only complete pack, but is not preferred because
  the comparison below showed no accuracy benefit.
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
| CPU, 8 threads | 11,699 ms | 11,728 ms | 30,838 ms | accurate checks passed; not interactive |
| NVIDIA CUDA 13 | 124 ms | 217 ms | 612 ms | accurate checks passed; all below one second |

- Cancellation removed the exact active worker in 584-614 ms on both providers.
- A Release x64 WinUI run selected Whisper through the development-only environment override, started its
  own CUDA worker, exercised the registered F8 capture path, and completed final transcription in 289 ms.
  Closing the shell also removed that exact worker. This proves the native shell-to-worker path with
  content-free diagnostics; it is not evidence of spoken multilingual accuracy.
- Multilingual fixtures are public MINDS-14 recordings, committed with source revision, config/split/row,
  reference text, SHA-256, and CC-BY-4.0 attribution in `tools/whisper-uat/fixtures/manifest.json`. The corpus
  now contains row zero for French and deterministic rows 0/100/200/300/400 for German and Spanish. The UAT
  fails closed unless the exact 11-row manifest, provenance, sizes, and hashes match.

The expanded bounded CUDA experiment produced:

| Language | Automatic decode | Fixed-language decode | Decision |
|---|---:|---:|---|
| French | 1/1 rows passed; 0% aggregate WER | 1/1; 0% | pass on one row |
| German | 2/5 rows passed; language detected on 4/5; 40.38% aggregate WER | 3/5; 33.65% aggregate WER | fail individual-row guardrail |
| Spanish | 4/5 rows passed; language detected on 5/5; 20% aggregate WER | 4/5; 20% aggregate WER | row-zero outlier still fails |

The expanded automatic CPU run produced the same pass/fail decision on every multilingual row. French
passed 1/1 at 0% aggregate WER, Spanish passed 4/5 at 20%, and German passed 2/5 with language detected on
4/5 and 39.42% aggregate WER. German row zero measured 100% WER on CPU versus 112.5% on CUDA; all other
row WER values matched. The CPU run loaded in 727 ms, completed the English 10/20/91.467-second clips in
11,699/11,728/30,838 ms, and removed the cancelled worker in 595 ms.

The original Spanish row-zero result is therefore not representative of the five-row slice: rows 100, 200,
300, and 400 measured 11.76%, 0%, 0%, and 19.05% WER. The strict UAT remains red because every admitted row
must meet the 35% individual guardrail; the aggregate is evidence, not a replacement acceptance rule.

Reference quality is itself a measured risk. German row 100's source transcription and English translation
both end mid-sentence, while its 23.296-second audio produced 48 model words against a 25-word reference. The
manifest retains the authoritative source text and the row remains red, but its 100% WER cannot safely be
attributed entirely to the model. German row 200 also contains visibly noisy source wording and measured
42.11% WER. This five-row slice is useful diagnostic evidence, not a representative language benchmark.

- An earlier three-row SHA-pinned full-precision comparison used the 1,624,555,275-byte
  `ggml-large-v3-turbo.bin` artifact with SHA-256
  `1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69`. On CUDA it produced the same
  multilingual results as Q5: French 0% WER, German auto-detection failure with 100% WER, and Spanish
  52.38% WER. English inference was 128/220/655 ms, slightly slower than Q5's 124/217/612 ms. The extra
  1.05 GB therefore has no measured product benefit on this corpus.
- On the original row-zero fixtures, fixed-language decoding with both Q5 and full precision brought German
  to 25% WER, inside the guardrail, but Spanish remained at 52.38%. The expanded Q5 evidence above supersedes
  that single-row German result for current acceptance. The durable UAT can rerun these diagnostics with
  `fixed-cpu` or `fixed-cuda` and the optional `--full-precision` switch without emitting transcript text.

## Required evidence still missing

- The expanded five-row German and Spanish slice narrows the failure: Spanish passes four rows and has 20%
  aggregate WER, while German still fails three automatic rows and two fixed-language rows. Reference quality
  is questionable for at least German row 100. Phase 7 still cannot claim representative multilingual parity;
  the next useful evidence needs a larger, quality-reviewed public corpus and an explicit decision about
  per-row versus corpus-level acceptance, not a weaker threshold chosen after seeing results.
- The CPU Q5 path is accurate on the English and French samples but takes 11-32 seconds. It is a functional
  safety fallback, not a useful default on the observed desktop CPU.
- The full-precision 1.5 GiB model was measured locally and rejected as the automatic choice. It is ignored
  local evidence, not a committed binary or shipping dependency.
- AMD/Intel GPU acceleration is unobserved. The pinned wrapper supports Vulkan, but the project has not yet
  shipped or capability-probed that runtime.
- CUDA UAT currently uses a locally configured CUDA 13 runtime directory. A distributable, licensed,
  hash-pinned CUDA dependency set is still Phase 16/19 work.
- A physical-key, spoken multilingual run through the WinUI shell remains unobserved.
