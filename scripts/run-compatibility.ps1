param(
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,39}$')]
    [string]$RunLabel = 'local',

    [string]$OutputDirectory,

    [switch]$IncludeLocalRuntime
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'out\compatibility'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function Resolve-DotNet10 {
    $candidates = @(
        (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.dotnet\dotnet.exe'),
        (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) {
            continue
        }

        $sdks = & $candidate --list-sdks
        if ($LASTEXITCODE -eq 0 -and $sdks -match '^10\.') {
            return $candidate
        }
    }

    throw '.NET 10 SDK is required for compatibility UAT.'
}

function Invoke-CompatibilityStep {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Write-Host "Running compatibility step: $Name"
    $started = [DateTimeOffset]::UtcNow
    $lines = @(& $Action 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    if ($null -eq $exitCode) {
        $exitCode = 0
    }
    foreach ($line in $lines) {
        Write-Host $line
    }

    [ordered]@{
        name = $Name
        succeeded = $exitCode -eq 0
        exitCode = $exitCode
        elapsedMilliseconds = [int64]([DateTimeOffset]::UtcNow - $started).TotalMilliseconds
    }
}

$dotnet10 = Resolve-DotNet10
$timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$probePath = Join-Path $OutputDirectory "$RunLabel-$timestamp-machine.json"
$reportPath = Join-Path $OutputDirectory "$RunLabel-$timestamp-run.json"
$steps = [System.Collections.Generic.List[object]]::new()

Push-Location $repoRoot
try {
    $steps.Add((Invoke-CompatibilityStep -Name 'portable-validation' -Action {
        if ($IncludeLocalRuntime) {
            & pwsh -NoProfile -File '.\scripts\validate.ps1' -IncludeLocalRuntime
        }
        else {
            & pwsh -NoProfile -File '.\scripts\validate.ps1'
        }
    }))
    $steps.Add((Invoke-CompatibilityStep -Name 'machine-probe' -Action {
        & $dotnet10 run --project '.\tools\compatibility-uat\EnviousWispr.Compatibility.Uat.csproj' `
            -c Release -- --output $probePath
    }))
    $steps.Add((Invoke-CompatibilityStep -Name 'physical-microphone' -Action {
        & $dotnet10 run --project '.\tools\audio-uat\EnviousWispr.Audio.Uat.csproj' -c Release
    }))
    $steps.Add((Invoke-CompatibilityStep -Name 'global-hotkey' -Action {
        & $dotnet10 run --project '.\tools\hotkey-uat\EnviousWispr.Hotkey.Uat.csproj' -c Release
    }))
    $steps.Add((Invoke-CompatibilityStep -Name 'isolated-runtime' -Action {
        & $dotnet10 run --project '.\tools\runtime-uat\EnviousWispr.Runtime.Uat.csproj' -c Release
    }))

    $machine = if (Test-Path -LiteralPath $probePath) {
        Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
    }
    else {
        $null
    }
    $report = [ordered]@{
        schemaVersion = 1
        runLabel = $RunLabel
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        machine = $machine
        steps = @($steps)
        succeeded = @($steps | Where-Object { -not $_.succeeded }).Count -eq 0
        privacy = 'No device identifiers, device names, account names, paths, audio, transcripts, clipboard contents, or surrounding text are recorded.'
        unobserved = @(
            'real target-app delivery',
            'multi-monitor overlay movement',
            'endpoint-security allow and block behavior',
            'signed install and update lifecycle'
        )
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
    Write-Host "Compatibility report: $reportPath"
    exit $(if ($report.succeeded) { 0 } else { 5 })
}
finally {
    Pop-Location
}
