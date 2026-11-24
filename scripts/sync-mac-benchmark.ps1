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

$remoteCmd = "cd $RemoteEvalRoot && tar -cf - $sliceArgs"
& ssh $RemoteHost $remoteCmd | & tar -C $Destination -xf -
if ($LASTEXITCODE -ne 0) {
    throw "Mac benchmark corpus sync failed with exit code $LASTEXITCODE."
}

$manifestCount = @(Get-ChildItem -LiteralPath $Destination -Recurse -File -Include "manifest.jsonl", "cases.jsonl", "cases.sealed.jsonl").Count
$wavCount = @(Get-ChildItem -LiteralPath $Destination -Recurse -File -Filter "*.wav").Count
Write-Host "Synced $manifestCount manifest(s) and $wavCount WAV file(s) into $Destination."
