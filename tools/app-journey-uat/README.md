# Production WinUI journey UAT

This harness proves the production shell-to-worker-to-delivery path with controlled public input. It launches
the real WinUI app with an isolated profile, requires exactly one owned final-ASR worker, focuses the native
WinForms delivery target, and signals a strictly allowlisted in-app journey. The app accepts only the three
reviewed `tools/whisper-uat/fixtures` WAV files by SHA-256.

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

The gitignored `models/whisper-large-v3-turbo` pack is required. The result is one content-free JSON object with
shell/worker/journey/target/cleanup booleans and typed Windows, architecture, engine, provider, model-pack,
fixture, and delivery-target fields. The temporary target observation records only whether the known public
phrase appeared and the character count, then the harness deletes it with the isolated profile.

This is real production pipeline proof, but not microphone or global-registration proof. Before Phase 23 can
close, a person must hold the configured global key from a non-EnviousWispr target, speak through a physical
microphone, release, observe insertion, and record only content-free pass/fail evidence for that exact build.
