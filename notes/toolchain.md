# Toolchain — what the rig has, installed for this project

## 2026-08-24 — Swift for Linux (for the portability spike)

**Why:** `BRIEF-map.md` deliverable — stop calling files "portable" without
compiling them. `EnviousWisprCore` and `EnviousWisprPostProcessing` get built
for Linux; success or the failure list both become MEASURED.

**What is installed (MEASURED — ran the commands, read the output):**

- **Swift 6.3.3 RELEASE** (swift.org official `ubuntu24.04` x86_64 tarball,
  1,069,589,818 bytes) at
  `/home/saura/tools/swift-6.3.3-RELEASE-ubuntu24.04/`.
  Version chosen deliberately: the snapshot's `Package.swift` comment pins the
  project to "Swift 6.3.3 toolchain" (swift-syntax 603.0.2), so the spike
  compiles against the same Swift the product builds with — `READ`
  `macos-source/Package.swift`.
  - `swift --version` output: *(filled in after install completes)*
- **apt packages** (Ubuntu 24.04.4, passwordless sudo): `binutils-gold
  git gnupg2 libc6-dev libcurl4-openssl-dev libedit2 libicu-dev
  libncurses-dev libpython3-dev libsqlite3-dev libxml2-dev libz3-dev
  pkg-config tzdata unzip zlib1g-dev`. Already present: `g++-13` /
  `libstdc++-13-dev` (the `libstdc++-14-dev` in swift.org's docs is the
  22.04-era name; 24.04's default compiler is 13).
- Pre-existing dpkg interrupt from a prior session fixed with
  `sudo dpkg --configure -a` + `apt-get install -f` before installing.

**Usage:**
```
export PATH=/home/saura/tools/swift-6.3.3-RELEASE-ubuntu24.04/usr/bin:$PATH
```

**Spike layout:** `/home/saura/swift-spike/{core,postprocessing}` —
byte-identical copies of the snapshot modules (verified with `diff -r`) plus a
minimal `Package.swift` each (real package's macOS platform line dropped —
SwiftPM requires one platform and Linux ignores it). Nothing in
`macos-source/` is modified.

## Rig facts observed this session (differs from the machine notes)

- `nproc` = **32** (CLAUDE.md says 24), `free -h` = **31 GiB** (says 64),
  disk free **741 GB** on `/dev/sdd`, GPU **RTX 4090 24 GB** (unchanged),
  Ubuntu **24.04.4** LTS. MEASURED 2026-08-24. Possibly a WSL RAM cap or a
  hardware change; noted so a future session does not chase a phantom.

## 2026-08-24 — C# LSP (`lsp_diagnostics`) on the Windows rig

Re-test of the toolcheck: `csharp-ls` (razzmatazz/csharp-language-server) was
installed on PATH but `lsp_diagnostics` returned **zero** diagnostics for a
deliberately broken file. Repaired with three steps, each MEASURED:

1. **Route config was missing.** pi-lsp (`@narumitw/pi-lsp`) resolves servers
   from `~/.pi/agent/pi-lsp.json`; its built-in `csharp` default expects the
   binary `roslyn-language-server`, which is not what `dotnet tool` installs.
   Without config the tool reports `Skipped unavailable default LSP server(s):
   … csharp …` and the name `csharp-ls` is not even a known server.
   Fix: user-level config mapping the installed binary to `.cs`/`.csx`.
2. **The file needs a project context.** csharp-ls only compiles documents
   that belong to a loaded solution/project; a loose `.cs` yields an empty
   report, never an error. Fix: minimal `lspcheck.csproj` + `lspcheck.sln`
   in `C:\rig-toolcheck` including `Broken.cs` (`EnableDefaultCompileItems`
   false, explicit `<Compile Include="Broken.cs"/>`).
3. **Version race — the real defect.** Installed version was **0.15.0**. Its
   `Diagnostic.handle` returns an *empty* report immediately when the document
   is not yet in the solution (READ, 0.15.0 `Handlers/Diagnostic.fs`:
   `GetDocument → None → emptyReport`). The solution loads in the background
   ~100 ms after `initialized` (~2 s warm on this box, MEASURED), while pi-lsp
   is a **spawn-per-call** client that pulls diagnostics seconds after spawn —
   so every tool call races the load and loses. 0.15.0 also never loads a
   solution at all if the client does not answer `client/registerCapability`
   and `workspace/configuration` (the load request is posted *after* those
   awaits in `handleInitialized`; READ 0.15.0 `Handlers/Initialization.fs`).
   pi-lsp answers both, so that path is fine — but the empty-pull race is not
   fixable in config. Later versions fix it: 0.23.0 "solution load on-demand"
   makes the pull handler load the workspace folder first (READ, main
   `Handlers/Diagnostic.fs`: `context.LoadWorkspaceFolder`).

   **Proof the server itself works (MEASURED, csharp-ls 0.15.0):** a manual
   LSP handshake script that answers all server→client requests, waits for
   "finished loading solution", then pulls, returns for `Broken.cs`:
   `CS0029 error: Cannot implicitly convert type 'string' to 'int'` and
   `CS0103 error: The name 'y' does not exist in the current context`.

**Upgrading:** `dotnet tool update/install -g csharp-ls --version 0.22+`
FAILS with only the .NET 8 SDK: "Settings file 'DotnetToolSettings.xml' was
not found" — the package *does* contain `tools/net10.0/any/DotnetToolSettings.xml`
(MEASURED, unzipped the nupkg); the .NET 8 SDK's tool installer rejects tool
payloads targeting a newer TFM. Fix: install the .NET 10 SDK side-by-side
(runtime 10.0.11 was already present), then tool install works. Side effect
noted: `dotnet new` templates will default to the newest SDK after install.

**Working config (in place):** `C:\Users\saura\.pi\agent\pi-lsp.json` →
`csharp-ls` server, command `["csharp-ls"],` extensions `.cs`/`.csx`. Test
fixture: `C:\rig-toolcheck\{Broken.cs, lspcheck.csproj, lspcheck.sln, probe6.log}`
— keep the fixture; it is the rig's standing test for this tool.
