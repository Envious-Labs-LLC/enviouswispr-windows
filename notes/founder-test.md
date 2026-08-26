# Founder test: EnviousWispr for Windows

> Historical evidence only: this file describes the preserved WPF proof from 2026-08-25. Use
> `docs/founder-local-daily-use.md` for the current production WinUI founder build and acceptance journey.

Updated 2026-08-25. The stable test build is installed on AlienSV and configured to use
the RTX 4090 for both transcription and EG-1 polish.

## Start here

1. Unlock the gaming PC.
2. Double-click **EnviousWispr Windows Test** on the desktop. If it is already running,
   the second launch safely exits instead of starting a duplicate.
3. Wait for the top-right pill to say `ready` and `hold F8 · GPU + EG-1`.
4. Click into a text field in Notepad, Chrome, Discord, or another app.
5. Hold **F8**, speak, then release **F8**.
6. Expect the pill to move through `recording`, `transcribing`, `polishing`, and `done`.
   Your text should appear at the cursor.

If automatic paste is blocked by the target app, the pill says `copied · press Ctrl+V`.
Press **Ctrl+V** and the transcript should appear. This fallback deliberately leaves the
new text on the clipboard.

## Controls

- Double-click the green tray icon for the how-to message.
- Right-click the tray icon for live status, **How to use**, **Start with Windows**, and
  **Quit EnviousWispr**.
- Autostart is on by default, remembers an explicit opt-out, and refreshes itself to the
  current published app path after an update.

## Installed paths

- Desktop shortcut: founder desktop `EnviousWispr Windows Test.lnk`
- App: founder-local `Apps\EnviousWispr-Windows-Test\EnviousWispr.exe`
- Log: beside the founder-local proof executable as `enviouswispr.log`
- Source checkout: repository root on the founder rig
- Configuration: `appsettings.json` beside the installed app

## Current proof

- Release build: 0 warnings and 0 errors.
- Test suite: 39 of 39 passed.
- CUDA fp32 ASR: 10 s clip in 346 ms, 20 s in 183 ms, 91.5 s in 485 ms.
- GPU EG-1: activation probe green in 72 ms.
- Full ASR plus polish smoke: 332 ms on the 10 s clip.
- Interactive app: running in Windows session 1, not the invisible SSH session 0.
- Overlay: rendered and visually inspected at 280 by 64 pixels.
- Founder live UAT: passed with physical F8, the Logitech BRIO microphone, GPU ASR,
  EG-1 polish, and automatic paste into the focused desktop app.

The strict synthetic F8 and paste test was correctly blocked while Windows was locked:
Windows returned access denied for injected keyboard input. The old test incorrectly counted
app-log text as pasted text; the replacement only passes when the focused target actually
contains the expected phrase. The real founder path was subsequently completed and confirmed.

## Known v1 limits

- Transcription begins after F8 is released. Live streaming is not included yet.
- F8 is push-to-talk, not tap-to-toggle.
- The default Windows input device is used at 16 kHz mono.
- Model files are local to this gaming PC and are not included in Git.
- Some elevated apps may block paste from a normal desktop app. The clipboard fallback handles
  this without losing the transcript.

## Normal quit and restart

Use the tray menu's **Quit EnviousWispr** command. It shuts down the app and its own EG-1
server together. Relaunch from the desktop shortcut.
