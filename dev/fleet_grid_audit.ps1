# fleet_grid_audit.ps1 -- measure the fleet panel's card grid from a screenshot:
# which cards exist, how many text lines each one drew, how wide those lines came out,
# and whether any card content reaches down into the "quan-dui-he-ji" (fleet totals) band.
#
# Why a script: "the ship cards fit / nothing paints over the totals" is a claim about where pixels
# land. Reading a 2560x1440 thumbnail by eye is the instrument that already got this wrong once in
# this project (a sprite was mistaken for a hull). So measure.
#
# The scan runs in C# via Add-Type, not in PowerShell loops: 2560x1440 is 3.7M pixels, and a
# per-pixel PS loop over that took minutes and had to be backgrounded. Same maths, ~100x faster.
#
# WHY EVERY PIXEL CONSTANT IS DERIVED, NOT HARDCODED (the bug this rewrite fixes):
#   The first version of this script hardcoded the pixel columns of a 1600x900 shot (scale 1.25).
#   The shots are now 2560x1440 (scale 2.0), so those constants pointed at the wrong rows --
#   the "totals band" it checked (y 555..618) actually straddled the bottom of row 2's cards, and
#   the bright pixels it counted as "the totals line renders" were card text. A gate that measures
#   the wrong window passes for the wrong reason. So this version reads the canvas scale off the
#   image width and locates the panel by finding the gold card frames, then derives the totals band
#   from the measured card rows -- no layout constants baked in except the ones in SeaHud itself,
#   which are quoted where used.
#
# Geometry it leans on (canvas units, SeaHud constants; the image is the canvas times `scale`):
#   canvas 1280x720 reference, ScaleWithScreenSize match=0.5, 16:9 screen -> scale = W/1280
#   card    232 wide (5 columns) = FleetPanelW 1252, gaps 12
#   card    inner text box = cardW - 2*(FleetCardEdge 2 + FleetCardPadX 10) = 208 at 5 columns.
#           A line that reaches that width WRAPS, which costs a whole extra line -- and the card
#           only has room for five. That is why every band reports its width.
#   totals  rect sits FleetCardsBottom (140) above the panel's bottom edge, 50 tall -> the band is
#           [rowBotLast-50, rowBotLast] in canvas, where rowBotLast is the bottom of the last gold
#           card frame. (SeaHud: fs.offsetMin=(26, 140-50), offsetMax=(-26, 140))
#
# What it reports:
#   1. the card pictures -> how many rows of cards there are.
#   2. the gold rails on a line through the pictures -> the cards' x extents, pitch and gap
#      (overlap between neighbours shows up as a negative gap).
#   3. the vertical gold runs down card 0's rail -> the card rows; dark runs are listed and
#      labelled as panel background so they cannot be mistaken for cards.
#   4. every text line band per card, with its width in canvas vs the wrap limit, and the band
#      list of the worst card in full.
#   5. the totals band: does the last card row's ink reach into it, and does the totals line
#      itself still render.
#
# NOTE: this file is pure ASCII on purpose. PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI,
# and a CJK literal in it can decode into a stray quote/backtick that breaks the parser.
#
# Usage: powershell -File fleet_grid_audit.ps1 <png>

param([Parameter(Mandatory=$true)][string]$Png)

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

public static class ShotScan
{
    const int FR = 87, FG = 112, FB = 138, FTOL = 8;   // opaque card picture frame (0.34,0.44,0.54)
    const float CardsBottom = 140f;                    // SeaHud.FleetCardsBottom: totals band sits on it
    const float TotalsH = 50f;                         // totals rect height
    const float TextInset = 24f;                       // 2*(FleetCardEdge 2 + FleetCardPadX 10)

    static bool Frame(byte[] b, int i)
    {
        return Math.Abs(b[i + 2] - FR) <= FTOL && Math.Abs(b[i + 1] - FG) <= FTOL && Math.Abs(b[i] - FB) <= FTOL;
    }
    // Anything brighter than both the card face (0.085,0.115,0.16 -> sum 92) and the picture mat
    // (11,19,29 -> sum 59). Catches ColTxt, the dim #8FA8C2 cargo line, and gold.
    static bool Ink(byte[] b, int i) { return b[i + 2] + b[i + 1] + b[i] > 200; }
    static bool Gold(byte[] b, int i)
    {
        // ColGoldBtn (0.93,0.68,0.18) -> (237,173,46): the card frame. Must be excluded from the
        // text scan, or every row of the card counts as text (the frame's side rails are in range).
        return Math.Abs(b[i + 2] - 237) <= 14 && Math.Abs(b[i + 1] - 173) <= 14 && Math.Abs(b[i] - 46) <= 20;
    }

    struct Span { public int A, B; public Span(int a, int b) { A = a; B = b; } }

    // One card's ink bands, top to bottom: {yFirst, yLast, xMin, xMax}. A band is a run of rows
    // that each have at least 3 ink pixels somewhere in the card's own span; the gaps between text
    // lines come back as the boundaries between bands. The gold frame is skipped everywhere it is
    // crossed (the side rails are inside the span, the top/bottom rails are outside the y range).
    static List<int[]> Bands(byte[] buf, int st, int x0, int x1, int y0, int y1)
    {
        var tb = new List<int[]>();
        int s2 = -1, xmin = 0, xmax = 0;
        for (int yy = y0; yy <= y1; yy++)
        {
            int n = 0, lo = -1, hi = -1;
            for (int x = x0; x <= x1; x++)
            {
                int i = yy * st + x * 4;
                if (Gold(buf, i)) continue;
                if (Ink(buf, i))
                {
                    n++;
                    if (lo < 0) lo = x;
                    hi = x;
                }
            }
            if (n >= 3)
            {
                if (s2 < 0) { s2 = yy; xmin = lo; xmax = hi; }
                else { if (lo < xmin) xmin = lo; if (hi > xmax) xmax = hi; }
            }
            else if (s2 >= 0) { tb.Add(new int[] { s2, yy - 1, xmin, xmax }); s2 = -1; }
        }
        if (s2 >= 0) tb.Add(new int[] { s2, y1, xmin, xmax });
        return tb;
    }

    public static string Report(string path)
    {
        var bmp = new Bitmap(path);
        int W = bmp.Width, H = bmp.Height;
        var rc = new Rectangle(0, 0, W, H);
        var dt = bmp.LockBits(rc, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int st = dt.Stride;
        var buf = new byte[st * H];
        Marshal.Copy(dt.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(dt);
        bmp.Dispose();

        float sc = W / 1280f;                       // canvas units -> pixels
        var sb = new StringBuilder();
        sb.AppendLine("size " + W + "x" + H + "  canvas scale = " + sc.ToString("F3")
            + (Math.Abs((float)W / H - 16f / 9f) < 0.01f ? "" : "  <== WARN: not 16:9, canvas is not 1280x720"));

        // ---- 1. picture blocks -> which rows of cards exist ----
        // A picture is a filled "frame" colour with a darker mat inset, so a scanline across a
        // picture only has ~2*2 canvas of frame colour per card -- the threshold has to be per
        // picture, not per row, or a 3-ship fleet (3 pictures) falls under it and finds nothing.
        var bands = new List<Span>();
        int s = -1;
        for (int y = 0; y < H; y++)
        {
            int n = 0, b = y * st;
            for (int x = 0; x < W; x++) if (Frame(buf, b + x * 4)) n++;
            if (n >= 6) { if (s < 0) s = y; }
            else if (s >= 0) { bands.Add(new Span(s, y - 1)); s = -1; }
        }
        if (s >= 0) bands.Add(new Span(s, H - 1));

        sb.AppendLine("--- 1. card pictures (opaque " + FR + "," + FG + "," + FB + "): one band per card row ---");
        foreach (var b in bands) sb.AppendLine("  y " + b.A + ".." + b.B + "  h " + (b.B - b.A + 1) + "px = "
            + ((b.B - b.A + 1) / sc).ToString("F0") + " canvas");
        if (bands.Count == 0) { sb.AppendLine("  <== no card pictures -- is any card drawn at all?"); return sb.ToString(); }

        // ---- 2. the cards' x extents, from the gold rails on a line through the pictures ----
        // At the picture's mid height the only gold on the row is the card's two side rails, so the
        // runs pair up two-per-card. Measuring them beats deriving card width from layout constants:
        // if the panel ever changes to 4 columns, this still reads the truth.
        int picMid = (bands[0].A + bands[0].B) / 2;
        var rails = new List<Span>();
        {
            int rs = -1, last = -99;
            for (int x = 0; x < W; x++)
            {
                if (Gold(buf, picMid * st + x * 4))
                {
                    if (rs < 0 || x - last > 4) { if (rs >= 0) rails.Add(new Span(rs, last)); rs = x; }
                    last = x;
                }
            }
            if (rs >= 0) rails.Add(new Span(rs, last));
        }
        var cols = new List<Span>();      // per-card x extent = left rail .. right rail
        for (int i = 0; i + 1 < rails.Count; i += 2)
            cols.Add(new Span(rails[i].A, rails[i + 1].B));
        if (cols.Count == 0) { sb.AppendLine("  <== no gold card rails found -- no card frames?"); return sb.ToString(); }
        int cardW = cols[0].B - cols[0].A + 1;
        float cardWc = cardW / sc;
        float textWc = (cardW - (int)(TextInset * sc)) / sc;
        sb.AppendLine("--- 2. card frames: " + cols.Count + " cards across ---");
        foreach (var c in cols) sb.AppendLine("  x " + c.A + ".." + c.B + "  w " + (c.B - c.A + 1) + "px");
        if (cols.Count > 1)
            sb.AppendLine("  pitch " + (cols[1].A - cols[0].A) + "px = " + ((cols[1].A - cols[0].A) / sc).ToString("F0")
                + " canvas, gap " + (cols[1].A - cols[0].B - 1) + "px = " + ((cols[1].A - cols[0].B - 1) / sc).ToString("F0")
                + " canvas  (overlap would show as a negative gap)");
        sb.AppendLine("  card " + cardWc.ToString("F0") + " canvas wide -> inner text box "
            + textWc.ToString("F0") + " canvas (a line reaching that width wraps)");

        // ---- 3. the card rows, walking the vertical gold rail just inside card 0's left edge ----
        var rows = new List<Span>();
        {
            int x = cols[0].A + 1;                            // 0.5 canvas inside a 2-canvas rail
            int rs = 0, len = 0, prev = -1;
            var runs = new List<object[]>();
            for (int y = 0; y < H; y++)
            {
                int i = y * st + x * 4;
                int key = (buf[i + 2] << 16) | (buf[i + 1] << 8) | buf[i];
                if (key == prev) { len++; }
                else
                {
                    if (len >= (int)(30 * sc)) runs.Add(new object[] { rs, y - 1, prev });
                    prev = key; rs = y; len = 1;
                }
            }
            if (len >= (int)(30 * sc)) runs.Add(new object[] { rs, H - 1, prev });
            sb.AppendLine("--- 3. constant-colour runs down x = " + x + " ---");
            foreach (var r in runs)
            {
                int k = (int)r[2];
                int rr = (k >> 16) & 255, gg = (k >> 8) & 255, bb = k & 255;
                bool gold = Math.Abs(rr - 237) <= 6 && Math.Abs(gg - 173) <= 6 && Math.Abs(bb - 46) <= 6;
                if (gold) rows.Add(new Span((int)r[0], (int)r[1]));
                sb.AppendLine("  y " + r[0] + ".." + r[1] + "  h " + ((int)r[1] - (int)r[0] + 1) + "px = "
                    + (((int)r[1] - (int)r[0] + 1) / sc).ToString("F0") + " canvas  rgb(" + rr + "," + gg + "," + bb + ")"
                    + (gold ? "  <== GOLD (ColGoldBtn) = a card" : "  (panel background, not a card)"));
            }
            sb.AppendLine("  -> " + rows.Count + " card rows of gold");
        }
        if (rows.Count == 0) { sb.AppendLine("  <== no gold card frame found -- cards are not framed?"); return sb.ToString(); }

        // ---- 4. text line bands inside each card ----
        // Scanned per card, over that card's own span (rail to rail minus a pixel of antialiasing).
        // Scanning the whole panel width instead -- the first version's bug -- makes every row "ink"
        // as long as ANY card has ink there, so all five cards fuse into one band and the line count
        // is meaningless.
        sb.AppendLine("--- 4. text lines per card (ink = brighter than the face; gold frame excluded) ---");
        var worst = new List<int[]>();          // the band list of the card with the most lines
        int worstLines = -1, worstBandCount = 0, worstRowBottom = -1;
        int worstX0 = 0, worstX1 = 0, worstY0 = 0, worstY1 = 0;
        string worstAt = "?";
        for (int y = 0; y < rows.Count; y++)
        {
            int y0 = rows[y].A + 3, y1 = rows[y].B - 3;
            var row = new StringBuilder();
            for (int c = 0; c < cols.Count; c++)
            {
                int x0 = cols[c].A + 2, x1 = cols[c].B - 2;
                var tb = Bands(buf, st, x0, x1, y0, y1);

                // The picture block is ink too (its mat and any art in it are brighter than the
                // face), so it shows up as one very tall band. A text line is never that tall.
                int lines = 0;
                foreach (var t in tb) if (t[1] - t[0] + 1 <= 30 * sc) lines++;
                row.Append("  c").Append(c).Append(':').Append(lines);
                if (lines > worstLines || (lines == worstLines && tb.Count > worstBandCount))
                {
                    worstLines = lines; worstBandCount = tb.Count; worst = tb;
                    worstX0 = x0; worstX1 = x1; worstY0 = y0; worstY1 = y1;
                    worstAt = "row " + y + " card " + c;
                }
                if (y == rows.Count - 1 && tb.Count > 0)
                {
                    int lastInk = tb[tb.Count - 1][1];
                    if (lastInk > worstRowBottom) worstRowBottom = lastInk;
                }
            }
            sb.Append("  row ").Append(y).Append(':').AppendLine(row.ToString());
        }
        // Do NOT read this count as pass/fail on its own: it includes the ship-name line, which sits
        // in the name plate ABOVE the stat block and is not one of the stat block's lines. The two
        // signals that actually decide "does the card's text fit" are printed below -- whether any
        // band reaches the wrap limit (a line that wraps costs a whole extra line), and whether the
        // band heights stay uniform (a truncated line is clipped mid-glyph, so it comes out short).
        sb.AppendLine("  worst card = " + worstLines + " text bands [" + worstAt + "]"
            + "  (the first is the ship-name line, above the stat block)");
        sb.AppendLine("  bands of that card (x " + worstX0 + ".." + worstX1 + ", y " + worstY0 + ".." + worstY1 + "):");
        int hMin = int.MaxValue, hMax = 0, atLimit = 0;
        foreach (var t in worst)
        {
            bool isPic = t[1] - t[0] + 1 > 30 * sc;
            if (!isPic)
            {
                int h = t[1] - t[0] + 1;
                if (h < hMin) hMin = h;
                if (h > hMax) hMax = h;
                if ((t[3] - t[2] + 1) / sc >= textWc - 8) atLimit++;
            }
        }
            int wpx = t[3] - t[2] + 1;
            float wc = wpx / sc;
            bool pic = t[1] - t[0] + 1 > 30 * sc;
            sb.AppendLine("    y " + t[0] + ".." + t[1] + "  h " + (t[1] - t[0] + 1) + "px  x " + t[2] + ".." + t[3]
                + "  w " + wpx + "px = " + wc.ToString("F0") + " canvas"
                + (wc >= textWc - 8 ? "  <== AT THE WRAP LIMIT" : "")
                + (pic ? "  (picture block, not a text line)" : ""));
        }
        sb.AppendLine("  -> line heights " + hMin + ".." + hMax + "px"
            + (hMax - hMin <= 4 ? " (uniform -> no line is clipped mid-glyph)" : "  <== WARN: uneven, a line looks cut")
            + ", bands at the wrap limit: " + atLimit
            + (atLimit == 0 ? " (nothing wraps -> the line count is the intended one)" : "  <== WARN: a line wrapped and cost an extra line"));

        // ---- 5. the totals band, derived from the measured card rows ----
        // In canvas the band's top edge sits (140-50) above the panel's bottom; the panel's bottom is
        // 140 below the last card row's bottom. So in canvas: [rowBotLast-50, rowBotLast].
        int rowBotLast = rows[rows.Count - 1].B;
        int bandTopPx = rowBotLast;                                          // canvas y == rowBotCanvas
        int bandBotPx = H - (int)Math.Round((((H - rowBotLast) / sc) - TotalsH) * sc);
        int panelL = cols[0].A, panelR = cols[cols.Count - 1].B;
        sb.AppendLine("--- 5. fleet totals band (canvas [" + (((H - rowBotLast) / sc) - TotalsH).ToString("F0")
            + ", " + ((H - rowBotLast) / sc).ToString("F0") + "] -> png y " + bandTopPx + ".." + bandBotPx + ") ---");
        int tot = 0;
        for (int yy = bandTopPx; yy <= bandBotPx && yy < H; yy++)
            for (int x = panelL; x <= panelR; x++) if (Ink(buf, yy * st + x * 4)) tot++;
        sb.AppendLine("  ink px inside the band: " + tot + (tot > 200 ? "  (the totals line renders)" : "  <== WARN: band looks empty"));
        if (worstRowBottom >= 0)
            sb.AppendLine("  lowest ink row of the last card row = " + worstRowBottom
                + "  ->  " + (worstRowBottom < bandTopPx
                    ? "CLEAR of the totals band by " + (bandTopPx - worstRowBottom) + "px = "
                      + ((bandTopPx - worstRowBottom) / sc).ToString("F0") + " canvas"
                    : "OVERLAPS the totals band"));
        return sb.ToString();
    }
}
'@

Write-Output ("FILE  : " + (Split-Path $Png -Leaf))
Write-Output ([ShotScan]::Report((Resolve-Path $Png).Path))
