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
| 2026-09-21 | 0.19.0+d313251 (step 10 branch) | live-cable-parakeet | yes | yes | 0 |
| 2026-09-21 | 0.19.0+d313251 (step 10 branch) | live-cable-whisper | yes | yes | 0 |
| 2026-09-21 | 0.19.0+d313251 (step 10 branch) | synthetic-quick-tap-preview | yes | yes | 0 |
| 2026-09-21 | 0.19.0+d313251 (step 10 branch) | escape-recovery | yes | yes | 0 |

## NativeDeliveryExercisesThreeRoutes

The synthetic quick tap, delivered by the production adapter into the controlled target in each of its
three modes. The log carries no route, so the harness reads the route from evidence: the window messages
the target's field received (a paste reaches a Win32 edit as `WM_PASTE`; a UI Automation value write as
`WM_SETTEXT`) together with where the words landed relative to the field's own seed text (appended after
it by the direct write; before it by a paste at a caret held at the start), or the log's refusal for a
protected field with the words on the clipboard only. The caret-start run is the positive control for
the paste count and the edit run its negative: a build that pasted everywhere would fail the edit run on
the message count.

| Recorded | Build | Target mode | Route observed | Passed | Exited cleanly |
| --- | --- | --- | --- | --- | --- |
| 2026-09-21 | 0.19.0+d313251 (step 10 branch) | edit | UiAutomationValue (`WM_SETTEXT` seen, no `WM_PASTE`, seed first) | yes | yes |
| 2026-09-21 | 0.19.0+d313251 (step 10 branch) | caret-start | ClipboardPaste (`WM_PASTE` seen, seed last) | yes | yes |
| 2026-09-21 | 0.19.0+d313251 (step 10 branch) | password | ClipboardOnly (`TextDeliveryRefused` / `DeliveryProtectedField`, field empty) | yes | yes |

## GeneralSavePersistsAndRendersCommittedValues

The actual Save button, pressed in the running app through the accessibility layer
(`scripts/native-settings-save.ps1`): on a seeded, onboarded temporary profile the Clipboard page's "Copy to
the clipboard instead of pasting" toggle is flipped through its TogglePattern, "Save settings" is invoked,
and the script reads back the file a launch would read (`preferences.copyInsteadOfPaste`), the toggle as
the window re-rendered it after the save, and the operation bar's title; the app then exits through its
UAT switch and must exit cleanly with `ApplicationCleanShutdown` in its log. No coordinates, no mouse.

| Recorded | Build | Persisted | Rendered | Message | Passed | Exited cleanly |
| --- | --- | --- | --- | --- | --- | --- |
| 2026-09-21 | 0.19.0+d797b9b (step 12 branch) | copyInsteadOfPaste = true | toggle On | "Settings saved" | yes | yes |

## Reproducing

```powershell
# from the repository root, after building the app (Release, x64), tools/app-journey-uat (Release, x64)
# and tools/delivery-target-uat (Release)
pwsh -NoProfile -File scripts/native-journeys.ps1
```

`-SkipVirtualCable` leaves out the two VB-Audio cable journeys on a machine without the cable.

```powershell
pwsh -NoProfile -File scripts/native-settings-save.ps1
```
