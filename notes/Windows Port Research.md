# Windows Port Research

**Session record: 2026-08-24.** A research pass over the macOS EnviousWispr was completed on the
rig, ahead of any Windows build work. This file is the durable record of what was read, what was
verified, what was concluded, and what is still open. Future sessions: read this before re-doing
the legwork. Companion detail lives in `portability-map.md`, `api-equivalents.md`, and
`language-options.md` (same directory, same date).

**Addendum 2026-08-24 (evening):** the rig moved from Linux/WSL to **native
Windows** (Windows 11 build 26200, i9-14900KF, 64 GB, RTX 4090 24 GB, no NPU —
MEASURED; see `toolchain.md`). Everything below that refers to "the Windows
host" as a separate machine now means **this rig**; S1/S2 and the EG-1 contract
spike are locally runnable.

## What was done

1. Read the project brief (`AGENTS.md`) and the macOS knowledge base at
   `macos-knowledge/knowledge/` — INDEX, architecture, pipeline-mechanics, capability-map,
   tech-stack, asr-landscape-2026, gotchas-audio, llm-contract, eg1-operations,
   model-sourcing-licensing, feature-inventory (all `READ`).
2. Verified the snapshot source at `macos-source/` (commit `f9b70283`, 2026-08-24):
   per-module line counts and import censuses `MEASURED` with `find`/`grep`/`wc -l`.
3. Web-verified the Windows ASR landscape (2026-08-24, Exa search + k2-fsa GitHub):
   sherpa-onnx artifacts and the streaming limitation `MEASURED` (external).

## Key findings

### The codebase (MEASURED against snapshot, 2026-08-24)

- `Sources/` = **139,085 lines** of Swift across 17 modules; `Tests/` = **164,289 lines**.
  The "308,321 lines" figure in the brief/README is Sources + Tests combined.
- Module table (lines): AppKit 46,803 · Pipeline 23,730 · Services 12,650 · LLM 12,650 ·
  PostProcessing 12,379 · Audio 7,527 · ASR 7,477 · Core 7,340 · ModelDelivery 3,365 ·
  LivePreview 1,689 · Storage 1,521 · rest 1,954.
- Import census: Core has zero Apple UI/ML imports (Foundation/OSLog/Darwin/CryptoKit only).
  PostProcessing: 3 of 19 files touch Apple frameworks (`SeamCasingOracleRuntime.swift`
  = NSSpellChecker oracle; `ImportFileParser.swift`, `SmartImportSource.swift` =
  UniformTypeIdentifiers/SQLite). LLM: one CoreML import (output-safety classifier),
  one Keychain, rest raw HTTP. Audio: 9 CoreAudio files — all of it platform.

### What survives the port as artifact, not code

- **Parakeet TDT 0.6B v3** (default engine, 25 EU languages, CC-BY-4.0, self-hosted at
  `models.enviouslabs.co`): weights are portable. The macOS runtime (FluidAudio Swift/CoreML)
  is not, but **sherpa-onnx ships `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8`** (ONNX
  encoder/decoder/joiner/tokens) — `MEASURED` via k2-fsa GitHub/docs, 2026-08-24.
- **EG-1** (Qwen3-4B polish tune, Q5_K_M GGUF, 8 shards, 2.89 GB) is **already a
  cross-platform architecture**: bundled static `llama-server` (llama.cpp, MIT, pinned commit
  `fdb1db8`) on 127.0.0.1 with per-launch bearer key, `-c 16384`, q8_0 KV cache
  (`READ` eg1-operations.md). Windows = rebuild llama-server for Windows (CUDA/Vulkan), same
  shards, same HTTP API, same prompt contract.
- **Deterministic text chain** (word correction → filler → emoji → ITN → polish → emoji restore)
  and the **164K-line test corpus** that specifies it — the corpus is the durable porting spec.
- Cloud polish (OpenAI/Gemini/Claude BYOK raw HTTP), model-delivery contract (manifest-pinned,
  SHA-256, stage-then-promote), and the privacy boundary (dictated audio never leaves the
  machine) all carry over unchanged.

### What does NOT survive

- **Apple Intelligence polish** — no Windows equivalent. Consequence: Windows default local
  polish = EG-1 (our first-party model, already a provider on the Mac). Lost tier: the
  zero-download system model on macOS 26+.
- **True Parakeet streaming** — the sliding-window decoder state is a custom Swift/FluidAudio
  implementation (11s chunk / 1s hypothesis / 2s left-right context, `READ`
  pipeline-mechanics.md). sherpa-onnx v3 is **offline-only**; maintainers confirm no true
  streaming for this model (GitHub issue k2-fsa/sherpa-onnx#2918, `MEASURED` web, 2026-08-24).
- **WhisperKit** — Argmax CoreML packaging is unlicensed (`READ` model-sourcing-licensing.md);
  the underlying Whisper large-v3-turbo is MIT, so Windows uses freely-licensed GGML/ONNX
  packaging (the pattern every competitor uses). License wall dissolves off-macOS. Phase 2.
- **XPC ASR isolation** — macOS-only; Windows gets helper-process isolation for free if ONNX
  inference runs out-of-process.

### Load-bearing constraints (headline)

1. **Sub-second = transcription only** (founder decision, `READ`). Mac baseline 2026-08-21:
   0.61s median no-polish, 1.65s on-device polish (PostHog 30-day). Windows RTFx for Parakeet
   int8 is **unmeasured** — first spike on the Windows host.
2. **Caret context / cursor-aware insertion is the biggest platform gap.** AX → UI Automation
   TextPattern; Windows coverage is app-by-app and assumed weaker until measured in a top-10
   app matrix. The Mac's Tier-1 AX direct-write paste has no exact Windows counterpart —
   clipboard + SendInput (Mac Tier 2) is the Windows workhorse; clipboard-restore contract
   stays (use `GetClipboardSequenceNumber` as the changeCount analogue).
3. **Streaming is a limb, not the heart** — default mode is batch (`useStreamingASR=false`,
   `READ`). Windows v1 can ship without live transcription and keep the speed promise.
4. **No TCC permission model** on Windows; the analogues to test are UIPI (elevated target
   windows) and RDP sessions.

## Recommendation (presented to founder 2026-08-24, AWAITING CONFIRMATION)

**C# / .NET 8 + WPF** (WinUI 3 only as an aesthetic call). Rationale in
`language-options.md`: all 8 platform capabilities first-class in .NET (WASAPI via NAudio,
WH_KEYBOARD_LL for PTT, tray, clipboard, SendInput, **UI Automation is a .NET citizen**, ONNX
Runtime/DirectML, llama.cpp-over-HTTP); one language = no FFI on the sub-second heart path;
MS Store is off the table (tray + global hotkey) → custom signed updater over the existing R2
delivery machinery. Rejected: Swift-for-Windows (interop tax on the heart path, second-class
GUI toolchain, 3 failure surfaces), Rust (identical logic-rewrite cost, weaker UI story).
Honest cost: ~77K lines of logic rewritten, not compiled.

## Path forward (as proposed)

1. **Done:** the map (this file + the three companion notes).
2. **Runnable on this rig today (not yet done):** EG-1 quality contract off-Metal —
   `llama-server` on rig CPU + the Python polish eval harness vs the shipped GGUF shards, to
   confirm the 93.7% bar holds before committing to a stack.
3. **Spikes on this rig** (it IS the Windows host since the 2026-08-24 move; GPU
   confirmed RTX 4090 24 GB — MEASURED):
   - S1: WASAPI → Parakeet v3 int8 batch (DirectML/CPU) → finalization latency vs the
     sub-second promise. The load-bearing number.
   - S2: UIA TextPattern support matrix across the top ~10 target apps (Word, Outlook, Chrome,
     VS Code, Teams, Windows Terminal, …). Decides whether cursor-aware insertion ships in
     wave 1.
4. **Wave 1 (heart path):** PTT → capture → batch ASR → deterministic chain → SendInput paste
   with clipboard restore. No streaming, no Whisper, minimal tray + pill.
5. **Wave 2 (limbs):** EG-1 polish, custom words, cursor-aware repair, settings, history,
   auto-updater.
6. **Wave 3 (hard limb):** live transcription. Options: port sliding-window over ONNX /
   simulated streaming (measured slow by sherpa-onnx users) / different model (Nemotron 3.5
   Streaming 0.6B announced in the same GitHub thread — unverified) / defer. Proposal: **defer
   to v1.1**.

## Explicitly NOT verified (do not treat the above as covering these)

- No Windows runtime behaviour was measured — everything Windows-side is ASSUMED or external
  (sherpa-onnx artifacts/limitation) until S1/S2 run.
- UIA coverage claims are ASSUMED.
- DirectML performance is unknown until S1 runs. (GPU model IS now confirmed:
  RTX 4090 24 GB, MEASURED 2026-08-24 evening.)
- The Mac knowledge base is a 2026-08-24 snapshot at `f9b70283`; anything it says about
  decisions is dated. Re-verify against the snapshot source before citing, and never present
  it as the Mac's live state.
- `notes/` otherwise empty before this session: no dead ends, no toolchain notes yet.

## Open questions for the founder (2026-08-24)

1. Confirm C#/.NET + WPF (or push back with a reason).
2. Confirm: Windows v1 ships without live transcription.
3. ~~Windows host access pattern (RDP/console?) and host GPU (DirectML vs CPU call for S1)~~
   — **RESOLVED 2026-08-24 (evening):** the rig is now native Windows (spikes run
   locally); GPU is RTX 4090 24 GB (MEASURED). DirectML vs CUDA for S1 is an
   engineering call, not an access question.
