# EnviousWispr — Windows Edition

Windows-native voice-to-text: record, transcribe, polish, paste into the focused app.
Sister project to macOS [EnviousWispr](https://github.com/saurabhav88/EnviousWispr) and to
[enviouswispr-mobile](https://github.com/saurabhav88/enviouswispr-mobile).

**Status: research. No code yet, and no decision yet on what to write it in.**

## What this repo is for

The macOS app is 308,321 lines of Swift across 17 modules, shipping to real users. This repo works out
what a Windows version costs, what carries over, and what has to be rebuilt — and then becomes the place
it gets built.

The first deliverable is a map, not code: which parts port as-is, which need a Windows counterpart, and
which are a rewrite. See [`AGENTS.md`](AGENTS.md).

## Who works here

Primarily **Qwen3.8-27B** running locally on the Envious Labs rig, driven by the `pi` agent. Its brief,
its evidence rules, and its notes discipline are in [`AGENTS.md`](AGENTS.md), which it reads automatically.

That machine is Linux, so it can cross-compile and unit-test portable logic but cannot exercise real
Windows audio, tray, clipboard or UI Automation. Claims are labelled `MEASURED`, `READ` or `ASSUMED`
accordingly, and that labelling is load-bearing rather than decoration.

## Reference material lives on the rig, not in this repo

A verbatim snapshot of the macOS source at commit `f9b70283` (2026-08-24), plus the internal engineering
knowledge, sits at `/home/saura/agent-workspace/enviouswispr-windows/` on the rig. It is deliberately NOT
committed here: it belongs to the macOS repo, it is 175 MB, and a copy in two places drifts.

Anything learned FROM that snapshot belongs here, in `notes/`, with the source path cited.

## Layout

```
AGENTS.md     the brief: objective, evidence rules, first deliverables
notes/        findings, one file per topic, terse, every claim labelled
```

Source directories arrive when there is something to put in them.
