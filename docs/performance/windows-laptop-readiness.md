# Windows performance and laptop-readiness gate

## Evidence rule

Performance claims require the exact Windows build, coarse hardware class, power source, command, model
pack, provider, and observed result. Reports stay under ignored `out/performance/` and contain only
content-free timings, resource counts, coarse hardware classes, provider outcomes, and public-fixture
quality metrics. They never contain audio, transcript text, clipboard or surrounding text, device names or
identifiers, account names, process identifiers, event names, or paths.

The budgets below are provisional engineering acceptance targets. They are not public minimum or
recommended requirements. A high-end desktop passing them does not prove that an ordinary laptop passes.

## Provisional budgets

| Area | Provisional target |
| --- | --- |
| Shell ready | Empty-profile first launch and warm p95 at or below 5,000 ms. |
| Full local runtime ready | App plus automatically selected final-ASR worker ready at or below 5,000 ms. |
| Ready memory | App plus direct child workers at or below 2 GiB combined working set. |
| Idle overhead | Normalized CPU at or below 5% and no sustained working-set growth. |
| Recording overhead | Normalized CPU at or below 15%, working-set growth at or below 256 MiB, 16 kHz mono capture at least 90% of requested duration. |
| Parakeet CPU final | 10 s at or below 3 s; 20 s at or below 5 s; 91.5 s at or below 15 s. |
| Whisper CPU final | 10 s at or below 5 s; 20 s at or below 8 s; 91.5 s at or below 20 s. |
| CPU preview | A snapshot update at or below 5 s for clips through 20 s; preview failure must not affect final transcription. |
| Cancellation | Worker cancellation and removal at or below 2 s. |
| Repeated lifecycle | 1,000 content-free recovery cycles, handle delta at or below 12, clean app exits, and no orphan worker. |
| Thermal stability | A 30-minute AC run and a 30-minute battery run with zero failed dictations, no stuck hooks or orphan workers, and final-latency p95 drift at or below 25% from the first to last quartile. |

The normalized CPU percentage divides process CPU time by elapsed time and the machine's logical processor
count. This makes results comparable across runs but is not an energy measurement. Processor current/max
frequency is only a throttling proxy. Battery discharge, package power, temperature, fan behavior, and
energy per dictation need supported laptop instrumentation before a battery-mode decision.

## Current measured cell

Measured 2026-08-26 on Windows 11 25H2 build 26200.9168, Intel 24 physical/32 logical cores, 32–63 GiB,
NVIDIA graphics with an incomplete ONNX Runtime CUDA dependency set, QHD at 100–124% scale, on AC power.
Automatic Parakeet therefore selected its tested quantized CPU path.

| Measurement | Result | Provisional budget |
| --- | ---: | ---: |
| Shell first ready | 592 ms | 5,000 ms |
| Shell warm median / p95 | 572 / 609 ms | p95 5,000 ms |
| Shell working set | 188–190 MB | 2 GiB |
| Full runtime first ready | 2,281 ms | 5,000 ms |
| Full runtime warm median / p95 | 2,275 / 2,313 ms | p95 5,000 ms |
| Full runtime combined working set | 973–975 MB | 2 GiB |
| Full runtime idle CPU | 0.195–0.439% | 5% |
| 5 s recording CPU / working-set growth | 0.058% / 7.4 MB | 15% / 256 MiB |
| Parakeet CPU, 10 s / 20 s / 91.5 s | 390 / 696 / 3,786 ms | 3,000 / 5,000 / 15,000 ms |
| Whisper CPU, 10 s / 20 s / 91.5 s | 11,760 / 11,803 / 32,249 ms | 5,000 / 8,000 / 20,000 ms |
| Preview CPU, 10 s / 20 s / 91.5 s | 2,489 / 2,723 / 7,485 ms | 5,000 ms through 20 s |
| Parakeet / Whisper / preview cancellation | 165 / 596 / 603 ms | 2,000 ms |
| Reliability cycles / handle delta | 1,000 / 9 | 1,000 / at most 12 |

## The preview budget on this page and the preview cadence in the code disagree

**Measured 2026-09-03 on the same rig, and this row needs re-deciding rather than re-running.** The
budget above allows a preview update 5,000 ms through 20 seconds; #113 has since made the loop ask for
one every 2,500 ms. So the run below passes this page and saturates the loop at the same time, and
"preview latency passes" is true only against the older of the two numbers.

Cost of one preview pass, whisper-small, the thread count `ConfigureLivePreview` picks, three real
recordings, three runs each, median:

| speech so far | 0.5 s | 1 s | 2.5 s | 5 s | 7.5 s | 10 s | 15 s | 20 s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| card | 51 | 48 | 65 | 88 | 108 | 142 | 232 | 308 |
| processor | 2,047 | 2,050 | 2,171 | 2,226 | 2,240 | 2,255 | 2,381 | 2,495 |

**The two shortest columns are the point.** On the processor, half a second of speech costs 2,047 ms -
82% of what twenty seconds costs. The cost is a floor, not a function of how long somebody has been
speaking, so the loop on a processor runs a 2.0-2.5 second pass against a 2.5 second cadence with no
headroom at any window length. On a card the same figures are a floor of 48 ms and growth to 308,
which is an eighth of the cadence and reaches nobody.

Reproduce with `tools/asr-incremental-spike`. Ref: #99.

Parakeet, shell/runtime readiness, recording, cancellation, and repeated lifecycle pass on this machine.
Whisper CPU final latency fails the provisional laptop budget even on this desktop. Its German language
detection and Spanish public-fixture quality also failed in this run. Preview latency passes, French and
German pass, and the known Spanish preview case remains at 52.38% WER. Quality failures are not relaxed to
make a performance run green.

Those language-quality statements describe the exact Phase 21 run and its then-current source references.
The later fail-closed reference audit in `notes/phase-seven-whisper.md` supersedes them for product claims:
the five-row Spanish final-Whisper slice now passes, while German remains below its individual-row gate.
The performance timings and CPU-latency conclusion above are unchanged.

## Runbook

Run the portable shell, recording, power-proxy, and repeated-lifecycle gate:

```powershell
pwsh -NoProfile -File .\scripts\run-performance.ps1 -RunLabel <coarse-machine-label>
```

On a machine with the pinned local model packs, add the full app worker, Parakeet CPU, Whisper CPU, and
preview CPU sequence:

```powershell
pwsh -NoProfile -File .\scripts\run-performance.ps1 `
  -RunLabel <coarse-machine-label>-runtime `
  -IncludeLocalRuntime
```

A nonzero exit means at least one gate failed. Do not rerun only the passing engine and describe the result
as a full pass. Attach a content-free summary to issue #47 and keep raw ignored reports on the measured
machine.

## Remaining exit cells

- Intel and AMD CPU-only laptops at 8 GiB and 16 GiB, on AC and battery.
- Intel integrated and AMD integrated/discrete graphics, plus NVIDIA laptops with and without the complete
  CUDA runtime pack.
- 30-minute thermal runs, sleep/resume, lid/topology changes, memory pressure, and battery discharge.
- Model switching during a live dictation session and preview-to-final resource handoff under load.
- Final tuning or model-tier changes for Whisper CPU; no battery-saving mode is justified yet.
