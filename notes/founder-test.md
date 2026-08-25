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

## Restart / stop

- **Quit (normal path):** tray icon (notification area, green dot on dark pill) →
  right-click → **Quit EnviousWispr**. This disposes the pipeline and kills the EG-1
  llama-server child (no orphans).
- Tray menu also has **Start with Windows** (checkbox; on by default since 2026-08-25 —
  it wrote `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\EnviousWispr` pointing at
  the exe) and a live status line mirroring the pill.
- From a shell, exact PID only, never by name:

```
tasklist | findstr EnviousWispr      :: find the PID
taskkill /PID <pid> /F               :: exact PID only (add /T to take the llama-server child)
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
Note: that knob was a NO-OP until 2026-08-25 (the engine hardcoded the int8 encoder) —
it is real now and was verified on this box (fp32 CPU: 223 ms / 10 s clip).

## Known limitations (v1)

- **No streaming**: transcribe happens after you release F9 (batch leg, like the Mac's v1 design).
- **CPU-only EG-1** right now (keeps the 4090 clear for the Qwen control plane). GPU swap =
  change `eg1.serverExe` to `C:\AI\llama-cuda-b10615\bin\llama-server.exe` and add
  `--gpu-layers all` support (not yet plumbed) — expect ~10x faster polish.
- **Push-to-talk = F9 hold**. Tap-to-toggle not implemented.
- Text insertion = clipboard + Ctrl+V (your previous clipboard content is restored after).
- Mic = default input device, 16 kHz mono (WASAPI shared mode).
- Quiet-clip guard: <100 ms of audio is discarded (no accidental blanks).

## EG-1 build A/B — answered with data (2026-08-25, MEASURED)

You asked which of the three local Jul 16 Q5_K_M builds to run. `EnviousWispr.Smoke.exe --ab`
ran all three on identical ASR transcripts (clip10/20/94 + activation probe), CPU server:

| Build | Probe | clip10 10s | clip20 20s | clip94 91.5s |
|---|---|---|---|---|
| v3-en | GREEN 1035 ms ("So move the meeting to Friday.") | 1662 ms | 4133 ms | 13738 ms |
| v4-twins | GREEN 978 ms | 1664 ms | 4146 ms | 13988 ms |
| v5 (running) | GREEN 991 ms | 1663 ms | 4156 ms | 14038 ms |

**Verdict: on this corpus they are effectively equivalent.** clip10/clip20 outputs are
byte-identical across all three (same comma insertion, same filler repair
"They don't they don't they're" → "They're not concerned"); clip94 differs only in
stylistic micro-splits (comma/"like" placement). Latency deltas <1%. v3's probe prefixes
"So" (cosmetic). **Keeping v5 (newest); switching is not justified by this data.**

Remaining quality question: v5 (local Q5_K_M) vs the Mac's "eg-1-v2" (8 shards, different
quantization, models.enviouslabs.co). That is your ears on real dictation — if polish feels
off vs the Mac, say so and we compare against the Mac build, not the other local ones.

## What's NOT built yet

- Runtime-leg tests: the ASR engine now has local-only runtime tests (37 total —
  `dotnet test src/EnviousWispr.Tests`; CI runs the 32-test contract subset). The live
  mic→paste path is still verified by the smoke exe + your own dictation
- Long dictation on the CPU tier: a 91.5 s clip → ~5.0 s (measured). Silence-gap
  chunking was measured and rejected — per-chunk overhead cancels the attention
  savings (notes/app-build.md, 2026-08-25). The fixes are the GPU tier (plumbed,
  `asr.provider: "cuda"` + `asr.pack: "fp32"`) or a fused-decoder model export (post-v1)
- ASR GPU tier: plumbed (CUDA EP ships with the app, `asr.provider: "cuda"` +
  `asr.pack: "fp32"`), but not yet validated on this box — the 4090 is occupied by the
  Qwen control plane. Expect ~10x faster ASR than CPU once validated (S1: 10 s clip →
  0.119 s)
- Official EG-1 distribution story (currently: local weights on this box)
