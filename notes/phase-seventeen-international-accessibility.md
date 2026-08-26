# Phase 17 international behavior and accessibility evidence

Measured on the founder's Windows rig on 2026-08-26. Product claims are bounded by
`docs/international-accessibility/support-matrix.md`; this note records the dated evidence behind them.

## Implemented

- Settings now store a bounded Whisper language preference: automatic, English, French, German, or
  Spanish. Schema migrations set existing settings and portable profiles to automatic detection. The
  selected code is passed to both final Whisper and preview workers. A development override accepts only
  the same normalized allowlist, so arbitrary worker arguments cannot enter through the environment.
- Settings and Help expose the same honest tier boundary: English baseline, French fixture-backed,
  German experimental with fixed-language evidence, Spanish experimental and below the accuracy bar,
  and no claim for other languages, mixed-language dictation, or translated UI.
- English inverse text normalization and spoken-emoji commands now run only for English or for the
  existing safe non-Whisper fallback when no language is reported. Explicitly detected non-English
  Whisper text is not rewritten by English date, number, punctuation, or emoji rules. Custom words and
  language-safe filler handling remain available.
- User-content recovery, preview, history, dictionary, and snippet surfaces detect text direction from
  their content. Displayed history dates and counts already use the current Windows culture.
- Unicode/RTL tests cover Arabic and Arabic-Indic digits, Hebrew, Devanagari, CJK, decomposed combining
  marks, skin tones, and ZWJ family emoji through deterministic processing and JSON history storage.

## Automated and native evidence

- `powershell -ExecutionPolicy Bypass -File scripts/validate.ps1` passed after the final changes: the
  preserved proof passed 34/34 tests, production passed 301/301 tests, and the Release x64 WinUI/module
  graph plus all native harnesses built with zero warnings and zero errors.
- The current fixed-language CPU Whisper UAT used the pinned Q5 model and public MInDS-14 fixtures. It
  emitted content-free metrics only:

| Language | Audio | Runtime | WER | Result |
|---|---:|---:|---:|---|
| French | 3,754 ms | 5,772 ms | 0% | pass |
| German | 5,851 ms | 5,677 ms | 25% | pass for the experimental fixed-language tier |
| Spanish | 17,066 ms | 5,769 ms | 52.38% | fail; remains below the 35% guardrail |

- The controlled native delivery target received `مرحبا ١٢٣ | שלום | Café | 👨‍👩‍👧‍👦 | 東京` after its
  existing `hello ` prefix. The delivery result was `UiAutomationValue`, `Delivered=true`, target context
  available, and no clipboard fallback. UI Automation then exposed the exact mixed-direction text,
  including the decomposed combining mark and ZWJ emoji. The exact controlled target was closed and no
  delivery or runtime worker remained.
- An isolated Release x64 WinUI launch exposed a named `Whisper language` combo box with bounded help
  text, the measured tier disclosure in Settings, and the full support boundary in Help and privacy.
  Onboarding was completed through the native accessibility surface in the first bounded UI inspection.
- A separate 30-second isolated lifecycle run wrote schema-6 settings with `whisperLanguage=Automatic`,
  reached `HotkeyReady` and `ShellShown`, exited through the built-in UAT seam, recorded `ShellClosed` and
  `ApplicationCleanShutdown`, and left `cleanShutdown=true` with no app or worker process.

## Source evidence retained from earlier phases

- Automatic French detection previously measured 0% WER on CPU and CUDA.
- Automatic German previously detected the wrong language and measured 100-112.5% WER.
- Automatic and fixed Spanish previously measured 52.38% WER. The current fixed CPU rerun reproduced that
  limit, so the UI does not imply Spanish support.
- Native light/dark presentation was previously observed at the host's 150% scale, and UI Automation names
  and live regions were inspected during Phase 14.

## Still unobserved and therefore not claimed

- A physical native-speaker journey for French, German, Spanish, or mixed-language speech.
- Japanese, Chinese, Korean, Arabic, or Indic IME composition through the full app and target-delivery path.
- A complete keyboard-only journey. The accessibility surface and onboarding activation were exercised,
  but not every page and command.
- A complete Narrator journey, native High Contrast run, separate live 100% and 200% scale runs, and a
  physical multi-monitor international-text journey.
- Representative corpora for any language beyond the narrow trusted fixtures. One public fixture is not a
  broad accuracy claim.

No real user text, credential, model weight, external provider, privacy setting, protected port 8081
runtime, or unrelated model server was changed during this phase.
