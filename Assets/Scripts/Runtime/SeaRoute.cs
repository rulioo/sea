using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sea
{
    // =============================================================
    // SEA · 沿海航线寻路(纯海路 A*)
    //   船的航线必须走海, 不得从陆地直穿到目的地("不是飞船")。
    //   做法: 以 SeaMapGen.LandAt 为唯一陆地真源, 每 0.25° 一格把海洋栅格化,
    //   两港端口"吸附"到最近海格, 8 邻 A*(对角不切角)找一条绕陆的海路,
    //   还原成一段沿海/跨洋的世界坐标折线(含两端港口点)。
    //   结果按 (起港,终港) 缓存, 供 TravelDays 与航行插值复用。
    //
    //   "不压陆"分两级保证(缺一不可):
    //     ① 格级: 格心不能在陆上, 对角还得两个正交邻格都是海(否则切过陆角)。
    //     ② 弦级: 格心都在海上 **不等于** 两个格心的连线在海上 —— 弦会从陆尖旁边切过去。
    //        近岸格才做精确线段求交(SeaMapGen.SegmentHitsLand), 远洋格跳过(见 _coastal)。
    //        抽直(Compress)同理, 合并前一律先验弦。
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
        // 栅格步长 = SeaMapGen.PxPerDeg 的倒数(0.25°), 与陆地底图的原生分辨率一比一。
        //   曾经用 0.5°: 比地图粗一倍, 霍尔木兹(波斯湾口)与马六甲两条窄海峡直接被陆地封死 ——
        //   那两个港和其余 51 个港两两不通, A* 全数失败退回直线兜底, 航线横穿整块大陆。
        //   粗网格也藏得住 0.25° 宽的陆尖: 相邻格心都在海上, 弦却切过陆地。
        public const double Step = 0.25;
        static int _cols, _rows;
        static byte[] _sea;          // 1 = 可航行海格(中心不在陆地)
        static bool[] _coastal;      // 1 = 近岸格(3×3 邻域里有格心在陆上/出界) → 弦要精确验
        static bool _built;
        static readonly Dictionary<string, SeaRouteData> _cache = new Dictionary<string, SeaRouteData>();

        public static void ClearCache() { _cache.Clear(); }

        // 诊断计数(只读, 不参与逻辑): 两种"直线兜底"各发生了几次。
        //   SeaMenu.ValidateRoutes 清零后跑一遍, 看看到底是吸附不到海格还是 A* 找不到路。
        public static int DiagSnapFail, DiagAstarFail;

        public static SeaRouteData Get(Port a, Port b)
        {
            if (a == null || b == null || a.Id == b.Id) return null;
            return Route(a.Lon, a.Lat, b.Lon, b.Lat, a.Id + ">" + b.Id);
        }

        // 通用"坐标航线": 任意起终点(港位或海上点)之间绕陆海路。
        //   起终点各自"吸附"到最近海格后 A*, 折线两端精确落在传入坐标上
        //   (泊港传港坐标 = 与旧 Get 一致; 海上抛锚续航 / 探索插旗点传海面坐标)。
        //   key 仅作缓存键(同起终点传同 key 才命中; 传空 = 不缓存)。
        public static SeaRouteData Route(double lonA, double latA, double lonB, double latB, string key)
        {
            Ensure();
            if (!string.IsNullOrEmpty(key) && _cache.TryGetValue(key, out var hit)) return hit;
            var rd = ComputeCoords(lonA, latA, lonB, latB);
            if (rd != null && !string.IsNullOrEmpty(key))
            {
                if (_cache.Count > 256) _cache.Clear();   // 简单淘汰, 避免无限增长
                _cache[key] = rd;
            }
            return rd;
        }

        // 诊断用: 从 (lon,lat) 吸附到的海格出发做 8 邻洪泛(不跑 A*), 报出可达格数与经纬范围。
        //   exact=true 走 A* 那套弦级精确校验。两次结果之差 = "精确校验到底封住了什么";
        //   范围之差 = 断口在哪。A* 失败时只报得出"到不了", 说不出是哪儿断的, 洪泛能。
        public static string DiagFlood(double lon, double lat, bool exact, out int count)
        {
            Ensure();
            count = 0;
            int s = SnapCell(lon, lat);
            if (s < 0) return "吸附失败";
            var seen = new bool[_cols * _rows];
            var q = new Queue<int>();
            q.Enqueue(s); seen[s] = true;
            int minx = _cols, maxx = -1, miny = _rows, maxy = -1;
            int[] dx8 = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] dy8 = { 0, 0, 1, -1, 1, -1, 1, -1 };
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                count++;
                int x = cur % _cols, y = cur / _cols;
                if (x < minx) minx = x;
                if (x > maxx) maxx = x;
                if (y < miny) miny = y;
                if (y > maxy) maxy = y;
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + dx8[k], ny = y + dy8[k];
                    if (!InGrid(nx, ny) || _sea[Idx(nx, ny)] == 0) continue;
                    int ni = Idx(nx, ny);
                    if (seen[ni]) continue;
                    bool diag = dx8[k] != 0 && dy8[k] != 0;
                    if (diag && (_sea[Idx(x + dx8[k], y)] == 0 || _sea[Idx(x, y + dy8[k])] == 0)) continue;
                    if (exact && (_coastal[cur] || _coastal[ni]) &&
                        SeaMapGen.SegmentHitsLand(
                            SeaMapGen.Lon0 + (x + 0.5) * Step, SeaMapGen.Lat0 + (y + 0.5) * Step,
                            SeaMapGen.Lon0 + (nx + 0.5) * Step, SeaMapGen.Lat0 + (ny + 0.5) * Step))
                        continue;
                    seen[ni] = true;
                    q.Enqueue(ni);
                }
            }
            return $"经{SeaMapGen.Lon0 + minx * Step:F1}..{SeaMapGen.Lon0 + (maxx + 1) * Step:F1}"
                 + $" 纬{SeaMapGen.Lat0 + miny * Step:F1}..{SeaMapGen.Lat0 + (maxy + 1) * Step:F1}";
        }

        // 诊断用: 打出 (lon,lat) 所在格周围 3×3 的判定明细 —— 哪一格是海、中心格连向它的弦
        //   有没有被精确校验挡下。洪泛只说得出"这一格是孤岛", 这个说得出"是哪条边断的"。
        public static string DiagCell(double lon, double lat)
        {
            Ensure();
            int cx = ColOf(lon), cy = RowOf(lat);
            double mlon = SeaMapGen.Lon0 + (cx + 0.5) * Step;
            double mlat = SeaMapGen.Lat0 + (cy + 0.5) * Step;
            var sb = new System.Text.StringBuilder();
            sb.Append($"中心格 ({mlon:F3},{mlat:F3})  LandAt={SeaMapGen.LandAt(mlon, mlat)}  近岸={_coastal[Idx(cx, cy)]}\n");
            for (int dy = 1; dy >= -1; dy--)
            {
                sb.Append("   ");
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (!InGrid(x, y)) { sb.Append("   [界外]        "); continue; }
                    double clon = SeaMapGen.Lon0 + (x + 0.5) * Step;
                    double clat = SeaMapGen.Lat0 + (y + 0.5) * Step;
                    if (_sea[Idx(x, y)] == 0) { sb.Append("   [陆]          "); continue; }
                    if (dx == 0 && dy == 0) { sb.Append("   [本格]        "); continue; }
                    bool cross = SeaMapGen.SegmentHitsLand(mlon, mlat, clon, clat);
                    bool diag = dx != 0 && dy != 0;
                    bool corner = diag && (_sea[Idx(cx + dx, cy)] == 0 || _sea[Idx(cx, cy + dy)] == 0);
                    sb.Append($"   {(cross ? "弦断" : "可走")}{(corner ? "/角挡" : "     ")}     ");
                }
                sb.Append('\n');
            }
            return sb.ToString();
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

            // 近岸标记: 3×3 邻域里有任一格的格心在陆上(或越界) → 本格算"近岸"。
            //   用途见 Astar: 只有近岸的弦才值得花精确求交的代价。
            //   为什么远洋格可以跳过: 陆尖要能把两个海格的连线切断, 它自己得先窄到不足一格,
            //   而本图的海岸是手绘的, 最小的岛(如四国 0.5°×0.6°)也远大于 0.25° 的格 ——
            //   这种瘦陆块周围一定有格心落陆的邻格, 于是必然被标成近岸。真正的漏网只可能是
            //   "比格还窄、且四周格心全在海上的陆条", 本图不存在这种形状。
            _coastal = new bool[n];
            for (int y = 0; y < _rows; y++)
            {
                int row = y * _cols;
                for (int x = 0; x < _cols; x++)
                {
                    bool near = false;
                    for (int dy = -1; dy <= 1 && !near; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (!InGrid(nx, ny) || _sea[Idx(nx, ny)] == 0) { near = true; break; }
                        }
                    _coastal[row + x] = near;
                }
            }
        }

        static int ColOf(double lon) => (int)Math.Floor((lon - SeaMapGen.Lon0) / Step);
        static int RowOf(double lat) => (int)Math.Floor((lat - SeaMapGen.Lat0) / Step);

        static bool InGrid(int x, int y) => x >= 0 && y >= 0 && x < _cols && y < _rows;
        static int Idx(int x, int y) => y * _cols + x;

        // 港 → 最近的"海格"(从港格向四周扩圈找, 回退半径 2°), 找不到返回 -1
        const int SnapMaxRing = 8;   // 8 格 × 0.25° = 2°(港建在陆上, 得往外挪到海)

        static int SnapCell(double lon, double lat)
        {
            Ensure();
            int cx = ColOf(lon), cy = RowOf(lat);
            int best = -1;
            double bestD = double.MaxValue;
            for (int r = 0; r <= SnapMaxRing && best < 0; r++)
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

        static SeaRouteData ComputeCoords(double lonA, double latA, double lonB, double latB)
        {
            Ensure();
            int sa = SnapCell(lonA, latA);
            int sb = SnapCell(lonB, latB);
            var rd = new SeaRouteData();
            rd.Span = DistDeg(lonA, latA, lonB, latB);

            // 兜底: 吸不到海格(极窄水道异常) → 直线海路
            if (sa < 0 || sb < 0)
            {
                DiagSnapFail++;
                rd.Pts.Add(new Vector3((float)lonA, 0f, (float)-latA));
                rd.Pts.Add(new Vector3((float)lonB, 0f, (float)-latB));
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
                    DiagAstarFail++;
                    rd.Pts.Add(new Vector3((float)lonA, 0f, (float)-latA));
                    rd.Pts.Add(new Vector3((float)lonB, 0f, (float)-latB));
                    rd.Total = rd.Span;
                    return rd;
                }
            }

            // 折线 = 起点 → 途经海格中心 → 终点
            rd.Pts.Add(new Vector3((float)lonA, 0f, (float)-latA));
            for (int i = 0; i < cells.Count; i++)
            {
                int c = cells[i];
                int x = c % _cols, y = c / _cols;
                rd.Pts.Add(new Vector3(
                    (float)(SeaMapGen.Lon0 + (x + 0.5) * Step),
                    0f,
                    (float)-(SeaMapGen.Lat0 + (y + 0.5) * Step)));
            }
            rd.Pts.Add(new Vector3((float)lonB, 0f, (float)-latB));

            // 去掉明显共线的中间点(直线走直, 转弯保留), 让航行轨迹干净
            Compress(rd.Pts);

            float tot = 0f;
            for (int i = 1; i < rd.Pts.Count; i++)
                tot += Vector3.Distance(rd.Pts[i - 1], rd.Pts[i]);
            rd.Total = Mathf.Max(0.001f, tot);
            return rd;
        }

        // 抽直链最长(度)。共线点留着不影响观感(虚线画出来一模一样), 只是给"验一段弦"的成本封顶 ——
        //   否则近乎笔直的远洋航线会把上百个点一路吞成一条 80° 的弦, 验证成本 O(n²) 爆炸。
        const float MergeMaxLen = 6f;

        // 弦 A→C 是否全程在海上。
        //   用精确求交(SeaMapGen.SegmentHitsLand)而不是沿线采样: 采样步长再小也会从比步长
        //   还短的陆尖两侧跨过去 —— 实测就是这么漏掉 0.08° 的贴岸的。求交不漏。
        //   注意本函数对"端点在陆上"也返回 true(见 SegmentHitsLand 的端点判定), 于是折线
        //   两端那两个港点永远不肯被合并 —— 正合期望: 首末段本就压在陆上, 不该被抽直拉长。
        static bool ChordAtSea(Vector3 a, Vector3 b)
            => !SeaMapGen.SegmentHitsLand(a.x, -a.z, b.x, -b.z);

        // 去掉明显共线的中间点(直线走直, 转弯保留), 让航行轨迹干净。
        //   但"抽直"必须压不着陆: 合并 B 之前先在弦 A→C 上细采样验一遍 ——
        //   A* 只保证"格心在海上", 弦切过陆角是它管不到的, 抽直会把这个缺口放大。
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
                    float acx = C.x - A.x, acz = C.z - A.z;
                    if (cross * cross < 0.0004f * (abx * abx + abz * abz + 1e-6f)
                        && (abx * cbx + abz * cbz) > 0f            // 同向近似共线 → 候选吞掉中间点
                        && (acx * acx + acz * acz) <= MergeMaxLen * MergeMaxLen
                        && ChordAtSea(A, C))                       // 且抽直后的弦不压陆
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
            var closed = new bool[n];        // 已定型的格子: 堆里可能还有它的旧条目, 弹出时跳过
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
                if (closed[cur]) continue;   // 同一格可能被推入多次, 只认第一次弹出(f 最小)
                closed[cur] = true;
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
                    // 弦级校验(见类头 ②): 这一刀只砍在近岸格上 —— 远洋格做精确求交纯属浪费。
                    if ((_coastal[cur] || _coastal[Idx(nx, ny)]) &&
                        SeaMapGen.SegmentHitsLand(
                            SeaMapGen.Lon0 + (x + 0.5) * Step, SeaMapGen.Lat0 + (y + 0.5) * Step,
                            SeaMapGen.Lon0 + (nx + 0.5) * Step, SeaMapGen.Lat0 + (ny + 0.5) * Step))
                        continue;
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
