## Outcome

What changed, why, and which issue does this complete or advance?

## Local proof

- [ ] Full local Debug validation: `powershell -ExecutionPolicy Bypass -File scripts/validate.ps1 -Configuration Debug`
- [ ] Local review is clear; applicable findings are resolved

Commands, revision and observed results:

## Pull-request gate

- [ ] This PR's `build-and-test` passed the full Release portable gate and Store-package check
- [ ] Review is clear before squash auto-merge is armed

## Relevant behavior and risks

For product changes, name the native Windows user path observed, failure/fallback evidence and any unobserved conditions. Workflow-only changes cite the modified workflow run instead of dictation UAT. Model-dependent validation uses `-Configuration Release -IncludeLocalRuntime`.

- [ ] No secrets, model weights, dictated content or private machine paths were committed

Remaining risks or deliberate validation gaps:
