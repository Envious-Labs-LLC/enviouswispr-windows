# Product contract

**How to read this file.** A PROMISE is stated here with its values, because if the code drops one this
file is what catches it. An IMPLEMENTATION DETAIL — an internal member list or display order with no
independent product requirement — names its owning type instead, because prose restating code can only
decay.

The line matters: a contract that points at code for everything can never disagree with code, so it can
never catch a regression. Providers, engines, navigation groups, licensing direction, and user-visible
fresh-install defaults are promises and stay written out. Internal enum membership and catalog order are
details and point at their owner.

**The test, when a line is ambiguous:** would changing or removing this value reduce supported
user-visible behaviour, violate a release promise, or weaken acceptance criteria? If yes, write it as a
promise. If it only describes internal representation, ordering or ownership, point at the code.

## Audience and promise

EnviousWispr is commercial-grade Windows dictation for ordinary laptops and powerful gaming PCs. The
default experience is hold, speak, release, and continue working; Toggle is available for people who
prefer press, speak, press. It must remain useful without an account and
without a dedicated GPU.

## Supported product shape

- Windows 11 x64 first. ARM64 is a later release track.
- Automatic hardware and engine selection by default, with manual selection when a choice exists.
- NVIDIA, AMD, Intel integrated graphics, and CPU-only systems are in scope.
- No separate battery-saving mode until measurements show that automatic selection is insufficient.
- Parakeet and Whisper are both first-class final transcription engines.
- Live preview is multilingual Whisper and is independent of the final engine.
- Polishing supports EG-1, Ollama, OpenAI, Anthropic Claude, and Google Gemini.
- Spoken punctuation, deterministic cleanup, inverse text normalization, and emoji support are required.
- Settings, dictionaries, snippets, history preferences, and reusable user data must be portable through
  import and export. Contacts integration is intentionally omitted from Windows parity.

## macOS interaction parity

- The main navigation uses the macOS product groups and names: APP (History, What’s New, Appearance),
  RECORD (Transcription, Live Preview, Microphone, Sounds, Keybinds), PROCESS (AI Polish, Your Words),
  OUTPUT (Clipboard), and SYSTEM (Permissions, Check for Updates, Open Source Licenses).
- Appearance follows the Windows setting by default and also offers explicit Light and Dark modes.
- The recording pill can appear at the top or bottom of the active monitor.
- Three recording-pill designs. Capsule and Level Rail are the wordless choices; Reading Well is the Live
  Preview choice and grows to show up to five lines of display-only preview. The app remembers the wordless
  and with-words selections separately. **The user-facing name "Capsule" is `Classic` in code** — the enum
  `RecordingPillDesign` is the authority and does not use the display name.
- Recording sounds are optional and off by default. Whisper Tick is the fresh-install selection. Settings
  can preview the selected start/stop pair while the master switch is off, but never during an active
  recording. **`RecordingSoundCatalog.Choices` owns the sounds, their display names and their order, and
  feeds the picker directly; `RecordingSoundPairing` owns the identities. Never restate either list here.**
- Keybinds offer Push to Talk and Toggle recording modes. The recording, cancel, and Add-a-word
  shortcuts are independently configurable and must not overlap. Windows defaults are F8, Escape, and
  Ctrl+Alt+W respectively.
- Escape Recovery is off by default and is frozen when each recording starts. When enabled, the cancel
  shortcut finishes local transcription, deterministic cleanup, and configured polish without delivering
  automatically. The audio is released after text is saved; the text is offered on Home and expires from
  History after 24 hours unless the user chooses Keep. A direct Cancel control, where present, still
  discards immediately.
- Add-a-word reads the current selection before EnviousWispr takes focus and opens Your Words with the
  selected spelling ready to correct. No selection text crosses a network or diagnostic boundary.
  Terminals and inaccessible targets fail safely with by-hand guidance.
- AI Polish lists installed Ollama models locally. For direct OpenAI, Anthropic, and Gemini providers,
  a stored BYOK credential may list the compatible model IDs available to that account without sending
  transcript text or invoking a generation endpoint. The recommended model and a custom compatible ID
  remain available when discovery cannot run.

## Privacy boundary

- Audio never leaves the device.
- Local transcription, deterministic processing, EG-1, and Ollama stay on the device.
- Cloud polish sends transcript text directly from the app to the provider chosen by the user.
- Provider keys are supplied by the user and stored with Windows Credential Manager.
- Envious Labs may receive consented operational metadata, crashes, and performance measurements, but no
  dictated audio, raw transcript, polished transcript, clipboard contents, or surrounding text.

## Reliability contract

The deterministic result is always usable on its own. Optional AI polish may improve it but cannot be
required to complete a dictation. If any later stage fails, the pipeline returns the last valid result and
explains the degraded stage without exposing private content.

## Release gate

Invariant 8 says public release waits for full agreed Windows parity. What "agreed" means, stated so it is
not re-litigated:

- **The catalog decides parity, not this file.** A feature is release-blocking while its Windows status is
  `absent` or `partial`. A `deliberately-different` row stops being a blocker only once
  `difference_reason` records the settled reason.
- **A deliberate difference needs its reason recorded before it counts as settled**, in the catalog's
  `difference_reason`. An undocumented divergence is a gap, not a decision.
- **Parity is claimed from observed behaviour, never from a green suite.** The evidence is the real
  application driven in a logged-on interactive desktop session, with the recording attached.

```bash
sqlite3 ~/.claude/knowledge/enviouswispr/catalog.db \
  "SELECT feature_slug, status FROM feature_platform \
   WHERE platform_key='windows' AND status IN ('absent','partial') ORDER BY status, feature_slug;"
```

**Until the founder records a changed contract, every `partial` row blocks public release.** A stated
limitation is not an exception.

## Licensing and release

The Windows application follows the same GPLv3 product licensing direction as the macOS project.

**The Microsoft Store is the single distribution channel** (founder, 2026-09-05, #42; supersedes "direct
distribution is primary"). Microsoft signs the package and delivers updates; no direct download is offered.
The basis, validated three times by an independent reviewer on #42: an MSIX declaring `runFullTrust` runs as
an ordinary medium-integrity desktop process - no Mac-App-Store-style sandbox - so the global hook,
clipboard-backed delivery, WASAPI, the out-of-process runtime worker, localhost Ollama, model downloads and
BYOK all survive. The promises below are user-visible and stay written out:

- **Uninstalling keeps the user's data.** Models, history and settings under the product's data folder
  survive uninstall (a Windows 11 folder virtualization exclusion, scoped to the product folder, not the
  company folder). The customer's choice lives in the app - Settings offers *Export my data* and *Delete all
  EnviousWispr data*, the latter also clearing stored credentials - and the first run and the Store listing
  say plainly that data must be deleted before uninstalling, or after reinstalling to reach the control.
  MSIX offers no uninstall-time hook for an application like this one.
- **One product identity.** Founder and beta testing happen through a private audience first, then package
  flights on the same installed identity - never a separately installed channel. Every release therefore
  ships schema-compatible settings and history migrations, and recovery means a higher-version package.
- **Updates never apply while recording or processing a dictation.** The app checks the Store for updates
  itself, offers them, and requests install only when idle; the Store then applies the package. The
  "last known-good version is recoverable" promise is met by publishing a higher-version recovery package.
- The parity gate above is **not** waived by this decision.
