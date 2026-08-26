# Phase 16 model delivery and storage evidence

Measured on the founder's Windows rig on 2026-08-26. The implementation is complete at the reusable
delivery/store boundary. Production catalog activation remains intentionally gated on founder-controlled
signing keys, final source URLs, and approved license notices.

## Implemented

- Signed envelopes use ECDSA P-256 with SHA-256 over exact manifest payload bytes. The verifier accepts only
  configured public-key ids, schema 1, safe model identities and paths, semantic versions, supported minimum
  app versions, HTTPS sources, exact sizes and SHA-256 values, and complete per-model license notices.
- The manifest client has a one-megabyte bound and never discovers or lists remote repositories at runtime.
- Downloads are single-flight across threads, store instances, and processes. They stage outside the active
  version, use ordered signed sources, retry only bounded transient failures, honor bounded `Retry-After`,
  and resume only with Range plus an ETag or Last-Modified `If-Range` validator.
- Disk space is checked before network transfer. Every artifact is size- and hash-verified before admission;
  unexpected cache files are removed before promotion. The active pointer is written only after an exact,
  complete version directory is ready.
- Version/digest directories keep upgrades and downgrades isolated. Offline open re-verifies the signature,
  active pointer, exact file set, hashes, and license notice without network access. Cleanup preserves the
  active version and a configured rollback count. Clean removal is confined to one validated model/version.
- Legacy flat directories migrate by verify, copy, verify, promote, activate, then remove only manifest-listed
  legacy files. An interrupted migration leaves the original usable.
- Diagnostics expose typed events and aggregate byte counts only. No model path, source URL, machine id,
  transcript, audio, credential, or user content field exists.

## Automated evidence

- `powershell -ExecutionPolicy Bypass -File scripts/validate.ps1` passed at revision under test. The
  preserved proof passed 34/34 tests and the production suite passed 285/285 tests. Every Release build
  completed with zero warnings and zero errors.
- Seven focused production tests cover signature and payload tampering, unsafe paths, interrupted resume,
  corrupt artifacts, insufficient disk before network access, ordered source fallback, upgrade, downgrade,
  offline reuse, exact license inventory, legacy migration, cleanup, clean removal, and traversal rejection.
- `dotnet run --project tools/model-delivery-uat/EnviousWispr.ModelDelivery.Uat.csproj -c Release` passed over
  a real loopback TCP/HTTP connection. It verified two signed manifests, observed one dropped transfer, sent
  one Range request and one matching If-Range validator, upgraded once, downgraded once, reused offline twice,
  and removed one obsolete version. It generated synthetic bytes and processed no user content.

## Native Windows acceptance observed

- The Release x64 WinUI app launched in an isolated data directory. UI Automation exposed the native
  onboarding surface, local-transcription readiness, microphone readiness, F8 instruction, privacy copy,
  and accessible control names.
- The bounded native run exited normally. Its run marker recorded `cleanShutdown: true`; diagnostics ended
  with `ShellClosed` and `ApplicationCleanShutdown`; the exact app PID had no remaining child processes.
- No real model file, protected model runtime, unrelated server, system PATH, privacy setting, or security
  setting was changed.

## Remaining release inputs and unobserved conditions

- Production public trust keys, signed Parakeet/Whisper/preview/EG-1 manifests, final CDN/upstream source
  order, and approved model license notices are founder/release inputs and are not invented or committed here.
  Until those are supplied, the existing founder/development model-directory overrides remain intact.
- The native harness proves the Windows network/filesystem path with bounded synthetic artifacts. A real
  multi-gigabyte model transfer, CDN failover, metered-network behavior, physical disk exhaustion, antivirus
  interference, and long-haul resume across reboot remain unobserved.
- The application surface explains signed, pinned, offline-capable model packs. A production download/remove
  UI becomes meaningful only when the approved catalog and trust roots exist; this change does not present a
  nonfunctional catalog or fabricate release infrastructure.
