param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet('stable', 'founder', 'beta')]
    [string]$Channel = 'stable',

    [string]$OutputDirectory,

    [string]$AzureTrustedSignFile,

    [switch]$DevelopmentUnsigned
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if ($DevelopmentUnsigned -and -not [string]::IsNullOrWhiteSpace($AzureTrustedSignFile)) {
    throw 'Choose either -DevelopmentUnsigned or -AzureTrustedSignFile, not both.'
}

if (-not $DevelopmentUnsigned) {
    if ([string]::IsNullOrWhiteSpace($AzureTrustedSignFile)) {
        throw 'Production packaging requires -AzureTrustedSignFile. Use -DevelopmentUnsigned only for local installer testing.'
    }

    $AzureTrustedSignFile = [IO.Path]::GetFullPath($AzureTrustedSignFile)
    if (-not (Test-Path -LiteralPath $AzureTrustedSignFile -PathType Leaf)) {
        throw "Azure Artifact Signing metadata was not found at $AzureTrustedSignFile"
    }
}

$identity = switch ($Channel) {
    'stable' {
        @{
            PackageId = 'EnviousLabs.EnviousWispr'
            DisplayName = 'EnviousWispr'
            ChannelName = 'win-x64-stable'
        }
    }
    'founder' {
        @{
            PackageId = 'EnviousLabs.EnviousWispr.Founder'
            DisplayName = 'EnviousWispr Founder'
            ChannelName = 'win-x64-founder'
        }
    }
    'beta' {
        @{
            PackageId = 'EnviousLabs.EnviousWispr.Beta'
            DisplayName = 'EnviousWispr Beta'
            ChannelName = 'win-x64-beta'
        }
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "dist\windows\$Channel"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

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
$scratchRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "EnviousWisprPackage-$([Guid]::NewGuid().ToString('N'))"))
$publishDirectory = Join-Path $scratchRoot 'publish'
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

Push-Location $repoRoot
try {
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw 'Pinned Velopack tool restore failed.'
    }

    & $dotnet10 publish 'src/Production/EnviousWispr.App/EnviousWispr.App.csproj' `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --nologo `
        -p:Platform=x64 `
        -p:Version=$Version `
        -p:EnviousWisprReleaseChannel=$Channel `
        -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw 'Self-contained Windows publish failed.'
    }

    $requiredPublishedFiles = @(
        'EnviousWispr.App.exe',
        'EnviousWispr.App.pri',
        'App.xbf',
        'MainWindow.xbf',
        'DictationOverlayWindow.xbf',
        'EnviousWispr.RuntimeWorker.exe',
        'Microsoft.WindowsAppRuntime.Bootstrap.dll'
    )
    foreach ($required in $requiredPublishedFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $required) -PathType Leaf)) {
            throw "Self-contained publish is missing required file: $required"
        }
    }

    $packArguments = @(
        'tool', 'run', 'vpk', '--', 'pack',
        '--packId', $identity.PackageId,
        '--packVersion', $Version,
        '--packDir', $publishDirectory,
        '--mainExe', 'EnviousWispr.App.exe',
        '--packAuthors', 'Envious Labs LLC',
        '--packTitle', $identity.DisplayName,
        '--channel', $identity.ChannelName,
        '--runtime', 'win-x64',
        '--outputDir', $OutputDirectory,
        '--shortcuts', 'StartMenuRoot'
    )
    if (-not $DevelopmentUnsigned) {
        $packArguments += @('--azureTrustedSignFile', $AzureTrustedSignFile)
    }

    & dotnet @packArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Velopack packaging failed.'
    }

    $setup = Get-ChildItem -LiteralPath $OutputDirectory -Filter "$($identity.PackageId)-$($identity.ChannelName)-Setup.exe" |
        Select-Object -First 1
    $fullPackage = Get-ChildItem -LiteralPath $OutputDirectory -Filter "$($identity.PackageId)-$Version-$($identity.ChannelName)-full.nupkg" |
        Select-Object -First 1
    $releaseIndex = Join-Path $OutputDirectory "releases.$($identity.ChannelName).json"
    if ($null -eq $setup -or $null -eq $fullPackage -or -not (Test-Path -LiteralPath $releaseIndex)) {
        throw 'Velopack did not produce the required setup, full package, and isolated channel index.'
    }

    if (-not $DevelopmentUnsigned) {
        $signature = Get-AuthenticodeSignature -LiteralPath $setup.FullName
        if ($signature.Status -ne 'Valid' -or
            $signature.SignerCertificate.Subject -notmatch 'Envious Labs') {
            throw 'The production setup executable does not have a valid Envious Labs Authenticode signature.'
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $signatureInspectionRoot = Join-Path $scratchRoot 'signed-package'
        [IO.Compression.ZipFile]::ExtractToDirectory($fullPackage.FullName, $signatureInspectionRoot)
        $packagedPortableExecutables = Get-ChildItem `
            -LiteralPath (Join-Path $signatureInspectionRoot 'lib\app') `
            -Recurse `
            -File |
            Where-Object { $_.Extension -in @('.exe', '.dll') }
        if ($packagedPortableExecutables.Count -eq 0) {
            throw 'The production package contains no portable executables to verify.'
        }

        foreach ($portableExecutable in $packagedPortableExecutables) {
            $portableSignature = Get-AuthenticodeSignature -LiteralPath $portableExecutable.FullName
            if ($portableSignature.Status -ne 'Valid' -or
                $portableSignature.SignerCertificate.Subject -notmatch 'Envious Labs') {
                throw "Packaged executable is not validly signed by Envious Labs: $($portableExecutable.Name)"
            }
        }
    }

    $releaseFiles = Get-ChildItem -LiteralPath $OutputDirectory -File |
        Sort-Object Name |
        ForEach-Object {
            [ordered]@{
                name = $_.Name
                size = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
    $manifest = [ordered]@{
        schemaVersion = 1
        packageId = $identity.PackageId
        version = $Version
        channel = $identity.ChannelName
        runtime = 'win-x64'
        signedForProduction = -not $DevelopmentUnsigned
        files = @($releaseFiles)
    }
    $manifestPath = Join-Path $OutputDirectory 'distribution-manifest.json'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

    if ($DevelopmentUnsigned) {
        Set-Content `
            -LiteralPath (Join-Path $OutputDirectory 'UNSIGNED-DEVELOPMENT-ONLY.txt') `
            -Value 'This package is for local installer/update testing only. It is not a production release.' `
            -Encoding utf8NoBOM
    }

    Write-Host "Windows package ready: $OutputDirectory"
    Write-Host "Identity: $($identity.PackageId)"
    Write-Host "Channel: $($identity.ChannelName)"
    Write-Host "Signed for production: $(-not $DevelopmentUnsigned)"
}
finally {
    Pop-Location
    $resolvedScratch = [IO.Path]::GetFullPath($scratchRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedScratch.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedScratch).StartsWith('EnviousWisprPackage-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}
