using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sea
{
    // =============================================================
    // SEA · M2 可玩垂直切片(航海 + 进港交易闭环)
    //   - 纯代码搭 3D 海图: 着色器海面 + 按航区着色的港口标记 + 旗舰/僚舰船模
    //   - 状态机: 泊港 ⇄ 海上
    //       泊港: 点选别港作目标 → 「出航」; 到港结薪/治病(复用 M1 FleetOps)
    //       海上: 逐"游戏日"推进市场(SimEngine.AdvanceDays)与舰队消耗
    //             (FleetOps.AdvanceOneSeaDay); 每日按危险度掷遭遇, 命中即自动结算
    //   - 交易 = SimEngine.Buy/Sell × 舰队舱位(AddCargo/RemoveUnit), 现金在 FleetState.Gold
    //   - 未引入任何外部素材; 尺寸/数值沿用 M1 占位, 先求"能玩、好看、好调"
    // =============================================================
    [DefaultExecutionOrder(-90)]
    public sealed class SeaPlay : MonoBehaviour
    {
        // 泊港(在城里) ⇄ 海上(驶往某城或某海图点) ⇄ 抛锚(到海图点停船, 不靠港)
        public enum Mode { Docked, Sailing, Anchored }

        // ---------------- 开局 ----------------
        public int seed = 15500712;
        public long startGold = 250000;
        public string homePortId = "seville";
        public string flagshipId = "m3_flute";   // 北欧轻快货船: 航速9 载260 水手40..120
        public string escortId = "m1_skiff";     // 近海小货: 载30 水手5..18

        // ---------------- 数据/状态 ----------------
        public World world;
        public SimEngine engine;
        public FleetCatalog cat;
        public FleetState fleet;
        public string LoadError { get; private set; }
        public SeaAudio audio;   // 同物体上的音频管理器(音效/音乐), 启动时 Ensure

        public Port Current { get; private set; }
        public Port Dest { get; private set; }
        // ---- 探索模式: 目标不是某座确定城, 而是海图上任意一点 —— 点海面插红旗, 船驶到那儿抛锚 ----
        public bool ExploreSet { get; private set; }        // 当前目标是海图上插旗处(非港口)
        public Vector3 ExploreWorld { get; private set; }   // 插旗/锚地世界坐标
        public bool HasSailTarget => Dest != null || ExploreSet;   // 有没有可出航的目标(城或旗点)
        public Mode State { get; private set; } = Mode.Docked;

        // 航行进度(供 UI 与相机)
        public float SailProgress01 { get; private set; }
        public int PlannedDays { get; private set; }
        public int DaysDone { get; private set; }
        public Vector3 DepartWorld { get; private set; }
        public Vector3 DestWorld { get; private set; }

        // 事件横幅 + 日志(供 UI): Log[0]=最新; LogDay 与之等长同位, 记每条日志发生时的"游戏日"
        public string Banner { get; set; }
        public readonly List<string> Log = new List<string>();
        public readonly List<int> LogDay = new List<int>();
        public int LogVersion;          // 每推一条日志 +1 → HUD 只在日志真变化时才重建
        const int MaxLogLines = 1500;   // 日志最多留最近这么多条(够回翻很多天)
        public Port LastBattleAt { get; private set; }

        // ---------------- 视觉 ----------------
        Transform _root;
        readonly Dictionary<string, Transform> _markers = new Dictionary<string, Transform>();
        readonly Dictionary<string, Color> _areaColor = new Dictionary<string, Color>();

        // ---- 地图旗标(设置开关): 大本营(homePortId)一面稍大黄色旗; 每新抵达城市插一面小红旗 ----
        public bool FlagsOn = true;                              // 设置里的开关, 随存读档
        readonly HashSet<string> _cityFlags = new HashSet<string>();   // 已抵达过的城市 id(红旗; 大本营不算)
        readonly Dictionary<string, Transform> _flagRoots = new Dictionary<string, Transform>();   // 港 id → 旗标根
        readonly Dictionary<string, Transform> _flagCloth = new Dictionary<string, Transform>();  // 港 id → 旗布(逐帧轻摆)
        readonly Dictionary<string, float> _flagPhase = new Dictionary<string, float>();          // 港 id → 摆动相位(错开, 别齐刷刷)
        readonly List<string> _areaOrder = new List<string>();
        SeaFog _fog;                                 // 黑雾遮罩(探索迷雾): 见 SeaFog.cs, 随存读档
        const float FogSailR = 16f;   // 航行时以船位为心揭开"航道条带"半径(两侧探明 ~16°) —— 海图逐程被自己走开
        const float FogDockR = 40f;   // 靠港/开局以港为心再亮一大圈(40°), 便于看图定下一程
        const float FogAnchorR = 24f; // 抛锚停船: 在船位再亮一小圈, 够看清下一点(不必像进港那样一大片)

        Transform _exploreFlag;        // 🚩 探索红旗: 选中的海图点 / 抛锚地标(杆+正红燕尾旗+水面红光环)
        Vector3 _anchor;               // 抛锚地: 到海图点停船后船所在(随波轻晃的中心), 下次续航从这儿出发
        Transform _ship;
        readonly List<Transform> _shipSails = new List<Transform>();
        Camera _cam;
        Vector3 _target = Vector3.zero;
        float _dist = 220f;   // 镜头到焦点的距离(≈缩放: 越小越近); 世界总览/出航/到港时按需赋值
        float _pitch = 82f;   // 俯角: 高空近乎正俯 → 一张"北在上、东在右"的海图, 不再绕视

        // 平陆(与 SeaMapGen 陆地真源同源)
        const double LandGridDeg = 0.5;   // 每 0.5° 一个网格顶点 → 500×220
        const float LandTopY = 0.06f;     // 陆面高: 几乎贴着海面的"平陆", 俯视像一张摊开的地图(无凸起方块山)
        const float SeaFloorY = -0.85f;   // 海床高(藏在水面下, 岸坡由此斜上陆面)
        const float BallFloatY = 1.2f;    // 港口光球的"球底"世界高度: 一定高于平陆与浪脊, 永不被大陆遮挡
        const float ShipVisualScale = 0.6f;   // 船模整体缩小(相对海图/港口球的比例更真实, 不显"巨舰")
        const float ShipTurnRate = 70f;       // 海上转向限速(°/秒): 转弯是渐转, 不再每帧甩头/来回摆
        const float FlagTilt = -28f;          // 旗面扬起角(外端略上扬, 高空俯视也像一面小旗而非地贴)

        // 航行计时
        float _voyageT;
        SeaRouteData _route;        // 本次航行的沿海海路(SeaRoute 缓存); 兜底为直线
        string _departAreaId;
        System.Random _pirateRng = new System.Random(7);

        // 鼠标拖动海图: 按下移动超阈值 = 拖拽平移; 松手且没拖动 = 点选港口
        bool _downHeld;
        bool _dragPan;
        Vector2 _downScreen;
        bool _midHeld;            // 中键按住平移中
        Vector3 _grabFocus;       // 按下瞬间的相机焦点(预留)
        Vector3 _grabGround;      // 按下瞬间光标所指的"地平面点"(预留)
        Vector2 _panPrev;         // 上一帧光标位置 → 增量平移用(每帧只加"上一光标→本光标"这段地面位移, 避免旧锚反馈振荡=抖)
        SeaHud _hud;              // 顶层层闸: 浮层开着时, 底下地图输入(缩放/拖移/点选)全停
        SeaHome _home;            // 启动首页(黑底 logo + 新开航程/继续辉煌/设置/退出)
        SeaSettings _settings;    // 设置弹层(置顶 + 窗口模式; AnyTopOpen 用它闸地图输入/藏地名)
        public bool SettingsOpen => _settings != null && _settings.OpenNow;

        // 持仓成本账本(行情表"成本"列 = 件均买入成本): 买多少件累计花了多少钱
        readonly Dictionary<string, long> _basisQty = new Dictionary<string, long>();
        readonly Dictionary<string, long> _basisCost = new Dictionary<string, long>();

        // 情报站 · 逐游戏日"全港进价"采样(本局累计, 供《全球分析报》画趋势; 读档后从头再累计)
        //   每个样本 = 一维数组, 下标 pi*Goods.Count+gi, 值 = 当日该港该货的 AskPrice(进货价)
        readonly List<int[]> _histAsk = new List<int[]>();   // 时间从旧到新
        readonly List<int> _histDay = new List<int>();       // 与样本一一对应的 engine.Day
        const int IntelHistCap = 60;                          // 只留最近 60 个游戏日样本(≈两个月)
        Dictionary<string, int> _portNo, _goodNo;             // id → 下标(惰性建)
        public readonly List<SeaIntelSave> IntelArchive = new List<SeaIntelSave>();  // 已购报刊档案: 贸易日报(正文快照)+ 商情报(订阅记录), 随存读档

        // =============================================================
        // 生命周期
        // =============================================================
        void Awake()
        {
            // 旧版 SeaGame 调试面板自动创建于场景时移除, 避免双地图双 UI
            foreach (var g in FindObjectsByType<SeaGame>(FindObjectsSortMode.None)) Destroy(g.gameObject);
            // 显示模式: 启动默认全屏(玩家存过「窗口模式」才窗口化 → 顶部出现标题栏)
            SeaSettings.ApplyStartupDisplay();
        }

        void Start()
        {
            // 顶层层闸: 浮层开着时吞掉底下地图的相机输入(SeaHud 由同物体的 SeaLauncher 负责挂)
            _hud = GetComponent<SeaHud>();

            // 相机兜底
            if (Camera.main != null) _cam = Camera.main;
            else
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                _cam = go.AddComponent<Camera>();
            }
            _cam.backgroundColor = new Color(0.012f, 0.05f, 0.10f);
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 2000f;
            _cam.orthographic = false;   // 透视但近乎正俯, 保住景深与立体船
            _cam.fieldOfView = 60f;

            audio = SeaAudio.Ensure(this);
            _settings = GetComponent<SeaSettings>() ?? gameObject.AddComponent<SeaSettings>();

            if (!BootData())
            {
                ShowHome();
                HomeHint("数据装载失败: " + LoadError);
                return;
            }
            BuildWorldVisual();
            BuildFleetVisual();
            BuildFogVisual();   // 海图盖一层黑雾(初始全黑; 开局在自家港自动现形一圈, 见 Update)
            FrameHome();
            ApplyCamera(_target);   // 首帧: 就近看船和港口, 不等 Update 才摆
            StartWorth = NetWorth();
            PushIntroLogs();
            SnapMarketDay();   // 情报站行情档案从开局当日起逐日留档
            ShowHome();   // 启动首页: 黑底盖住海图, 由玩家点「新开航程/继续辉煌」才真正开局(首页曲 = head.wav)
        }

        // =============================================================
        // 启动首页(新开航程 / 继续辉煌 / 设置 / 退出)
        // =============================================================
        void ShowHome()
        {
            if (_home == null) _home = GetComponent<SeaHome>() ?? gameObject.AddComponent<SeaHome>();
            if (SeaAudio.Instance != null) SeaAudio.Instance.PlayBgmByName("head");   // 首页固定播片头曲
            _home.Open();
        }
        void HomeHint(string msg)
        {
            if (_home != null && !string.IsNullOrEmpty(msg)) _home.Hint(msg);
        }

        // 「新开航程」: Start 已在底下搭好一个全新白手局面, 揭开幕布即可玩
        public void StartNewVoyage()
        {
            if (world == null || fleet == null) { HomeHint("开局失败: " + (LoadError ?? "世界未就绪")); return; }
            StartFlagsOn();                            // 启动默认点亮地图旗标(大本营黄旗/到过红旗)
            if (audio != null) audio.PlayBgmDefault();   // 离开主页 → 主题曲(玩家在设置里选过则用所选)
            _home?.Hide();
        }

        // 「继续辉煌」: 读档回上次局面; 没档/坏档留在首页给提示
        public void ContinueVoyage()
        {
            if (LoadGame())
            {
                StartFlagsOn();                        // 读档起步同样默认点亮旗标
                if (audio != null) audio.PlayBgmDefault();
                _home?.Hide();
            }
            else HomeHint(Banner);
        }

        // 开局/读档起步: 一律把地图旗标拨回"开"并立即应用(随后的存档也会把这状态记下)
        void StartFlagsOn()
        {
            if (!FlagsOn)
            {
                FlagsOn = true;
                ApplyCityFlags();
            }
        }

        // 开局那两句日志(与可能的"重开"共用同一份话术)
        void PushIntroLogs()
        {
            PushLog("新舰长入港。当前在 " + Current.Name + " · " + Current.Area.Name + ", 现金 " + Money(fleet.Gold));
            PushLog("玩法: 开行情 → 买这里盛产(◆)的货 → 点海图上远处港口 → 出航 → 到港高价卖出(▲ 抢手)。");
        }

        bool BootData()
        {
            LoadError = null;
            if (!SeaBootstrap.TryLoad(seed, out world, out engine, out var err)) { LoadError = err; return false; }
            if (!SeaFleetLoader.TryLoad(WeightOfWorld, out cat, out var t, out var ferr)) { LoadError = ferr; return false; }

            var h = world.FindPort(homePortId);
            if (h == null) { LoadError = "homePortId 不存在: " + homePortId; return false; }
            Current = h;

            // 船队: 旗舰 + 一艘小僚舰, 初始水手取中位, 现金给定
            fleet = new FleetState { Cat = cat, Gold = startGold, WeightOf = WeightOfWorld };
            var fm = cat.FindShip(flagshipId);
            var em = cat.FindShip(escortId);
            if (fm == null || em == null) { LoadError = "开局船 id 缺失"; return false; }
            FleetBuilder.BuildShip(fleet, flagshipId, (fm.CrewMin + fm.CrewMax) / 2, flagship: true);
            FleetBuilder.BuildShip(fleet, escortId, em.CrewMin);
            fleet.Morale = cat.Tuning.MoraleMax;
            fleet.Bonus = Sea.FleetBonus.From(null);
            return true;
        }

        int WeightOfWorld(string goodId)
        {
            if (world != null && world.GoodById.TryGetValue(goodId ?? "", out var g)) return g.Weight;
            return 1;
        }

        void Update()
        {
            if (world == null || fleet == null) return;
            HandleCamAndPick();
            TickFlagWaver();
            if (State == Mode.Sailing) TickSailing();
            else if (State == Mode.Docked) PlaceShipAt(Current, Time.time);
            else AnchorBob(Time.time);   // Anchored: 船在海图点下抛锚, 随波轻晃不挪位
            if (_fog != null && _ship != null)   // 迷雾逐帧以船位为中心揭圈(航行=航道条带; 泊港=视野大圈; 抛锚=中等圈)
            {
                float rr = State == Mode.Sailing ? FogSailR : State == Mode.Docked ? FogDockR : FogAnchorR;
                _fog.RevealAt(_ship.position.x, _ship.position.z, rr);
                _fog.Tick(Time.deltaTime);
            }
        }

        // =============================================================
        // 港口标记 / 世界视觉
        // =============================================================
        public static Vector3 LonLatToWorld(Port p) => new Vector3(p.Lon, 0f, -p.Lat);

        // 黑雾遮罩就位(与海面同界的一大块; 初始全黑, 见 SeaFog.cs)
        void BuildFogVisual()
        {
            if (_fog != null) return;
            _fog = new SeaFog();
            _fog.Build(_root);
        }

        // 该港头顶是否已探明(黑雾已掀开、能看见它)? 没建雾(理论不会)一律视为可见。
        public bool CityRevealed(Port p)
        {
            if (p == null) return false;
            if (_fog == null) return true;
            return _fog.RevealedXZ(p.Lon, -p.Lat);
        }

        void BuildWorldVisual()
        {
            _root = new GameObject("SeaMap").transform;
            BuildOcean();        // 程序化海面网格(比陆地外扩一圈; 无碰撞 → 点选交给港口球标)
            BuildLandVisual();   // 凸起大陆(用 SeaMapGen 同一份陆地真源)

            var palette = new[]
            {
                new Color(0.36f, 0.66f, 0.95f), new Color(0.95f, 0.55f, 0.35f),
                new Color(0.45f, 0.85f, 0.55f), new Color(0.93f, 0.80f, 0.35f),
                new Color(0.85f, 0.45f, 0.62f), new Color(0.62f, 0.52f, 0.92f),
                new Color(0.40f, 0.82f, 0.85f), new Color(0.72f, 0.60f, 0.44f),
            };
            foreach (var p in world.Ports)
                if (!_areaColor.ContainsKey(p.Area.Id))
                {
                    _areaColor[p.Area.Id] = palette[_areaOrder.Count % palette.Length];
                    _areaOrder.Add(p.Area.Id);
                }

            foreach (var p in world.Ports)
                _markers[p.Id] = BuildMarker(p);

            BuildCityFlags();   // 城市旗标(黄=大本营 / 红=到过)也随世界一起挂好, 初始只亮大本营
        }

        // 程序化海面网格: 比陆地(地图)四周外扩 ~65-75°, 斜俯视"世界总览"时海图边缘不会露出黑边。
        // 无碰撞体 —— 海面不该挡住港口点选; 波浪仍由 SEA/Water 在顶点上按世界坐标算。
        void BuildOcean()
        {
            const float x0 = -235f, x1 = 265f;   // 世界 x(=经度): 总览俯视(北在上)时左右也不露背景
            const float z0 = -150f, z1 = 175f;   // 世界 z(=-纬度): 南(近侧)透视拉得更开, 底边多留些海
            const float cell = 8f;               // 网格 8 单位/格 → 波浪采样足够密
            int cols = Mathf.Max(4, (int)Mathf.Round((x1 - x0) / cell));
            int rows = Mathf.Max(4, (int)Mathf.Round((z1 - z0) / cell));
            int vw = cols + 1, vh = rows + 1;
            var verts = new Vector3[vw * vh];
            for (int r = 0; r < vh; r++)
            {
                float z = z0 + (z1 - z0) * r / rows;
                for (int c = 0; c < vw; c++)
                {
                    float x = x0 + (x1 - x0) * c / cols;
                    verts[r * vw + c] = new Vector3(x, -0.05f, z);
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

            var go = new GameObject("Ocean");
            go.transform.SetParent(_root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var sh = Shader.Find("SEA/Water");
            var waterMat = sh != null ? new Material(sh) : StdMat(new Color(0.05f, 0.24f, 0.32f));
            var wren = go.AddComponent<MeshRenderer>();
            wren.sharedMaterial = waterMat;
            wren.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            wren.receiveShadows = false;
            if (sh != null)   // 风格化微调: 涌浪放低缓, 高空俯视才有"一张安静海图"的底色, 船破浪感仍在
            {
                waterMat.SetFloat("_Speed", 0.5f);
                waterMat.SetFloat("_Freq", 0.30f);
                waterMat.SetFloat("_Amp", 0.04f);   // 振幅压到很低 → 浪脊最高不超过 +0.03, 平陆(0.06)永不被浪淹
            }
        }

        // 用 SeaMapGen 的陆地真源烘一张"凸起大陆"地形网格:
        //   陆格顶点抬到 LandTopY, 海底顶点沉到 SeaFloorY → 每段海岸是斜/陡坡岸;
        //   顶面按 积雪带/干旱区/温带绿 着色, 海岸一圈顶点调成沙色, 海底用中性岩色(免得深崖发黑)。
        //   加碰撞体? 不 —— 点选仍交给港口球标。
        void BuildLandVisual()
        {
            double lon0 = SeaMapGen.Lon0, lat0 = SeaMapGen.Lat0;
            double lon1 = SeaMapGen.Lon1, lat1 = SeaMapGen.Lat1;
            int cols = Mathf.Max(2, (int)Math.Round((lon1 - lon0) / LandGridDeg));  // 250° → 500 列
            int rows = Mathf.Max(2, (int)Math.Round((lat1 - lat0) / LandGridDeg));  // 110° → 220 行
            int vw = cols + 1, vh = rows + 1, vn = vw * vh;
            double dLon = (lon1 - lon0) / cols, dLat = (lat1 - lat0) / rows;

            var pos = new Vector3[vn];
            var col = new Color32[vn];
            var isLand = new bool[vn];
            var sand = new bool[vn];
            for (int y = 0; y < vh; y++)
            {
                double lat = lat0 + y * dLat;
                int row = y * vw;
                for (int x = 0; x < vw; x++)
                {
                    int i = row + x;
                    bool land = SeaMapGen.LandAt(lon0 + x * dLon, lat);
                    isLand[i] = land;
                    pos[i] = new Vector3((float)(lon0 + x * dLon), land ? LandTopY : SeaFloorY, (float)-lat);
                }
            }
            // 海岸沙带: 陆顶格只要 8 邻域有一格是海(或顶到地图边)就作岸
            for (int y = 0; y < vh; y++)
            {
                int row = y * vw;
                for (int x = 0; x < vw; x++)
                {
                    int i = row + x;
                    if (!isLand[i]) continue;
                    bool shore = false;
                    for (int dy = -1; dy <= 1 && !shore; dy++)
                        for (int dx = -1; dx <= 1 && !shore; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= vw || ny >= vh) { shore = true; break; }
                            if (!isLand[ny * vw + nx]) shore = true;
                        }
                    sand[i] = shore;
                }
            }
            var snowC = new Color32(222, 234, 244, 255);   // 极地积雪(高纬 / 北美北端)
            var aridC = new Color32(166, 141, 98, 255);    // 干旱沙土(沙漠/中亚/澳洲内陆)
            var greenC = new Color32(76, 112, 58, 255);    // 温带/亚热带绿
            var sandC = new Color32(206, 182, 128, 255);   // 海岸沙
            var rockC = new Color32(70, 80, 78, 255);      // 海底中性岩
            for (int y = 0; y < vh; y++)
            {
                double lat = lat0 + y * dLat;
                int row = y * vw;
                for (int x = 0; x < vw; x++)
                {
                    int i = row + x;
                    double lon = lon0 + x * dLon;
                    if (!isLand[i]) { col[i] = rockC; continue; }
                    bool snowy = lat > 66.0 || (lat > 63.0 && lon < -20.0);
                    Color32 c = snowy ? snowC : (SeaMapGen.Arid(lon, lat) ? aridC : greenC);
                    if (sand[i])
                        c = Color32.Lerp(c, snowy ? new Color32(236, 242, 247, 255) : sandC, 150);
                    // 确定性微噪, 让大片平原有颗粒感、不单调
                    double h = Math.Abs(Math.Sin(lon * 127.1 + lat * 311.7) * 43758.5453);
                    int nz = (int)((h - Math.Floor(h) - 0.5) * 22.0);
                    col[i] = new Color32((byte)Mathf.Clamp(c.r + nz, 0, 255),
                                         (byte)Mathf.Clamp(c.g + nz, 0, 255),
                                         (byte)Mathf.Clamp(c.b + nz, 0, 255), 255);
                }
            }
            var tris = new int[cols * rows * 6];
            int t = 0;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    int a = y * vw + x, b = a + 1, c = a + vw, d = c + 1;
                    tris[t++] = a; tris[t++] = b; tris[t++] = c;   // 顶面朝 +Y
                    tris[t++] = b; tris[t++] = d; tris[t++] = c;
                }

            var mesh = new Mesh();
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;   // >65k 顶点必需 32 位索引
            mesh.vertices = pos;
            mesh.colors32 = col;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();

            var go = new GameObject("Continents");
            go.transform.SetParent(_root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var sh = Shader.Find("SEA/Land");
            var ren = go.AddComponent<MeshRenderer>();
            ren.sharedMaterial = sh != null ? new Material(sh) : StdMat(new Color(0.42f, 0.48f, 0.35f));
            ren.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ren.receiveShadows = false;
        }

        Transform BuildMarker(Port p)
        {
            var go = new GameObject("marker_" + p.Id);
            go.transform.SetParent(_root, false);
            go.transform.position = LonLatToWorld(p);

            float r = MarkerRadius(p);                     // 比初版略大, 近景/俯视都好点选
            var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.name = "ball";
            ball.transform.SetParent(go.transform, false);
            ball.transform.localScale = Vector3.one * r;   // 球网格半径=0.5×缩放 → 视觉半径 r/2
            // 球底定在 BallFloatY(1.2): 平陆(0.06)与浪脊(+0.03)都远在其下 → 光球永远悬在大陆上方, 不被遮挡
            ball.transform.localPosition = new Vector3(0f, BallFloatY + r * 0.5f, 0f);
            var col = ball.GetComponent<Collider>();
            if (col) { col.isTrigger = true; ball.name = p.Id; }
            var ren = ball.GetComponent<Renderer>();
            if (ren)
            {
                var c = _areaColor[p.Area.Id];
                ren.material = StdMat(c);
                ren.material.SetColor("_EmissionColor", c * 0.55f);
                ren.material.EnableKeyword("_EMISSION");
            }

            // 水面"锚点光环"
            var ring = new GameObject("ring").AddComponent<LineRenderer>();
            ring.transform.SetParent(go.transform, false);
            var lr = ring.GetComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = 33;
            float rr = r * 1.35f;
            for (int i = 0; i <= 32; i++)
            {
                float a = i / 32f * Mathf.PI * 2f;
                lr.SetPosition(i, new Vector3(Mathf.Cos(a) * rr, 0.40f, Mathf.Sin(a) * rr));   // 浮在平陆与浪脊之上
            }
            lr.loop = true;
            lr.startColor = lr.endColor = Color.Lerp(_areaColor[p.Area.Id], Color.white, 0.25f);
            lr.startWidth = lr.endWidth = 0.06f;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.material.color = Color.white;
            return go.transform;
        }

        public Color PortColor(Port p)
        {
            Color c; return _areaColor.TryGetValue(p.Area.Id, out c) ? c : Color.white;
        }
        public Transform MarkerTransform(string portId)
        {
            Transform t; return _markers.TryGetValue(portId, out t) ? t : null;
        }

        // 光球半径/顶点世界高 —— 供 HUD 把地名标签浮在光球之上(与 BuildMarker 同源, 不漂移)
        float MarkerRadius(Port p)
        {
            return (0.42f + p.Size * 0.15f) * 1.15f;
        }
        public float MarkerBallTopY(Port p)
        {
            return BallFloatY + MarkerRadius(p);
        }

        // =============================================================
        // 城市旗标: 大本营(homePortId)一面稍大金黄旗; 每个到过的城市一面正红小旗。
        //   每港 = 一根细旗杆 + 旗杆顶一面"燕尾帆船旗"(外端开叉, 底下垫一片略大的深色同形 = 勾边,
        //   底色正红/金黄在图上边界清晰), 旗布平时略上扬、逐帧随风轻摆。
        //   亮不亮由设置开关 FlagsOn + 港状态决定, 进港 / 读档 / 切开关后 ApplyCityFlags 刷新。
        // =============================================================
        static readonly Color ColWoodFlag = new Color(0.42f, 0.29f, 0.17f);
        static readonly Color ColRedFlag = new Color(0.86f, 0.08f, 0.06f);      // 正红(纯红系, 不再偏橙)
        static readonly Color ColHomeFlag = new Color(1f, 0.80f, 0.14f);        // 金黄(大本营)
        static readonly Color ColEdgeFlag = new Color(0.09f, 0.07f, 0.05f);     // 勾边: 深暖褐近黑, 垫在旗布下方一圈

        void BuildCityFlags()
        {
            _flagRoots.Clear();
            _flagCloth.Clear();
            _flagPhase.Clear();
            if (world == null) return;
            foreach (var p in world.Ports)
            {
                if (!_markers.TryGetValue(p.Id, out var m) || m == null) continue;
                _flagRoots[p.Id] = BuildCityFlag(p, m, p.Id == homePortId);
            }
            ApplyCityFlags();
        }

        Transform BuildCityFlag(Port p, Transform marker, bool home)
        {
            var root = new GameObject(home ? "flag_home" : "flag").transform;
            root.SetParent(marker, false);
            root.gameObject.SetActive(false);

            float s = home ? 1.5f : 0.85f;       // 大本营旗稍大, 普通到过旗小
            float H = (home ? 2.6f : 1.7f) * s;   // 旗杆高
            float L = (home ? 2.0f : 1.35f) * s;  // 燕尾旗伸出长(沿 +Z)
            float W = (home ? 1.15f : 0.8f) * s;  // 旗杆端高度(hoist)
            const float baseY = 0.35f;            // 旗杆扎根于水面之上(浪高≈0.03, 淹不到)

            // 旗杆立在港球一侧(向 +X 东侧移开), 不与球顶的港名标签抢正上方位置
            float side = MarkerRadius(p) + (home ? 0.7f : 0.4f);
            root.localPosition = new Vector3(side, 0f, 0f);

            var pole = Box(StdMat(ColWoodFlag), new Vector3(0.1f, H, 0.1f));
            pole.SetParent(root, false);
            pole.localPosition = new Vector3(0f, baseY + H * 0.5f, 0f);

            var cloth = PennantMesh(home ? ColHomeFlag : ColRedFlag, L, W * 0.5f);
            cloth.SetParent(root, false);
            cloth.localPosition = new Vector3(0f, baseY + H, 0f);
            cloth.localRotation = Quaternion.Euler(FlagTilt, 0f, 0f);   // 旗面略上扬: 高空俯视像一面小旗

            // 记下布面 → 逐帧轻摆; 相位按港 id 打散, 别整片齐刷刷
            _flagCloth[p.Id] = cloth;
            float ph = 0f;
            for (int k = 0; k < p.Id.Length; k++) ph += (p.Id[k] - 'a') * (k + 1);
            _flagPhase[p.Id] = Mathf.Repeat(ph * 0.13f, Mathf.PI * 2f);
            return root;
        }

        // 一面扬起的"燕尾帆船旗"(像帆船尾挂的小旗: 杆端满高, 外端开成两角):
        //   主色(正红 / 金黄)下方先垫一片略大的同形深色 = 勾边, 色块在图上边界利落。
        //   上扬角与轻摆由父级 cloth 的 rotation 逐帧调(见 TickFlagWaver)。
        Transform PennantMesh(Color c, float len, float halfW)
        {
            var go = new GameObject("cloth");
            float notch = Mathf.Min(len * 0.5f, halfW * 1.7f);   // 燕尾开叉深度(沿旗长向内切)
            const float edgeK = 1.15f;                            // 勾边同形放大系数
            AddClothPly(go, ColEdgeFlag, new Vector3(0f, -0.02f, 0f),
                len * edgeK, halfW * edgeK, notch * edgeK);
            AddClothPly(go, c, Vector3.zero, len, halfW, notch);
            return go.transform;
        }

        // 一片燕尾布面(平面 XZ, +y 法线朝上): 布面不会只有一块三角, 外端开叉 → 更像船旗
        void AddClothPly(GameObject parent, Color c, Vector3 localPos, float len, float halfW, float notch)
        {
            var go = new GameObject("ply");
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPos;
            var mf = go.AddComponent<MeshFilter>();
            float apex = len - notch;   // 叉内凹点到杆端的距离
            // 轮廓: 杆端上下角 → 外端上下两尾尖, 中间内凹到叉根: 一块矩形切掉中央三角
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(halfW, 0f, 0f),     // 0 杆端上角
                    new Vector3(-halfW, 0f, 0f),    // 1 杆端下角
                    new Vector3(halfW, 0f, len),    // 2 外端上尾尖
                    new Vector3(0f, 0f, apex),      // 3 叉内凹点(开叉根部)
                    new Vector3(-halfW, 0f, len)    // 4 外端下尾尖
                },
                triangles = new[] { 0, 1, 2, 1, 4, 2, 2, 4, 3 }
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mf.mesh = mesh;

            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            var mat = StdMat(c);
            mat.SetColor("_EmissionColor", c * 0.45f);
            mat.EnableKeyword("_EMISSION");
            mr.material = mat;
        }

        // 逐帧让各旗布轻摆(极廉价, 只在有旗亮时算): 一低一高两条 sin 叠加 → 上下起伏 + 左右轻晃, 像随海风自然飘动
        void TickFlagWaver()
        {
            if (_flagCloth.Count == 0) return;
            float t = Time.time;
            foreach (var kv in _flagCloth)
            {
                var cloth = kv.Value;
                if (cloth == null || !cloth.gameObject.activeSelf) continue;
                float ph = _flagPhase.TryGetValue(kv.Key, out var p) ? p : 0f;
                float up = Mathf.Sin(t * 2.3f + ph) * 4f;       // 上下起伏 ±4°
                float sway = Mathf.Sin(t * 1.4f + ph * 1.7f) * 7f;   // 左右轻晃 ±7°
                cloth.localRotation = Quaternion.Euler(FlagTilt + up, sway, 0f);
            }
        }

        // 按开关 + 到过状态点亮各港旗标(开关切换 / 进港 / 读档后调用)
        public void ApplyCityFlags()
        {
            if (world == null) return;
            foreach (var p in world.Ports)
            {
                if (!_flagRoots.TryGetValue(p.Id, out var f) || f == null) continue;
                bool show = FlagsOn && (p.Id == homePortId || _cityFlags.Contains(p.Id));
                if (f.gameObject.activeSelf != show) f.gameObject.SetActive(show);
            }
        }

        // =============================================================
        // 探索红旗: 海图上选中一处 → 插旗把船驶到那儿抛锚。一根直杆 + 正红燕尾旗(同城市旗布,
        //   并入 _flagCloth 队列一起逐帧轻摆) + 水面一圈正红光晕, 远处也一眼认得出这处旗点。
        // =============================================================
        void EnsureExploreFlag()
        {
            if (_exploreFlag != null) return;
            var root = new GameObject("explore_flag").transform;
            root.SetParent(_root, false);
            root.gameObject.SetActive(false);
            const float s = 1.2f, baseY = 0.35f;
            float H = 2.5f * s;              // 杆高(比城市小旗高些, 一眼是"这处就是目标")
            float L = 2.0f * s;              // 旗伸出长(沿 +Z)
            float W = 1.05f * s;             // 杆端满高(hoist)
            var pole = Box(StdMat(ColWoodFlag), new Vector3(0.13f, H, 0.13f));
            pole.SetParent(root, false);
            pole.localPosition = new Vector3(0f, baseY + H * 0.5f, 0f);
            var cloth = PennantMesh(ColRedFlag, L, W * 0.5f);
            cloth.SetParent(root, false);
            cloth.localPosition = new Vector3(0f, baseY + H, 0f);
            cloth.localRotation = Quaternion.Euler(FlagTilt, 0f, 0f);
            _flagCloth["explore"] = cloth;                 // 并入逐帧轻摆; 专属相位不与别旗同步
            _flagPhase["explore"] = Mathf.PI * 0.5f;

            var ring = new GameObject("ring").AddComponent<LineRenderer>();
            ring.transform.SetParent(root, false);
            var lr = ring.GetComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = 33;
            for (int i = 0; i <= 32; i++)
            {
                float a = i / 32f * Mathf.PI * 2f;
                lr.SetPosition(i, new Vector3(Mathf.Cos(a) * 1.7f, 0.42f, Mathf.Sin(a) * 1.7f));
            }
            lr.loop = true;
            lr.startColor = lr.endColor = new Color(1f, 0.30f, 0.28f);
            lr.startWidth = lr.endWidth = 0.09f;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            _exploreFlag = root;
        }
        void ShowExploreFlag(Vector3 w)
        {
            EnsureExploreFlag();
            _exploreFlag.position = w;
            _exploreFlag.gameObject.SetActive(true);
        }
        void HideExploreFlag()
        {
            if (_exploreFlag != null) _exploreFlag.gameObject.SetActive(false);
        }

        // 记一个"新到过的城市"(大本营本身除外); 首次记入返回 true
        bool MarkCityVisited(string id)
        {
            if (string.IsNullOrEmpty(id) || id == homePortId) return false;
            return _cityFlags.Add(id);
        }

        void BuildFleetVisual()
        {
            _ship = BuildShipModel(fleet.Ships.Count);
            _ship.name = "PlayerFleet";
            PlaceShipAt(Current, Time.time);
        }

        // 舰队条数变了(买船/卖船/读档)后重建船模 —— 僚舰以一串小艇跟在旗舰身后
        public void RebuildFleetVisual()
        {
            if (_ship != null)
            {
                _shipSails.Clear();
                if (_ship != null) Destroy(_ship.gameObject);
                _ship = null;
            }
            if (world == null) return;
            BuildFleetVisual();
        }

        Transform BuildShipModel(int shipCount)
        {
            var root = new GameObject("FleetShip").transform;
            float s = 1f;
            var wood = StdMat(new Color(0.42f, 0.27f, 0.16f));
            var deck = StdMat(new Color(0.56f, 0.38f, 0.20f));
            var sail = StdMat(new Color(0.99f, 0.98f, 0.93f));   // 白帆(主角船 + 僚舰小帆共用)
            var dark = StdMat(new Color(0.10f, 0.07f, 0.05f));

            // 船壳(一个拉伸的长方体打底 + 船首斜切感由一根舷木带出)
            var hull = Box(wood, new Vector3(2.6f * s, 0.5f * s, 0.85f * s));
            hull.SetParent(root, false); hull.localPosition = Vector3.zero;

            var prow = Box(dark, new Vector3(0.9f * s, 0.45f * s, 0.75f * s));
            prow.SetParent(root, false); prow.localPosition = new Vector3(1.55f * s, 0.05f * s, 0f);
            prow.localRotation = Quaternion.Euler(0, 0, -25f);

            var rail = Box(deck, new Vector3(2.4f * s, 0.18f * s, 0.8f * s));
            rail.SetParent(root, false); rail.localPosition = new Vector3(-0.05f * s, 0.38f * s, 0f);

            // 两根立起的桅杆, 各挂一面大白帆 —— 帆略斜向天空, 海图俯视也见得到白帆面(不会竖成一条线)
            AddMast(root, sail, -0.30f * s, 2.6f * s, 1.55f * s, 1.90f * s);   // 主桅(偏中后): 高、帆大
            AddMast(root, sail,  0.85f * s, 2.10f * s, 1.30f * s, 1.60f * s);  // 前桅: 略矮、帆略小

            // 旗杆小三角旗
            var flag = Box(StdMat(new Color(0.85f, 0.28f, 0.24f)), new Vector3(0.28f * s, 0.28f * s, 0.05f));
            flag.SetParent(root, false); flag.localPosition = new Vector3(1.25f * s, 1.35f * s, 0f);
            flag.localRotation = Quaternion.Euler(0f, 0f, 20f);

            // 僚舰 = 舰尾一列小艇(舰数越多越长; 每艘也有自己一面小帆)
            for (int k = 0; k < shipCount - 1; k++)
            {
                var esc = Box(wood, new Vector3(1.2f * s, 0.3f * s, 0.5f * s));
                esc.SetParent(root, false);
                float zp = (k % 2 == 0) ? 0f : 0.24f * s;
                esc.localPosition = new Vector3(-(2.2f + k * 0.8f) * s, -0.05f * s, zp);
                var escSail = Box(sail, new Vector3(0.08f, 1.0f * s, 0.8f * s));
                escSail.SetParent(esc, false); escSail.localPosition = new Vector3(0.1f * s, 0.7f * s, 0f);
            }
            root.localScale = Vector3.one * ShipVisualScale;   // 整船按比例缩小(桅/帆/僚舰一串全跟着小)
            return root;
        }

        // 一面"立起的帆": 一根竖直桅杆 + 挂在桅身上段的一片白帆(横张在船身的薄片: 宽=sailW、高=sailH)。
        //   帆的姿态(斜向天空 ~26°, 见 SetSailsRaised)让帆面朝上 —— 高空海图俯视是一面白帆。
        void AddMast(Transform root, Material sailMat, float x, float h, float sailH, float sailW)
        {
            var mast = Box(StdMat(new Color(0.30f, 0.22f, 0.12f)), new Vector3(0.08f, h, 0.08f));
            mast.SetParent(root, false); mast.localPosition = new Vector3(x, h * 0.5f, 0f);
            float centerY = Mathf.Max(0.6f, h * 0.9f - sailH * 0.5f);   // 帆中心: 顶端贴近桅顶, 底不拖到甲板下
            var sail = Box(sailMat, new Vector3(0.10f, sailH, sailW));
            sail.SetParent(root, false);
            sail.localPosition = new Vector3(x, centerY, 0f);
            _shipSails.Add(sail);
        }

        static Transform Box(Material m, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.localScale = scale;
            var c = go.GetComponent<Collider>();
            if (c) Destroy(c);
            var r = go.GetComponent<Renderer>();
            if (r && m) r.material = m;
            return go.transform;
        }

        Material StdMat(Color c)
        {
            var sh = Shader.Find("Standard");
            var m = sh != null ? new Material(sh) : new Material(Shader.Find("Sprites/Default"));
            m.color = c;
            return m;
        }

        void PlaceShipAt(Port p, float t)
        {
            if (_ship == null) return;
            var basePos = LonLatToWorld(p) + new Vector3(0f, 0.02f, 0f);
            // 泊港时轻晃 + 前进方向朝向洋流(顺手朝东)
            _ship.position = basePos + new Vector3(Mathf.Sin(t * 0.6f) * 0.12f, 0.03f, Mathf.Cos(t * 0.7f) * 0.10f);
            _ship.rotation = Quaternion.Euler(0f, -20f + Mathf.Sin(t * 0.4f) * 4f, 0f);
            // 白帆始终扬着(靠港也满帆) —— 让主角的船一眼是"立桅 + 白帆"的模样
            SetSailsRaised(true, t);
        }

        // 帆的姿态: 主角船随时扬着白帆, 不再收帆压平; 每面帆绕 z(船身左右)斜 ~SailLean, 帆面朝上,
        // 高空俯视看得到整片白帆而非一条竖线; 随时间叠极轻的"吃风鼓胀"起伏。
        void SetSailsRaised(bool raised, float t)
        {
            const float SailLean = 26f;
            foreach (var s in _shipSails)
            {
                if (s == null) continue;
                s.localRotation = Quaternion.Euler(raised ? 6f : 4f, 0f, SailLean);
                var lc = s.localScale;
                lc.z = 1f + Mathf.Sin(t * 1.6f) * 0.05f;
                s.localScale = lc;
            }
        }

        // 海上抛锚: 船就在 _anchor 旗点下随波轻晃(不挪开锚地中心), 白帆照扬
        void AnchorBob(float t)
        {
            if (_ship == null) return;
            _ship.position = _anchor + new Vector3(Mathf.Sin(t * 0.55f) * 0.10f,
                0.03f + Mathf.Sin(t * 0.8f) * 0.015f, Mathf.Cos(t * 0.45f) * 0.08f);
            _ship.rotation = Quaternion.Euler(0f, Mathf.Sin(t * 0.3f) * 4f, 0f);
            SetSailsRaised(true, t);
        }

        // =============================================================
        // 相机 & 点选
        // =============================================================
        void FrameMap()
        {
            // 世界总览(海图镜头): 以整张世界地图中心取景, 高空俯视一次看全
            _target = new Vector3((float)((SeaMapGen.Lon0 + SeaMapGen.Lon1) * 0.5), 0f,
                                  (float)-((SeaMapGen.Lat0 + SeaMapGen.Lat1) * 0.5));
            _dist = 220f;   // 常规 16:9 下正好框住全图 + 一圈海; 缩放靠滚轮
            _pitch = 82f;
        }

        // 开局/回港近景: 相机对着船和它附近的一圈港, 港球大、一点就能选目标(别一上来甩一张看不见船的远图)
        void FrameHome()
        {
            var p = Current ?? world.FindPort(homePortId);
            _target = p != null ? LonLatToWorld(p) : _target;
            _dist = 62f;
            _pitch = 82f;
        }
        public void ViewWorld() { FrameMap(); }   // HUD"世界全景"
        public void ViewHome() { FrameHome(); }   // HUD"回港/就近"

        public bool PointerOverUi() // SeaHud 设置
        {
            return UnityEngine.EventSystems.EventSystem.current != null
                && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
        }

        void HandleCamAndPick()
        {
            if (_cam == null) return;
            // 原则: 弹出的对话框/信息就是最高层 —— 有浮层(行情/舰队/确认/出航检查)开着,
            // 底下海图的输入(滚轮缩放 / 拖移 / 点选)全部暂停, 交还给最上层 UI;
            // 平时指针悬在任一 UI(按钮/面板/日志)上, 同样只让那层响应, 别让地图跟着滚轮缩放。
            bool overUi = PointerOverUi();
            if (_hud != null && _hud.AnyTopOpen)
            {
                _downHeld = false; _dragPan = false; _midHeld = false;
                ApplyCamera(State == Mode.Sailing ? _ship.position : _target);
                return;
            }

            // 滚轮缩放(指针在海图上才缩放; 指针在 UI 上 = 滚轮只滚动 UI 那层)
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (!overUi && Mathf.Abs(wheel) > 0.0001f)
                _dist = Mathf.Clamp(_dist * (1f - wheel * 0.48f), 10f, 700f);   // 每滚一格缩放倍率=原先×4

            // 中键按住 = 拖海图(按下瞬间抓住一个"地平面点", 之后地图跟手)
            if (Input.GetMouseButtonDown(2) && !overUi) { _midHeld = true; GrabAt(Input.mousePosition); }
            if (_midHeld)
            {
                if (Input.GetMouseButton(2)) PanGrab();
                else _midHeld = false;
            }

            // 左键: 按下后移动超过阈值 = 拖动平移; 松手且没拖动 = 点选目标港
            if (Input.GetMouseButtonDown(0) && !overUi)
            {
                _downHeld = true; _dragPan = false;
                _downScreen = Input.mousePosition;
                GrabAt(Input.mousePosition);   // 按住时先抓住底下的点, 拖起来才"跟手"
            }
            if (_downHeld)
            {
                if (Input.GetMouseButton(0))
                {
                    if (!_dragPan && Vector2.Distance(Input.mousePosition, _downScreen) > 7f)
                    {
                        _dragPan = true;              // 动够远才认作拖, 免得误吞点选
                        GrabAt(Input.mousePosition);  // 过闸瞬间咬住当前光标 → 起手不带"跳一下"
                    }
                    if (_dragPan) PanGrab();
                }
                else
                {
                    bool wasPan = _dragPan;
                    _downHeld = false; _dragPan = false;
                    // 停着(泊港/抛锚)点选: 港球=去那座城; 海面=插红旗自由探索
                    if (!wasPan && State != Mode.Sailing && !PointerOverUi()) PickAt(Input.mousePosition);
                }
            }

            // 相机对焦: 海上跟船
            ApplyCamera(State == Mode.Sailing ? _ship.position : _target);
        }

        // 按下瞬间记住 相机焦点 + 光标所指的"地平面点"(锚仅供记录; 平移用增量法)
        void GrabAt(Vector2 screen)
        {
            _grabFocus = _target;
            _grabGround = GroundAtScreen(screen);
            _panPrev = screen;
        }

        // 1:1 跟手(增量式, 防抖):
        //   每帧都用【同一帧的相机姿态】分别采样"上一光标位置"与"当前光标位置"的地面点,
        //   两者相减 = 光标这一小段移动对应的地面位移 → 把地图平移同样但反向的量。
        //   不做"绝对锚点回算"(旧锚会随相机姿态每帧变化形成 ± 反馈环 → 静止时地图来回振 = 抖动)。
        void PanGrab()
        {
            Vector2 now = Input.mousePosition;
            Vector3 a = GroundAtScreen(_panPrev);
            Vector3 b = GroundAtScreen(now);
            _target += (a - b);
            _panPrev = now;
        }

        // 屏幕像素 → 海面地平面(y=0)上的世界点(拖拽抓点用; 港球/船浮在空中不影响平面求交)
        Vector3 GroundAtScreen(Vector2 screen)
        {
            if (_cam == null) return _target;
            var ray = _cam.ScreenPointToRay(screen);
            float d;
            var plane = new Plane(Vector3.up, 0f);
            if (plane.Raycast(ray, out d) && d > 0f) return ray.GetPoint(d);
            return _target;   // 极边缘没打到地面时保持不动
        }

        // 点选(停着时): 点中港口光球 = 设那座城为目标; 没点中港球且点在海上 = 插红旗自由探索
        void PickAt(Vector2 screen)
        {
            if (State != Mode.Docked && State != Mode.Anchored) return;
            var ray = _cam.ScreenPointToRay(screen);
            if (Physics.Raycast(ray, out var hit, 4000f) && hit.collider != null)
            {
                var p = world.FindPort(hit.collider.gameObject.name);
                if (p != null)
                {
                    // 泊港时点"自己正停着的那颗"没意义; 抛锚在外时当前港也算远处一座城, 可以回航
                    if (State == Mode.Docked && p == Current) { Banner = "已在 " + p.Name + " 港"; return; }
                    SetPortTarget(p);
                    return;
                }
            }
            TryPlantSeaFlag(screen);
        }

        void SetPortTarget(Port p)
        {
            Dest = p;
            if (ExploreSet) { ExploreSet = false; HideExploreFlag(); }
            // 黑雾不锁航线: 就算港还埋在雾里也能凭海图朝它驶去(路经迷雾沿途会被一步步揭开)
            PushLog(CityRevealed(p)
                ? "航线目标: " + p.Name + "(" + p.Area.Name + ")"
                : "朝 " + p.Name + " 进发 —— 那片海雾还没散, 一路驶过去把航道探开。");
        }

        // 光标落点须在海上: 把红旗插到那儿, 并设为下一个出航目标(可反复点海面挪旗)
        void TryPlantSeaFlag(Vector2 screen)
        {
            var g = GroundAtScreen(screen);
            double lon = Mathf.Clamp(g.x, (float)SeaMapGen.Lon0, (float)SeaMapGen.Lon1);
            double lat = Mathf.Clamp(-g.z, (float)SeaMapGen.Lat0, (float)SeaMapGen.Lat1);
            if (SeaMapGen.LandAt(lon, lat)) { Banner = "那是陆地 —— 点一处海面才能插旗驶去。"; return; }
            Dest = null;
            ExploreWorld = new Vector3((float)lon, 0f, (float)-lat);
            ExploreSet = true;
            ShowExploreFlag(ExploreWorld);
            PushLog("🚩 海面插旗: 船将驶向这处(不是港口, 到点抛锚)。点别处海面可挪旗; 点发光圆球则改去那座城。");
        }

        // 海图镜头: 相机摆在焦点正南高空(_dist 远处, _pitch 高俯角), 视线朝北看 → 世界"北在上、东在右",
        // 像一张摊开的活地图; 焦点永远贴地(跟船=船位, 泊港/总览=地图点)。无自由绕视。
        void ApplyCamera(Vector3 focus)
        {
            if (_cam == null) return;
            focus.y = 0f;
            float ph = _pitch * Mathf.Deg2Rad;
            var off = new Vector3(0f, _dist * Mathf.Sin(ph), _dist * Mathf.Cos(ph));
            _cam.transform.position = focus + off;
            _cam.transform.LookAt(focus, Vector3.up);
        }

        // =============================================================
        // 航海
        // =============================================================
        public void SetDestination(Port p)
        {
            if (State != Mode.Docked && State != Mode.Anchored) return;
            Dest = p;
            if (ExploreSet) { ExploreSet = false; HideExploreFlag(); }
            PushLog("航线目标: " + p.Name + " · " + p.Area.Name);
        }

        // 出航起点: 泊港 = 精确港位; 海上抛锚 = 船在旗点下停稳处 —— 从哪儿起锚就从哪儿算海路
        Vector3 VoyOrigin()
        {
            if (State == Mode.Docked && Current != null) return LonLatToWorld(Current);
            return _ship != null ? new Vector3(_ship.position.x, 0f, _ship.position.z) : Vector3.zero;
        }

        static string RouteCacheKey(Vector3 a, Vector3 b)
        {
            int ax = Mathf.RoundToInt(a.x * 10f), az = Mathf.RoundToInt(-a.z * 10f);
            int bx = Mathf.RoundToInt(b.x * 10f), bz = Mathf.RoundToInt(-b.z * 10f);
            return ax + "_" + az + ">" + bx + "_" + bz;
        }

        // 起终点航程日数(走绕陆海路; 吸不到海格/找不到路才退直线)
        int DaysFor(Vector3 from, Vector3 to)
        {
            var rt = SeaRoute.Route(from.x, -from.z, to.x, -to.z, RouteCacheKey(from, to));
            float deg = rt != null ? rt.Total : Vector3.Distance(from, to);
            float speed = Mathf.Max(1f, fleet != null ? FleetTopSpeed() : 8f);
            return Mathf.Max(1, Mathf.CeilToInt(deg * 3f / speed));
        }

        // 当前已选目标(城或旗点)到出航点的航程日数 —— HUD 引导 / 出航前检查共用
        public int TargetDays
        {
            get
            {
                if (!HasSailTarget || _ship == null) return 0;
                Vector3 to = Dest != null ? LonLatToWorld(Dest) : ExploreWorld;
                return DaysFor(VoyOrigin(), to);
            }
        }

        public void Depart()
        {
            bool fromPort = State == Mode.Docked;                 // 抛锚出航也算合法(海图点续航)
            if ((!fromPort && State != Mode.Anchored) || !HasSailTarget) return;
            if (fleet.TotalCrew() <= 0) { Banner = "没有水手, 无法出航"; return; }

            bool toPort = Dest != null;
            DepartWorld = VoyOrigin();
            if (toPort) { var dw = LonLatToWorld(Dest); DestWorld = new Vector3(dw.x, 0f, dw.z); }
            else DestWorld = ExploreWorld;                        // 探索目标: 海图插旗点(到点抛锚)
            _route = SeaRoute.Route(DepartWorld.x, -DepartWorld.z, DestWorld.x, -DestWorld.z,
                                    RouteCacheKey(DepartWorld, DestWorld));   // 沿海/绕陆海路(船不走陆地)
            PlannedDays = DaysFor(DepartWorld, DestWorld);
            _departAreaId = Current != null ? Current.Area.Id : _departAreaId;
            _voyageT = 0f;
            DaysDone = 0;
            SailProgress01 = 0f;
            State = Mode.Sailing;
            // 起锚瞬间把船摆正: 船艏(局部 +X)对准第一段航向, 平滑转向由此起步
            Vector3 d0 = (_route != null && _route.Pts.Count >= 2)
                ? _route.Pts[1] - _route.Pts[0]
                : (DestWorld - DepartWorld);
            d0.y = 0f;
            if (d0.sqrMagnitude > 1e-6f && _ship != null)
            {
                _ship.rotation = Quaternion.LookRotation(d0.normalized, Vector3.up) * Quaternion.Euler(0f, -90f, 0f);
                _ship.position = new Vector3(_ship.position.x, 0.05f, _ship.position.z);
            }
            _target = DepartWorld + (DestWorld - DepartWorld) * 0.5f; // 相机跟船, 从出发处看
            float span = Vector3.Distance(DepartWorld, DestWorld);
            _dist = Mathf.Clamp(Mathf.Max(span, _route != null ? _route.Total * 0.45f : 0f) * 0.9f, 16f, 210f);
            string fromName = fromPort && Current != null ? Current.Name : "海上锚地";
            string toName = toPort ? Dest.Name : "海图 🚩 目标点";
            PushLog("出航! " + fromName + " → " + toName + ", 预计 " + PlannedDays + " 日(沿海航线"
                    + (toPort ? "" : " · 到点抛锚") + ")");
            if (FleetOps.EnduranceDays(fleet) <= PlannedDays)
                Banner = "⚠ 口粮可能不够撑 " + PlannedDays + " 日, 请掂量!";
        }

        public int TravelDays(Port a, Port b)
        {
            if (a == null || b == null) return 0;
            return DaysFor(LonLatToWorld(a), LonLatToWorld(b));
        }

        // 沿海路折线上的参数点(u=0..1 按航程弧长分布); 无海路时退化为直线
        Vector3 RoutePointAt(float u)
        {
            if (_route == null || _route.Pts.Count == 0)
                return Vector3.Lerp(DepartWorld, DestWorld, u);
            if (_route.Pts.Count == 1) return _route.Pts[0];
            float target = Mathf.Clamp01(u) * _route.Total;
            float acc = 0f;
            for (int i = 1; i < _route.Pts.Count; i++)
            {
                float seg = Vector3.Distance(_route.Pts[i - 1], _route.Pts[i]);
                if (seg <= 0.0001f) continue;
                if (acc + seg >= target)
                {
                    float t = Mathf.Max(0f, (target - acc) / seg);
                    return Vector3.Lerp(_route.Pts[i - 1], _route.Pts[i], t);
                }
                acc += seg;
            }
            return _route.Pts[_route.Pts.Count - 1];
        }

        public float FleetTopSpeed()
        {
            float hi = 0f;
            foreach (var s in fleet.Ships) hi = Mathf.Max(hi, s.EffSpeed);
            return hi;
        }

        void TickSailing()
        {
            if (!HasSailTarget) { State = Current != null ? Mode.Docked : Mode.Anchored; return; }
            float totalT = Mathf.Clamp(PlannedDays * 0.24f, 3f, 13f);
            _voyageT += Time.deltaTime;
            float p = Mathf.Clamp01(_voyageT / totalT);
            int wantDone = Mathf.FloorToInt(p * PlannedDays);
            while (DaysDone < wantDone) { StepSeaDay(); DaysDone++; }

            SailProgress01 = p;
            // 船沿海路折线前移(绝不直线穿陆地); 浮沉很轻, 不再大起大落
            Vector3 wp = RoutePointAt(p);
            float bob = Mathf.Sin(_voyageT * 2.2f) * 0.018f;
            _ship.position = new Vector3(wp.x, 0.05f + bob, wp.z);

            // 船头始终对准"再往前一小段"的走向(把船艏 x 轴转到行进方向), 转弯按限速渐变,
            // 而不是每帧死贴瞬时切线 —— 过折点不甩头、不来回摆
            if (p < 1f)
            {
                float look = 0.005f;
                if (_route != null && _route.Total > 0.001f)
                    look = Mathf.Clamp(3.5f / _route.Total, 0.002f, 0.05f);
                Vector3 ahead = RoutePointAt(Mathf.Min(1f, p + look));
                Vector3 dir = ahead - wp; dir.y = 0f;
                if (dir.sqrMagnitude > 1e-6f)
                {
                    Quaternion want = Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(0f, -90f, 0f);
                    _ship.rotation = Quaternion.RotateTowards(_ship.rotation, want, ShipTurnRate * Time.deltaTime);
                }
            }
            SetSailsRaised(true, _voyageT);

            if (p >= 1f && DaysDone >= PlannedDays) Arrive();
        }

        void StepSeaDay()
        {
            engine.AdvanceDays(1);
            SnapMarketDay();   // 航行途中也逐日留档, 分析报的曲线不断
            FleetOps.AdvanceOneSeaDay(fleet);

            // 每日遭遇掷点(危险区按出航港所在航区估)
            float danger = cat.Tuning.DangerOf(_departAreaId);
            float p = 0.02f + danger * 0.10f;
            if (fleet.TotalCrew() > 0 && _pirateRng.NextDouble() < p)
                TriggerPirate();
        }

        void TriggerPirate()
        {
            var areaDanger = cat.Tuning.DangerOf(_departAreaId);
            float our = FleetFormulas.FleetPower(fleet);
            float ratio = 0.82f + (float)_pirateRng.NextDouble() * 0.55f;   // 敌人 0.82..1.37 倍
            float pirPower = Mathf.Max(1f, our * ratio);
            float ourSpd = FleetTopSpeed();
            float pirSpeed = ourSpd * (0.7f + (float)_pirateRng.NextDouble() * 0.6f);
            int seedX = 1000 + engine.Day * 13 + DaysDone;
            var o = FleetOps.AutoResolve(fleet, _departAreaId, (int)pirPower, pirSpeed, seedX);
            LastBattleAt = Dest;

            if (o.Escaped) PushLog("遭遇海盗…… 舵手全速脱身, 险险避开。");
            else if (o.Victory) { PushLog("海盗来袭! 一阵炮战, 击退贼船, 士气大涨。"); fleet.Morale = Mathf.Min(cat.Tuning.MoraleMax, fleet.Morale + 10); }
            else
            {
                string tail = o.RansomPaid > 0 ? $", 破财 {Money(o.RansomPaid)}" : "";
                PushLog("海盗劫船! 丢了 {0} 件货, 士气大跌{1}。".Replace("{0}", o.CargoLostUnits.ToString()).Replace("{1}", tail));
            }
        }

        void Arrive()
        {
            var arrive = Dest;                 // 空 = 这次目标是海图旗点(探索), 不是港
            DaysDone = PlannedDays;
            SailProgress01 = 1f;
            if (arrive != null)                // —— 到港: 照旧进港结算 / 治病 / 记到过 ——
            {
                State = Mode.Docked;
                Dest = null;
                Current = arrive;
                if (ExploreSet) { ExploreSet = false; HideExploreFlag(); }
                _target = LonLatToWorld(arrive);
                _dist = Mathf.Min(_dist, 60f);   // 到港落稳, 推近到港区
                PlaceShipAt(arrive, Time.time);
                long wage = FleetOps.SettleAtPort(fleet, arrive.Id);
                PushLog("抵达 " + arrive.Name + "。进港结算工资 " + Money(wage) + ", 士气恢复, 伤号已治。");
                // 红旗照记(不因开关关着就漏记; 开关随时可打开补显); 只有开着时才广播日志
                if (MarkCityVisited(arrive.Id) && FlagsOn)
                    PushLog("🚩 头回踏足 " + arrive.Name + " —— 已给它插上一面小红旗。");
                ApplyCityFlags();
                Banner = "已抵达 " + arrive.Name;
            }
            else                               // —— 到海上旗点: 抛锚停船(不靠港、不结薪) ——
            {
                State = Mode.Anchored;
                Dest = null;
                _anchor = ExploreWorld;        // 就停在这面旗下
                ShowExploreFlag(_anchor);      // 红旗就地当"锚地标" —— 告诉玩家船此刻在这儿
                ExploreSet = false;            // 已抵达 → 不再算"待出航目标", 另点新目标才再出航
                _target = _anchor;
                _dist = Mathf.Min(_dist, 60f);
                PushLog("抵达 🚩 海图目标点 —— 船已抛锚停稳(海上不结薪)。点一座城驶去做买卖, 或再点海面插新旗继续自由探索。");
                Banner = "已到目标海面 · 抛锚停泊";
            }
        }

        // =============================================================
        // 泊港操作(交易 / 补给 / 休整 / 招募)
        // =============================================================
        // ---------------- 本局试炼目标 ----------------
        public long StartWorth { get; private set; }
        public const long ProfitTarget = 150000;   // 目标: 净赚 15 万
        // 身家 = 现金 + 货舱现有货按当前港卖价折算(船上补给与船价不算, 简单直观)
        public long NetWorth()
        {
            if (fleet == null) return 0;
            long v = fleet.Gold;
            if (Current != null && world != null)
                foreach (var s in fleet.Ships)
                    foreach (var kv in s.Cargo)
                        if (world.GoodById.TryGetValue(kv.Key, out var g))
                            v += (long)engine.BidPrice(Current, g) * kv.Value;
            return v;
        }
        public long ProfitSoFar => Math.Max(0, NetWorth() - StartWorth);
        public bool GoalMet => ProfitSoFar >= ProfitTarget;

        public long Cash => fleet.Gold;
        public int TotalSailors => fleet.TotalCrew();
        public float Morale => fleet.Morale;
        public int SickCount => fleet.Sick;
        public int EnduranceDays => FleetOps.EnduranceDays(fleet);

        public int TotalCapacity()
        {
            int c = 0;
            foreach (var s in fleet.Ships) c += s.EffCapacity;
            return c;
        }
        public int CargoWeight()
        {
            int w = 0;
            foreach (var s in fleet.Ships) w += s.OccupiedWeight(fleet.WeightOf);
            return w;
        }
        public int ProvWeight()
        {
            int w = 0;
            var t = cat.Tuning;
            foreach (var k in new[] { "food", "water", "tea" })
                w += (int)Mathf.Ceil(fleet.Prov.Get(k) * t.Provision[k].UnitWeight);
            return w;
        }
        public int FreeLoad() => TotalCapacity() - CargoWeight() - ProvWeight();
        public int GoodInHold(string goodId)
        {
            int n = 0;
            foreach (var s in fleet.Ships) { int v; if (s.Cargo.TryGetValue(goodId, out v)) n += v; }
            return n;
        }

        // 某货当前"持仓平均成本/件"(行情表成本列)。卖出/被海盗劫走货后, 按实有余量摊薄总账(件均成本不变)。
        // 无货、或账本缺该货(旧档遗留) → -1(UI 显示 "—")。
        public int AvgHoldCost(string goodId)
        {
            long qty = _basisQty.TryGetValue(goodId, out var bq) ? bq : 0L;
            long cost = _basisCost.TryGetValue(goodId, out var bc) ? bc : 0L;
            if (qty <= 0L) return -1;
            int actual = GoodInHold(goodId);
            if (actual <= 0)
            {
                _basisQty.Remove(goodId); _basisCost.Remove(goodId);
                return -1;
            }
            if (qty != actual)                       // 余量变了(被卖/被劫) → 总成本按余量缩放, 均价不变
            {
                if (qty > actual) cost = cost * actual / qty;
                qty = actual;
                _basisQty[goodId] = qty; _basisCost[goodId] = cost;
            }
            if (cost <= 0L) return 0;
            return (int)((cost + qty / 2L) / qty);   // 四舍五入到整数金
        }
        public int BuyStock(Port p, Good g) => engine.BuyStock(p, g);

        public void BuyGood(Good g, int want)
        {
            if (Current == null || State != Mode.Docked) return;
            int ask = engine.AskPrice(Current, g);
            if (ask <= 0 || want <= 0) return;
            int stock = engine.BuyStock(Current, g);
            if (stock <= 0) { Banner = g.Name + ": 港口无货可售"; return; }
            // 能装多少与 FleetOps.AddCargo 逐船口径一致(货种≤slots, 载重≤capacity), 避免"买得进装不下"
            int can = CanPlace(g.Id, Mathf.Max(1, g.Weight));
            int afford = (int)Mathf.Min(int.MaxValue - 1, Cash) / ask;
            int qty = Mathf.Min(Mathf.Min(want, can), Mathf.Min(stock, afford));
            if (qty <= 0) { Banner = "装不下或买不起: " + g.Name; return; }
            var res = engine.Buy(Current, g, qty, (int)Mathf.Min(int.MaxValue - 1, Cash));
            if (res.Units <= 0) { Banner = "交易被拒(封锁或缺货)"; return; }
            int placed = FleetOps.AddCargo(fleet, g.Id, res.Units); // 恒等于 res.Units
            fleet.Gold -= res.Gold;
            // 持仓成本记账: 这次又买 placed 件、共花 res.Gold → 累进均价账本(卖/被劫后由 AvgHoldCost 摊薄)
            long bq = _basisQty.TryGetValue(g.Id, out var obq) ? obq : 0L;
            long bc = _basisCost.TryGetValue(g.Id, out var obc) ? obc : 0L;
            _basisQty[g.Id] = bq + placed;
            _basisCost[g.Id] = bc + res.Gold;
            int unit = res.Gold / Mathf.Max(1, placed);   // 实付单价(便于日志回看成交价)
            PushLog($"买入 {g.Name} ×{placed}(单价 {unit} 金), 共花 {Money(res.Gold)}");
            if (audio != null) audio.SfxTrade();
        }

        int CanPlace(string goodId, int weight)
        {
            int can = 0;
            foreach (var s in fleet.Ships)
            {
                if (!s.Cargo.ContainsKey(goodId) && s.Cargo.Count >= s.Model.Slots) continue;
                int remain = s.EffCapacity - s.OccupiedWeight(fleet.WeightOf);
                int fit = weight > 0 ? remain / weight : 0;
                if (fit > 0) can += fit;
            }
            return can;
        }

        public void SellGood(Good g, int want)
        {
            if (Current == null || State != Mode.Docked) return;
            int have = GoodInHold(g.Id);
            int qty = Mathf.Min(want, have);
            if (qty <= 0) return;
            var res = engine.Sell(Current, g, qty);
            fleet.Gold += res.Gold;
            fleet.RemoveUnit(g.Id, qty);
            fleet.Career.NoteSold(Current.Id, qty);
            PushLog($"卖出 {g.Name} ×{qty}, 得 {Money(res.Gold)}");
            if (audio != null) audio.SfxTrade();
        }

        // ---- 花金操作的"预览", 供确认界面显示费用(不产生副作用) ----
        public long RecruitCostToFull()
        {
            if (State != Mode.Docked) return -1L;
            long cost = 0;
            foreach (var s in fleet.Ships)
            {
                int need = s.Model.CrewMax - s.CrewAboard;
                if (need > 0) cost += (long)(need * cat.Tuning.RecruitCostBase);
            }
            return cost;
        }
        // 补给行: 一种给养在本次"补满 N 日"里要买的人·日份数 / 占载 / 花费
        public sealed class ProvLine
        {
            public string Kind;     // food / water / tea
            public string Cn;       // 中文名
            public float Add;       // 本次要补的"人·日份"数(1份=1水手吃1日该给养)
            public float Weight;    // 本次占载 = Add × 份重
            public long Cost;       // 金
        }

        static string ProvCn(string k)
        {
            switch (k) { case "food": return "食物"; case "water": return "水"; case "tea": return "茶叶"; default: return k; }
        }

        public long ProvisionPlanCost(int nDays)   // 预计要把给养补到 N 日要花多少(0 = 无需补)
        {
            long s = 0; foreach (var p in ProvisionLines(nDays)) s += p.Cost; return s;
        }
        public bool ProvisionHasRoom(int nDays) => ProvisionLines(nDays).Count > 0;

        // 把"补满 N 日给养"实际会买的每样量算出来: 以当前水手数为准, 受现金/舱位约束尽力补。
        // 预览与执行共用此函数 → 确认界面所示费用/占载恒等于实扣。
        // 公式: 每样需求 = N日 × 全队水手 × 日耗(食物另乘省粮buff); 占载 = 份数 × 份重。
        public List<ProvLine> ProvisionLines(int nDays)
        {
            var plan = new List<ProvLine>();
            if (State != Mode.Docked || nDays <= 0) return plan;
            int crew = fleet.TotalCrew();
            if (crew <= 0) return plan;
            long cash = fleet.Gold;
            float freeW = FreeLoad();
            foreach (var kind in new[] { "food", "water", "tea" })
            {
                var spec = cat.Tuning.Provision[kind];
                float rate = spec.PerCrewPerDay;
                if (kind == "food")
                    rate *= (fleet.Bonus != null && fleet.Bonus.FoodUseMul > 0f) ? fleet.Bonus.FoodUseMul : 1f;
                float have = fleet.Prov.Get(kind);
                float need = nDays * crew * rate;
                float add = need - have;
                if (add <= 0.01f) continue;
                float unitW = spec.UnitWeight > 0f ? spec.UnitWeight : 1f;
                // 现金/舱位封顶
                if (add * unitW > freeW) add = Mathf.Max(0f, freeW / unitW);
                if (add * spec.Price > cash) add = Mathf.Max(0f, (float)cash / spec.Price);
                if (add <= 0.01f) continue;
                long cost = Mathf.CeilToInt(Mathf.Max(1f, add * spec.Price));
                if (cost > cash) continue;
                freeW = Mathf.Max(0f, freeW - add * unitW);
                cash -= cost;
                plan.Add(new ProvLine { Kind = kind, Cn = ProvCn(kind), Add = add, Weight = add * unitW, Cost = cost });
            }
            return plan;
        }

        // 按目标"满 N 日给养"补三种给养(以预览计划为准, 保证确认界面所示费用 = 实扣)
        public bool ProvisionTo(int nDays)
        {
            var plan = ProvisionLines(nDays);
            if (plan.Count == 0) return false;
            foreach (var p in plan)
            {
                fleet.Prov.Set(p.Kind, fleet.Prov.Get(p.Kind) + p.Add);
                fleet.Gold -= p.Cost;
                PushLog($"补给 {p.Cn} {p.Add:0.#} 份(人·日), 花 {Money(p.Cost)}");
            }
            return true;
        }

        public void RecruitCrewFull()
        {
            if (State != Mode.Docked) return;
            long cost = 0; int missing = 0;
            foreach (var s in fleet.Ships) { int need = s.Model.CrewMax - s.CrewAboard; if (need > 0) { missing += need; cost += (long)(need * cat.Tuning.RecruitCostBase); } }
            if (missing <= 0) { Banner = "水手满编"; return; }
            if (cost > fleet.Gold) { Banner = "现金不足补员(" + Money(cost) + ")"; return; }
            fleet.Gold -= cost;
            foreach (var s in fleet.Ships) s.CrewAboard = s.Model.CrewMax;
            PushLog($"在港口码头招募水手 ×{missing}, 花 {Money(cost)}");
        }

        public void RestDays(int n)
        {
            if (State != Mode.Docked) return;
            for (int i = 0; i < n; i++)
            {
                engine.AdvanceDays(1);
                SnapMarketDay();   // 休整亦逐日留档: 买了月报后躺着也能攒曲线
            }
            PushLog($"在 {Current.Name} 休整 {n} 日, 行情流转。");
        }

        // =============================================================
        // 情报站(玩家改版): 《贸易日报》(每日一刊·报纸, 正文快照进"我的订阅")
        //   与《全球商情报》(月订 · 按地区"一张表"观行情, 含近期走势)。
        //   买下的刊物都留进 IntelArchive 随存读档 → 情报站里随时翻旧刊。
        //   报价取"发刊那一刻", 有实效 —— 纸面仅供参考, 明日请早。
        // =============================================================
        public const int IntelPaperCost = 100;     // 《贸易日报》· 每期
        public const int IntelReportCost = 200;    // 《全球商情报》· 每月
        public const string IntelPaperName = "贸易日报";     // 旧名《全球商报》
        public const string IntelReportName = "全球商情报";  // 旧名《全球分析报》
        public const string IntelBlockSep = "\x02";          // 报纸正文"块"分隔(存储/分页共用)


        readonly List<int[]> _histBid = new List<int[]>();   // 与 _histAsk 同形(求购价序列)

        public bool IntelPaperOwnedToday => IntelFind("paper", engine.Day) != null;
        public bool IntelReportMonthOwned => IntelFind("report", IntelMonthKey()) != null;

        int IntelMonthKey() => engine.Year * 12 + engine.Month;
        public int HistCount => _histAsk.Count;
        public int HistNewestDay => _histDay.Count > 0 ? _histDay[_histDay.Count - 1] : engine.Day;
        public int HistDayAt(int k) => (k >= 0 && k < _histDay.Count) ? _histDay[k] : -1;

        public int PortIndex(Port p) => p != null ? world.Ports.IndexOf(p) : -1;
        public int GoodIndex(string id) => world != null ? world.GoodIndex(id) : -1;

        // 序列访问: 下标 k = 按留存先后排(0 = 最早那档, 末档 = 最新); 停市/缺档返回 -1
        public int HistAskValue(int k, int pi, int gi)
        {
            int width = world != null ? world.Goods.Count : 0;
            if (world == null || k < 0 || k >= _histAsk.Count || pi < 0 || gi < 0 || gi >= width) return -1;
            var row = _histAsk[k];
            int idx = pi * width + gi;
            return row != null && idx >= 0 && idx < row.Length ? row[idx] : -1;
        }
        public int HistBidValue(int k, int pi, int gi)
        {
            int width = world != null ? world.Goods.Count : 0;
            if (world == null || k < 0 || k >= _histBid.Count || pi < 0 || gi < 0 || gi >= width) return -1;
            var row = _histBid[k];
            int idx = pi * width + gi;
            return row != null && idx >= 0 && idx < row.Length ? row[idx] : -1;
        }

        // 求购深度估算: 当前求购价(R)之下、把挂牌比打到 ~1.05 供需线前, 城市约还能吃下几件
        //   (卖货一次把 R 压 SellRatioPushPerUnit; 商船来卖得越多, 越早磨平价差 —— 这就是"求购数量")
        public int IntelDepth(Port p, Good g)
        {
            if (p == null || g == null) return 0;
            float r = engine.GetCell(p, g).R;
            float push = Mathf.Abs(engine.T.SellRatioPushPerUnit);
            if (push < 0.0001f) return 0;
            int d = (int)Mathf.Floor((r - 1.05f) / push);
            return d < 0 ? 0 : (d > 9999 ? 9999 : d);
        }

        // 逐游戏日行情留档(新档首日 / 每日航行 / 休整 / 读档后都补一档)
        void SnapMarketDay()
        {
            if (world == null || engine == null || world.Goods.Count == 0) return;
            if (_histDay.Count > 0 && _histDay[_histDay.Count - 1] == engine.Day) return;   // 同日不重记
            var ports = world.Ports; var goods = world.Goods;
            int P = ports.Count, G = goods.Count;
            var ask = new int[P * G];
            var bid = new int[P * G];
            for (int pi = 0; pi < P; pi++)
            {
                var p = ports[pi];
                int row = pi * G;
                bool off = p.Blocked;   // 封锁 = 停市(无报价)
                for (int gi = 0; gi < G; gi++)
                {
                    if (off) { ask[row + gi] = -1; bid[row + gi] = -1; continue; }
                    var g = goods[gi];
                    ask[row + gi] = engine.AskPrice(p, g);
                    bid[row + gi] = engine.BidPrice(p, g);
                }
            }
            _histAsk.Add(ask); _histBid.Add(bid); _histDay.Add(engine.Day);
            while (_histAsk.Count > IntelHistCap)   // 只留最近 60 档(≈两个月)
            {
                _histAsk.RemoveAt(0); _histBid.RemoveAt(0); _histDay.RemoveAt(0);
            }
        }

        // ---- 购买情报(泊港 + 现金 + 时效未过才卖; 每份当日/当月只卖一次) ----
        // 成功即生成刊物并进"我的订阅"档案: 报纸含当日正文快照, 商情报留订阅记录、详情按最新行情重绘。
        public SeaIntelSave IntelFind(string kind, int key)
        {
            for (int i = 0; i < IntelArchive.Count; i++)
                if (IntelArchive[i] != null && IntelArchive[i].kind == kind && IntelArchive[i].key == key)
                    return IntelArchive[i];
            return null;
        }
        public SeaIntelSave IntelPaperTodayIssue() => IntelFind("paper", engine.Day);
        public SeaIntelSave IntelReportMonthIssue() => IntelFind("report", IntelMonthKey());
        public int IntelArchiveCount => IntelArchive.Count;

        // 我的订阅封面列表: 新 → 旧
        public List<SeaIntelSave> IntelArchiveNewestFirst()
        {
            var r = new List<SeaIntelSave>();
            for (int i = IntelArchive.Count - 1; i >= 0; i--)
                if (IntelArchive[i] != null) r.Add(IntelArchive[i]);
            return r;
        }

        public SeaIntelSave IntelBuyPaper()
        {
            if (State != Mode.Docked) { Banner = "靠港停泊后才能买情报。"; return null; }
            if (IntelPaperOwnedToday) { Banner = "今天的《" + IntelPaperName + "》已经买过 —— 新的一期明早出版。"; return null; }
            if (fleet.Gold < IntelPaperCost) { Banner = "现金不足 " + Money(IntelPaperCost) + " 金, 买不起今天的" + IntelPaperName + "。"; return null; }
            fleet.Gold -= IntelPaperCost;
            var e = new SeaIntelSave
            {
                kind = "paper",
                key = engine.Day,
                title = IntelDateText(engine.Day),
                sub = "刊价 " + IntelPaperCost + " 金 · 每日一刊 · 情报站监制",
                paid = IntelPaperCost,
                body = string.Join(IntelBlockSep, IntelPaperBlocks()),
            };
            IntelArchive.Add(e);
            PushLog("购得《" + IntelPaperName + "》: " + e.title + " (" + Money(IntelPaperCost) + " 金), 收进我的订阅。");
            Banner = "📰 今日《" + IntelPaperName + "》到手, 已收进《我的订阅》。";
            return e;
        }
        public SeaIntelSave IntelBuyReport()
        {
            if (State != Mode.Docked) { Banner = "靠港停泊后才能订阅情报。"; return null; }
            if (IntelReportMonthOwned) { Banner = "本月的《" + IntelReportName + "》已经订过。"; return null; }
            if (fleet.Gold < IntelReportCost) { Banner = "现金不足 " + Money(IntelReportCost) + " 金, 订不起本月" + IntelReportName + "。"; return null; }
            fleet.Gold -= IntelReportCost;
            var e = new SeaIntelSave
            {
                kind = "report",
                key = IntelMonthKey(),
                title = IntelMonthLabel(IntelMonthKey()),
                sub = "订阅价 " + IntelReportCost + " 金 · 当月行情一表观 · 含近期走势",
                paid = IntelReportCost,
                body = "",
            };
            IntelArchive.Add(e);
            PushLog("订阅《" + IntelReportName + "》: " + e.title + " (" + Money(IntelReportCost) + " 金), 收进我的订阅。");
            Banner = "📈 本月《" + IntelReportName + "》已订。";
            return e;
        }
        public string IntelMonthLabel(int mk)
        {
            int y = mk / 12, m = mk % 12;
            if (m == 0) { y--; m = 12; }
            return y + " 年 " + m + " 月";
        }
        public static string IntelDateText(int d)
        {
            if (d < 0) return "……更早……";
            int y = SimEngine.StartYear + d / SimEngine.DaysPerYear;
            int m = (d % SimEngine.DaysPerYear) / 30 + 1;
            int dd = d % 30 + 1;
            return y + " 年 " + m + " 月 " + dd + " 日";
        }


        // 报纸"块"生成: 每块是一个可单独排上版面的小单元(卷首语 / 晨报速递 / 每港一段),
        // 分港行市 = 各港一块平铺, 所属大陆只作城名后的小标签(不再按大陆分段)。
        // 返回正文含 <size> 富文本(发刊那一刻的行情)。SeaHud 分页时按块排成报纸版面。
        List<string> IntelPaperBlocks()
        {
            var blocks = new List<string>();
            var en = engine; var goods = world.Goods; int G = goods.Count;
            var sb = new System.Text.StringBuilder(512);

            void Line(string s) { sb.Append(s).Append('\n'); }
            void Flush()
            {
                if (sb.Length > 0) { blocks.Add(sb.ToString()); sb.Length = 0; }
            }

            Line("<size=11><color=#7A5F33>行情随每笔买卖与时日流动 —— 本刊只反映发刊这一瞬。别家船东多半也买了同一份, 等你赶到, 便宜处未必还便宜。仅供参考, 出手从速。</color></size>");
            Flush();

            // 一) 逐货最贱产地(抄底坐标)
            var bestAsk = new int[G]; var bestCity = new string[G];
            for (int i = 0; i < G; i++) { bestAsk[i] = int.MaxValue; bestCity[i] = "—"; }
            foreach (var p in world.Ports)
            {
                if (p.Blocked) continue;
                for (int gi = 0; gi < G; gi++)
                {
                    var c = en.GetCell(p, goods[gi]);
                    if (!c.Produced) continue;
                    int a = en.AskPrice(p, goods[gi]);
                    if (a >= 0 && a < bestAsk[gi]) { bestAsk[gi] = a; bestCity[gi] = p.Name; }
                }
            }

            // 二) 候选: 可捡便宜的特产 / 被抢购的缺货
            var prods = new List<IntelCand>();
            var needs = new List<IntelCand>();
            foreach (var p in world.Ports)
            {
                if (p.Blocked) continue;
                for (int gi = 0; gi < G; gi++)
                {
                    var g = goods[gi];
                    var c = en.GetCell(p, g);
                    int a = en.AskPrice(p, g), b = en.BidPrice(p, g);
                    if (c.Produced && a > 0)
                        prods.Add(new IntelCand { City = p.Name, Good = g.Name, Ask = a, Stock = en.BuyStock(p, g) });
                    if (c.Needed && b > 0 && bestAsk[gi] != int.MaxValue)
                    {
                        int m = b - bestAsk[gi];
                        needs.Add(new IntelCand { City = p.Name, Good = g.Name, Bid = b, Depth = IntelDepth(p, g), Margin = m, Ask = bestAsk[gi] });
                    }
                }
            }
            prods.Sort((x, y) => x.Ask.CompareTo(y.Ask));
            needs.Sort((x, y) => x.Margin != y.Margin ? y.Margin.CompareTo(x.Margin) : y.Bid.CompareTo(x.Bid));

            // 晨报速递 · 一版
            sb.Length = 0;
            Line("<size=13><color=#1A4553><b>◆ 晨报速递 · 捡便宜(全市最低供货价 → 去哪进货)</b></color></size>");
            int shown = Mathf.Min(5, prods.Count);
            if (shown == 0) Line("<size=11><color=#7A5F33>  · 今日无够便宜的特产可捡。</color></size>");
            for (int i = 0; i < shown; i++)
                Line("<size=11>  · <color=#3C6B48>" + prods[i].City + "</color> <b>" + prods[i].Good + "</b>  供 <color=#9E4A24>" + prods[i].Ask + "</color> 金 · 存 " + prods[i].Stock + " 件</size>");
            Flush();

            sb.Length = 0;
            Line("<size=13><color=#9E4A24><b>◆ 晨报速递 · 卖高价(卖出价 − 该货最低进价 → 去哪出手)</b></color></size>");
            shown = Mathf.Min(5, needs.Count);
            if (shown == 0) Line("<size=11><color=#7A5F33>  · 今日无特别抢手的缺货。</color></size>");
            for (int i = 0; i < shown; i++)
                Line("<size=11>  · 把 <b>" + needs[i].Good + "</b> 运去 <color=#9E4A24>" + needs[i].City + "</color>  求 <color=#3C6B48>" + needs[i].Bid + "</color> 金 · 约可收 " + needs[i].Depth + " 件 · 单件毛利约 <color=#9E4A24>" + needs[i].Margin + "</color> 金</size>");
            Flush();

            // 三) 分港行市: 各港一块(城块), 交给 SeaHud 按「头版之后: 每版 4 栏 × 每栏至多 3 城」排行情版。
            //      行文紧凑到一货一行: 城名首行(◆ 城 大小·海域小注), 下面 ●特产(供=供货价 / 存=存量)、
            //      ▲缺货(求=求购价 / 约收=可收件数)各一行 —— 一行一货基本不折行, 窄栏里也好稳稳叠 3 城。
            //      全报字号在 SeaHud 分页时统一, 这里不写 <size> 只留色/粗体。
            foreach (var p in world.Ports)
            {
                string an = p.Area != null ? p.Area.Name : "未知海域";
                string sz = p.Size >= 3 ? "大港" : p.Size == 2 ? "中港" : "小港";
                if (p.Blocked)
                {
                    Line("<b>✖ " + p.Name + "</b>  <color=#8A6D3F>" + sz + " · " + an + "</color>  <color=#A03A24>大事件封锁 · 今日停市</color>");
                    Flush();
                    continue;
                }
                Line("<b>◆ " + p.Name + "</b>  <color=#8A6D3F>" + sz + " · " + an + "</color>");
                var specIdx = new List<int>();
                for (int s = 0; s < (p.Specialties != null ? p.Specialties.Length : 0); s++)
                {
                    int gi = world.GoodIndex(p.Specialties[s]);
                    if (gi >= 0) specIdx.Add(gi);
                }
                specIdx.Sort((x, y) => en.GetCell(p, goods[x]).R.CompareTo(en.GetCell(p, goods[y]).R));
                for (int s = 0; s < specIdx.Count; s++)
                {
                    var g = goods[specIdx[s]];
                    Line("  <color=#3C6B48>●</color> 特产 <b>" + g.Name + "</b>  供 " + en.AskPrice(p, g) + " · 存 " + en.BuyStock(p, g));
                }
                var impIdx = new List<int>();
                for (int s = 0; s < (p.Imports != null ? p.Imports.Length : 0); s++)
                {
                    int gi = world.GoodIndex(p.Imports[s]);
                    if (gi >= 0) impIdx.Add(gi);
                }
                impIdx.Sort((x, y) => en.GetCell(p, goods[y]).R.CompareTo(en.GetCell(p, goods[x]).R));
                for (int s = 0; s < impIdx.Count; s++)
                {
                    var g = goods[impIdx[s]];
                    Line("  <color=#9E4A24>▲</color> 缺货 <b>" + g.Name + "</b>  求 " + en.BidPrice(p, g) + " · 约收 " + IntelDepth(p, g));
                }
                Flush();
            }
            return blocks;
        }

        sealed class IntelCand
        {
            public string City, Good;
            public int Ask, Stock, Bid, Depth, Margin;
        }


        // =============================================================
        // 船坞: 买船 / 卖船 / 改装(SeaHud 的船坞浮层调用)
        // =============================================================
        public int FleetShipCount => fleet != null ? fleet.Ships.Count : 0;
        public int FleetShipMax => cat != null ? cat.Tuning.FleetMaxShips : 5;

        // 该船型能否在本港购得(看 年份 / 所属航区 / 舰队上限 / 现金)。why = 不可购原因(可空=可购)
        public bool ShipBuyable(ShipModel m, out string why)
        {
            why = null;
            if (State != Mode.Docked) { why = "靠港后才能购船"; return false; }
            if (m == null) { why = "未知船型"; return false; }
            if (engine != null && m.Year > engine.Year) { why = m.Year + " 年后此船才下水"; return false; }
            if (m.AreaUnlock != null && m.AreaUnlock.Length > 0)
            {
                bool ok = false;
                foreach (var aid in m.AreaUnlock) if (aid == Current.Area.Id) { ok = true; break; }
                if (!ok)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < m.AreaUnlock.Length; i++)
                    {
                        if (i > 0) sb.Append('/');
                        SeaArea aa; if (world != null && world.Areas.TryGetValue(m.AreaUnlock[i], out aa)) sb.Append(aa.Name);
                        else sb.Append(m.AreaUnlock[i]);
                    }
                    why = "本港不售, 需到「" + sb + "」一带船坞";
                    return false;
                }
            }
            if (fleet != null && fleet.Ships.Count >= cat.Tuning.FleetMaxShips) { why = "船队已满(" + cat.Tuning.FleetMaxShips + " 艘)"; return false; }
            if (fleet.Gold < m.Price) { why = "资金不足"; return false; }
            return true;
        }

        public bool BuyShip(string modelId)
        {
            if (State != Mode.Docked) { Banner = "靠港后才能购船。"; return false; }
            var m = cat != null ? cat.FindShip(modelId) : null;
            string why;
            if (!ShipBuyable(m, out why)) { Banner = "无法购船: " + why; return false; }
            fleet.Gold -= m.Price;
            FleetBuilder.BuildShip(fleet, m.Id, m.CrewMin, flagship: false);
            RebuildFleetVisual();
            PushLog("在船坞购得「" + m.Name + "」(载 " + m.Capacity + " · 航速 " + m.Speed + "), 花费 " + Money(m.Price));
            Banner = "🛒 新船入列: " + m.Name;
            return true;
        }

        // 出售折价 = 船价五成 + 已改装费各回五成
        public long ShipSellValue(ShipState s)
        {
            if (s == null) return 0L;
            long v = (long)Mathf.Round(s.Model.Price * 0.5f);
            foreach (var r in s.Refits) v += r.Cost / 2L;
            return v;
        }

        public bool SellShip(ShipState s)
        {
            if (State != Mode.Docked) { Banner = "靠港后才能出售。"; return false; }
            if (s == null) return false;
            if (s.IsFlagship) { Banner = "旗舰不能出售 —— 要卖先得另立旗舰。"; return false; }
            if (s.Cargo.Count > 0) { Banner = "先把船上货物卖光, 再出售空船。"; return false; }
            long val = ShipSellValue(s);
            fleet.Ships.Remove(s);
            fleet.Gold += val;
            RebuildFleetVisual();
            PushLog("船坞售出「" + s.Model.Name + "」, 回笼 " + Money(val));
            Banner = "已售出 " + s.Model.Name + ", 回笼 " + Money(val);
            return true;
        }

        // 换旗舰: 把 s 提到队首(原旗舰若在则顺位后移), 旗舰标记重排
        public bool MakeFlagship(ShipState s)
        {
            if (State != Mode.Docked) { Banner = "靠港后才能任免旗舰。"; return false; }
            if (s == null || s.IsFlagship || fleet == null || !fleet.Ships.Contains(s)) return false;
            fleet.Ships.Remove(s);
            fleet.Ships.Insert(0, s);
            for (int i = 0; i < fleet.Ships.Count; i++) fleet.Ships[i].IsFlagship = i == 0;
            RebuildFleetVisual();
            PushLog("在船坞任命 " + s.Model.Name + " 为新旗舰。");
            Banner = "⭐ " + s.Model.Name + " 升为旗舰。";
            return true;
        }

        // 改造费预览(不扣钱, 与 FleetOps.RefitCalc 同口径; 槽满返回 -1)
        public long RefitCostPreview(ShipState s, RefitKind kind)
        {
            if (s == null || !s.CanRefit) return -1L;
            RefitRates r = null;
            foreach (var kv in cat.Tuning.Refit) if (kv.Value.Kind == kind) { r = kv.Value; break; }
            if (r == null) return -1L;
            long cost = (long)Mathf.Round(s.Model.Price * r.CostMinPct);
            float cut = fleet.Bonus != null ? fleet.Bonus.RefitCostCut : 0f;
            if (cut > 0f) cost -= (long)(cost * (cut > 1f ? 1f : cut));
            return cost;
        }

        public bool DoRefit(ShipState s, RefitKind kind)
        {
            if (State != Mode.Docked) { Banner = "靠港后才能改装。"; return false; }
            var ap = FleetOps.RefitCalc(fleet, s, kind);
            if (ap == null)
            {
                if (s != null && !s.CanRefit) Banner = s.Model.Name + " 的改装槽已满, 不能再加。";
                else Banner = "改装费不够。";
                return false;
            }
            PushLog("船坞改装「" + s.Model.Name + "」: " + RefitCn(kind) + " " + RefitDeltaCn(kind, ap.EffectValue) + ", 花 " + Money(ap.Cost));
            Banner = "🔧 改装完成: " + s.Model.Name + " " + RefitCn(kind);
            return true;
        }

        public static string RefitCn(RefitKind k)
        {
            switch (k)
            {
                case RefitKind.Capacity: return "加装货舱";
                case RefitKind.Fire: return "增配火炮";
                case RefitKind.Armor: return "加厚装甲";
                default: return "改良桅帆";
            }
        }
        public static string RefitDeltaCn(RefitKind k, float v)
        {
            return k == RefitKind.Speed ? "+" + v.ToString("0.0") + " 节" : "+" + Mathf.RoundToInt(v * 100f) + "%";
        }
        public static string RefitExplainCn(RefitKind k)
        {
            switch (k)
            {
                case RefitKind.Capacity: return "加装货舱: 载货量上升, 能多装买卖";
                case RefitKind.Fire: return "增配火炮: 海战火力上升, 遇海盗更不易吃亏";
                case RefitKind.Armor: return "加厚装甲: 船更抗打耐撞";
                default: return "改良桅帆: 航速提升, 海上走得更快更省时";
            }
        }

        public void PushLog(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            Log.Insert(0, "· " + s);
            LogDay.Insert(0, engine != null ? engine.Day : 0);   // 记下发生那天 → HUD 按日期分组
            if (Log.Count > MaxLogLines)
            {
                Log.RemoveRange(MaxLogLines, Log.Count - MaxLogLines);
                LogDay.RemoveRange(MaxLogLines, LogDay.Count - MaxLogLines);
            }
            LogVersion++;
        }

        // =============================================================
        // 存 / 读进度(单档文本, 存 persistentDataPath)
        // =============================================================
        static string SavePath() => System.IO.Path.Combine(Application.persistentDataPath, "sea_save.json");

        public bool SaveGame()
        {
            if (fleet == null || engine == null || world == null || Current == null) return false;
            if (State != Mode.Docked) { Banner = "航行中不能存档 —— 先靠港再存。"; return false; }
            try
            {
                var d = new SeaSaveData();
                d.day = engine.Day;
                d.portId = Current.Id;
                d.startWorth = StartWorth;
                d.gold = fleet.Gold;
                d.morale = (int)fleet.Morale;
                d.sick = fleet.Sick;
                d.foodOutDays = fleet.FoodOutDays;
                d.waterOutDays = fleet.WaterOutDays;
                d.teaOutDays = fleet.TeaOutDays;
                d.food = fleet.Prov.Food;
                d.water = fleet.Prov.Water;
                d.tea = fleet.Prov.Tea;
                foreach (var s in fleet.Ships)
                {
                    var ss = new SeaShipSave { model = s.Model.Id, crew = s.CrewAboard, damage = s.Damage };
                    foreach (var kv in s.Cargo) ss.cargo.Add(new SeaCargoSave { good = kv.Key, qty = kv.Value });
                    d.ships.Add(ss);
                }
                // 持仓成本账本
                foreach (var kv in _basisQty)
                {
                    if (kv.Value <= 0) continue;
                    long cb = _basisCost.TryGetValue(kv.Key, out var c) ? c : 0L;
                    d.basis.Add(new SeaBasisSave { good = kv.Key, qty = kv.Value, cost = cb });
                }
                int G = world.Goods.Count, P = world.Ports.Count;
                d.eq = new float[P * G]; d.r = new float[P * G]; d.stock = new float[P * G];
                int pi = 0;
                foreach (var p in world.Ports)
                {
                    var cells = world.Market[p.Id];
                    for (int gi = 0; gi < G && cells != null && gi < cells.Length; gi++)
                    {
                        int idx = pi * G + gi;
                        d.eq[idx] = cells[gi].Eq; d.r[idx] = cells[gi].R; d.stock[idx] = cells[gi].Stock;
                    }
                    pi++;
                }
                // 情报站刊物档案(我的订阅): 每份都随档走
                for (int i = 0; i < IntelArchive.Count; i++)
                {
                    var e = IntelArchive[i];
                    if (e == null) continue;
                    d.intel.Add(new SeaIntelSave
                    {
                        kind = e.kind, key = e.key, title = e.title, sub = e.sub, body = e.body, paid = e.paid
                    });
                }
                // 地图旗标: 设置开关 + 到过的城市
                d.flagsOn = FlagsOn;
                if (_cityFlags.Count > 0) d.flagVisited = new List<string>(_cityFlags);
                // 黑雾: 已探明网格压成位图(base64; 全黑存空串)
                d.fog = _fog != null ? _fog.EncodeState() : "";
                var json = JsonUtility.ToJson(d);
                System.IO.File.WriteAllText(SavePath(), json);
                PushLog("已存档: " + engine.Year + " 年, 在 " + Current.Name + ", 现金 " + Money(fleet.Gold));
                Banner = "💾 已保存进度。";
                return true;
            }
            catch (System.Exception ex) { Banner = "保存失败: " + ex.Message; return false; }
        }

        public bool LoadGame()
        {
            if (!System.IO.File.Exists(SavePath())) { Banner = "还没有存档可载入。"; return false; }
            SeaSaveData d = null;
            try { d = JsonUtility.FromJson<SeaSaveData>(System.IO.File.ReadAllText(SavePath())); }
            catch (System.Exception) { d = null; }
            if (d == null) { Banner = "存档损坏, 无法载入。"; return false; }
            try
            {
                if (!BootData()) return false;                       // 全新世界/引擎/舰队
                _basisQty.Clear(); _basisCost.Clear();               // 账本随档重来
                engine.SetDay(d.day);
                // 逐格恢复全港行情(顺序: Ports×Goods 与存档时一致)
                int G = world.Goods.Count, pi = 0;
                if (d.eq != null)
                    foreach (var p in world.Ports)
                    {
                        var cells = world.Market[p.Id];
                        for (int gi = 0; gi < G && cells != null && gi < cells.Length; gi++)
                        {
                            int idx = pi * G + gi;
                            if (idx >= d.eq.Length) break;
                            cells[gi].Eq = d.eq[idx];
                            if (d.r != null && idx < d.r.Length) cells[gi].R = d.r[idx];
                            if (d.stock != null && idx < d.stock.Length) cells[gi].Stock = d.stock[idx];
                        }
                        pi++;
                    }
                // 舰队
                fleet.Gold = d.gold;
                fleet.Morale = Mathf.Clamp(d.morale, 0, cat.Tuning.MoraleMax);
                fleet.Sick = Mathf.Max(0, d.sick);
                fleet.FoodOutDays = Mathf.Max(0, d.foodOutDays);
                fleet.WaterOutDays = Mathf.Max(0, d.waterOutDays);
                fleet.TeaOutDays = Mathf.Max(0, d.teaOutDays);
                fleet.Prov.Food = Mathf.Max(0, d.food);
                fleet.Prov.Water = Mathf.Max(0, d.water);
                fleet.Prov.Tea = Mathf.Max(0, d.tea);
                fleet.Ships.Clear();
                for (int i = 0; i < d.ships.Count; i++)
                {
                    var ss = d.ships[i];
                    var m = cat.FindShip(ss.model);
                    if (m == null) continue;
                    int crew = Mathf.Clamp(ss.crew, m.CrewMin, m.CrewMax);
                    FleetBuilder.BuildShip(fleet, ss.model, crew, flagship: i == 0);
                    var ship = fleet.Ships[fleet.Ships.Count - 1];
                    ship.Damage = Mathf.Max(0, ss.damage);
                    foreach (var c in ss.cargo)
                    {
                        if (ship.Cargo.Count >= ship.Model.Slots) break;
                        if (!world.GoodById.ContainsKey(c.good)) continue;
                        int byW = (ship.EffCapacity - ship.OccupiedWeight(WeightOfWorld)) / Mathf.Max(1, WeightOfWorld(c.good));
                        if (byW <= 0) continue;
                        ship.Cargo[c.good] = Mathf.Min(Mathf.Max(0, c.qty), byW);
                    }
                }
                if (fleet.Ships.Count == 0)   // 兜底: 至少旗舰
                {
                    var fm = cat.FindShip(flagshipId);
                    if (fm != null) FleetBuilder.BuildShip(fleet, flagshipId, (fm.CrewMin + fm.CrewMax) / 2, flagship: true);
                }
                // 持仓成本账本(只认还在货表里的货)
                foreach (var b in d.basis)
                {
                    if (b == null || b.qty <= 0 || !world.GoodById.ContainsKey(b.good)) continue;
                    _basisQty[b.good] = b.qty; _basisCost[b.good] = b.cost;
                }
                // 情报站刊物档案(我的订阅): 只认合法刊物
                IntelArchive.Clear();
                if (d.intel != null)
                    for (int i = 0; i < d.intel.Count; i++)
                    {
                        var e = d.intel[i];
                        if (e == null) continue;
                        if (e.kind != "paper" && e.kind != "report") continue;
                        if (string.IsNullOrEmpty(e.title)) continue;
                        IntelArchive.Add(e);
                    }
                Current = world.FindPort(d.portId);
                if (Current == null) Current = world.FindPort(homePortId);
                if (Current == null) Current = world.Ports[0];
                Dest = null; State = Mode.Docked;
                ExploreSet = false; ExploreWorld = Vector3.zero;
                _anchor = Vector3.zero; HideExploreFlag();   // 探索是临时目标, 存档不带
                PlannedDays = 0; DaysDone = 0; SailProgress01 = 0f;
                StartWorth = d.startWorth;
                // 地图旗标: 设置开关 + 到过城市(红旗), 随档恢复
                FlagsOn = d.flagsOn;
                _cityFlags.Clear();
                if (d.flagVisited != null)
                    foreach (var id in d.flagVisited)
                        if (!string.IsNullOrEmpty(id) && world.FindPort(id) != null) _cityFlags.Add(id);
                ApplyCityFlags();
                Log.Clear(); LogDay.Clear(); LogVersion++;
                fleet.Bonus = Sea.FleetBonus.From(null);
                RebuildFleetVisual();          // 僚舰条数按存档重摆(可能比新建多/少)
                PushLog("已载入存档 · " + engine.Year + " 年 " + Current.Name + " · 现金 " + Money(fleet.Gold));
                Banner = "📂 已载入进度。";
                PlaceShipAt(Current, Time.time);
                // 黑雾: 以档为准恢复已探明区域。
                //   新档直接还原海图; 无 fog 字段的旧档 → 至少让"到过的城"在雾里现形一圈(老玩家地图不被清零),
                //   随后的 Update 还会在当前位置继续现形。
                if (_fog != null)
                {
                    bool hadFog = !string.IsNullOrEmpty(d.fog);
                    _fog.DecodeState(d.fog);
                    if (!hadFog && _cityFlags.Count > 0)
                        foreach (var id in _cityFlags)
                        {
                            var pp = world.FindPort(id);
                            if (pp == null) continue;
                            var w = LonLatToWorld(pp);
                            _fog.RevealAt(w.x, w.z, FogDockR);
                        }
                    _fog.Flush();
                }
                FrameHome(); ApplyCamera(_target);
                _histAsk.Clear(); _histDay.Clear();   // 情报档案按读档后的日子重记
                SnapMarketDay();
                return true;
            }
            catch (System.Exception ex) { Banner = "载入失败: " + ex.Message; return false; }
        }

        static string Money(long v)
        {
            if (v >= 10000) return (v / 10000.0).ToString("0.0") + "万";
            return v.ToString();
        }
    }

    // Play 模式兜底: 任意场景直接进播放也能看到游戏(沿用旧版 SeaGame 的 AutoBoot 思路)
    public static class SeaLauncher
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoBoot()
        {
            if (UnityEngine.Object.FindFirstObjectByType<SeaPlay>() != null) return;
            var go = new GameObject("SEA · 旗舰航线(自动创建)");
            go.AddComponent<SeaPlay>();
            go.AddComponent<SeaHud>();
        }
    }
}
