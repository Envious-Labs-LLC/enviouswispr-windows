# Production WinUI journey UAT

This harness proves the production shell-to-worker-to-delivery path with controlled public input. It launches
the real WinUI app with an isolated profile, requires exactly one owned final-ASR worker, focuses the native
WinForms delivery target, and signals a strictly allowlisted in-app journey. The app accepts only the four
journey fixtures selected from the reviewed Whisper fixture manifest by SHA-256.

The journey uses the same push-to-talk session controller, final transcription worker, deterministic text
pipeline, recovery handling, context capture, and delivery adapter as a normal launch. Only audio capture and
the physical key transition are substituted: the reviewed fixture implements `IAudioCapture`, and named events
request press and release. Those hooks require `ENVIOUSWISPR_UAT_JOURNEY=public-fixture-v1`, reviewed fixture
bytes, isolated event names, and an isolated credential suffix. A normal launch still creates WASAPI capture.

Build the production app, delivery target, and harness, then run on an interactive configured Windows machine:

```powershell
pwsh -NoProfile -File .\scripts\validate.ps1
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj -c Release
```

The default path uses the reviewed French fixture with Whisper. Add `--english-parakeet` to run the same
deterministic production journey with the pinned English fixture and Parakeet:

```powershell
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet
```

Either deterministic success path can identify and exercise an exact installed candidate instead of the
repository build. Exit the normally running candidate first and supply its fully qualified executable path.
The harness requires the candidate's companion runtime worker, emits ProductVersion, executable SHA-256, and
the bounded source label `ExplicitCandidateExecutable`, but never emits the supplied path:

```powershell
$installedApp = Join-Path $env:LOCALAPPDATA `
  'EnviousLabs.EnviousWispr.Founder\current\EnviousWispr.App.exe'
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet --app-executable $installedApp
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --app-executable $installedApp
```

These runs still substitute reviewed fixture capture and named press/release events. They prove the exact
installed shell, worker, ASR, deterministic pipeline, and controlled native delivery; they do not replace the
physical global-hotkey and microphone journey below.

The English fixture can also exercise an isolated deterministic settings profile. The enabled profile adds a
reviewed custom-word entry whose replacement must then pass through filler removal, spoken emoji, and spoken
punctuation before the controlled target accepts it. The disabled profile keeps the same custom-word entry but
turns all four user switches off and requires the original recognized word to survive. The result reports only
the profile name and enabled boolean, never transcript or target content:

```powershell
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet --deterministic-profile enabled `
  --app-executable $installedApp
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet --deterministic-profile disabled `
  --app-executable $installedApp
```

Deterministic-profile UAT is intentionally isolated from polish, Live Preview, live microphone, Escape Recovery,
and fault injection so a passing transformation has one unambiguous settings and stage-order contract.

The same reviewed-fixture success journey can require healthy local AI polish. These modes wait for provider
readiness, require `PolishStarted` and `PolishCompleted` from the selected provider, reject degraded fallback,
and verify native delivery plus exact owned-process cleanup. They never emit the supplied EG-1 paths or the
selected Ollama model ID:

```powershell
$founderData = Join-Path $env:LOCALAPPDATA 'Envious Labs\EnviousWispr-Founder'
$egOneServer = Join-Path $founderData 'runtime\llama.cpp\llama-server.exe'
$egOneModel = Join-Path $founderData 'models\eg-1\active.gguf'
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet --polish eg-1 `
  --eg1-server $egOneServer --eg1-model $egOneModel --app-executable $installedApp

dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet --polish ollama `
  --ollama-endpoint http://localhost:11434 --ollama-model <installed-local-model> `
  --app-executable $installedApp
```

EG-1 requires an existing local GGUF and app-owned `llama-server.exe`. Ollama requires an already running
loopback daemon and installed local chat model; the harness never installs or pulls one. Local-polish UAT is
kept separate from Live Preview, live-microphone, Escape Recovery, and fault-injection modes so one result has
one unambiguous resource and fallback contract.

The same exact installed journey can prove safe cloud composition without using a real credential or making a
billable request. OpenAI, Anthropic, and Gemini modes always use the harness's unique isolated Credential Manager
suffix, require `PolishStarted` followed by provider-tagged `PolishDegraded/PolishCredentialMissing`, reject a
false completion, and require deterministic text to continue through native delivery and clean process teardown:

```powershell
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet --polish openai --app-executable $installedApp
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet --polish anthropic --app-executable $installedApp
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet --polish gemini --app-executable $installedApp
```

These are credential-fallback tests, not real-provider quality or billing acceptance. Real generation remains
available only through the separate explicit-consent cloud UAT with a founder-supplied key.

Add `--escape-recovery` to exercise the same production pipeline while cancelling with Escape Recovery
enabled. That mode requires transcription and deterministic processing to finish, requires a 24-hour recovery
entry in History, and proves that no text reaches the controlled target:

```powershell
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --escape-recovery
```

Three mutually exclusive failure journeys exercise the required native fail-safe paths:

```powershell
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --failure microphone-unavailable
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --failure worker-startup
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --failure target-unavailable
```

The microphone mode injects a strongly allowlisted `AccessDenied` capture result and drives the installed global
hook through the normal session controller. The worker mode launches an isolated copy of the production payload
with only the owned worker executable omitted. The target mode closes the controlled edit window after recording
starts and requires clipboard-only refusal rather than unintended delivery. It snapshots every supported
clipboard format before that test, refuses to proceed when a format cannot be cloned safely, and restores the
original clipboard during cleanup. None of these modes changes the normal app path or records content.

The separate live-audio mode keeps the production WASAPI capture and global hook. It synthesizes F8 edges,
plays the reviewed public French MINDS-14 fixture through the default speakers, and requires the known word
`adresse` to travel back through the default capture device and appear in the controlled target. The harness
verifies the fixture's reviewed SHA-256 before playback:

Playback applies a bounded 2x in-memory gain with clipping protection and plays the fixture twice with a short
silence gap so a webcam microphone can hear the quiet mu-law source; no converted or amplified audio is written
to disk. Use `--acoustic-gain 1` through `--acoustic-gain 8` to override the in-memory fixture gain when measuring
another machine.

```powershell
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --live-microphone
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet --live-microphone
```

For a clearer source than the quiet call-center fixture, the English Parakeet path can speak a fixed public
sentence through Windows speech synthesis. The phrase is created in memory and is never written to disk:

```powershell
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet --live-microphone --synthesized-acoustic
```

This mode is audible. Before the app journey, it runs the same stimulus once through the production WASAPI
capture implementation and reports only content-free coupling measurements: start/outcome/error, duration,
level-event count, peak, average level RMS, and captured RMS. Samples remain in memory only for the measurement
and are discarded. The actual journey records only while F8 is held, never persists audio, uses an isolated
profile, and stores only a temporary phrase-match boolean and character count before cleanup. It refuses to run
while an unowned EnviousWispr or controlled-target process exists.

The remaining physical acceptance path is a separate guided mode. Exit any normally installed EnviousWispr
instance first, run the command below, keep the controlled target focused, and follow the fixed public instruction
shown in that window. The person must physically hold F8, speak the displayed sentence into the microphone, and
release F8. This mode does not synthesize key edges or play audio through the speakers:

```powershell
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet --manual-microphone
```

The guided mode can also validate an exact installed founder candidate. Supply a fully qualified
`EnviousWispr.App.exe` path; the harness never emits that path:

```powershell
$installedApp = Join-Path $env:LOCALAPPDATA `
  'EnviousLabs.EnviousWispr.Founder\current\EnviousWispr.App.exe'
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet --manual-microphone --app-executable $installedApp
```

Add `--live-preview` to the same command when the gitignored small preview model is installed. The guided mode
passes only when the real global hook starts and stops recording, production WASAPI captures the spoken phrase,
the real worker and deterministic pipeline complete, the known public word appears in the native edit target,
the app exits cleanly, and no owned worker remains. It retains the same content-free boolean, character-count,
stage, runtime, and cleanup evidence as the automated journey; it does not retain audio or transcript text.

The default path requires the gitignored `models/whisper-large-v3-turbo` pack; the English path requires
`models/parakeet-tdt-0.6b-v3`. The result is one content-free JSON object with
shell/worker/journey/target/cleanup booleans and typed Windows, architecture, engine, provider, model-pack,
fixture, delivery-target, ProductVersion, and executable SHA-256 fields. The temporary target observation records
only whether the known public
phrase appeared and the character count, then the harness deletes it with the isolated profile.

The default mode is real production pipeline proof, but not microphone or global-registration proof. The live
mode adds production WASAPI, the installed global hook, and an acoustic speaker-to-microphone path, but its key
edges and playback source are still synthetic. On the current webcam-microphone hardware, both the reviewed
fixture and Windows-synthesized sentence were detected by the content-free probe but failed their lexical gates;
speaker echo suppression is the likely boundary. The `--manual-microphone` mode makes the remaining requirement
directly runnable, but it is not evidence until a person completes it successfully on the exact candidate build.

## Synthetic hotkey: the installed global hook, without a sound

`--synthetic-hotkey` is the take that starts the way a finger starts it. The reviewed fixture is still the
microphone, so nothing plays through the speakers, but the harness never signals the START event: it resolves
the recording key the app is actually listening for, injects that key through `SendInput`, and the app's
installed `WH_KEYBOARD_LL` hook - the same single route every real press takes - starts and stops the take.

```powershell
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet --synthetic-hotkey
```

Every verdict is read from `app.jsonl` and the controlled target, never from the injector. The control has
both halves: `HotkeyReady` must be logged before anything is pressed; a two-second quiet window must show
`DictationRecordingStarted` ABSENT; the press must make it PRESENT within three seconds; and the observed
press-to-recording latency must fit comfortably inside the quiet window, or the run refuses to count the
absence. The result adds a content-free `syntheticHotkey` object: the virtual key, how long it was held, the
quiet window, the press-to-recording latency, and whether the target observed the phrase.

The recording key is resolved through the app's own parser and key map from the isolated profile, in three
states: no settings file resolves to the app's default (`F8`, `DictationPreferences.Default`); a gesture the
app accepts is the binding; anything else is refused. Chords and modifier taps are refused as a declared
boundary - their gesture completes on a 40 ms poll that an instant synthetic press can fall between - and
that refusal says so rather than reporting a flaky hotkey.

**Exit codes.** `0` is a pass. `2` is `JOURNEY EXPECTATION NOT MET`: the app did not do what the journey
required. `3` is `INSTRUMENT INVALID`: the harness could not stage or trust the run - a key it would not
guess, a payload that failed its own precondition, a control window that meant nothing - and **nothing in a
code-3 run is evidence about the product.** A runner must never average it into a pass rate. An unrecognised
argument is also code 3: the harness matches flags exactly, so a mistyped or joined flag would otherwise select
the default journey and report it green in place of the one requested - which is exactly what happened on
2026-09-05 when a wrapper passed `--english-parakeet,--synthetic-hotkey` as one token. Read the result's
`inputKind` against what you asked for; the harness now refuses before it can happen.

Running from a git worktree: the model packs are not in git, so bring them in as **hard links or real files,
never symlinks** - .NET reports `FileInfo.Length` of 0 for a symlink, so the Parakeet `Int8Complete` probe
correctly refuses a pack made of them ("the gitignored Parakeet quantized model is required"). Measured
2026-09-05: the same file read 0 bytes as a symlink and 652,183,999 as a hard link.

This mode cannot be combined with live or manual microphone, Live Preview, the head start, Escape Recovery, or
failure injection, each of which owns the trigger or the hold differently. It refuses to run while an unowned
EnviousWispr instance exists, because a second low-level hook would also receive the injected key and start a
real take. It proves the installed hook and the real key path; it still does not prove the microphone.
