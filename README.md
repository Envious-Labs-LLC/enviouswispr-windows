# EnviousWispr — Windows Edition

Windows-native voice-to-text: hold **F9**, speak, release — polished text is typed into your
focused app. Sister project to macOS [EnviousWispr](https://github.com/saurabhav88/EnviousWispr).

**Status: working app.** Push-to-talk dictation with local ASR + local LLM polish, verified
end-to-end on the Envious Labs rig (2026-08-25). GPU tiers are plumbed but awaiting
validation (see [status](#status)).

Maintained by [Envious Labs](https://github.com/Envious-Labs-LLC).

## What it does

- Push-to-talk on **F9** (changeable in config). A small pill shows recording state.
- 16 kHz mono capture → **Parakeet TDT 0.6B v3** (ONNX Runtime, C#) → raw transcript.
- **EG-1** (a 2B fine-tuned Qwen, GGUF) polishes the transcript via a llama-server the app
  **launches itself** on an ephemeral loopback port with a random API key. The model and the
  server are torn down on quit.
- The polished text is pasted into whatever app had focus.
- Resident in the system tray (right-click: live status, "Start with Windows", Quit).
  Autostarts on login by default.

## Measured performance (this rig: i9-14900KF, RTX 4090)

| Stage | CPU (default tier) | Notes |
|---|---|---|
| ASR, 10 s clip | **215 ms** | int8 pack, intra_op=8 (thread pinning is mandatory on hybrid cores — S1) |
| ASR, 91.5 s clip | ~5.0 s | long-clip floor is the per-frame decode loop; see [chunking](notes/app-build.md) |
| EG-1 polish, 10 s clip | ~1.8 s | CPU llama-server, ~21 tok/s |
| Full pipeline, 10 s clip | **~2.2 s** stop → text lands | |
| ASR on GPU (plumbed, unvalidated) | — | S1 measured CUDA fp32: **0.119 s** per 10 s clip, 95 s → 0.654 s |

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
```

## Building and running

```powershell
dotnet build src/EnviousWispr.Smoke/EnviousWispr.Smoke.csproj   # builds app + smoke
src\EnviousWispr\bin\Debug\net8.0-windows\EnviousWispr.exe      # the app
src\EnviousWispr.Smoke\bin\Debug\net8.0-windows\EnviousWispr.Smoke.exe   # E2E smoke
dotnet test src/EnviousWispr.Tests/EnviousWispr.Tests.csproj    # 37 tests locally
```

Model packs (ASR ~670 MB int8 / ~2.5 GB fp32) and the EG-1 GGUF are **not in git** — see
[`notes/founder-test.md`](notes/founder-test.md) for exact paths and the config reference
(`src/EnviousWispr/appsettings.json`).

## Status

| Area | State |
|---|---|
| Push-to-talk dictation, ASR + polish + paste | ✅ verified end-to-end (SMOKE PASS) |
| Tray / autostart / quit (tears down its llama-server) | ✅ in the app |
| CPU ASR tier (int8 default, fp32 selectable) | ✅ both packs verified |
| GPU ASR tier (`asr.provider: "cuda"` + `asr.pack: "fp32"`) | 🔌 plumbed, runtime-unvalidated (4090 occupied by the control plane) |
| GPU EG-1 polish (`eg1.gpuLayers`) | 🔌 plumbed, runtime-unvalidated |
| Contract tests (prompt bytes, probe, polish strip, vocab) + runtime ASR tests | ✅ 37/37 locally, CI on every push |
| EG-1 distribution story | ⏳ open (founder's call) |
| Streaming ASR / fused-decoder export | post-v1 (S1 verdicts) |

## Who works here

Primarily **Qwen3.8-27B** running locally on the Envious Labs rig, driven by the `pi` agent. Its
brief, its evidence rules, and its notes discipline are in [`AGENTS.md`](AGENTS.md), which it reads
automatically.

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
