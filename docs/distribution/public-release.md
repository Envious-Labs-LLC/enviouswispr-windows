# Public-release admission

Public distribution is a founder-approved decision about one immutable signed candidate, not a property of
a branch, passing compile, or unsigned package. The operator begins with the Phase 22 founder/beta admission
and then verifies every Phase 23 evidence class below.

| Evidence class | Required proof | Current state |
| --- | --- | --- |
| Product parity | Agreed feature matrix and native end-to-end UAT across the final pipeline | Incomplete |
| Legal | GPL source notice plus reviewed notices for every NuGet, native runtime, model, and CUDA artifact | NuGet inventory automated; model/CUDA review open |
| Privacy | Approved user notice, telemetry server record, schema/redaction review, and deletion procedure | Engineering boundary implemented; approval open |
| Security | Private reporting, secret scanning, dependency review, threat review, and signed artifact scan | Repository protections enabled; candidate review open |
| Accessibility | Keyboard, screen reader, contrast, DPI, multi-monitor, locale, RTL, and IME evidence | Partial matrix only |
| Compatibility | Written Windows/hardware/audio/display/security/target-app requirements from real machines | Single desktop cell only |
| Performance | Latency, memory, battery, power, and thermal budgets on target laptops | Desktop baseline only |
| Distribution | Valid signature, clean install, admitted update, atomic restart, forced failure, rollback, repair, uninstall, and preservation | Unsigned UAT only |
| Operations | HTTPS feeds, rollback owner, support intake, crash triage, incident response, and retained last-known-good release | Contracts present; production operation open |
| Approval | Saurabh approves the exact version, channel, hashes, evidence record, and public exposure | Not granted |

Run repository compliance first:

```powershell
pwsh -NoProfile -File .\scripts\audit-public-release.ps1 -VerifyGitHubSecurity
```

Then run the Phase 22 admission against the exact immutable signed founder/beta artifact and content-free
lifecycle evidence. The final stable candidate repeats clean install/update/rollback/uninstall and complete
native UAT on every published supported class. Preserve the result in GitHub issue #52; raw user content,
credentials, private machine paths, audio, transcripts, crash dumps, and signing metadata never enter the
repository or evidence record.

The final action is an explicit founder decision. The gate never merges, signs, uploads, publishes, enables
paid infrastructure, or converts an unobserved cell into a pass.
