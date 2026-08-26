param(
    [Parameter(Mandatory = $true)]
    [string]$DistributionDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateSet('founder', 'beta')]
    [string]$Channel,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$EvidenceFile
)

$ErrorActionPreference = 'Stop'

function Require-Equal {
    param([string]$Name, $Actual, $Expected)
    if ($Actual -ne $Expected) {
        throw "$Name must be '$Expected' but was '$Actual'."
    }
}

function Require-ExactProperties {
    param([string]$Name, $Object, [string[]]$Expected)
    $actual = @($Object.PSObject.Properties.Name | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if (Compare-Object -ReferenceObject $wanted -DifferenceObject $actual) {
        throw "$Name has missing or unexpected properties."
    }
}

$identity = switch ($Channel) {
    'founder' {
        @{
            PackageId = 'EnviousLabs.EnviousWispr.Founder'
            ChannelName = 'win-x64-founder'
        }
    }
    'beta' {
        @{
            PackageId = 'EnviousLabs.EnviousWispr.Beta'
            ChannelName = 'win-x64-beta'
        }
    }
}

$distributionRoot = [IO.Path]::GetFullPath($DistributionDirectory)
if (-not (Test-Path -LiteralPath $distributionRoot -PathType Container)) {
    throw 'The distribution directory does not exist.'
}

$manifestPath = Join-Path $distributionRoot 'distribution-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'The distribution manifest is missing.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
Require-ExactProperties 'Distribution manifest' $manifest @(
    'schemaVersion', 'packageId', 'version', 'channel', 'runtime', 'signedForProduction', 'files')
Require-Equal 'Manifest schema' $manifest.schemaVersion 1
Require-Equal 'Package identity' $manifest.packageId $identity.PackageId
Require-Equal 'Version' $manifest.version $Version
Require-Equal 'Channel' $manifest.channel $identity.ChannelName
Require-Equal 'Runtime' $manifest.runtime 'win-x64'
Require-Equal 'Production signing flag' $manifest.signedForProduction $true

if (Test-Path -LiteralPath (Join-Path $distributionRoot 'UNSIGNED-DEVELOPMENT-ONLY.txt')) {
    throw 'Unsigned development output cannot become a release candidate.'
}

$manifestNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in @($manifest.files)) {
    Require-ExactProperties 'Manifest file entry' $file @('name', 'size', 'sha256')
    if ([string]::IsNullOrWhiteSpace($file.name) -or
        $file.name -ne [IO.Path]::GetFileName($file.name) -or
        $file.name.Length -gt 160) {
        throw 'A manifest file name is unsafe.'
    }
    if (-not $manifestNames.Add($file.name)) {
        throw "Duplicate manifest file: $($file.name)"
    }

    $artifactPath = Join-Path $distributionRoot $file.name
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "Manifest artifact is missing: $($file.name)"
    }
    $artifact = Get-Item -LiteralPath $artifactPath
    Require-Equal "Size for $($file.name)" $artifact.Length ([int64]$file.size)
    $actualHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
    Require-Equal "SHA-256 for $($file.name)" $actualHash $file.sha256
}

$actualNames = @(Get-ChildItem -LiteralPath $distributionRoot -File |
    Where-Object Name -ne 'distribution-manifest.json' |
    ForEach-Object Name)
if (Compare-Object -ReferenceObject @($manifestNames) -DifferenceObject $actualNames) {
    throw 'The distribution directory and manifest do not contain the same immutable artifact set.'
}

$setupName = "$($identity.PackageId)-$($identity.ChannelName)-Setup.exe"
$packageName = "$($identity.PackageId)-$Version-$($identity.ChannelName)-full.nupkg"
$indexName = "releases.$($identity.ChannelName).json"
foreach ($required in @($setupName, $packageName, $indexName)) {
    if (-not $manifestNames.Contains($required)) {
        throw "Required release artifact is missing: $required"
    }
}

$setupSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $distributionRoot $setupName)
if ($setupSignature.Status -ne 'Valid' -or
    $setupSignature.SignerCertificate.Subject -notmatch 'Envious Labs') {
    throw 'Setup is not validly signed by Envious Labs.'
}

$scratch = Join-Path ([IO.Path]::GetTempPath()) "EnviousWisprReleaseGate-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $distributionRoot $packageName), $scratch)
    $portableRoot = Join-Path $scratch 'lib\app'
    $portableFiles = @(Get-ChildItem -LiteralPath $portableRoot -Recurse -File |
        Where-Object Extension -in @('.exe', '.dll'))
    if ($portableFiles.Count -eq 0) {
        throw 'The full package contains no portable binaries.'
    }
    foreach ($portable in $portableFiles) {
        $signature = Get-AuthenticodeSignature -LiteralPath $portable.FullName
        if ($signature.Status -ne 'Valid' -or
            $signature.SignerCertificate.Subject -notmatch 'Envious Labs') {
            throw "A packaged binary is not validly signed by Envious Labs: $($portable.Name)"
        }
    }
}
finally {
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $expectedParent = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
    if ((Split-Path -Parent $resolvedScratch) -eq $expectedParent -and
        (Split-Path -Leaf $resolvedScratch).StartsWith('EnviousWisprReleaseGate-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$evidencePath = [IO.Path]::GetFullPath($EvidenceFile)
if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
    throw 'The lifecycle evidence file is missing.'
}
$evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json -ErrorAction Stop
Require-ExactProperties 'Lifecycle evidence' $evidence @(
    'schemaVersion', 'channel', 'version', 'machineClass', 'checks', 'approvals', 'blockerIssueNumbers')
Require-Equal 'Evidence schema' $evidence.schemaVersion 1
Require-Equal 'Evidence channel' $evidence.channel $identity.ChannelName
Require-Equal 'Evidence version' $evidence.version $Version
if ($evidence.machineClass -notmatch '^[a-z0-9][a-z0-9-]{0,39}$') {
    throw 'Evidence machineClass must be a coarse, non-identifying label.'
}

$requiredChecks = @(
    'cleanInstall', 'admittedUpdate', 'atomicRestart', 'forcedFailureRollback', 'repair', 'uninstall',
    'dataPreservation', 'diagnosticsConsent', 'feedbackTriage', 'crashTriage', 'smartScreen',
    'endpointSecurity')
Require-ExactProperties 'Lifecycle checks' $evidence.checks $requiredChecks
foreach ($check in $requiredChecks) {
    Require-Equal "Lifecycle check $check" $evidence.checks.$check 'passed'
}

$requiredApprovals = @('founderRelease', 'updateEndpoint', 'telemetryServerPolicy')
Require-ExactProperties 'Release approvals' $evidence.approvals $requiredApprovals
foreach ($approval in $requiredApprovals) {
    Require-Equal "Approval $approval" $evidence.approvals.$approval $true
}
if (@($evidence.blockerIssueNumbers).Count -ne 0) {
    throw 'Release evidence still lists unresolved P0/P1 blocker issues.'
}

[ordered]@{
    schemaVersion = 1
    channel = $identity.ChannelName
    version = $Version
    machineClass = $evidence.machineClass
    artifactsVerified = $manifestNames.Count
    packagedBinariesVerified = $portableFiles.Count
    lifecycleChecksPassed = $requiredChecks.Count
    approvalsPassed = $requiredApprovals.Count
    ready = $true
    privacy = 'Content-free release evidence only; no paths, accounts, device identifiers, user content, or secrets.'
} | ConvertTo-Json
