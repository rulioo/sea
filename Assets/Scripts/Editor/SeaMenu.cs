using System;
using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;

namespace Sea
{
    // =============================================================
    // SEA · 编辑器工具
    //   菜单 SEA ▸ 校验数据…   : 数据/世界自检(同 SimCheck 思路, 编辑器内快速跑)
    //   菜单 SEA ▸ 搭建主场景… : 生成 Assets/Scenes/Main.unity(相机+光+SeaPlay+SeaHud)
    // =============================================================
    public static class SeaMenu
    {
        const string ScenePath = "Assets/Scenes/Main.unity";

        [MenuItem("SEA/校验数据 (数据+世界+冒烟)", false, 10)]
        public static void ValidateAll()
        {
            var ok = SeaBootstrap.TryLoad(15500712, out var w, out var eng, out var err);
            if (!ok) { EditorUtility.DisplayDialog("SEA 校验", "数据装载失败:\n" + err, "确定"); return; }

            try
            {
                int goods = w.Goods.Count, ports = w.Ports.Count,
                    areas = w.Areas.Count, events = w.Events.Count;

                var issues = new System.Collections.Generic.List<string>();
                if (goods == 0) issues.Add("无商品");
                if (ports == 0) issues.Add("无港口");
                if (areas == 0) issues.Add("无航区");
                foreach (var g in w.Goods)
                {
                    bool anyProducer = false;
                    foreach (var p in w.Ports)
                        if (Array.IndexOf(p.Specialties, g.Id) >= 0) { anyProducer = true; break; }
                    if (!anyProducer) issues.Add("商品无人生产: " + g.Id);
                }
                foreach (var p in w.Ports)
                    if (p.Specialties.Length == 0 && p.Imports.Length == 0)
                    { issues.Add("港口无任何贸易属性: " + p.Id); }

                // 舰队冒烟(M1): Ships/Crews/FleetTuning 装载 + 标准卡拉维尔满员 45 日给养 → 续航 45
                string enduranceLine = "- 舰队冒烟: 未执行";
                var weightByGood = new System.Collections.Generic.Dictionary<string, int>();
                foreach (var g in w.Goods) weightByGood[g.Id] = g.Weight;
                if (SeaFleetLoader.TryLoad(id => weightByGood.TryGetValue(id, out var wv) ? wv : 1,
                        out var cat, out var fTun, out var fErr))
                {
                    int ships = cat.ShipOrder.Count, crews = cat.CrewOrder.Count;
                    float rate = fTun.Provision["food"].PerCrewPerDay; // 1.0
                    var demo = FleetBuilder.Start(cat, "m2_caravel", 80, 100000,
                        new ProvisionStock { Food = 80f * rate * 45f, Water = 100000f, Tea = 100000f });
                    enduranceLine = $"- 舰队冒烟: {ships} 舰 / {crews} 船员目录; 卡拉维尔 45 日给养 → 续航 {FleetOps.EnduranceDays(demo)} 日";
                    if (FleetOps.EnduranceDays(demo) != 45) issues.Add("舰队冒烟续航 ≠ 45");
                }
                else issues.Add("舰队数据装载失败: " + fErr);

                // 冒烟: 推进整年 + 手工一买一卖
                eng.AdvanceDays(366);
                Port first = w.Ports[0];
                Good g0 = first.Specialties.Length > 0 ? w.GoodById[first.Specialties[0]] : null;
                int buyU = 0, sellU = 0;
                if (g0 != null)
                {
                    buyU = eng.Buy(first, g0, 5, 50000).Units;
                    sellU = eng.Sell(first, g0, 2).Units;
                }

                string msg = $"- 商品 {goods} / 港口 {ports} / 航区 {areas} / 事件 {events}\n"
                           + "- 推进 366 日, 无异常\n"
                           + $"- 冒烟交易: 买入 {buyU} 件, 卖出 {sellU} 件\n"
                           + enduranceLine + "\n"
                           + (issues.Count > 0 ? "- 问题:\n  · " + string.Join("\n  · ", issues) : "- 无结构性问题 ✓");
                if (issues.Count > 0)
                    Debug.LogWarning("[SEA] 校验发现潜在问题:\n" + msg);
                else
                    Debug.Log("[SEA] 校验通过:\n" + msg);
                EditorUtility.DisplayDialog("SEA 校验", msg, "确定");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("SEA 校验", "校验中抛出异常: " + ex.Message, "确定");
            }
        }

        // =============================================================
        // 航线"绝不压陆"自检
        //   批处理: Unity -batchmode -nographics -quit -projectPath . \
        //           -executeMethod Sea.SeaMenu.ValidateRoutes -logFile dev/routes.log
        //   做什么: 遍历全部港对, 用真实 SeaRoute.Route 求折线, 逐段拿 SeaMapGen.SegmentHitsLand
        //          做精确线段求交 —— 任一段与陆地相交即"压陆"。
        //   为什么改精确求交(原来每 0.1° 采样): 采样会从比步长还短的陆尖两侧跨过去, 实测漏过
        //          0.08° 的贴岸(约一条船身长)。既然要的是"绝对不允许", 判决就得是判决式的。
        //   判哪些段: **只判画出来的那一部分**。折线首末两点是港口本身(建在陆上), 首末两段是
        //          出港/进港的登陆腿, SeaPlay.RebuildRouteLine 已经不画它们了。只判 Pts[1..last-1]
        //          之间那些段, 于是"本自检报 0"等价于"画面上不会有黄虚线压在陆地上"。
        //   兜底(只有两点的直线折线)单独计数: 它一段可画的都没有, 说明这一对压根没走 A*。
        //   压陆段数不为 0 即 exit 1(可当回归闸门)。
        // =============================================================

        [MenuItem("SEA/校验航线不压陆", false, 20)]
        public static void ValidateRoutes()
        {
            if (!SeaBootstrap.TryLoad(15500712, out var w, out _, out var err))
            {
                Debug.LogError("[SEA] 航线自检: 数据装载失败 · " + err);
                EditorApplication.Exit(1);
                return;
            }

            int pairs = 0, landPairs = 0, midPairs = 0, fallback = 0, midSegs = 0, sampledHits = 0;
            float worstSeg = 0f;
            string worstSegAt = "";
            SeaRoute.DiagSnapFail = SeaRoute.DiagAstarFail = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var samples = new System.Collections.Generic.List<string>();
            var fbEx = new System.Collections.Generic.List<string>();
            var fbCount = new System.Collections.Generic.Dictionary<string, int>();
            Progress($"开始: {w.Ports.Count} 港, 逐一求折线并逐段判定");

            for (int i = 0; i < w.Ports.Count; i++)
            {
                for (int j = i + 1; j < w.Ports.Count; j++)
                {
                    var a = w.Ports[i];
                    var b = w.Ports[j];
                    var rd = SeaRoute.Route(a.Lon, a.Lat, b.Lon, b.Lat, null);   // key=null → 不污染缓存
                    if (rd == null || rd.Pts.Count < 2) continue;
                    pairs++;
                    if ((pairs & 63) == 0)
                        Progress($"进行中: 第 {i + 1}/{w.Ports.Count} 港起, 已判 {pairs} 对"
                               + $" · {sw.Elapsed.TotalSeconds:F0}s · 兜底 {fallback} · 压陆段 {midSegs}");
                    if (rd.Pts.Count == 2)   // 只有起终点两点 = 直线兜底(没走 A*)
                    {
                        fallback++;
                        Bump(fbCount, a.Id); Bump(fbCount, b.Id);
                        if (fbEx.Count < 8)
                            fbEx.Add($"{a.Id}({a.Lon:F0},{a.Lat:F0})->{b.Id}({b.Lon:F0},{b.Lat:F0}) 直线{rd.Span:F0}°");
                    }

                    float acc = 0f;          // 已走过的弧长(到当前段起点)
                    int last = rd.Pts.Count - 1;
                    bool pairHasLand = false;

                    // 段 k = Pts[k-1]→Pts[k]。只判 2..last-1, 即两端都在海上的那些段 ——
                    //   它们正是 RebuildRouteLine 唯一会画出来的部分(它跑 i = 1..last-2)。
                    //   每段过两套判据, 两边都报 0 才算干净 —— 它们的盲区不重合:
                    //     · 精确求交: 只看"真穿越", 会漏掉弦正好穿过多边形顶点的退化情形;
                    //     · 0.05° 细采样: 反过来, 会漏掉比步长还短的陆尖。
                    for (int k = 2; k <= last - 1; k++)
                    {
                        var A = rd.Pts[k - 1];
                        var B = rd.Pts[k];
                        float segLen = Vector3.Distance(A, B);
                        float at = acc;
                        acc += segLen;
                        if (segLen <= 0.0001f) continue;

                        if (SeaMapGen.SegmentHitsLand(A.x, -A.z, B.x, -B.z))
                        {
                            pairHasLand = true;
                            midSegs++;
                            if (segLen > worstSeg)
                            {
                                worstSeg = segLen;
                                worstSegAt = $"{a.Id}->{b.Id} 自起点 {at:F1}° 处";
                            }
                            if (midSegs + sampledHits <= 12)
                                samples.Add($"{a.Id}->{b.Id} 自起点 {at:F1}° 处(段长 {segLen:F2}°) [求交]");
                        }

                        // 细采样只取开区间(不含两端点): 端点是吸附出来的海格, 但格心可能正好压在
                        //   海岸线上, 而 LandAt 对边界点的内外判定是任意的 —— 拿它当"压陆"会冤枉
                        //   一堆本来正常的航线。段内部才是我们要保的。
                        int steps = Mathf.Max(1, Mathf.CeilToInt(segLen / 0.05f));
                        for (int s = 1; s < steps; s++)
                        {
                            float u = (float)s / steps;
                            if (!SeaMapGen.LandAt(Mathf.Lerp(A.x, B.x, u), Mathf.Lerp(-A.z, -B.z, u))) continue;
                            pairHasLand = true;
                            sampledHits++;
                            if (midSegs + sampledHits <= 12)
                                samples.Add($"{a.Id}->{b.Id} 自起点 {at:F1}° 处 [细采样]");
                            break;
                        }
                    }

                    if (pairHasLand) { landPairs++; midPairs++; }
                }
            }
            sw.Stop();
            Progress($"判定完毕: {pairs} 对 / {sw.Elapsed.TotalSeconds:F1}s · 兜底 {fallback} · 压陆段 {midSegs}");
            int snapFail = SeaRoute.DiagSnapFail, astarFail = SeaRoute.DiagAstarFail;   // 探针会清零, 先取值

            // 连通性探针: 几组开阔海点之间直接寻路(不经港口), 判定"兜底"到底是
            //   地图本身断航(陆地封死了水道)还是 A* 搜索上限(路太长, 没搜到就被闸掉)。
            var probeLines = new System.Collections.Generic.List<string>();
            var probes = new (string Tag, double Lon1, double Lat1, double Lon2, double Lat2)[]
            {
                ("好望角绕行 南大西洋→南印度洋", -20, -38, 60, -38),
                ("北大西洋→南大西洋",             5,  55, -20, -30),
                ("南印度洋→南太平洋",            60, -38, 120, -38),
                ("地中海→红海 (无运河, 应不通)",  10,  35,  40,  20),
            };
            foreach (var pb in probes)
            {
                SeaRoute.DiagAstarFail = 0;
                var pr = SeaRoute.Route(pb.Lon1, pb.Lat1, pb.Lon2, pb.Lat2, null);
                probeLines.Add($"{pb.Tag}: {(pr == null ? "null" : pr.Total.ToString("F0") + "°")}"
                             + $" 折线{(pr == null ? 0 : pr.Pts.Count)}点"
                             + $"{(pr != null && pr.Pts.Count <= 2 ? "  ← 兜底" : "")}"
                             + $"{(SeaRoute.DiagAstarFail > 0 ? "  ← A*失败" : "")}");
            }

            // 再补几组"兜底港 → 开阔洋"的探针: 若连大洋都到不了, 说明该港被网格困在死水湾里
            foreach (var kv in TopFallbackPorts(fbCount, w, 4))
            {
                SeaRoute.DiagAstarFail = 0;
                var pr = SeaRoute.Route(kv.Lon, kv.Lat, -20, -30, null);
                probeLines.Add($"{kv.Id}({kv.Lon:F0},{kv.Lat:F0})→南大西洋: "
                             + (pr == null ? "null" : pr.Total.ToString("F0") + "°")
                             + $"{(pr != null && pr.Pts.Count <= 2 ? "  ← 兜底(该港被陆地围死)" : "  ✓ 通")}");
            }

            Debug.Log($"[SEA] 航线自检: {pairs} 港对 / 耗时 {sw.Elapsed.TotalSeconds:F1}s\n"
                    + $"  · 直线兜底 {fallback} 对(吸附失败 {snapFail} / A* 失败 {astarFail})\n"
                    + $"  · 折线沾陆港对 {landPairs}(多为仅进港段 {landPairs - midPairs}, 正常)\n"
                    + $"  · 画出的航线段压陆(求交) {midSegs} 段 / 涉及 {midPairs} 对  ← 必须为 0\n"
                    + $"  · 画出的航线段压陆(细采样复核) {sampledHits} 段  ← 必须为 0\n"
                    + $"  · 最长压陆段 {worstSeg:F2}° @{(string.IsNullOrEmpty(worstSegAt) ? "无" : worstSegAt)}\n"
                    + "  · 兜底港(牵涉次数): " + FbTop(fbCount, 12) + "\n"
                    + "  · 连通性探针:\n    " + string.Join("\n    ", probeLines) + "\n"
                    + (fbEx.Count > 0 ? "  · 兜底样例:\n    " + string.Join("\n    ", fbEx) + "\n" : "")
                    + (samples.Count > 0 ? "  · 切角样例:\n    " + string.Join("\n    ", samples) : ""));
            if (midPairs > 0 || sampledHits > 0) EditorApplication.Exit(1);
        }

        // =============================================================
        // 海图探针, 两个用途:
        //   ① 剖面对数 —— 把一块经纬区域打成 ASCII(# 陆地 . 海), 肉眼定位"哪条水道被封死、在什么尺度上被封死"。
        //      想看别处, 改下面 DumpRegion 的参数即可。
        //   ② 洪泛哨兵 —— 海岸线一改, 先跑这个。见 Sentinel 的注释。
        //   批处理: -executeMethod Sea.SeaMenu.ProbeMap  (哨兵不过 → exit 1)
        // =============================================================
        [MenuItem("SEA/探针 海图剖面+洪泛哨兵", false, 22)]
        public static void ProbeMap()
        {
            // 两条"曾经被焊死"的水道: 改岸线后第一眼看这里。
            //   霍尔木兹: 阿拉伯半岛的穆桑代姆角一度比同经度的伊朗岸还靠北, 两个多边形相交,
            //     海峡被焊成一块整陆 —— 波斯湾成内湖, 51 条航线压陆。见 SeaMapGen 里 northamerica/
            //     eurasia_sw 的注释。
            DumpRegion("霍尔木兹海峡", 54, 60, 23, 29, 0.2);
            DumpRegion("马六甲海峡", 95, 105, -2, 7, 0.2);

            bool ok = true;

            // 哨兵: 从几个远洋点做 8 邻域洪泛, 只用"格心在海上"判一次, 再叠上弦级精确校验判一次。
            //   两个数**必须相等** —— 不等就说明精确求交把海格封成了孤岛(把"只是蹭到岸"的弦判成了
            //   "穿过去了"), 那种格在航路上就是死胡同: A* 失败 → 退回直线兜底 → 航线从大陆上直穿。
            //   踩过一次: 维拉克鲁斯那格的格心正好落在海岸边上, 叉积舍入残差(≈1e-15)恰好为负,
            //   "A 在 CD 直线上"被读成"A 跨到了另一侧", 三条出海方向的弦全断, 25 万格塌成 1 格。
            //   见 SeaMapGen.SegCrossProper 的零带注释。
            //   某个哨兵报"✗封死"时, 用 SeaRoute.DiagCell(lon, lat) 打印那一格 3×3 邻域的逐边判定。
            ok &= Sentinel("维拉克鲁斯外海", -96, 19);      // 墨西哥湾: 上次出事的地方
            ok &= Sentinel("直布罗陀外海", -10, 35);
            ok &= Sentinel("菲律宾海外", 130, 20);
            ok &= Sentinel("南大西洋", -20, -30);

            // 端到端: 维拉克鲁斯 → 大西洋。这里一度 A* 失败退回直线兜底, 航线从墨西哥本土上穿过去。
            SeaRoute.DiagAstarFail = 0;
            var t = SeaRoute.Route(-96, 19, -60, 30, null);
            bool routeOk = t != null && SeaRoute.DiagAstarFail == 0;
            ok &= routeOk;
            Debug.Log($"[SEA] 维拉克鲁斯→大西洋: {(t == null ? "null" : t.Total.ToString("F0") + "°")}"
                    + $" 折线{(t == null ? 0 : t.Pts.Count)}点 A*失败{SeaRoute.DiagAstarFail}  {(routeOk ? "✓" : "✗退回兜底")}");

            Debug.Log($"[SEA] 海图探针: {(ok ? "✓ 全部通过" : "✗ 有哨兵不过, 见上")}");
            EditorApplication.Exit(ok ? 0 : 1);
        }

        // 洪泛哨兵: 同一格两次洪泛(仅格心 / 加弦级精确校验)必须给出同一个格数。
        //   差出来的那部分, 就是精确求交凭空封出来的墙 —— 墙上的格 A* 出不去。
        static bool Sentinel(string tag, double lon, double lat)
        {
            string a = SeaRoute.DiagFlood(lon, lat, false, out int n0);
            string b = SeaRoute.DiagFlood(lon, lat, true, out int n1);
            bool ok = n0 == n1;
            Debug.Log($"[SEA] 洪泛哨兵 {tag} ({lon:F0},{lat:F0}): 仅格心 {n0} 格 → 加精确 {n1} 格"
                    + $"  {(ok ? "✓ 通" : "✗ 封死 " + (n0 - n1) + " 格")}   加精确范围 {b}  仅格心范围 {a}");
            return ok;
        }

        // =============================================================
        // 球面投影自检(球体化的地基)  -executeMethod Sea.SeaMenu.ValidateGlobe
        //   只校验 SeaGlobe 的纯数学, 不建场景不碰渲染 —— 无头可跑, 不过就 exit 1。
        //   为什么值得单独立一道闸: 球体化之后 "1 世界单位 = 1° 弧长" 是 RouteY /
        //   BallFloatY / 船模尺寸 / 虚线长 这些按度估出来的常量的立命之本。半径一旦被
        //   改错, 画面未必当场崩(往往只是所有图标比例悄悄变了), 但这里的数会立刻红。
        // =============================================================
        [MenuItem("SEA/校验球面投影", false, 24)]
        public static void ValidateGlobe()
        {
            bool ok = true;
            var sb = new System.Text.StringBuilder();
            sb.Append("[SEA] 球面投影自检:\n");
            sb.Append($"  · R = {SeaGlobe.R:F5}  周长 = {2f * Mathf.PI * SeaGlobe.R:F3}(应为 360)"
                    + $"  DegToWorld = {SeaGlobe.DegToWorld:F3}  WorldToDeg = {SeaGlobe.WorldToDeg:F3}\n");
            if (Mathf.Abs(2f * Mathf.PI * SeaGlobe.R - 360f) > 1e-2f) ok = false;
            if (Mathf.Abs(SeaGlobe.DegToWorld - 1f) > 1e-6f) ok = false;

            // ---- ① 往返: 经纬度 → 球面 → 经纬度, 1° 网格全域扫一遍 ----
            //   极点(lat=±90)经度无定义, 跳过。判据用**世界空间残差**而不是度数: 极点附近
            //   经度是病态的(纬度 89° 处整条纬线半径只剩 R·cos89° ≈ 1 个单位, 同样的位置
            //   误差换算成经度会放大几十倍), 拿度数卡全域会在 (±180,±89) 这种角落误报
            //   —— 第一版就是这么误报的。度数口径只用在真实港位上(见 ⑦), 那才是玩法真会反推的。
            double worstRT = 0; string worstRTAt = "无";
            for (int lon = -180; lon <= 180; lon++)
                for (int lat = -89; lat <= 89; lat++)
                {
                    var wp = SeaGlobe.ToWorld(lon, lat);
                    SeaGlobe.ToLonLat(wp, out double lo, out double la);
                    double e = Vector3.Distance(SeaGlobe.ToWorld(lo, la), wp);   // 往返点 vs 原点
                    if (e > worstRT) { worstRT = e; worstRTAt = $"({lon},{lat})"; }
                }
            bool rtOk = worstRT < 1e-3;
            ok &= rtOk;
            sb.Append($"  · 往返残差(世界单位) max {worstRT:E2} @{worstRTAt}  {(rtOk ? "✓" : "✗ 应 < 1e-3")}\n");

            // ---- ② 大圆角距: 与"独立算法"对账 ----
            //   这里刻意不用 haversine 复算 haversine, 而是走 ToWorld 拿两个单位法线, 用
            //   atan2(|a×b|, a·b) 求夹角 —— 公式不同源, 才能真的互相验证(也能验出 ToWorld 的
            //   参数化是否真的是等距圆柱意义下的经纬映射)。
            double[] sm = {
                -99.9, 19.2,  129.9, 33.0,     // 横跨全图的一对
                   0,  0,       0, 60,          // 高纬东西向: 平面距离与球面差得最狠的地方
                 -60, -34,     50, 40,
                 -10, 35,      30, 31,
                 139, -35,    -74, 40,
                 -99.9, 19.2, -9.4, 38.7,
                  55, 26,      56, 27,          // 短距(霍尔木兹那种量级)
            };
            double worstGC = 0; string worstGCAt = "无";
            for (int i = 0; i < sm.Length; i += 2)
                for (int j = 0; j < sm.Length; j += 2)
                {
                    var na = SeaGlobe.ToWorld(sm[i], sm[i + 1]).normalized;
                    var nb = SeaGlobe.ToWorld(sm[j], sm[j + 1]).normalized;
                    double ang = Math.Atan2(Vector3.Cross(na, nb).magnitude, Vector3.Dot(na, nb)) * Mathf.Rad2Deg;
                    double got = SeaGlobe.GreatCircleDegLonLat(sm[i], sm[i + 1], sm[j], sm[j + 1]);
                    double e = Math.Abs(got - ang);
                    if (e > worstGC) { worstGC = e; worstGCAt = $"({sm[i]:F1},{sm[i + 1]:F1})→({sm[j]:F1},{sm[j + 1]:F1})"; }
                }
            bool gcOk = worstGC < 1e-3;
            ok &= gcOk;
            sb.Append($"  · 大圆角距 vs 法线夹角 max 差 {worstGC:E2}° @{worstGCAt}  {(gcOk ? "✓" : "✗ 应 < 1e-3")}\n");

            // ---- ③ 球面度量: 子午向步长恒为 0.25 单位, 东西向按 cos φ 收缩 ----
            //   这是"现有世界单位常量原值有效"的**唯一**依据(R·Deg2Rad == 1 ⇒ 1 单位 = 1° 弧长)。
            //   注意这里验的是**两条**式子, 不是一条: 第一版想当然地断言"0.25° 步长在哪儿都是
            //   0.25 单位", 结果在纬度 60° 处报 50% 偏差 —— 那不是 bug, 正是球面几何本身
            //   (经线在两极收敛, 纬线半径 = R·cos φ)。
            //   顺带记一笔(不属本闸门职责, 也未修): SeaRoute 的 A* 栅格是 **lon/lat 空间**的
            //   0.25°, 所以 rt.Total 是按平面累加的 —— 高纬东西向航程的相对代价被系统性高估,
            //   纬度 60° 处最大约 2×。这是平面地图时代就有的口径, 球体化刻意不动它(见计划:
            //   玩法数值口径不变), 若要修应单独立项。
            double worstMer = 0, worstPar = 0; string worstParAt = "无";
            for (int lat = -60; lat <= 60; lat += 10)
                for (int lon = -100; lon <= 130; lon += 10)
                {
                    var p0 = SeaGlobe.ToWorld(lon, lat);
                    worstMer = Math.Max(worstMer,
                        Math.Abs(Vector3.Distance(p0, SeaGlobe.ToWorld(lon, lat + 0.25)) - 0.25));
                    double want = 0.25 * Math.Cos(lat * Mathf.Deg2Rad);      // = R·Δλ_rad·cos φ
                    double got = Vector3.Distance(p0, SeaGlobe.ToWorld(lon + 0.25, lat));
                    double e = Math.Abs(got - want) / Math.Max(1e-6, want);
                    if (e > worstPar) { worstPar = e; worstParAt = $"({lon},{lat}) 期望 {want:F4} 实得 {got:F4}"; }
                }
            bool scOk = worstMer < 1e-3 && worstPar < 1e-3;
            ok &= scOk;
            sb.Append($"  · 球面度量: 子午向 0.25° 步长偏差 max {worstMer:E2} 单位;"
                    + $" 东西向 vs 0.25·cosφ 相对误差 max {worstPar:P4} @{worstParAt}  {(scOk ? "✓" : "✗")}\n");

            // ---- ④ 球面局部系: up=法线, 且 X=东 Y=法线 Z=北 的单位正交右手系 ----
            //   Unity 约定 forward = cross(right, up); 港标/旗/船全挂在这个 frame 下,
            //   它一错, 表现是"旗子歪了/船躺着开", 不看画面根本发现不了。
            double worstFrame = 0; string worstFrameAt = "无";
            for (int lat = -80; lat <= 80; lat += 20)
                for (int lon = -180; lon <= 180; lon += 30)
                {
                    var rot = SeaGlobe.SurfaceRotation(lon, lat);
                    var up = rot * Vector3.up;
                    var east = rot * Vector3.right;
                    var north = rot * Vector3.forward;
                    double e = Math.Max(
                        Vector3.Angle(up, SeaGlobe.ToWorld(lon, lat).normalized),
                        Math.Max(Math.Abs(up.magnitude - 1), Math.Abs(east.magnitude - 1)));
                    e = Math.Max(e, Math.Abs(north.magnitude - 1));
                    e = Math.Max(e, Math.Abs(Vector3.Angle(up, east) - 90) + Math.Abs(Vector3.Angle(up, north) - 90)
                                  + Math.Abs(Vector3.Angle(east, north) - 90));
                    e = Math.Max(e, (north - Vector3.Cross(east, up)).magnitude);   // forward = cross(right, up)
                    if (e > worstFrame) { worstFrame = e; worstFrameAt = $"({lon},{lat})"; }
                }
            bool frOk = worstFrame < 1e-3;
            ok &= frOk;
            sb.Append($"  · 局部系(东/法线/北) max 偏差 {worstFrame:E2} @{worstFrameAt}  {(frOk ? "✓" : "✗ 应 < 1e-3")}\n");

            // ---- ⑤ 射线-球求交: 正打要回到原经纬度, 掠射要判"未命中" ----
            double worstRay = 0; string worstRayAt = "无";
            bool missOk = true;
            foreach (float d in new[] { 6f, 28f, 78f })          // 滚轮下限 / 本港近景 / 整球取景
                for (int lat = -60; lat <= 60; lat += 30)
                    for (int lon = -180; lon <= 180; lon += 45)
                    {
                        var surf = SeaGlobe.ToWorld(lon, lat);
                        var origin = surf.normalized * (SeaGlobe.R + d);
                        if (!SeaGlobe.RaySphere(origin, -origin.normalized, out var hit)) { missOk = false; continue; }
                        SeaGlobe.ToLonLat(hit, out double lo, out double la);
                        double e = Math.Max(Math.Abs(lo - lon), Math.Abs(la - lat));
                        if (e > worstRay) { worstRay = e; worstRayAt = $"({lon},{lat}) d={d}"; }

                        // 掠射未命中: 离球心 |o| = R+d, 最近点距离 = |o|·sinθ > R 即擦不到。
                        //   θ 取 70° 时 sinθ = 0.94 > R/(R+6) = 0.905 → 必然落空。
                        var toC = -origin.normalized;
                        var graze = Quaternion.AngleAxis(70f, Vector3.Cross(toC, Vector3.up).normalized) * toC;
                        if (SeaGlobe.RaySphere(origin, graze, out _)) missOk = false;
                    }
            bool rayOk = worstRay < 1e-3 && missOk;
            ok &= rayOk;
            sb.Append($"  · 射线-球求交 正打误差 max {worstRay:E2}° @{worstRayAt}, 掠射未命中 {(missOk ? "✓" : "✗ 误判命中")}  {(rayOk ? "✓" : "✗")}\n");

            // ---- ⑥ 折线加密: 段长必须落回 maxDeg 以内, 端点原样 ----
            //   弦高 sag = R(1-cos(Δ/2)): Δ=1° 时仅 0.0022 单位, 远小于航线抬升 RouteY(0.46);
            //   Δ=20° 时高达 0.87 —— 这就是"不加密的话船会钻进地里"的来源。
            var raw = new System.Collections.Generic.List<Vector3>
            {
                new Vector3(-96f, 0f, -19f), new Vector3(-60f, 0f, -30f), new Vector3(0f, 0f, -35f),
            };
            var fine = SeaGlobe.ArcSubdivide(raw, 1f);
            double worstSeg = 0;
            for (int i = 1; i < fine.Count; i++)
                worstSeg = Math.Max(worstSeg, SeaGlobe.GreatCircleDeg(fine[i - 1], fine[i]));
            double sagRaw = SeaGlobe.R * (1.0 - Math.Cos(SeaGlobe.GreatCircleDeg(raw[0], raw[1]) * Mathf.Deg2Rad * 0.5));
            double sagFine = SeaGlobe.R * (1.0 - Math.Cos(worstSeg * Mathf.Deg2Rad * 0.5));
            bool subOk = fine.Count > raw.Count
                      && (fine[0] - raw[0]).magnitude < 1e-4
                      && (fine[fine.Count - 1] - raw[raw.Count - 1]).magnitude < 1e-4
                      && worstSeg <= 1.0001
                      && sagFine < 0.05;
            ok &= subOk;
            sb.Append($"  · 折线加密: {raw.Count} 点 → {fine.Count} 点, 最长段 {worstSeg:F4}°(应 ≤1)"
                    + $"  弦高 {sagRaw:F3} → {sagFine:F5} 单位(须 ≪ RouteY 0.46)  {(subOk ? "✓" : "✗")}\n");

            // ---- ⑦ 用真实港位过一遍: 港口数据上球后不得漂移 ----
            if (SeaBootstrap.TryLoad(15500712, out var w, out _, out var err))
            {
                double worstPort = 0; string worstPortAt = "无";
                foreach (var p in w.Ports)
                {
                    var wp = SeaGlobe.ToWorld(p.Lon, p.Lat);
                    SeaGlobe.ToLonLat(wp, out double lo, out double la);
                    double e = Math.Max(Math.Abs(lo - p.Lon), Math.Abs(la - p.Lat));
                    if (e > worstPort) { worstPort = e; worstPortAt = p.Name; }
                }
                bool portOk = worstPort < 1e-4;
                ok &= portOk;
                sb.Append($"  · {w.Ports.Count} 座真实港位上球往返 max {worstPort:E2}° @{worstPortAt}"
                        + $"  {(portOk ? "✓" : "✗ 应 < 1e-4")}\n");
            }
            else
            {
                ok = false;
                sb.Append("  · ✗ 数据装载失败, 港口往返未校验: " + err + "\n");
            }

            sb.Append($"[SEA] 球面投影自检: {(ok ? "✓ 全部通过" : "✗ 有项目不过, 见上")}");
            Debug.Log(sb.ToString());
            EditorApplication.Exit(ok ? 0 : 1);
        }

        // 长自检的进度条 —— 直接写文件, 绕开 Unity 的日志缓冲。
        //   批处理的 -logFile 是**缓冲**的: 进程退出才整块落盘。而这个自检要跑十几分钟,
        //   于是中途既看不到进度, 也分不清"还在跑"和"已经死了"(实测就这么白白查了两轮)。
        //   这里每 64 个港对直接覆写一行 dev/routes.progress.txt, tail 一下就知道死活与快慢;
        //   Unity 日志里也照常 Log 一份留档。
        static void Progress(string s)
        {
            Debug.Log("[SEA] " + s);
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(Application.dataPath, "..", "dev", "routes.progress.txt"), s);
            }
            catch { }   // 进度写不出去不该拖垮自检本身
        }

        static void DumpRegion(string tag, double lon0, double lon1, double lat0, double lat1, double step)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"\n[SEA] {tag}  经 {lon0}..{lon1} / 纬 {lat0}..{lat1}  格 {step}°  (# 陆地  . 海)\n        ");
            for (double lon = lon0; lon <= lon1 + 1e-9; lon += step)
                sb.Append((int)System.Math.Round(lon) % 10);
            sb.Append('\n');
            for (double lat = lat1; lat >= lat0 - 1e-9; lat -= step)
            {
                sb.Append($"  {lat,5:F1} ");
                for (double lon = lon0; lon <= lon1 + 1e-9; lon += step)
                    sb.Append(SeaMapGen.LandAt(lon, lat) ? '#' : '.');
                sb.Append('\n');
            }
            Debug.Log(sb.ToString());
        }

        static void Bump(System.Collections.Generic.Dictionary<string, int> d, string k)
        {
            d.TryGetValue(k, out var c);
            d[k] = c + 1;
        }

        // 按牵涉次数降序取前 n 个"兜底港"(凑成一行日志)
        static string FbTop(System.Collections.Generic.Dictionary<string, int> d, int n)
        {
            if (d.Count == 0) return "无";
            var l = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(d);
            l.Sort((x, y) => y.Value.CompareTo(x.Value));
            var s = new System.Collections.Generic.List<string>();
            for (int i = 0; i < l.Count && i < n; i++) s.Add(l[i].Key + "×" + l[i].Value);
            return string.Join(" ", s);
        }

        static System.Collections.Generic.List<Port> TopFallbackPorts(
            System.Collections.Generic.Dictionary<string, int> d, World w, int n)
        {
            var l = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(d);
            l.Sort((x, y) => y.Value.CompareTo(x.Value));
            var outp = new System.Collections.Generic.List<Port>();
            for (int i = 0; i < l.Count && outp.Count < n; i++)
            {
                var p = w.FindPort(l[i].Key);
                if (p != null) outp.Add(p);
            }
            return outp;
        }

        [MenuItem("SEA/搭建主场景 Main.unity", false, 30)]
        public static void BuildMainScene()
        {
            if (!System.IO.Directory.Exists("Assets/Scenes"))
                System.IO.Directory.CreateDirectory("Assets/Scenes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 相机(SeaGame.Start 会兜底, 这里也放一个便于编辑视口)
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.transform.position = new Vector3(0f, 120f, -140f);
            camGo.transform.rotation = Quaternion.Euler(40f, 0f, 0f);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.08f, 0.16f);
            cam.farClipPlane = 1000f;
            camGo.AddComponent<AudioListener>();

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            lightGo.transform.rotation = Quaternion.Euler(55f, -35f, 0f);

            var seaGo = new GameObject("SEA · 旗舰航线");
            seaGo.AddComponent<SeaPlay>();
            seaGo.AddComponent<SeaHud>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            Selection.activeGameObject = seaGo;
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("SEA", "已生成并保存:\n" + ScenePath + "\n点击 Play 即可游玩(相机+光+SeaPlay/SeaHud 已就位)。", "确定");
        }

        // 打包 Windows 独立版(真正可双击运行的 exe, 不依赖 Unity 编辑器)。
        // 批处理调用: Unity -batchmode -nographics -quit -projectPath <proj>
        //             -executeMethod Sea.SeaMenu.BuildStandalone -logFile build.log
        // 产物: Build/SEA.exe + Build/SEA_Data/(含 Resources 里的全部 JSON 数据)。
        // 只靠运行时 Shader.Find 用的着色器, 不引用的话打包会被裁掉 → 提前登记进 Always Included。
        // 内建 Standard/Sprites 与项目自写 SEA/Water、SEA/Land 都在列。
        static void EnsureAlwaysIncludedShaders()
        {
            var assets = UnityEditor.AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (assets == null || assets.Length == 0) return;
            var so = new UnityEditor.SerializedObject(assets[0]);
            var prop = so.FindProperty("m_AlwaysIncludedShaders");
            if (prop == null) return;
            var want = new[]
            {
                Shader.Find("Standard"), Shader.Find("Sprites/Default"),
                Shader.Find("SEA/Water"), Shader.Find("SEA/Land"), Shader.Find("SEA/Route"),
                Shader.Find("SEA/Fog"),   // 黑雾壳: 也是运行时 Shader.Find 取的, 漏了会被裁掉
            };
            bool changed = false;
            foreach (var w in want)
            {
                if (w == null) continue;
                bool has = false;
                for (int j = 0; j < prop.arraySize; j++)
                    if (prop.GetArrayElementAtIndex(j).objectReferenceValue == w) { has = true; break; }
                if (has) continue;
                prop.InsertArrayElementAtIndex(prop.arraySize);
                prop.GetArrayElementAtIndex(prop.arraySize - 1).objectReferenceValue = w;
                changed = true;
            }
            if (changed)
            {
                so.ApplyModifiedProperties();
                UnityEditor.AssetDatabase.SaveAssets();
                Debug.Log("[SEA] 已登记 Always Included Shaders");
            }
        }

        [MenuItem("SEA/构建独立版 (Windows x64)", false, 45)]
        public static void BuildStandalone()
        {
            EnsureAlwaysIncludedShaders();
            var opts = new UnityEditor.BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Main.unity" },
                locationPathName = "Build/SEA.exe",
                target = UnityEditor.BuildTarget.StandaloneWindows64,
                options = UnityEditor.BuildOptions.None,
            };
            var report = UnityEditor.BuildPipeline.BuildPlayer(opts);
            var r = report.summary.result;
            bool ok = r == UnityEditor.Build.Reporting.BuildResult.Succeeded;
            Debug.Log("[SEA] 独立版构建 " + (ok ? "成功" : "失败") + " @ " + opts.locationPathName + "  (" + r + ", " + report.summary.totalSize / 1048576 + " MB)");
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("SEA", ok
                    ? "构建成功:\n" + System.IO.Path.GetFullPath(opts.locationPathName)
                    : "构建失败: " + r + "\n详见 Console / 构建日志。", "确定");
            if (!ok) EditorApplication.Exit(1);
        }

        // 供「一键试玩」cmd 在启动时调用: 打开主场景并自动进入 Play。
        // 退出 Play 后回到编辑态, Ctrl+P 可再次试玩; AutoBoot 保证任意场景都能自建整套试玩。
        [MenuItem("SEA/直接进入试玩 (自动 Play)", false, 40)]
        public static void OpenSceneAndPlay()
        {
            try
            {
                if (System.IO.File.Exists(ScenePath))
                    EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SEA] 打开主场景失败, 将用当前场景(SeaLauncher 仍会自动搭建): " + ex.Message);
            }

            if (Application.isBatchMode) return;   // 批处理(编译冒烟)不自动进 Play
            EditorApplication.delayCall += () =>
            {
                if (!EditorApplication.isPlaying)
                {
                    Debug.Log("[SEA] 已自动进入 Play 试玩; 退出 Play 后回到编辑态, 可 Ctrl+P 再试。");
                    EditorApplication.isPlaying = true;
                }
            };
        }
    }
}
