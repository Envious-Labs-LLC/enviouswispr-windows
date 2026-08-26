# Phase 19 evidence: installer, signing, and updates

Date: 2026-08-26

## Measured on this Windows 11 development machine

- Pinned Velopack 1.2.0 restored and produced self-contained founder packages from 0.19.0 through 0.19.7.
- The package script produced isolated founder indexes, full packages, deltas, Setup, portable output, and a
  SHA-256 distribution manifest. These artifacts were explicitly unsigned development output.
- Initial native install was per-user and required no elevation. The Start menu shortcut and install hook
  were observed.
- Native installed UI reached onboarding and the Help > Updates card. The embedded identity displayed
  `win-x64-founder`; version 0.19.4 correctly reported no update against the same feed.
- An initial 0.19.5 delta UAT rejected safely but exposed that a reconstructed full package does not retain
  the remote full package's byte-for-byte hash. The client was changed to download full packages only.
- The corrected 0.19.6 client downloaded the exact 0.19.7 full package from loopback. SHA-256 passed and the
  unsigned payload was rejected at trusted publisher verification. Apply remained disabled and the rejected
  package was deleted.
- A synthetic marker at
  `%LocalAppData%\Envious Labs\EnviousWispr-Founder\phase19-uat-preserve.txt` survived install, repeated
  repair, a deliberately interrupted repair, recovery, and uninstall.
- The recovery Setup log showed the old install root renamed for rollback, the package extracted, shortcuts
  updated, the app install hook succeeding, uninstall registration written, and rollback storage removed only
  after success.
- Silent uninstall returned exit code 0. The founder install root and shortcut were removed; the external
  founder data directory and marker remained.
- The local feed was served only on `127.0.0.1:43192` under the explicit UAT gate and was stopped after the
  test. Existing port 8081 and external model runtimes were not touched.

## Unsigned clean-profile follow-up

Commit `1eab02b` was packaged as isolated beta versions `0.24.0-beta.1` and `0.24.0-beta.2`, then exercised
under a newly created standard local Windows account with no prior beta install or data directories. This is
clean-profile evidence on the same Windows 11 Home build 26200 machine, not clean-machine coverage.

- Beta.1 installed silently with exit code 0 into its package-specific per-user root. The install hook,
  uninstall registration, and Start menu shortcut succeeded without affecting the running founder channel.
- The installed WinUI shell opened the real onboarding page, observed the physical microphone, honestly
  reported that local models were absent, completed through its documented keyboard path, and reached Home.
  The app created its normal settings, run-state, and content-free diagnostics in the external beta data root.
- Running the same beta.1 Setup as a repair returned exit code 0, stopped the running beta app, restored the
  current payload, and preserved the app-created settings byte for byte.
- The first beta.2 Setup attempt failed during extraction with `corrupt deflate stream`. Setup and package
  sizes and SHA-256 matched their manifests, the embedded Setup package matched the full nupkg, and all 646
  nupkg entries decompressed independently. Automatic rollback then encountered a transient file lock and
  left a 1,005,879,430-byte rollback directory instead of restoring the active root. This is failed rollback
  evidence, not an admitted update or atomic rollback pass. It is consistent with the external-lock class
  tracked upstream in [Velopack issue 228](https://github.com/velopack/velopack/issues/228).
- Rerunning the known-good beta.1 Setup recovered the installation with exit code 0 and preserved settings.
  A single beta.2 retry with no beta process running then succeeded, reported
  `0.24.0-beta.2+1eab02bf6f430eca2df76f958bf372fa85421a6d`, and preserved settings. The upgraded app opened
  directly on Home rather than replaying onboarding and surfaced its interrupted-run recovery state.
- Silent uninstall returned exit code 0, ran the uninstall hook, stopped the beta process, removed the active
  install root and Start menu shortcut, and preserved the external settings byte for byte. The earlier stale
  rollback directory remained until the temporary test profile was removed.

The beta.1 Setup SHA-256 was
`78DE0DB76916994D0E195F312D6673B64153E640FB7496649CC105BB4F876518`; beta.2 was
`8471A07F8DE9D35C4696514BF2CACBE9806F6B7B8A42D71AC7CA083CC957C776`. Both manifests explicitly marked
the artifacts `signedForProduction: false`.

## Defects found and corrected by native UAT

1. `dotnet publish` omitted unpackaged WinUI PRI/XBF resources. The installed shell failed with a XAML parse
   exception. Packaging now copies and asserts `EnviousWispr.App.pri`, `App.xbf`, `MainWindow.xbf`, and
   `DictationOverlayWindow.xbf`; the repaired installed shell then ran.
2. Broad runtime-worker publish copying caused duplicate UI Automation publish files. Copy rules now include
   only the worker executable family and keep UI Automation dependencies in build output without duplicate
   publish items.
3. Delta reconstruction changed nupkg bytes, so an independent comparison to the full-release hash failed.
   Deltas are now disabled in the client to preserve exact artifact admission.
4. Velopack extracts the package's updater during download before the app's independent publisher check.
   The app now backs up the installed updater and restores it whenever admission fails.

## Source evidence

Velopack's integration contract requires `VelopackApp.Run()` before app initialization. Its channel model,
per-user Windows layout, self-contained publish guidance, signing flow, and update APIs were verified against
the official 1.2.0 documentation and source. Repository distribution contracts remain authoritative for
EnviousWispr product behavior.

## Not yet observed

- No paid Azure Artifact Signing account or final production certificate was available. No artifact in this
  UAT is suitable for distribution.
- A valid Envious Labs-signed package reaching Apply, atomic restart, forced apply failure, and last-known-good
  rollback remains unobserved.
- Clean-machine tests across every supported Windows version remain unobserved.
- Production HTTPS feed hosting, CDN behavior, interrupted/resumed remote downloads, SmartScreen reputation,
  Store/MSIX, and representative endpoint-security products remain unobserved.

Phase 19 implementation is ready for review, but the master-plan exit gate is not complete until these
founder-controlled signing and clean-machine validation items are executed.
