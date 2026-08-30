# Build and test the Windows tree ON the Windows machine, at high priority so the work reaches the
# performance cores.
#
# TRACKED IN THE REPOSITORY, and it was not. It lived only on one machine, so a note describing its
# safeguards described something nobody else had, and `scripts/validate.ps1` still ran the suite
# unfiltered. A safeguard that exists on one laptop is a safeguard the project does not have.
#
# TWO LANES, TWO RECEIPTS, AND THAT IS THE WHOLE POINT OF THE SHAPE. Measured 2026-08-29:
# `WindowsCredentialApiKeyStoreTests` does not merely fail on that machine, it sometimes takes 46
# other tests down with it, and the run then reports `Failed: 1, Passed: 693, Total: 694` in under a
# second against a suite of 740. A truncated run reports the tests it DID execute, so its pass line is
# true about a smaller suite and reads exactly like a full pass. Running the credential class in its
# own process means it can fail without deciding anything about the other 734.
#
# NEITHER LANE MAY BE CALLED A FULL PASS ON ITS OWN. `.claude/rules/validation.md` says a filtered
# green is never full runtime proof, so both receipts are printed and both are named.

param(
    [string] $Tree = 'C:\Users\saura\agent-workspace\enviouswispr-windows',
    [string] $Dotnet = 'C:\Users\saura\.dotnet\dotnet.exe'
)

$ErrorActionPreference = 'Continue'
$testProject = 'src\Production\EnviousWispr.Architecture.Tests\EnviousWispr.Architecture.Tests.csproj'
$isolated = 'WindowsCredentialApiKeyStore'

Set-Location $Tree
"HEAD: $(git rev-parse --short HEAD)  plus $((git status --porcelain | Measure-Object).Count) local file(s)"

function RunHigh($argline, $out) {
    $process = Start-Process $Dotnet -ArgumentList $argline -NoNewWindow -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    # WITHOUT THIS IT LANDS ON THE EFFICIENCY CORES. Windows puts a process started by a background
    # service into EcoQoS, and every build run over SSH was scheduled onto the E-cores until this
    # was measured.
    try { $process.PriorityClass = 'High' } catch { }
    $process.WaitForExit()
    $process.Refresh()
    return $process.ExitCode
}

function Summarise($log, $lane) {
    # BUILD ERRORS FROM THE TEST PROJECT REACH NO VERDICT OTHERWISE. A test project that does not
    # compile prints no Passed and no Failed line, so the summary went out BLANK and read like a
    # hang. The build step above only builds the app.
    $errors = Get-Content $log, "$log.err" -ErrorAction SilentlyContinue |
        Select-String -Pattern ': error ' | Select-Object -First 8
    if ($errors) {
        "   [$lane] BUILD ERRORS:"
        $errors | ForEach-Object { "      $_" }
        return
    }

    Get-Content $log -ErrorAction SilentlyContinue |
        Select-String '\[FAIL\]|Passed!|Failed!' | ForEach-Object { "   [$lane] $_" }
    Get-Content $log -ErrorAction SilentlyContinue |
        Select-String -Pattern 'Error Message' -Context 0, 2 |
        Select-Object -First 4 | ForEach-Object { "      $_" }
}

'--- building the app (Release x64) ---'
$null = RunHigh "build src\Production\EnviousWispr.App\EnviousWispr.App.csproj -c Release -p:Platform=x64 --nologo -v minimal" "$env:TEMP\v-build.log"
# GATE ON THE OUTCOME IN THE LOG, NEVER THE EXIT CODE. Start-Process -PassThru with redirected output
# returns an empty ExitCode here, so an `-ne 0` test is true for a build that plainly succeeded and
# had already linked the app.
$buildErrors = Get-Content "$env:TEMP\v-build.log", "$env:TEMP\v-build.log.err" -ErrorAction SilentlyContinue |
    Select-String -Pattern ': error ' | Select-Object -First 12
if ($buildErrors) {
    'BUILD ERRORS:'
    $buildErrors | ForEach-Object { "   $_" }
    exit 1
}

if ((Get-Content "$env:TEMP\v-build.log" | Select-String 'Build succeeded' | Measure-Object).Count -lt 1) {
    'STOPPING - the build did not succeed, so the suite would test nothing.'
    exit 1
}
Get-Content "$env:TEMP\v-build.log" | Select-String 'Build succeeded|Time Elapsed' | ForEach-Object { "   $_" }

# HOW MANY TESTS EXIST, which a run's own summary cannot say. This is the control that turns "the
# suite passed" into a claim about a known number rather than about whatever ran.
$null = RunHigh "test $testProject -c Release --nologo --list-tests" "$env:TEMP\v-list.log"
# READ THE HITS, NEVER THE COUNT. The first pattern here was `^\s+EnviousWispr\.`, which also
# matched the ten build lines that read `  EnviousWispr.Audio -> C:\...dll` - so it reported 750
# discovered against 734 executed and cried SHORT RUN on a run that was complete. A control that
# accuses a healthy suite gets switched off, which is worse than not having one.
$names = Get-Content "$env:TEMP\v-list.log" -ErrorAction SilentlyContinue |
    Select-String '^\s+EnviousWispr\.Architecture\.Tests\.' |
    Where-Object { $_.Line -notmatch '->' } |
    ForEach-Object { $_.Line.Trim() }
$discovered = ($names | Measure-Object).Count
$expected = ($names | Where-Object { $_ -notmatch $isolated } | Measure-Object).Count
"--- $discovered tests discovered, $expected outside the isolated class ---"

'--- lane 1: the suite, without the class that truncates it ---'
$null = RunHigh "test $testProject -c Release --nologo --filter FullyQualifiedName!~$isolated" "$env:TEMP\v-test.log"
Summarise "$env:TEMP\v-test.log" 'suite'

# CARRY THE EXPECTED VALUE, NOT A FLOOR. An earlier version warned below 700, which still let 34
# tests disappear without a word. The count is known, so the check is an equality.
$ran = Get-Content "$env:TEMP\v-test.log" -ErrorAction SilentlyContinue |
    Select-String 'Total:\s+(\d+)' | Select-Object -Last 1
# A THEORY IS ONE DISCOVERED NAME AND SEVERAL EXECUTED CASES, so the run can legitimately exceed
# the discovered count and only a SHORTFALL is a problem. The failure being caught is a run that
# stopped early, not one that expanded.
if ($ran -and [int]$ran.Matches[0].Groups[1].Value -lt $expected) {
    "   SHORT RUN: $($ran.Matches[0].Groups[1].Value) tests executed, at least $expected expected."
    '   This is an instrument failure rather than a result. Run it again before believing either.'
}

'--- lane 2: the isolated class, in its own process ---'
$null = RunHigh "test $testProject -c Release --nologo --filter FullyQualifiedName~$isolated" "$env:TEMP\v-isolated.log"
Summarise "$env:TEMP\v-isolated.log" $isolated

''
'BOTH LANES ARE RECEIPTS AND NEITHER IS A FULL PASS ON ITS OWN.'
"Lane 1 is the suite minus $isolated. Lane 2 is that class alone. Quote both."
