$fs = [System.IO.File]::OpenRead("$PWD/spikes/s1/audio/clip94.wav")
$br = New-Object System.IO.BinaryReader($fs)
$len = [int64]$fs.Length
$fs.Position = 44
$cnt = $len - 44
$bytes = $br.ReadBytes($cnt)
$n = [int]($cnt / 2)
$s = [int16[]]::new($n)
[System.Buffer]::BlockCopy($bytes, 0, $s, 0, $cnt)
$fs.Close()
$sr = 16000
$win = [int]($sr / 10)
$cnt2 = [int](($n - $win) / $win)
$rms = New-Object float[] $cnt2
for ($i = 0; $i -lt $cnt2; $i++) {
  $sum = 0.0
  $base = $i * $win
  for ($j = 0; $j -lt $win; $j += 8) { $x = [float]$s[$base + $j]; $sum += $x * $x }
  $rms[$i] = [math]::Sqrt($sum / ($win / 8)) / 32768
}
$gaps = [System.Collections.Generic.List[object]]::new()
$i = 0
while ($i -lt $cnt2) {
  if ($rms[$i] -lt 0.01) {
    $j = $i
    while ($j -lt $cnt2 -and $rms[$j] -lt 0.01) { $j++ }
    if ($j - $i -ge 4) { $gaps.Add([pscustomobject]@{ start = $i * 0.1; end = $j * 0.1; len = ($j - $i) * 0.1 }) }
    $i = $j
  } else { $i++ }
}
$dur = $n / $sr
"duration {0:N1}s" -f $dur
"silence gaps >=400ms: {0}" -f $gaps.Count
$gaps | Select-Object -First 15 | ForEach-Object { "  {0,6:N1}s - {1,6:N1}s  ({2:N1}s)" -f $_.start, $_.end, $_.len }
$bounds = @(0.0)
foreach ($g in $gaps) { $bounds += [double]$g.start }
$bounds += $dur
$maxC = 0.0; $minC = 999.0
for ($k = 0; $k -lt $bounds.Count - 1; $k++) {
  $c = $bounds[$k + 1] - $bounds[$k]
  if ($c -gt $maxC) { $maxC = $c }
  if ($c -lt $minC) { $minC = $c }
}
"chunks if split at gaps: {0}, max {1:N1}s, min {2:N1}s" -f ($bounds.Count - 1), $maxC, $minC
