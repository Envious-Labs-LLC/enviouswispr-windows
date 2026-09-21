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
| 2026-09-21 | 0.19.0+e5a1bcc | edit | UiAutomationValue (`WM_SETTEXT` seen, no `WM_PASTE`, seed first) | yes | yes |
| 2026-09-21 | 0.19.0+e5a1bcc | caret-start | ClipboardPaste (`WM_PASTE` seen, seed last; the clipboard sentinel placed before the delivery read back intact afterwards, `clipboardRestored` true) | yes | yes |
| 2026-09-21 | 0.19.0+e5a1bcc | password | ClipboardOnly (`TextDeliveryRefused` / `DeliveryProtectedField`, field empty) | yes | yes |

The caret-start run also observes clipboard restoration (plan-2 step 13): the harness places a sentinel
line on the clipboard before the paste route runs and requires the same line back after the delivery,
before its guard restores the desk's own clipboard.

## SettledReceiptCountsAQueuedPaste

The receipt the journeys read is acknowledged, not assumed. The target publishes it on every text change
and on every counted message, and after the app has exited the harness posts the target a settle request;
the target answers only once its own thread's queue has held no keystroke, no posted and no sent message
for two consecutive looks, writing the request's sequence on the receipt it publishes then, and the harness
reads that one. The instrument is proved before it is trusted: the target's own settle self-test
(`--mode settle-self-test`, run first by `scripts/native-journeys.ps1`) sends itself a Ctrl+V with the
clipboard emptied - a paste that changes nothing - and posts a settle request right behind it; a posted
message outranks queued input, so a target that answered on the request itself would report no paste.

| Recorded | Build | Case | Observed | Passed |
| --- | --- | --- | --- | --- |
| 2026-09-21 | tools at e5a1bcc | settle-self-test | settled receipt: `pasteMessages` 1, text unchanged (5 characters) | yes |

A target mutated to answer the settle request synchronously fails this case (`pasteMessages` 0, exit 2);
checked on this desk before the record was taken.

## UnverifiedDirectWriteNeverPastes

The production adapter against a controlled field that rewrites every value set into it
(`--target-mode unverified-write`): the field appends a mark on `WM_SETTEXT`, so the adapter's read-back
after its UI Automation value write never matches what it wrote. The adapter must report the insertion
unverified and stop there - a paste after a write of unknown effect could insert the words twice. The
harness requires `WM_SETTEXT` seen, the field rewritten, `TextDeliveryFailed` / `DeliveryUnverified` in
the log (its own code, not a policy refusal's), no `TextDeliveryCompleted`, and no `WM_PASTE` at all.

| Recorded | Build | Target mode | Observed | Passed | Exited cleanly |
| --- | --- | --- | --- | --- | --- |
| 2026-09-21 | 0.19.0+e5a1bcc | unverified-write | UiAutomationValueUnverified (`WM_SETTEXT` seen, field rewritten, `DeliveryUnverified` logged, no `WM_PASTE`) | yes | yes |

The four exit journeys above also passed on 0.19.0+e5a1bcc (live-cable-parakeet, live-cable-whisper,
synthetic-quick-tap-preview, escape-recovery: passed, exited cleanly, no stray workers), all nine cases
through `scripts/native-journeys.ps1` in one run. In the first
eight-run sequence on the step-13 branch (build beb0bf9, before the fault fields landed) one
live-cable-whisper run ended `TextDeliveryFailed` / `DeliveryFaulted` - a fault the previous classification
would have filed as "accessibility unavailable" - and did not recur in eighteen further runs; the log now
carries the stage and the family of such a fault, so the next occurrence names itself.

## GeneralSavePersistsAndRendersCommittedValues

The actual Save button, pressed in the running app through the accessibility layer
(`scripts/native-settings-save.ps1`): on a seeded, onboarded temporary profile the Clipboard page's "Copy to
the clipboard instead of pasting" toggle is flipped through its TogglePattern, "Save settings" is invoked,
and the script reads back the file a launch would read (`preferences.copyInsteadOfPaste`), the operation
bar's title, and - only after that title is on the tree - the toggle as the window re-rendered it after
the save; the app then exits through its UAT switch and must exit cleanly, with `ApplicationCleanShutdown`
among the log lines the tested launch wrote (the seeding launch's own clean shutdown, earlier in the same
file, does not count). Toggle states are polled to a bound, never slept for. No coordinates, no mouse.
The build column names the commit the tested binary was built from, as the app reports its version.

| Recorded | Build | Persisted | Rendered | Message | Passed | Exited cleanly (this launch's log) |
| --- | --- | --- | --- | --- | --- | --- |
| 2026-09-21 | 0.19.0+ef01d7b | copyInsteadOfPaste = true | toggle On, read after the message | "Settings saved" | yes | yes (8 lines, `ApplicationCleanShutdown` among them) |

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
