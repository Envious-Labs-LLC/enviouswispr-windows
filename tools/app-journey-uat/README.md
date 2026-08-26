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

Add `--live-preview` to the same command when the gitignored small preview model is installed. The guided mode
passes only when the real global hook starts and stops recording, production WASAPI captures the spoken phrase,
the real worker and deterministic pipeline complete, the known public word appears in the native edit target,
the app exits cleanly, and no owned worker remains. It retains the same content-free boolean, character-count,
stage, runtime, and cleanup evidence as the automated journey; it does not retain audio or transcript text.

The default path requires the gitignored `models/whisper-large-v3-turbo` pack; the English path requires
`models/parakeet-tdt-0.6b-v3`. The result is one content-free JSON object with
shell/worker/journey/target/cleanup booleans and typed Windows, architecture, engine, provider, model-pack,
fixture, and delivery-target fields. The temporary target observation records only whether the known public
phrase appeared and the character count, then the harness deletes it with the isolated profile.

The default mode is real production pipeline proof, but not microphone or global-registration proof. The live
mode adds production WASAPI, the installed global hook, and an acoustic speaker-to-microphone path, but its key
edges and playback source are still synthetic. On the current webcam-microphone hardware, both the reviewed
fixture and Windows-synthesized sentence were detected by the content-free probe but failed their lexical gates;
speaker echo suppression is the likely boundary. The `--manual-microphone` mode makes the remaining requirement
directly runnable, but it is not evidence until a person completes it successfully on the exact candidate build.
