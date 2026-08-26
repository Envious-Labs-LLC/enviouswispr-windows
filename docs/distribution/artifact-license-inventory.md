# Model and native-runtime license inventory

This is a source-evidence inventory, not legal approval. It records the upstream license statements observed
for the model, native-runtime, and public-test artifacts that are outside the production NuGet graph. The
machine-readable source is `artifact-license-inventory.json`.

| Artifact | Upstream evidence | Current decision |
| --- | --- | --- |
| Parakeet final model | NVIDIA identifies Parakeet TDT 0.6B v3 as CC-BY-4.0 | Conversion provenance, exact payload, attribution, and approval pending |
| Whisper final and preview models | OpenAI's repository licenses Whisper code and model weights under MIT | Conversion provenance, exact payloads, notices, and approval pending |
| EG-1 | The recorded base, Qwen3-4B-Instruct-2507, is Apache-2.0 | Base revision, training/ownership chain, derivative license, payload, and approval pending |
| Windows llama.cpp server | Upstream project is MIT | Exact commit/build, vendored-component notices, binary hashes, and approval pending |
| CUDA runtime subset | NVIDIA's CUDA EULA limits redistribution to identified distributable portions | Exact release/Attachment A files, downstream terms, private packaging, and approval pending |
| cuDNN runtime subset | NVIDIA's cuDNN agreement identifies runtime `.dll` files as distributable subject to its requirements | Exact release/archive/files, downstream terms, app-only access, and approval pending |
| MINDS-14 public UAT fixtures | PolyAI dataset is CC-BY-4.0 at the pinned fixture revision | Checked-in attribution and public distribution approval pending |

Run the structural gate at any time:

```powershell
pwsh -NoProfile -File .\scripts\validate-artifact-license-inventory.ps1
```

The release owner runs the strict form only after every exact source revision, payload, notice, and decision is
approved:

```powershell
pwsh -NoProfile -File .\scripts\validate-artifact-license-inventory.ps1 -RequireApproved
```

The strict form currently fails by design. An approval applies only to exact signed-manifest payloads; it does
not authorize a different model conversion, llama.cpp build, CUDA/cuDNN release, or file set. If an artifact is
excluded from a release, the release candidate needs a separate manifest-to-inventory check proving that its
files and feature claims are absent. Do not turn a pending record into an approval based only on this page.
