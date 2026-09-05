$ErrorActionPreference = "Stop"

$dev = "E:\cc\sea\dev"
$seahud = "E:\cc\sea\Assets\Scripts\Runtime\SeaHud.cs"
$seaplay = "E:\cc\sea\Assets\Scripts\Runtime\SeaPlay.cs"

# ---------- backup ----------
Copy-Item $seahud "$dev\SeaHud.cs.bak" -Force
Copy-Item $seaplay "$dev\SeaPlay.cs.bak" -Force

# ---------- recolor paper-body hex colors (dark-bg tuned -> parchment ink) ----------
$f4 = "$dev\frag_s4.txt"
$t = [System.IO.File]::ReadAllText($f4)
$map = @{
  "#9FB6CF" = "#7A5F33"
  "#7FE0FF" = "#1A4553"
  "#BFE0B0" = "#3C6B48"
  "#FFD76A" = "#9E4A24"
  "#FFD0A0" = "#9E4A24"
  "#FFE0A0" = "#9E4A24"
  "#7FE0A0" = "#3C6B48"
  "#FFB0A0" = "#9E4A24"
  "#FFA0A0" = "#A03A24"
  "#E8D59A" = "#1A4553"
}
foreach ($k in $map.Keys) { $t = $t.Replace($k, $map[$k]) }
$enc = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($f4, $t, $enc)

# ---------- generic line-range replacement ----------
function Replace-Lines($path, $start, $end, $fragPath) {
  $text = [System.IO.File]::ReadAllText($path)
  $nl = "`r`n"
  if (-not $text.Contains($nl)) { $nl = "`n" }
  $all = $text -split "`r?`n"
  $ft = [System.IO.File]::ReadAllText($fragPath)
  $ins = $ft -split "`r?`n"
  $out = New-Object System.Collections.Generic.List[string]
  $i = 0
  for (; $i -lt ($start - 1) -and $i -lt $all.Length; $i++) { $out.Add($all[$i]) }
  foreach ($l in $ins) { $out.Add($l) }
  for ($i = $end; $i -lt $all.Length; $i++) { $out.Add($all[$i]) }
  $res = [string]::Join($nl, $out)
  [System.IO.File]::WriteAllText($path, $res, $enc)
}

# ---------- SeaHud: high region first (bottom-up), then low field block ----------
Replace-Lines $seahud 1741 2095 "$dev\frag_he.txt"
Replace-Lines $seahud 67 80 "$dev\frag_hd.txt"

# ---------- SeaPlay: bottom-up S4 -> S3 -> S2 -> S1 ----------
Replace-Lines $seaplay 1192 1315 "$dev\frag_s4.txt"
Replace-Lines $seaplay 1168 1190 "$dev\frag_s3.txt"
Replace-Lines $seaplay 1099 1100 "$dev\frag_s2.txt"
Replace-Lines $seaplay 1087 1095 "$dev\frag_s1.txt"

Write-Output "SPLICE DONE"
