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
