param(
    [string]$RemoteHost = "mac",
    [string]$RemoteEvalRoot = "~/Developer/EnviousLabs/EnviousWispr/scripts/eval",
    [string]$Destination = (Join-Path $env:USERPROFILE "AgentArtifacts\enviouswispr-windows\benchmarks\mac-corpus"),
    [switch]$SkipIntlSealed
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$slices = @(
    "german-parakeet-stress",
    "lang-stress"
)
if (-not $SkipIntlSealed) {
    $slices += "runs/intl-sealed-2026-11/speech"
}

$sliceArgs = $slices -join " "
Write-Host "Syncing Mac benchmark corpus ($sliceArgs) from ${RemoteHost}:${RemoteEvalRoot} to $Destination ..."

# Use a native OS pipe / file redirect via cmd.exe rather than a PowerShell pipeline so Windows PowerShell 5.1
# never decodes and re-encodes binary tar bytes as text.
$tempArchive = Join-Path ([IO.Path]::GetTempPath()) ("mac-corpus-sync-" + [guid]::NewGuid().ToString("N") + ".tar")
try {
    $remoteCmd = "cd $RemoteEvalRoot && tar -cf - $sliceArgs"
    & cmd.exe /d /c "ssh `"$RemoteHost`" `"$remoteCmd`" > `"$tempArchive`""
    if ($LASTEXITCODE -ne 0) {
        throw "Remote tar export over ssh failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $tempArchive -PathType Leaf) -or (Get-Item -LiteralPath $tempArchive).Length -le 0) {
        throw "Synced tar archive was missing or empty: $tempArchive"
    }

    & tar.exe -C $Destination -xf $tempArchive
    if ($LASTEXITCODE -ne 0) {
        throw "Local tar extraction failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $tempArchive) {
        Remove-Item -LiteralPath $tempArchive -Force -ErrorAction SilentlyContinue
    }
}

$manifestCount = @(Get-ChildItem -LiteralPath $Destination -Recurse -File -Include "manifest.jsonl", "cases.jsonl", "cases.sealed.jsonl").Count
$wavCount = @(Get-ChildItem -LiteralPath $Destination -Recurse -File -Filter "*.wav").Count
if ($manifestCount -le 0 -or $wavCount -le 0) {
    throw "Corpus sync verification failed: found $manifestCount manifest(s) and $wavCount WAV file(s) in $Destination."
}

Write-Host "Synced $manifestCount manifest(s) and $wavCount WAV file(s) into $Destination."
