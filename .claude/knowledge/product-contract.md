# Product contract

## Audience and promise

EnviousWispr is commercial-grade Windows dictation for ordinary laptops and powerful gaming PCs. The
experience is hold, speak, release, and continue working. It must remain useful without an account and
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
- The three recording-pill designs are Capsule, Reading Well, and Level Rail. Capsule and Level Rail are
  wordless choices. Reading Well is the Live Preview choice and grows to show up to five lines of the
  display-only preview. The app remembers the wordless and with-words selections separately.
- Recording sounds are optional and off by default. Whisper Tick is the fresh-install selection. The
  ordered catalog is Dust Mote, Velvet Hush, Muted Confirm, Whisper Tick, Round Pebble, Paper Tap, Soft
  Hush, Low Nod, Cloud Pop, Velvet Tap, Satin Shift, and Air Glint. Settings can preview the selected
  start/stop pair even while the master switch is off, but never during an active recording.
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

## Licensing and release

The Windows application follows the same GPLv3 product licensing direction as the macOS project. Direct
distribution is primary. The Microsoft Store can become a secondary channel after direct installation and
updates are proven.
