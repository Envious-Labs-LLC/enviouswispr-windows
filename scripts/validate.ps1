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

function Invoke-DotNetTest {
    # A GREEN TEST RUN MUST BE A WHOLE TEST RUN, AND `dotnet test` WILL NOT TELL YOU THAT.
    #
    # When the test host dies, the run is aborted and the summary still begins with the word
    # `Passed!` - beside a total for the tests that happened to finish. Measured four times in one
    # session on the development machine: totals of 1064, 1049, 1064 and 1069 against a complete
    # suite of 1127, each printed under `Passed!`. Only the exit code disagreed. The same shape took
    # 46 tests down with a credential-store failure earlier and reported 694 of 740 as a pass.
    #
    # SO THE EXIT CODE IS NOT ENOUGH ON ITS OWN EITHER. It happens to be correct today, which is
    # exactly what makes it a bad single guard: nothing about the output would look different if a
    # future runner swallowed it, and the failure mode is a silent pass. Two independent checks, and
    # the second one does not depend on the first being right.
    #
    # THE EXPECTED COUNT IS DISCOVERED, NEVER WRITTEN DOWN. `--list-tests` enumerates what the
    # assembly actually contains, including one entry per theory case, so it matches the run's own
    # Total exactly - verified 1127 against 1127. A floor would not do: a floor of 700 still lets 34
    # tests vanish, which is the objection recorded on #79 against the first attempt at this.
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string]$Project,
        [string[]]$ExtraArguments = @()
    )

    $common = @("test", $Project, "-c", "Release", "--nologo") + $ExtraArguments
    # ASSIGNED FIRST, THEN BRANCHED. A pipeline reports its LAST command's status, so testing
    # $LASTEXITCODE after piping the output anywhere would report the pipe.
    #
    # AND STDERR IS MERGED UNDER 'Continue', WHICH IS LOAD-BEARING. This script runs with
    # ErrorActionPreference = Stop, and under Stop a native command writing ANYTHING to stderr raises
    # a terminating NativeCommandError at the moment of capture - before any of the checks below can
    # run. Measured: a crashing test host printed its abort line to stderr and the gate died here with
    # `NativeCommandError`, so the run failed for the right reason with the wrong explanation, twice.
    #
    # THE SAME TRAP IS ALREADY DOCUMENTED IN `Resolve-Python` IN THIS FILE, and knowing about it did
    # not stop it being reintroduced fifty lines away. Restored in a finally, so a throw inside the
    # capture cannot leave the rest of the gate running with errors non-terminating.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Executable @common 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    $output | ForEach-Object { Write-Host $_ }

    $aborted = @($output | Select-String -Pattern 'Test host process crashed|The active test run was aborted')
    if ($aborted.Count -gt 0) {
        throw ("The test run was ABORTED, so its summary describes only the tests that finished: " +
            ($aborted[0].ToString().Trim()))
    }

    if ($exitCode -ne 0) {
        throw "dotnet $($common -join ' ') failed with exit code $exitCode"
    }

    # --no-build BECAUSE THE RUN ABOVE JUST BUILT IT, and re-building here would let a discovery
    # against different bytes agree with a run it never described.
    $ErrorActionPreference = 'Continue'
    try {
        $listed = & $Executable @($common + @("--no-build", "--list-tests")) 2>&1
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    $discovered = @($listed | Select-String -Pattern '^\s{4}\S').Count
    $reported = 0
    foreach ($match in ($output | Select-String -Pattern 'Total:\s*(\d+)' -AllMatches)) {
        foreach ($item in $match.Matches) {
            $reported += [int]$item.Groups[1].Value
        }
    }

    if ($discovered -le 0) {
        throw ("Test discovery returned no tests for $Project, so the count this gate compares " +
            "against is meaningless. Fix the discovery rather than removing the check.")
    }

    if ($reported -ne $discovered) {
        throw ("The test run reported $reported tests and the assembly contains $discovered. A run " +
            "that executes fewer tests than exist still prints a pass line for the ones it reached.")
    }

    Write-Host "  verified: $reported of $discovered tests ran."
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

    Write-Host "Building the model-manifest authoring tool (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/model-manifest/EnviousWispr.ModelManifest.Tool.csproj", "-c", "Release", "--nologo")

    # VERIFY EVERY BUNDLED MANIFEST FROM SOURCE, not only through the embedded copy the tests read.
    # A manifest edited by hand without its digest recomputed would otherwise be found by a user.
    Get-ChildItem -Path "models/manifests" -Filter "*.json" | ForEach-Object {
        Write-Host "Verifying bundled manifest $($_.Name)..."
        Invoke-DotNet -Executable $dotnet10Exe -Arguments @("run", "--no-build", "-c", "Release", "--project", "tools/model-manifest/EnviousWispr.ModelManifest.Tool.csproj", "--", "verify", $_.FullName)
    }

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

    # BUILT BY THE GATE THOUGH THE GATE CANNOT RUN IT. It needs a model pack and long recordings that
    # are not in the repository, so it is a measuring instrument rather than a check. Compiling it here
    # is what stops it rotting into something that cannot be pointed at the next question.
    Write-Host "Building live-preview cost spike (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/asr-incremental-spike/EnviousWispr.Asr.Incremental.Spike.csproj", "-c", "Release", "--nologo")

    Write-Host "Building production WinUI end-to-end journey UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/app-journey-uat/EnviousWispr.AppJourney.Uat.csproj", "-c", "Release", "--nologo")

    if ($IncludeLocalRuntime) {
        Write-Host "Running contract and local model runtime tests..."
        Invoke-DotNetTest -Executable $dotnet8Exe -Project "src/EnviousWispr.Tests/EnviousWispr.Tests.csproj"
        Write-Host "Running production CPU and CUDA ASR acceptance..."
        Invoke-DotNet -Executable $dotnet10Exe -Arguments @("run", "--project", "tools/asr-uat/EnviousWispr.Asr.Uat.csproj", "-c", "Release", "--no-build")
        Write-Host "Running production CPU and CUDA Whisper acceptance..."
        Invoke-DotNet -Executable $dotnet10Exe -Arguments @("run", "--project", "tools/whisper-uat/EnviousWispr.Whisper.Uat.csproj", "-c", "Release", "--no-build")
        Write-Host "Running the production WinUI public-fixture journey..."
        Invoke-DotNet -Executable $dotnet10Exe -Arguments @("run", "--project", "tools/app-journey-uat/EnviousWispr.AppJourney.Uat.csproj", "-c", "Release", "--no-build")
        Write-Host "Running the production WinUI English Parakeet journey..."
        Invoke-DotNet -Executable $dotnet10Exe -Arguments @("run", "--project", "tools/app-journey-uat/EnviousWispr.AppJourney.Uat.csproj", "-c", "Release", "--no-build", "--", "--english-parakeet")
        # ARMS THE HEAD-START CHECK, WHICH IS THE POINT OF ADDING IT. The streaming head start polls
        # every 500 ms and every other journey holds a recording for a fraction of that, so nothing
        # here had ever reached the first poll: the feature was abandoned 511 ms into every recording
        # from the day it shipped and the gate stayed green throughout. A guard nobody runs is not a
        # guard, so this costs the four seconds it takes to hold a recording long enough to matter.
        Write-Host "Running the streaming head-start journey..."
        Invoke-DotNet -Executable $dotnet10Exe -Arguments @("run", "--project", "tools/app-journey-uat/EnviousWispr.AppJourney.Uat.csproj", "-c", "Release", "--no-build", "--", "--english-parakeet", "--head-start")
    }
    else {
        Write-Host "Running portable contract tests..."
        Invoke-DotNetTest -Executable $dotnet8Exe -Project "src/EnviousWispr.Tests/EnviousWispr.Tests.csproj" -ExtraArguments @("-p:ExcludeLocalOnlyTests=true")
        Write-Host "Local model runtime tests were not requested. Use -IncludeLocalRuntime on a configured Windows machine."
    }

    Write-Host "Running production architecture and foundation tests..."
    Invoke-DotNetTest -Executable $dotnet10Exe -Project "src/Production/EnviousWispr.Architecture.Tests/EnviousWispr.Architecture.Tests.csproj"

    Write-Host "Validation passed."
}
finally {
    Pop-Location
}
