# Language & stack — costs and a recommendation

Date: 2026-08-24. Baseline: 139,085 lines of Swift in Sources/ (MEASURED), of which
~62K (AppKit 46.8K + Audio 7.5K + ASR 7.5K) is a rewrite on EVERY stack because it
is welded to Apple UI/audio/ML frameworks. The decision is about the remaining ~77K
(Pipeline 23.7K, Services 12.7K, LLM 12.7K, PostProcessing 12.4K, Core 7.3K,
ModelDelivery 3.4K, Storage 1.5K, LivePreview 1.7K, misc 0.8K) and about the stack
that owns the 8 platform capabilities.

## The 8 capabilities, per stack

| Capability | C# / .NET 8 (WPF/WinUI) | Rust | Swift-for-Windows |
|---|---|---|---|
| WASAPI capture | first-class (NAudio) | first-class (cpal) | weak — no first-class audio on Swift/Windows |
| Global hotkey (PTT) | first-class (RegisterHotKey / WH_KEYBOARD_LL P/Invoke) | fine (winapi) | fine (winapi via FFI) but thin tooling |
| Tray icon | first-class (WinForms NotifyIcon / WinUI) | fine (tray-icon crate) | weak |
| Clipboard | first-class (System.Windows.Clipboard + GetClipboardSequenceNumber) | fine | fine |
| Synthetic paste (SendInput) | first-class (P/Invoke) | fine (winapi) | fine (FFI) |
| Caret context (UIA TextPattern) | **first-class — UIAutomation is a .NET citizen** | fine (uiautomation crate, lower level) | weak |
| On-device ASR (ONNX Runtime) | first-class C# API, DirectML provider | first-class (onnxruntime + sherpa-onnx C ABI) | exists but second-tier on Windows |
| On-device LLM (llama.cpp HTTP) | n/a — it's HTTP, same in any stack | n/a | n/a |

## The real cost difference

The logic modules (~77K) are portable Swift in principle (PostProcessing has 3 Apple-import
files; Core has none). Two ways to treat them:

**Option A — Swift-for-Windows core + C# UI shell.**
Compile the pure-logic modules for Windows; write AppKit's 46.8K as WPF/WinUI in C#;
bridge the two. Cost:
- The PCM hot path (capture → ASR → the sub-second promise) crosses an FFI boundary
  (Swift Core + C# audio/UI). That is EXACTLY the path the product promise sits on.
- Apple's Windows Swift toolchain is officially a server/CLI story; there is no
  production-grade GUI path and no Xcode-equivalent. CI, debugging, and the whole
  agent-driven workflow run on a second-class toolchain for 60%+ of the new code.
- You end up with THREE failure surfaces: Swift core, C# shell, and the interop layer.

**Option B — C# / .NET 8 native, rewrite the logic.**
One language owns all 8 capabilities first-class. The ~77K of logic is rewritten,
BUT the 164K-line test suite is the real specification: port the test corpus alongside
the logic and the logic is checked against the product's actual invariants, not against
a memory of what it did. The kernel/Services/LLM are welded to the platform seams being
replaced anyway, so "compile the Swift" saves less than it looks.

**Option C — Rust.** Strong ASR/audio (cpal + ONNX + sherpa-onnx), but the weakest UI
story for THIS product shape (tray + borderless topmost pill + settings + per-app UIA).
The logic-rewrite cost is identical to Option B; it buys nothing over B on the heart path
and costs more on the UI.

## Recommendation

**C# / .NET 8, WPF** (WinUI 3 if the founder wants the modern look — flag as a
separate aesthetic call, not a capability one).

Why it wins:
1. Every one of the 8 hard capabilities is a first-class .NET/Win32 story, including the
   two that decide the product (sub-second ASR via ONNX Runtime/DirectML, and caret
   context via UI Automation, which is a .NET citizen).
2. One language for the whole app — no FFI on the heart path, no second-class toolchain.
3. Matures into packaging/updating: a custom signed updater over the existing R2
   verified-delivery machinery (MS Store is out — tray + global hotkey).
4. The "port" is really "re-implement the logic against the ported test corpus" —
   and the test corpus (164K lines) is the durable asset that encodes the product's
   invariants. That work is identical in C# and Rust; C# wins on UI.
5. Agent-driven development (Claude Code) is equally strong in C#, so the team's
   existing workflow carries over without a toolchain gamble.

What this costs (be honest): the ~77K of logic is re-written, not compiled. That is the
price of a native Windows stack with no interop tax on the heart path.

## Decision for the founder (open-questions)
- Confirm C#/.NET + WPF (vs WinUI 3) — or push back with a reason.
- Streaming (live transcription) limb: ship Windows v1 WITHOUT it and keep the batch
  speed promise, or invest in a real streaming story (port sliding-window over ONNX /
  simulated streaming / a different model). Recommend: **defer streaming to v1.1**;
  it is a limb, not the heart.
- Multilingual (Whisper) engine: Phase 2, MIT GGML/ONNX packaging, not v1 heart.
