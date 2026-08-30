# Phase 5 hardware discovery and runtime isolation evidence

Measured on the founder's Windows rig on 2026-08-26. This phase remains in progress until the supported
hardware-class matrix and provider-failure fallback exit in `docs/plans/windows-master-plan.md` are
satisfied.

## Implemented so far

- Services discovers processor architecture and vendor, physical and logical core counts, physical memory,
  active graphics-vendor capabilities, DirectML runtime availability, and NVIDIA CUDA driver/device state.
  The returned contract deliberately excludes adapter names, device identifiers, paths, and user content.
- Model delivery checks the exact shared, int8, and full-precision Parakeet file sets and requires every
  selected file to be non-empty.
- ASR selects an explained safe default. NVIDIA with an initialized CUDA device and a complete QDQ-free
  full-precision pack selects CUDA. Every other supported x64 case falls back to the int8 CPU pack with
  measured thread tuning bounded to two through eight intra-op threads and one inter-op thread.
- Manual CPU and CUDA choices pass through the same probes. DirectML is explicitly rejected for this
  Parakeet decoder because the measured per-frame decoder path is incompatible with the phase-one latency
  bar; it is not silently selected merely because the DLL or a candidate adapter exists.
- A content-free, versioned JSON-line worker runs outside the app process, watches its parent, supports
  health and graceful shutdown commands, and is supervised with startup/health timeouts, process-tree
  teardown, bounded crash recovery, and typed retryable failure.
- CPU and accelerator resources have separate single-owner leases. Preview, final ASR, and local polish can
  report a typed busy result instead of oversubscribing a device.
- `tools/runtime-uat` exercises the native probes and process/resource lifecycle without emitting hardware
  names, identifiers, filesystem paths, model content, transcript text, or audio.

## Automated evidence

- The production test project passed 77 tests. Coverage includes vendor classification, partial-probe
  behavior, complete and incomplete model packs, automatic NVIDIA CUDA selection, CPU fallback and thread
  bounds, manual-provider rejection, DirectML incompatibility, worker start/health/stop, worker crash
  recovery, bounded restart failure, startup timeout, and CPU/accelerator resource isolation.
- The repository validator builds the preserved proof, production WinUI module graph, audio/hotkey/runtime
  native harnesses, portable contract tests, and the production test suite with warnings treated as errors.

## Native Windows acceptance observed

- The hardware probe completed on x64 Intel with 24 physical cores, 32 logical processors, 63.7 GiB of
  physical memory, one active NVIDIA-class adapter, DirectML available, and one initialized CUDA device.
- Both local Parakeet packs were complete. Automatic selection chose CUDA, the QDQ-free full-precision pack,
  one intra-op thread, and one inter-op thread with reason `NvidiaCudaWithQdqFreeModel`.
- The isolated worker started and answered health, was forcibly terminated, restarted under the bounded
  recovery policy with a new process identifier, and stopped without leaving its replacement alive.
- A deliberately delayed worker missed its 100 ms startup deadline, returned a typed fault, and was torn
  down. No worker process remained attached to that failed start.
- A live-preview accelerator lease blocked final ASR during a bounded wait. Releasing preview immediately
  allowed final ASR to acquire the same resource.

## Required evidence still missing

- Native discovery and explained defaults remain unobserved on AMD DirectML, Intel DirectML, integrated-only,
  CPU-only, ARM64, and NPU-equipped machines. The current product policy supports x64; ARM64 is detected and
  rejected explicitly rather than assumed compatible.
- A real ONNX Runtime provider initialization or inference failure is not yet wired to preserve audio and
  retry final ASR on the CPU fallback. Phase 6 owns the concrete Parakeet engine needed to close that path.
- Resource arbitration is proven at the contract/process level, but production ASR and local-polish workers
  do not consume these leases yet.
