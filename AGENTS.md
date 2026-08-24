# EnviousWispr for Windows

**Objective: work out how to build a Windows version of EnviousWispr, then build it. Record everything
learned in `notes/`.**

EnviousWispr's promise is very fast, commercial-grade voice-to-text on macOS: hold a key, speak, the text
appears in whatever app has focus, cleaned up. Record, transcribe, polish, paste. It is launched and
shipping to real users. The Windows version does not exist. Your job is to make it possible, then real.

Treat any COMPARATIVE speed claim as unverified unless you can cite the benchmark behind it; the product
promise is not a measurement you may repeat as one.

## This repo is PUBLIC

Everything you push is visible to anyone the moment it lands, including `notes/`. That is deliberate —
building in the open is the strategy, not an accident — and it changes two things about how you write:

- **Notes are still terse working notes, not marketing.** Do not soften a dead end or dress up a result
  because someone might read it. An honest "tried X, it does not work, here is why" is worth more in
  public than in private.
- **Nothing about the private side goes in.** No credentials, no internal infrastructure detail beyond
  what the macOS repo already publishes, no unreleased business plan. The macOS source is public, so
  measurements taken from it are fine to publish.

## Nothing here is a permission gate

Install toolchains, build, run, throw away and start again. Use `sudo`. Use the GPU. Push branches, open
pull requests. This file is craft and objective, not a fence. Machine-level guidance is in
`/home/saura/CLAUDE.md`, method in `/home/saura/agent-workspace/AGENTS.md`; neither gates you either.

## Reference material — on the rig, not in this repo

At `/home/saura/agent-workspace/enviouswispr-windows/`:

| Path | What it is |
|---|---|
| `macos-source/` | The shipping macOS app, verbatim, 1,648 files at commit `f9b70283`, 2026-08-24 |
| `macos-knowledge/` | Its engineering knowledge: architecture, pipeline, gotchas, conventions |

Deliberately not committed here: it belongs to the macOS repo, it is 175 MB, and one artifact in two
places drifts. Leave both directories byte-identical — not for lack of permission, but because they are
the baseline you compare against and a modified baseline silently stops being evidence. Copy out anything
you want to change.

**Start with `macos-knowledge/`, not the source.** `knowledge/INDEX.md` routes by topic;
`knowledge/capability-map.md` answers "where does X live"; `knowledge/architecture.md` and
`knowledge/pipeline-mechanics.md` are the shape of the thing. The source is 308,000 lines and reading it
cold is the slow path.

Both are DATED. They were accurate about the Mac on 2026-08-24 and describe decisions, not necessarily
current code. Verify any claim a decision rests on against the snapshot source, and never present them as
the Mac's current state.

## The shape of the problem, measured

308,321 lines of Swift across 17 modules:

| Module | Lines | Note |
|---|---|---|
| `EnviousWisprAppKit` | 46,803 | UI and OS integration. Biggest module, least portable |
| `EnviousWisprPipeline` | 23,730 | Orchestration: record to text to delivery |
| `EnviousWisprServices` | 12,650 | |
| `EnviousWisprLLM` | 12,650 | Text cleanup via local and cloud models |
| `EnviousWisprPostProcessing` | 12,379 | Deterministic text repair. Mostly pure logic |
| `EnviousWisprAudio` | 7,527 | Capture |
| `EnviousWisprASR` | 7,477 | Speech recognition |
| `EnviousWisprCore` | 7,340 | Shared types. Mostly pure logic |

Apple-specific surface, by files touching each:

| API | Files | Why it matters on Windows |
|---|---|---|
| AppKit | 81 | macOS UI toolkit. No Windows equivalent; replace wholesale |
| SwiftUI | 78 | Does not run on Windows |
| CoreML | 27 | Runs the speech models on Apple hardware |
| AXUIElement | 11 | Reads text around the cursor in other apps. Windows counterpart is UI Automation |
| FoundationModels | 7 | Apple's on-device LLM, macOS 26+. No equivalent |
| NSPasteboard | 6 | Clipboard |
| CGEvent | 4 | Synthesises keystrokes to paste into the focused app |
| NSStatusItem | 1 | Menu bar. Windows counterpart is the system tray |

Third-party: `argmax-oss-swift` (WhisperKit), `fluidaudio` (Parakeet), `sparkle` (updates),
`swift-argument-parser`, `swift-syntax`.

## What the rig can and cannot prove

Linux, not Windows; not a Mac. Cross-compiling and unit-testing portable logic works. Exercising real
Windows audio, tray, clipboard or UI Automation does not — though Wine, a container, or a run on the
Windows host across the WSL boundary are all fair game if you want to close that gap.

Be precise about what a toolchain buys, because it is less than it feels like. Compilation is `MEASURED`
only when the exact TARGET toolchain builds the exact revision you cite; a successful Linux build does not
establish that the same code builds for Windows. A claim about Windows RUNTIME behaviour stays `ASSUMED`
however good your toolchain is.

## Evidence labels — every claim carries one

- `MEASURED` — you ran it and read the output. Say what you ran.
- `READ` — it is in the source or the docs. Cite the path. Source establishes implemented INTENT and code
  paths, never observed runtime behaviour, and you cannot run the macOS app.
- `ASSUMED` — you reasoned it and did not check. Legitimate and common. Never let it look like the first.

## First deliverables

Build whatever helps along the way — a spike that lets you measure beats an argument about what would
happen. The first thing to LAND is a map a human can decide from:

1. **Split the codebase three ways.** Portability CANDIDATE (no Apple imports — call it portable only
   after checking transitive dependencies and actually compiling it for the target, because a file with no
   Apple import can still depend on one two hops down), needs a Windows counterpart, or rewrite. Real line
   counts per module, read off the source.
2. **Name the Windows counterpart for each Apple dependency**, or state there is none: audio capture,
   global hotkey, tray icon, clipboard, synthetic keystroke, reading text around the cursor, on-device
   speech, on-device LLM, auto-update.
3. **Answer the language question with evidence.** Swift runs on Windows; whether it beats C#, Rust or a
   cross-platform toolkit depends on what those eight capabilities need. Lay out the costs, then say which
   you would pick and why. A prototype supports the recommendation only for the capability and platform it
   actually exercised. The final call is the founder's, but a committed recommendation he can push back on
   beats a fence-sitting comparison.
4. **Find the load-bearing constraints early.** The promise is sub-second transcription and text landing
   correctly in the foreground app. Anything threatening either is a headline finding, not a footnote.

## Working rules

- **Notes as you go, not at the end.** One file per topic in `notes/`, append rather than rewrite, terse,
  every claim labelled, every entry dated. A dead end is a result: record it with the reason.
- **Never claim a macOS behaviour you have not read in the snapshot.** Cite the file path.
- **The privacy boundary is a product commitment and it survives the port.** Dictated audio never leaves
  the user's machine; Envious Labs receives metadata only, never content. Read `macos-knowledge/CLAUDE.md`
  before proposing any architecture that moves user content across a network.
- **Windows is not macOS with different names.** Where the platforms genuinely differ in what a user
  expects, say so rather than porting the Mac's answer.
- Branch `rig/<topic>`, stage explicitly, leave `main` alone.
