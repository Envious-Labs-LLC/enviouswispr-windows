# EnviousWispr — Windows Edition

Windows-native voice-to-text: hold **F8**, speak, release — polished text is typed into your
focused app. Sister project to macOS [EnviousWispr](https://github.com/saurabhav88/EnviousWispr).

**Status: founder test build ready.** GPU transcription, GPU polish, the visible overlay,
tray controls, and startup behavior are verified on the Envious Labs rig (2026-08-25).
The final physical F8 and paste check is intentionally left for the founder on the unlocked PC.

Maintained by [Envious Labs](https://github.com/Envious-Labs-LLC).

## What it does

- Push-to-talk on **F8** (changeable in config). A small pill shows recording state.
- 16 kHz mono capture → **Parakeet TDT 0.6B v3** (ONNX Runtime, C#) → raw transcript.
- **EG-1** (a 2B fine-tuned Qwen, GGUF) polishes the transcript via a llama-server the app
  **launches itself** on an ephemeral loopback port with a random API key. The model and the
  server are torn down on quit.
- The polished text is pasted into whatever app had focus.
- Resident in the system tray (right-click: live status, how-to help, "Start with Windows", Quit).
  Autostarts on login by default.

## Measured performance (this rig: i9-14900KF, RTX 4090)

| Stage | GPU test tier | Notes |
|---|---|---|
| ASR, 10 s clip | **346 ms** | CUDA fp32, warm run |
| ASR, 20 s clip | **183 ms** | CUDA fp32, warm run |
| ASR, 91.5 s clip | **485 ms** | CUDA fp32, warm run |
| EG-1 probe | **72 ms** | CUDA llama-server, all layers on GPU |
| Full ASR + polish, 10 s clip | **332 ms** | model pipeline smoke; excludes live capture and paste |

## Layout

```
src/EnviousWispr/         the WPF app (capture, ASR, polish, paste, tray, overlay)
src/EnviousWispr.Tests/   xUnit contract suite + local-only runtime ASR tests
src/EnviousWispr.Smoke/   end-to-end smoke harness (ASR + EG-1 + A/B mode)
spikes/s1/                the S1 latency spike (measurement, verdicts in notes/)
spikes/web-rtc-vad/       VAD exploration (not in the v1 capture path)
notes/                    findings — one file per topic, every claim labelled
                          MEASURED / READ / ASSUMED
models/                   ASR model packs (gitignored — see notes/founder-test.md)
tools/llama.cpp/          local llama.cpp build (gitignored)
tools/synth-test/         strict interactive overlay and paste test harness
```

## Building and running

```powershell
dotnet build src/EnviousWispr.Smoke/EnviousWispr.Smoke.csproj -c Release
src\EnviousWispr\bin\Release\net8.0-windows\EnviousWispr.exe
dotnet run --project src/EnviousWispr.Smoke -c Release
dotnet test src/EnviousWispr.Tests/EnviousWispr.Tests.csproj -c Release
```

Model packs (ASR ~670 MB int8 / ~2.5 GB fp32) and the EG-1 GGUF are **not in git** — see
[`notes/founder-test.md`](notes/founder-test.md) for exact paths and the config reference
(`src/EnviousWispr/appsettings.json`).

## Status

| Area | State |
|---|---|
| GPU ASR + EG-1 model pipeline | ✅ measured smoke pass on the RTX 4090 |
| Visible overlay, tray, startup, single-instance behavior | ✅ verified in interactive Windows session 1 |
| Physical F8, live mic, paste into founder-selected apps | 🧪 ready for founder test on the unlocked PC |
| Clipboard-safe delivery fallback | ✅ keeps text on clipboard when automatic paste is blocked |
| CPU fallback | ✅ app falls back automatically if CUDA cannot load |
| Contract tests plus runtime ASR and native-input tests | ✅ 39/39 locally, CI on every push |
| EG-1 distribution story | ⏳ open (founder's call) |
| Streaming ASR / fused-decoder export | post-v1 (S1 verdicts) |

## Who works here

The first implementation was built by **Qwen3.8-27B** on the Envious Labs rig and was then
hardened for founder testing. The repo's evidence rules and Windows constraints live in
[`AGENTS.md`](AGENTS.md).

That machine is native Windows (Windows 11, i9-14900KF, 64 GB, RTX 4090 24 GB — MEASURED
2026-08-24), so it can exercise real Windows audio, tray, clipboard and UI Automation and
build the C#/.NET stack; it cannot run the macOS app or Apple silicon. Claims are labelled
`MEASURED`, `READ` or `ASSUMED` accordingly, and that labelling is load-bearing rather than
decoration. (The rig moved from Linux/WSL to native Windows on 2026-08-24; Linux-era rig
facts in the notes are superseded history.)

## Reference material lives on the rig, not in this repo

A verbatim snapshot of the macOS source at commit `f9b70283` (2026-08-24), plus the internal engineering
knowledge, sits at `C:\Users\saura\agent-workspace\enviouswispr-windows\` on the rig. It is deliberately NOT
committed here: it belongs to the macOS repo, it is 175 MB, and a copy in two places drifts.

Anything learned FROM that snapshot belongs here, in `notes/`, with the source path cited.

## How this got here

The repo started as research: the macOS app is 139,085 lines of Swift across 17 modules with a
164,289-line test suite, and the first deliverable was a port map, not code. That map and the
S1 latency spike (which settled the CPU-vs-GPU tiering and the thread-pinning requirement)
became the design the app was built on. The full evidence trail is in `notes/`.
