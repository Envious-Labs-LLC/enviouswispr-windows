# Phase 4 hotkey and session evidence

Measured on the founder's Windows rig on 2026-08-25. This phase remains in progress until the physical-key
and real focus-change exit in `docs/plans/windows-master-plan.md` is satisfied.

## Implemented so far

- Core parses configurable push-to-talk gestures into canonical modifier/key values and returns typed,
  content-free errors for invalid gestures.
- Services probes `RegisterHotKey` for conflicts, then installs a narrow `WH_KEYBOARD_LL` hook because
  push-to-talk requires both press and release edges. The callback recognizes only the configured gesture
  plus Escape while held, queues signals away from the hook thread, debounces repeat keydown, and passes
  unrelated typing to the next hook.
- The hook consumes the configured trigger only while its full modifier set matches. Once a valid press is
  active, release remains recognized even if a modifier is released first. Escape cancels once and the
  later trigger release cannot finalize the cancelled session.
- Pipeline freezes an opaque foreground window handle before capture starts, rejects overlapping and stale
  transitions, applies a 100 ms minimum-hold debounce, preserves recoverable interrupted audio, and models
  recording, finalizing, delivery, completion, cancellation, failure, and reset explicitly.
- The production WinUI shell installs the configured hook, drives real WASAPI capture, shows visible state,
  writes content-free lifecycle categories, and unhooks before disposing capture on shutdown. Final ASR and
  delivery are still intentionally absent from this production path.
- `tools/hotkey-uat` is a content-free native harness built by the canonical validator. Its generated input
  is explicitly labeled synthetic and is not counted as physical-key evidence.

## Automated evidence

- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/validate.ps1` passed with zero build
  warnings/errors, 34 portable proof tests, and 59 production tests.
- Gesture tests cover canonical function keys, letter/number/space forms, modifiers in different orders,
  duplicates, reserved Escape, malformed separators, and unsupported keys.
- Settings validation rejects gestures that the production hook cannot install.
- Edge tests cover press/release, repeat debounce, exact modifier matching, unrelated-key passthrough,
  Escape cancellation, suppressed release after cancellation, and modifier-first release.
- Session tests cover frozen target identity, overlap rejection, cancellation with no stop/delivery, missing
  target, buffered interruption fallback, empty interruption failure, stale completion, reset, and disposal
  of an active session.

## Native Windows acceptance observed

- The synthetic native harness reported hook install, conflict detection, press/release, Escape
  cancellation, valid foreground-target capture, and teardown success.
- The Release x64 production WinUI app launched with `Hold F8` and `Idle` visible. A Computer Use-generated
  F8 edge changed the app to recording and then `Capture complete`; the content-free diagnostic sequence
  was `HotkeyReady`, `DictationRecordingStarted`, `DictationCaptureFinalized`, and `ShellClosed`, all with
  failure category `None`.
- The first zero-duration synthetic tap found a recorder-start race. The 100 ms minimum-hold debounce fixed
  it; the repeated native app path finalized in about 129 ms without an audio failure.
- While the production hook remained installed, 31 ordinary characters entered an isolated unsaved
  Notepad tab unchanged. No ordinary key values or text entered diagnostics.
- Closing the production window removed its only visible window and ran the content-free `ShellClosed`
  path. The independent native harness also reported `teardownReleasedHook: true`.

## Required evidence still missing

- A person has not yet physically held and released the configured key against this production build.
- Physical Escape-during-hold, focus change while held, repeated long sessions, and confirmation that the
  configured key never remains blocked after a forced shutdown are unobserved.
- Conflict probing detects combinations reserved through `RegisterHotKey`; Windows does not expose a
  reliable way to enumerate competing low-level keyboard hooks, so that narrower conflict class remains a
  documented platform limitation.
