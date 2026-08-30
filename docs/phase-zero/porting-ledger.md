# Phase Zero porting ledger

This ledger records what Windows inherits from the shipping macOS project, what changes for Windows, and
what is deliberately left behind. It prevents both reinvention and blind copying.

## Retain unchanged in meaning

| Lesson | Windows treatment |
|---|---|
| Audio stays local | Product invariant and testable privacy boundary |
| AI is a limb, not the heart | Deterministic output remains useful when every AI provider fails |
| Deterministic processing order | Port as fixtures before feature expansion |
| Emoji protection and restoration | First-class pipeline behavior, not prompt luck |
| Freeze the target at recording start | Prevent accidental delivery after focus changes |
| Last-successful-result fallback | Never discard a usable dictation because a later stage failed |
| Content-free diagnostics | Logs and telemetry cannot contain dictated or surrounding text |
| Model manifest and hash checks | Required for every downloaded local model or runtime |
| Native UAT | Real hotkey, microphone, focus, and insertion proof is required |
| Evidence labels | Say measured, read, or assumed; include the command or source |
| GitHub issue and PR trail | Durable decisions and work stay reviewable |
| Fresh-agent readability | The repository brain must let a new agent work safely without oral history |

## Adapt for Windows

| macOS shape | Windows counterpart |
|---|---|
| Swift, SwiftUI, AppKit | C#, WinUI 3, Windows App SDK |
| Keychain | Windows Credential Manager |
| Core Audio capture | WASAPI capture |
| WhisperKit | Pinned `whisper.cpp` runtime |
| FluidAudio Parakeet | Direct ONNX Runtime C# baseline, with measured alternatives only |
| Core ML execution | ONNX Runtime, `whisper.cpp`, and `llama.cpp` providers |
| Apple Intelligence | No equivalent; use EG-1, Ollama, or BYOK cloud polish |
| AXUIElement | Windows UI Automation |
| CGEvent and NSPasteboard | UI Automation, clipboard, and narrowly scoped `SendInput` |
| Menu bar item | Windows system tray |
| Sparkle | Velopack |
| Apple code signing and notarization | Windows code signing and SmartScreen reputation |

## Leave behind

- AppKit, SwiftUI, Xcode, Swift package, sandbox, translocation, and macOS permission mechanics.
- Apple-only model APIs and hardware assumptions.
- Timing constants measured only on Apple silicon.
- Old incident narratives that no longer encode a reusable rule.
- Duplicate rules, giant context dumps, and machine-specific personal instructions.
- Contacts integration, which is explicitly outside agreed Windows parity.

## Decision test

Before porting a macOS mechanism, ask: is this product behavior, a reliability lesson, or only an Apple
implementation detail? Port the first two. Replace the third with the native Windows answer and prove it on
Windows.
