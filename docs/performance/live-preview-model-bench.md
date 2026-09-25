# Live preview model bench

Ref: #127. Tool: `tools/preview-model-bench`. First run: 2026-09-25, the primary Windows test PC.

## Why this exists

Live preview asks for an update every 2,500 ms. On the processor the shipped preview model, Whisper Small,
costs about two seconds a pass whatever the window, so there is no headroom (#127). The founder's direction
(2026-09-05) is to choose the preview model by measuring candidates across hardware tiers, and to expect a
different model per tier, rather than to tune a cadence around one model.

## What it measures, and how

For each candidate on each provider, through the product's own path - `RuntimeWorkerTranscriptionEngine`
and the out-of-process runtime worker, with the thread count live preview picks:

- **Per-pass cost** at the window lengths the preview loop sees (0.5, 2.5, 5, 10, 20 s), on real archived
  dictations, median of the repeats. No padding or looping: a window is measured only where real audio
  reaches it (the reason is in `tools/asr-incremental-spike`).
- **Accuracy**: word error rate against the public MINDS-14 fixtures in `tools/whisper-uat/fixtures`
  (the evaluation reference where the manifest has one), per language and pooled.
- **Cold start**: starting the worker and the first 2.5-second pass.
- **Memory**: the worker's peak working set; on the card, the card's memory before start and after load.

A Whisper candidate other than the shipped preview model is handed to the worker under the preview model's
file name in a folder of its own, so the product is unchanged. **A card that something else is using is
refused by name** (`card busy ... not measured`) unless `--allow-busy-card` is passed.

## Results, processor, 2026-09-25

8 threads, 32 logical processors, 11 archived dictations (the longest 9.1 s), 12 fixtures, 2 repeats.

| model | pass @ 0.5 s | pass @ 2.5 s | pass @ 5 s | over 2.5 s | WER all | WER de | WER es | cold start | peak RAM |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Whisper Tiny q5_1 | 264 ms | 261 ms | 314 ms | 0 of 66 | 23.9% | 34.6% | 15.6% | 736 ms | 189 MB |
| Whisper Base q5_1 | 543 ms | 566 ms | 557 ms | 0 of 66 | 10.5% | 11.8% | 11.5% | 720 ms | 253 MB |
| **Whisper Small q5_1 (shipped)** | 2,088 ms | 2,159 ms | 2,511 ms | 13 of 66 | 6.9% | 9.4% | 5.2% | 2,282 ms | 520 MB |
| Whisper Large v3 Turbo q5_0 | 11,796 ms | 11,860 ms | 11,757 ms | 66 of 66 | 8.9% | 12.6% | 6.2% | 11,395 ms | 787 MB |
| **Parakeet TDT 0.6B v3 int8** | **42 ms** | **134 ms** | **251 ms** | 0 of 66 | 8.5% | 8.7% | 10.4% | 1,918 ms | 1,089 MB |

English and French scored 0% for every model, on one fixture each, so they are left out of the table;
the pooled figure includes them.

The shipped Small figures agree with #127's own measurement (2,047 to 2,495 ms), which is the control
that the bench measures what the product runs.

## What the processor results say

- **Parakeet is the only candidate that is both fast and accurate on the processor.** It is 10 to 50 times
  cheaper per pass than Small at these windows, with pooled accuracy between Small and Base, and the best
  German of any candidate. Unlike every Whisper model its cost grows with the window (42 ms at half a
  second, 251 ms at five), so the 20-second figure matters and is not yet measured.
- **Whisper Base is the Whisper answer**: about a quarter of Small's cost, clear of the cadence
  everywhere, at 10.5% pooled error against Small's 6.9%.
- **Whisper Tiny is not a preview model.** Its German error is 34.6%, and it misheard a German fixture as
  garbled English, so the language detection a multilingual preview relies on is not dependable.
- **Large v3 Turbo on the processor is not a preview candidate at all** (about 12 s a pass).

## What is NOT settled yet

- **The card.** The first run's card figures were taken while another process held the card at 100% and
  23.6 of 24.5 GB, and are discarded; that run is why the bench now refuses a busy card. #127's earlier card
  measurement of Small (51 to 308 ms) stands until a clean re-run. Parakeet on the card fell back to the
  processor: onnxruntime's CUDA files are not on this machine, the known gap on #163.
- **Windows of 10 and 20 seconds.** The archived dictations on this machine end at 9.1 s. Parakeet's growth
  with length makes the 20-second pass the number to get, from a longer real recording.
- **Accuracy is a small sample**: 12 fixtures, one each for English and French. Enough to rank bands, not
  to split close candidates.
- **Language coverage.** Parakeet v3 covers 25 European languages; Whisper covers 99. A preview in a
  language outside Parakeet's set would still need a Whisper model.
- **Two engines on the processor.** When the final engine is also Parakeet, a Parakeet preview is a second
  copy of the same model in a second worker, about 1 GB. Whether the preview can share the final engine's
  worker is an engineering question, not measured here.

## The decision this feeds (the founder's)

A per-tier choice the numbers support, to be confirmed by a clean card run and a 20-second window:

| tier | preview model | why |
|---|---|---|
| card | Whisper Small (as today) | pending the clean re-run; #127 measured it comfortably inside the cadence |
| processor, language Parakeet covers | Parakeet int8 | tens to hundreds of ms a pass at near-Small accuracy |
| processor, other languages | Whisper Base | a quarter of Small's cost, clear of the cadence |

## Reproduce

```powershell
dotnet build tools/preview-model-bench -c Release
tools/preview-model-bench/bin/Release/net10.0-windows10.0.26100.0/EnviousWispr.Preview.Model.Bench.exe `
  --extra-models <folder with ggml-tiny-q5_1.bin and ggml-base-q5_1.bin> --repeats 2
```

Tiny and Base are the public whisper.cpp files from `huggingface.co/ggerganov/whisper.cpp`; they are
downloaded for the bench only and are not shipped.
