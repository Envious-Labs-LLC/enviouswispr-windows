# Phase 22 private-beta release evidence

Date: 2026-08-26

## Implemented release controls

- `scripts/validate-release-candidate.ps1` independently verifies founder/beta identity, immutable manifest
  membership, size and SHA-256, required Setup/full-package/index artifacts, the Envious Labs Authenticode
  publisher on Setup and every packaged executable, and a bounded lifecycle evidence schema.
- A candidate cannot pass unless twelve native lifecycle/support checks are `passed`, the exact release,
  update endpoint, and telemetry server policy have explicit approvals, and the evidence lists no open P0
  or P1 blockers.
- The checked-in evidence example is intentionally unobserved, unapproved, and blocked. Canonical validation
  parses the gate and enforces that this example stays red.
- Privacy-safe GitHub forms bound founder/private-beta bug, crash, and product feedback intake. They warn
  against transcript, audio, clipboard, context, credentials, accounts, private paths, window titles, raw
  logs, crash dumps, and device identity.
- The server approval contract defines schema rejection, body-log exclusion, connection-metadata
  minimization, access, retention, deletion, incident response, sampling, regional processing, and renewed
  review requirements. Client upload remains disabled until a concrete operating record is approved.

## Validation

- The current unsigned founder 0.21.1 package was rejected before lifecycle evidence with exit code 1
  because `signedForProduction` was false.
- The canonical validation gate passed: 34 preserved-proof tests, 350 production tests, all Release builds,
  the bundled worker launch assertion, the Phase 22 script parse, and the deliberately red evidence check.
- Phase 21 draft PR #49 completed both CI workflows successfully before this branch was created.

## Unobserved and blocked

No production signing identity, signed artifact, immutable HTTPS feed, telemetry endpoint approval, signed
install/update/apply/rollback, clean target machine, SmartScreen result, representative endpoint-security
result, private-beta daily use, or production feedback/crash triage has been observed. Open Phase 19–22
signing, CUDA delivery, compatibility, performance, and release issues remain release blockers. Phase 22 is
therefore infrastructure-ready but not release-ready.
