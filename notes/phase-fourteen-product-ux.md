# Phase 14: complete product UX

## Contracts

- READ 2026-08-26 — `docs/plans/windows-master-plan.md` Phase 14 requires the production product
  shell: onboarding, microphone readiness, overlay, tray, settings, engine/model visibility, local
  history, dictionary, snippets, portable profiles, update/help surfaces, accessibility, theme,
  localization, and founder-journey validation.
- READ 2026-08-26 — `.claude/knowledge/product-contract.md`, `architecture.md`, `pipeline.md`,
  `distribution.md`, `workflow.md`, and `validation.md` require private local defaults, explicit
  provider consent, Windows-native behavior, content-free diagnostics, atomic storage, current-phase
  truth, and native runtime evidence rather than compile-only claims.
- READ 2026-08-26 — the routed macOS onboarding, settings, history, overlay, permissions, tray, and
  update maps define the reusable product concepts. Windows translates those concepts to WinUI 3,
  WASAPI, Windows Credential Manager, per-monitor work areas, and Windows microphone privacy without
  importing Apple-specific permission or lifecycle machinery.

## Implementation

- MEASURED 2026-08-26 — the WinUI shell now provides first-run privacy guidance and live readiness,
  Home, History, Dictionary, Snippets, Settings, Help and privacy, plus a compact always-on-top
  non-activating dictation overlay and notification-area lifecycle. Closing the main window hides the
  process to the tray; explicit tray Exit remains the shutdown path.
- MEASURED 2026-08-26 — the overlay reports recording, processing, success, warning, and failure,
  carries a polite accessibility name/live state, never contains transcript diagnostics, and uses
  signed work-area placement math for primary, negative-coordinate left, right, above, oversized, and
  invalid monitor arrangements.
- MEASURED 2026-08-26 — settings schema v5 adds a machine-local preferred microphone, routes it into
  capture, validates it, migrates older schemas, and deliberately excludes it from portable profile
  exports. Microphone discovery reports a real readiness snapshot and both onboarding and Settings can
  open the exact Windows microphone privacy destination.
- MEASURED 2026-08-26 — local history uses bounded atomic JSON storage, configurable retention,
  corruption preservation, search, copy, per-entry deletion, and explicit clear-all confirmation.
  Final processed text is saved only when history is enabled; diagnostics still exclude transcript
  content.
- MEASURED 2026-08-26 — dictionary and snippet editing persist through the existing settings store;
  portable import/export includes reusable settings and user data while excluding keys, microphone,
  history, audio, lifecycle state, and private machine paths.
- MEASURED 2026-08-26 — direct-cloud API-key management is write-only in the UI. OpenAI, Anthropic,
  and Gemini keys are stored under provider-specific Windows Credential Manager targets; settings and
  exports contain no secret. Presence checks free the native credential without materializing its
  value, and the UI reports only found, missing, or unavailable.
- MEASURED 2026-08-26 — light, dark, and high-contrast resources, semantic UI Automation names,
  polite live status, access keys, a Ctrl+Enter onboarding accelerator, deterministic first focus, and
  an en-US resource seam are present. Phase 17 still owns the wider language and assistive-technology
  matrix.
- MEASURED 2026-08-26 — model downloads and signed app updates remain truthful read-only surfaces.
  They do not simulate completion before Phase 16 and Phase 19 implement those delivery systems.

## Validation

- MEASURED 2026-08-26 — `scripts/validate.ps1` passed with zero warnings/errors: preserved founder
  proof 34/34, production architecture 258/258, WinUI x64 Release, and every existing native UAT
  harness. `git diff --check` also passed.
- MEASURED 2026-08-26 — isolated native WinUI UAT reported an active microphone, local transcription
  readiness, and the configured F8 shortcut. A clean first launch exposed meaningful UI Automation
  names and focused Get started; completing onboarding focused Home and persisted the choice.
- MEASURED 2026-08-26 — isolated native journeys created and reloaded synthetic dictionary, snippet,
  and history rows; history search returned the synthetic match. Theme changes rendered immediately,
  and dark plus light surfaces were readable at the host's actual 150% scale.
- MEASURED 2026-08-26 — native cloud-key UAT selected OpenAI, changed the key controls from disabled
  to enabled, reported missing, masked a synthetic non-secret value, stored it under a bounded isolated
  Credential Manager target, cleared the field, and reported presence without revealing the value.
  The exact synthetic credential was deleted and its absence verified afterward.
- MEASURED 2026-08-26 — the microphone privacy action opened Windows Settings. No privacy control was
  read or changed. Closing the product window hid the main surface while the exact process remained
  responsive in the tray lifecycle.
- MEASURED 2026-08-26 — the recording-state UAT seam exposed a second overlay window with the exact
  accessible listening/cancel guidance while Get started retained focus, proving the overlay did not
  steal activation.
- UNOBSERVED 2026-08-26 — this host exposes one active monitor, so a physical multi-monitor move and
  tray-menu restore were not observed. Monitor placement has deterministic signed-coordinate coverage,
  but those tests do not replace native multi-monitor proof.
- UNOBSERVED 2026-08-26 — the host was exercised at its actual 150% scale, not separate live 100% and
  200% configurations. Narrator was not run. UI Automation names/live regions were inspected, but a
  real screen-reader journey and a complete keyboard-only journey remain required before the Phase 14
  exit can be called final.
- UNOBSERVED 2026-08-26 — native file-picker profile import/export and founder acceptance were not
  completed in this pass. Atomic profile contracts and exclusions are covered by tests; picker and
  founder evidence still need native observation.
- MEASURED 2026-08-26 — the founder-tested WPF proof was not modified. No real credential, user text,
  model weight, external provider, protected port 8081 runtime, or unrelated model server was touched.
