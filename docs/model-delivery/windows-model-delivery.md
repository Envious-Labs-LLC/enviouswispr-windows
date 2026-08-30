# Windows model delivery contract

This document describes the Phase 16 implementation in `EnviousWispr.ModelDelivery`. The product-level
contracts in `.claude/knowledge/` remain authoritative.

## Trust and manifest format

The delivery client accepts a JSON envelope with four fields:

```json
{
  "envelopeVersion": 1,
  "keyId": "release-key-id",
  "payloadBase64": "base64-encoded UTF-8 manifest JSON",
  "signatureBase64": "ECDSA P-256 SHA-256 signature of the exact payload bytes"
}
```

The payload pins `schemaVersion`, `modelId`, semantic `version`, `minimumAppVersion`, one license notice,
and every artifact's safe relative path, exact byte count, SHA-256, and ordered source URLs. Production
sources must use HTTPS. Loopback HTTP is available only when the caller explicitly creates a verifier for
local acceptance testing.

The application or release pipeline supplies the public-key map. Private signing keys never belong in this
repository, application binaries, diagnostics, or model manifests. Key rotation uses a new `keyId`; an app
update adds the new public key before manifests begin using it and removes retired keys only after affected
model versions are no longer supported.

## Download and admission

One writer per model is enforced both in process and with a filesystem lock. Each artifact follows this
sequence:

1. Check free space for the remaining pinned bytes plus the configured reserve.
2. Download into the manifest-specific staging directory.
3. Resume a partial file only with HTTP `Range` and a matching `If-Range` ETag or Last-Modified validator.
4. Retry transient transport failures with bounded exponential full-jitter backoff and honor `Retry-After`
   up to ten seconds. Permanent source failures move to the next signed source.
5. Require the exact byte count and SHA-256 before the artifact receives its final name.
6. Remove unlisted staging files, write the signed envelope and license notice atomically, and atomically
   move the complete directory into the versioned store.
7. Update `active.json` only after the complete version is admitted.

Cancellation leaves validator-backed partial bytes in staging for a later resume. No partial or unverified
artifact is visible through the active model API.

## Store layout

```text
models/
  <model-id>/
    .delivery.lock
    active.json
    .staging/<version>-<manifest-digest>/...
    versions/<version>/<manifest-digest>/
      .model-manifest.json
      .license-notice.txt
      <pinned artifacts>
```

The manifest digest permits safe replacement of a manifest without mixing bytes. Offline open verifies the
signature, pointer, exact file set, sizes, hashes, and license notice without making a network request.
Installing a newer version does not delete the prior known-good version. Downgrade changes only the active
pointer. Cleanup keeps the active version plus a caller-selected number of inactive versions. Removal is
confined to one validated model id and semantic version.

`MigrateLegacyAsync` verifies a legacy flat model directory against a signed manifest, copies and verifies
it in staging, activates the versioned copy, and only then removes the listed legacy artifacts. An
interruption therefore leaves either the old usable layout or the new admitted layout.

## Licensing and privacy

Every signed manifest carries a model-specific license name, URL, and notice. Admission persists that exact
notice beside the model, and inventory/open APIs return it to the UI. A changed or extra cache file makes
offline validation fail closed.

Delivery diagnostics contain only event codes, typed failure categories, and optional aggregate byte
counts. They have no fields for model paths, URLs, machine identifiers, transcript text, audio, credentials,
or user content.

## Validation

Portable behavior is covered in `ModelDeliveryTests`. The native Windows harness at
`tools/model-delivery-uat` uses a real loopback TCP/HTTP connection and synthetic bytes to verify remote
signed-manifest fetch, a dropped connection, validator-backed resume, upgrade, downgrade, offline reuse,
and cleanup. It does not download or modify real model weights.

The release owner must supply and approve production public keys, manifests, source URLs, and license text.
The repository intentionally does not invent a production signing identity or commit a private key.
