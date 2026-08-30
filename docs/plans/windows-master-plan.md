# EnviousWispr Windows Edition master plan

This plan recreates the complete agreed product in small proof-based phases. A phase exits only when its
acceptance evidence is committed or linked. Later work may begin in parallel when contracts are stable,
but no phase is called complete from source code alone.

## Phase 0: preserve the proof and distill the brain

### 0A. Inventory and preserve

Keep the founder-tested WPF and .NET 8 build runnable. Record its commit, model assumptions, native UAT,
performance, and known gaps. Exit when a clean clone can build the proof and its evidence is easy to find.

### 0B. Distilled project brain

Create the product, architecture, pipeline, distribution, workflow, and validation contracts in this repo.
Keep reusable macOS lessons and remove Apple-only or duplicated process. Exit when a fresh agent can state
the privacy boundary, pipeline order, target architecture, and delivery rules without the Mac folder.

### 0C. Validation spine

Provide one portable validation command, model-dependent validation, CI parity, and a PR evidence template.
Exit when the portable gate passes locally and in GitHub Actions with skips reported honestly.

### 0D. Fresh-agent test

Give a new Codex or Claude Code session a bounded task using only this repository. Exit when it follows the
right sources, changes the correct layer, validates it, and produces a complete handoff without coaching.

### 0E. Production decisions

Record final ADRs for .NET LTS and WinUI 3, self-contained distribution, Velopack, runtime isolation, data
layout, and migration from the WPF proof. Exit when no Phase 1 decision depends on hidden oral history.

## Phase 1: production solution and UI shell

Create the WinUI 3 solution, module boundaries, dependency rules, app lifecycle, single-instance handling,
logging shell, and test projects. Keep the old proof buildable beside it. Exit with a signed-off empty shell
that launches, quits, restores settings, and passes CI on Windows x64.

## Phase 2: core contracts, settings, and local storage

Port platform-neutral value types, session state, typed errors, provider settings, versioned storage,
atomic writes, migrations, import, and export. Exit with corruption, downgrade, reset, and migration tests
plus proof that secrets and user content are not stored in normal logs.

## Phase 3: audio capture and device routing

Build WASAPI microphone capture, device enumeration, default-device changes, level metering, 16 kHz mono
conversion, interruption handling, and device-loss recovery. Exit with real tests on built-in, USB, and
Bluetooth microphones and preserved audio after recoverable failures.

## Phase 4: hotkey and session state machine

Implement configurable push-to-talk, press/release semantics, cancellation, debounce, no-overlap rules,
target freezing, and conflict detection. Exit with physical-key UAT and proof that normal typing is never
captured or blocked by a stuck hook.

## Phase 5: hardware discovery and runtime isolation

Probe CPU, memory, NVIDIA CUDA, AMD DirectML, Intel DirectML, and model availability. Add automatic and
manual selection, safe process isolation, crash recovery, and resource arbitration. Exit with an explained
default on each supported hardware class and tested fallback after provider failure.

## Phase 6: Parakeet final ASR

Harden the direct ONNX Runtime C# decoder, tokenizer, feature extraction, timestamps, cancellation, and CPU
fallback. Benchmark sherpa-onnx only as a candidate, not a presumed dependency. Exit with accuracy fixtures,
long-audio tests, cold and warm timings, and native UAT on GPU and CPU-only machines.

## Phase 7: Whisper final ASR

Integrate a pinned `whisper.cpp` Windows runtime, model selection, multilingual detection, CPU and GPU
providers, cancellation, and the same engine-neutral result contract. Exit with language, accuracy,
hardware, and failure parity tests against representative recordings.

## Phase 8: live multilingual preview

Run a separate small Whisper model on audio snapshots at lower priority than final ASR. Keep preview text
out of final processing and release its resources before final transcription. Exit when preview remains
responsive on target laptops and can fail or be disabled without affecting the final result.

## Phase 9: deterministic text and emoji parity

Port custom words, filler and false-start cleanup, punctuation, casing, inverse text normalization, spoken
emoji, emoji protection, and restoration. Exit when the shared parity corpus covers English and selected
international cases, and every intended Mac difference is documented.

## Phase 10: EG-1 local polish

Integrate the pinned Windows `llama.cpp` runtime, unchanged model family, prompt contract, health probe,
timeouts, cancellation, resource cleanup, and deterministic fallback. Exit with quality corpus results,
CPU and GPU measurements, startup recovery, and no leaked prompt or transcript content.

## Phase 11: cloud polish providers and credentials

Implement direct BYOK OpenAI, Anthropic, and Gemini adapters with Windows Credential Manager, retry rules,
timeouts, cancellation, consent copy, provider diagnostics, and delete-key behavior. Exit when mocked and
real opt-in tests prove no audio or Envious Labs proxy is involved.

## Phase 12: Ollama polish

Add localhost discovery, model listing, manual endpoint support, health checks, cancellation, and clear
offline error UX. Exit with multiple Ollama model tests and deterministic fallback when the service stops
mid-request.

## Phase 13: context and text delivery

Implement safe surrounding-text reads, cursor-aware repair, UI Automation insertion, clipboard fallback,
scoped `SendInput`, clipboard restoration, target validation, and app-specific compatibility rules. Exit
with UAT in browsers, Office, chat apps, terminals, editors, games, password fields, and elevated apps,
including explicit safe refusal where Windows blocks access.

## Phase 14: complete product UX

Build onboarding, permissions and microphone checks, overlay states, tray menu, settings, engine and model
management, history, dictionary, snippets, import/export, update UX, help, accessibility, and localization.
Exit after keyboard-only, screen-reader, high-DPI, multi-monitor, light/dark, and founder journey UAT.

## Phase 15: reliability and recovery

Harden startup, shutdown, sleep/wake, device changes, engine crashes, network loss, low disk, low memory,
corrupt settings, duplicate instances, stuck sessions, and crash recovery. Exit with fault injection and a
multi-day soak that does not lose text, leak processes, or leave input hooks active.

## Phase 16: model delivery and storage

Build signed manifests, resumable downloads, hashes, disk checks, version pinning, migration, cleanup,
offline reuse, and per-model license notices. Exit with interrupted, corrupt, insufficient-disk, upgrade,
downgrade, and clean-removal tests.

## Phase 17: international behavior and accessibility

Validate Whisper languages, locale-aware dates and numbers, Unicode, right-to-left text, input methods,
emoji, mixed-language dictation, UI localization, and assistive technology. Exit with a published support
matrix and native-speaker or trusted-fixture evidence for each advertised language tier.

## Phase 18: privacy-safe observability

Add consented content-free crashes, performance, engine choice, hardware class, and failure category data.
Create redaction tests, local diagnostic export, retention controls, and opt-out. Exit after a privacy review
proves dictated text, audio, keys, clipboard, and surrounding context cannot enter telemetry.

## Phase 19: installer, signing, and updates

Package self-contained x64 builds, sign them, integrate Velopack channels, stage downloads, validate hashes
and signatures, block updates during dictation, support rollback, and preserve user data on uninstall and
upgrade. Exit after clean-machine install, update, rollback, repair, and uninstall tests on supported Windows
versions. Evaluate Store/MSIX only as a secondary channel.

## Phase 20: compatibility matrix

Run the complete workflow across Windows versions, CPU generations, NVIDIA, AMD, Intel integrated graphics,
CPU-only laptops, memory tiers, microphones, display scales, multiple monitors, common endpoint security,
and representative target apps. Exit with published minimum and recommended requirements grounded in data.

## Phase 21: performance and laptop readiness

Measure cold start, recording overhead, preview cadence, final latency, memory, power, thermal behavior, and
model switching. Tune automatic selection before considering a battery-saving mode. Exit when no-dedicated-
GPU laptops remain useful and target hardware meets written latency and stability budgets.

## Phase 22: founder and private beta release

Ship signed founder and beta channels with diagnostics, feedback capture, crash triage, compatibility
playbooks, rollback, and release checklists. Exit after real daily use across target hardware closes all P0
and P1 issues and the remaining gaps are explicit product decisions.

## Phase 23: full-parity public release

Complete the agreed feature matrix, privacy review, licenses, security review, accessibility, support docs,
website copy, clean install, update, rollback, and public hardware requirements. Exit only when the release
candidate passes end-to-end UAT and the founder explicitly approves public distribution.
