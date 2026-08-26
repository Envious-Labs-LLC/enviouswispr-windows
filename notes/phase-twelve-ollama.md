# Phase 12: local Ollama polish

## Contracts

- READ 2026-08-26 — `docs/plans/windows-master-plan.md` Phase 12 requires localhost discovery,
  model listing, manual endpoint support, health checks, cancellation, offline UX, multiple-model
  tests, and deterministic fallback when the service stops mid-request.
- READ 2026-08-26 — `.claude/knowledge/product-contract.md` says Ollama stays on-device. The Windows
  adapter therefore accepts only HTTP/HTTPS loopback endpoints and refuses `/api/tags` rows with a
  non-empty `remote_host` before `/api/chat`.
- READ 2026-08-26 — macOS `OllamaConnector.swift`, `OllamaSetupService.swift`,
  `LocalFixedPromptBuilder.swift`, and the routed LLM/Ollama knowledge define the reusable provider
  behavior: `/api/tags` per-attempt truth, native `/api/chat`, fixed local L3 prompt, `keep_alive:60m`,
  no `num_ctx`, capability-driven thinking, and deterministic fail-down.
- READ 2026-08-26 — `capabilities` is three-state. Reported `thinking` uses `think:"low"` and a 2,048
  output floor; a reported non-thinking model omits `think` and uses 256; an absent capability list
  omits `think` and conservatively uses 2,048. Models explicitly reporting no `completion` capability
  are excluded from the chat catalog.

## Implementation

- MEASURED 2026-08-26 — the provider accepts `localhost`, IPv4 loopback, or IPv6 loopback, normalizes
  the base URI, and rejects non-loopback hosts, credentials, query strings, fragments, and API paths.
  The persisted endpoint is optional; app settings migrated from v3 to v4 and portable profiles from
  v2 to v3 without losing provider/model selections.
- MEASURED 2026-08-26 — discovery and startup health use a single non-retried `/api/tags` call with a
  one-second deadline. Every polish attempt probes again so a daemon or model change cannot be hidden
  by cached readiness.
- MEASURED 2026-08-26 — polish uses the validated 1,813-character local L3 system prompt, a plain
  `Transcript to clean:` user message, temperature zero, a computed output cap, a 15-second total
  generation budget, caller cancellation, and bounded 1s/3s retries. Explicit truncation, empty
  output, excessive expansion/drop, code-shaped output, transport failure, and timeout all return the
  deterministic input.
- MEASURED 2026-08-26 — diagnostics contain provider, typed error code, and duration only. No
  transcript, prompt body, model response, credential, or user content is logged.
- MEASURED 2026-08-26 — the WinUI app composes Ollama from settings or the
  `ENVIOUSWISPR_OLLAMA_ENDPOINT` / `ENVIOUSWISPR_OLLAMA_MODEL` test overrides. It visibly identifies
  local-only routing and surfaces endpoint, daemon, model, timeout, remote-model, and truncation
  fallback states.

## Validation

- MEASURED 2026-08-26 — `scripts/validate.ps1` passed: preserved proof 34/34, production architecture
  211/211, every Release build zero warnings/errors, including the new Ollama UAT harness.
- MEASURED 2026-08-26 — mocked coverage passed for three model-capability shapes, local/remote and
  embedding filtering, canonical `:latest` matching, endpoint safety, request shape, remote refusal,
  caller cancellation, timeout, truncation, code output, and a controlled daemon stop after readiness.
  The controlled stop made three bounded chat attempts and preserved the exact deterministic input.
- MEASURED 2026-08-26 — the existing Ollama inventory at `localhost:11434` contained ten eligible
  local chat models and one embedding-only row. No model was installed, pulled, deleted, or unloaded.
- MEASURED 2026-08-26 — real synthetic-text UAT passed semantically on `ministral-3:14b`
  (non-thinking, 5,617 ms warm) and `qwen3:14b` (thinking, 10,628 ms). The test verified the repair
  kept Friday and removed Thursday, "no wait", and the filler "um" without printing model output.
- MEASURED 2026-08-26 — cold and warm `qwen3.6:27b` exceeded the 15-second budget; both attempts
  returned `PolishTimedOut` with the exact deterministic input. This is valid fail-down evidence, not a
  quality pass for that model on this machine.
- MEASURED 2026-08-26 — native WinUI accessibility inspection showed both: (1) ready disclosure
  naming `http://localhost:11434/`, on-PC processing, and hosted-model refusal; and (2) an unused
  loopback endpoint showing `Ollama is offline — cleaned text will still be preserved`. Both exact app
  processes were closed cleanly after inspection.
- MEASURED 2026-08-26 — the founder-tested proof was not modified. No connection was made to port
  8081 or any unrelated model server; only Ollama's existing port 11434 was queried.
