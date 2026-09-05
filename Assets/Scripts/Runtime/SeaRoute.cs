using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sea
{
    // =============================================================
    // SEA · 沿海航线寻路(纯海路 A*)
    //   船的航线必须走海, 不得从陆地直穿到目的地("不是飞船")。
    //   做法: 以 SeaMapGen.LandAt 为唯一陆地真源, 每 0.5° 一格把海洋栅格化,
    //   两港端口"吸附"到最近海格, 8 邻 A*(对角不切角)找一条绕陆的海路,
    //   还原成一段沿海/跨洋的世界坐标折线(含两端港口点)。
    //   结果按 (起港,终港) 缓存, 供 TravelDays 与航行插值复用。
    // =============================================================
    public sealed class SeaRouteData
    {
        public string FromId, ToId;
        public readonly List<Vector3> Pts = new List<Vector3>(); // 世界坐标 y=0, [0]=起港, [last]=终港
        public float Total;      // 折线总长(≈度=世界单位)
        public float Span;       // 起终点直线距离(度, 兜底用)
    }

    public static class SeaRoute
    {
        public const double Step = 0.5;
        static int _cols, _rows;
        static byte[] _sea;          // 1 = 可航行海格(中心不在陆地)
        static bool _built;
        static readonly Dictionary<string, SeaRouteData> _cache = new Dictionary<string, SeaRouteData>();

        public static void ClearCache() { _cache.Clear(); }

        public static SeaRouteData Get(Port a, Port b)
        {
            if (a == null || b == null || a.Id == b.Id) return null;
            string key = a.Id + ">" + b.Id;
            SeaRouteData hit;
            if (_cache.TryGetValue(key, out hit)) return hit;
            var rd = Compute(a, b);
            if (rd == null) return null;
            if (_cache.Count > 256) _cache.Clear();   // 简单淘汰, 避免无限增长
            _cache[key] = rd;
            return rd;
        }

        static void Ensure()
        {
            if (_built) return;
            _built = true;
            _cols = (int)Math.Ceiling((SeaMapGen.Lon1 - SeaMapGen.Lon0) / Step);
            _rows = (int)Math.Ceiling((SeaMapGen.Lat1 - SeaMapGen.Lat0) / Step);
            int n = _cols * _rows;
            _sea = new byte[n];
            for (int y = 0; y < _rows; y++)
            {
                double lat = SeaMapGen.Lat0 + (y + 0.5) * Step;
                int row = y * _cols;
                for (int x = 0; x < _cols; x++)
                {
                    double lon = SeaMapGen.Lon0 + (x + 0.5) * Step;
                    _sea[row + x] = (byte)(SeaMapGen.LandAt(lon, lat) ? 0 : 1);
                }
            }
        }

        static int ColOf(double lon) => (int)Math.Floor((lon - SeaMapGen.Lon0) / Step);
        static int RowOf(double lat) => (int)Math.Floor((lat - SeaMapGen.Lat0) / Step);

        static bool InGrid(int x, int y) => x >= 0 && y >= 0 && x < _cols && y < _rows;
        static int Idx(int x, int y) => y * _cols + x;

        // 港 → 最近的"海格"(从港格向四周扩圈找, 回退半径 4 格=2°), 找不到返回 -1
        static int SnapCell(double lon, double lat)
        {
            Ensure();
            int cx = ColOf(lon), cy = RowOf(lat);
            int best = -1;
            double bestD = double.MaxValue;
            for (int r = 0; r <= 4 && best < 0; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        int x = cx + dx, y = cy + dy;
                        if (!InGrid(x, y) || _sea[Idx(x, y)] == 0) continue;
                        double lc = SeaMapGen.Lon0 + (x + 0.5) * Step;
                        double tc = SeaMapGen.Lat0 + (y + 0.5) * Step;
                        double d = (lc - lon) * (lc - lon) + (tc - lat) * (tc - lat);
                        if (d < bestD) { bestD = d; best = Idx(x, y); }
                    }
                }
            }
            return best;
        }

        static SeaRouteData Compute(Port a, Port b)
        {
            Ensure();
            int sa = SnapCell(a.Lon, a.Lat);
            int sb = SnapCell(b.Lon, b.Lat);
            var rd = new SeaRouteData { FromId = a.Id, ToId = b.Id };
            rd.Span = DistDeg(a.Lon, a.Lat, b.Lon, b.Lat);

            // 兜底: 吸不到海格(极窄水道异常) → 直线海路
            if (sa < 0 || sb < 0)
            {
                rd.Pts.Add(new Vector3(a.Lon, 0f, -a.Lat));
                rd.Pts.Add(new Vector3(b.Lon, 0f, -b.Lat));
                rd.Total = rd.Span;
                return rd;
            }

            List<int> cells;
            if (sa == sb)
            {
                cells = new List<int> { sa };
            }
            else
            {
                cells = Astar(sa, sb);
                if (cells == null)   // 找不到海路(理论不发生) → 直线兜底
                {
                    rd.Pts.Add(new Vector3(a.Lon, 0f, -a.Lat));
                    rd.Pts.Add(new Vector3(b.Lon, 0f, -b.Lat));
                    rd.Total = rd.Span;
                    return rd;
                }
            }

            // 折线 = 起港 → 途经海格中心 → 终港
            rd.Pts.Add(new Vector3(a.Lon, 0f, -a.Lat));
            for (int i = 0; i < cells.Count; i++)
            {
                int c = cells[i];
                int x = c % _cols, y = c / _cols;
                rd.Pts.Add(new Vector3(
                    (float)(SeaMapGen.Lon0 + (x + 0.5) * Step),
                    0f,
                    (float)-(SeaMapGen.Lat0 + (y + 0.5) * Step)));
            }
            rd.Pts.Add(new Vector3(b.Lon, 0f, -b.Lat));

            // 去掉明显共线的中间点(直线走直, 转弯保留), 让航行轨迹干净
            Compress(rd.Pts);

            float tot = 0f;
            for (int i = 1; i < rd.Pts.Count; i++)
                tot += Vector3.Distance(rd.Pts[i - 1], rd.Pts[i]);
            rd.Total = Mathf.Max(0.001f, tot);
            return rd;
        }

        static void Compress(List<Vector3> pts)
        {
            if (pts.Count < 3) return;
            int w = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                if (w >= 2)
                {
                    var A = pts[w - 2]; var B = pts[w - 1]; var C = pts[i];
                    float abx = B.x - A.x, abz = B.z - A.z;
                    float cbx = C.x - B.x, cbz = C.z - B.z;
                    float cross = abx * cbz - abz * cbx;
                    if (cross * cross < 0.0004f * (abx * abx + abz * abz + 1e-6f)
                        && (abx * cbx + abz * cbz) > 0f)   // 同向近似共线 → 吞掉中间点
                    {
                        w--;
                    }
                }
                pts[w++] = pts[i];
            }
            pts.RemoveRange(w, pts.Count - w);
        }

        static float DistDeg(double l1, double t1, double l2, double t2)
        {
            double dx = l2 - l1, dz = t2 - t1;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        // ---------- A*(8 邻, 对角不切角; 允许重复入堆的简单版) ----------
        static List<int> Astar(int start, int goal)
        {
            int n = _cols * _rows;
            var g = new float[n];
            var parent = new int[n];
            for (int i = 0; i < n; i++) { g[i] = float.MaxValue; parent[i] = -1; }
            g[start] = 0f;
            parent[start] = start;

            var pq = new List<int>(4096);
            var pk = new List<float>(4096);
            Push(pq, pk, start, 0f + Heur(start, goal));

            int sx = start % _cols, sy = start / _cols;
            int gx = goal % _cols, gy = goal / _cols;

            int[] dx8 = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] dy8 = { 0, 0, 1, -1, 1, -1, 1, -1 };
            int expand = 0;

            while (pq.Count > 0)
            {
                int cur = Pop(pq, pk);
                if (cur == goal)
                {
                    var rev = new List<int>();
                    int c = cur;
                    while (c != start) { rev.Add(c); c = parent[c]; }
                    rev.Add(start);
                    rev.Reverse();
                    return rev;
                }
                if (++expand > n * 4) return null;   // 保险闸, 防病态地图死循环
                int x = cur % _cols, y = cur / _cols;
                float gcur = g[cur];
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + dx8[k], ny = y + dy8[k];
                    if (!InGrid(nx, ny) || _sea[Idx(nx, ny)] == 0) continue;
                    bool diag = dx8[k] != 0 && dy8[k] != 0;
                    if (diag)   // 对角: 两个正交邻格都必须是海, 否则会切过陆地尖角
                    {
                        if (_sea[Idx(x + dx8[k], y)] == 0 || _sea[Idx(x, y + dy8[k])] == 0) continue;
                    }
                    float step = diag ? (float)(Step * 1.41421356) : (float)Step;
                    float ng = gcur + step;
                    int ni = Idx(nx, ny);
                    if (ng < g[ni] - 0.0001f)
                    {
                        g[ni] = ng;
                        parent[ni] = cur;
                        Push(pq, pk, ni, ng + HeurIdx(nx, ny, gx, gy));
                    }
                }
            }
            return null;
        }

        static float Heur(int cell, int goal)
        {
            int x = cell % _cols, y = cell / _cols;
            int gx = goal % _cols, gy = goal / _cols;
            return HeurIdx(x, y, gx, gy);
        }

        static float HeurIdx(int x, int y, int gx, int gy)
        {
            float dx = (gx - x) * (float)Step, dy = (gy - y) * (float)Step;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        static void Push(List<int> pq, List<float> pk, int c, float f)
        {
            pq.Add(c); pk.Add(f);
            int i = pq.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (pk[p] <= pk[i]) break;
                Swap(pq, pk, p, i);
                i = p;
            }
        }

        static int Pop(List<int> pq, List<float> pk)
        {
            int top = pq[0];
            int last = pq.Count - 1;
            pq[0] = pq[last]; pk[0] = pk[last];
            pq.RemoveAt(last); pk.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, m = i;
                if (l < pq.Count && pk[l] < pk[m]) m = l;
                if (r < pq.Count && pk[r] < pk[m]) m = r;
                if (m == i) break;
                Swap(pq, pk, m, i);
                i = m;
            }
            return top;
        }

        static void Swap(List<int> pq, List<float> pk, int a, int b)
        {
            int tc = pq[a]; pq[a] = pq[b]; pq[b] = tc;
            float tf = pk[a]; pk[a] = pk[b]; pk[b] = tf;
        }
    }
}
