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
to disk.

```powershell
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --live-microphone
dotnet run --no-build --project .\tools\app-journey-uat\EnviousWispr.AppJourney.Uat.csproj `
  -c Release -- --english-parakeet --live-microphone
```

This mode is audible. It records only while F8 is held, never persists audio, uses an isolated profile, and
stores only a temporary phrase-match boolean and character count before cleanup. It refuses to run while an
unowned EnviousWispr or controlled-target process exists.

The default path requires the gitignored `models/whisper-large-v3-turbo` pack; the English path requires
`models/parakeet-tdt-0.6b-v3`. The result is one content-free JSON object with
shell/worker/journey/target/cleanup booleans and typed Windows, architecture, engine, provider, model-pack,
fixture, and delivery-target fields. The temporary target observation records only whether the known public
phrase appeared and the character count, then the harness deletes it with the isolated profile.

The default mode is real production pipeline proof, but not microphone or global-registration proof. The live
mode adds production WASAPI, the installed global hook, and an acoustic speaker-to-microphone path, but its key
edges and reviewed-fixture playback source are still synthetic. Before Phase 23 can close, a person must hold
the configured global key from a non-EnviousWispr target, speak through a physical microphone, release, observe
insertion, and record only content-free pass/fail evidence for that exact build.
