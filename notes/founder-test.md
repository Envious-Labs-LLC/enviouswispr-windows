# Founder test — EnviousWispr for Windows (2026-08-25)

Status: ASR leg + EG-1 polish leg both verified end-to-end (MEASURED, smoke PASS).
The app is RUNNING on this machine right now (PID 38816, overlay pill top-right, F9 hotkey live).

## What to do

1. **It's already running.** Click into any text field (Notes, VS Code, browser).
2. **Hold F9** (or press-and-hold — push-to-talk), speak, release.
   - Pill states: `recording` (red) → `transcribing…` (yellow) → `polishing…` → `done` (green, brief).
3. Expect the polished text to appear where the cursor was (~2-4 s after release for a 10 s clip:
   ASR ~0.4 s + EG-1 polish ~1.5-2 s on CPU).
4. If it goes silent → check `enviouswispr.log` next to the exe (see paths below) and send it over.

## Restart / stop (only these PIDs, never by name)

```
tasklist | findstr EnviousWispr      :: find the PID
taskkill /PID <pid> /F               :: exact PID only
:: relaunch:
src\EnviousWispr\bin\Debug\net8.0-windows\EnviousWispr.exe
```

## Paths

- App: `C:\Users\saura\agent-workspace\enviouswispr-windows\src\EnviousWispr\bin\Debug\net8.0-windows\`
- Log: `enviouswispr.log` in that dir
- ASR model: `models\parakeet-tdt-0.6b-v3` (int8, 652 MB encoder)
- EG-1 model: `C:\Users\saura\eg1-v5-Q5_K_M.gguf` (Jul 16 build — see "open question" below)
- Config: `appsettings.json` in the app dir (hotkey, pack int8/fp32, threads)

## Verified numbers (this rig, CPU)

| Stage | 10 s clip | 91.5 s clip |
|---|---|---|
| ASR (int8) | 214-413 ms | 4.8-4.9 s |
| EG-1 boot (cold) | 2.1 s | — |
| EG-1 probe | 1.07 s | — |
| Polish | 1.8 s | (scales ~0.2 ms/token) |

Full corpus (453 clips): int8 median 274 ms / p95 2.2 s; fp32 median 326 ms / p95 2.1 s.
**int8 emits empty text on ~4% of clips; fp32 on <0.5%.** If you hear blanks, flip
`asr.pack` to `fp32` in appsettings and restart (slower median, fewer blanks).

## Known limitations (v1)

- **No streaming**: transcribe happens after you release F9 (batch leg, like the Mac's v1 design).
- **CPU-only EG-1** right now (keeps the 4090 clear for the Qwen control plane). GPU swap =
  change `eg1.serverExe` to `C:\AI\llama-cuda-b10615\bin\llama-server.exe` and add
  `--gpu-layers all` support (not yet plumbed) — expect ~10x faster polish.
- **Push-to-talk = F9 hold**. Tap-to-toggle not implemented.
- Text insertion = clipboard + Ctrl+V (your previous clipboard content is restored after).
- Mic = default input device, 16 kHz mono (WASAPI shared mode).
- Quiet-clip guard: <100 ms of audio is discarded (no accidental blanks).

## Open question for you

Mac ships "eg-1-v2" (8 shards, ~3.2 GB, from models.enviouslabs.co). This box has three
Q5_K_M builds from the Jul 16 training runs: `eg1-v3-en`, `eg1-v4-twins`, `eg1-v5`
(all byte-identical size 2,889,511,680). I'm running **eg1-v5** (newest by mtime).
The prompt contract is byte-verified identical. If polish quality feels off vs the Mac,
say so — we can A/B the three builds (probe is green on v5; output quality is your call).

## What's NOT built yet

- Auto-start with Windows / tray menu (app exits when its console host dies — fine for now)
- xUnit test suite (smoke exe stands in for it)
- GPU tier for ASR (CUDA fp32 encoder: RTFx 84-145 measured in S1, not plumbed)
- Official EG-1 distribution story (currently: local weights on this box)
