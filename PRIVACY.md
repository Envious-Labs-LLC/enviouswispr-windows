# Engineering privacy notice

This notice describes the implemented EnviousWispr for Windows privacy boundary. It is a prerelease
engineering notice and requires founder/legal approval, final product links, and the approved telemetry
operating record before public distribution.

## Local dictation

Microphone audio stays on the user's PC. Parakeet and Whisper transcription, deterministic processing,
EG-1, and local Ollama polish run locally. Live preview is display-only and cannot enter final processing,
history, analytics, or delivery. Audio is not an Envious Labs telemetry field.

Settings, dictionaries, snippets, optional history, model packs, recovery state, and privacy-safe local
diagnostics are stored outside the replaceable install directory. Provider keys are stored through Windows
Credential Manager. Import/export excludes credentials and machine-local observability consent.

## Optional cloud polish

OpenAI, Anthropic, and Gemini polish are direct bring-your-own-key integrations. When the user explicitly
selects one, the app sends transcript text directly to that provider under the user's provider account and
terms. Envious Labs is not a text proxy. Audio is never sent. A provider failure returns the last valid
deterministic result.

## Diagnostics and operational sharing

Local diagnostics contain only sealed enums, bounded durations, timestamps, coarse engine/hardware classes,
and typed failure/error categories. They cannot carry transcript, audio, keys, clipboard, surrounding text,
paths, model IDs, account names, device names/IDs, exception messages, or stack traces. Local diagnostics
default on with a 14-day retention setting that the user can disable or change from 1–90 days.

Anonymous operational sharing defaults off and remains unavailable until an approved HTTPS endpoint and
server policy exist. Saved consent is required before queue admission. A network service can inherently see
connection metadata such as source IP and TLS timing; server minimization, access, retention, deletion,
incident response, sampling, region, and subprocessors must be approved and disclosed before activation.

## User control and deletion

Users can disable local diagnostics, export the allowlisted diagnostic record, clear optional history and
recovery text, delete provider keys, and uninstall the app. Repair, update, and uninstall preserve the user
data directory by design; a public release must provide a separate documented procedure for deleting all
retained local product data. Cloud providers control data already sent to them under the user's account and
their own retention policy.

EnviousWispr does not require an Envious Labs account in the implemented offline product. This notice must
be updated and reviewed before any account system, telemetry schema, stable identifier, crash SDK, endpoint,
provider, retention, or data-processing purpose changes.
