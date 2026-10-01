# btn_color_audit.ps1 -- read the market table's button fill colours out of a shot PNG.
#
# Why measure instead of eyeballing: "the buy buttons are now the same colour as the bottom
# bar" and "the sell buttons turn yellow when I'm holding the good" are claims about pixels,
# and a 16px CJK glyph sits in the middle of every button -- exactly the kind of thing an eye
# turns into a wrong answer (it happened once already in this project: a sprite was mistaken
# for the ship and "55px off-centre" was reported when the real figure was 1.8px).
#
# Method: draw a horizontal line just inside the top edge of a button (below the border,
# above the glyph box, so the fill reads as one unbroken run), then report the LONGEST run of
# constant colour inside each button's predicted rectangle. The run's colour IS the fill.
#
# Geometry. SeaHud builds the HUD in canvas units against a CanvasScaler of
# referenceResolution 1280x720 / ScaleWithScreenSize / match 0.5, so at the shot gate's
# 1600x900 the canvas scale factor is exactly 1.25. All of SeaHud's numbers below are canvas
# units; the screen/PNG numbers are those x1.25. (Getting this wrong is why an earlier pass
# of this script scanned the wrong rows entirely -- the buttons came out 1.25x wider and 1.25x
# further right than predicted.)
#
#   market panel  RectAt(anchor/pivot(0,0), pos(10,124), size 900x452)
#                 -> screen x [12.5,1137.5], y [155,720]
#   viewport      offsetMin(4,4) offsetMax(-4,-64)  -> screen y [160,640]
#   row i         anchoredPosition(0,-(26i+2)), height 24  -> row top = 637.5 - 32.5i
#   row button    anchoredPosition(x,1), size 54x20 -> screen x = 17.5 + 1.25*x, w 67.5, h 25
#                 PNG y = [261.25+32.5i, 286.25+32.5i]
#     buy  x_local 530/588/646 -> screen x 680.0 / 752.5 / 825.0
#     sell x_local 706/764/822 -> screen x 900.0 / 972.5 / 1045.0
#   dock bar      RectAt(anchor/pivot(0.5,0), pos(0,10), size 1252x102); buttons size(wd,40)
#                 at pos(x_i,10) -> screen y [25,75]; dock button #2 centre 532.5 w 147.5
#
# Colour mapping is calibrated by the reading itself: ColBtn (0.14,0.40,0.50) reads back as
# (36,102,128) = round(value*255), no gamma step. So ColGoldBtn (0.93,0.68,0.18) must read
# (237,173,46). The script prints that expectation instead of hiding it in an assertion.
#
# The 持有 column (x_local 396..452 -> screen x 512.5..582.5) is read too: its numbers are
# drawn in ColGold text, so counting gold-ish pixels there says WHICH rows are actually
# holding the good. Without it, "row 0 and 1 hold the good" would be a deduction from the
# sort code rather than a measurement -- and the whole point is to check the colour against
# the real holdings.
#
# Usage: powershell -File btn_color_audit.ps1 <png> [-Row 0] [-RowsToScan 4]

param(
    [Parameter(Mandatory=$true)][string]$Png,
    [int]$Row = 0,
    [int]$RowsToScan = 4
)

Add-Type -AssemblyName System.Drawing

$TEAL = @(36, 102, 128)
$GOLD = @(237, 173, 46)

function Get-LongestRun {
    param($Buf, $Stride, $Y, $X0, $X1, $MinLen = 10)
    $best = $null
    $curKey = -2; $curStart = $X0; $len = 0
    $runs = New-Object System.Collections.ArrayList
    for ($x = $X0; $x -le $X1; $x++) {
        $i = $Y * $Stride + $x * 4
        $key = ([int]$Buf[$i + 2] -shl 16) -bor ([int]$Buf[$i + 1] -shl 8) -bor [int]$Buf[$i]
        if ($key -eq $curKey) { $len++ }
        else {
            if ($len -ge $MinLen) { [void]$runs.Add([pscustomobject]@{ X0 = $curStart; Len = $len; Key = $curKey }) }
            $curKey = $key; $curStart = $x; $len = 1
        }
    }
    if ($len -ge $MinLen) { [void]$runs.Add([pscustomobject]@{ X0 = $curStart; Len = $len; Key = $curKey }) }
    $best = $runs | Sort-Object -Property Len -Descending | Select-Object -First 1
    if (-not $best) { return [pscustomobject]@{ R = -1; G = -1; B = -1; Len = 0; Runs = $runs } }
    return [pscustomobject]@{
        R = ($best.Key -shr 16) -band 255; G = ($best.Key -shr 8) -band 255; B = $best.Key -band 255
        Len = $best.Len; Runs = $runs
    }
}

function Show-Color($c) {
    if ($c.R -lt 0) { return "(no run >= min length -- rect likely misplaced)" }
    $tag = ""
    if ($c.R -eq $TEAL[0] -and $c.G -eq $TEAL[1] -and $c.B -eq $TEAL[2]) { $tag = "  <== TEAL (ColBtn)" }
    elseif ($c.R -eq $GOLD[0] -and $c.G -eq $GOLD[1] -and $c.B -eq $GOLD[2]) { $tag = "  <== GOLD (ColGoldBtn)" }
    return ("rgb=({0,3},{1,3},{2,3}) longest-run {3}px{4}" -f $c.R, $c.G, $c.B, $c.Len, $tag)
}

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

Write-Output "FILE  : $(Split-Path $Png -Leaf)  (${imgW}x${imgH})"
if ($imgW -ne 1600 -or $imgH -ne 900) {
    Write-Output "WARN  : lines are placed for 1600x900 @ scale 1.25 -- this image is ${imgW}x${imgH}, readings are meaningless"
}
Write-Output "EXPECT: ColBtn teal = (36,102,128)   ColGoldBtn gold = (237,173,46)"

# ---- reference colours from the bottom action bar, same frame, no colour-space assumption
$refTeal = Get-LongestRun $buf $stride 845 459 606 20     # dock button "本港行情" = ColBtn
Write-Output ("REF   : dock market button (teal reference) -> " + (Show-Color $refTeal))

$buyX = @(680, 752, 825)
$sellX = @(900, 972, 1045)
$btnW = 67

Write-Output ""
for ($k = 0; $k -lt $RowsToScan; $k++) {
    $i = $Row + $k
    $y = [int][Math]::Round(265 + 32.5 * $i)

    # 持有 column: how many gold-ish text pixels (ColGold 255,217,115) are in this row's cell.
    # Window x 512..588 is clear of the data columns either side (售价 ends at 502, 成本 starts at 592).
    $held = 0
    for ($yy = $y; $yy -le $y + 18; $yy++) {
        $rowBase = $yy * $stride
        for ($x = 512; $x -le 588; $x++) {
            $i2 = $rowBase + $x * 4
            $b = $buf[$i2]; $g = $buf[$i2 + 1]; $r = $buf[$i2 + 2]
            if ($r -ge 180 -and $g -ge 150 -and $g -le 240 -and $b -le 160) { $held++ }
        }
    }

    $buy = Get-LongestRun $buf $stride $y $buyX[0] ($buyX[0] + $btnW) 10
    $s1 = Get-LongestRun $buf $stride $y $sellX[0] ($sellX[0] + $btnW) 10
    $s2 = Get-LongestRun $buf $stride $y $sellX[1] ($sellX[1] + $btnW) 10
    $s3 = Get-LongestRun $buf $stride $y $sellX[2] ($sellX[2] + $btnW) 10

    Write-Output ("row {0} (PNG y={1})   held-column gold text pixels: {2,4}" -f $i, $y, $held)
    Write-Output ("    buy1  : " + (Show-Color $buy))
    Write-Output ("    sell1 : " + (Show-Color $s1))
    Write-Output ("    sell2 : " + (Show-Color $s2))
    Write-Output ("    sell3 : " + (Show-Color $s3))
}

# Label colour, for the row asked for. Counted over the whole key rect rather than sampled on
# a line: CJK strokes are antialiased, so no single line sees a long run of pure glyph colour.
# What DOES separate the two candidates is brightness -- white (255,255,255) leaves hundreds of
# near-white pixels, the dark brown label (0.15,0.09,0.04)->(38,23,10) leaves near-dark ones.
# Worth checking because the fill change and the label change are separate edits: gold fill left
# paired with white text is the low-contrast failure mode, and it would look "mostly right".
$gy0 = [int][Math]::Round(262 + 32.5 * $Row); $gy1 = [int][Math]::Round(286 + 32.5 * $Row)
Write-Output ""
Write-Output "--- label pixels, row $Row (PNG y $gy0..$gy1) ---"
foreach ($pair in @(@("buy1 ", $buyX[0]), @("sell1", $sellX[0]))) {
    $white = 0; $dark = 0
    for ($yy = $gy0; $yy -le $gy1; $yy++) {
        $rowBase = $yy * $stride
        for ($x = $pair[1]; $x -lt ($pair[1] + $btnW); $x++) {
            $i2 = $rowBase + $x * 4
            $b = $buf[$i2]; $g = $buf[$i2 + 1]; $r = $buf[$i2 + 2]
            if ($r -ge 200 -and $g -ge 200 -and $b -ge 200) { $white++ }
            elseif ($r -le 90 -and $g -le 70 -and $b -le 60) { $dark++ }
        }
    }
    Write-Output ("  {0} x {1}..{2}:  near-white {3,5}px   near-dark {4,5}px" -f $pair[0], $pair[1], ($pair[1] + $btnW), $white, $dark)
}
