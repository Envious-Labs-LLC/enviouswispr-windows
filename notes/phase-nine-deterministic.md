# Phase 9 deterministic text and emoji evidence

## 2026-08-27 installed exit

- `MEASURED` Exact installed founder.11 (`0.24.0-founder.11+aa6bd735294d4219321d3240eb00d9a6f7efe89a`,
  executable SHA-256 `083D4CE340315E6124DE371D2F11CF3E9E1D86618940019AD3FC3BCC05ED42C2`) passed an
  isolated enabled-profile journey in 4,655 ms. A reviewed custom-word entry replaced the known public
  Parakeet fixture word with a bounded synthetic phrase; filler removal, spoken-emoji formatting, and spoken
  punctuation then had to produce the expected transformed marker while the injected filler remained absent
  from the controlled native edit target.
- `MEASURED` The same exact installed candidate passed the paired disabled-profile journey in 4,561 ms. The
  identical custom-word entry remained present, all four user-facing deterministic switches were disabled,
  and the original recognized fixture word survived into the controlled native edit target.
- `MEASURED` Both runs used the production shell and one production runtime worker, observed final ASR and
  deterministic processing, delivered through the native Windows path, exited cleanly, and left zero owned
  workers. The result records only profile state and pass/fail evidence; the temporary target content and
  isolated settings profile are removed during cleanup.
- `MEASURED` Together with the committed 5,840-row exact macOS ITN oracle, curated end-to-end parity corpus,
  stage ordering, timeout, cancellation, international gating, and fallback tests below, this satisfies the
  Phase 9 exit contract. Physical microphone/global-hotkey acceptance remains a separate whole-product gate.

## 2026-08-26

- `READ` The binding contract is final ASR -> custom words -> filler/false-start cleanup -> spoken
  punctuation/emoji -> English ITN -> optional polish -> emoji/token restoration -> cursor repair ->
  delivery/history. Source: `.claude/knowledge/pipeline.md`.
- `READ` The Windows implementation uses the public macOS snapshot pinned at
  `f9b70283326254aad6974cceb33ca41316e493ec`. The committed fixture provenance and hashes are in
  `src/Production/EnviousWispr.Architecture.Tests/Fixtures/README.md`.
- `MEASURED` `dotnet test src/Production/EnviousWispr.Architecture.Tests/EnviousWispr.Architecture.Tests.csproj -c Debug --nologo`
  passed 149/149. The suite includes ordering, cancellation, per-stage timeouts, last-valid fallback,
  settings/profile migration, international language gates, 19 pinned emoji-restoration placements, and
  every row in both ITN oracles.
- `MEASURED` Windows ITN matched 2,084 curated and 3,756 independent holdout macOS rows byte-for-byte:
  5,840/5,840 exact. Categories include currency, dates, email, negative and general numbers, phone
  numbers, spoken punctuation, times, and URLs. Real-dictation slices and founder-like email examples
  were removed before commit to enforce the repository's no-user-content rule.
- `MEASURED` `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/validate.ps1` passed. The
  preserved proof, production WinUI x64 app, runtime worker, and five UAT harnesses built in Release with
  zero warnings/errors; portable proof tests passed 34/34 and production tests passed 149/149.
- `MEASURED` A Release x64 WinUI run used Computer Use-generated F8 input and the real WASAPI/final-ASR
  path. The content-free sequence was `DictationTranscriptionStarted` ->
  `DictationTranscriptionDegraded` (`RuntimeProvider`, 67 ms) -> `DeterministicProcessingStarted` ->
  `DeterministicProcessingCompleted` (3 ms). The shell showed `No speech detected`; closing it produced
  `ShellClosed` and removed the production app and its exact worker.
- `MEASURED` The latest native-run diagnostic objects contained only `timestamp`, `event`, `failure`, and
  `elapsedMilliseconds`. No transcript, audio, model path, hardware identifier, or user content was
  present.
- `READ` Every deterministic step is typed, locally bounded, cancellation-aware, and returns the last
  valid text after a step timeout or non-fatal failure. Optional polish is not required for usable output.
  Source: `src/Production/EnviousWispr.Pipeline/DeterministicTextPipeline.cs`.

## Intentional Windows differences

- `READ` Windows removes exact adjacent stutters and obvious prefix fragments conservatively. The macOS
  deterministic filler step does not perform semantic false-start rewriting; broader self-correction is
  part of optional polish. Windows leaves semantic correction markers for Phase 10 instead of risking
  deterministic content loss.
- `READ` The Phase 2 Windows `CustomWordEntry` contract stores one spoken/replacement pair. The macOS
  product also stores IDs, aliases, categories, source, frequency, priority, and thresholds. Runtime alias
  behavior can be represented by multiple Windows entries; rich portable import and management UI remain
  Phase 14.
- `READ` Windows does not copy macOS `NSSpellChecker` or cursor-specific casing machinery. The portable
  ITN/casing output is exact across the 5,840-row privacy-safe oracle; foreground cursor-aware repair
  remains Phase 13.
- `READ` Windows declines a fuzzy spoken-emoji match when an existing glyph interrupts the spoken surface.
  This is a deliberate safety hardening against deleting mixed literal content.

## Superseded 2026-08-26 observations

- `MEASURED` The native F8 run contained ambient/no-speech audio, so it proved native stage ordering and
  fallback behavior but did not visibly display transformed transcript content. Exact content behavior is
  covered by committed fixtures; visible delivery is Phase 13.
- `ASSUMED` Optional polish will feed `PolishedText` into the existing emoji-restoration seam in Phase 10;
  no polish provider is wired in Phase 9.
