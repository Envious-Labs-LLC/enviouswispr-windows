param(
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,39}$')]
    [string]$RunLabel = 'local',

    [string]$OutputDirectory,

    [switch]$IncludeLocalRuntime
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'out\performance'
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

    throw '.NET 10 SDK is required for performance UAT.'
}

function Invoke-PerformanceStep {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [switch]$CaptureSafeJson
    )

    Write-Host "Running performance step: $Name"
    $started = [DateTimeOffset]::UtcNow
    $lines = @(& $Action 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    if ($null -eq $exitCode) {
        $exitCode = 0
    }
    foreach ($line in $lines) {
        Write-Host $line
    }

    $metrics = $null
    if ($CaptureSafeJson) {
        try {
            $metrics = ($lines -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            # Only typed JSON from the public-fixture UAT tools is persisted.
        }
    }

    [ordered]@{
        name = $Name
        succeeded = $exitCode -eq 0
        exitCode = $exitCode
        elapsedMilliseconds = [int64]([DateTimeOffset]::UtcNow - $started).TotalMilliseconds
        metrics = $metrics
    }
}

$dotnet10 = Resolve-DotNet10
$timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$machinePath = Join-Path $OutputDirectory "$RunLabel-$timestamp-machine.json"
$shellPath = Join-Path $OutputDirectory "$RunLabel-$timestamp-shell.json"
$runtimePath = Join-Path $OutputDirectory "$RunLabel-$timestamp-runtime.json"
$reportPath = Join-Path $OutputDirectory "$RunLabel-$timestamp-run.json"
$steps = [System.Collections.Generic.List[object]]::new()

Push-Location $repoRoot
try {
    $steps.Add((Invoke-PerformanceStep -Name 'portable-validation' -Action {
        & pwsh -NoProfile -File '.\scripts\validate.ps1'
    }))
    $steps.Add((Invoke-PerformanceStep -Name 'machine-probe' -Action {
        & $dotnet10 run --no-build --project '.\tools\compatibility-uat\EnviousWispr.Compatibility.Uat.csproj' `
            -c Release -- --output $machinePath
    }))
    $steps.Add((Invoke-PerformanceStep -Name 'shell-startup-and-recording' -Action {
        & $dotnet10 run --no-build --project '.\tools\performance-uat\EnviousWispr.Performance.Uat.csproj' `
            -c Release -- --output $shellPath
    }))
    $steps.Add((Invoke-PerformanceStep -Name 'reliability-cycles-1000' -Action {
        & $dotnet10 run --no-build --project '.\tools\reliability-uat\EnviousWispr.Reliability.Uat.csproj' `
            -c Release -- --iterations 1000
    } -CaptureSafeJson))

    if ($IncludeLocalRuntime) {
        $steps.Add((Invoke-PerformanceStep -Name 'full-runtime-startup-and-recording' -Action {
            & $dotnet10 run --no-build --project '.\tools\performance-uat\EnviousWispr.Performance.Uat.csproj' `
                -c Release -- --require-local-runtime --output $runtimePath
        }))
        $steps.Add((Invoke-PerformanceStep -Name 'parakeet-cpu-final-latency' -Action {
            & $dotnet10 run --no-build --project '.\tools\asr-uat\EnviousWispr.Asr.Uat.csproj' -c Release -- cpu
        } -CaptureSafeJson))
        $steps.Add((Invoke-PerformanceStep -Name 'whisper-cpu-final-latency' -Action {
            & $dotnet10 run --no-build --project '.\tools\whisper-uat\EnviousWispr.Whisper.Uat.csproj' -c Release -- cpu
        } -CaptureSafeJson))
        $steps.Add((Invoke-PerformanceStep -Name 'preview-cpu-cadence' -Action {
            & $dotnet10 run --no-build --project '.\tools\whisper-uat\EnviousWispr.Whisper.Uat.csproj' `
                -c Release -- cpu --preview-small
        } -CaptureSafeJson))
    }

    $machine = if (Test-Path -LiteralPath $machinePath) {
        Get-Content -LiteralPath $machinePath -Raw | ConvertFrom-Json
    }
    else {
        $null
    }
    $shell = if (Test-Path -LiteralPath $shellPath) {
        Get-Content -LiteralPath $shellPath -Raw | ConvertFrom-Json
    }
    else {
        $null
    }
    $runtime = if (Test-Path -LiteralPath $runtimePath) {
        Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json
    }
    else {
        $null
    }
    $report = [ordered]@{
        schemaVersion = 1
        runLabel = $RunLabel
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        includeLocalRuntime = [bool]$IncludeLocalRuntime
        machine = $machine
        shell = $shell
        runtime = $runtime
        steps = @($steps)
        succeeded = @($steps | Where-Object { -not $_.succeeded }).Count -eq 0
        privacy = 'Only content-free timings, resource counts, coarse hardware classes, provider outcomes, and public-fixture quality metrics are recorded.'
        unobserved = @(
            'battery discharge and energy use',
            'sustained thermal throttling',
            'sleep and resume during a performance run',
            'no-dedicated-GPU laptop classes',
            'model switching during a live dictation session'
        )
    }
    $report | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
    Write-Host "Performance report: $reportPath"
    exit $(if ($report.succeeded) { 0 } else { 8 })
}
finally {
    Pop-Location
}
