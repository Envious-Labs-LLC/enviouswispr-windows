# Founder and private-beta release gate

The founder and beta channels are release candidates only after the signed artifact set and one
content-free lifecycle evidence record pass `scripts/validate-release-candidate.ps1`. This gate is
independent of packaging: it rehashes the immutable artifact set, rechecks the isolated channel identity,
requires a valid Envious Labs signature on Setup and every packaged executable, and refuses unsigned
development output.

Lifecycle evidence is a bounded record, not a free-text report. Copy
`docs/distribution/private-beta-evidence.example.json` outside the repository for each coarse machine class,
replace only the typed values, and keep every check `unobserved` or `failed` until the exact native path has
been witnessed. Do not include paths, machine names, accounts, device identifiers, crash text, transcript,
audio, clipboard data, credentials, endpoint secrets, or user feedback in this record. Link detailed
privacy-safe evidence from the corresponding GitHub issue.

Run the admission gate after packaging and native lifecycle UAT:

```powershell
pwsh -NoProfile -File .\scripts\validate-release-candidate.ps1 `
  -DistributionDirectory <immutable-founder-or-beta-output> `
  -Channel founder `
  -Version <version> `
  -EvidenceFile <content-free-evidence.json>
```

All twelve native checks must be `passed`, all three approvals must be `true`, and
`blockerIssueNumbers` must be empty. Approval values mean the founder approved this exact release, the
channel-specific immutable HTTPS update endpoint is operational, and the telemetry server minimization,
access, retention, deletion, incident-response, sampling, and regional-processing policy is approved.
They are not substitutes for those decisions.

The gate does not publish, upload, sign, install, update, roll back, merge, or close issues. A passing
record for one machine class does not cover another. Store the signed artifacts immutably, publish the new
channel index last, retain the last-known-good signed release, and record daily-use, feedback, and crash
triage in issue #50. Founder and beta remain unavailable while the checked-in example stays red and the
current signing, endpoint, compatibility, performance, and daily-use blockers remain open.
