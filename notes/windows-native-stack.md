# Windows-native stack — the M-series question

**Date: 2026-08-24.** Companion to `Windows Port Research.md`. Question asked by founder:
"EnviousWispr is macOS-native and maximizes the M-series architecture. What is the equivalent
play on Windows?" External stack facts verified via Microsoft docs/blogs/ONNX Runtime docs
(MEASURED web, 2026-08-24). Nothing here is measured on our models or the rig's hardware.

## Conclusion

**There is no single Windows equivalent of the M-series — and that changes the strategy.**
The M-series win was one homogeneous target: build on ANE + Metal + CoreML once, every
supported machine is fast/silent/private. Windows is a heterogeneous market (Qualcomm/Intel/AMD
NPUs, NVIDIA/AMD dGPUs, long CPU tail), so the native-maximize play is **one model graph,
OS-routed to the best silicon per workload**: NPU for always-on background, dGPU for
latency-critical bursts, CPU as the universal floor. Microsoft built that abstraction layer
in 2025–26: **Windows ML (WinML 2.0) + Foundry Local + Windows AI APIs** — the platform-side
equivalent of what CoreML gave us on the Mac.

## Pillar map (macOS pillar → Windows equivalent)

| M-series pillar (what we exploited) | Windows equivalent | Status (2026-08-24) |
|---|---|---|
| ANE — always-on low-watt ML | NPU on Copilot+ PCs (40+ TOPS: Qualcomm Hexagon / Intel AI Boost / AMD Ryzen AI) via OS-delivered EPs: QNN / OpenVINO / VitisAI | Real; ecosystem still maturing — INT8-only, opset limits, vendor quirks (ASSUMED improved since 2024; unmeasured) |
| Metal — universal GPU compute | dGPU via WinML EPs: TensorRT-RTX (NVIDIA), MIGraphX (AMD); DirectML = legacy any-DX12 path, now "sustained engineering", new dev moved to WinML | Mature |
| CoreML — one OS-native model API | **Windows ML (WinML 2.0)** — ONNX Runtime under the hood; EPs shipped via Windows Update; **device policies** (`PREFER_NPU`, `MAX_EFFICIENCY`, `MAX_PERFORMANCE`) = zero hardware-detection code, the descendant of our `.cpuAndNeuralEngine` one-liner | GA; WinML 2.0 shipped with Foundry Local 1.2.0 (2026-06-04) |
| Apple Intelligence / FoundationModels | Windows AI APIs (Phi Silica LLM, speech recognition — Microsoft-hosted, shared across apps, mostly Copilot+) + Foundry Local (GA 2026-04-09; curated ONNX catalog: Qwen, Phi, Whisper, GPT-OSS, DeepSeek, Mistral; OpenAI-compatible API incl. audio transcription) | Real, but NOT task-tuned — quality vs EG-1 unmeasured, eval-gated |
| Unified memory — no copies, big models fit | **None.** Mitigate as we already do: int8 quantization + on-demand load/unload (WarmEnginePolicy/unload-policy logic ports conceptually) | Gap, managed |
| CoreAudio HAL — deep device introspection | WASAPI — first-class capture, shallower introspection (no per-device mute property, coarser hotplug). Zero-signal/liveness machinery re-derived, not ported | Gap |
| AX — deep read/write of any app | UI Automation — biggest real gap | Unmeasured (spike S2 in `Windows Port Research.md`) |
| TCC — permission prompts | None — no prompts for UIA/SendInput/tray/clipboard. Feature; constraints are UIPI (elevated target windows) + RDP instead | Feature |

## Product decisions this implies (proposed, awaiting founder)

1. **Runtime: ONNX Runtime via WinML.** Not DirectML (sustained engineering), not hand-rolled
   provider selection. Parakeet v3 int8 ONNX graph + device policy:
   - burst/latency-critical finalize → dGPU (TensorRT-RTX; NVIDIA is the likely user-base majority — unverified)
   - always-on background (VAD, warm model) → NPU on Copilot+
   - CPU int8 = universal floor; for a 0.6B int8 model very likely sub-second on modern x86
     (ASSUMED — spike S1 measures)
2. **NPU is the strategic differentiator — the actual "M-series moment" on Windows.**
   Dictation = all-day, background, low-watt workload; Copilot+ NPU is the "fast, silent,
   battery-neutral" story transplanted. Risk: NPU paths want INT8 ONNX in a constrained opset,
   so the sherpa-onnx int8 export may need Olive re-quantization per vendor. Needs a real
   Copilot+ test machine — the rig (now native Windows: i9-14900KF + RTX 4090 24 GB,
   MEASURED 2026-08-24) can answer dGPU/CPU; it has **no NPU** (MEASURED device
   enumeration), so the NPU tier is out of reach from this machine entirely.
3. **EG-1 keeps the llama.cpp architecture** (task-tuned moat, already cross-platform).
   llama.cpp has NO NPU backend (CPU/CUDA/Metal/Vulkan/SYCL) → on Copilot+ it runs dGPU/CPU.
   EG-1 → ONNX for NPU routing = Phase 3+ experiment, not v1. Phi Silica / Foundry Local Qwen
   are provider-list *options*, not defaults — the 93.7% eval bar decides anything.
4. **Reframe the public promise.** Mac: "maximizes Apple Silicon." Windows v1:
   **"sub-second on every PC we support — best-in-class efficiency on Copilot+."** Turns
   heterogeneity into the marketing story instead of a footnote.
5. **Model sourcing:** heart model (Parakeet) stays on our own R2 mirror (#1339 reliability
   reasons, READ model-sourcing-licensing.md). Foundry Local's Microsoft-hosted catalog is a
   side-channel for limb models only — it ships Whisper, a ready answer to the Phase-2
   multilingual engine question (ASSUMED fit until benchmarked).
6. **Philosophy shift to record in architecture notes:** on Windows we pin the MODEL
   (manifest + SHA-256, our delivery machinery) but the OS owns the RUNTIME (EPs via Windows
   Update — a moving, OS-versioned target). "Pinned and deterministic" applies to one layer
   only, unlike the Mac.

## Honest gaps (do not romanticize)

- No unified memory, no ANE parity on the CPU tail — sub-second on old/weak hardware is a
  CPU-int8 measurement, not a given.
- NPU EPs via Windows Update collide with our pinned/deterministic delivery philosophy (see #6).
- UIA caret coverage is still the #1 unmeasured risk — silicon optimization cannot fix
  "text didn't land in the app."
- Everything above is READ (Microsoft docs, 2026-08-24) except the sherpa-onnx artifacts
  (MEASURED web, prior session); **nothing measured on our models or the rig's hardware yet.**

## Spike S1 update (supersedes the S1 line in `Windows Port Research.md`)

S1 now prices THREE tiers, not two: Parakeet v3 int8 finalize latency via
- dGPU (RTX 4090 24 GB — confirmed MEASURED 2026-08-24 evening),
- CPU int8 (rig CPU),
- NPU — only if a Copilot+ machine becomes available; otherwise stays ASSUMED and the
  marketing claim is scoped to dGPU/CPU tiers until measured.

## Open questions (added to founder list)

- Do we have/plan a Copilot+ test machine? If not, NPU stays out of the v1 promise.
- ~~Host GPU model for the rig (NVIDIA? which?)~~ — **RESOLVED 2026-08-24
  (evening):** RTX 4090 24 GB (nvidia-smi). NPU tier: the rig has no NPU
  (i9-14900KF, MEASURED), so a Copilot+ machine is the only path for that tier.
