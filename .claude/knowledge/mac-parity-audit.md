<!-- tier: 2 -->
# macOS Parity Audit

**when:** "does Windows have X yet?", planning parity work, or before proposing a feature that may
already exist on one side. Taken 2026-08-27 against the Mac repo's own
`.claude/knowledge/capability-map.md`, which is the authoritative list of what macOS ships.

## RULE: this-audit-expires-and-its-absences-are-claims
Every PRESENT row was confirmed by a hit in the Windows tree. Every ABSENT row was swept twice, once
for the Mac's own symbol name and once for the CAPABILITY's synonyms, and both came back empty.

**An absence claim expires.** Re-run the sweep before acting on a row rather than citing this file, and
never cite this file as proof a capability is missing. The sweep command is in
PROC: how-this-was-taken below so the next person runs it rather than trusts the table.

**THE SOURCE LIST HAS ITS OWN LIMIT AND IT CUTS ONE WAY.** The Mac's capability map says of itself
that it is a router rather than a census. So a capability absent from BOTH the map and this audit may
still ship on macOS and be missing here, and nothing in this file would say so. Read the absent table as
a floor on the gap, never as its size.

**Rows marked PARTIAL are the dangerous ones.** Something with the right NAME exists and does less than
the Mac's version. A name sweep finds them and reports them present, which is how a parity gap survives
an audit. Read the "what is actually there" column before believing a PARTIAL row is done.

## FACT: absent-on-windows
Re-swept 2026-08-27 22:25 against the full macOS capability list: 40 of 48 present, up from 27 that
morning. Eight remain, and they are NOT one list - three need a decision nobody has made, three
need hardware or an environment this machine cannot provide, and two are ordinary work.

### Needs a decision, not effort

| Capability | The decision |
|---|---|
| Pre-roll ring buffer | Keeping 500ms before the key press means the microphone is always listening. Within the privacy contract - audio never leaves the machine - but the in-use indicator and the battery cost are visible to the user, so it is the founder's call rather than an implementation detail. |
| Hands-free lock | Built and REVERTED: the gesture cannot fire without deferring finalisation, which adds up to half a second before every short dictation delivers. Trading latency on the common path for a gesture some users never touch is a product decision. |
| Modifier-only hotkeys | BUILT 2026-08-28, NOT YET VERIFIED ON HARDWARE. Not a decision after all: a modifier cannot be held to talk, because holding one is how every shortcut begins, so the gesture is a TAP - press and release alone, quickly. Another key, another modifier, or a long hold all cancel it, and the key is never consumed on any path. Alt is refused: a lone Alt tap already opens the menu bar. Owed before this counts as done: with an ordinary binding, confirm Ctrl+C, Ctrl+V and Alt+Tab are untouched. |

### Needs hardware or an environment

| Capability | What is missing |
|---|---|
| Bluetooth-aware routing and wake allowance | A Bluetooth headset to develop and verify against. Untestable here in the way that matters - a headset waking mid-press. |
| Contacts as vocabulary | The Windows contacts API needs a capability declaration and user consent, and this build is unpackaged. Reachability is unproven before any of it is worth writing. |
| Ollama catalogue install and remove | Ollama installed and running, to develop the install and remove path against. |

### Ordinary work, nobody blocking

| Capability | Note |
|---|---|
| AI-suggested aliases for custom terms | DONE 2026-08-27. Parser, prompt, all three provider families and the screen. |
| Warm capture-engine policy | MEASURED AND REJECTED 2026-08-28. See below. |

## FACT: warm-capture-engine-was-measured-and-rejected
Not missing. Measured on the rig against a real build, eight key presses, and deliberately not built.

Holding the microphone open between dictations removes the OPEN half of a key press and nothing else.
Instrumenting the two halves separately gave:

| | open | start |
|---|---|---|
| first press, cold | 6 ms | 22 ms |
| presses 2-6 | 0-2 ms | 12-16 ms |
| after 180s idle | 1 ms | 12 ms |

**Opening is already free.** It is 0 or 1 ms in six of eight presses, and 1 ms even after three
minutes idle - which was the one case where warming could still have justified itself. Starting the
stream costs 12 ms and never less; that half cannot be removed by holding anything open. Best case
for the feature is about 1 ms.

**The first press pays on BOTH halves** (6 and 22 against a steady 0 and 12), so the cold-start cost
is general warmup rather than device opening, and warming the device would not have removed it.

**This also retires a founder decision that was never spent.** The feature raised a real privacy
question - whether an open microphone lights the Windows in-use indicator - and that question only
had to be answered if the feature was worth building. It is not.

**What was shown and what was not.** Opening is cheap: shown. WHY it is cheap: not shown. The natural
reading is that the audio library defers the real work to start, and a genuinely fast open would
produce identical numbers. The distinction matters for the privacy argument and not at all for the
build decision, because a 1 ms saving does not need a privacy question answered either way.

The two log lines stay. They are now a latency tripwire: the 12 ms floor is the fastest a key press
can be, and if it drifts upward nothing else would report it.
Ref: rig measurement 2026-08-28, build 00:42:35.

## FACT: what-was-closed-2026-08-27
Thirteen capabilities in one session. Listed because the audit above only shows what remains, and a
reader comparing it to the morning version cannot otherwise tell what moved.

Streaming transcription's core (planner and accumulator; the worker integration is the remaining
half) · speech segmentation · auto-stop with its settings · hallucination detection on polish output
· custom-words import and export · vocabulary packs with a picker · guarded synthetic copy, so Quick
Add works in terminals · model-unload policy · per-dictation wait metric · benchmark statistics and a
speed check · debug audio archive with a WAVE writer and retention.

**And one REVERTED after the fact**, which belongs in the same list because reverting it was the
work: hands-free lock, removed once a control test proved the wiring could never reach it.

## FACT: partial-on-windows
The name exists. The behaviour does not match.

**FOUR ROWS IN THIS TABLE WERE WRONG WHEN READ ON 2026-08-29, EVERY ONE IN THE SAME DIRECTION.**
Custom-words import, custom-words export, per-dictation metrics and streaming-feeds-the-final-transcript
were all described as absent or partial, and all four ship. A session about to build plain-file import
found it already there, reachable, and better than what it was about to write.

**THE DIRECTION IS THE FINDING, NOT THE COUNT.** Every error was toward "missing", and the cost of that
error is rebuilding something that works - which is expensive, and which also REPLACES a tested
implementation with an untested one. The file's own opening rule predicts this and says an absence
claim expires; four rows in one reading says the expiry is faster than anyone assumed.

**RE-SWEEP BEFORE BUILDING AGAINST ANY ROW IN THIS TABLE.** Two of the four were caught by reading the
code for five minutes before writing any. That is the whole cost of the check.

| Capability | What is actually there | Gap |
|---|---|---|
| Streaming (as "Live Preview") | **STALE ROW, CORRECTED 2026-08-29. The streamed text DOES feed the final transcript.** `TranscribeUsingAnyHeadStartAsync` takes the streamed head start, re-transcribes only the audio AFTER the point streaming reached, and joins the two through `StreamingTranscriptAccumulator`. It falls back to transcribing everything when the head start is unusable. | Whether the JOIN is as good as macOS's is unaudited. The seam between head start and tail is where a duplicated or dropped word would appear, and nothing here has compared the two implementations' seam handling. |
| Multi-route paste cascade | One direct value-write route (`TryDirectValueWrite`) plus a clipboard fallback | macOS runs several routes with per-route eligibility |
| Auto-stop on silence | An ENERGY segmenter with hysteresis drives auto-stop in toggle mode, off by default (`SpeechSegmenter`, `AutoStopPolicy`) | macOS uses a NEURAL detector, which also does speech-segment filtering. This one hears a slammed door as speech. The user-visible behaviour is present; the recogniser is not. |
| Per-dictation execution metrics | **Re-read 2026-08-29: every stage was ALREADY measured**, and the code already argues for measuring the whole wait rather than the sum, so the gap was never the timings. It was the JOIN. `DictationScope` now stamps every line of the post-capture pipeline with one id, ambient rather than threaded, so a helper cannot forget it. | Capture, the streaming worker and auto-stop run before that scope and are not joined - #78. The join is LOCAL only: `PrivacySafeObservabilityTests` refuses any identifier on the record that crosses the network, and it fired on the first attempt. |
| Custom-words import | **BOTH ROWS BELOW WERE STALE AND ARE CORRECTED. Re-swept 2026-08-29.** A plain word list imports from a file today: `CustomWordImport.Read` in Core, reached by an "Import words" button on the Dictionary page (`MainWindow.xaml:442`). It takes a comma, a tab or an equals sign, reports collisions rather than deciding them, and refuses a quoted CSV comma out loud rather than mangling it. | **Closed 2026-08-29: a "Paste a list" button reads the clipboard through the same path, and a conflict is now OFFERED rather than decided - the message carries a button that takes the list's version, in place, keeping the user's order.** Remaining: no import from a rival app's own format. |
| Custom-words export | `CustomWordImport.Write` behind an "Export words" button, comma-separated so it opens in a spreadsheet | No bulk edit. Collision ownership is now answered on the IMPORT side by the offer above. |

## FACT: present-on-windows
Confirmed present, not confirmed equivalent. Depth is unaudited.

**Locked-language mode** is present and user-facing: `WhisperLanguageComboBox` offers Automatic plus a
fixed language (`MainWindow.xaml:446`). **In-session salvage** is present: when the transcription worker
dies, `RuntimeWorkerTranscriptionEngine` brings it back and RETRIES the same audio rather than losing the
dictation (`RuntimeWorkerTranscriptionEngine.cs:116`).

Windows also ships these, all confirmed by file, and whether macOS has each is UNCHECKED: a reusable
snippets library (`MainWindow.xaml:418`) · portable profile import and export (`MainWindow.xaml:540`) ·
Windows, Light and Dark appearance modes · a user-selectable microphone (`MainWindow.xaml:438`) · three
recording-pill designs (`MainWindow.xaml:506`) · top or bottom pill placement · bring-your-own-key storage
in the Windows Credential Manager (`WindowsCredentialApiKeyStore.cs`) · automatic CPU transcription
fallback (`ParakeetEngineFactory.cs:41`).

Dual on-device engines with user selection · manifest-pinned model download and verification · LLM polish
across cloud, local and first-party providers · first-party on-device polish model · custom-word fuzzy
correction · filler-word removal · inverse text normalisation · spoken punctuation · spoken emoji and
post-polish restoration · clipboard snapshot and restore · cursor-aware spacing and casing · seam
de-duplication · terminal-aware insertion · target-app freezing at record start · push-to-talk and toggle ·
Escape Recovery · recording sound cues · tray status icon · floating recording overlay · transcript history
with search · Quick Add from selection · encrypted recovery store and launch replay · model-load wedge
detection · auto-update surfacing · guided onboarding · permission surfacing · Whisper model install ·
telemetry · debug-mode local log.

## FACT: what-the-review-round-changed
A Codex adjudication of the first draft moved FIVE rows, and every one moved the same way: I had called
something absent that was there. Profile import and export already carry dictionary entries and snippets;
the deterministic pipeline already records per-stage timings; locked-language mode already ships as a
user-facing picker; the worker already retries the same audio after a crash rather than losing it.

**The shared cause is worth more than the five rows.** Each was swept for the macOS SYMBOL and for the
macOS FRAMING of the capability, and this port solves several of them by a different route with a
different name. A synonym sweep finds a capability spelled differently; it does not find one SHAPED
differently, and no amount of extra synonyms would have. What found them was a reviewer with the repo in
front of it and the inventory in hand.

So the audit was too pessimistic rather than too generous, and it failed toward WORK - toward building
something already shipped. That is the direction to expect from any parity audit written from one side.

## FACT: what-this-session-closed
Re-swept 2026-08-27 after the work, so the tables above are not stale.

**Closed as behaviour, not as recogniser:** auto-stop. A toggle-mode recording can now end when the
speaker stops. It is off by default, and that is the founder's own priority order deciding it rather
than caution: dictation works every time it physically can, so a switch that can end a recording
early must not be on for anyone who has not asked.

**The verification limit is stated because it will not go away.** The Windows session can drive the
state machine - when it arms, whether a pause resets it, whether it fires with a key held, whether
it fires before anyone speaks - and every one of those is a real defect that needs no voice. It
CANNOT speak, pause mid-thought and resume, which is the failure that matters. That needs the
founder's microphone, and an approximation of it would sound confident and mean nothing.

**"All Settings" was REMOVED on parity grounds**, and it is recorded here because it is the shape
worth remembering: it sat in the founder's queue as a product decision for hours, and macOS
shipping fifteen sections and no aggregate page answered it in one reading. **It was a lookup
wearing a decision's clothes.** The reverse held for the Clipboard page, which reads as unfinished
and stays, because macOS ships the same section.

## FACT: all-four-critic-findings-closed-in-code-2026-08-29
Closed on branch `codex/ui-design-system`.

**#63, the pill could not offer an ACTION: BUILT, NOT YET PRESSED.** `PillAction` carries a printed
label, a separate spoken label and an INTENT; the app translates the intent to a page. Eight advisories
now offer "Open settings". Hover pauses the dwell clock, which is not decoration: an advisory dwells six
seconds, moving a mouse to a button spends much of that, and macOS pairs its own action pills with
hover-pause for the same reason.

**Nobody has pressed it.** A session reaching the rig over SSH runs in session 0, and a WinUI app
launched from there dies in `Microsoft.UI.Input.dll` with `0xc0000602` before drawing. Confirmed
ENVIRONMENTAL rather than ours by running the same launch against the reviewed commit with no working
changes: identical death, different `EnviousWispr.App.dll` hash. `ENVIOUSWISPR_UAT_OVERLAY_STATE` now
accepts `advisory` (carrying the button) and `distress` so whoever is on the desktop can reach it.

**#65, appearance inferred from the message text: CLOSED, and it was hiding two live defects.**
`OverlayStateFor` is deleted. `DictationStatus` in `EnviousWispr.Core.Presentation` carries the state
beside the text, so every producer names the pill it wants. The two defects the deletion exposed were
both sentences beginning "Recording": a memory-pressure PAUSE and a timed-out CANCEL each matched
`StartsWith("Recording")` and wore the live listening pill with a running timer.

**#64, one severity: CLOSED.** `Advisory` and `Distress` exist, with the macOS rationale carried into
the enum. Routed: four Ollama health rows, three local-transcription setup rows and the
audio-captured-but-unavailable row to Advisory; memory-pressure pause and Windows-interrupted to
Distress.

**And a fifth nobody had filed: the pill drew ONE capsule for every outcome.** Surface, border and ink
were identical for success, warning and error - the same hole `design-system.md`
RULE: every-severity-needs-its-own-tint describes for the in-window notifications, one surface over.
Each severity now has an ink and a wash token in all three themes plus three style families.

**Distress shares the error ink deliberately**, with a deeper wash and a repeating opacity storyboard.
The reuse is stated in the expected-token table rather than left to look like a token falling back to a
neighbour, which is how the notification hole was created.

## FACT: the-tray-icon-now-changes-2026-08-29
#74, closed in code. Four states matching macOS - idle, recording, processing, error - drawn at runtime
from the brand mark rather than shipped as .ico files, because the tray asks for a different size at
every display scale.

**EVERY STATE HAS ITS OWN SHAPE, NOT ONLY ITS OWN COLOUR, AND A TEST FORCED THAT.** macOS tells idle
from recording by colour alone, which it can afford because the menu bar tints its icons; the Windows
notification area draws a bitmap exactly as handed over. Under High Contrast every colour collapses to
one system colour, and the first version then drew three of the four states identically. The icon would
have stopped saying anything at the moment it mattered most.

**Three review findings had one cause: values read once in a constructor.** Reduce Motion, display DPI
and High Contrast are all read live now, through `SystemEvents.UserPreferenceChanged` and
`DisplaySettingsChanged`. `SystemAnimationsAreOn` fails CLOSED - an accessibility preference that cannot
be read is not one to guess in our own favour.

**Stopping a timer does not unpost the work it has queued.** The sweep's frame callback rechecks the
state rather than trusting that the timer stopped, or a user turning animations off mid-transcription
would still see queued frames arrive.

**Not yet seen by anyone**, and tracked with the rest of the unobserved UI: the live path from a system
setting changing to the icon redrawing needs somebody toggling High Contrast, Reduce Motion and display
scale on the real desktop.

## FACT: the-parity-audit-marked-the-tray-icon-present-and-it-was-not
Filed 2026-08-29 as #74. `WindowsTrayIcon.SetStatus` sets a TOOLTIP; `_notifyIcon.Icon` is assigned once
at construction and never again. macOS `MenuBarIconAnimator` renders and swaps five states including
two animations, and honours Reduce Motion.

**This is the PARTIAL class the audit warns about, caught by reading rather than by sweeping.** The
capability has the right name on both sides, a name sweep reports it present, and the Windows version
does a fraction of the work. Read it as evidence that the present-on-windows table is a list of NAMES
confirmed, exactly as its own heading says, and that every row in it can hide a gap this size.

## FACT: what-actually-stands-between-here-and-parity-2026-08-29
Re-swept against the tree rather than read off the tables above, because four of those rows were wrong.
**Almost nothing left is blocked on code.**

### Blocked on a person at the machine
| Capability | What is needed |
|---|---|
| The whole 2026-08-29 UI: pill severities, the pill's action button, four tray icons, High Contrast, Reduce Motion | #76. Nobody has seen any of it. A session over SSH runs in Windows session 0, where a WinUI app dies in `Microsoft.UI.Input.dll` before drawing. Confirmed environmental by running the same launch against a reviewed commit. |
| The fourth delivery tier, macOS's `menuPaste` | #77. Invoking an app's own Paste command. Getting it wrong presses the wrong control inside somebody's document, so it needs Word, Excel and OneNote open in front of a person. |
| Modifier-only hotkeys, `Ctrl+Win` as the default | #66. Built. Needs a real keyboard. |
| Bluetooth-aware routing and wake allowance | A Bluetooth headset. |
| Ollama catalogue install and remove | Ollama installed and running. |

### Blocked on a decision nobody has made
| Capability | The decision |
|---|---|
| Pre-roll ring buffer | Confirmed still absent by sweep. Keeping 500ms before the key press means the microphone is always listening: within the privacy contract, visible to the user as an in-use indicator and a battery cost. |
| Hands-free lock | Present in the gesture policy and its tests, absent from production. Was built and reverted: the gesture cannot fire without deferring finalisation, which adds latency to every short dictation. |
| Contacts as vocabulary | Needs a capability declaration and user consent, and this build is unpackaged. |

### Ordinary work, nobody blocking
| Capability | Note |
|---|---|
| Capture, streaming and auto-stop lines joined to their dictation | #78, with the `AsyncLocal` constraint and the design that survives it recorded on the issue. |
| Auto-stop's recogniser | Behaviour ships; macOS uses a NEURAL detector and this hears a slammed door as speech. Needs a model, which is closer to ordinary work than to a decision. |
| The streaming JOIN's quality | The head start feeds the final transcript; whether the seam matches macOS's is unaudited, and the seam is where a duplicated or dropped word would appear. |
| Import from a rival app's own format, and bulk edit | The last two custom-words rows. |

**THE SHAPE OF WHAT IS LEFT IS THE POINT.** Five of the remaining items need somebody at the machine and
three need a product decision. A session that can only reach the machine over SSH cannot close them by
writing more code, and writing more code around them is how the unverified pile grows.

## PROC: how-this-was-taken
Two sweeps per capability from `src/Production`, the second using the CAPABILITY's synonyms rather than
the Mac's symbol, because a name sweep only finds what someone already called by that name:

```
/usr/bin/grep -rlEi "<pattern>" --include='*.cs' --include='*.xaml' . \
  | /usr/bin/grep -v '/obj/' | /usr/bin/grep -v '/bin/' | /usr/bin/grep -v 'Tests'
```

**Read the HITS, never the count.** Broad synonym patterns match noise heavily: `Import` matched every
`using` line, `Pack` matched `WhisperModelPack`, `capitali` matched half the tree. Every row above was
classified by reading the hits, and several first-pass MISSING results were false.

## FACT: critic-pass-against-the-macos-overlay-source-2026-08-28
The first parity check done by READING macOS rather than by remembering it. Four gaps, none of which
was on the feature list, because the list enumerated FEATURES and this compares SURFACES.

Source: `macos-source/Sources/EnviousWisprAppKit/App/Overlay/PillDefinition.swift`, a local snapshot.

**PRESENT ON BOTH, so this is a gap list and not a verdict:** recording / processing / success /
warning / error states, per-severity dwell, pill designs gated on whether Live Preview is on,
top-or-bottom position, live preview text, audio level, elapsed time.

### 1. The Windows pill cannot offer an ACTION
macOS's recovery notice offers **Discard** and its accessibility toast offers **Grant**, on the pill
itself. The Windows overlay markup contains **zero** buttons - counted, not assumed. So where macOS
lets a user fix the thing from the notice, Windows tells them and leaves them to find the setting.
This is the largest of the four and the only one a user would notice unprompted.

### 2. No `advisory` severity, and macOS wrote down why it exists
macOS separates "your setup needs attention" from "our software failed", with the rationale in the
source: an error's red mark and "Error" heading say the app broke, and a user-setup advisory is not
the app breaking. Windows has Warning and Error only, so a setup problem is shown as one or the
other, and Error blames the wrong party.

### 3. No `distress` severity
macOS's interruption look. No Windows equivalent.

### 4. THE PILL'S APPEARANCE IS INFERRED FROM THE MESSAGE TEXT
`OverlayStateFor` in `MainWindow.xaml.cs` reads the status sentence -
`StartsWith("Recording")`, `Contains("copied only")` - and picks the pill from the words.

macOS is built the other way round and its source says why, verbatim: *inferring a visual from a
string is how a copy edit silently changes an icon.*

**So a copy change can make the pill vanish.** Rewording "Recording. Release to finish" to
"Listening..." drops through every branch to Hidden and there is no pill at all while the user
speaks. No code change, no failing test, no different build.

The fix is to hand the state in beside the text. Until then a source-level guard checks the reverse
direction - that every trigger word the mapping looks for still appears in something the app says -
so an orphaned trigger goes red instead of going quiet. That guard is written and NOT YET VERIFIED,
because builds on the rig are stopped.

### What this says about the method, not the app
The feature list said 43 present and missed all four. A list of features cannot find a gap in HOW a
feature behaves; only a comparison of the two implementations can, and that needs the other side's
source in front of you rather than in memory.
