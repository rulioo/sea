using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Sea;

namespace Sea.MapPreview
{
    // =============================================================
    // SEA · 离屏地图预览
    //   把 SeaMapGen 烘出的世界地图写成 PNG(dev 上两级 map_preview.png),
    //   并逐个打印"港口距最近陆地"。可用参数单环校对/窗口查看:
    //     dotnet run -c Release           全地图
    //     dotnet run -c Release -- africa 只看非洲(其港口距离校验)
    //     dotnet run -c Release -- africa -10,60,20,70  非洲+窗口ASCII(西北欧一带)
    //   窗口形如 lon0,lon1,lat0,lat1。
    // =============================================================
    class PortRow
    {
        public string id; public string name; public string areaId;
        public float lat; public float lon; public int size;
    }

    static class Program
    {
        const string OutPng = @"..\..\map_preview.png";
        static readonly (byte, byte, byte)[] AreaColors = new (byte, byte, byte)[]
        {
            (120,170,255), (255,205,90), (255,150,110), (255,120,150),
            (170,230,120), (120,255,210), (255,120,255), (255,255,120),
        };

        static void Main(string[] args)
        {
            string ring = null, mode = null;
            double? w0 = null, w1 = null, w2 = null, w3 = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--") continue;
                if (args[i] == "edges") { mode = "edges"; continue; }
                if (args[i] == "cross") { mode = "cross"; continue; }
                if (args[i] == "probe") { mode = "probe"; continue; }
                if (double.TryParse(args[i], out var v))
                {
                    if (w0 == null) w0 = v; else if (w1 == null) w1 = v;
                    else if (w2 == null) w2 = v; else w3 = v;
                }
                else ring = args[i];
            }
            bool windowed = w0.HasValue && w1.HasValue && w2.HasValue && w3.HasValue;
            if (mode == "edges")
            {
                PrintEdges(ring, w0.Value, w1.Value, w2.Value, w3.Value);
                return;
            }
            if (mode == "cross")
            {
                CrossReport(ring);
                return;
            }
            if (mode == "probe")
            {
                var ps = new List<double>();
                foreach (var x in args) if (double.TryParse(x, out var v)) ps.Add(v);
                SeaMapGen.Build(4, ring);
                for (int k = 0; k + 1 < ps.Count; k += 2)
                    Console.WriteLine($"  ({ps[k],7:0.0},{ps[k + 1],6:0.0}) → 距岸 {SeaMapGen.CoastDistDeg(ps[k], ps[k + 1]):+0.00;-0.00}° ({(SeaMapGen.CoastDistDeg(ps[k], ps[k + 1]) < 0 ? "陆" : "海")})  LandAt={SeaMapGen.LandAt(ps[k], ps[k + 1], ring)}");
                return;
            }

            var jsonOpts = new JsonSerializerOptions { IncludeFields = true };
            string dataFile = Path.Combine(AppContext.BaseDirectory, "Data", "Ports.json");
            if (!File.Exists(dataFile)) dataFile = "Data/Ports.json";
            var ports = JsonSerializer.Deserialize<List<PortRow>>(File.ReadAllText(dataFile), jsonOpts);
            Console.WriteLine($"港口 {ports.Count} 座 | 陆地环: {(ring ?? "全部 " + string.Join("/", SeaMapGen.RingNames))}");

            // 单环校验时, 只烘该环, 检查落在它身上(距离<20°)的港口
            SeaMapGen.Build(4, ring);
            var rows = new List<(int idx, double dist)>();
            for (int i = 0; i < ports.Count; i++)
            {
                var p = ports[i];
                double d = SeaMapGen.CoastDistDeg(p.lon, p.lat);
                if (ring != null && Math.Abs(d) > 20) continue;
                rows.Add((i, d));
            }
            rows.Sort((a, b) => Math.Abs(a.dist).CompareTo(Math.Abs(b.dist)));
            Console.WriteLine("港口距岸(度, 正=海上 负=内陆);  目标 <1.5°:");
            Console.WriteLine($"{"港口",-11}{"距岸",8}{"规模",4}  坐标");
            Console.WriteLine(new string('-', 46));
            int bad = 0;
            foreach (var r in rows)
            {
                var p = ports[r.idx];
                bool flag = Math.Abs(r.dist) > 1.5;
                if (flag) bad++;
                Console.WriteLine($"{p.id,-11}{r.dist,7:0.00}{p.size,4}  ({p.lon,7:0.0},{p.lat,6:0.0}){(flag ? "  ←" : "")}");
            }
            Console.WriteLine(bad == 0 ? "→ 该环港口全部贴岸 ✓" : $"→ 离岸>1.5°: {bad} 座");

            PrintAsciiMap(ports, ring, windowed ? w0.Value : SeaMapGen.Lon0,
                windowed ? w1.Value : SeaMapGen.Lon1, windowed ? w2.Value : SeaMapGen.Lat0,
                windowed ? w3.Value : SeaMapGen.Lat1);

            // 全地图 PNG
            var rgba = SeaMapGen.Build();
            var buf = (byte[])rgba.Clone();
            DrawPortDots(buf, ports);
            string outPath = Path.GetFullPath(OutPng);
            WritePng(buf, SeaMapGen.W, SeaMapGen.H, outPath);
            Console.WriteLine($"已写出全地图预览: {outPath}");
        }

        static void DrawPortDots(byte[] buf, List<PortRow> ports)
        {
            int w = SeaMapGen.W, h = SeaMapGen.H;
            var areaIdx = new Dictionary<string, int>();
            foreach (var p in ports) if (!areaIdx.ContainsKey(p.areaId)) areaIdx[p.areaId] = areaIdx.Count;
            foreach (var p in ports)
            {
                int cx = (int)Math.Round((p.lon - SeaMapGen.Lon0) * SeaMapGen.PxPerDeg);
                int cy = (h - 1) - (int)Math.Round((p.lat - SeaMapGen.Lat0) * SeaMapGen.PxPerDeg);
                var col = AreaColors[areaIdx[p.areaId] % AreaColors.Length];
                int r = 3 + (p.size == 3 ? 1 : 0);
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (dx * dx + dy * dy > r * r + 1) continue;
                        int x = cx + dx, y = cy + dy;
                        if (x < 0 || y < 0 || x >= w || y >= h) continue;
                        int o = (y * w + x) * 4;
                        bool edge = dx * dx + dy * dy > (r - 1) * (r - 1);
                        buf[o] = edge ? (byte)0 : col.Item1;
                        buf[o + 1] = edge ? (byte)0 : col.Item2;
                        buf[o + 2] = edge ? (byte)0 : col.Item3;
                        buf[o + 3] = 255;
                    }
            }
        }

        static void PrintAsciiMap(List<PortRow> ports, string onlyRing,
            double lon0, double lon1, double lat0, double lat1)
        {
            const int cols = 150;
            int rows = (int)Math.Round(cols * (lat1 - lat0) / (lon1 - lon0));
            if (rows < 8 || rows > 90) rows = Math.Min(90, Math.Max(8, rows));
            double dx = (lon1 - lon0) / cols, dy = (lat1 - lat0) / rows;
            var grid = new char[cols * rows];
            for (int j = 0; j < rows; j++)
                for (int i = 0; i < cols; i++)
                {
                    double lon = lon0 + (i + 0.5) * dx;
                    double lat = lat1 - (j + 0.5) * dy;
                    double d = SeaMapGen.CoastDistDeg(lon, lat);
                    grid[j * cols + i] = d < 0 ? (onlyRing == null ? '#' : '*')
                        : (d <= 0.5 ? '~' : (d <= 1.6 ? ':' : '.'));
                }
            foreach (var p in ports)
            {
                if (p.lon < lon0 || p.lon > lon1 || p.lat < lat0 || p.lat > lat1) continue;
                int i = (int)((p.lon - lon0) / dx);
                int j = (int)((lat1 - p.lat) / dy);
                if (i >= 0 && i < cols && j >= 0 && j < rows)
                    grid[j * cols + i] = '@';
            }
            Console.WriteLine($"--- ASCII [{lon0:0},{lon1:0}]x[{lat0:0},{lat1:0}] (顶=北; {(onlyRing == null ? '#' : '*')}陆 ~岸边水 :浅海 .深海 @港口) ---");
            for (int j = 0; j < rows; j++)
                Console.WriteLine(new string(grid, j * cols, cols));
        }

        static void CrossReport(string ring)
        {
            if (ring == null) { Console.WriteLine("cross 模式需要环名"); return; }
            var pts = SeaMapGen.GetRingPts(ring);
            if (pts == null) { Console.WriteLine($"无此环: {ring}"); return; }
            int n = pts.Length, bad = 0;
            for (int a = 0; a < n; a++)
            {
                int a2 = (a + 1) % n;
                for (int b = a + 1; b < n; b++)
                {
                    if (b == a || b == a2) continue;
                    int b2 = (b + 1) % n;
                    if (a2 == b || a == b2) continue;   // 共享端点的相邻边跳过
                    if (SegCross(pts[a][0], pts[a][1], pts[a2][0], pts[a2][1],
                                 pts[b][0], pts[b][1], pts[b2][0], pts[b2][1]))
                    {
                        Console.WriteLine($"  交叉: 边[{a}]({pts[a][0]:0.0},{pts[a][1]:0.0})→({pts[a2][0]:0.0},{pts[a2][1]:0.0})  ×  边[{b}]({pts[b][0]:0.0},{pts[b][1]:0.0})→({pts[b2][0]:0.0},{pts[b2][1]:0.0})");
                        bad++;
                    }
                }
            }
            Console.WriteLine(bad == 0 ? "→ 无自相交 ✓" : $"→ 自相交边对 {bad} 处");
        }
        static bool SegCross(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
        {
            double o1 = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            double o2 = (bx - ax) * (dy - ay) - (by - ay) * (dx - ax);
            double o3 = (dx - cx) * (ay - cy) - (dy - cy) * (ax - cx);
            double o4 = (dx - cx) * (by - cy) - (dy - cy) * (bx - cx);
            return (o1 * o2 < 0) && (o3 * o4 < 0);
        }

        // 把某环的顶点折线画到 ASCII 窗口上(数字=顶点序号%10), 目视找错位/交叉
        static void PrintEdges(string ring, double lon0, double lon1, double lat0, double lat1)
        {
            if (ring == null) { Console.WriteLine("edges 模式需要环名"); return; }
            var pts = SeaMapGen.GetRingPts(ring);
            if (pts == null) { Console.WriteLine($"无此环: {ring}"); return; }
            const int cols = 150;
            int rows = Math.Max(30, Math.Min(90, (int)Math.Round(cols * (lat1 - lat0) / (lon1 - lon0))));
            double dx = (lon1 - lon0) / cols, dy = (lat1 - lat0) / rows;
            var g = new char[cols * rows];
            for (int j = 0; j < rows * cols; j++) g[j] = '.';
            void mark(double lon, double lat, char c)
            {
                int i = (int)((lon - lon0) / dx), j = (int)((lat1 - lat) / dy);
                if (i >= 0 && i < cols && j >= 0 && j < rows && (g[j * cols + i] == '.' || char.IsDigit(g[j * cols + i]))) g[j * cols + i] = c;
            }
            for (int k = 0; k < pts.Length; k++)
            {
                double a0 = pts[k][0], b0 = pts[k][1];
                double a1 = pts[(k + 1) % pts.Length][0], b1 = pts[(k + 1) % pts.Length][1];
                int steps = (int)Math.Ceiling((Math.Abs(a1 - a0) + Math.Abs(b1 - b0)) / 0.7);
                for (int s = 0; s <= steps; s++)
                    mark(a0 + (a1 - a0) * s / steps, b0 + (b1 - b0) * s / steps, (char)('0' + k % 10));
            }
            Console.WriteLine($"--- {ring} 边线 [{lon0:0},{lon1:0}]x[{lat0:0},{lat1:0}] 数字=该段顶点号%10 (顶=北) ---");
            for (int j = 0; j < rows; j++) Console.WriteLine(new string(g, j * cols, cols));
        }

        // ============ 极简 PNG 写入(zlib stored, 零依赖) ============
        static readonly byte[] PngSig = { 137, 80, 78, 71, 13, 10, 26, 10 };
        static uint[] _crc;

        static void WritePng(byte[] rgba, int w, int h, string path)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            fs.Write(PngSig, 0, 8);
            byte[] ihdr = new byte[13];
            WriteU32(ihdr, 0, (uint)w);
            WriteU32(ihdr, 4, (uint)h);
            ihdr[8] = 8; ihdr[9] = 6;
            WriteChunk(fs, "IHDR", ihdr);
            int stride = w * 4;
            var raw = new byte[h * (stride + 1)];
            int wp = 0;
            for (int ty = 0; ty < h; ty++)
            {
                int srcRow = (h - 1 - ty) * stride;
                raw[wp++] = 0;
                Array.Copy(rgba, srcRow, raw, wp, stride);
                wp += stride;
            }
            WriteChunk(fs, "IDAT", ZlibStored(raw));
            WriteChunk(fs, "IEND", Array.Empty<byte>());
        }

        static byte[] ZlibStored(byte[] data)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0x78); ms.WriteByte(0x01);
            int pos = 0;
            while (pos < data.Length)
            {
                int n = Math.Min(65535, data.Length - pos);
                bool final = pos + n >= data.Length;
                ms.WriteByte(final ? (byte)1 : (byte)0);
                ms.WriteByte((byte)(n & 255)); ms.WriteByte((byte)(n >> 8));
                ms.WriteByte((byte)(~n & 255)); ms.WriteByte((byte)((~n >> 8) & 255));
                ms.Write(data, pos, n);
                pos += n;
            }
            WriteU32BE(ms, Adler32(data));
            return ms.ToArray();
        }

        static void WriteChunk(Stream s, string type, byte[] data)
        {
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            var lenB = new byte[4];
            WriteU32(lenB, 0, (uint)data.Length);
            s.Write(lenB, 0, 4);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            if (_crc == null) BuildCrc();
            uint crc = 0xFFFFFFFF;
            crc = Crc(crc, t, t.Length);
            crc = Crc(crc, data, data.Length) ^ 0xFFFFFFFF;
            var crcB = new byte[4];
            WriteU32(crcB, 0, crc);
            s.Write(crcB, 0, 4);
        }

        static void BuildCrc()
        {
            _crc = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                _crc[i] = c;
            }
        }
        static uint Crc(uint c, byte[] buf, int len)
        {
            for (int i = 0; i < len; i++) c = _crc[(c ^ buf[i]) & 0xFF] ^ (c >> 8);
            return c;
        }
        static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (var v in data) { a = (a + v) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }
        static void WriteU32(byte[] b, int o, uint v)
        { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        static void WriteU32BE(Stream s, uint v)
        { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    }
}
