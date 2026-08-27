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
Confirmed by two sweeps each. Ordered by what a user loses, not by effort.

| Capability | What macOS does | Impact |
|---|---|---|
| Streaming transcription overlapping the recording | Transcribes WHILE you speak, so the final text is ready almost as soon as you release | The single largest latency gap. Windows starts transcribing only on release. See PARTIAL: Live Preview. |
| AI-suggested aliases for custom terms | Suggests what else a term might be heard as | Every alias is typed by hand |
| Installable vocabulary packs | Ships domain word lists | Absent entirely |
| Contacts imported as vocabulary | Names from the address book are recognised | Names are misheard until typed in by hand |
| Hands-free lock mode | A third recording mode beside push-to-talk and toggle | Windows has two of three modes |
| Modifier-only hotkeys | Bind to a bare modifier | Windows requires a named key |
| Guarded synthetic Copy when an app publishes no selection | Quick Add still works in apps that publish nothing, clipboard preserved | Quick Add silently does nothing in those apps |
| Auto-stop on silence (neural VAD) | Recording ends when you stop speaking | Toggle mode needs a second press; no segment filtering |
| Pre-roll ring buffer | Keeps 500ms before the key press, reducing first-word clipping | First word can be clipped |
| Bluetooth-aware device routing and wake allowance | Handles a headset waking up mid-press | Untested and unhandled |
| ASR model-unload policy | Frees the model when idle | Model stays resident |
| Hallucination detection and output-safety classification | Catches a degenerate polish result | A bad polish result reaches the user |
| Ollama catalogue install and remove | Install a model from inside the app | The user installs Ollama models themselves |
| Built-in benchmark suite | Repeatable speed measurement in the app | No in-app baseline |
| Local dictation-audio archive, DEBUG only | Keeps the audio for debugging | Nothing to replay a bad transcript against |
| Bluetooth cold-start education card | Explains the first-press delay on a headset | Absent |
| Cross-process-safe custom-word mutations | Two copies of the app can edit words safely | `SingleInstanceLock` is app-wide, which is a different thing |

## FACT: partial-on-windows
The name exists. The behaviour does not match.

| Capability | What is actually there | Gap |
|---|---|---|
| Streaming (as "Live Preview") | `RunLivePreviewAsync` re-transcribes a rolling 20-second window every 2.5s and shows it in the UI ONLY | The final transcript is computed from scratch on release. The preview never feeds it. |
| Multi-route paste cascade | One direct value-write route (`TryDirectValueWrite`) plus a clipboard fallback | macOS runs several routes with per-route eligibility |
| Custom-words import | The PORTABLE PROFILE import carries dictionary entries and snippets (`MainWindow.xaml:540`) | No import from a plain file, from a paste, or from a rival app. A user's existing word list still has to be retyped unless it arrives as one of our own profiles. |
| Custom-words export | The portable profile export carries them out (`MainWindow.xaml.cs:1263`) | No word-list export, no collision ownership, no bulk edit |
| Per-dictation execution metrics | `DeterministicStageReceipt` records per-stage elapsed milliseconds for the deterministic pipeline (`DeterministicTextPipeline.cs:25`) | Covers the deterministic stages only. Nothing spans capture, ASR, polish and delivery for one dictation. |

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
