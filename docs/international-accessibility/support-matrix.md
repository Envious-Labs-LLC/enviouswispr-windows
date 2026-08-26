# International behavior and accessibility support matrix

This matrix is the product claim boundary for the current Windows development build. A row is supported
only when the evidence column names an observed native path or a trusted, repeatable fixture. A language
being accepted by upstream Whisper does not make it an EnviousWispr-supported language.

## Language tiers

| Surface | Tier | Current claim | Evidence and limits |
|---|---|---|---|
| English UI (`en-US`) | Supported baseline | All product UI and help text are English. | Native WinUI/UI Automation inspection. No translated UI is shipped. Windows falls back to `en-US`. |
| English final dictation | Supported baseline | Parakeet is the automatic final engine; Whisper English is selectable. English dates, numbers, punctuation, fillers, and spoken emoji may use deterministic cleanup. | Pinned English fixtures and native shell-to-worker evidence. |
| French Whisper | Fixture-backed | French can be selected explicitly or detected automatically. English-only date, number, punctuation, and spoken-emoji commands are skipped when French is detected. | One public SHA-pinned MINDS-14 row passed at 0% WER on measured CPU and CUDA paths. A physical French speaker journey and representative corpus remain unobserved. |
| German Whisper | Experimental, one ambiguous row below the per-row bar | Explicit German selection is available for testing because fixed-language decoding improves the measured slice, but it is not supported accuracy. | Five public SHA-pinned MINDS-14 rows: Q5 automatic CUDA passed 3/5 at 13.39% aggregate WER and detected German on 4/5; fixed-language CPU and CUDA passed 4/5 at 7.87%. Short row zero is misdetected automatically. Row 200 measures 42.11% WER against a grammatically broken source annotation even though bounded decoder and model variants agree on the core sentence. German-speaking human audio review and a representative corpus remain required. |
| Spanish Whisper | Fixture-backed | Spanish can be selected explicitly or detected automatically. English-only date, number, punctuation, and spoken-emoji commands are skipped when Spanish is detected. | Five public SHA-pinned MINDS-14 rows passed 5/5 in automatic and fixed-language modes on measured CPU and CUDA paths, with 6.25% aggregate Q5 WER. Row zero uses a reviewed complete evaluation reference because the source annotation is truncated. A physical Spanish speaker journey and representative corpus remain unobserved. |
| Other Whisper languages | Not advertised | No product support claim. | Upstream capability alone is insufficient; there is no trusted EnviousWispr fixture tier or UI selection. |
| Mixed-language dictation | Not advertised | No product support claim. | No representative corpus or native-speaker evidence. |

## Text, locale, and input behavior

| Behavior | Current claim | Evidence and limits |
|---|---|---|
| Unicode storage and processing | Supported | Tests preserve Arabic, Hebrew, Devanagari, CJK, combining marks, variation/skin-tone sequences, and ZWJ emoji through deterministic cleanup and local history without normalization or replacement. |
| Right-to-left presentation | Supported for content direction | User-content controls use `DetectFromContent`; UI chrome remains left-to-right English. Bidirectional text is preserved, but a native Arabic/Hebrew IME journey is still required. |
| Clipboard and safe delivery | Supported for Unicode text | Windows uses Unicode clipboard data and the controlled native delivery harness covers international synthetic text. Protected/elevated/changed-target safety rules still take priority over insertion. |
| Locale-aware display | Supported | User-visible counts and history dates use `CurrentCulture`; protocol fields, hashes, paths, and machine-readable values remain invariant. |
| IME composition | Not yet claimed end-to-end | Text boxes use native WinUI controls, but Japanese, Chinese, Korean, Arabic, and Indic IME composition has not been observed in a complete native journey. |
| Spoken emoji | English only | English commands are deterministic. The step is skipped for explicitly detected non-English Whisper text to avoid rewriting ordinary foreign-language speech. |

## Accessibility

| Area | Current claim | Evidence and limits |
|---|---|---|
| UI Automation names and state | Implemented | Pages, core controls, listening state, recovery, history, and live status expose meaningful names; dynamic status uses polite live regions. |
| Keyboard operation | Native navigation and core controls observed | The production WinUI build completed onboarding with Control+Enter; opened Home, History, Dictionary, Snippets, Help, and Settings with arrow/Enter navigation; opened and dismissed engine, language, and provider selectors; changed and restored a cleanup toggle with Space; and traversed Settings through the final Save button. File-dialog, destructive-history, external-link, and Windows-privacy commands were not activated in this run. |
| Scaling and themes | Partially observed | Light and dark themes were observed at the host's 150% scale. Separate 100% and 200% native runs remain unobserved. |
| Screen reader | Not yet release-proven | UI Automation structure has been inspected, but a complete Narrator journey has not been run. |
| High contrast | Not yet release-proven | Controls use theme resources, but a native Windows High Contrast journey has not been observed. |

## Advertising rule

Release copy may say that English is the supported baseline and French and Spanish have trusted-fixture
support. It must label German experimental with the limits above. It must not claim translated UI,
representative multilingual parity, mixed-language accuracy, IME compatibility, or complete screen-reader
support until the corresponding row gains repeatable evidence.
