# Third-party notices

This inventory is generated from the exact resolved NuGet graphs of the production WinUI app and
runtime worker. Run `scripts/generate-third-party-notices.ps1` after dependency changes and commit
the result. Canonical validation rejects missing license metadata or notice drift.

| Package | Version | License metadata | License-file SHA-256 |
| --- | --- | --- | --- |
| Microsoft.Extensions.AI.Abstractions | 10.2.0 | expression: MIT | not-applicable |
| Microsoft.ML.OnnxRuntime.Gpu | 1.29.0 | file: embedded package file LICENSE | C250D6278F0B47A6439FB7592B08B58A55EB9F535AA49A1DB63211C3F982B674 |
| Microsoft.ML.OnnxRuntime.Gpu.Linux | 1.29.0 | file: embedded package file LICENSE | C250D6278F0B47A6439FB7592B08B58A55EB9F535AA49A1DB63211C3F982B674 |
| Microsoft.ML.OnnxRuntime.Gpu.Windows | 1.29.0 | file: embedded package file LICENSE | C250D6278F0B47A6439FB7592B08B58A55EB9F535AA49A1DB63211C3F982B674 |
| Microsoft.ML.OnnxRuntime.Managed | 1.29.0 | file: embedded package file LICENSE.txt | C250D6278F0B47A6439FB7592B08B58A55EB9F535AA49A1DB63211C3F982B674 |
| Microsoft.Web.WebView2 | 1.0.3719.77 | file: embedded package file LICENSE.txt | 0AF8F1B807512AAE39C2AC1AA4D0CAE65CABECB6FD554B8439A5162A0D6ECA55 |
| Microsoft.Windows.AI.MachineLearning | 2.1.74 | file: embedded package file license.txt | 66395F8CB219087FAE2BD025010BD9076B736C14F03B48F20295471C0C376814 |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2526 | url: https://aka.ms/WinSDKLicenseURL | not-applicable |
| Microsoft.Windows.SDK.BuildTools.MSIX | 1.7.251221100 | file: embedded package file sdk_license.txt | A7A5C7E7FF998558983D6ACA2702117C328AEB0C6404D298CB275F5623C6FD13 |
| Microsoft.WindowsAppSDK | 2.4.0 | file: embedded package file license.txt | 5B11E6347756E40FE0274BC08C97F89201B94F0D50181A09A00F1F4740840501 |
| Microsoft.WindowsAppSDK.AI | 2.4.4 | file: embedded package file license.txt | 5B11E6347756E40FE0274BC08C97F89201B94F0D50181A09A00F1F4740840501 |
| Microsoft.WindowsAppSDK.Base | 2.0.4 | file: embedded package file license.txt | 5B11E6347756E40FE0274BC08C97F89201B94F0D50181A09A00F1F4740840501 |
| Microsoft.WindowsAppSDK.DWrite | 2.1.0 | file: embedded package file license.txt | 5B11E6347756E40FE0274BC08C97F89201B94F0D50181A09A00F1F4740840501 |
| Microsoft.WindowsAppSDK.Foundation | 2.3.9 | file: embedded package file license.txt | 5B11E6347756E40FE0274BC08C97F89201B94F0D50181A09A00F1F4740840501 |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 2.1.6 | file: embedded package file license.txt | 5B11E6347756E40FE0274BC08C97F89201B94F0D50181A09A00F1F4740840501 |
| Microsoft.WindowsAppSDK.ML | 2.1.74 | file: embedded package file license.txt | 656AAB74C15AA9F9964BCDCC993EB2755CBDB4822D5E0E3BC61D2E281897F758 |
| Microsoft.WindowsAppSDK.Runtime | 2.4.0 | file: embedded package file license.txt | 5B11E6347756E40FE0274BC08C97F89201B94F0D50181A09A00F1F4740840501 |
| Microsoft.WindowsAppSDK.Search | 2.4.4 | file: embedded package file license.txt | 5B11E6347756E40FE0274BC08C97F89201B94F0D50181A09A00F1F4740840501 |
| Microsoft.WindowsAppSDK.Widgets | 2.0.5 | file: embedded package file license.txt | 5B11E6347756E40FE0274BC08C97F89201B94F0D50181A09A00F1F4740840501 |
| Microsoft.WindowsAppSDK.WinUI | 2.3.6 | file: embedded package file license.txt | 5B11E6347756E40FE0274BC08C97F89201B94F0D50181A09A00F1F4740840501 |
| NAudio.Core | 3.0.1 | expression: MIT | not-applicable |
| NAudio.Wasapi | 3.0.1 | expression: MIT | not-applicable |
| System.CodeDom | 10.0.11 | expression: MIT | not-applicable |
| System.Management | 10.0.11 | expression: MIT | not-applicable |
| System.Numerics.Tensors | 9.0.0 | expression: MIT | not-applicable |
| Velopack | 1.2.0 | expression: MIT | not-applicable |
| Whisper.net | 1.9.1 | file: embedded package file LICENSE | D9CA846BBB028D80A87027B886DC7DB63BA22DFC6A5E17C4AACE03F62AB644EC |
| Whisper.net.Runtime | 1.9.1 | expression: MIT | not-applicable |
| Whisper.net.Runtime.Cuda.Windows | 1.9.1 | expression: MIT | not-applicable |
| Whisper.net.Runtime.Metal | 1.9.1 | expression: MIT | not-applicable |

Model packs, CUDA redistributables, and EG-1 are separately delivered artifacts. Their signed model
manifests must carry the exact upstream license notice and acceptance requirements; this NuGet
inventory does not approve or replace those notices. A public release remains blocked until every
shipped artifact has a reviewed license record. Source evidence and open decisions are tracked in
`docs/distribution/artifact-license-inventory.md`.
