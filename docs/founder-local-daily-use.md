# Founder-local daily use: production WinUI build

This is the current hands-on guide for the production Windows application. The older
`notes/founder-test.md` records the preserved WPF proof and must not be used as the current product guide.

## Current installed candidate

The founder machine currently has the unsigned, isolated founder channel installed under
`%LOCALAPPDATA%\EnviousLabs.EnviousWispr.Founder\current`.

- Product version: `0.24.0-founder.9+ce13525d41770c669bb2034c360bb9247bfc7447`
- Installed executable SHA-256: `131C053C8D60EB5BD1A5BC2272D29B442E46AF92F932CFA5238CFC1B8E38C197`
- Setup: `EnviousLabs.EnviousWispr.Founder-win-x64-founder-Setup.exe`
- Setup SHA-256: `AA6AAC51413A126BBFDFF8203FF965FF1B95300B35D3BE43122A7C40DF56A0BC`
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
