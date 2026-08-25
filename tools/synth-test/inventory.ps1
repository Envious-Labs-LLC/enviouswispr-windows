$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$bin = Join-Path $repoRoot "src\EnviousWispr\bin\Release\net8.0-windows"
Add-Type -Path (Join-Path $bin "NAudio.Core.dll")
Add-Type -Path (Join-Path $bin "NAudio.Wasapi.dll")
$enum = New-Object NAudio.CoreAudioApi.MMDeviceEnumerator

Write-Output "=== CAPTURE endpoints ==="
foreach ($d in $enum.EnumerateAudioEndPoints([NAudio.CoreAudioApi.DataFlow]::Capture, [NAudio.CoreAudioApi.DeviceState]::Active)) {
  $isDef = $d.ID -eq $enum.GetDefaultAudioEndpoint([NAudio.CoreAudioApi.DataFlow]::Capture, [NAudio.CoreAudioApi.Role]::Multimedia).ID
  "{0,-3} {1}" -f $(if ($isDef) { "*" } else { " " }), $d.FriendlyName
}
Write-Output "=== RENDER endpoints ==="
foreach ($d in $enum.EnumerateAudioEndPoints([NAudio.CoreAudioApi.DataFlow]::Render, [NAudio.CoreAudioApi.DeviceState]::Active)) {
  $isDef = $d.ID -eq $enum.GetDefaultAudioEndpoint([NAudio.CoreAudioApi.DataFlow]::Render, [NAudio.CoreAudioApi.Role]::Multimedia).ID
  "{0,-3} {1}" -f $(if ($isDef) { "*" } else { " " }), $d.FriendlyName
}

Write-Output "=== SAPI ==="
$voice = New-Object -ComObject SAPI.SpVoice
try { "default voice: " + $voice.GetDescription() } catch {
  try { "default voice: " + $voice.GetDescription } catch { "default voice: (unknown, will use SAPI default)" } }

Write-Output "=== mic privacy (desktop apps) ==="
$reg = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone' -ErrorAction SilentlyContinue
"Value: " + $reg.Value
