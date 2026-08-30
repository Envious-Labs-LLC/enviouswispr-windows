# Windows direct distribution and updates

## Release identities

Direct releases are self-contained `win-x64` Velopack packages. Each audience has a separate package,
feed, data directory, and single-instance key:

| Audience | Velopack channel | Package ID | Data directory |
| --- | --- | --- | --- |
| Stable | `win-x64-stable` | `EnviousLabs.EnviousWispr` | `%LocalAppData%\Envious Labs\EnviousWispr` |
| Founder | `win-x64-founder` | `EnviousLabs.EnviousWispr.Founder` | `%LocalAppData%\Envious Labs\EnviousWispr-Founder` |
| Beta | `win-x64-beta` | `EnviousLabs.EnviousWispr.Beta` | `%LocalAppData%\Envious Labs\EnviousWispr-Beta` |

The install roots are Velopack-managed per-user directories under `%LocalAppData%`. User settings,
models, logs, history, and diagnostics must remain in the data directory, never in the replaceable
install root.

## Build a release

Restore the pinned repository tool and run the packaging script from the repository root:

```powershell
pwsh -NoProfile -File .\scripts\package-windows.ps1 `
  -Version 1.0.0 `
  -Channel stable `
  -AzureTrustedSignFile C:\secure\enviouswispr-trusted-signing.json
```

The script publishes a self-contained x64 app, verifies the WinUI PRI/XBF resources and runtime worker,
then asks Velopack to sign and package the application. It fails closed unless Azure Trusted Signing
metadata is supplied. `-DevelopmentUnsigned` exists only for isolated local installer testing and leaves
an explicit warning file in the output.

Signing metadata and credentials are operator secrets. Do not commit them. Public signing also requires
founder approval for Azure's paid service and confirmation of the final certificate subject. Production
validation currently expects an Authenticode subject containing `Envious Labs`.

## Publish a feed

Publish each output directory to a different HTTPS path. Do not combine channel indexes or package IDs.
The app receives its vetted, channel-specific base URL through `ENVIOUSWISPR_UPDATE_ENDPOINT`. Production
rejects HTTP, URL credentials, queries, and fragments. Loopback HTTP is accepted only when
`ENVIOUSWISPR_UAT_ALLOW_LOOPBACK_UPDATES=1` is also present for local UAT.

Release operators must upload the complete output as one immutable set, then expose the new channel index
last. Preserve older full packages so a last-known-good release can be republished and users can repair an
installation. Never replace a published version with different bytes; issue a higher version instead.

## In-app admission and apply

`VelopackApp.Run()` executes before WinUI initialization. The in-app flow then:

1. Checks only the embedded channel and package identity.
2. Acquires the same operation gate used by recording and processing.
3. Downloads a full release. Delta application is deliberately disabled because reconstruction does not
   retain the feed's byte-for-byte SHA-256.
4. Verifies the exact SHA-256 advertised by the feed.
5. Opens the package and requires every packaged PE image to have a trusted, cache-only Authenticode chain
   whose signer subject contains `Envious Labs`.
6. Deletes a rejected package and restores the previous updater executable.
7. Enables Apply only for an admitted package. Apply rechecks that dictation is idle, requests a graceful
   app exit, and lets Velopack atomically replace and restart the installed app.

No automatic check occurs in a developer build or when no update endpoint is configured. Update checks and
dictation are mutually exclusive. Once update-driven exit begins, a new push-to-talk session is refused.

## Repair, rollback, and uninstall

Running the same or newer Setup executable repairs the per-user installation. Velopack renames the existing
install root to a rollback directory before replacement and removes that directory only after install hooks,
shortcuts, and uninstall registration succeed. A failed or interrupted repair is recovered by rerunning a
known-good signed Setup. The external data directory is not removed by repair or uninstall.

Rollback policy is operational: retain the last-known-good signed Setup/full package, retract a bad channel
index, and publish or reinstall the known-good build. The product does not silently downgrade through the
normal update client. A real signed apply failure and rollback must be observed before public release.

Silent uninstall for validation is:

```powershell
& "$env:LOCALAPPDATA\EnviousLabs.EnviousWispr\Update.exe" uninstall --silent
```

Use the package-specific founder or beta root for those channels. Resolve and inspect the exact root before
running this command.

## Release gate

Founder and beta candidates must also pass the independent artifact and lifecycle admission described in
`docs/distribution/private-beta-release.md`. Packaging success alone is never a release approval.

The following remain mandatory before public direct distribution:

- Founder approval and configuration of Azure Artifact Signing.
- A valid Envious Labs-signed Setup and full package.
- Immutable HTTPS hosting for all three isolated feeds.
- Signed install, admitted update, atomic apply/restart, forced failure, rollback, repair, and uninstall on
  clean supported Windows machines.
- SmartScreen and endpoint-security observations on the compatibility matrix.
- A release checklist confirming user data, models, and settings survive every operation.

Microsoft Store/MSIX remains a secondary evaluation only after the direct path passes these gates. It must
not weaken offline use, local model delivery, BYOK, or Ollama support.

## Primary implementation references

- [Velopack integration overview](https://docs.velopack.io/integrating/overview)
- [Velopack channels](https://docs.velopack.io/packaging/channels)
- [Velopack Windows packaging](https://docs.velopack.io/packaging/operating-systems/windows)
- [Velopack signing](https://docs.velopack.io/packaging/signing)
