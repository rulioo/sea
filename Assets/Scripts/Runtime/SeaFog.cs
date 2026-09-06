using System;
using UnityEngine;

namespace Sea
{
    // =============================================================
    // SEA · 世界海图「黑雾遮罩 / 探索迷雾」
    //   一整张浮在世界海面上方的黑色雾罩, 把未探明的陆/海/港口/航线全部盖黑 ——
    //   只有舰队「到达的区域」(靠港大圈)与「经过的航道」(航行条带)附近的迷雾才会揭开。
    //
    //   实现: 一块纯色大平面(Sprites/Default 透明材质, 高 renderQueue 恒盖最上层),
    //   叠加一张「已探明网格」alpha 纹理 —— 探明格 alpha=0(透明露景), 未探明格 alpha=255(纯黑遮罩)。
    //   网格按 2 世界单位一格覆盖整片海面; 舰队逐帧以船位为中心抹开一圈圆盘(圆心跨过半格才重抹,
    //   避免每帧空扫); 边缘做 ~2 格羽毛渐变, 揭开处像真的雾在消散。探明网格随存读档(base64 压进
    //   SeaSaveData.fog), 旧档无 fog 字段 = 尚未探索(读档后在当前港自动现形一小圈)。
    // =============================================================
    public sealed class SeaFog
    {
        // 与 SeaPlay.BuildOcean 同源的海面边界(雾罩正好盖满, 揭洞永不出界)
        public const float WorldX0 = -235f, WorldX1 = 265f;
        public const float WorldZ0 = -150f, WorldZ1 = 175f;

        const float CellWorld = 2f;      // 每探明格 = 2 世界单位(≈2°) → 500×325 → 250×163 格
        const float SheetY = 7f;         // 雾罩高: 高过平陆(0.06)/港球顶(≈2.5)/旗杆顶(≈4.5), 仍低于镜头最低高度(≈9.9)
        const float MoveEps = 1.2f;      // 圆心挪过 1.2 世界单位才重抹一圈(≥½格, 防每帧空扫)
        const float UploadGap = 0.09f;   // 纹理上传节流: ≤ ~11 次/秒, 抹开途中不卡
        const int Feather = 2;           // 边缘羽毛宽度(格): 最外圈淡出, 像雾在散

        int W, H;
        bool[] _rev;          // 探明网格(W×H, row-major; y 行随世界 z 增 = 纹理 v 增)
        bool _dirty;
        float _lastUp;
        float _lcx = float.NegativeInfinity, _lcz = float.NegativeInfinity;   // 上次抹圈圆心
        Texture2D _tex;
        Renderer _ren;
        bool _ready;

        public bool Ready => _ready;
        public int CellW => W;
        public int CellH => H;

        // 把雾罩挂到世界根下。只建一次; 初始整片全黑。
        public void Build(Transform root)
        {
            W = Mathf.Max(1, Mathf.RoundToInt((WorldX1 - WorldX0) / CellWorld));
            H = Mathf.Max(1, Mathf.RoundToInt((WorldZ1 - WorldZ0) / CellWorld));
            _rev = new bool[W * H];
            ClearGrid();

            _tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            _tex.wrapMode = TextureWrapMode.Clamp;
            _tex.filterMode = FilterMode.Bilinear;   // 跨格取样 → 羽边天然再柔一格
            PaintAllBlack();

            var mat = new Material(Shader.Find("Sprites/Default"));
            mat.mainTexture = _tex;
            mat.color = Color.white;
            mat.renderQueue = 3500;                  // 压在所有透明(港圈/旗/船)之后 → 雾下任何东西都被盖死

            // 纯四边平面(无碰撞体 —— 雾绝不能挡港口点选), 顶点把整幅 uv 对齐世界 [X0..X1]×[Z0..Z1]
            var go = new GameObject("FogSheet");
            go.transform.SetParent(root, false);
            float cxx = (WorldX0 + WorldX1) * 0.5f, czz = (WorldZ0 + WorldZ1) * 0.5f;
            float hw = (WorldX1 - WorldX0) * 0.5f, hd = (WorldZ1 - WorldZ0) * 0.5f;
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(cxx - hw, 0f, czz - hd),
                    new Vector3(cxx + hw, 0f, czz - hd),
                    new Vector3(cxx - hw, 0f, czz + hd),
                    new Vector3(cxx + hw, 0f, czz + hd),
                },
                uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) },
                triangles = new[] { 0, 2, 1, 1, 2, 3 },   // 法线朝 +Y(高空俯视正面朝镜)
            };
            mesh.RecalculateBounds();
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var ren = go.AddComponent<MeshRenderer>();
            ren.sharedMaterial = mat;
            ren.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ren.receiveShadows = false;
            _ren = ren;
            go.transform.localPosition = new Vector3(0f, SheetY, 0f);
            _ready = true;
        }

        // 全部探明格清空(全黑)。
        public void ClearGrid()
        {
            if (_rev == null) return;
            for (int n = 0; n < _rev.Length; n++) _rev[n] = false;
            _dirty = true;
        }

        // 以世界点(wx, wz)为中心、radius 世界单位为半径抹开一圈探明。可反复调(同一圈单调累积)。
        public void RevealAt(float wx, float wz, float radius)
        {
            if (!_ready) return;
            if (Mathf.Abs(wx - _lcx) < MoveEps && Mathf.Abs(wz - _lcz) < MoveEps) return;
            _lcx = wx; _lcz = wz;

            float rr = radius * radius;
            int i0 = Mathf.Max(0, (int)Mathf.Floor((wx - radius - WorldX0) / CellWorld));
            int i1 = Mathf.Min(W - 1, (int)Mathf.Ceil((wx + radius - WorldX0) / CellWorld));
            int j0 = Mathf.Max(0, (int)Mathf.Floor((wz - radius - WorldZ0) / CellWorld));
            int j1 = Mathf.Min(H - 1, (int)Mathf.Ceil((wz + radius - WorldZ0) / CellWorld));
            bool any = false;
            for (int j = j0; j <= j1; j++)
            {
                float dz = WorldZ0 + (j + 0.5f) * CellWorld - wz;
                float dzz = dz * dz;
                for (int i = i0; i <= i1; i++)
                {
                    float dx = WorldX0 + (i + 0.5f) * CellWorld - wx;
                    if (dx * dx + dzz <= rr)
                    {
                        int n = j * W + i;
                        if (!_rev[n]) { _rev[n] = true; any = true; }
                    }
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

        // 某世界点是否已探明(读入端口球顶坐标即可问「这城能看见吗」)。
        public bool RevealedXZ(float wx, float wz)
        {
            if (!_ready) return false;
            int i = (int)Mathf.Floor((wx - WorldX0) / CellWorld);
            int j = (int)Mathf.Floor((wz - WorldZ0) / CellWorld);
            if (i < 0 || i >= W || j < 0 || j >= H) return false;
            return _rev[j * W + i];
        }

        // ---- 存档: 探明网格 → 位图 → base64(空格约 6~7KB)。全黑 → 空串省空间。
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
            return Convert.ToBase64String(b);
        }

        // 读档: 先全黑再按存档位点亮(存档为准 → 启动首页那几帧的现形不会带进读档)。
        public void DecodeState(string s)
        {
            if (!_ready) return;
            ClearGrid();
            if (string.IsNullOrEmpty(s)) return;
            try
            {
                var b = Convert.FromBase64String(s);
                for (int n = 0; n < _rev.Length && (n >> 3) < b.Length; n++)
                    if ((b[n >> 3] & (1 << (n & 7))) != 0) _rev[n] = true;
            }
            catch (Exception) { /* 坏串: 保持全黑即可 */ }
            _dirty = true;
        }

        void PaintAllBlack()
        {
            var px = new Color32[W * H];
            for (int n = 0; n < px.Length; n++) px[n] = new Color32(0, 0, 0, 255);
            _tex.SetPixels32(px);
            _tex.Apply(false, false);
        }

        // 由网格重算 alpha: 未探明 255; 探明区离未探明边界越近越淡(Feather 格内从 48→140 淡出), 内里 0。
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
                                int ii = i + di;
                                if (ii < 0 || ii >= W) continue;
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
