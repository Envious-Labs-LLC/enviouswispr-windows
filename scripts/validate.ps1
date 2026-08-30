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

# BEFORE THE BUILDS, WHICH IS THE WHOLE POINT OF WHERE THIS SITS. An invalid escape in a regular
# string literal is a compile error, and the compiler reports it ten minutes into a CI round as one
# error among however many the failure cascades into. This reads C# the way the compiler does and
# names the file, the line and the escape in about a second.
#
# IT WAS ARMED ONLY AFTER BEING PROVED, and the proof changed it. Run with no arguments it read its
# file list from argv, scanned nothing, and printed "0 problems" - so wiring it up as it stood would
# have shipped a gate that passes forever and catches nothing. It now walks the repository, reports
# how many files it scanned, and exits non-zero if that number is ever zero.
Write-Host "Checking C# string escapes..."

# A COMMAND NAMED PYTHON IS NOT NECESSARILY PYTHON. On Windows, `python` resolves by default to a
# Microsoft Store stub that prints an advert and exits 9009. Running it and reporting the failure as
# a check failure blames the wrong thing entirely, so the interpreter is PROBED rather than assumed.
function Resolve-Python {
    # THE STUB DOES NOT MERELY FAIL, IT WRITES TO STDERR - and a native command writing to stderr is
    # a terminating NativeCommandError under this script's ErrorActionPreference, so the probe
    # designed to tolerate a missing Python was itself ending the run. Measured on the dev machine,
    # where `python` is the Microsoft Store alias.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        foreach ($name in @('python', 'python3')) {
            $command = Get-Command $name -ErrorAction SilentlyContinue
            if ($null -eq $command) { continue }
            $version = & $command.Source --version 2>&1 | Out-String
            if ($LASTEXITCODE -eq 0 -and $version -match 'Python 3') { return $command.Source }
        }
    }
    finally {
        $ErrorActionPreference = $previous
    }

    return $null
}

$python = Resolve-Python
if ($null -eq $python) {
    # SKIPPED LOUDLY, NOT SILENTLY, and only because the check is enforced elsewhere. CI runs on a
    # runner that has Python, so the rule is kept there; a developer machine without it gets a line
    # it cannot miss rather than a green run that quietly proved less. This is the same allowance
    # validation.md already makes for model-dependent checks, with the same obligation to say so.
    Write-Host "  NOT RUN: no working Python 3 on this machine, so the C# escape check was skipped."
    Write-Host "  It is enforced in CI. Install Python 3 to run it here."
}
else {
    & $python (Join-Path $repoRoot "scripts\check-cs-escapes.py")
    if ($LASTEXITCODE -ne 0) {
        throw "C# string escape check failed."
    }
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

    # RAISING PRIORITY HERE WAS TRIED AND MEASURED NOT TO WORK; see #68. Three findings, kept
    # because each one refutes an obvious next attempt:
    #
    #   Priority is NOT inherited. A child started immediately after setting this script to High
    #   came back Normal, so raising the script's own priority reaches nothing.
    #
    #   `Start-Process -PassThru` reports an EMPTY ExitCode on this host, with redirection and
    #   without it, so switching to it would cost this helper the exit code it exists to check. A
    #   raw Process object does report it.
    #
    #   AND SETTING IT ON THE dotnet PROCESS IS STILL NOT ENOUGH, which is why the attempt was
    #   reverted rather than kept. `dotnet build` spawns MSBuild worker nodes to do the compiling,
    #   they do not inherit the priority either, and sampling them during a real run showed Normal.
    #   Opting out of EcoQoS properly needs SetProcessInformation with a power-throttling state
    #   applied to the tree, not a priority class on one process.
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

    # RUN, NOT MERELY BUILD, AND IN BOTH LANES. This harness proves the hardest promise the product
    # makes - that no dictated content crosses the network - and it was compiled by this gate and
    # executed by nothing, here or in CI. A privacy gate that only has to compile is not a gate.
    # It needs no model, no microphone and no GPU: it stands up a loopback listener and reads back a
    # file, so there is no reason for it to sit behind -IncludeLocalRuntime.
    Write-Host "Running the privacy-safe observability acceptance..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("run", "--project", "tools/observability-uat/EnviousWispr.Observability.Uat.csproj", "-c", "Release", "--no-build")

    Write-Host "Building privacy-safe compatibility UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/compatibility-uat/EnviousWispr.Compatibility.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building privacy-safe performance UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/performance-uat/EnviousWispr.Performance.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building native ASR UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/asr-uat/EnviousWispr.Asr.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building native Whisper UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/whisper-uat/EnviousWispr.Whisper.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building production WinUI end-to-end journey UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/app-journey-uat/EnviousWispr.AppJourney.Uat.csproj", "-c", "Release", "--nologo")

    if ($IncludeLocalRuntime) {
        Write-Host "Running contract and local model runtime tests..."
        Invoke-DotNet -Executable $dotnet8Exe -Arguments @("test", "src/EnviousWispr.Tests/EnviousWispr.Tests.csproj", "-c", "Release", "--nologo")
        Write-Host "Running production CPU and CUDA ASR acceptance..."
        Invoke-DotNet -Executable $dotnet10Exe -Arguments @("run", "--project", "tools/asr-uat/EnviousWispr.Asr.Uat.csproj", "-c", "Release", "--no-build")
        Write-Host "Running production CPU and CUDA Whisper acceptance..."
        Invoke-DotNet -Executable $dotnet10Exe -Arguments @("run", "--project", "tools/whisper-uat/EnviousWispr.Whisper.Uat.csproj", "-c", "Release", "--no-build")
        Write-Host "Running the production WinUI public-fixture journey..."
        Invoke-DotNet -Executable $dotnet10Exe -Arguments @("run", "--project", "tools/app-journey-uat/EnviousWispr.AppJourney.Uat.csproj", "-c", "Release", "--no-build")
        Write-Host "Running the production WinUI English Parakeet journey..."
        Invoke-DotNet -Executable $dotnet10Exe -Arguments @("run", "--project", "tools/app-journey-uat/EnviousWispr.AppJourney.Uat.csproj", "-c", "Release", "--no-build", "--", "--english-parakeet")
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
