<#
Builds the Microsoft Store package (MSIX) for EnviousWispr. Ref #42, step D.

  powershell -ExecutionPolicy Bypass -File scripts/package-store.ps1
  powershell -ExecutionPolicy Bypass -File scripts/package-store.ps1 -ForStoreUpload

THE PACKAGE IS A SELF-CONTAINED PUBLISH, NEVER A BUILD. `dotnet build -p:EnviousWisprPackaged=true` made a
package that ran only on a PC with the .NET 10 runtime already installed, and without the runtime worker's
own assembly, so transcription could not start. The project now refuses that route; this script is the
one that works, and it checks the payload rather than trusting the build's exit code.

A test package is unsigned: install it on a development machine by registering its unpacked layout in
Developer Mode, or sign it with the development certificate (distribution.md). The Store signs the upload.
#>
param(
    [string]$OutputDirectory,

    # Refuse to produce a package the Store would reject at upload: the identity must be the one Partner
    # Center reserved, not the placeholder committed until that reservation exists.
    [switch]$ForStoreUpload
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\Production\EnviousWispr.App\EnviousWispr.App.csproj'
$manifestSource = Join-Path $repoRoot 'src\Production\EnviousWispr.App\Package.appxmanifest'
$placeholderPublisher = 'CN=EnviousLabsPlaceholder'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'dist\windows\store'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

[xml]$sourceManifest = Get-Content -LiteralPath $manifestSource -Raw
$sourcePublisher = $sourceManifest.Package.Identity.Publisher
if ($ForStoreUpload -and $sourcePublisher -eq $placeholderPublisher) {
    throw "Package.appxmanifest still carries the placeholder publisher ($placeholderPublisher). Commit the Identity Name and Publisher that Partner Center assigned before building a Store upload."
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
$scratchRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "EnviousWisprStore-$([Guid]::NewGuid().ToString('N'))"))
$publishDirectory = Join-Path $scratchRoot 'publish'
$packageDirectory = Join-Path $scratchRoot 'package\'
New-Item -ItemType Directory -Force -Path $publishDirectory, $packageDirectory | Out-Null

& $dotnet10 publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --nologo `
    -p:Platform=x64 `
    -p:EnviousWisprPackaged=true `
    "-p:AppxPackageDir=$packageDirectory" `
    -o $publishDirectory
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
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName })
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

# A SELF-CONTAINED WORKER SAYS SO IN ITS OWN CONFIG. A framework-dependent one names a shared framework
# the customer's PC does not have, and starts only on a developer's machine.
$workerConfig = Get-Content -LiteralPath (Join-Path $publishDirectory 'EnviousWispr.RuntimeWorker.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($null -eq $workerConfig.runtimeOptions.includedFrameworks) {
    throw 'The runtime worker was published framework-dependent; the Store package must carry its own .NET.'
}

Write-Host 'Validating the self-contained runtime worker can launch...'
& (Join-Path $publishDirectory 'EnviousWispr.RuntimeWorker.exe') 2>$null
if ($LASTEXITCODE -ne 2) {
    throw "The self-contained runtime worker could not launch (exit $LASTEXITCODE)."
}

$identity = $packagedManifest.Package.Identity
if ($identity.Version -notmatch '^\d+\.\d+\.\d+\.0$') {
    throw "The package version $($identity.Version) must have a revision of 0; the Store assigns the revision."
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$destination = Join-Path $OutputDirectory $package.Name
Copy-Item -LiteralPath $package.FullName -Destination $destination -Force

Write-Host "Store package: $destination"
Write-Host "Identity: $($identity.Name) $($identity.Version) ($($identity.Publisher))"
Write-Host ("Size: {0:N0} MB, {1} files" -f ($package.Length / 1MB), $entries.Count)
if ($identity.Publisher -eq $placeholderPublisher) {
    Write-Host 'TEST PACKAGE: the publisher is the placeholder, so this cannot be uploaded to the Store.'
}
