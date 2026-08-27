# Phase 10 local EG-1 polish evidence

## 2026-08-26

- `READ` EG-1 is a merged, quantized QLoRA derivative of Qwen3-4B-Instruct-2507. The shipped macOS
  artifact is v2 Q5_K_M, uses prompt template `eg1-v1`, and is served through pinned `llama.cpp` with a
  16,384-token context, flash attention, and Q8 key/value caches. Sources:
  `macos-knowledge/knowledge/eg1-model-provenance.md` and
  `macos-knowledge/knowledge/eg1-operations.md`.
- `READ` The 265-byte system prompt and uppercase `<TRANSCRIPT>` wrapper are the model's training
  contract. Windows pins both byte-for-byte and neutralizes dictated wrapper tags with a zero-width
  non-joiner. Source: `macos-source/Sources/EnviousWisprLLM/Prompting/EGOnePromptBuilder.swift`.
- `READ` Windows uses a typed Core `IPolishProvider` contract and isolates `llama.cpp` inside the LLM
  module. The application binds final ASR -> deterministic text -> optional EG-1 -> emoji restoration.
  Every non-cancellation failure returns the deterministic input unchanged.
- `READ` Every launched server is assigned to a Windows Job Object with kill-on-job-close, so an abrupt
  parent exit cannot orphan the model process. Normal window close also performs synchronous termination
  of the exact owned process tree before asynchronous application disposal begins.
- `MEASURED` Production tests passed 168/168. Coverage includes the frozen prompt, wrapper injection
  defense, Windows server arguments, per-launch authentication, health semantics, output cleanup,
  truncation rejection, context preflight, timeout, caller cancellation, one retry, deterministic
  fallback, privacy surface, exact runtime disposal, and deterministic -> polish -> emoji ordering.
- `MEASURED` `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/validate.ps1` passed. The
  preserved proof and every production/UAT project built in Release with zero warnings/errors; preserved
  proof tests passed 34/34 and production tests passed 168/168.
- `MEASURED` The privacy-safe local UAT corpus passed 12/12 on CPU. Semantic health was green; model
  startup plus the fixed health transformation took 2,941 ms, median warm inference was 633 ms, maximum
  warm inference was 774 ms, and the complete 12-case run took 10,311 ms.
- `MEASURED` The same 12/12 corpus passed on CUDA. Semantic health was green; model startup plus health
  took 2,431 ms, median warm inference was 70 ms, maximum warm inference was 87 ms, and the complete run
  took 3,289 ms.
- `MEASURED` CPU and CUDA acceptance covered filler removal, self-correction, grammar, punctuation,
  entity/number preservation, prompt-injection passthrough, French, and German. The harness emits only
  aggregate counts, categories, statuses, and timings; it does not print prompts, inputs, outputs, model
  paths, or bearer tokens.
- `MEASURED` A native Release x64 WinUI launch selected EG-1 through environment-backed local
  configuration, spawned a distinct app-owned CUDA server on a dynamic loopback port, and completed the
  fixed semantic readiness probe in 1,703 ms. The process tree showed the app-owned server as a child of
  the production app while the unrelated protected server remained separate.
- `MEASURED` The first native close exposed an asynchronous shutdown race that left the exact app-owned
  server alive. Windows now invokes synchronous exact-process-tree termination before the window can
  exit. A second native launch reached green readiness in 1,663 ms; closing it removed the production
  app, runtime worker, and its exact EG-1 child immediately while the unrelated server remained running.
- `MEASURED` Native diagnostics contain only timestamp, event, failure category, and elapsed
  milliseconds. The provider and server manager have no logging surface for prompt, transcript, model
  file, bearer token, request body, or response body.
- `MEASURED` Exact installed founder.11 then completed the full Parakeet English/CPU -> deterministic ->
  EG-1 -> protected-token restoration -> native delivery path in 9,270 ms. Provider-tagged EG-1 polish took
  1,714 ms with no degraded fallback. The app started one owned llama server; normal app exit removed it and
  the ASR worker, while unrelated llama/Ollama processes were neither owned nor stopped. This run used a
  reviewed public audio fixture and named transitions, not a physical microphone.
- `MEASURED` The installed journey exposed that EG-1's provider ID `eg-one` was absent from diagnostics
  because the app mapper recognized only the display/model spelling `eg-1`. The shared typed mapper now
  accepts the provider's canonical ID (and the legacy alias), all local-polish readiness and attempt records
  identify the provider, and tests cover every known provider plus unknown-value refusal.

## Scope and limits

- `MEASURED` Available local acceptance used the founder-tested single-file EG-1 v5 Q5_K_M artifact,
  which belongs to the same Qwen3-4B EG-1 family and uses the same pinned prompt. It is not the current
  eight-shard macOS v2 distribution artifact. Windows model delivery and signed artifact admission remain
  later phases; no model or private path is committed here.
- `MEASURED` The 12-case corpus is a privacy-safe functional gate, not a held-out quality estimate. The
  macOS historical quality corpora overlap training data and cannot support a clean shipping claim.
- `MEASURED` Computer Use exercised native F8 capture, final-ASR fallback, and deterministic processing,
  but its 117 ms input contained no speech. A non-empty physical-microphone WinUI run through the final
  EG-1 handoff remains unobserved; the provider, model, stage order, and cleanup are independently proven
  by the native readiness run, CPU/CUDA acceptance, and committed tests.
