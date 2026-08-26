param(
    [switch]$IncludeLocalRuntime
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    Write-Host "Building EnviousWispr app (Release)..."
    dotnet build src/EnviousWispr/EnviousWispr.csproj -c Release --nologo

    Write-Host "Building smoke harness (Release)..."
    dotnet build src/EnviousWispr.Smoke/EnviousWispr.Smoke.csproj -c Release --nologo

    if ($IncludeLocalRuntime) {
        Write-Host "Running contract and local model runtime tests..."
        dotnet test src/EnviousWispr.Tests/EnviousWispr.Tests.csproj -c Release --nologo
    }
    else {
        Write-Host "Running portable contract tests..."
        dotnet test src/EnviousWispr.Tests/EnviousWispr.Tests.csproj -c Release --nologo -p:ExcludeLocalOnlyTests=true
        Write-Host "Local model runtime tests were not requested. Use -IncludeLocalRuntime on a configured Windows machine."
    }

    Write-Host "Validation passed."
}
finally {
    Pop-Location
}
