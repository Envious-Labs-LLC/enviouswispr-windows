# Phase 11 evidence: direct BYOK cloud polish

Date: 2026-08-26

## Shipped boundary

- OpenAI calls `https://api.openai.com/v1/chat/completions` directly.
- Anthropic calls `https://api.anthropic.com/v1/messages` directly.
- Gemini calls `https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent` directly.
- Requests contain the deterministic transcript text and prompt context. They never contain captured audio.
- Envious Labs is not an API proxy and receives neither the request nor the response.
- OpenAI and Gemini requests set `store: false`. Anthropic has no equivalent request field, so its account retention policy applies.
- API keys live only in Windows Credential Manager generic credentials under the `EnviousLabs.EnviousWispr.ApiKey.*` target family. Settings and portable profiles contain no key field.

The WinUI shell shows provider-specific disclosure when a cloud provider is selected:

> [Provider] polish sends your transcribed text directly to [Provider] using your API key. Audio never leaves this PC, and Envious Labs never receives the request.

## Provider behavior

- All providers use the validated macOS `cloud-fixed-v7` prompt byte-for-byte, plus the same language-preservation and short-input guards.
- OpenAI uses `store: false`, omits a client output cap, and uses low reasoning effort for compatible reasoning models.
- Anthropic uses the Messages API, the required `max_tokens` field at 8,192, and explicitly disables extended thinking.
- Gemini uses the model-specific thinking dialect and the same measured default values as macOS.
- Provider-reported truncation, empty or malformed output, unsafe output growth, credential failures, authorization failures, quota/rate failures, content blocking, cancellation, and timeouts all preserve the complete deterministic input.
- Transient network, 408, 409, 429, and 5xx failures receive at most two retries, delayed by one then three seconds, inside one 20-second request budget.
- Diagnostics contain only typed event, failure category, provider identifier, error code, and elapsed time. They never contain keys, prompt bodies, transcripts, outputs, or provider error bodies.

## Validation

The production test project covers:

- exact prompt SHA-256 parity with the macOS v7 source;
- exact provider hosts and authorization header shapes;
- request bodies contain transcript text but no audio field;
- no Envious Labs host is used;
- `store: false` for OpenAI and Gemini;
- Anthropic's required output cap and truncation rejection;
- Gemini thought-part filtering and model-specific low-thinking request;
- OpenAI reasoning-model request shape;
- missing-key fail-down without a network request;
- bounded transient retry and caller cancellation;
- content-free provider diagnostics; and
- a real isolated Windows Credential Manager create/read/replace/delete lifecycle with cleanup.

`tools/cloud-polish-uat` is deliberately opt-in. It can inspect, save, and delete one provider credential without putting the key on the command line. A real API call sends only a fixed synthetic transcript and refuses to start unless `--i-consent-to-send-synthetic-text` is supplied. Building or running canonical validation never calls a cloud provider.

Native WinUI composition was observed with the OpenAI provider selected. The accessibility tree exposed the title `Direct BYOK cloud polish enabled` and the full provider-specific direct-text/no-audio/no-Envious-Labs disclosure. The shell reached idle with its F8 hook ready, closed through its native Close button, and left no app-owned process. No dictation or provider request was triggered during this check.

Canonical `scripts/validate.ps1` passed with zero build warnings or errors, 34/34 preserved-proof tests, and 189/189 production tests.

Real-provider UAT remains unobserved because no explicit authorization to transmit text or incur provider charges was given. API-key entry in the full product settings UI belongs to Phase 14; Phase 11 supplies and validates the credential-store and adapter contracts that screen will call.

## 2026-08-27 exact installed missing-key fallback

The exact installed founder.11 candidate completed isolated OpenAI, Anthropic, and Gemini app journeys without a
real credential. Each run selected Parakeet English/CPU, completed final ASR and deterministic processing, emitted
provider-tagged `PolishStarted`, then returned `PolishDegraded` with the typed
`PolishCredentialMissing` error in 3 ms, 3 ms, and 2 ms respectively. Deterministic text continued through the
controlled native delivery target, the app exited cleanly, and the one owned runtime worker was reduced to zero.

The harness supplies a unique per-run Credential Manager suffix that is known to be empty, so provider code
returns before constructing or sending a request. It requires degraded fallback and rejects a false polish
completion. This proves exact installed provider composition and safe missing-key behavior; it does not replace
the explicitly consented real-provider test, claim model quality, or authorize charges.

## Primary protocol references

- [OpenAI Chat Completions](https://developers.openai.com/api/reference/cli/resources/chat/subresources/completions)
- [Anthropic Messages API](https://platform.claude.com/docs/en/api/messages/create)
- [Anthropic API errors](https://platform.claude.com/docs/en/api/errors)
- [Gemini API reference](https://ai.google.dev/api)
- [Gemini API errors](https://ai.google.dev/gemini-api/docs/generate-content/api-errors)
- [Windows Credential Management API](https://learn.microsoft.com/en-us/windows/win32/api/wincred/)
