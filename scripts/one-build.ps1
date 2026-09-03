# ONE BUILD, ONE COPY, ONE RECEIPT. Build the app on the Windows machine, mirror it to the single
# folder that is ever launched, and refuse to launch unless the two are provably the same bytes.
#
# WHY THIS EXISTS AT ALL. Between 2026-08-28 and 2026-08-30 this machine accumulated six separate
# EnviousWispr build folders: a run copy, a legacy console build that auto-started from the HKCU Run
# key, two Codex LocalCache copies and two runcopy folders left by earlier sessions. A microphone
# defect was chased for a day across measurements that could not all have come from the same binary,
# and the legacy build's global keyboard hook produced a false defect report of its own on 2026-08-28
# by swallowing every F8 on the machine. Duplicate builds do not merely waste disk, they invalidate
# evidence, and no amount of care in the measurement survives not knowing which binary produced it.
#
# THE CONTRACT THIS SCRIPT ENFORCES:
#   1. dotnet writes to exactly one place, the project's own bin\x64\Release output.
#   2. Exactly one other folder is ever launched, and it is a byte mirror of that output.
#   3. The mirror exists ONLY because a running app holds its DLLs open and the next build then
#      fails. It is a copy, never a second build.
#   4. Nothing is launched, and no measurement is reported, unless the md5 of the built
#      EnviousWispr.App.dll equals the md5 of the mirrored one.
#   5. Every run prints a stamp. A measurement without a stamp is not evidence.
#
# RUN IT FROM ANYWHERE ON THE WINDOWS MACHINE:
#   powershell -ExecutionPolicy Bypass -File scripts\one-build.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\one-build.ps1 -Launch
#   powershell -ExecutionPolicy Bypass -File scripts\one-build.ps1 -Launch -Scan
#
# NO MACHINE PATHS. This is a public repository and its own compliance scan refuses any committed
# file that names whose machine it is, so the tree is found from this script's location and the run
# folder defaults under $env:LOCALAPPDATA.
param(
    [string] $Tree = (Split-Path -Parent $PSScriptRoot),
    [string] $RunFolder = (Join-Path $env:LOCALAPPDATA 'EnviousWispr-run'),
    [string] $Dotnet,
    [switch] $Launch,
    [switch] $Scan,
    [switch] $NoFetch
)

if (-not $Dotnet) {
    # THE USER-LOCAL SDK FIRST. The machine-wide dotnet on this rig is 8.0 and fails every project
    # with NETSDK1045 before anything is compiled.
    $userLocal = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    $Dotnet = if (Test-Path -LiteralPath $userLocal) { $userLocal } else { 'dotnet' }
}

$ErrorActionPreference = 'Continue'
Set-Location $Tree

# COMPUTED WITHOUT AN AUTOLOADED CMDLET, ON PURPOSE. This used to call `Get-FileHash`, and on the
# development machine that cmdlet is NOT FOUND when PowerShell is started with the user's profile -
# which is exactly how this script is invoked. Reproduced both ways:
#
#   powershell -NoProfile -Command "[bool](Get-Command Get-FileHash -EA SilentlyContinue)"   # True
#   powershell           -Command "[bool](Get-Command Get-FileHash -EA SilentlyContinue)"   # False
#
# The profile is breaking module autoloading, almost certainly by replacing rather than appending to
# PSModulePath. Rather than depend on that being fixed, the one check this whole script exists to make
# now uses a type that is always present. Ref: #115.
function Md5($path) {
    if (-not (Test-Path -LiteralPath $path)) { return 'MISSING' }
    $stream = [IO.File]::OpenRead($path)
    try {
        $algorithm = [Security.Cryptography.MD5]::Create()
        try {
            return (-join ($algorithm.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') })).ToUpperInvariant()
        }
        finally { $algorithm.Dispose() }
    }
    finally { $stream.Dispose() }
}

# ---- 1. nothing may be running, or the build silently writes nothing -------------------------
# A LOCKED DLL DOES NOT FAIL LOUDLY ENOUGH. MSBuild reports the copy failure among its output and the
# previous binary stays in place, so the next launch tests the change that was never written. Killing
# first is what makes "the build succeeded" mean the file on disk is new.
$running = Get-Process -Name 'EnviousWispr*' -ErrorAction SilentlyContinue
if ($running) {
    "--- stopping $(($running | Measure-Object).Count) running EnviousWispr process(es) ---"
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

# ---- 2. build from a known commit ------------------------------------------------------------
# FETCH BEFORE REPORTING THE SHA. Reporting HEAD without fetching produced receipts naming a commit
# that was already behind the branch, which is a stamp that certifies the wrong thing.
if (-not $NoFetch) { git fetch --quiet 2>&1 | Out-Null }
$head = git rev-parse --short HEAD
$dirty = (git status --porcelain | Measure-Object).Count

'--- building the app (Release x64) ---'
$log = Join-Path $env:TEMP 'one-build.log'
# -p:Platform=x64 IS LOAD-BEARING. Without it the output lands in bin\Release while everything that
# reads the build reads bin\x64\Release, so a green build mirrors a stale binary.
$process = Start-Process $Dotnet -ArgumentList @(
    'build', 'src\Production\EnviousWispr.App\EnviousWispr.App.csproj',
    '-c', 'Release', '-p:Platform=x64', '--nologo', '-v', 'minimal') `
    -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
try { $process.PriorityClass = 'High' } catch { }
$process.WaitForExit()

# MATCH ANY ANALYSER PREFIX, NOT A LIST OF THEM. An earlier filter named CS, XLS and XAML and let a
# CA2016 error through as a clean build.
$buildErrors = Get-Content $log, "$log.err" -ErrorAction SilentlyContinue |
    Select-String -Pattern ': error [A-Z]+[0-9]+' | Select-Object -First 12
if ($buildErrors) {
    'BUILD ERRORS - nothing was mirrored and nothing was launched:'
    $buildErrors | ForEach-Object { "   $_" }
    exit 1
}
if ((Get-Content $log -ErrorAction SilentlyContinue | Select-String 'Build succeeded' | Measure-Object).Count -lt 1) {
    'STOPPING - the build did not report success, so there is nothing trustworthy to mirror.'
    exit 1
}

# ---- 3. find the one output folder -----------------------------------------------------------
# DISCOVERED, NOT SPELLED OUT. The target framework moniker carries a Windows SDK version that
# changes with the SDK, and a hardcoded one turns an SDK bump into a mysterious empty mirror.
$outputRoot = Join-Path $Tree 'src\Production\EnviousWispr.App\bin\x64\Release'
$built = Get-ChildItem -LiteralPath $outputRoot -Recurse -Filter 'EnviousWispr.App.dll' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) {
    "STOPPING - the build succeeded but no EnviousWispr.App.dll was found under $outputRoot."
    exit 1
}
$buildFolder = $built.DirectoryName

# ---- 4. mirror to the single run folder ------------------------------------------------------
# /MIR DELETES WHAT THE BUILD NO LONGER PRODUCES. A copy that only adds leaves an old DLL beside the
# new ones and the app happily loads it, which is a duplicate build hiding inside the run folder.
"--- mirroring to $RunFolder ---"
$null = robocopy $buildFolder $RunFolder /MIR /NFL /NDL /NJH /NJS /NP
# ROBOCOPY EXIT CODES BELOW 8 ARE SUCCESS. Anything from 8 up is a real failure.
if ($LASTEXITCODE -ge 8) {
    "STOPPING - the mirror failed (robocopy $LASTEXITCODE). Nothing was launched."
    exit 1
}

$builtHash = Md5 (Join-Path $buildFolder 'EnviousWispr.App.dll')
$runHash = Md5 (Join-Path $RunFolder 'EnviousWispr.App.dll')

# ---- 5. optional: prove no other build exists on the machine ---------------------------------
if ($Scan) {
    '--- scanning the user profile for other EnviousWispr binaries ---'
    $strays = Get-ChildItem -LiteralPath $env:USERPROFILE -Recurse -Filter 'EnviousWispr.App.exe' `
        -ErrorAction SilentlyContinue -Force |
        Where-Object { $_.DirectoryName -ne $buildFolder -and $_.DirectoryName -ne $RunFolder }
    if ($strays) {
        'STRAY BUILDS FOUND - do not trust any measurement until these are gone:'
        $strays | ForEach-Object { "   $($_.FullName)" }
    } else {
        '   none outside the build output and the run folder.'
    }
}

# ---- 6. the stamp ----------------------------------------------------------------------------
# THE WHOLE POINT OF THE SCRIPT IS THIS BLOCK. Quote it verbatim beside any result, so a reader can
# tell which binary produced it without taking anyone's word for it.
''
'================ STAMP ================'
"HEAD           $head$(if ($dirty -gt 0) { "  plus $dirty local file(s)" })"
"BUILD FOLDER   $buildFolder"
"BUILD MD5      $builtHash"
"RUN FOLDER     $RunFolder"
"RUN MD5        $runHash"
"BUILT AT       $($built.LastWriteTime)"
'======================================='

# AN EMPTY ANSWER IS NOT AN AGREEMENT, AND IT USED TO READ AS ONE. When `Get-FileHash` was missing
# both sides came back empty, '' -ne '' is false, and this script launched with a stamp reporting
# blank hashes - certifying nothing while looking exactly like a pass. A tool that cannot measure has
# to fail closed, because the whole reason this file exists is that a measurement nobody can tie to a
# binary is worthless. Observed on 2026-09-03; the run happened to be correct, which is the point.
if ([string]::IsNullOrWhiteSpace($builtHash) -or [string]::IsNullOrWhiteSpace($runHash) -or
    $builtHash -eq 'MISSING' -or $runHash -eq 'MISSING') {
    'REFUSING TO LAUNCH - the hashes could not be computed, so nothing above is verified.'
    "   BUILD MD5 '$builtHash'   RUN MD5 '$runHash'"
    exit 1
}

if ($builtHash -ne $runHash) {
    'REFUSING TO LAUNCH - the run copy is not the build. Report no measurement from this machine.'
    exit 1
}

if ($Launch) {
    $exe = Join-Path $RunFolder 'EnviousWispr.App.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        "STOPPING - $exe does not exist."
        exit 1
    }
    "--- launching $exe ---"
    Start-Process -FilePath $exe -WorkingDirectory $RunFolder | Out-Null
}
