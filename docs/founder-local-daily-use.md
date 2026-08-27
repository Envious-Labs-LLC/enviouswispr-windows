# Founder-local daily use: production WinUI build

This is the current hands-on guide for the production Windows application. The older
`notes/founder-test.md` records the preserved WPF proof and must not be used as the current product guide.

## Current installed candidate

The founder machine currently has the unsigned, isolated founder channel installed under
`%LOCALAPPDATA%\EnviousLabs.EnviousWispr.Founder\current`.

- Product version: `0.24.0-founder.11+aa6bd735294d4219321d3240eb00d9a6f7efe89a`
- Installed executable SHA-256: `083D4CE340315E6124DE371D2F11CF3E9E1D86618940019AD3FC3BCC05ED42C2`
- Setup: `EnviousLabs.EnviousWispr.Founder-win-x64-founder-Setup.exe`
- Setup SHA-256: `01886DE3AF30C98240AC50A52C4BD73C9E867A8A1675BA7DACE307062D51351F`
- Platform: Windows 11 x64
- Release status: founder-local and unsigned; not certified or approved for public distribution

Later repository commits add validation and documentation around this same product build. They do not silently
replace the installed executable. Always identify the installed ProductVersion and executable hash when recording
candidate-specific evidence.

## Normal dictation

Launch **EnviousWispr Founder** from Windows. The app normally remains available in the notification area after
its main window is closed.

1. Open a normal editable field in Notepad, a browser, or another non-elevated desktop application.
2. Hold F8, speak naturally into the selected microphone, and release F8.
3. Observe the recording pill move through recording and processing.
4. Confirm that the final text appears at the original cursor. If direct insertion is unsafe, EnviousWispr must
   keep the text on the clipboard and explain that Ctrl+V is required.

Do not use an elevated target for the first session. Windows intentionally prevents a normal desktop process from
injecting input into some elevated applications.

## Product-parity session

Use the production navigation groups rather than the historical proof-app controls:

- **APP** — review History and What's New; exercise System, Light, and Dark appearance.
- **RECORD / Transcription** — complete one Parakeet dictation and one Whisper dictation. Automatic selection is
  the normal default; manual choices are available when the required local model exists.
- **RECORD / Live Preview** — enable preview, select Reading Well, and confirm preview text stays display-only.
- **RECORD / Microphone, Sounds, and Keybinds** — verify the chosen capture device, preview the selected sound,
  and confirm Push to Talk or Toggle behavior. Recording, cancel, and Add-a-word shortcuts must remain distinct.
- **APP / Appearance** — inspect Capsule, Reading Well, and Level Rail at both top and bottom placement. The app
  must remember the wordless and with-words pill choices separately.
- **PROCESS / Your Words** — add a harmless project term, dictate it, correct it if needed, and remove it after the
  session unless it is genuinely useful.
- **PROCESS / AI Polish** — try deterministic-only output first. EG-1 and Ollama remain local. OpenAI, Anthropic,
  and Gemini are direct BYOK options and send transcript text only after the user selects that provider.
- **OUTPUT / Clipboard** — confirm the safe fallback in a target where direct insertion is unavailable.
- **SYSTEM** — inspect Permissions, Check for Updates, and Open Source Licenses. The unsigned founder channel is
  not evidence for signed update or SmartScreen behavior.

For deterministic cleanup, use ordinary harmless phrases containing filler words, spoken punctuation, a number,
and a spoken emoji command. Judge the configured cleanup result, not the exact wording of a private dictation.

## Exact installed-candidate microphone acceptance

The repository harness can first run deterministic public fixtures through the exact installed executable with
an isolated temporary profile and content-free evidence. Exit the normally running app using **Exit
EnviousWispr** in its notification-area menu, then run from the repository root:

```powershell
$installedApp = Join-Path $env:LOCALAPPDATA `
  'EnviousLabs.EnviousWispr.Founder\current\EnviousWispr.App.exe'
& "$env:USERPROFILE\.dotnet\dotnet.exe" run --no-build `
  --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj -c Release -- `
  --english-parakeet --app-executable $installedApp
& "$env:USERPROFILE\.dotnet\dotnet.exe" run --no-build `
  --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj -c Release -- `
  --app-executable $installedApp
```

Those automated runs prove the installed shell, worker, Parakeet and Whisper paths, deterministic processing,
controlled Windows delivery, and cleanup. They substitute reviewed audio and named transitions, so physical
microphone acceptance remains a separate action:

The commands above passed against the founder.10 candidate on 2026-08-26. The current founder.11 candidate
repeated Whisper French/CUDA in 8,615 ms and Parakeet English/CPU with Live Preview in 9,423 ms. Each run
observed the production stage sequence, delivered to the controlled native edit field, exited cleanly, and
left zero owned workers. The Live Preview run observed a non-empty display-only update before final
transcription and delivery.

Founder.11 also passed exact installed local-polish journeys. Parakeet English/CPU with EG-1 completed in
9,270 ms, including 1,714 ms of polish; it started one app-owned llama server and left none after exit.
Parakeet English/CPU with an existing local Ollama model completed in 5,086 ms, including 667 ms of polish;
it started no owned polish process and did not stop the external loopback daemon. Both required provider-tagged
readiness, `PolishStarted`, `PolishCompleted`, no degraded fallback, native delivery, and clean shutdown. The
privacy-safe commands and prerequisites are documented in `tools/app-journey-uat/README.md`.

Founder.11 also passed the paired deterministic-settings journeys. With all four cleanup switches enabled, a
reviewed custom-word replacement crossed filler removal, spoken emoji, and spoken punctuation and delivered the
expected transformed marker with the injected filler absent in 4,655 ms. With the identical custom-word entry
present but all four switches disabled, the original recognized word survived and delivered in 4,561 ms. Both
exact installed runs exited cleanly and left zero owned workers. Reproduction commands are in
`tools/app-journey-uat/README.md`.

Founder.11 also passed exact installed missing-key fallback for OpenAI, Anthropic, and Gemini in 4,373 ms,
4,341 ms, and 4,393 ms. Each provider emitted its typed missing-credential result within 2–3 ms, preserved the
deterministic transcript, delivered it natively, exited cleanly, and left zero workers. These runs used unique
empty test credential slots and made no provider request; real BYOK generation remains unobserved until a founder
supplies a key and explicitly accepts transmission and possible provider charges.

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" run --no-build `
  --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj -c Release -- `
  --english-parakeet --manual-microphone --app-executable $installedApp
```

Keep the controlled target focused, physically hold F8, speak the complete public sentence displayed in the
window, and release F8. The result passes only if the real global hook, production WASAPI capture, installed
worker, Parakeet, deterministic pipeline, Windows delivery, clean app exit, and zero-worker cleanup all succeed.
The JSON result identifies the installed ProductVersion and executable SHA-256 without exposing its path. Audio
and transcript text are not retained.

Add `--live-preview` only when the repository's gitignored small preview model is installed. After the isolated
journey exits, relaunch **EnviousWispr Founder** normally for daily use.

## Content-free feedback

Report the candidate version, selected engine/provider, target application class, pass or fail, and the visible
product stage where a failure occurred. Do not put dictated text, audio, clipboard contents, credentials, device
names, account names, or private paths into an issue or diagnostic export.

## Still outside founder-local acceptance

The unsigned founder build can be useful before certification. Public readiness still requires trusted signing,
signed update and rollback evidence, clean-machine and representative-laptop runs, licensing approvals, and
founder approval for the exact release. The current bounded German Whisper corpus also remains below its final
language-quality gate.
