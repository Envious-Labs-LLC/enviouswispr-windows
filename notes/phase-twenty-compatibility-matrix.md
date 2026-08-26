# Phase 20 compatibility evidence

Date: 2026-08-26

## Reusable runner

`scripts/run-compatibility.ps1` now creates an ignored JSON report for one coarse machine label. It runs the
canonical repository validation, a privacy-safe machine probe, physical WASAPI capture, synthetic global
hotkey behavior, and isolated runtime recovery. `-IncludeLocalRuntime` adds the real model-dependent gates.
The runner continues after individual failures so one broken provider does not erase evidence from other
stages, then returns nonzero if any stage failed.

The machine probe records only product-relevant classes: exact Windows build/revision, architecture, CPU
vendor and core counts, a memory tier, active graphics vendors, DirectML/CUDA capability, completeness of
the pinned ONNX Runtime CUDA dependency set, display count/scale/resolution tiers, capture-device count,
default-device presence, endpoint-security provider count, and probe status. Tests reject string fields and
property names that could hold device names, identifiers, paths, accounts, text, audio, or clipboard data.

## Primary-rig results

The first complete non-model run passed:

- Windows 11 25H2 build 26200.9168, x64.
- Intel, 24 physical cores and 32 logical processors, 32–63 GiB memory tier.
- One active NVIDIA adapter and CUDA device; DirectML available.
- QHD single display at the 100–124% scale tier.
- One capture device and one default capture device.
- Endpoint-security query available with one registered provider.
- Canonical validation passed 34 preserved-proof and 346 production tests.
- Default and explicitly selected physical microphone capture produced 16 kHz mono audio, level events,
  overlap refusal, and clean cancel.
- Global hotkey installation, conflict detection, hold/release, cancel, and teardown passed.
- Runtime worker start, forced crash recovery, clean stop, startup-timeout rejection, and resource arbitration
  passed.

## Model-dependent finding

The real CPU Parakeet gate passed. The 10 s and 20 s clips completed in 405 ms and 711 ms; the 91.467 s clip
completed in 3,758 ms. CUDA failed before inference because ONNX Runtime 1.29 requires CUDA 13/cuDNN 9 and
`cublasLt64_13.dll` was absent. The NVIDIA driver alone was therefore a false-positive capability signal.

The production probe and automatic Parakeet selector now require the complete pinned DLL-name set before
choosing CUDA. On this machine the new probe reports driver available, dependency set unavailable, and
automatic provider CPU. CPU fallback and isolated worker recovery pass. Issue #45 remains open for delivery
of a licensed, signed, hash-pinned CUDA runtime pack and a rerun of explicit CUDA acceptance.

The final hardened non-model rerun passed 34 preserved-proof and 350 production tests, the exact
26200.9168 revision probe, physical microphone capture, global hotkey behavior, and isolated runtime
recovery. A malformed CUDA search-directory test also confirms that untrusted `PATH` state fails closed.

## Unobserved

Only one high-end NVIDIA desktop is available in this run. AMD, Intel integrated, CPU-only laptop, low-memory,
multi-display, mixed-DPI, Bluetooth, privacy-denied, sleep/resume, third-party endpoint security, current
Windows 11 24H2/26H1, real target-app delivery, and signed release-lifecycle cells remain unobserved. No
minimum or recommended hardware requirement can be inferred yet.
