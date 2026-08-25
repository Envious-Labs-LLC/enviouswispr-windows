# Interactive Windows UAT harness

This folder validates the visible Windows app without allowing app-log text to stand in for
real pasted text.

- `inventory.ps1` reports the active microphone, output device, SAPI voice, and mic permission.
- `setup-founder-test.ps1` creates the desktop shortcut and launches the UAT in interactive
  Windows session 1 through Task Scheduler.
- `run-interactive-uat.ps1` waits for GPU ASR, a green EG-1 probe, and a real overlay render.
- `e2e-synthetic.ps1` holds F9 around synthetic speech and only passes when the expected phrase
  appears in `SynthTarget`.
- `capture-overlay.ps1` renders the WPF overlay by its owning process for visual inspection.

Windows blocks synthetic keyboard input while the desktop is locked. In that state the runner
records `UAT BLOCKED`, leaves EnviousWispr running, and waits for a human test after unlock.
