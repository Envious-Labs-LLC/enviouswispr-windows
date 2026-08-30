# Phase 17 international behavior and accessibility evidence

Measured on the founder's Windows rig on 2026-08-26. Product claims are bounded by
`docs/international-accessibility/support-matrix.md`; this note records the dated evidence behind them.

## Implemented

- Settings now store a bounded Whisper language preference: automatic, English, French, German, or
  Spanish. Schema migrations set existing settings and portable profiles to automatic detection. The
  selected code is passed to both final Whisper and preview workers. A development override accepts only
  the same normalized allowlist, so arbitrary worker arguments cannot enter through the environment.
- Settings and Help expose the bounded language choices. The current claim boundary is maintained in
  `docs/international-accessibility/support-matrix.md`; later reference-quality review promoted the bounded
  Spanish slice to fixture-backed while German remains experimental.
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
- An earlier three-row fixed-language CPU Whisper UAT used the pinned Q5 model and public MINDS-14 fixtures.
  It emitted content-free metrics only; the expanded evidence below supersedes its single-row language view:

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
- A keyboard-only native run against the Release x64 production shell at `7c6a843` completed onboarding with
  Control+Enter; opened Home, History, Dictionary, Snippets, Help and privacy, and Settings through arrow and
  Enter navigation; opened and dismissed the engine, language, and provider selectors with Alt+Down and
  Escape; changed and restored dictionary correction with Space; and tabbed through Settings to the final
  Save settings button. The accessibility tree exposed meaningful page, control, status, and description
  names throughout. No setting value remained changed. Alt+F4 hid the window to the tray as designed; because
  the automation surface could not target the Windows notification area, the exact owned app and its direct
  worker were then stopped and this run is not counted as graceful tray-exit evidence.

## Source evidence retained from earlier phases

- A later fail-closed reference audit superseded the raw-source accuracy numbers below without erasing
  them as dated evidence. The current Q5 CUDA run passes Spanish 5/5 at 6.25% aggregate WER in automatic
  and fixed-language modes. German passes 3/5 automatically at 13.39% aggregate WER with German detected
  on 4/5, and 4/5 at 7.87% in fixed-language CPU and CUDA modes.
- The upstream Dataset Viewer confirms that German row 200 at the pinned revision is
  `de-DE~CARD_ISSUES/response_7.wav` and carries the same grammatically broken source annotation preserved
  in the manifest. Default greedy, greedy best-of-five, beam-search-five, Q5, and full-precision bounded
  comparisons all produced the same core sentence while the row remained over the 35% per-row bar. No
  decoder tuning or corpus-specific correction was retained. A German-speaking human audio review is
  required before changing that evaluation reference.
- The older measurements in the next two bullets used source annotations before the reviewed evaluation
  references were admitted; they are retained as historical evidence rather than current product claims.
- Automatic French detection measured 0% WER on the one admitted row on CPU and CUDA.
- The expanded five-row German slice passed 2/5 automatically at 39.42% aggregate WER on CPU and 40.38%
  on CUDA; fixed-language CUDA passed 3/5 at 33.65%. Individual rows still fail the 35% guardrail.
- The expanded five-row Spanish slice passed 4/5 automatically and with fixed-language CUDA at 20% aggregate
  WER. Row zero remained at 52.38% before its truncated annotation received a separate reviewed complete
  evaluation reference.
- Native light/dark presentation was previously observed at the host's 150% scale, and UI Automation names
  and live regions were inspected during Phase 14.

## Still unobserved and therefore not claimed

- A physical native-speaker journey for French, German, Spanish, or mixed-language speech.
- Japanese, Chinese, Korean, Arabic, or Indic IME composition through the full app and target-delivery path.
- Keyboard activation of file-dialog commands, destructive history actions, the external source link, and
  the Windows microphone-privacy command. The complete navigation path and representative form controls are
  now observed, but these intentionally side-effecting commands were not invoked in the keyboard run.
- A complete Narrator journey, native High Contrast run, separate live 100% and 200% scale runs, and a
  physical multi-monitor international-text journey.
- Representative language corpora. The current one-row French and five-row German/Spanish slices are not
  broad accuracy claims.

No real user text, credential, model weight, external provider, privacy setting, protected port 8081
runtime, or unrelated model server was changed during this phase.
