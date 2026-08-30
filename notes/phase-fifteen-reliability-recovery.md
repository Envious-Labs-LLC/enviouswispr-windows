# Phase 15 reliability and recovery evidence

Measured on the founder's Windows rig on 2026-08-26. Phase 15 is implemented and handed off as a draft,
but the master-plan exit remains open because a multi-day soak, physical sleep/resume, and a physical
recording-device removal were not performed in this work session.

## Implemented

- App startup writes a content-free atomic run marker, heartbeats it, detects interrupted or invalid prior
  runs, and marks a run clean only after critical cleanup finishes.
- Duplicate launches send one fixed byte over a current-user-only named pipe. The primary window restores
  from the tray without sending arguments, user text, or file paths between processes.
- Deterministic processed text is retained in memory and, when disk admission allows it, protected with
  Windows DPAPI for the current user before delivery. Startup never pastes recovered text automatically.
- The Home page supports explicit copy and confirmed deletion of recovered text. First-run onboarding also
  surfaces interrupted-run and recovered-text notices before the Home page is visible.
- Low-memory admission refuses capture before allocation. Low disk keeps dictation usable while disabling
  durable recovery for that session. A failed health probe does not disable the application.
- Five-minute recording and three-minute final-processing watchdogs cancel stuck work, reset the session,
  and preserve the last valid text. Bounded UAT-only time controls make the same native paths testable.
- Suspend and session-lock events cancel active processing or finalize buffered capture; resume and unlock
  return the shell to ready state. System event subscriptions are removed during shutdown.
- Capture-device notifications refresh the microphone list. A missing configured microphone retries the
  same session against the Windows default device.
- Cloud network loss, local runtime loss, worker crashes, and provider startup failures fall back to the last
  deterministic result or a fresh CPU-only worker.
- Shutdown now cancels active work, removes hooks and lifecycle subscriptions, stops owned workers, closes
  local activation, stops the heartbeat, and continues cleanup even if one resource fails.
- The WinUI packaging target now copies the complete runtime worker from its actual Release output. The
  previous target omitted `EnviousWispr.RuntimeWorker.dll` from the app output.

## Automated evidence

- `powershell -ExecutionPolicy Bypass -File scripts/validate.ps1` passed: 34 preserved proof tests and 278
  production architecture tests, with zero build warnings or errors.
- `dotnet run --project tools/reliability-uat/EnviousWispr.Reliability.Uat.csproj -c Release --no-build --
  --iterations 5000` passed 5,000 encrypted-recovery, interrupted-run, activation-channel, and resource-fault
  cycles in 59.506 seconds. After one warm-up cycle, the process handle count stayed bounded at +11;
  no child process or input hook was created by the harness.
- `dotnet run --project tools/runtime-uat/EnviousWispr.Runtime.Uat.csproj -c Release` passed worker start,
  exact-PID crash recovery, clean stop, startup timeout rejection, and resource arbitration.
- The production suite explicitly covers cloud network loss returning unchanged deterministic text after
  bounded retries, preferred-device loss falling back to the default device, forced session abort/reset,
  DPAPI round-trip and deletion, invalid source preservation, current-user activation, lifecycle mapping,
  and resource admission.
- The model-dependent gate passed all 39 preserved proof/runtime tests and all Parakeet CPU, CUDA, crash,
  cancellation, and fallback checks when the previously documented process-local CUDA 13 paths were used.
  The overall gate still stops at the known Phase 7 Whisper German and Spanish accuracy failures; those
  failures predate Phase 15 and remain documented in `notes/phase-seven-whisper.md`.

## Native Windows paths observed

- A real WinUI launch reported microphone, F8 hook, and local transcription ready. Its isolated worker was
  a child process and was absent after clean exit.
- Closing to the tray kept the exact primary process alive. A second launch exited and restored that same
  hidden primary window; only one app process remained.
- Force-terminating only the exact isolated UAT app PID left `cleanShutdown: false`. Relaunch detected the
  interrupted run and displayed a warning during first-run onboarding. A second forced interruption was
  reported as two consecutive interrupted starts.
- A fixed synthetic recovery sentence was DPAPI-protected on disk, announced during onboarding, displayed
  on Home without automatic paste, and removed only after the native confirmation dialog. The recovery file
  was then absent.
- Holding generated F8 input against a 1.2-second UAT watchdog started real WASAPI capture, logged one
  `SessionTimedOut` recovery, reset safely, then completed a clean shutdown with no owned worker remaining.
- A timed native tray-equivalent exit wrote `ApplicationCleanShutdown` before closing the final window. The
  exact app and worker PIDs were both gone afterward and no post-shutdown unhandled event was recorded.

All UAT profiles and recovery text were fixed synthetic data in isolated temporary directories. No Windows
privacy, security, microphone, or system PATH setting was changed. The isolated directories contain no
user content; recursive cleanup outside the workspace was blocked by the command safety policy, so they
remain eligible for normal OS temporary-file cleanup.

## Exit gaps

- The accelerated 5,000-cycle run is useful leak pressure but is not a multi-day soak. The Phase 15 exit
  must remain open until a real multi-day app run shows no text loss, owned-process growth, or hook leak.
- Actual machine suspend/resume and session lock/unlock were not triggered because those actions would
  disrupt the founder's active workstation. Event mapping and recovery behavior are automated; the physical
  lifecycle path remains unobserved.
- No physical microphone was unplugged or disabled. Notification tracking and preferred-device fallback are
  automated, while the real device-removal path remains unobserved.
- Real low-memory and low-disk pressure were not induced. The admission rules and deterministic injected
  snapshots passed, but Windows behavior under actual resource exhaustion remains unobserved.
