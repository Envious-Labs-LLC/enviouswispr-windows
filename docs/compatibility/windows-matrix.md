# Windows compatibility matrix

## Evidence rule

This matrix records completed product evidence, not assumptions from a compile. Run
`scripts/run-compatibility.ps1` on every machine class and keep the generated JSON under ignored `out/`.
Post the content-free result summary to GitHub issue #44. Never commit machine paths, device names or IDs,
account names, audio, transcripts, clipboard contents, or surrounding text.

As of 2026-08-26, Microsoft lists Windows 11 x64 builds 26100 (24H2), 26200 (25H2), and 28000 (26H1)
as supported client versions. The release candidate must be checked against Microsoft's current lifecycle
page again because supported versions change over time.

## Measured cells

| Cell | Machine class | Product evidence | Result |
| --- | --- | --- | --- |
| Primary NVIDIA desktop | Windows 11 25H2 build 26200.9168; Intel, 24 physical/32 logical cores; 32–63 GiB; NVIDIA with one CUDA device; QHD single display at 100–124%; one default capture device; one endpoint-security provider | 34 preserved-proof tests; 350 production tests; physical WASAPI default/selected capture; global hotkey conflict, hold/release, cancel, and teardown; isolated worker start/crash recovery/stop/timeout; Parakeet CPU model acceptance | Partial pass |

The machine's NVIDIA driver is healthy, but the pinned ONNX Runtime 1.29 CUDA 13/cuDNN 9 dependency set is
not installed or delivered. Automatic Parakeet selection now detects that condition and chooses the tested
CPU fallback. Explicit CUDA acceptance remains failed and is tracked in issue #45. This is not a completed
NVIDIA product cell until a licensed, signed, hash-pinned runtime pack is delivered and retested.

## Required operating-system cells

| OS cell | Required evidence |
| --- | --- |
| Windows 11 24H2, build 26100 | Clean signed install/update/uninstall plus the complete compatibility runner on a currently patched Home/Pro machine; Enterprise remains relevant while Microsoft supports it. |
| Windows 11 25H2, build 26200 | Complete compatibility runner and signed lifecycle on a current patch. The primary rig covers only the high-end NVIDIA/single-display subset. |
| Windows 11 26H1, build 28000 | Clean compatibility runner and signed lifecycle; do not infer results from 24H2/25H2. |

## Required hardware and workflow coverage

| Dimension | Cells that need measured runs |
| --- | --- |
| CPU and memory | Intel and AMD CPU-only laptops at 8 GiB and 16 GiB; older supported 4-core and 8-core classes; 32 GiB desktop baseline. |
| Graphics | NVIDIA with complete CUDA runtime; NVIDIA without runtime (CPU fallback); AMD discrete; Intel integrated; no usable accelerator. |
| Microphone | Built-in array, USB microphone/headset, Bluetooth headset, default-device change, unplug during capture, privacy permission denied. |
| Displays | 100%, 125%, 150%, and 200%; 1080p, QHD, and 4K; mixed-scale two-monitor placement and movement. |
| Endpoint security | Microsoft Defender default policy plus at least two representative third-party products; allow, quarantine/block, repair, update, and uninstall behavior. |
| Target apps | Notepad, Word, Outlook, Chrome contenteditable, Slack, Teams, VS Code, protected password fields, elevated targets, and clipboard-unavailable fallback. |
| Power and mobility | AC and battery use, sleep/resume, microphone change, lid/monitor topology change, memory pressure, and thermal throttling. |
| Release lifecycle | Signed fresh install, update, interrupted update, rollback, repair, and uninstall with user-data preservation for stable/founder/beta isolation. |

## Runbook

On a prepared Windows 11 x64 machine:

```powershell
pwsh -NoProfile -File .\scripts\run-compatibility.ps1 -RunLabel <coarse-machine-label>
```

When the local signed model/runtime packs and public fixtures are available, also run:

```powershell
pwsh -NoProfile -File .\scripts\run-compatibility.ps1 `
  -RunLabel <coarse-machine-label>-runtime `
  -IncludeLocalRuntime
```

The runner intentionally continues after a failed step so the report captures all safe evidence from the
machine. A nonzero final exit means the cell failed. Native target-app delivery, multi-monitor movement,
endpoint-security allow/block behavior, and the signed release lifecycle remain interactive checks and must
be added to the issue with exact app/version and observed behavior.

## Requirement status

No public minimum or recommended hardware requirement is approved yet. One high-end desktop cannot prove an
ordinary laptop minimum. The only current product-level requirement is Windows 11 x64; CPU execution remains
mandatory. Publish numeric CPU, memory, GPU, and latency requirements only after the required low-end,
midrange, and high-end cells above have real results.

## Primary sources

- [Microsoft supported Windows client versions](https://learn.microsoft.com/en-us/windows/release-health/supported-versions-windows-client)
- [Microsoft Windows 11 Home and Pro lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/windows-11-home-and-pro)
- [ONNX Runtime CUDA execution-provider requirements](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html)
