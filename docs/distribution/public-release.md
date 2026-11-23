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

The script publishes the self-contained app, verifies that `Package.appxmanifest` carries the assigned Partner Center
identity (rejecting placeholder identities), validates required payload contents inside the MSIX package archive,
and ensures the extracted `EnviousWispr.RuntimeWorker.exe` launches self-contained.

## Microsoft Store release acceptance checklist

Before public launch, the candidate must satisfy the complete Store release checklist:

1. **Partner Center Identity:** Replace placeholder identity in `Package.appxmanifest` with assigned Partner Center Identity `Name` and `Publisher`. Version format must have a revision of 0 (`X.Y.Z.0`).
2. **Capability Justifications:** File written justifications for restricted capability `runFullTrust` (system hotkey interception, UI Automation text delivery, worker process isolation) and `unvirtualizedResources` (persisting user data, custom vocabulary, transcripts, and model downloads in `%LocalAppData%\Envious Labs\EnviousWispr` across updates and uninstalls).
3. **Windows App Certification Kit (WACK):** Run WACK validation on the final `.msix` package (`appcert.exe`). Must pass all tests with zero errors.
4. **Clean-User End-to-End Acceptance:** Install on a fresh Windows 11 user account or clean VM without developer tools and without `ENVIOUSWISPR_UAT_*` overrides. Verify onboarding, microphone permission prompt, first-run model download from Cloudflare R2, push-to-talk dictation, UI Automation delivery, settings persistence, and unvirtualized user data survival.
5. **Store Flight & In-App Update Verification:** Publish package to a Partner Center private flight. Verify that `StoreUpdateService` detects the update when idle, holds dictation admission during update application, cleanly exits, and preserves existing data.
6. **Issue Evidence Preservation:** Record all validation results in GitHub issue #52. Raw user content, credentials, private machine paths, audio, transcripts, crash dumps, and signing metadata never enter the repository or evidence record.

The final action is an explicit founder decision. The gate never merges, signs, uploads, publishes, enables
paid infrastructure, or converts an unobserved cell into a pass.
