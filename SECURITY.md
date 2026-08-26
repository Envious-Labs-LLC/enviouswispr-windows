# Security policy

## Supported versions

EnviousWispr for Windows is pre-release. The active production branch and an explicitly identified signed
founder or beta candidate receive security fixes; preserved proof builds, experiments, unsigned packages,
and superseded candidates are not supported releases. A stable supported-version table will be published
before public distribution.

## Report a vulnerability privately

Use [GitHub private vulnerability reporting](https://github.com/Envious-Labs-LLC/enviouswispr-windows/security/advisories/new).
Do not open a public issue for a suspected vulnerability. Include the affected version/channel, Windows
build, coarse hardware class, affected component, impact, and sanitized reproduction steps.

Do not include dictated or polished text, audio, clipboard or surrounding text, credentials, API keys,
account names, private paths, window titles, device identifiers, raw diagnostics, or crash dumps. If the
maintainer needs a sensitive artifact, agree on a private minimum-data transfer method first. Never test
against another person's account, machine, data, or service without authorization.

## Security boundaries

Audio must remain local. Cloud polish is direct BYOK and may receive text only after explicit provider
selection. Secrets belong in Windows Credential Manager. Updates and model packs require identity,
signature, and hash admission. Runtime workers are isolated and failures must preserve the last safe text.
Telemetry is consented, content-free, and schema allowlisted.

GitHub private vulnerability reporting, secret scanning, push protection, validity checks, and automated
dependency security fixes are enabled for this repository. A public release also requires review of the
exact signed artifact, dependencies, native runtimes, model licenses, installer/update path, and server-side
telemetry policy. This file is a reporting and engineering policy, not a warranty or response-time SLA.
