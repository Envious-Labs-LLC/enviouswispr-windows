# Open questions — needs a founder decision

Consolidated 2026-08-24 from `Windows Port Research.md`,
`windows-native-stack.md`, and `load-bearing-constraints.md`. A question left
scattered is a question that gets asked twice.

## Resolved by this session (MEASURED 2026-08-24)

- **Host GPU for S1: NVIDIA GeForce RTX 4090, 24 GB VRAM** (`nvidia-smi` on
  the WSL host). The DirectML/CUDA tier of spike S1 is answerable on this
  machine; only the NPU tier needs a Copilot+ machine.
- **2026-08-24 (evening) — the rig moved to native Windows.** Windows 11 build
  26200, i9-14900KF (32 logical), 64 GB, RTX 4090 24 GB, **no NPU** (MEASURED —
  device enumeration + CPU spec; see `toolchain.md`). The "Windows host vs WSL"
  distinction is retired: S1, S2 and the EG-1 contract spike all run on this
  rig. This also resolves founder question 3 below.

## Awaiting the founder

1. **Stack: C# / .NET 8 + WPF** (vs WinUI 3 — aesthetic call, not capability).
   Current recommendation, unchanged (see the Recommendation section of
   `language-options.md`). The Swift compile experiment that was to bear on it
   never completed — abandoned at the move to Windows with no result (see
   `toolchain.md`).
2. **Windows v1 ships without live transcription** (streaming is a limb;
   sherpa-onnx has no true streaming for Parakeet v3).
3. ~~**Windows host access pattern for spikes** (RDP/console?)~~ — **RESOLVED
   2026-08-24 (evening):** the rig itself is now native Windows; spikes run
   locally.
4. **NPU in the v1 promise or not** — needs a Copilot+ test machine; without
   one the public claim is scoped to dGPU/CPU tiers until measured. (The rig
   has NO NPU — i9-14900KF, MEASURED 2026-08-24 evening — so a separate
   Copilot+ machine is the only path for the NPU tier.)
5. **NEW — clipboard contract scope** (`load-bearing-constraints.md` T2.2):
   the Win32 clipboard holds one item (many formats); macOS pasteboards hold
   many items. `ClipboardSnapshot`'s "preserve every item and type" degrades
   to "every format of the current item" on Windows. Accept in v1 (the
   common text case is lossless) or invest in OLE multi-item emulation
   (edge-case work)?
6. **NEW — model residency default**: proposal is resident-by-default, no
   unload policy in v1 (there is no Windows equivalent of launchd's idle
   reaping, so the Mac's measured 459 ms–12.8 s respawn tail simply does not
   exist). Confirm or push back.

## Parked (not blocking the map)

- Whisper large-v3-turbo packaging for the Phase-2 multilingual engine
  (MIT GGML/ONNX; pattern established by every competitor).
- EG-1 off-Metal quality contract (93.7 % bar) — runnable on this rig
  (CPU llama-server + Python eval harness), scheduled after the map.
