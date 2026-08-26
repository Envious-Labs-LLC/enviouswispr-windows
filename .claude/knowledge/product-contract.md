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
