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
| Modifier-only hotkeys | A bare modifier binding fires on the first key of every shortcut a user types. Making it safe needs tap-versus-hold discrimination, and getting it wrong breaks the keyboard rather than the feature. |

### Needs hardware or an environment

| Capability | What is missing |
|---|---|
| Bluetooth-aware routing and wake allowance | A Bluetooth headset to develop and verify against. Untestable here in the way that matters - a headset waking mid-press. |
| Contacts as vocabulary | The Windows contacts API needs a capability declaration and user consent, and this build is unpackaged. Reachability is unproven before any of it is worth writing. |
| Ollama catalogue install and remove | Ollama installed and running, to develop the install and remove path against. |

### Ordinary work, nobody blocking

| Capability | Note |
|---|---|
| AI-suggested aliases for custom terms | Ask the polish model what else a term might be heard as. The machinery exists; nothing has been written. |
| Warm capture-engine policy | Keeping the capture engine warm between dictations to shorten the first press. Distinct from the model-unload policy, which is now present. |

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

| Capability | What is actually there | Gap |
|---|---|---|
| Streaming (as "Live Preview") | `RunLivePreviewAsync` re-transcribes a rolling 20-second window every 2.5s and shows it in the UI ONLY | The final transcript is computed from scratch on release. The preview never feeds it. |
| Multi-route paste cascade | One direct value-write route (`TryDirectValueWrite`) plus a clipboard fallback | macOS runs several routes with per-route eligibility |
| Auto-stop on silence | An ENERGY segmenter with hysteresis drives auto-stop in toggle mode, off by default (`SpeechSegmenter`, `AutoStopPolicy`) | macOS uses a NEURAL detector, which also does speech-segment filtering. This one hears a slammed door as speech. The user-visible behaviour is present; the recogniser is not. |
| Per-dictation execution metrics | The whole wait is measured and logged as `DictationCompleted` | macOS records a per-dictation breakdown across capture, ASR, polish and delivery in ONE record. Here the stages log separately with nothing tying them to a dictation. |
| Custom-words import | The PORTABLE PROFILE import carries dictionary entries and snippets (`MainWindow.xaml:540`) | No import from a plain file, from a paste, or from a rival app. A user's existing word list still has to be retyped unless it arrives as one of our own profiles. |
| Custom-words export | The portable profile export carries them out (`MainWindow.xaml.cs:1263`) | No word-list export, no collision ownership, no bulk edit |

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
