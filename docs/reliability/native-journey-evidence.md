# Native journey evidence

## Evidence rule

The portable test suite (`EnviousWispr.Architecture.Tests`) proves the production session's wiring with
fakes at the leaves: it cannot establish what the production WASAPI capture, the runtime worker
supervisors or `WindowsTextTargetAdapter` do on a real desk. The two native cases below are run by
`scripts/native-journeys.ps1` on a logged-on Windows machine through `tools/app-journey-uat`, against the
controlled target windows in `tools/delivery-target-uat` and the reviewed public fixtures, and recorded
here with the build they were taken on. Nothing is downloaded by the portable gate; the model packs are
under `models/` already. A record here says what one run observed on one machine; it is not a claim about
every machine.

Records contain only the harness's content-free verdict fields: no audio, no transcript text, no
clipboard content, no device names, no paths.

## NativeExitClosesMicrophoneAndWorkers

Each journey ends with the app asked to exit; the harness requires the process to have exited cleanly,
`ApplicationCleanShutdown` in its log (the run's clean ending written and the exit concluded - see
`.claude/knowledge/diagnostics.md`), and no runtime worker the app ever started still running. The
microphone is closed by the session's teardown, under the session, before the exit concludes (the
composed proofs read the capture's disposal; the native run reads the clean exit that follows it).

| Recorded | Build | Journey | Passed | Exited cleanly | Stray workers |
| --- | --- | --- | --- | --- | --- |
| 2026-09-21 | 0.19.0+8823407 (step 10 branch) | live-cable-parakeet | yes | yes | 0 |
| 2026-09-21 | 0.19.0+8823407 (step 10 branch) | live-cable-whisper | yes | yes | 0 |
| 2026-09-21 | 0.19.0+8823407 (step 10 branch) | synthetic-quick-tap-preview | yes | yes | 0 |
| 2026-09-21 | 0.19.0+8823407 (step 10 branch) | escape-recovery | yes | yes | 0 |

## NativeDeliveryExercisesThreeRoutes

The synthetic quick tap, delivered by the production adapter into the controlled target in each of its
three modes. The log carries no route, so the harness reads the route from evidence: where the words
landed relative to the field's own seed text (appended after it by the direct UI Automation value write;
before it by a paste at a caret held at the start), or the log's refusal for a protected field with the
words on the clipboard only.

| Recorded | Build | Target mode | Route observed | Passed | Exited cleanly |
| --- | --- | --- | --- | --- | --- |
| 2026-09-21 | 0.19.0+8823407 (step 10 branch) | edit | UiAutomationValue | yes | yes |
| 2026-09-21 | 0.19.0+8823407 (step 10 branch) | caret-start | ClipboardPaste | yes | yes |
| 2026-09-21 | 0.19.0+8823407 (step 10 branch) | password | ClipboardOnly (`TextDeliveryRefused` / `DeliveryProtectedField`, field empty) | yes | yes |

## Reproducing

```powershell
# from the repository root, after building the app (Release, x64), tools/app-journey-uat (Release, x64)
# and tools/delivery-target-uat (Release)
pwsh -NoProfile -File scripts/native-journeys.ps1
```

`-SkipVirtualCable` leaves out the two VB-Audio cable journeys on a machine without the cable.
