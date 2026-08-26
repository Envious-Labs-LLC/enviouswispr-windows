# Phase 7 Whisper final-ASR evidence

Measured on the founder's Windows rig on 2026-08-26. This phase remains in progress because the bounded
German corpus does not yet meet the acceptance guardrail and the CPU path is functional but not interactive.

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
  now contains row zero for English and French and deterministic rows 0/100/200/300/400 for German and
  Spanish. The UAT fails closed unless the exact 12-row manifest, provenance, sizes, hashes, and reviewed
  evaluation-reference fields match.

The expanded bounded CUDA experiment produced:

| Language | Automatic decode | Fixed-language decode | Decision |
|---|---:|---:|---|
| English | 1/1 rows passed; 0% evaluation WER | 1/1; 0% | pass on one row |
| French | 1/1 rows passed; 0% aggregate WER | 1/1; 0% | pass on one row |
| German | 3/5 rows passed; language detected on 4/5; 13.39% aggregate WER | 4/5; 7.87% aggregate WER | fail individual-row guardrail |
| Spanish | 5/5 rows passed; language detected on 5/5; 6.25% aggregate WER | 5/5; 6.25% aggregate WER | pass on this bounded slice |

The expanded automatic CPU run produced the same pass/fail decision on every multilingual row. French
passed 1/1 at 0% aggregate WER, Spanish passed 5/5 at 6.25%, and German passed 3/5 with language detected on
4/5 and 12.60% aggregate WER. German row zero measured 100% WER on CPU versus 112.5% on CUDA; all other
row WER values matched. The CPU run loaded in 504 ms, completed the English 10/20/91.467-second clips in
12,105/12,241/31,429 ms, and removed the cancelled worker in 596 ms. Fixed-language CPU matched the Q5 CUDA
row decisions and aggregate WER exactly.

Spanish rows 0, 100, 200, 300, and 400 measured 0%, 11.76%, 0%, 0%, and 19.05% evaluation WER. The strict
UAT remains red only because German rows 0 and 200 fail automatic mode and row 200 fails fixed-language mode;
every admitted row must meet the 35% individual guardrail, so aggregate WER is evidence rather than a replacement
acceptance rule.

Reference quality is itself a measured risk. German row 100's source annotation ends mid-sentence near 11.6
seconds even though its audio continues to 23.296 seconds. Q5 and full precision, in automatic and fixed-language
modes, independently produced the same complete 48-word transcription with tokens spanning the remaining audio.
Spanish row zero has the same bounded defect: its source annotation ends near 10.1 seconds even though the audio
continues to 17.067 seconds, and both packs in both modes independently produced the same complete 32-word result.
The manifest preserves both authoritative source annotations verbatim and separately records the complete reviewed
evaluation transcriptions plus typed reference-status markers. The public audit fails closed on any drift or use of
such a correction on another row.

German row 200 remains visibly noisy and ambiguous: Q5 measured 42.11% WER and full precision measured 36.84%
against the unchanged source annotation, so it remains red. Automatic language detection also labels the short
German row zero as Icelandic; explicitly selecting German brings that row to 25% WER, inside the guardrail. There
was no safe confidence rule in the observed language scores that could correct this automatically without creating
new errors. This five-row slice is useful diagnostic evidence, not a representative language benchmark.

English row zero exposed the same source-reference problem in a form that could be resolved without discarding
the row. The authoritative annotation ends after `partner` at approximately 7.0 seconds, but the admitted audio
continues to 10.837 seconds. Parakeet CPU, Whisper Q5 CPU, Whisper full-precision CPU, fixed English, and automatic
language all independently produced the same additional seven-word question over the remaining timestamped
audio. The manifest preserves the source transcription verbatim and separately records the complete reviewed
evaluation transcription plus a typed reference-status marker. Both Whisper packs then measure 0% evaluation
WER. The German and Spanish reviewed reference corrections now use the same fail-closed mechanism.

- The expanded SHA-pinned full-precision comparison used the 1,624,555,275-byte
  `ggml-large-v3-turbo.bin` artifact with SHA-256
  `1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69`. It produced the same row-level
  acceptance decisions as Q5: Spanish 5/5, German 3/5 automatic, and German 4/5 fixed. Its CUDA automatic
  aggregate WER was 11.81% German and 7.29% Spanish, versus Q5's 13.39% and 6.25%; neither pack resolved the
  two German blockers. English inference was 123/211/618 ms versus Q5's 119/207/592 ms in the current run.
  The extra 1.05 GB therefore has no measured product benefit on this corpus. The durable UAT can rerun these
  diagnostics with `fixed-cpu` or `fixed-cuda` and the optional `--full-precision` switch without emitting
  transcript text.

## Required evidence still missing

- The expanded five-row Spanish slice passes every admitted row in automatic and fixed-language modes after the
  truncated row-zero source annotation was reviewed. German still fails two automatic rows and one fixed-language
  row. Phase 7 still cannot claim representative multilingual parity; the next useful evidence needs a larger,
  quality-reviewed public corpus, particularly for short German speech and the noisy financial-support recording.
- The CPU Q5 path is accurate on the English and French samples but takes 11-32 seconds. It is a functional
  safety fallback, not a useful default on the observed desktop CPU.
- The full-precision 1.5 GiB model was measured locally and rejected as the automatic choice. It is ignored
  local evidence, not a committed binary or shipping dependency.
- AMD/Intel GPU acceleration is unobserved. The pinned wrapper supports Vulkan, but the project has not yet
  shipped or capability-probed that runtime.
- CUDA UAT currently uses a locally configured CUDA 13 runtime directory. A distributable, licensed,
  hash-pinned CUDA dependency set is still Phase 16/19 work.
- A physical-key, spoken multilingual run through the WinUI shell remains unobserved.
