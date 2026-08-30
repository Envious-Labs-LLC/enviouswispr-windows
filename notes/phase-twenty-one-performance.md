# Phase 21 performance evidence

Date: 2026-08-26

## Reusable gate

`scripts/run-performance.ps1` builds and tests the repository, records a privacy-safe compatibility
snapshot, measures five isolated WinUI launches, measures physical WASAPI recording overhead, and runs
1,000 reliability cycles. `-IncludeLocalRuntime` adds five full app-plus-Parakeet-worker launches followed
by public-fixture Parakeet CPU, Whisper CPU, and small-preview CPU gates. The ignored aggregate report
persists only typed, content-free metrics.

The app signals readiness through allowlisted, UAT-only named events after the shell is shown and, when
requested, after the final-ASR worker has started. The harness uses an isolated data directory, never stops
an application it did not create, samples the app and its direct children, waits for normal shutdown, and
fails if an owned worker remains.

## P1 runtime-worker packaging defect

The first full-runtime measurement found that the visible WinUI app reported `Local transcription worker
could not start` even though standalone runtime UAT passed. The build target copied stale RID output. Its
runtime descriptor expected a self-contained host, but the development output had no `hostpolicy.dll`.
After the development copy was corrected, full app runtime readiness passed.

The new packaging launch assertion then caught a second form of the same defect: self-contained publish
contained the worker executable and runtime descriptor but omitted `EnviousWispr.RuntimeWorker.dll`.
The publish target now copies the complete current RID worker file set after publish. Both the canonical
development build and self-contained package execute the worker and require the expected no-arguments exit
code. An unsigned founder 0.21.1 package passed this assertion and completed Velopack packaging. Production
signing remains unobserved.

## Measured outcome

The exact primary-rig numbers and provisional budgets are recorded in
`docs/performance/windows-laptop-readiness.md`. Highlights:

- Shell: 592 ms empty-profile ready; 572 ms warm median and 609 ms p95; about 188–190 MB.
- Full app and Parakeet CPU worker: 2,281 ms first ready; 2,275 ms warm median and 2,313 ms p95;
  about 973–975 MB combined.
- Five shell and five runtime launches exited cleanly with no orphan workers.
- Physical 5 s recording completed at 16 kHz mono with 0.058% normalized CPU and 7.4 MB working-set growth.
- Parakeet CPU passed 390 ms, 696 ms, and 3,786 ms on the 10 s, 20 s, and 91.467 s fixtures.
- Whisper CPU took 11,760 ms, 11,803 ms, and 32,249 ms and failed the provisional laptop latency budget.
- Preview CPU took 2,489 ms, 2,723 ms, and 7,485 ms; its intended through-20-second cadence passes.
- Cancellation completed in 165–603 ms with exact worker removal.
- 1,000 reliability cycles passed with handle delta 9.

Whisper CPU also failed German detection and the Spanish accuracy fixture. Small preview passed French and
German but retained the known 52.38% Spanish WER. These are reported as product blockers, not performance
noise.

## Unobserved

Only the AC-powered high-end NVIDIA desktop was measured. No-dedicated-GPU laptops, battery discharge,
energy, temperature, sustained thermal throttling, sleep/resume, live model switching, and signed package
runtime startup remain unobserved. The Phase 21 exit and public hardware requirements remain open.
