# The app's own diagnostic log

**Read this before investigating any crash, hang, silent exit or "it did something and I do not know
why". The app records its own life, and that record is the first evidence, not the last.**

The Windows event log and `%LOCALAPPDATA%\CrashDumps` answer one narrow question: did the process fault?
They say nothing about a process that ended without faulting, which is the harder and more common case.

## Where it is

`%LOCALAPPDATA%\Envious Labs\<data directory>\diagnostics\app.jsonl`, one JSON object per line, oldest
first. The data directory is per release channel and is named in `docs/distribution/windows-release.md`;
a run started with `ENVIOUSWISPR_DATA_DIRECTORY` set writes under that path instead, which is why a UAT
harness leaves its own log beside its own scratch profile.

`run-state.json` sits one level up in the same data directory and carries `startedAt`, `lastHeartbeatAt`,
`cleanShutdown`, `consecutiveInterruptedRuns` and `dictationActive`. **A heartbeat lands once a minute**
(`App.RunHeartbeatAsync`), so on a process that vanished, the last heartbeat bounds the time of death to
the minute even though the log itself stops earlier.

## It is on by default

`UserPreferences` ships `LocalDiagnosticsEnabled: true` with `DiagnosticRetentionDays: 14`, applied
through `JsonLineFileLogger.Configure`. Assume the log exists and go and read it. Settings can turn it
off, so an empty file is a fact worth checking rather than proof that nothing happened.

The file is privacy-safe by construction. **The field-by-field list lives in one place,
`docs/privacy/observability.md`, and a build gate compares it against the record that crosses the
network, so do not restate it here.** In shape: typed enums, bounded durations, a timestamp, and one
boolean. No audio, no transcript, no polished text, no clipboard, no surrounding document. The
`dictationId` correlates lines within one dictation, exists only on the local line, and is never sent.

## The three events that decide what kind of ending you are looking at

`AppEventCode` is the vocabulary. Three of its members answer the question "how did this run end", and
they are the reason the log beats every other source:

| Last seen | What it proves |
| --- | --- |
| `ApplicationCleanShutdown` | The whole teardown succeeded and the run was completed in `run-state.json`. |
| `ShellClosed` but no `ApplicationCleanShutdown` | The app chose to exit and something in teardown failed. |
| Neither | **The app never chose to exit.** Something outside it ended the process. |

`App.PrepareForExitCoreAsync` writes `ShellClosed`, and it sits on the far side of every exit the app can
choose: tray Exit, both `ENVIOUSWISPR_UAT_*` exit variables, a window close with `_exitRequested`, and
update-apply. So the absence of `ShellClosed` eliminates all of them at once rather than leaving them as
suspicions to test one at a time. Measured 2026-08-30: zero `ShellClosed` across 22 launches on the test
machine, which is what moved issue #88 off "why does the app quit" and onto "what is killing it".

## How to read it

Split the file on `ApplicationStarting`. Each slice is one launch, and the last line of a slice is the
last thing that run managed to say. Compare that timestamp against `run-state.json`'s `lastHeartbeatAt`
to see how long the process lived after it went quiet.

```python
import json
runs, cur = [], None
for line in open('app.jsonl'):
    if not line.strip(): continue
    r = json.loads(line)
    if r['event'] == 'ApplicationStarting': cur = []; runs.append(cur)
    if cur is not None: cur.append(r)
for i, run in enumerate(runs, 1):
    print(i, run[0]['timestamp'], '->', run[-1]['timestamp'], 'last =', run[-1]['event'])
```

`failure` is `"None"` on a healthy line, so filtering for anything else gives every degraded moment in the
file in one pass.

## A frozen reading is a death, not a datum

An automated watcher that samples the app must treat an identical value N times running as a liveness
alarm. Three spontaneous exits on 2026-08-30 went unnoticed while a measurement loop kept returning
plausible constant numbers; it was sampling the wallpaper. Assert liveness on a schedule, and never let a
measurement outlive proof that the thing being measured is still there.
