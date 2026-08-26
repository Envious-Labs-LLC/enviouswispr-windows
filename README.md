# EnviousWispr for Windows

EnviousWispr is a native Windows 11 dictation app: hold the configured key, speak, release, and continue
working in the app that was focused when recording began. Audio and local transcription stay on the PC.

This repository is **pre-release**. It contains a preserved founder-tested WPF proof and the production
.NET 10 / WinUI 3 application that is replacing it capability by capability. No artifact in the repository
or its draft pull requests is approved for public distribution.

## Production product shape

- WASAPI capture with device routing, 16 kHz mono conversion, interruption handling, and recovery.
- Configurable global push-to-talk with frozen-target delivery and safe refusal when Windows blocks access.
- Isolated Parakeet and Whisper final transcription, automatic CPU-safe selection, and crash recovery.
- Separate multilingual Whisper preview that cannot affect the final transcript.
- Deterministic custom words, cleanup, spoken punctuation and emoji, inverse text normalization, and
  cursor-aware repair.
- Optional local EG-1 or Ollama polish and direct BYOK OpenAI, Anthropic, or Gemini polish. Cloud polish is
  opt-in and sends text only to the provider selected by the user; audio never leaves the PC.
- Onboarding, overlay, tray, settings, history, dictionary, snippets, import/export, updates, diagnostics,
  accessibility, and localization foundations.
- Self-contained Velopack founder, beta, and stable identities with isolated data and update channels.

The product contracts are authoritative. Source code and tests prove implementation; dated notes record
measurements and experiments but do not redefine the forward-looking product.

## Current measured evidence

On 2026-08-26, one AC-powered Windows 11 25H2 NVIDIA desktop produced these production-path results:

| Measurement | Observed result |
| --- | ---: |
| WinUI shell warm startup p95 | 609 ms |
| App plus Parakeet worker warm startup p95 | 2,313 ms |
| Combined ready working set | 973–975 MB |
| Parakeet CPU, 10 / 20 / 91.5 second public fixtures | 390 / 696 / 3,786 ms |
| Production WinUI public-fixture journey | 3/3 passed; 8,827–12,206 ms |
| Reliability lifecycle | 1,000 cycles, handle delta 9 |
| Portable contract suites | 34 proof + 350 production tests |

Those numbers do not establish public hardware requirements. Whisper CPU latency, Spanish fixture quality,
CUDA runtime delivery, battery and thermal behavior, lower-spec laptops, signed lifecycle testing, and
multi-machine private-beta daily use remain open release evidence.

## Build and validate

Run the canonical Windows gate from the repository root:

```powershell
pwsh -NoProfile -File .\scripts\validate.ps1
```

It builds the preserved .NET 8 proof, the .NET 10 production application and UAT tools, verifies the bundled
runtime worker, checks release-compliance artifacts, and runs both contract suites. On a machine with the
gitignored pinned model packs, add `-IncludeLocalRuntime` for the real Parakeet, Whisper, and production WinUI
public-fixture journey gates. The journey's physical microphone and global-key boundary still requires a
separate human pass; see [its UAT contract](tools/app-journey-uat/README.md).

The production executable after a Release/x64 build is under:

```text
src/Production/EnviousWispr.App/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/
```

Unsigned packaging is only for isolated installer UAT. Production packaging fails closed unless approved
Azure Artifact Signing metadata is supplied:

```powershell
pwsh -NoProfile -File .\scripts\package-windows.ps1 `
  -Version <version> -Channel founder `
  -AzureTrustedSignFile <secure-signing-metadata>
```

Never commit signing metadata, credentials, model weights, private machine paths, audio, transcripts, or
user content.

## Repository layout

```text
src/EnviousWispr/          preserved WPF/.NET 8 proof
src/Production/            production WinUI app, modules, worker, and architecture tests
tools/                     native and model-dependent Windows UAT tools
scripts/                   canonical validation, packaging, compatibility, performance, and release gates
.claude/knowledge/         forward-looking product and architecture contracts
docs/                      durable operator, privacy, compatibility, and release documentation
notes/                     dated measurements and experiment evidence
models/                    local model packs, ignored by Git
```

## Release status

The direct installer is the primary distribution path; Microsoft Store/MSIX is a later secondary option.
Draft release gates exist, but public release still requires a valid Envious Labs signature, immutable HTTPS
feeds, clean-machine install/update/rollback/uninstall, representative laptop and target-app evidence,
reviewed model/CUDA licenses, security and privacy review, private-beta daily use, and Saurabh's explicit
approval for the exact release candidate. Pull requests are never merged automatically.

Start with [CLAUDE.md](CLAUDE.md), the [product contract](.claude/knowledge/product-contract.md), and the
[Windows master plan](docs/plans/windows-master-plan.md). Release operators should also read the
[distribution runbook](docs/distribution/windows-release.md) and
[public-release gate](docs/distribution/public-release.md).

## Security, privacy, support, and license

- Report vulnerabilities through [GitHub private vulnerability reporting](https://github.com/Envious-Labs-LLC/enviouswispr-windows/security/advisories/new), not a public issue. See [SECURITY.md](SECURITY.md).
- Read the engineering privacy notice in [PRIVACY.md](PRIVACY.md).
- Use the bounded support and private-beta forms described in [SUPPORT.md](SUPPORT.md).
- EnviousWispr source is licensed under GNU GPL version 3 only; see [LICENSE](LICENSE). Production NuGet
  notices are generated in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Separate model and native
  redistributable source evidence and pending decisions are tracked in the
  [artifact license inventory](docs/distribution/artifact-license-inventory.md).

Copyright © 2026 Envious Labs LLC.
