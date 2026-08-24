# Portability map — macOS → Windows

Snapshot baseline: commit `f9b70283`, 2026-08-24. All line counts below MEASURED by
`find Sources/<Module> -name '*.swift' | xargs wc -l` on the snapshot, 2026-08-24.

## Line-count correction to the brief

`macos-source/` total = **139,085** lines of Swift across 17 modules (Sources/ only).
`Tests/` adds **164,289**. The "308,321 lines" figure in AGENTS.md/README is
**Sources + Tests** — MEASURED, same command summed over the whole tree. Per-module
figures in the brief are accurate against Sources/.

| Module | Lines | Verdict |
|---|---:|---|
| EnviousWisprAppKit | 46,803 | REWRITE — AppKit/SwiftUI shell, windows, menu bar, overlay, onboarding |
| EnviousWisprPipeline | 23,730 | REWRITE (logic is portable, but the kernel is welded to the Swift concurrency model + the Audio/ASR/Services interfaces being replaced) |
| EnviousWisprServices | 12,650 | REWRITE against Windows APIs (hotkeys, paste, permissions, terminal context); settings/telemetry logic is portable |
| EnviousWisprLLM | 12,650 | PARTIAL — cloud connectors are raw HTTP (portable); Keychain → Windows DPAPI/Credential Manager; AFM connector has no counterpart; CoreMLOutputClassifier needs a replacement |
| EnviousWisprPostProcessing | 12,379 | PORTABILITY CANDIDATE — 19 files, only 3 touch Apple frameworks (see below); 164K-line test suite is the real spec |
| EnviousWisprAudio | 7,527 | REWRITE — 9 files import CoreAudio, 1 AudioToolbox, 1 CoreML; all of it becomes WASAPI + Silero-ONNX |
| EnviousWisprASR | 7,477 | REWRITE — WhisperKit + FluidAudio are CoreML/Swift; becomes ONNX Runtime |
| EnviousWisprCore | 7,340 | PORTABILITY CANDIDATE — imports only Foundation/OSLog/Darwin/CryptoKit; OSLog + Darwin are the seams |
| EnviousWisprModelDelivery | 3,365 | PORTABILITY CANDIDATE — manifest download/SHA-256/atomic cache, pure HTTP+FS |
| EnviousWisprLivePreview | 1,689 | REWRITE later — display-only recognizer; limb, not heart |
| EnviousWisprStorage | 1,521 | PORTABILITY CANDIDATE — JSON + `.ewrec` spool, portable format logic |
| rest (WhisperPreviewAdapter 497, ObservabilityCore 377, Contacts 362, ASRService 357, FluidAudioBridge 242, app 119) | 1,954 | ASRService/FluidAudioBridge deleted-with-platform; Contacts → Windows Contacts via Outlook/ICS or drop in v1 |

MEASURED import census (2026-08-24, `grep -rhoE '^import [A-Za-z]+'` per module):
- Core: Foundation 43, os/OSLog 2, Darwin 1, CryptoKit 1 — no AppKit.
- PostProcessing: Foundation 18, Core 12, os 4, UniformTypeIdentifiers 1, SQLite 1, NaturalLanguage 1, AppKit 1.
  Files with Apple imports: `SeamCasingOracleRuntime.swift` (AppKit/NSSpellChecker oracle),
  `ImportFileParser.swift`, `SmartImportSource.swift` (UniformTypeIdentifiers/SQLite).
- LLM: CoreML 1, AppKit 1, Security 1, NaturalLanguage 1, ArgmaxOSS 1 — rest raw HTTP.

## What survives across the port as ARTIFACT, not code

| Artifact | Why it ports |
|---|---|
| Parakeet TDT 0.6B v3 (default ASR, 25 EU languages, CC-BY-4.0, self-hosted at models.enviouslabs.co) | Weights are model-agnostic; the macOS runtime (FluidAudio Swift/CoreML) does not, but ONNX exports exist — see api-equivalents.md. MEASURED web check 2026-08-24: sherpa-onnx ships `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8` (ONNX, encoder+decoder+joiner+tokens) |
| EG-1 (Qwen3-4B polish tune, Q5_K_M GGUF, 8 shards, 2.89 GB) | READ: `eg1-operations.md` FACT eg1-artifact-identity / eg1-runtime-config — it ALREADY runs as a bundled `llama-server` (llama.cpp, MIT) on 127.0.0.1 with per-launch bearer, `-c 16384`, q8_0 KV. Same GGUF + same HTTP API on a Windows llama-server build. The hardest "Apple-specific" capability is already a cross-platform architecture |
| Deterministic text chain (word correction, filler, ITN, emoji, cursor repair) | Pure text logic + the 164K-line test corpus as the porting spec |
| Cloud polish connectors (OpenAI/Gemini/Claude, BYOK, raw HTTP) | No Apple surface |
| Model delivery contract (manifest-pinned, SHA-256, stage-then-promote, resumable) | Pure HTTP+FS |
| Privacy boundary | Dictated audio never leaves the machine — unchanged; Parakeet + EG-1 are both local on Windows |
| Polish quality bar (93.7% reproducible public number; eval harness + judge) | Harness is Python; runnable on any OS — ASSUMED portable until run |

## What does NOT survive

- Apple Intelligence polish (FoundationModels) — no Windows equivalent. Windows default local polish becomes EG-1 (which is our first-party model anyway) or BYOK cloud.
- Parakeet TRUE streaming — the sliding-window decoder state is a custom Swift/FluidAudio implementation (11s chunk / 1s hypothesis / 2s context, READ `pipeline-mechanics.md` FACT fluidaudio-sliding-window-api). sherpa-onnx v3 is offline-only; maintainers confirm no true streaming for this model (MEASURED web, GitHub issue k2-fsa/sherpa-onnx#2918, 2026-08-24).
- WhisperKit — Argmax CoreML packaging is unlicensed (READ model-sourcing-licensing.md); the underlying Whisper large-v3-turbo is MIT, so Windows uses freely-licensed GGML/ONNX packaging (the pattern every competitor uses). License wall dissolves off-macOS.
- XPC ASR isolation — macOS-only mechanism; Windows gets process isolation for free if ONNX inference runs in a helper process.

## Load-bearing constraints (headline findings)

1. **Sub-second = transcription only** (founder decision, READ pipeline-mechanics.md FACT subsecond…). Baseline 2026-08-21 PostHog: 0.61s median no-polish, 1.65s on-device polish (Mac, Apple Silicon). Windows RTFx for Parakeet int8 on DirectML/CPU is UNMEASURED — first thing to measure on the Windows host.
2. **Caret context / cursor-aware insertion is the biggest platform gap.** AX reading of text around the caret → UIA TextPattern. Windows UIA coverage is app-by-app and weaker in places (ASSUMED — must be measured in the top-10 app matrix before promising cursor-aware insertion). The Mac's Tier-1 paste (AX direct write, verified character-count change) has no exact Windows counterpart; the workhorse becomes clipboard + SendInput (already Tier 2 on the Mac), with the sacred clipboard-restore contract intact.
3. **Streaming is a limb, not the heart.** Default is batch (`useStreamingASR=false`, READ pipeline-mechanics.md FACT parakeet-pipeline). Windows v1 can ship without live transcription and still keep the speed promise; the streaming limb needs a real decision (port sliding window over ONNX / simulated streaming / different model / defer).
4. **No TCC-style permission model.** Windows has no accessibility-permission prompt for UIA/SendInput, but elevated-target windows (UIPI) and RDP are the analogues to test.
