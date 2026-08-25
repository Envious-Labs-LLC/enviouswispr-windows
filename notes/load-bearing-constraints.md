# Load-bearing constraints — what threatens the two promises

**Date: 2026-08-24.** Companion to `portability-map.md`. This is deliverable 4 of the
project brief, written against the snapshot source (`f9b70283`) and the 2026-08-24
knowledge base. Every claim labelled. The two promises:

1. **Sub-second transcription** — founder decision: "sub-second" means transcription,
   NOT ASR+polish+paste (`READ` pipeline-mechanics.md FACT `subsecond-is-transcription-…`).
   Mac fleet baseline, PostHog 30-day trailing median 2026-08-21 (`READ` same FACT):
   **0.61 s** no-polish (n=24,376), **1.65 s** on-device polish (n=29,443).
2. **Text lands correctly in the app that has focus** — tiered paste cascade plus
   cursor-aware insertion repair (`READ` pipeline-mechanics.md FACTs `paste-cascade-tiers`,
   `insertion-repair-runs-after-the-chain`).

These are the only two numbers the product is judged on. Everything below is what could
break each one on Windows.

**Addendum 2026-08-24 (evening):** "the Windows host" in this file is now **this
rig** — it moved from Linux/WSL to native Windows (see `toolchain.md`). S1/S2 are
locally runnable; neither has run yet, so nothing here has become MEASURED.

## How the Mac achieves promise 1 (READ — snapshot at `f9b70283`)

The budget, in order, per `architecture.md` FACT `data-flow` + `pipeline-mechanics.md`:

| Stage | Mechanism on the Mac | Windows counterpart | Threat level |
|---|---|---|---|
| PTT press → capture armed | PTT start awaits capture prewarm; session warm-up waits for stable format (1.5 s max, 200 ms poll, one rebuild), wires buffer callback BEFORE capture starts, then enters `.live` (FACT `parakeet-pipeline`) | WASAPI shared-mode + same prewarm logic, re-derived (FACT `audio-capture-internals`; gotchas-audio.md owns the traps) | LOW-MED — logic ports, device story re-derived |
| VAD/conditioning | In-process Silero VAD on every 4,096-sample chunk + `CapturedAudioConditioner` (FACT `vad-config-and-filtering`) | Silero via ONNX Runtime (same model family) | LOW — tiny model; one-time load cost |
| ASR decode (the dominant term) | Parakeet TDT 0.6B v3 batch, in an XPC helper process (`EnviousWisprASRService`), model RESIDENT; `useStreamingASR` default off (FACT `xpc-path-default`, `parakeet-pipeline`) | ONNX Runtime (CPU/DirectML/CUDA) int8, helper process under our lifetime control | **HIGH — UNMEASURED. This is spike S1 and the only number that can kill the promise** |
| Deterministic chain | wordCorrection → filler → emoji → ITN → (optional polish, OUTSIDE the sub-second bar) → emoji restore (FACT `text-processing-chain`) | Pure logic, rewritten in C# against the 164K-line test corpus | LOW — no platform surface |
| Paste | Tier cascade, 200 ms post-paste wait on the workhorse tier (FACT `paste-cascade-tiers`) | SendInput + clipboard (promise 2 below) | see promise 2 |
| Warm-up of polish | `LLMPolishStep.preWarm()` at recording start hides setup behind capture/ASR (FACT `llm-polish-networking`) | Same, trivial | LOW |

**Model residency is a measured Mac pain, not an abstract one** (`READ` FACT
`xpc-path-default`): launchd reaps the idle Parakeet helper by design — 14 days of
production data show **1.54 reclaims per active user-day, 48.1 % of active user-days
affected**; the #959 warm-respawn absorbs it at **p50 459 ms / p95 1,881 ms / p99
12,783 ms** — and that path shows no UI, so the user sees a silent multi-second dead app.
Pinning with `xpc_transaction_begin()` was researched and REJECTED (#1906).

**Windows threat T1.1 (headline): RTFx for Parakeet int8 on Windows is UNMEASURED.**
The 0.61 s median was produced by CoreML on Apple Silicon (ANE/Metal). A 0.6 B int8
model on a modern x86 CPU or a dGPU is very likely sub-second (ASSUMED — the model is
small and int8; no measurement on this workload exists anywhere). It must be MEASURED
before the promise is repeated for Windows: **spike S1** on this rig (WASAPI →
Parakeet v3 int8 batch → finalize, via ONNX Runtime; CPU + RTX 4090 tiers — GPU
confirmed MEASURED 2026-08-24).
Anything that fails S1 with >1 s median changes the product, not the benchmark.

**Windows threat T1.2: cold/warm model lifecycle — but this one FAVOURS Windows.**
There is no launchd reaper on Windows. The tray app can keep the ONNX session and the
llama-server helper resident for the whole session at zero OS-imposed cost, deleting
the entire measured 459 ms–12.8 s respawn tail the Mac lives with. Decision to record:
**resident-by-default, no unload policy in v1** (the Mac's `modelUnloadPolicy` exists
because the OS forces it; we do not). Cost: ~0.5 GB (Parakeet int8) + up to 2.89 GB
(EG-1, only when polish is enabled) of resident RAM — acceptable for a tray dictation app.

**Windows threat T1.3: capture reliability machinery is re-derived, not ported.**
The liveness/zero-signal/route-change traps of gotchas-audio.md were measured on
CoreAudio AUHAL semantics. WASAPI gives coarser device introspection (no per-device
mute property, different hotplug/route-change events, `READ` windows-native-stack.md
pillar map). This threatens the TAIL (rare failed takes), not the median.

**Windows threat T1.4: EG-1 polish on Windows silicon.** Same llama.cpp architecture
(READ eg1-operations.md), but the quality bar (93.7 %) was verified off-Metal? No — it
was verified ON Metal on the Mac; the off-Metal CPU/CUDA contract run is already
scheduled as a rig-runnable spike (see `Windows Port Research.md` path forward #2).
Polish is outside the sub-second bar, so this threatens the *quality* promise, not the
latency one.

## How the Mac achieves promise 2 (READ)

1. **Tiered paste cascade** (FACT `paste-cascade-tiers`, `PasteCascadeExecutor.swift` /
   `PasteService.swift`): freeze target app + focused AX element + paste settings at
   recording start; then Tier 1 AX direct write **with character-count-change
   verification** → Tier 2 clipboard snapshot + CGEvent Cmd+V + 200 ms wait + guarded
   restore → 2b AppleScript (activation timeout only) → 2c `AXPress` on a Cmd+V menu
   item found by metadata → 3 clipboard-only + notice. Every round logs which tier.
2. **Sacred clipboard contract** (RULE `clipboard-restore-is-sacred`):
   `ClipboardSnapshot` preserves **every item and every type**; restore happens only if
   the pasteboard `changeCount` still equals our post-write count. Failures elsewhere in
   the pipeline must never touch clipboard policy.
3. **Cursor-aware insertion repair** (FACT `insertion-repair-runs-after-the-chain`):
   one caret-context read, ONE `CursorInsertionRepair` candidate, revalidated at each
   paste route's own commit boundary (`caretUnchanged`) before submission; cumulative
   delivery budget (100 ms class); **no caret context → today's legacy payload**, so the
   whole feature degrades to "paste the text" rather than breaking it. Terminal payloads
   carrying a line break are REFUSED (a screen-derived line is one rendered row; in a
   terminal a newline submits the command). For terminals whose caret is a lie
   (Ghostty pins `AXSelectedTextRange` at 0 — `READ` accessibility-macos.md), a
   screen-parse path reads the rendered grid instead, with a per-process circuit
   breaker and a Gate-1 process scan whose measured cost (36–115 ms of the cumulative
   budget) nearly broke the deadline before.
4. **Casing at the seam** needs `NSSpellChecker`/`NLTagger` (12 languages supported,
   tiered by measured dictionary honesty — `READ` cursor-aware-insertion.md
   `language-support-matrix`). Windows has no equivalent (api-equivalents.md #14) —
   the Mac already ships the no-context legacy fallback, so this degrades, not breaks.

### Windows threats, ranked

**T2.1 (headline): UI Automation coverage is per-app and UNMEASURED.** The Mac's AX
layer is deep and uniform; UIA TextPattern/ValuePattern support varies app by app
(ASSUMED weaker until measured). The S2 matrix must cover the real target list — Word,
Outlook, Chrome/Edge, VS Code, Cursor, Teams, Slack/Discord (Electron), Windows
Terminal, Notepad, Excel. Known Mac-side anchor points that bound the risk: Word is
"NO, permanently" for READING even on the Mac (write-only `AXLayoutArea`, `READ`
accessibility-macos.md) yet dictation INTO Word works fine there via Tier-2 paste —
so the Windows Word risk is paste, and paste is the robust tier. Electron apps on
Windows expose Chromium's UIA tree when a client connects; the Mac's intermittent
"native AX bridge can be OFF" defect (FACT `chrome-native-ax-bridge-can-be-off`) is a
different mechanism and may not have a Windows analogue (ASSUMED — S2 settles it).

**T2.2: the clipboard contract has a real semantic gap, not just an API rename.**
- `changeCount` → `GetClipboardSequenceNumber` (increments on writes; ASSUMED
  sufficient guard until S2 tests listener apps like PowerToys Run / OneNote).
- **Multiple items do not survive.** macOS pasteboards hold *several items* (e.g. three
  images + a text rendition); `ClipboardSnapshot` preserves all of them. The Win32
  clipboard holds ONE data object (many formats, one item). A user with 3 images
  copied loses 2 on restore. This degrades the "sacred" contract from *all items and
  types* to *all formats of the current item*. **This is a product decision for the
  founder, not an engineering detail** — options: accept the degradation in v1 (the
  common case — text — is lossless), or invest in OLE deferred-render multi-item
  emulation (significant work for an edge case).

**T2.3: UIPI and RDP are the permission-model analogues.** No TCC prompt on Windows
(good), but `SendInput` into an **elevated** target window is blocked by UIPI with no
prompt — the user gets a silent failure. Detect target elevation
(`GetTokenInformation` on the focused process) and surface it. RDP: the app must run
in the user's session; input injected into the console session is invisible to an RDP
user (and vice versa). Both need explicit test cases; neither is a blocker if handled.

**T2.4: terminals are the LOWEST-risk part of promise 2 on Windows.** Windows Terminal
is a modern UIA app (TextPattern exposed — ASSUMED, verify in S2); conhost exposes the
console buffer directly (`ReadConsoleOutput`) — a simpler analogue of the Mac's
screen-parse path. The newline-refusal and circuit-breaker logic ports unchanged.

**T2.5: timing constants were tuned on Apple Silicon.** The 50 ms activation poll,
1 s caps, 200 ms post-paste wait, 100 ms cumulative repair deadline, and the
Gate-1-scan budget all carry Mac-measured constants. Re-tune on Windows in S2; do not
port the numbers blindly.

**T2.6: no TCC means no "permission" failure class, but a new "silently unimplemented"
one.** AX trust is a binary the Mac can check (`AXIsProcessTrusted`). On Windows the
pattern is always *available* but the app may not implement it — diagnostics must read
per-app capability, not a global grant.

## What the Mac measurements do NOT prove (honest scope)

- 0.61 s / 1.65 s are **Mac fleet medians** (PostHog, 2026-08-21). They define the bar;
  they are not a Windows expectation. No Windows number exists yet.
- Every Windows runtime claim above is **ASSUMED** until S1/S2 run on this rig
  (it became the Windows host on 2026-08-24 evening; neither spike has run yet).
  The Mac-side mechanics are READ from the snapshot and its knowledge base
  (dated 2026-08-24).
- The snapshot is a photograph: `f9b70283`, 2026-08-24. Re-verify before citing as live.

## The two decisions this section forces

1. **S1 and S2 are the gates for the two promises.** No Windows launch claim before
   both have numbers. (Already in `Windows Port Research.md` path forward; restated
   here as the load-bearing reading of the same plan.)
2. **Two Windows-forced design choices, not ports:** (a) model residency defaults to
   resident (no OS reaper exists to work around); (b) clipboard contract scope in the
   face of the one-item Win32 clipboard (T2.2) — founder decision.
