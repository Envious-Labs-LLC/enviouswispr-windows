param(
    [switch]$IncludeLocalRuntime
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

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

    Write-Host "Building native audio UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/audio-uat/EnviousWispr.Audio.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building native hotkey UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/hotkey-uat/EnviousWispr.Hotkey.Uat.csproj", "-c", "Release", "--nologo")

    Write-Host "Building native runtime UAT harness (Release)..."
    Invoke-DotNet -Executable $dotnet10Exe -Arguments @("build", "tools/runtime-uat/EnviousWispr.Runtime.Uat.csproj", "-c", "Release", "--nologo")

    if ($IncludeLocalRuntime) {
        Write-Host "Running contract and local model runtime tests..."
        Invoke-DotNet -Executable $dotnet8Exe -Arguments @("test", "src/EnviousWispr.Tests/EnviousWispr.Tests.csproj", "-c", "Release", "--nologo")
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
