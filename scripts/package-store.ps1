<#
Builds the Microsoft Store package (MSIX) for EnviousWispr. Ref #42, steps C and D.

  powershell -ExecutionPolicy Bypass -File scripts/package-store.ps1
  powershell -ExecutionPolicy Bypass -File scripts/package-store.ps1 -ForStoreUpload
  powershell -ExecutionPolicy Bypass -File scripts/package-store.ps1 -LlamaRuntimeDirectory C:\path\to\llama.cpp

THE PACKAGE IS A SELF-CONTAINED PUBLISH, NEVER A BUILD. `dotnet build -p:EnviousWisprPackaged=true` made a
package that ran only on a PC with the .NET 10 runtime already installed, and without the runtime worker's
own assembly, so transcription could not start. The project now refuses that route; this script is the
one that works, and it checks the payload rather than trusting the build's exit code.

PINNED TOOLCHAIN (verified before packaging):
  - .NET 10 SDK: 10.0.4xx (local rig 10.0.400; CI actions/setup-dotnet 10.0.x)
  - TargetFramework: net10.0-windows10.0.26100.0 (MinVersion 10.0.22000.0, MaxVersionTested 10.0.26100.0)
  - Microsoft.WindowsAppSDK: 2.4.0
  - Microsoft.Windows.SDK.BuildTools: 10.0.28000.2526 (makeappx, makepri, signtool)
  - Microsoft.Windows.SDK.BuildTools.MSIX: 1.7.251221100
  - Microsoft Store Developer CLI (msstore): 0.4.4.3 (required for Partner Center submission)

A test package is unsigned: install it on a development machine by registering its unpacked layout in
Developer Mode, or sign it with the development certificate (distribution.md). The Store signs the upload.
#>
param(
    [string]$OutputDirectory,

    # Optional explicit directory containing llama-server.exe and its DLL dependencies.
    # Auto-discovered from tools\llama.cpp or %LocalAppData%\Envious Labs\EnviousWispr\runtime\llama.cpp when present.
    [string]$LlamaRuntimeDirectory,

    # Skip staging runtime\llama-server.exe even if a local llama.cpp directory exists (never permitted with -ForStoreUpload).
    [switch]$SkipLlamaRuntime,

    # Refuse to produce a package the Store would reject at upload: the identity must be the one Partner
    # Center reserved, not the placeholder committed until that reservation exists, and runtime\llama-server.exe
    # must be bundled inside the package payload under Store policy 10.2.2.
    [switch]$ForStoreUpload
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\Production\EnviousWispr.App\EnviousWispr.App.csproj'
$directoryBuildProps = Join-Path $repoRoot 'src\Production\Directory.Build.props'
$manifestSource = Join-Path $repoRoot 'src\Production\EnviousWispr.App\Package.appxmanifest'
$placeholderName = 'EnviousLabsLLC.EnviousWispr'
$placeholderPublisher = 'CN=EnviousLabsPlaceholder'

$pinnedToolchain = [ordered]@{
    DotNetMajor                  = '10'
    TargetFramework              = 'net10.0-windows10.0.26100.0'
    TargetPlatformMinVersion     = '10.0.22000.0'
    TargetDeviceMaxVersionTested = '10.0.26100.0'
    WindowsAppSdkVersion         = '2.4.0'
    WindowsSdkBuildToolsVersion  = '10.0.28000.2526'
    MsixBuildToolsVersion        = '1.7.251221100'
    MsStoreCliMinVersion         = '0.4.2'
}

function Test-PlaceholderIdentity($identity) {
    return $identity.Name -eq $placeholderName -or $identity.Publisher -eq $placeholderPublisher
}

function Assert-PinnedToolchain {
    param(
        [xml]$PropsXml,
        [xml]$ProjectXml,
        [xml]$ManifestXml
    )

    $tfm = [string]$PropsXml.Project.PropertyGroup.TargetFramework
    $minVer = [string]$PropsXml.Project.PropertyGroup.TargetPlatformMinVersion
    if ($tfm -ne $pinnedToolchain.TargetFramework -or $minVer -ne $pinnedToolchain.TargetPlatformMinVersion) {
        throw "Directory.Build.props drifted from the pinned Store toolchain ($tfm / $minVer; expected $($pinnedToolchain.TargetFramework) / $($pinnedToolchain.TargetPlatformMinVersion))."
    }

    $deviceFamily = $ManifestXml.Package.Dependencies.TargetDeviceFamily
    if ($deviceFamily.MinVersion -ne $pinnedToolchain.TargetPlatformMinVersion -or
        $deviceFamily.MaxVersionTested -ne $pinnedToolchain.TargetDeviceMaxVersionTested) {
        throw "Package.appxmanifest TargetDeviceFamily ($($deviceFamily.MinVersion) .. $($deviceFamily.MaxVersionTested)) drifted from pinned versions."
    }

    $packageRefs = @($ProjectXml.Project.ItemGroup.PackageReference)
    $winAppSdk = $packageRefs | Where-Object { $_.Include -eq 'Microsoft.WindowsAppSDK' } | Select-Object -First 1
    $sdkBuildTools = $packageRefs | Where-Object { $_.Include -eq 'Microsoft.Windows.SDK.BuildTools' } | Select-Object -First 1
    if ($null -eq $winAppSdk -or $winAppSdk.Version -ne $pinnedToolchain.WindowsAppSdkVersion) {
        throw "EnviousWispr.App.csproj Microsoft.WindowsAppSDK version drifted (expected $($pinnedToolchain.WindowsAppSdkVersion))."
    }
    if ($null -eq $sdkBuildTools -or $sdkBuildTools.Version -ne $pinnedToolchain.WindowsSdkBuildToolsVersion) {
        throw "EnviousWispr.App.csproj Microsoft.Windows.SDK.BuildTools version drifted (expected $($pinnedToolchain.WindowsSdkBuildToolsVersion))."
    }
}

function Resolve-LlamaRuntimeDirectory {
    param(
        [string]$ExplicitDirectory,
        [switch]$Skip,
        [switch]$Required
    )

    if ($Skip) {
        if ($Required) {
            throw 'Cannot combine -SkipLlamaRuntime with -ForStoreUpload: a Store upload package must bundle runtime\llama-server.exe (Store policy 10.2.2).'
        }
        return $null
    }

    foreach ($explicit in @($ExplicitDirectory, $env:ENVIOUSWISPR_LLAMA_RUNTIME_DIRECTORY)) {
        if (-not [string]::IsNullOrWhiteSpace($explicit)) {
            $full = [IO.Path]::GetFullPath($explicit)
            if (-not (Test-Path -LiteralPath (Join-Path $full 'llama-server.exe') -PathType Leaf)) {
                throw "The specified llama.cpp runtime directory does not contain llama-server.exe: $full"
            }
            return $full
        }
    }

    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    $candidates = @(
        (Join-Path $repoRoot 'tools\llama.cpp'),
        (Join-Path $localAppData 'Envious Labs\EnviousWispr\runtime\llama.cpp'),
        (Join-Path $localAppData 'Envious Labs\EnviousWispr-Founder\runtime\llama.cpp')
    )
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'llama-server.exe') -PathType Leaf)) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    if ($Required) {
        throw 'A Store upload package must bundle runtime\llama-server.exe and its dependencies (Store policy 10.2.2). Supply -LlamaRuntimeDirectory or stage tools\llama.cpp.'
    }

    return $null
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'dist\windows\store'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

[xml]$propsXml = Get-Content -LiteralPath $directoryBuildProps -Raw
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
[xml]$sourceManifest = Get-Content -LiteralPath $manifestSource -Raw
Assert-PinnedToolchain -PropsXml $propsXml -ProjectXml $projectXml -ManifestXml $sourceManifest

if ($ForStoreUpload -and (Test-PlaceholderIdentity $sourceManifest.Package.Identity)) {
    throw "Package.appxmanifest still carries a placeholder identity ($placeholderName, $placeholderPublisher). Commit the Identity Name and Publisher that Partner Center assigned before building a Store upload."
}

function Resolve-DotNet10 {
    $candidate = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $candidate) {
        $sdks = & $candidate --list-sdks
        if ($LASTEXITCODE -eq 0 -and ($sdks -match '^10\.')) {
            return $candidate
        }
    }

    $pathDotNet = (Get-Command dotnet -ErrorAction Stop).Source
    $sdks = & $pathDotNet --list-sdks
    if ($LASTEXITCODE -eq 0 -and ($sdks -match '^10\.')) {
        return $pathDotNet
    }

    throw '.NET 10 SDK is required to package the Windows application.'
}

$dotnet10 = Resolve-DotNet10
$dotnetSdkVersion = (& $dotnet10 --version).Trim()
$resolvedLlamaDirectory = Resolve-LlamaRuntimeDirectory `
    -ExplicitDirectory $LlamaRuntimeDirectory `
    -Skip:$SkipLlamaRuntime `
    -Required:$ForStoreUpload

$scratchRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "EnviousWisprStore-$([Guid]::NewGuid().ToString('N'))"))
$publishDirectory = Join-Path $scratchRoot 'publish'
$packageDirectory = Join-Path $scratchRoot 'package\'
New-Item -ItemType Directory -Force -Path $publishDirectory, $packageDirectory | Out-Null

try {
    $publishArgs = @(
        'publish', $project,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '--nologo',
        '-p:Platform=x64',
        '-p:EnviousWisprPackaged=true',
        "-p:AppxPackageDir=$packageDirectory",
        '-o', $publishDirectory
    )
    if (-not [string]::IsNullOrWhiteSpace($resolvedLlamaDirectory)) {
        Write-Host "Staging bundled llama-server runtime from $resolvedLlamaDirectory..."
        $publishArgs += "-p:EnviousWisprLlamaRuntimeDir=$resolvedLlamaDirectory"
    }
    else {
        Write-Host 'No local llama.cpp runtime directory found; building development/CI Store package without bundled runtime\llama-server.exe.'
    }

    & $dotnet10 @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'Self-contained Store publish failed.'
    }

    $packages = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -Filter '*.msix' -File)
    if ($packages.Count -ne 1) {
        throw "Expected exactly one .msix under $packageDirectory, found $($packages.Count)."
    }
    $package = $packages[0]

    # THE PAYLOAD IS CHECKED INSIDE THE PACKAGE, NOT IN THE PUBLISH FOLDER. The two differ: the worker's
    # assembly was in the folder and missing from the package, which is the defect this list exists for.
    $requiredEntries = @(
        'AppxManifest.xml',
        'resources.pri',
        'EnviousWispr.App.exe',
        'EnviousWispr.App.dll',
        'EnviousWispr.RuntimeWorker.exe',
        'EnviousWispr.RuntimeWorker.dll',
        'EnviousWispr.RuntimeWorker.deps.json',
        'EnviousWispr.RuntimeWorker.runtimeconfig.json',
        'coreclr.dll',
        'hostfxr.dll',
        'Microsoft.WindowsAppRuntime.Bootstrap.dll',
        'UIAutomationClient.dll',
        'WindowsBase.dll'
    )
    if (-not [string]::IsNullOrWhiteSpace($resolvedLlamaDirectory)) {
        $requiredEntries += @(
            'runtime/llama-server.exe',
            'runtime/llama-server-impl.dll',
            'runtime/llama-common.dll',
            'runtime/llama.dll',
            'runtime/ggml.dll',
            'runtime/ggml-base.dll',
            'runtime/libomp.dll'
        )
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName -replace '\\', '/' })
        $manifestEntry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $manifestEntry) {
            throw 'The package has no AppxManifest.xml.'
        }
        $reader = New-Object IO.StreamReader($manifestEntry.Open())
        try {
            [xml]$packagedManifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $missing = @($requiredEntries | Where-Object { $entries -notcontains $_ })
    if ($missing.Count -gt 0) {
        throw "The Store package is missing required files: $($missing -join ', ')"
    }

    $duplicates = @($entries | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
    if ($duplicates.Count -gt 0) {
        throw "The Store package carries a path more than once: $($duplicates -join ', ')"
    }

    # CUDA AND cuDNN ARE DELIVERED ON DEMAND (models/manifests/cuda-runtime.json), NEVER IN THE STORE MSIX.
    $forbiddenCudaEntries = @($entries | Where-Object {
        $leaf = [IO.Path]::GetFileName($_)
        $leaf -like 'cublas*.dll' -or $leaf -like 'cudnn*.dll' -or $leaf -like 'cudart*.dll' -or $leaf -like 'cufft*.dll'
    })
    if ($forbiddenCudaEntries.Count -gt 0) {
        throw "The Store package must not bundle NVIDIA CUDA/cuDNN redistributables ($($forbiddenCudaEntries -join ', ')); they ship via the on-demand cuda-runtime manifest."
    }

    # THE WORKER IS JUDGED FROM THE PACKAGE, NOT FROM THE PUBLISH FOLDER, for the same reason the payload
    # is: the two have already disagreed once. A self-contained worker says so in its own config (a
    # framework-dependent one names a shared framework the customer's PC does not have), and it must start
    # from the extracted payload with nothing but what the package carries.
    $extractedDirectory = Join-Path $scratchRoot 'extracted'
    [IO.Compression.ZipFile]::ExtractToDirectory($package.FullName, $extractedDirectory)
    $workerConfig = Get-Content -LiteralPath (Join-Path $extractedDirectory 'EnviousWispr.RuntimeWorker.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($null -eq $workerConfig.runtimeOptions.includedFrameworks) {
        throw 'The packaged runtime worker is framework-dependent; the Store package must carry its own .NET.'
    }

    Write-Host 'Validating the packaged runtime worker can launch...'
    & (Join-Path $extractedDirectory 'EnviousWispr.RuntimeWorker.exe') 2>$null
    if ($LASTEXITCODE -ne 2) {
        throw "The packaged runtime worker could not launch (exit $LASTEXITCODE)."
    }

    if (-not [string]::IsNullOrWhiteSpace($resolvedLlamaDirectory)) {
        Write-Host 'Validating the packaged llama-server runtime can launch from the extracted MSIX payload...'
        $packagedLlama = Join-Path $extractedDirectory 'runtime\llama-server.exe'
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $llamaVersionOutput = & $packagedLlama --version 2>&1 | Out-String
            $llamaExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousPreference
        }
        if ($llamaExitCode -ne 0) {
            throw "The packaged runtime\llama-server.exe could not launch from the extracted MSIX layout (exit $llamaExitCode): $llamaVersionOutput"
        }
    }

    $identity = $packagedManifest.Package.Identity
    if ($identity.Version -notmatch '^\d+\.\d+\.\d+\.0$') {
        throw "The package version $($identity.Version) must have a revision of 0; the Store assigns the revision."
    }
    if ($ForStoreUpload -and (Test-PlaceholderIdentity $identity)) {
        throw "The built package carries a placeholder identity ($($identity.Name), $($identity.Publisher)); the Store would reject it at upload."
    }

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $destination = Join-Path $OutputDirectory $package.Name
    Copy-Item -LiteralPath $package.FullName -Destination $destination -Force

    Write-Host "Store package: $destination"
    Write-Host "Identity: $($identity.Name) $($identity.Version) ($($identity.Publisher))"
    Write-Host "Toolchain: .NET SDK $dotnetSdkVersion | TFM $($pinnedToolchain.TargetFramework) | WinAppSDK $($pinnedToolchain.WindowsAppSdkVersion) | BuildTools $($pinnedToolchain.WindowsSdkBuildToolsVersion) | MSIX $($pinnedToolchain.MsixBuildToolsVersion)"
    Write-Host ("Llama runtime bundled: {0}" -f (-not [string]::IsNullOrWhiteSpace($resolvedLlamaDirectory)))
    Write-Host ("Size: {0:N0} MB, {1} files" -f ($package.Length / 1MB), $entries.Count)
    if (Test-PlaceholderIdentity $identity) {
        Write-Host 'TEST PACKAGE: the identity is the placeholder, so this cannot be uploaded to the Store.'
    }
}
finally {
    Remove-Item -LiteralPath $scratchRoot -Recurse -Force -ErrorAction SilentlyContinue
}
