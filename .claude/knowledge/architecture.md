# Architecture contract

## Preserve the proof, build the product

The current WPF and .NET 8 application is a founder-tested vertical slice. Keep it runnable as a reference
until each capability has production replacement evidence. Do not rewrite working behavior all at once.

The production target is C# on the current .NET LTS with WinUI 3 and Windows App SDK. Model engines may
use pinned native libraries behind small C# interfaces. Direct distribution uses a self-contained Windows
build so customers do not need to install developer tooling.

## Module boundaries

- `App`: composition, lifecycle, tray, onboarding, and WinUI views.
- `Core`: shared value types, settings contracts, errors, and session state.
- `Audio`: WASAPI capture, device selection, resampling, and level monitoring.
- `ASR`: engine-neutral transcription contracts and adapters.
- `PostProcessing`: deterministic cleanup, inverse text normalization, and emoji rules.
- `LLM`: optional local and cloud polish providers.
- `Pipeline`: recording-to-delivery orchestration and cancellation.
- `Services`: storage, credentials, updates, telemetry boundaries, and Windows integration.
- `ModelDelivery`: manifests, downloads, hashes, versions, storage, and cleanup.
- `RuntimeWorker`: a **separate executable** that hosts the native speech runtimes, including the CUDA
  build. `Services` drives it through `RuntimeWorkerSupervisor` over a versioned protocol with a bounded
  restart budget and an explicit process priority.

Dependencies point inward toward contracts. UI, storage, network, and model runtimes do not leak into the
deterministic core.

**Speech models run OUT OF PROCESS.** Wire a new engine through the worker and its supervisor, never
in-process in the app. The supervisor's process priority is also what keeps the worker off the efficiency
cores — see `../rules/validation-discipline.md` RULE: work-started-over-ssh-lands-on-the-slow-cores.

The RATIONALE for the split is not recorded anywhere in the source. Treat crash isolation as the likely
reason but ask rather than assert it, and write the answer here when you get it.

## Speech engines

- Parakeet production work begins from the measured direct ONNX Runtime C# decoder in this repository.
  Sherpa-onnx is a benchmarked fallback candidate, not the assumed baseline.
- Whisper uses a pinned `whisper.cpp` Windows runtime behind the same final-ASR contract.
- Live preview uses a separate small multilingual Whisper model through `whisper.cpp`. It is display-only,
  runs below final-ASR priority, and yields resources before final transcription.
- CPU execution is mandatory. GPU acceleration is selected only after a real capability probe.

## Polishing engines

- EG-1 uses a pinned Windows `llama.cpp` server or library and the same GGUF model family as macOS.
- Ollama uses its documented loopback API and never requires an Envious Labs proxy.
- OpenAI, Anthropic, and Gemini are direct BYOK integrations.
- All providers implement one contract with timeouts, cancellation, health checks, and deterministic
  fallback. Provider-specific wire details stay inside adapters.

## Windows integration

- Audio: WASAPI through a maintained .NET wrapper or a narrow native bridge.
- Hotkey: ONE route, a `WH_KEYBOARD_LL` hook (`WindowsPushToTalkHook`), for every binding. The original
  intent was `RegisterHotKey` where possible, and it did not survive for a reason worth keeping: push-to-talk
  needs the key-UP edge and `RegisterHotKey` delivers only the press. `RegisterHotKey` remains in that file
  as a conflict PROBE - register, unregister, report - and never receives a keystroke, which is why a
  synthetic press takes exactly the path a finger takes (see `uat-testing.md`).
- Focus and context: Windows UI Automation with explicit fallbacks and privacy limits.
- Delivery: clipboard-backed paste through narrowly scoped `SendInput`, with clipboard-only fallback when
  synthetic paste is refused. Two routes, not the macOS cascade of five, and that is deliberate.
- Secrets: Windows Credential Manager.
- Storage: versioned user data outside the install directory with atomic writes and migrations.

## Runtime selection

At startup, discover CPU, GPU providers, memory, model availability, and known incompatibilities. Choose a
safe default and show why. A manual choice is allowed when it passes the same capability probe. One engine
failure can fall back without crashing the app or losing recorded audio.
