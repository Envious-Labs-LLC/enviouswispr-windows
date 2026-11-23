# Public-release admission

Public distribution is a founder-approved decision about one immutable Microsoft Store package candidate,
not a property of a branch, passing compile, or unsigned test build. The operator begins with the
Phase 22 Microsoft Store private-audience and flight admission criteria and then verifies every Phase 23
evidence class below.

| Evidence class | Required proof | Current state |
| --- | --- | --- |
| Product parity | Agreed feature matrix and native end-to-end UAT across the final pipeline | Incomplete |
| Legal | GPL source notice plus reviewed notices for every NuGet, native runtime, model, and CUDA artifact | NuGet inventory automated; model/native source inventory recorded; approval open |
| Privacy | Approved user notice, telemetry server record, schema/redaction review, and deletion procedure | Engineering boundary implemented; approval open |
| Security | Private reporting, secret scanning, dependency review, threat review, and signed artifact scan | Repository protections enabled; candidate review open |
| Accessibility | Keyboard, screen reader, contrast, DPI, multi-monitor, locale, RTL, and IME evidence | Partial matrix only |
| Compatibility | Written Windows/hardware/audio/display/security/target-app requirements from real machines | Single desktop cell only |
| Performance | Latency, memory, battery, power, and thermal budgets on target laptops | Desktop baseline only |
| Distribution | Store-signed MSIX, self-contained publish validation, clean user install, WACK certification pass, Store-mediated idle update apply, unvirtualized user data preservation across updates/uninstall, and higher-version roll-forward | Unsigned UAT only |
| Operations | Partner Center package flighting, rollback ownership via higher-version release, privacy-safe diagnostics, incident response, and retained last-known-good package | Contracts present; production operation open |
| Approval | Saurabh approves the exact version, channel, hashes, evidence record, and public exposure | Not granted |

## Release gate commands

Run repository compliance first:

```powershell
pwsh -NoProfile -File .\scripts\audit-public-release.ps1 -VerifyGitHubSecurity
```

The audit validates the pending model/native source inventory without converting it into approval. Before
admitting a public candidate, the exact artifact records must pass the separate strict gate:

```powershell
pwsh -NoProfile -File .\scripts\validate-artifact-license-inventory.ps1 -RequireApproved
```

Execute the full canonical release build and portable validation suite:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\validate.ps1 -Configuration Release
```

Build the self-contained Microsoft Store MSIX package for submission:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package-store.ps1 -ForStoreUpload -OutputDirectory dist\windows\store
```

The script publishes the self-contained app, verifies the pinned Store packaging toolchain (`.NET 10 SDK 10.0.4xx`,
`net10.0-windows10.0.26100.0`, `Microsoft.WindowsAppSDK 2.4.0`, `Microsoft.Windows.SDK.BuildTools 10.0.28000.2526`,
`Microsoft.Windows.SDK.BuildTools.MSIX 1.7.251221100`, `msstore` CLI `0.4.4.3+`), verifies that `Package.appxmanifest`
carries the assigned Partner Center identity (rejecting placeholder identities), stages and verifies
`runtime\llama-server.exe` plus its native dependencies inside the MSIX archive under Microsoft Store policy 10.2.2,
rejects bundled CUDA/cuDNN libraries (delivered separately on demand via `models/manifests/cuda-runtime.json`), and
ensures both the extracted `EnviousWispr.RuntimeWorker.exe` and `runtime\llama-server.exe` launch from the extracted
MSIX payload.

## Microsoft Store release acceptance checklist

Before public launch, the candidate must satisfy the complete Store release checklist:

1. **Partner Center Identity:** Replace placeholder identity in `Package.appxmanifest` with assigned Partner Center Identity `Name` and `Publisher`. Version format must have a revision of 0 (`X.Y.Z.0`).
2. **Capability Justifications:** File written justifications for restricted capability `runFullTrust` (system hotkey interception, UI Automation text delivery, worker process isolation) and `unvirtualizedResources` (persisting user data, custom vocabulary, transcripts, and model downloads in `%LocalAppData%\Envious Labs\EnviousWispr` across updates and uninstalls).
3. **Windows App Certification Kit (WACK):** Run WACK validation on the final `.msix` package (`appcert.exe`). Must pass all tests with zero errors.
4. **Clean-User End-to-End Acceptance:** Install on a fresh Windows 11 user account or clean VM without developer tools and without `ENVIOUSWISPR_UAT_*` overrides. Verify onboarding, microphone permission prompt, first-run model download from Cloudflare R2, push-to-talk dictation, UI Automation delivery, settings persistence, and unvirtualized user data survival.
5. **Store Flight & In-App Update Verification:** Publish package to a Partner Center private flight. Verify that `StoreUpdateService` detects the update when idle, holds dictation admission during update application, cleanly exits, and preserves existing data.
6. **Issue Evidence Preservation:** Record all validation results in GitHub issue #52. Raw user content, credentials, private machine paths, audio, transcripts, crash dumps, and signing metadata never enter the repository or evidence record.

## Partner Center Restricted Capability Justifications

Use the submission-ready text below in Partner Center (**Application -> Packages -> Capability declarations** and **Notes for certification**) when submitting `Package.appxmanifest` for Microsoft Store certification (#42, #52).

### 1. `runFullTrust` (`rescap:Capability Name="runFullTrust"`)

> **Partner Center Justification (`runFullTrust`):**
> EnviousWispr is a system-wide push-to-talk desktop dictation utility for Windows 11 (`EntryPoint="Windows.FullTrustApplication"`). It requires medium-integrity desktop execution (`runFullTrust`) for three core product functions that cannot execute inside an AppContainer sandbox:
> 1. **System-wide push-to-talk keyboard hook (`WH_KEYBOARD_LL`):** The app listens for both key-down and key-up transitions of the user's configured push-to-talk shortcut (plus optional cancel, Quick Add, and Paste/Copy Last Dictation shortcuts) while any desktop application is focused. Win32 `RegisterHotKey` delivers only key-down events and cannot detect the key-release edge required to stop recording.
> 2. **Cross-process text delivery (`UIAutomation` and `SendInput` / clipboard restoration):** When the user releases the push-to-talk key, EnviousWispr delivers the completed transcript into the target application window that held focus when recording started, using direct UI Automation `ValuePattern` writes for standard edit controls or clipboard-backed `SendInput` paste with automatic clipboard snapshot and restoration.
> 3. **Out-of-process local AI runtime workers (`EnviousWispr.RuntimeWorker.exe` and `runtime\llama-server.exe`) and local loopback integrations:** All speech recognition (NVIDIA Parakeet via ONNX Runtime and OpenAI Whisper via `whisper.cpp` in `EnviousWispr.RuntimeWorker.exe`) and bundled EG-1 local text polishing (`runtime\llama-server.exe`) run in supervised child processes on the user's PC, while optional local Ollama polishing connects over loopback HTTP to an independently running, user-managed Ollama service (which EnviousWispr never starts), ensuring dictated audio never leaves the device.
>
> The application runs at standard user (`mediumIL`) integrity, never requests administrator elevation, never injects input into elevated windows (falling back cleanly to clipboard copy), and honours the user-controlled `windows.startupTask` declaration (`EnviousWisprStartup`, disabled by default) in Windows Settings and Task Manager.

### 2. `unvirtualizedResources` (`rescap:Capability Name="unvirtualizedResources"` and `virtualization:FileSystemWriteVirtualization`)

> **Partner Center Justification (`unvirtualizedResources`):**
> EnviousWispr declares `<rescap:Capability Name="unvirtualizedResources" />` paired with a single Windows 11 directory exclusion in `Package.appxmanifest`:
> `<virtualization:ExcludedDirectory>$(KnownFolder:LocalAppData)\Envious Labs\EnviousWispr</virtualization:ExcludedDirectory>`.
>
> Without this exclusion, MSIX redirects writes under `%LocalAppData%` into package-private `LocalCache` storage and deletes that directory when the package is uninstalled. Excluding `%LocalAppData%\Envious Labs\EnviousWispr` from file-system write virtualization preserves the user's multi-gigabyte downloaded on-device speech and polish model packs (`models\`, up to ~3 GB across Parakeet, Whisper, EG-1, and optional on-demand CUDA runtime libraries), dictation history (`history.json`), custom dictionary and snippets, and preferences (`settings.json`) across MSIX repairs, updates, and uninstall/reinstall cycles.
>
> Virtualization is not disabled globally: the exclusion is scoped strictly to the single product folder `$(KnownFolder:LocalAppData)\Envious Labs\EnviousWispr` (never the parent company folder or the Windows registry). Because MSIX packages do not execute custom uninstaller dialogs during package removal, EnviousWispr discloses data retention during first-run onboarding and provides explicit in-app controls under **Settings -> Backup** (**Export my data** to a `.zip` archive and **Delete all EnviousWispr data**, which removes `%LocalAppData%\Envious Labs\EnviousWispr` and stored Windows Credential Manager API keys upon user confirmation).

The final action is an explicit founder decision. The gate never merges, signs, uploads, publishes, enables
paid infrastructure, or converts an unobserved cell into a pass.
