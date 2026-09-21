# Architecture contract

## Preserve the proof, build the product

The current WPF and .NET 8 application is a founder-tested vertical slice. Keep it runnable as a reference
until each capability has production replacement evidence. Do not rewrite working behavior all at once.

The production target is C# on the current .NET LTS with WinUI 3 and Windows App SDK. Model engines may
use pinned native libraries behind small C# interfaces. Direct distribution uses a self-contained Windows
build so customers do not need to install developer tooling.

## Module boundaries

- `App`: composition, lifecycle, tray, onboarding, and WinUI views. The session's composition is
  WinUI-free, under `App/Composition/` (since plan-2 step 4 on #148): `SessionComposition.Compose`
  joins the controller, the background owner, the finalisation runner, the executor and the coordinator
  from the concrete things the shell chooses plus a `SessionShell` of leaf reads and notifications and an
  `ISessionView` of four window sinks. The architecture tests compile those exact files (a source link,
  not a copy) and drive the composed coordinator with fakes at the leaves, so the production joins are
  proved under xunit rather than only by a journey through the built app. `RuntimeComposition.Compose`
  (plan-2 step 5) builds the long-lived owners the same way - the persistence owner, the finaliser
  and its polish, the live preview, streaming, the watchdog and the auto-stop - from the concrete
  stores plus a `RuntimeShell` of leaf reads (settings, engines, the capture, the coordinator) and an
  `IRuntimeView` of the preview, recovery and history sinks; its `SessionQueue` is the one route a
  key, the auto-stop and the watchdog take to the coordinator. `App.xaml.cs` keeps the concrete
  selection, the two dispatcher-bound view forwarders and the session teardown.
- `Core`: shared value types, settings contracts, errors, and session state.
- `Audio`: WASAPI capture, device selection, resampling, and level monitoring.
- `ASR`: engine-neutral transcription contracts and adapters.
- `PostProcessing`: deterministic cleanup, inverse text normalization, and emoji rules.
- `LLM`: optional local and cloud polish providers.
- `Pipeline`: recording-to-delivery orchestration and cancellation.
- `Presentation`: the decisions a window makes, without the window - settings transactions and their
  failure answers, and (as #148 lanes 15-20 land) provider configuration, the microphone test, history
  commands, vocabulary editing and import. Depends on Core only, so every rule that used to sit behind a
  WinUI control runs under xunit. Windows keep control reads, rendering and WinUI events. The General
  page's Save is `SettingsPresenter.SaveGeneralAsync(GeneralSettingsInput)` (plan-2 step 11): the window
  reads its controls into raw values - text as typed, choices as their index, numbers as the field holds
  them - and the presenter parses the three shortcuts, refuses a clash with the same detector the live
  warning asks, normalises every index, turns an empty number into its default, shares telemetry only
  where the build can, and replaces exactly three stored fields (microphone, preferences, observability)
  so an import or an app-state write that landed meanwhile survives; the window focuses the field the
  outcome names, shows its message and applies the theme it returns. The index maps are the presenter's
  statics, so filling the controls and saving them cannot drift. The window takes its presentation as one
  thing, `WindowPresentationSession` (plan-2 step 12): the one settings writer every presenter shares,
  the presenters built over it, the microphone test and the device catalogue opened through factories
  the shell's `WindowComposition` supplies (the WASAPI ones; a test's fakes), the profile and diagnostic
  services handed through - owned by the session, which the shell's lifetime closes among its shell
  services: the drain first, so the settings write in flight is kept, then the writer's gate and the
  catalogue, once, a second close a no-op. The window also takes `WindowLaunch`, the immutable facts of
  the build and the start, and keeps WinUI: controls, layout, navigation, focus, the overlay. The
  actual Save button is exercised natively by `scripts/native-settings-save.ps1`.
- `Services`: storage, credentials, updates, telemetry boundaries, and Windows integration.
- `ModelDelivery`: manifests, downloads, hashes, versions, storage, and cleanup.
- `RuntimeWorker`: a **separate executable** that hosts the native speech runtimes, including the CUDA
  build. `Services` drives it through `RuntimeWorkerSupervisor` over a versioned protocol with an explicit
  process priority; automatic restarts are bounded per crash loop, with the budget replenished by a
  successful transcription request and reset by an explicit start. Each worker is a **generation**
  (plan-2 step 6): a record that exists from the start that creates it to the exit that is observed,
  with ownership as a semaphore (a stop, a disposal and an abort take turns on the one handle) and the
  end as a promise published only once the exit was seen and the handle closed - so a handle closes
  exactly once, and a process nobody saw leave keeps its handle for the next attempt. For a shutdown
  the supervisor has a terminal `AbortAsync(deadline)`: it never waits behind the request gate, takes
  or waits for the generation in flight, kills, and reports what it saw (`Exited` / `StillRunning` /
  `NoWorker`); a wedged request then ends as a failed one, and no start of any kind brings a worker
  back (`RuntimeWorkerState.Aborted`). The transcription and preview adapters expose the same call;
  the preview's lets go of the resource it held only on an observed exit.

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
- **Which preview and final models ship, per hardware tier, is decided by a local evaluation bench, not by
  hand** (founder, 2026-09-05, #127). The Mac chose its engine from a 20-candidate rubric (finalize latency
  30%, English WER 25%, tail accuracy 15%, streaming 15%, integration 10%, memory 5%) and needed no tiering
  on unified-memory Apple Silicon. Windows spans CPU-only, integrated and discrete GPUs, so memory weighs
  more here and the expected outcome is tiered models selected by the capability probe. Until the bench
  exists, Live Preview costing 2.0-2.5 s a pass on CPU against a 2.5 s cadence (#127) is a known limit, not
  a tuning target - the Mac's rubric numbers are historical and are not to be reused as measurements.
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
- Delivery: three routes, tried in this order by `WindowsTextTargetAdapter.CommitAsync`, and every
  result names the one that ran (`TextDeliveryRoute`).
  1. `UiAutomationValue`: a direct value write through UI Automation. Taken only when the caret context is a
     standard edit field that supports the value pattern, nothing is selected, and the text is within
     `MaximumDirectValueCharacters` (16,384). Once the write has been issued the adapter returns whether or
     not it verified (`DirectWriteUnverified`) and never falls through to a paste. The source records no
     reason for that; the likely one is that a paste after a write of unknown effect could insert the text
     twice. Confirm before relying on it.
  2. `ClipboardPaste`: clipboard-backed paste through narrowly scoped `SendInput`.
  3. `ClipboardOnly`: the text is left on the clipboard when the paste is refused, or when the target is
     elevated, protected, changed since recording began, or unsupported.
  Three routes, not the macOS cascade of five, and that is deliberate. This entry said "two routes" from
  2026-08-26 to 2026-09-19 while the code had three (#148); a contract that omits a mutation path hides
  the path that most needs validating.
- Secrets: Windows Credential Manager.
- Storage: versioned user data outside the install directory with atomic writes and migrations.

## Runtime selection

At startup, discover CPU, GPU providers, memory, model availability, and known incompatibilities. Choose a
safe default and show why. A manual choice is allowed when it passes the same capability probe. One engine
failure can fall back without crashing the app or losing recorded audio.
