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
file name in a folder of its own, so the product is unchanged. The worker runs as the preview adapter runs it:
below-normal priority and no restart, with the thread count from the same hardware probe and rule.

It refuses to publish a number it cannot stand behind:

- **A card it cannot read, or that is 50% busy before a row (the middle of five readings), is not measured**,
  and a card found busy again once the row is done discards the row (`--allow-busy-card` overrides both).
- **A row that falls back to the processor at any pass is failed**, not labelled as the card.
- **A missing fixture stops the run**: a smaller corpus would publish a different error rate.
- Memory is the worker's own peak working set, by the id the engine reports. **Video memory is an estimate**:
  Windows does not report a process's share, so it is the card's total with the model loaded less the total
  before it started, and is omitted if that goes negative.
- A median of an even count is the upper middle pass, so every reported figure is a pass that happened.

## Results, 2026-09-25

The primary Windows test PC: 32 logical processors, an RTX 4090. 8 threads, 11 archived dictations (the
longest 9.1 s), 12 fixtures, 2 repeats.

### Processor

| model | pass @ 0.5 s | pass @ 2.5 s | pass @ 5 s | over 2.5 s | WER all | WER de | WER es | cold start | peak RAM |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Whisper Tiny q5_1 | 258 ms | 261 ms | 274 ms | 0 of 66 | 23.9% | 34.6% | 15.6% | 656 ms | 187 MB |
| Whisper Base q5_1 | 547 ms | 549 ms | 594 ms | 0 of 66 | 10.5% | 11.8% | 11.5% | 768 ms | 254 MB |
| **Whisper Small q5_1 (shipped)** | 2,166 ms | 2,170 ms | 2,203 ms | 0 of 66 | 6.9% | 9.4% | 5.2% | 2,377 ms | 518 MB |
| Whisper Large v3 Turbo q5_0 | 11,711 ms | 11,725 ms | 12,169 ms | 66 of 66 | 8.9% | 12.6% | 6.2% | 12,179 ms | 786 MB |
| Parakeet TDT 0.6B v3 int8 | 40 ms | 133 ms | 256 ms | 0 of 66 | 8.5% | 8.7% | 10.4% | 1,656 ms | 1,081 MB |
| Parakeet TDT 0.6B v3 full precision | 67 ms | 129 ms | 228 ms | 0 of 66 | 7.7% | 6.3% | 11.5% | 2,651 ms | 2,787 MB |

### Card (RTX 4090)

| model | pass @ 0.5 s | pass @ 2.5 s | pass @ 5 s | over 2.5 s | WER all | cold start | video memory (estimate) | card load before |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Whisper Tiny q5_1 | 27 ms | 28 ms | 37 ms | 0 of 66 | 24.3% | 488 ms | 412 MB | 27% |
| Whisper Base q5_1 | 33 ms | 34 ms | 43 ms | 0 of 66 | 11.3% | 499 ms | 438 MB | 12% |
| **Whisper Small q5_1 (shipped)** | 55 ms | 54 ms | 72 ms | 0 of 66 | 6.9% | 581 ms | 562 MB | 14% |
| Whisper Large v3 Turbo q5_0 | 66 ms | 67 ms | 78 ms | 0 of 66 | 9.3% | 825 ms | 928 MB | 16% |
| Parakeet TDT 0.6B v3 full precision | 16 ms | 25 ms | 34 ms | 0 of 66 | 7.7% | 2,785 ms | 3,768 MB | under 30% |
| Parakeet TDT 0.6B v3 int8 | the product refuses the quantized pack on the card | | | | | | | |

English and French scored 0% for every model on one fixture each, so they are left out of the tables; the
pooled figure includes them. The Parakeet full-precision card row came from the run before the load was
sampled; it passed that run's single-reading guard.

**The controls.** The shipped Small on the processor (2.2 s) and on the card (54 to 72 ms) agree with #127's
own measurements (2.0 to 2.5 s, and 51 to 142 ms), so the bench measures what the product runs. **And the
guard earned its place**: the first run's card figures were taken while another process held the card at 100%
and 23.6 of 24.5 GB - Small read 3.5 s a pass on a 4090 - and every one was discarded.

**Small on the processor sits within about 300 ms of the cadence.** The first run counted 13 of its 66 passes
over 2.5 s; the second, at the preview's below-normal priority, counted none. Whether a pass crosses the line
depends on what else the machine is doing, which is itself the finding: there is no headroom.

## What the results say

- **On the card there is no problem to solve.** Every Whisper candidate is about 30 to 90 times inside the cadence,
  and the shipped Small is the most accurate on this corpus. Large v3 Turbo costs more memory for no accuracy
  gain here. Parakeet full precision is the fastest, but holds an estimated 3.8 GB of video memory.
- **On the processor, Parakeet was the fastest candidate at the measured windows**, about 10 to 50 times faster
  than Small. Whisper Base also stayed below the cadence in every measured pass, at about a quarter of Small's
  cost. On 247 reference words from complete fixtures, Parakeet int8 made 21 edits, Parakeet full precision 19,
  Small 17 and Base 26. These results nominate candidates for further testing; they do not establish
  short-window preview accuracy or a dependable accuracy ranking.
- **Whisper Tiny is not a preview model**: 34.6% German error, and it misheard a German fixture as garbled
  English, so the language detection a multilingual preview relies on is not dependable.
- **Large v3 Turbo on the processor is not a preview candidate** (about 12 s a pass).

## What is NOT settled yet

- **Windows of 10 and 20 seconds.** The archived dictations on this machine end at 9.1 s. Parakeet's cost grows
  with the window (40 ms at half a second, 256 ms at five, int8), so the 20-second pass is the number to get,
  from a longer real recording.
- **Accuracy is a small sample, and of whole clips**: 247 reference words over 12 fixtures, one each for English
  and French, graded on complete fixtures rather than on the short partial windows a preview actually shows.
  Enough to nominate, not to rank close candidates.
- **Language coverage.** Parakeet v3 covers 25 European languages; Whisper covers 99. A preview in a language
  outside Parakeet's set would still need a Whisper model.
- **Two engines on the processor.** When the final engine is also Parakeet, a Parakeet preview is a second copy
  of the model in a second worker, about 1 GB for int8. Whether the preview can share the final engine's worker
  is an engineering question, not measured here.
- **Other hardware.** One machine, a fast desktop processor and a top-end card. The tier the problem lives on - a
  laptop processor - is exactly the one not measured; its passes will be slower than these.

## The decision this feeds (the founder's)

Candidate tiers for follow-up, requiring longer windows, accuracy grading of partial previews, and a laptop
processor:

| tier | candidate | why |
|---|---|---|
| card | Whisper Small (as today) | well inside the cadence, most accurate here |
| processor, a language Parakeet covers | Parakeet int8 | tens to hundreds of ms a pass at near-Small accuracy on whole clips |
| processor, other languages | Whisper Base | a quarter of Small's cost, clear of the cadence |

## Reproduce

```powershell
dotnet build tools/preview-model-bench -c Release
tools/preview-model-bench/bin/Release/net10.0-windows10.0.26100.0/EnviousWispr.Preview.Model.Bench.exe `
  --extra-models <folder with ggml-tiny-q5_1.bin and ggml-base-q5_1.bin> --repeats 2
```

Tiny and Base are the public whisper.cpp files from `huggingface.co/ggerganov/whisper.cpp`; they are
downloaded for the bench only and are not shipped.
