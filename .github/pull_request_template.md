## Outcome

What changed, why, and which issue does this complete or advance?

## Local proof

- [ ] Full local Debug validation: `powershell -ExecutionPolicy Bypass -File scripts/validate.ps1 -Configuration Debug`
- [ ] Local review is clear; applicable findings are resolved

Commands, revision and observed results:

## Pull-request gate

- [ ] This PR's `build-and-test` passed the full Release portable gate and Store-package check
- [ ] Review is clear before squash auto-merge is armed

Cloud review outcome for the final HEAD: qualifying verdict, findings resolved with local all-clear, or recorded absent-review waiver. Include the HEAD SHA.

## Relevant behavior and risks

For product/runtime changes, list task-specific tests and native Windows paths observed. Include evidence that failure and cancellation preserve the last valid result and that hardware/model changes retain a safe fallback. Name unobserved conditions and absent-model skips. Workflow-only changes cite the modified workflow run instead of dictation UAT. Model-dependent validation uses `-Configuration Release -IncludeLocalRuntime`.

- [ ] No secrets, model weights, user content, private machine paths or transcripts were committed
- [ ] Logs and telemetry remain content-free

Remaining risks or deliberate validation gaps:
