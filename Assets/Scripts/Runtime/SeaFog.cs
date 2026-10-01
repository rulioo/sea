using System;
using UnityEngine;

namespace Sea
{
    // =============================================================
    // SEA · 世界海图「黑雾遮罩 / 探索迷雾」(球面版)
    //   一层罩住**整颗地球**的黑色雾壳, 把未探明的陆/海/港口/航线全部盖黑 ——
    //   只有舰队「到达的区域」(靠港大圈)与「经过的航道」(航行条带)附近的迷雾才会揭开。
    //
    //   实现: 一颗比地球略大的球壳(半径 R + ShellY), 配 SEA/Fog 着色器 ——
    //   片元里把视线打回**半径 R 的地球**, 用交点的经纬采样这张「已探明网格」alpha 纹理,
    //   打不到地球的片元 discard。所以壳抬多高都不产生视差, 球缘外也不会多出黑边
    //   (为什么不沿用平面时代那块板: 见 Assets/Shaders/SeaFog.shader 的头注释)。
    //   渲染次序 3500 —— 压过陆/海/港圈/旗, 但压不过计划航线(3600)与船(3700)
    //   (那条明梯见 SeaRoute.shader / SeaPlay.ShipRenderQueue)。
    //
    //   网格按 2°×2° 一格覆盖**全球**经纬; 舰队逐帧以船位为中心抹开一圈圆盘(圆心跨过 1.2° 才重抹,
    //   避免每帧空扫); 边缘做 ~2 格羽毛渐变, 揭开处像真的雾在消散。探明网格随存读档
    //   (带尺寸标签的 base64 压进 SeaSaveData.fog), 认不出的旧档 = 尚未探索
    //   (读档后在到过的城各现形一圈)。
    // =============================================================
    public sealed class SeaFog
    {
        // 雾网格覆盖**整颗地球**的经纬范围, 不再只盖图幅:
        //   壳是按地表经纬采样探明图的, 而球上处处是海 —— 只盖图幅的话, 图幅外那 110° 空海
        //   会露出一大片没有雾的空白, 那里恰恰是"最没探明"的地方。整圈覆盖只需 180×90 格,
        //   比原来那片 250×163 的平面还小。
        public const float Lon0 = -180f, Lon1 = 180f;
        public const float Lat0 = -90f, Lat1 = 90f;

        const float CellDeg = 2f;        // 每探明格 = 2°×2° → 180×90 格
        // 雾壳抬升(沿球面法线): 下限是"必须盖住该被盖住的东西" —— 港球顶 ≈2.55、普通城旗顶 ≈1.8、
        //   大本营旗**杆**顶 4.25(baseY 0.35 + 杆高 2.6×1.5)。但最高的其实不是杆顶, 是那面旗布:
        //   它从杆顶往东伸出 3.0 并上扬, 临时诊断实测布心已在 5.0 上下、布顶约 5.5 —— **高过 4.8 的壳**。
        //   为什么这样也行: 旗布只长在**已探明**的地方 —— 大本营(开局就揭)、到过的城(到港那一刻揭,
        //   见 SeaPlay.Arrive / RevealFogAtPort)。
        //   壳真正要盖的是"未探明区里的东西", 那些地方只有港球/港圈/红绿旗, 全在 1.8 以下。
        //   上限是"相机绝不能钻进壳里" —— 壳是敞口球面, 相机一旦进去, 近半球被裁掉, 黑雾会整片消失。
        //   于是这个数必须 < SeaPlay 滚轮下限(6.0)。可用区间 ~2.55 … 6.0 很宽, 取中偏上即可。
        const float ShellY = 4.8f;
        const float MoveEps = 1.2f;      // 圆心挪过 1.2° 才重抹一圈(≥半格, 防每帧空扫)
        const float UploadGap = 0.09f;   // 纹理上传节流: ≤ ~11 次/秒, 抹开途中不卡
        const int Feather = 2;           // 边缘羽毛宽度(格): 最外圈淡出, 像雾在散

        // 存档标签: 版本 + 网格尺寸。尺寸写进串里, 于是"换过网格的旧档"**自己就认不出来**,
        //   不会把 250×163 的旧位图按 180×90 错位解开, 在球上摊出一片张冠李戴的探明区。
        const string Tag = "F2";
        const double Deg2Rad = Math.PI / 180.0;

        int W, H;
        bool[] _rev;          // 探明网格(W×H, row-major; j 行随纬度增 = 纹理 v 增)
        bool _dirty;
        float _lastUp;
        float _lcx = float.NegativeInfinity, _lcz = float.NegativeInfinity;   // 上次抹圈的圆心(经, 纬)
        float _lcr;                       // 上次抹圈的半径(= 该圆心已抹开的范围)
        Texture2D _tex;
        Renderer _ren;
        bool _ready;

        public bool Ready => _ready;
        public int CellW => W;
        public int CellH => H;

        // 把雾壳挂到世界根下。只建一次; 初始整片全黑。
        public void Build(Transform root)
        {
            W = Mathf.Max(1, Mathf.RoundToInt((Lon1 - Lon0) / CellDeg));
            H = Mathf.Max(1, Mathf.RoundToInt((Lat1 - Lat0) / CellDeg));
            _rev = new bool[W * H];
            ClearGrid();

            _tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            // 经度方向必须**环绕**: u=0 与 u=1 是同一条 ±180° 经线, 最左格与最右格在地球上相邻,
            //   双线性过滤要跨缝取样才不会露出一条直边。纬度方向两端是极点, 没有"下一格"可绕 → Clamp。
            _tex.wrapModeU = TextureWrapMode.Repeat;
            _tex.wrapModeV = TextureWrapMode.Clamp;
            _tex.filterMode = FilterMode.Bilinear;   // 跨格取样 → 羽边天然再柔一格
            PaintAllBlack();

            var sh = Shader.Find("SEA/Fog");
            if (sh == null)
            {
                // 打包时若没登记进 Always Included 就会被裁掉, 那时 Find 返回 null。
                //   直接报出来, 别默默建出一个粉色材质(那种"看得见但看不懂"的坏法最难查)。
                Debug.LogError("[SEA] 找不到 SEA/Fog 着色器 —— 黑雾无法成形。检查 SeaMenu.EnsureAlwaysIncludedShaders 是否登记了它。");
                return;
            }
            var mat = new Material(sh);
            mat.mainTexture = _tex;
            mat.color = Color.white;
            mat.renderQueue = 3500;                  // 压过陆/海/港圈/旗 → 未探明处一片黑; 之上的航线(3600)/船(3700)另算

            var go = new GameObject("FogShell");
            go.transform.SetParent(root, false);
            var mesh = BuildShellMesh();
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var ren = go.AddComponent<MeshRenderer>();
            ren.sharedMaterial = mat;
            ren.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ren.receiveShadows = false;
            _ren = ren;
            _ready = true;
        }

        // 雾壳网格: 半径 R+ShellY 的 UV 球。
        //   网格只负责"把像素喂给片元着色器", 真正的经纬是片元里现算的 ——
        //   所以这里**不需要 UV**, 也不必与探明网格同密度(2° 只是顺手取的一个够用的值):
        //   2° 的弦高 0.0087 单位, 折算到采样点上的角误差约 0.004°, 相对 2° 的格子可以忽略。
        Mesh BuildShellMesh()
        {
            const int cols = 180, rows = 90;   // 2° 一格(与探明网格同密度纯属巧合, 不是约束)
            int vw = cols + 1, vh = rows + 1;
            float rr = SeaGlobe.R + ShellY;
            var verts = new Vector3[vw * vh];
            for (int r = 0; r < vh; r++)
            {
                double lat = Lat1 - (Lat1 - Lat0) * r / rows;
                for (int c = 0; c < vw; c++)
                {
                    double lon = Lon0 + (Lon1 - Lon0) * c / cols;
                    // ToWorld 给的是**半径 R** 的球面点 → 归一化后按雾壳半径重铺
                    verts[r * vw + c] = SeaGlobe.ToWorld(lon, lat).normalized * rr;
                }
            }
            var tris = new int[cols * rows * 6];
            int t = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int a = r * vw + c, b = a + 1, d = a + vw, e = d + 1;
                    tris[t++] = a; tris[t++] = b; tris[t++] = d;
                    tris[t++] = b; tris[t++] = e; tris[t++] = d;
                }
            var mesh = new Mesh();
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---- 网格坐标 ↔ 经纬(格心口径: i 格覆盖 [Lon0+i·Cell, Lon0+(i+1)·Cell), 格心在 +半格处)
        static float LonOf(int i) => Lon0 + (i + 0.5f) * CellDeg;
        static float LatOf(int j) => Lat0 + (j + 0.5f) * CellDeg;
        // 经度的格**下标**要能环绕: ±180° 是同一条经线, 最左格与最右格在地球上是相邻的。
        //   (纬度不需要 —— 两端是极点, 没有"下一格"可绕。)
        int Wrap(int i) => ((i % W) + W) % W;
        int LonIdx(float lon) => Mathf.FloorToInt((lon - Lon0) / CellDeg);
        int LatIdx(float lat) => Mathf.FloorToInt((lat - Lat0) / CellDeg);

        // 全部探明格清空(全黑)。
        public void ClearGrid()
        {
            if (_rev == null) return;
            for (int n = 0; n < _rev.Length; n++) _rev[n] = false;
            _dirty = true;
        }

        // 以经纬点(lon, lat)为心、radiusDeg 为**球面角半径**抹开一圈。可反复调(同一圈单调累积)。
        //   为什么不能用经纬差当距离: 球上 1° 纬差到处都一样长, 1° 经差却要乘 cosφ ——
        //   60°N 上"按经纬差画出来的圆"在球面上是个又扁又窄的椭圆, 抹出来的洞和实际视野对不上。
        //   这里逐**行**解球面余弦定理(每行一次 acos, 不是逐格查角距, 便宜):
        //     cos c = sinφ₀·sinφ + cosφ₀·cosφ·cosΔλ ≤ cos r  ⇒  cosΔλ ≥ k,
        //     k = (cos r − sinφ₀·sinφ) / (cosφ₀·cosφ)
        //   k ≤ −1 → 该行整圈都在半径内; k ≥ 1 → 整圈都在外。拿到该行的经度半宽 Δλ,
        //   再用每个格心的真实经差卡边界。
        public void RevealAt(float lon, float lat, float radius)
        {
            if (!_ready) return;
            // 攒圈判定必须**连半径一起看**, 不能只看圆心挪没挪:
            //   每次靠港, 船就停在港上, 而进港前一瞬的航行条带(16°)已经以同一个点为中心抹过一次 ——
            //   圆心差不到 1.2° 是常事, 于是"到港揭一大圈"会被整个跳过, 到港反而比航行时看得还少。
            //   半径变大 = 有新地方要抹; 不变或更小 = 这一圈早被更大的圈包住了, 跳过是对的。
            if (Mathf.Abs(lon - _lcx) < MoveEps && Mathf.Abs(lat - _lcz) < MoveEps && radius <= _lcr + 0.01f) return;
            _lcx = lon; _lcz = lat; _lcr = radius;

            double phi0 = lat * Deg2Rad, rRad = radius * Deg2Rad;
            double c0 = Math.Cos(rRad), s0 = Math.Sin(phi0), cf0 = Math.Cos(phi0);
            int j0 = Mathf.Max(0, LatIdx(lat - radius));
            int j1 = Mathf.Min(H - 1, LatIdx(lat + radius));
            bool any = false;
            for (int j = j0; j <= j1; j++)
            {
                double phi = LatOf(j) * Deg2Rad;
                double dlamDeg;
                double denom = cf0 * Math.Cos(phi);
                if (Math.Abs(denom) < 1e-9)
                {
                    // 极点行(或圆心正在极点): 该行整圈到圆心的角距都一样, 只看纬度差够不够
                    if (Math.Abs(phi - phi0) > rRad) continue;
                    dlamDeg = 180.0;
                }
                else
                {
                    double k = (c0 - s0 * Math.Sin(phi)) / denom;
                    if (k <= -1.0) dlamDeg = 180.0;
                    else if (k >= 1.0) continue;
                    else dlamDeg = Math.Acos(k) / Deg2Rad;
                }
                int ic = LonIdx(lon);
                int half = (int)Math.Ceiling(dlamDeg / CellDeg);
                for (int d = -half; d <= half; d++)
                {
                    int i = Wrap(ic + d);
                    // 用格心的真实经差卡边界(Δλ 取环绕后的最短差), 不留半格毛边
                    double dl = Math.Abs(LonOf(i) - lon);
                    if (dl > 180.0) dl = 360.0 - dl;
                    if (dl > dlamDeg) continue;
                    int n = j * W + i;
                    if (!_rev[n]) { _rev[n] = true; any = true; }
                }
            }
            if (any) _dirty = true;
        }

        // 逐帧调用: 有改动且距上次上传够久才真的重算 alpha 并上传(抹开途中保持顺滑)。
        public void Tick(float dt)
        {
            if (!_ready || !_dirty) return;
            _lastUp += dt;
            if (_lastUp < UploadGap) return;
            _lastUp = 0f;
            Flush();
        }

        // 强制按当前网格重刷纹理(读档后 / 全清后调用)。
        public void Flush()
        {
            if (!_ready) return;
            RebuildAlpha();
            _dirty = false;
        }

        // 该经纬点是否已探明(问「这城/这片海能看见吗」)。
        public bool Revealed(float lon, float lat)
        {
            if (!_ready) return false;
            int j = LatIdx(lat);
            if (j < 0 || j >= H) return false;
            return _rev[j * W + Wrap(LonIdx(lon))];
        }

        // ---- 存档: 探明网格 → 位图 → base64。全黑 → 空串省空间。
        public string EncodeState()
        {
            if (!_ready) return "";
            bool any = false;
            for (int n = 0; n < _rev.Length; n++)
                if (_rev[n]) { any = true; break; }
            if (!any) return "";
            int nb = (_rev.Length + 7) >> 3;
            var b = new byte[nb];
            for (int n = 0; n < _rev.Length; n++)
                if (_rev[n]) b[n >> 3] |= (byte)(1 << (n & 7));
            return Tag + W + "x" + H + ":" + Convert.ToBase64String(b);
        }

        // 读档: 返回"这份雾数据认不认"。先全黑再按存档位点亮(存档为准 → 启动首页那几帧的现形不会带进读档)。
        //   认不出的(旧版本 / 换过网格尺寸 / 坏串)→ 返回 false, 由调用方退回"到过的城各揭一圈"。
        public bool DecodeState(string s)
        {
            if (!_ready) return false;
            ClearGrid();
            if (string.IsNullOrEmpty(s)) return false;
            string head = Tag + W + "x" + H + ":";
            if (!s.StartsWith(head, StringComparison.Ordinal)) return false;
            try
            {
                var b = Convert.FromBase64String(s.Substring(head.Length));
                for (int n = 0; n < _rev.Length && (n >> 3) < b.Length; n++)
                    if ((b[n >> 3] & (1 << (n & 7))) != 0) _rev[n] = true;
            }
            catch (Exception) { ClearGrid(); return false; }   // 坏串: 保持全黑, 并告诉调用方"没认出来"
            _dirty = true;
            return true;
        }

        void PaintAllBlack()
        {
            var px = new Color32[W * H];
            for (int n = 0; n < px.Length; n++) px[n] = new Color32(0, 0, 0, 255);
            _tex.SetPixels32(px);
            _tex.Apply(false, false);
        }

        // 由网格重算 alpha: 未探明 255; 探明区离未探明边界越近越淡(Feather 格内从 48→140 淡出), 内里 0。
        //   水平方向按 Wrap 取邻居 —— 这张图在球上是首尾相接的, 不环绕的话 ±180° 那条经线上
        //   会露出一条突兀的"羽毛断头"(球上没有缝, 只有这张图有)。
        void RebuildAlpha()
        {
            var px = new Color32[W * H];
            for (int j = 0; j < H; j++)
            {
                int row = j * W;
                for (int i = 0; i < W; i++)
                {
                    int n = row + i;
                    byte a;
                    if (!_rev[n]) a = 255;
                    else
                    {
                        int best = Feather + 1;   // 到最近未探明格的切比雪夫距离
                        for (int dj = -Feather; dj <= Feather && best > 1; dj++)
                        {
                            int jj = j + dj;
                            if (jj < 0 || jj >= H) continue;
                            int ad = dj < 0 ? -dj : dj;
                            if (ad >= best) continue;
                            int rowj = jj * W;
                            for (int di = -Feather; di <= Feather; di++)
                            {
                                int ii = Wrap(i + di);   // 经向环绕
                                int d = ad > (di < 0 ? -di : di) ? ad : (di < 0 ? -di : di);
                                if (d >= best) continue;
                                if (!_rev[rowj + ii]) { best = d; break; }
                            }
                        }
                        a = best >= Feather + 1 ? (byte)0 : (byte)(best == 1 ? 140 : 48);
                    }
                    px[n] = new Color32(0, 0, 0, a);
                }
            }
            _tex.SetPixels32(px);
            _tex.Apply(false, false);
        }
    }
}
