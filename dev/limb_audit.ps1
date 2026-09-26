# limb_audit.ps1 -- count lit pixels OUTSIDE the globe's silhouette in a shot PNG.
#
# Why this is exact rather than heuristic: SeaPlay's camera sits on the surface normal
# through the globe centre and looks straight at it, so the sphere's centre projects to
# the EXACT screen centre and the silhouette is a circle of radius
#     r_px = (H/2) * tan(limb) / tan(fov/2),   limb = asin(R / (R + dist))
# with H=900, fov=60, R=57.2958, dist=78  ->  364.2 px  (this matched the in-engine
# probe's independently measured "limb half-angle 25.1 deg at camera distance 135.3").
#
# Anything lit beyond that circle hangs in space: nothing on the globe can occlude it,
# because the view ray never touches the sphere. That is the "floating dots" artifact.
#
# IMPORTANT -- this only means anything for the WHOLE-GLOBE framing (dist=78). Zoomed in
# (dist=28) the silhouette is ~707 px while the screen half-diagonal is only 918 px, so
# the disk runs off the screen and NO artifact can be visible at all; the script says so
# instead of silently reporting a misleading "clean". Pass -Dist to match the shot.
#
# HUD that legitimately sits outside the circle is excluded by rectangle, and the
# threshold is high enough to ignore the background wash behind the HUD.
# Usage: powershell -File limb_audit.ps1 <png> [-Dist 78] [-Threshold 110] [-PadPx 8]

param(
    [Parameter(Mandatory=$true)][string]$Png,
    [double]$Dist = 78.0,
    [int]$Threshold = 110,
    [int]$PadPx = 8
)

Add-Type -AssemblyName System.Drawing

$CX = 800.0; $CY = 450.0
# Globe radius R=57.2958, vertical fov 60 deg, image 1600x900 -> focal length in px.
$R_GLOBE = 57.2958
$F_PX = 450.0 / [Math]::Tan(30.0 * [Math]::PI / 180.0)          # 779.4 px
$LIMB = [Math]::Asin($R_GLOBE / ($R_GLOBE + $Dist)) * 180.0 / [Math]::PI
$R_PX = $F_PX * [Math]::Tan($LIMB * [Math]::PI / 180.0)
$SCREEN_DIAG = [Math]::Sqrt(800.0 * 800.0 + 450.0 * 450.0)      # 918.8 px to the corner

# Rectangles occupied by HUD (x0,y0,x1,y1) -- allowed to be lit. Kept generously wide
# so the HUD's own glow/wash is never mistaken for an artifact.
$Hud = @(
    @(0,    0,   360, 175),   # logo + title plaque (+ glow); the plaque reaches ~y=150
    @(440,  0,   960, 100),   # top status box (its last line's text reaches x=946)
    @(1120, 0,  1600,  85),   # trial/goal box
    @(1120, 60, 1600, 485),   # voyage log panel
    @(0,    745,1600, 900)    # bottom hint + button bars
)

$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Png).Path)
$imgW = $bmp.Width; $imgH = $bmp.Height
$rect = New-Object System.Drawing.Rectangle 0, 0, $imgW, $imgH
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                      [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$stride = $data.Stride
$buf = New-Object byte[] ($stride * $imgH)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
$bmp.UnlockBits($data)
$bmp.Dispose()

$limit = $R_PX + $PadPx
$hits = New-Object System.Collections.ArrayList
$maxR = 0.0
$rowsScanned = 0

for ($y = 0; $y -lt $imgH; $y++) {
    $rowsScanned++
    $row = $y * $stride
    $dy = $y - $CY
    for ($x = 0; $x -lt $imgW; $x++) {
        $dx = $x - $CX
        $r = [Math]::Sqrt($dx * $dx + $dy * $dy)
        if ($r -le $limit) { continue }
        $skip = $false
        foreach ($hr in $Hud) {
            if ($x -ge $hr[0] -and $x -lt $hr[2] -and $y -ge $hr[1] -and $y -lt $hr[3]) { $skip = $true; break }
        }
        if ($skip) { continue }
        $i = $row + $x * 4
        $b = $buf[$i]; $g = $buf[$i + 1]; $rch = $buf[$i + 2]
        $m = [Math]::Max($rch, [Math]::Max($g, $b))
        if ($m -le $Threshold) { continue }
        if ($r -gt $maxR) { $maxR = $r }
        [void]$hits.Add(@($x, $y, [int]$r, $rch, $g, $b))
    }
}

$name = Split-Path $Png -Leaf
Write-Output "FILE      : $name  (${imgW}x${imgH}, rows scanned $rowsScanned)"
Write-Output "SILHOUETTE: dist=$Dist  limb=$([Math]::Round($LIMB,2))deg  centre=($CX,$CY) radius=$([Math]::Round($R_PX,1))px  ->  flagged when r > ${limit}px"
if ($R_PX -gt $SCREEN_DIAG) {
    Write-Output "VACUOUS   : limb is off-screen (radius $([Math]::Round($R_PX,1))px > corner $([Math]::Round($SCREEN_DIAG,1))px) -- this framing cannot show the artifact, 0 hits would mean nothing"
}
if ($hits.Count -eq 0) {
    Write-Output "RESULT    : CLEAN -- no lit pixel outside the silhouette (threshold $Threshold)"
} else {
    Write-Output "RESULT    : $($hits.Count) lit pixels outside, farthest r=$([int]$maxR)px"
    $hits | Sort-Object -Property @{Expression={-$_[2]}} | Select-Object -First 20 | ForEach-Object {
        Write-Output ("  ({0},{1}) r={2} rgb=({3},{4},{5})" -f $_[0], $_[1], $_[2], $_[3], $_[4], $_[5])
    }
}
