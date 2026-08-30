# Phase Zero production architecture decisions

These decisions close the Phase 0E prerequisites in the Windows master plan. The contracts under
`.claude/knowledge/` remain authoritative; this file records the concrete production choices that
Phase 1 implements.

## ADR 1: current .NET LTS and WinUI 3

**Decision:** Production projects target .NET 10 and WinUI 3 through the stable Windows App SDK. The
founder-tested WPF and .NET 8 proof remains buildable beside them until replacement evidence exists.

**Reason:** The architecture contract requires the current .NET LTS and WinUI 3. One C# toolchain owns
the UI, Windows integration, orchestration, and model adapter boundaries without an FFI seam on the
dictation path.

## ADR 2: direct, self-contained x64 application

**Decision:** The production app begins as an unpackaged, self-contained x64 executable. Velopack and
signed direct distribution remain the primary release path. MSIX and Store packaging are secondary and
must not become prerequisites for offline models, global input, tray behavior, BYOK, or Ollama.

**Reason:** This matches the distribution contract and lets a clean customer machine run without a
separate .NET or Windows App Runtime installation.

## ADR 3: inward module dependencies

**Decision:** `Core` owns shared value types and contracts. `Audio`, `ASR`, `PostProcessing`, `LLM`,
`Pipeline`, `Services`, and `ModelDelivery` depend directly on `Core`, not on each other. `App` is the
composition root and may reference all modules. Architecture tests lock this graph.

**Reason:** Runtime, UI, storage, and network details must not leak into the deterministic heart. Later
cross-module behavior is composed through Core contracts rather than concrete outward references.

## ADR 4: runtime isolation and fallback

**Decision:** Model engines sit behind narrow C# contracts. Crash-prone or independently distributed
native runtimes may run out of process under app-owned lifetime control. Every accelerator choice follows
a real capability probe, CPU remains mandatory, and a provider failure preserves captured audio and the
last valid text whenever possible.

**Reason:** This preserves the measured proof while meeting the reliability and heterogeneous-hardware
contracts.

## ADR 5: versioned user data outside the install directory

**Decision:** Reusable, non-secret state lives under the user's local application-data directory using
versioned schemas and atomic replacement. Provider keys use Windows Credential Manager. Logs accept only
typed, content-free events; they have no transcript, clipboard, surrounding-text, or arbitrary-message
field.

**Reason:** Updates and uninstall must not corrupt user data, and the privacy boundary must be difficult
to violate accidentally.

## ADR 6: capability-by-capability migration

**Decision:** Production work follows a strangler migration. Each capability moves from the WPF proof
only after its production replacement has contract tests and the relevant native Windows evidence. The
proof is removed only after the final replacement path is observed and accepted.

**Reason:** A rewrite would discard founder-tested evidence and make regressions hard to distinguish from
architecture work.
