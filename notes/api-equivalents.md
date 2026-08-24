# Apple API → Windows equivalent

Every Apple dependency in the macOS app, and its Windows counterpart or "none".
Labels per the project rules. Date: 2026-08-24.

| # | macOS (what the Mac uses) | Windows counterpart | Notes / risk |
|---|---|---|---|
| 1 | Audio capture — CoreAudio AUHAL, in-process `HALDeviceInputSource`, 16 kHz mono Float32 | **WASAPI** (shared-mode capture), via C# NAudio or Rust `cpal` | Same resample story. Windows device hotplug/bluetooth routing is coarser — the whole `gotchas-audio.md` liveness/zero-signal machinery needs re-derivation, not porting. |
| 2 | VAD — FluidAudio Silero (Swift) | **Silero VAD via ONNX Runtime** (sherpa-onnx ships `silero_vad.onnx`) | Same model family. The app-level EMA/hangover/threshold logic lives in Swift and re-implements in the target lang. |
| 3 | ASR default — Parakeet TDT 0.6B v3 via FluidAudio Swift/CoreML (batch + custom sliding-window streaming) | **ONNX Runtime** (DirectML/CUDA/CPU) with `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8` | Batch: ready today (MEASURED web: sherpa-onnx artifact exists). TRUE streaming: **none** for this model on ONNX (maintainers confirm, issue #2918). See constraint 3 in portability-map. |
| 4 | ASR multilingual — WhisperKit (Argmax CoreML, unlicensed packaging) | **Whisper large-v3-turbo in MIT GGML/ONNX packaging** | Same underlying model, freely-licensed packaging (competitor pattern). Phase 2, not v1 heart. |
| 5 | On-device LLM polish — Apple Intelligence (FoundationModels) | **NONE** | No Windows equivalent. Windows default local polish = EG-1. |
| 6 | First-party on-device polish — EG-1 via bundled `llama-server` (llama.cpp) over 127.0.0.1 HTTP | **llama.cpp Windows build** (CUDA/Vulkan/CPU), same GGUF shards + HTTP API | READ eg1-operations.md: already a cross-platform architecture. The only Apple-specific part (Metal binary) is rebuilt for Windows. Quality bar must be re-verified off-Metal. |
| 7 | Cloud polish — OpenAI/Gemini/Claude raw HTTP, BYOK | Same, raw HTTP | No Apple surface. Portable. |
| 8 | Global hotkey (PTT/toggle/hands-free) — `HotkeyService` (Carbon/CGEventTap) | **`RegisterHotKey`** or low-level keyboard hook (`WH_KEYBOARD_LL`) | PTT hold-release needs the LL hook (RegisterHotKey has no key-up distinction for PTT). Modifier-only combos are the documented mode. |
| 9 | Menu-bar status icon — `NSStatusItem` | **Win32 shell notify icon** (tray), or WinUI | 5 states (idle/recording/processing/error/update) — same state machine, new rendering. |
| 10 | Clipboard — `NSPasteboard` + `ClipboardSnapshot` | **Win32 clipboard / `System.Windows.Clipboard`** | The changeCount-guarded restore contract ports 1:1 conceptually; Windows clipboard has no changeCount — use `GetClipboardSequenceNumber`. |
| 11 | Synthetic keystroke (Cmd+V) — `CGEvent` | **`SendInput`** | Same role in Tier-2 paste. |
| 12 | Text around the caret in other apps — `AXUIElement` | **UI Automation (UIA) `TextPattern` / `ValuePattern`** | BIGGEST GAP. Coverage is app-by-app; must be measured in the top-10 app matrix before promising cursor-aware insertion. (ASSUMED weaker than AX until measured.) |
| 13 | Direct text write into focused field — AX settable value (paste Tier 1) | **UIA `ValuePattern.SetValue`** (narrow, best-effort) | No exact equivalent; Windows Tier-1 is best-effort, Tier-2 (clipboard+SendInput) is the workhorse. |
| 14 | Spell/oracle for recasing — `NSSpellChecker` | **NONE first-class** | Windows has no public spellcheck oracle equivalent. `SeamCasingOracle` (is-this-a-name / is-this-English) needs a replacement or must degrade to the no-context legacy payload (which the Mac already falls back to — READ pipeline-mechanics.md FACT insertion-repair-runs-after-the-chain). |
| 15 | Accessibility permission (TCC prompt) | **NONE** (no TCC) | But UIPI (elevated target windows) and RDP are the analogues to test. |
| 16 | Auto-update — Sparkle (EdDSA appcast, DMG) | **MSIX/MS Store OR custom signed updater** (Squirrel-style over the same R2 CDN) | MS Store sandboxing conflicts with tray + global hotkey — a non-packaged custom updater over the existing verified-model-delivery machinery is the likely path. |
| 17 | Keychain (API keys) — `KeychainManager` | **Windows DPAPI / Credential Manager** | Standard. |
| 18 | Telemetry — PostHog + Sentry (Cocoa) | PostHog + Sentry **.NET SDKs** | Both exist. Same privacy boundary. |

## Where "none" is a real product decision, not a gap
- **#5 Apple Intelligence** — dropping it does NOT drop on-device polish, because EG-1 (our own model) is the first-party local provider on the Mac too. Windows default = EG-1. The only thing lost is the macOS-26 "system model, zero download" tier.
- **#14 spell oracle** — the Mac already has a no-context fallback (legacy payload). Windows v1 can launch without the oracle and keep every other seam property.
