# Distribution and update contract

## Primary path: the Microsoft Store

**Decided 2026-09-05 (#42), replacing the Velopack direct-installer path below the fold.** The app ships as a
self-contained `win-x64` **MSIX** through the Microsoft Store: Microsoft signs it, the Store delivers updates,
SmartScreen never sees it, and no signing account exists anywhere. Direct download is not offered. The
Velopack code, scripts and gates remain in the tree until Phase 19 step E removes them; the plan, its three
validation rounds and the acceptance evidence are on #42.

The promises the old path made are kept in Store terms: the app never updates while recording or processing
(it checks the Store itself and requests install only when idle); user data lives outside the package and
survives uninstall; recovery is a higher-version package. Signature and hash validation of updates is the
Store's job, and the package gate verifies identity, version and a complete payload instead.

## Signing

None to arrange. The Store replaces any signature on an MSIX with Microsoft's. SignPath Foundation and Azure
Trusted Signing, both weighed on 2026-09-05, are retired with the direct-download path. The only certificate
in the project is a **self-signed development certificate** for sideloading test builds, whose public half
must be imported into the test machine's *Trusted People* store before a sideload installs - a signed package
alone does not establish device trust.

## Store packaging decisions

Each of these was a founder decision or a validator finding on #42; the reason is the part to keep.

- **Execution model:** `packagedClassicApp` at medium integrity via `runFullTrust`, with `internetClient`,
  `microphone`, and a declared `windows.startupTask` extension whose user-disabled state the app honours.
  `runFullTrust` is a restricted capability and needs a justification at certification.
- **Storage:** `%LocalAppData%\Envious Labs\EnviousWispr` is excluded from virtualization
  (`unvirtualizedResources` plus the Windows 11 folder exclusion, scoped to the product folder), so it
  survives uninstall. Migration must handle mixed storage - pre-existing physical files beside newly
  virtualized writes - and the exclusion's approval is a real gate. The customer's delete/export controls are
  in-app; see `product-contract.md`.
- **Identity and channels:** one production identity; private audience first, then package flights. A flight
  updates the same install, so `JsonSettingsStore` (which rejects newer schemas) must always be met by a
  schema-compatible migration, and a recovery package carries a higher version.
- **Credentials are not isolated by the data directory.** The app selects the production credential store
  unless a UAT suffix is present, so development and acceptance runs use their own credential namespace and
  the override-free clean-user acceptance runs in a separate Windows account or VM, never the founder's.
- **Payload:** the shell, the RuntimeWorker (its copy targets must run before payload collection), native
  libraries with CUDA **beside the executable** (where the dependency probe already looks), and
  `runtime\llama-server.exe` with its dependencies - the app currently finds llama-server in the data folder
  first and the install directory last, and no script stages it. Store policy 10.2.2 governs dynamic code.
- **Toolchain:** CLI packaging through the documented `dotnet` target, with the Windows SDK, packaging tool
  and `msstore` CLI versions pinned in the packaging script and mirrored in CI; `windows-latest` is not a pin.
  If Visual Studio tooling is ever used, `net10.0` needs VS 2026 (18+).
- **Proof:** the journey harness overrides data, model and runtime paths and so cannot prove packaged storage;
  packaged validation adds identity assertions and a clean-user mode without overrides. WACK is necessary,
  not sufficient. The Store-delivered update is observed after the first private certification.

## Retired: direct download

Not offered. A sideload MSIX exists only for testing, signed by the development certificate.

## Model delivery

Speech models are not in the installer. A fresh install carries three DELIVERY MANIFESTS and no
weights, and downloads what its configuration needs on request: the Parakeet final model by default,
the Whisper final model when that engine is selected, and the Live Preview model when preview is on.
Ref: #92, which found the store built, tested and constructed nowhere.

### RULE: the-manifest-is-bundled-and-the-package-is-the-trust-root
`models/manifests/<modelId>.json` is embedded into `EnviousWispr.ModelDelivery.dll` at build time and
never fetched over the network. What a build can install is fixed when it is built and changes only
through an update - the macOS contract's invariant 4, restated for Windows. There is no signing key on
either platform, deliberately: the Mac's `docs/model-delivery/model-delivery-contract.md` (v1.3) makes
the signed app bundle the trust root, and Windows does the same with the signed package.

Each manifest carries `manifestDigest`, SHA-256 over its canonical JSON (sorted keys, no whitespace,
slashes unescaped, the digest key removed), the Mac's rule exactly. **The digest is a self-check against
a hand edit, and nothing more.** The guarantee is per-file SHA-256 at admission, owned by `ModelStore`.

The signed-envelope path (`ModelManifestVerifier.Verify`, `ModelManifestClient`) stays in the library
and is reachable from nothing in the app. `VerifyBundled` is a separate entry point on purpose: a mirror
that served an unsigned document could never have it admitted.

### FACT: where-the-bytes-come-from
Cloudflare R2, bucket `enviouslabs-models`, served as `https://models.enviouslabs.co`, the same bucket the
Mac app uses. Windows has its own prefixes, `/<family>/<revision>/` with the revision being the upstream
Hugging Face commit, because the `/parakeet/` prefix is scoped to the Mac's CoreML files by the
Cloudflare cache rules and the Worker allowlist:

| prefix | files | upstream, byte-identical |
|---|---|---|
| `parakeet-onnx/8f23f0c03c8761650bdb5b40aaf3e40d2c15f1ce/` | 5, 670 MB | `istupakov/parakeet-tdt-0.6b-v3-onnx` |
| `whisper-ggml/5359861c739e955e79d9a303bcbc70fb988958b1/` | 2, 764 MB | `ggerganov/whisper.cpp` |

Every file lists the mirror first and the PINNED Hugging Face `resolve/<commit>/` URL second, and the
store fails over per file.

### RULE: nothing-on-the-mirror-is-over-512-MB
The zone is on Cloudflare's Free plan, and the largest object the edge will cache is 512 MB on Free, Pro
and Business alike; only Enterprise raises it. An object over that line serves from the single R2 origin
region, uncached, to every user - the far-from-origin first-run stall the Mac's #1405 was built to fix.
The Parakeet encoder (652 MB) and Whisper large-v3-turbo (574 MB) are the first two objects over it.

So they ship as PARTS: `<file>.part0`, `.part1`, `.part2`, each 256 MiB or less, under the same prefix.
The manifest carries them as `parts` on the file - each with its own size, hash and mirror-only source -
and keeps the whole-file hash and the whole-file sources. The store fetches the parts with the same
Range/If-Range resume, verifies each, concatenates in order, verifies the WHOLE against its hash, and
only then admits. If any part cannot be had, it deletes what it fetched and takes the whole-file sources,
so the shard layer can make delivery faster and never make it fail. The three scenarios - every part
served, one part missing, one part corrupt - are the tests.

256 MiB is also under `wrangler r2 object put`'s 300 MiB ceiling, so the Mac can publish every object
without the multipart credential that a whole 652 MB file would need. The macOS EG-1 model ships the
same way, as eight shards under `/eg1/v2-sharded/`. `resolve/main/` is not a pin and the test suite refuses it. Until an object
exists on the mirror, the mirror answers 404 and the backup carries the download; this is how the flow
was proven end to end before anything was uploaded.

**Both Windows prefixes are edge-cached**, founder-approved and live 2026-09-05: cache eligible, edge TTL
one year, `Cache-Control: public, max-age=31536000, immutable, no-transform`, the same two rules as
`/parakeet/`. Measured on real ranged GETs twice each: MISS then HIT on a shard, HIT with an age on a
whole file, 206 throughout, so Range survives the cache. The cache-settings ruleset is `/parakeet/`,
`/eg1/v2-sharded/`, `/s1/`, `/parakeet-onnx/`, `/whisper-ggml/`. **Editing it from this side: append with
`POST /zones/{zone}/rulesets/{id}/rules`; a `PUT` replaces every rule with no lock and no warning.**

**A cache rule caches its NEGATIVES, so deploy order is load-bearing.** A 404 served once for a path is
stamped with the rule's one-year TTL, and re-uploading the object does NOT evict it - the edge stops
asking the origin. Measured 2026-09-05 on the two whole-file URLs of the sharded entries, which had been
requested before the decision not to upload them; both were purged by file. Upload every object BEFORE
any client can request its path, and if a path ever served a 404 that later gets a real object, purge it:
`POST /zones/{zone}/purge_cache` with `{"files": ["<url>"]}`. This is also why a sharded file's whole-file
`sources` name only the pinned Hugging Face URL: the mirror never holds the whole, and a mirror URL that
cannot resolve is a guaranteed 404 on every fallback and a cached one after the first.

**A checker that reads `cf-cache-status` without the status line calls a cached 404 a success.** Gate on
200 or 206 AND `HIT`, never on `HIT` alone.

**Probe the edge with a real GET twice, never HEAD.** Cloudflare does not cache HEAD, so `curl -sI`
reports `cf-cache-status: DYNAMIC` on a correctly cached prefix and cannot pass. The honest check is
`curl -sS -o /dev/null -D - <url>` run twice, reading `cf-cache-status` and `age` on the second.
`tools/model-manifest provision` prints that header on every request the store makes.

**Uploads happen from the Mac.** R2 tokens are account-level, and that account's R2 also holds the primary
backups, so a write token on this PC would be delete authority over them. That is a founder decision
that has not been made; route uploads through the Mac session.

### PROC: changing-a-model
1. Put the new files in a local directory and run
   `dotnet run --project tools/model-manifest -- create --model-id <id> --version <semver> --minimum-app <version> --directory <dir> --file <name>... --mirror <base> --backup <pinned base> --license-name ... --license-url ... --license-notice ... --output models/manifests/<id>.json`.
   Sizes and hashes come from the files, never from the command line.
2. `dotnet run --project tools/model-manifest -- verify models/manifests/<id>.json --directory <dir>`
   re-hashes every file. The gate runs `verify` without a directory on every manifest.
3. Ask the Mac session to upload to the new `/<family>/<revision>/` prefix and confirm the live URLs,
   BEFORE any build carrying the manifest runs (see the cached-negatives rule above).
4. Bump `version` - the store keys installed copies by version and digest, so a changed manifest with the
   same version is a different install, not an upgrade in place.

### FACT: how-a-model-is-found
`InstalledModelLocator.Resolve`, in this order: the `ENVIOUSWISPR_MODEL_DIRECTORY` override; the version
the store activated under `<data>/models/<modelId>/versions/`; the legacy hand-copied `<data>/models/<modelId>`
when the probe says it is COMPLETE; the development checkout's `models/<modelId>` when complete. The
old resolver returned the first directory that EXISTED, and the store keeps its staging files under the
legacy path, so a half-finished download used to read as an installed model that then failed to load.
