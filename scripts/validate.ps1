param(
    [switch]$IncludeLocalRuntime
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host "Validating the private-beta release gate and deliberately red evidence example..."
$releaseGatePath = Join-Path $repoRoot "scripts\validate-release-candidate.ps1"
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile(
    $releaseGatePath,
    [ref]$null,
    [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "The private-beta release gate has PowerShell parse errors."
}
$redEvidence = Get-Content `
    -LiteralPath (Join-Path $repoRoot "docs\distribution\private-beta-evidence.example.json") `
    -Raw | ConvertFrom-Json -ErrorAction Stop
if (@($redEvidence.checks.PSObject.Properties.Value | Where-Object { $_ -ne 'unobserved' }).Count -gt 0 -or
    @($redEvidence.approvals.PSObject.Properties.Value | Where-Object { $_ -ne $false }).Count -gt 0 -or
    @($redEvidence.blockerIssueNumbers).Count -eq 0) {
    throw "The checked-in private-beta evidence example must remain explicitly unobserved and blocked."
}

Write-Host "Validating public-release repository compliance..."
& pwsh -NoProfile -File (Join-Path $repoRoot "scripts\audit-public-release.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "Public-release repository compliance failed."
}

function Resolve-DotNetSdk {
    param([Parameter(Mandatory = $true)][int]$MajorVersion)

    $candidates = [System.Collections.Generic.List[string]]::new()

    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        $candidates.Add((Join-Path $env:DOTNET_ROOT "dotnet.exe"))
    }

    $pathCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $pathCommand) {
        $candidates.Add($pathCommand.Source)
    }

    $userProfile = [Environment]::GetFolderPath("UserProfile")
    if ([string]::IsNullOrWhiteSpace($userProfile)) {
        $userProfile = $env:USERPROFILE
    }
    if (-not [string]::IsNullOrWhiteSpace($userProfile)) {
        $candidates.Add((Join-Path $userProfile ".dotnet\dotnet.exe"))
    }

    foreach ($candidate in ($candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate)) {
            continue
        }

        $installedSdks = & $candidate --list-sdks
        if ($LASTEXITCODE -eq 0 -and ($installedSdks -match "^$MajorVersion\.")) {
            return $candidate
        }
    }

    throw ".NET $MajorVersion SDK is required before running validation."
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

$dotnet8Exe = Resolve-DotNetSdk -MajorVersion 8
$dotnet10Exe = Resolve-DotNetSdk -MajorVersion 10

Push-Location $repoRoot
try {
    Write-Host "Using .NET 8 SDK from $dotnet8Exe for the preserved proof"
    Write-Host "Using .NET 10 SDK from $dotnet10Exe for production"

    Write-Host "Building EnviousWispr app (Release)..."
    Invoke-DotNet -Executable $dotnet8Exe -Arguments @("build", "src/EnviousWispr/EnviousWispr.csproj", "-c", "Release", "--nologo")

    Write-Host "Building smoke harness (Release)..."
    Invoke-DotNet -Executable $dotnet8Exe -Arguments @("build", "src/EnviousWispr.Smoke/EnviousWispr.Smoke.csproj", "-c", "Release", "--nologo")

    Write-Host "Building production WinUI app and module graph (Release, x64)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "src/Production/EnviousWispr.App/EnviousWispr.App.csproj", "-c", "Release", "--nologo", "-p:Platform=x64")

    Write-Host "Validating the WinUI-bundled runtime worker can launch..."
    $bundledWorker = Join-Path $repoRoot "src\Production\EnviousWispr.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\EnviousWispr.RuntimeWorker.exe"
    & $bundledWorker 2>$null
    if ($LASTEXITCODE -ne 2) {
        throw "The WinUI-bundled runtime worker could not launch (exit $LASTEXITCODE)."
    }

    Write-Host "Building native audio UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/audio-uat/EnviousWispr.Audio.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building native hotkey UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/hotkey-uat/EnviousWispr.Hotkey.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building native context and delivery UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/delivery-uat/EnviousWispr.Delivery.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building controlled delivery target UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/delivery-target-uat/EnviousWispr.Delivery.Target.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building limited-token delivery launcher UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/limited-launch-uat/EnviousWispr.LimitedLaunch.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building local polish UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/polish-uat/EnviousWispr.Polish.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building opt-in cloud polish UAT harness (Release; no provider call)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/cloud-polish-uat/EnviousWispr.CloudPolish.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building local Ollama UAT harness (Release; no model pull)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/ollama-uat/EnviousWispr.Ollama.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building native runtime UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/runtime-uat/EnviousWispr.Runtime.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building signed model-delivery UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/model-delivery-uat/EnviousWispr.ModelDelivery.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building privacy-safe observability UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/observability-uat/EnviousWispr.Observability.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building privacy-safe compatibility UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/compatibility-uat/EnviousWispr.Compatibility.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building privacy-safe performance UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/performance-uat/EnviousWispr.Performance.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building native ASR UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/asr-uat/EnviousWispr.Asr.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building native Whisper UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/whisper-uat/EnviousWispr.Whisper.Uat.csproj", "-c", "Release", "--nologo")

    if ($IncludeLocalRuntime) {
        Write-Host "Running contract and local model runtime tests..."
        Invoke-DotNet -Executable $dotnet8Exe -Arguments @("test", "src/EnviousWispr.Tests/EnviousWispr.Tests.csproj", "-c", "Release", "--nologo")
        Write-Host "Running production CPU and CUDA ASR acceptance..."
        Invoke-DotNet -Executable $dotnet10Exe -Arguments @("run", "--project", "tools/asr-uat/EnviousWispr.Asr.Uat.csproj", "-c", "Release", "--no-build")
        Write-Host "Running production CPU and CUDA Whisper acceptance..."
        Invoke-DotNet -Executable $dotnet10Exe -Arguments @("run", "--project", "tools/whisper-uat/EnviousWispr.Whisper.Uat.csproj", "-c", "Release", "--no-build")
    }
    else {
        Write-Host "Running portable contract tests..."
        Invoke-DotNet -Executable $dotnet8Exe -Arguments @("test", "src/EnviousWispr.Tests/EnviousWispr.Tests.csproj", "-c", "Release", "--nologo", "-p:ExcludeLocalOnlyTests=true")
        Write-Host "Local model runtime tests were not requested. Use -IncludeLocalRuntime on a configured Windows machine."
    }

    Write-Host "Running production architecture and foundation tests..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("test", "src/Production/EnviousWispr.Architecture.Tests/EnviousWispr.Architecture.Tests.csproj", "-c", "Release", "--nologo")

    Write-Host "Validation passed."
}
finally {
    Pop-Location
}
