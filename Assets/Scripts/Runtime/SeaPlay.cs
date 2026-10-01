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
        // Dest 一变就同步"目标绿旗"(见 SyncTargetFlag) —— 点选 / 出航 / 到港 / 读档 所有赋值点
        //   自动一致, 不会漏插一面或漏收一面。收绿旗只有"不再有城目标"(改点海面/到港/读档)这一条路径。
        Port _dest;
        public Port Dest
        {
            get { return _dest; }
            private set { _dest = value; SyncTargetFlag(); }
        }
        // ---- 探索模式: 目标不是某座确定城, 而是海图上任意一点 —— 点海面插红旗, 船驶到那儿抛锚 ----
        public bool ExploreSet { get; private set; }        // 当前目标是海图上插旗处(非港口)
        public Vector3 ExploreWorld { get; private set; }   // 插旗/锚地世界坐标
        public bool HasSailTarget => Dest != null || ExploreSet;   // 有没有可出航的目标(城或旗点)
        public Mode State { get; private set; } = Mode.Docked;
        // 「一次进港」的序号: 开局 / 读档各 +1。行情表"手里有货的排最上面"只在进港那一刻排一次,
        //   而 SeaHud 光看"港口 id 有没有变"判不全 —— 启动首页那几帧 Current 就已经是母港
        //   (见 BootData), 于是"读一份存在母港的档"时 id 从头到尾没变过, 排序永远触发不了。
        //   这里给它一个不含糊的信号: 新开一程 / 载入一档, 都是一次进港。
        public int VoyageSerial { get; private set; }

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
        // 每港"球半径 / 圈半径"两件套: 圈不许与邻城的圈相交, 而点选命中半径 = 圈半径(画多大就点多大)。
        //   两份值在 BuildMarkTable 一次算好 —— 画球 / 画圈 / 点选命中 / 地名浮高 全走它, 不各算各的。
        struct PortMark { public float BallR; public float RingR; }
        readonly Dictionary<string, PortMark> _mark = new Dictionary<string, PortMark>();
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
        const float FogDockR = 40f;   // 到港以港为心亮一大圈(40°), 便于看图定下一程; **只在抵达那一刻**揭一次(见 Arrive)
        const float FogAnchorR = 24f; // 抛锚停船: 在船位再亮一小圈, 够看清下一点(不必像进港那样一大片)
        // 开局可视范围 = 出发点周围**一小圈**, 不是 40° 的泊港大圈(用户定: 「开局时缩小可视范围, 就出发点周围一小圈」)。
        //   12° 是照着母港塞维利亚的邻居定的: 里斯本 2.8°、波尔多 8.4° 在圈内(一开局就有两处买卖可去),
        //   热那亚 13.2°、伦敦 14.7° 在圈外 —— 那些得自己驶过去, 一路把雾走开。
        //   为什么不用 40°: 那半个地中海开局就是亮的, "探索"这层玩法等于没有。
        const float FogStartR = 12f;

        Transform _exploreFlag;        // 🚩 探索红旗: 选中的海图点 / 抛锚地标(杆+正红燕尾旗+水面红光环)
        Transform _targetFlag;         // 🟢 目标绿旗: 当前选中的出航目标城(随 Dest 自动插/收)
        Transform _routeLine;          // 计划航线: 黄色虚线(自建网格, 见 RebuildRouteLine)
        Mesh _routeMesh;
        string _routeSig;              // 航线签名: 起终点/状态变了才重建网格(逐帧比, 平时零开销)
        static readonly Color ColRoute = new Color(1f, 0.86f, 0.22f);   // 金黄(与"持有"列/大本营旗同色系)
        const float RouteY = 0.46f;        // 贴海面: 高于浪脊(~0.05)与港圈(0.40), 低于光球(1.2+), 不打架
        const float RouteHalfW = 0.11f;    // 虚线半宽(世界单位)
        const float RouteDashLen = 1.05f;  // 一段虚线长
        const float RouteGapLen = 0.75f;   // 虚线段之间的空档
        // 渲染次序明梯: 黑雾 3500 → 航线虚线 3600(SeaRoute.shader) → 船 3700(本值)。
        //   船必须最后画, 否则黄虚线会横切过船身(船在几何上比虚线矮, 深度测试救不了)。
        const int ShipRenderQueue = 3700;
        Vector3 _anchor;               // 抛锚地: 到海图点停船后船所在(随波轻晃的中心), 下次续航从这儿出发
        Transform _ship;
        readonly List<Transform> _shipSails = new List<Transform>();
        Camera _cam;
        // 地球仪镜头: 相机站在**焦点正上方、沿球面法线** _dist 处, 朝球心看 —— 于是"转球"就等于挪焦点。
        //   _focus 存球面点, 每帧归一到半径 R, 抹掉拖拽连乘累积的浮点漂移;
        //   _camUp 是**持久**上方向(当前切平面内的"上"), 拖拽时与 _focus 被同一个四元数一起转。
        //   为什么不每帧从经纬度重算"切向北": 极点上经度无定义, 那样滚转会在极点附近跳变。
        //   持久 up 每帧投影回切平面即可: 既随球面自然转, 又不会抖。
        Vector3 _focus = new Vector3(0f, 0f, -SeaGlobe.R);   // = ToWorld(0,0)
        Vector3 _camUp = Vector3.up;
        float _dist = 78f;   // 沿法线离地表的距离(≈缩放: 越小越近); 78 时整颗球约占屏高 83%

        // 右键「摆正」的过渡: 从当前镜头姿态平滑转到 北上南下 + 舰队居中 的目标姿态。
        //   插值的是**整个镜头四元数**, 不是分别插 _focus 与 _camUp —— 后者会经过
        //   "球已经转到位、滚转还没跟上"的拧巴中间态; 四元数 slerp 天然是一段刚体旋转。
        //   _alignT >= _alignDur 即"没在摆正"(初值就取这个)。
        Quaternion _alignFrom, _alignTo;
        float _alignT = 1f, _alignDur;
        const float AlignSecs = 0.55f;

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
        // 上面那条折线**加密到 ≤1°** 之后的版本(map 空间), 船走它、虚线画它 —— 同一份, 所以不可能"画一条、走另一条"。
        //   为什么必须加密: 球面上两点的**弦**切进球体内部, 弦高 sag = R(1-cos(Δ/2)); Δ=20° 时 0.87 单位,
        //   远大于航线抬升 RouteY(0.46) —— 不加密的话船会钻到地面以下、虚线也会沉进地里。
        List<Vector3> _routePath;
        string _departAreaId;
        System.Random _pirateRng = new System.Random(7);

        // 鼠标拖动海图: 按下移动超阈值 = 拖拽平移; 松手且没拖动 = 点选港口
        bool _downHeld;
        bool _dragPan;
        Vector2 _downScreen;
        bool _midHeld;            // 中键按住平移中
        Vector2 _panPrev;         // 上一帧光标位置 → 增量转球用(每帧只转"上一光标→本光标"这一段, 避免旧锚反馈振荡=抖)
        // [闸门] 左键点击路径记录仪(见 QaClickNote 上方那段说明)。默认开窗, 因为复现路径就是"玩家真手点"。
        float _qaClickUntil = -1f;   // 开窗截止(Time.realtimeSinceStartup); <0 = 关
        int _qaClickNotes;           // 本次开窗已记条数(封顶, 免得狂点刷屏)
        int _qaAutoAt;               // [闸门] -seaAuto: 第几帧自动开局(<0/0 = 没开这个开关)
        int _qaAutoPickAt;           // [闸门] -seaAuto: 第几帧自动"点一下最远的城"(复现第一次点城那一卡)
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
            // 近裁剪面 0.05 → 0.2: 球上 1 单位 = 1° 弧长, 0.05 单位只有 3 角分, 对这套视距毫无必要,
            //   却把 far/near 拉到 40000:1 —— 深度精度全浪费在镜头眼皮底下那一小段。
            //   这一条现在有实际后果: 黑雾壳(SeaFog)与航线(SeaRoute)都是拿 **ZTest LEqual** 跟地表比深度的,
            //   精度不够就会在球缘附近跟地表打架、露出一圈闪烁的缝。0.2 把比值压到 10000:1, 精度翻两番。
            _cam.nearClipPlane = 0.2f;
            _cam.farClipPlane = 2000f;
            _cam.orthographic = false;   // 透视(不再"正俯": 镜头站在焦点法线上, 取景由 _focus/_camUp 决定)
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
            BuildFogVisual();   // 海图盖一层黑雾(初始全黑)
            RevealFogAtPort(Current, FogStartR);   // 开局只揭开出发点周围一小圈, 其余交给玩家自己驶开
            FrameMap();             // 开局先看整颗地球(用户定); 点「回本港」再推近
            ApplyCamera(_focus);    // 首帧: 不等 Update 才摆
            StartWorth = NetWorth();
            PushIntroLogs();
            SnapMarketDay();   // 情报站行情档案从开局当日起逐日留档
            SeaRoute.WarmUp();   // 见 SeaRoute.PumpBuild: 44 万格的寻路栅格先在首页那几秒里分帧建完,
                                 //   别等玩家点下第一座城才现算 —— 那一下是 2.2 秒的定格(实测)。
            ShowHome();   // 启动首页: 黑底盖住海图, 由玩家点「新开航程/继续辉煌」才真正开局(首页曲 = head.wav)
            // 无头截图闸门: 带 -seaShot <目录> 启动时才挂(见 SeaShot.cs)。
            //   挂在**这里**是因为它要调 StartNewVoyage/ViewWorld, 得等世界与首页都就位。
            if (SeaShot.Requested()) gameObject.AddComponent<SeaShot>();
            // [闸门] 见 QaAutoPlay。等 5 帧再动手: 那一刻相机矩阵已经真的应用过一轮,
            //   WorldToScreenPoint 才作数(Awake 当场算容易拿到还没提交投影矩阵的相机)。
            //   等 ~4 秒(240 帧)再开局, 不是等 5 帧: 玩家真实路径是**首页摆着看一会儿**才点「新开航程」,
            //   而 SeaRoute 的栅格正是趁这段时间分帧预建的(SeaRoute.PumpBuild)。开局太早会把
            //   "预建窗口"整个跳掉, 量出来的就不是玩家那条路了。
            else if (System.Array.IndexOf(Environment.GetCommandLineArgs(), "-seaAuto") >= 0)
                _qaAutoAt = Time.frameCount + 240;
        }

        // =============================================================
        // [闸门] `-seaAuto`: 跳过启动首页直接开局, 并把**画内每一座城的屏幕坐标**打进日志。
        //
        //   为什么本轮的病灶非它不可: 症状是"玩家真手点第一下没反应", 而嫌疑最大的一条是
        //   **鼠标事件根本没投递到 Input.GetMouseButtonDown**(窗口焦点/输入层) —— 这一层
        //   恰恰是无头截图闸门永远碰不到的: 截图轮从头到尾没有鼠标, Input 里什么都没有。
        //   有了这个开关, 外部就能真的挪光标、真的按一下, 让那一次点击走**完整条真路径**,
        //   再由点击记录仪(QaClickNote)把四道闸门的当场状态记下来。
        //   它只跳过"点首页那个按钮"这一步 —— 那一步与病灶无关; 之后的玩法路径一字不改。
        //   顺带把城坐标打出来, 是因为外部不知道哪座城在屏幕何处; 由游戏自己报, 才不会点空。
        // =============================================================
        void QaAutoPlay()
        {
            StartNewVoyage();
            if (world == null || _cam == null) { Debug.LogError("[SEA] -seaAuto 开局失败"); return; }
            var sb = new System.Text.StringBuilder();
            foreach (var p in world.Ports)
            {
                Vector3 pw = SeaGlobe.ToWorld(p.Lon, p.Lat);
                if (Vector3.Dot(pw.normalized, _cam.transform.position - pw) <= 0f) continue;   // 背半球
                Vector3 sp = _cam.WorldToScreenPoint(pw);
                if (sp.z <= 0f) continue;
                // 离屏幕边缘 60px 以内的不报: 外部要拿它当真鼠标的目标点, 贴边容易点到别的东西上
                if (sp.x < 60f || sp.x > Screen.width - 60f || sp.y < 60f || sp.y > Screen.height - 60f) continue;
                sb.Append(p.Id).Append('=').Append((int)sp.x).Append(',').Append((int)sp.y).Append(' ');
            }
            Debug.Log("[SEA] -seaAuto 已开局(距地=" + _dist.ToString("F0") + " 屏幕=" + Screen.width + "x" + Screen.height
                + ")。画内各城屏幕坐标: " + sb);
            _qaAutoPickAt = Time.frameCount + 150;   // 约 2.5 秒后自动点一次城, 让首帧开销落在一次可量的调用里
        }

        // [闸门] 自动"点一次最远的城" —— 复现"进游戏第一次点目的地"那一卡。
        //   故意取**最远**的那座: 航线最长、A* 展开的节点最多、虚线顶点最多, 三个开销都取上界。
        //   走的是 PickAt(屏幕点) —— 与真鼠标点城**完全同一条代码路径**(港球判定、设目标、重建航线、
        //   点目标旗), 唯一跳过的就是 Input 那一层, 而那一层已经被排除了(用户实测: 不是没收到, 是卡)。
        void QaAutoPick()
        {
            if (world == null || _cam == null || Current == null) return;
            Port far = null; float best = -1f;
            foreach (var p in world.Ports)
            {
                if (p == Current) continue;
                Vector3 pw = SeaGlobe.ToWorld(p.Lon, p.Lat);
                if (Vector3.Dot(pw.normalized, _cam.transform.position - pw) <= 0f) continue;   // 背半球, 点不到
                float d = SeaGlobe.GreatCircleDegLonLat(Current.Lon, Current.Lat, p.Lon, p.Lat);
                if (d > best) { best = d; far = p; }
            }
            if (far == null) { Debug.LogError("[SEA] -seaAuto 找不到可点的远港"); return; }
            Vector3 sp = _cam.WorldToScreenPoint(SeaGlobe.ToWorld(far.Lon, far.Lat));
            bool gridAlreadyBuilt = SeaRoute.MsEnsure > 0f;   // 预建赶上了没有: 这一行的答案就是修好没修好
            var sw = System.Diagnostics.Stopwatch.StartNew();
            PickAt(new Vector2(sp.x, sp.y));
            sw.Stop();
            Debug.Log("[SEA] -seaAuto 自动点城: " + far.Name + "(" + best.ToString("F0") + "° 外) → "
                + (Dest != null ? Dest.Id : "null(没选中!)")
                + " | PickAt 合计 " + sw.Elapsed.TotalMilliseconds.ToString("F0") + "ms"
                + " | 栅格: " + (gridAlreadyBuilt
                    ? "开局前已建完 " + SeaRoute.MsEnsure.ToString("F0") + "ms, 本次未再付 ✔"
                    : "预建没赶上, 本次同步付了 " + SeaRoute.MsEnsure.ToString("F0") + "ms ✘")
                + " | A* " + SeaRoute.MsAstar.ToString("F0") + "ms / 展开 " + SeaRoute.AstarExpanded + " 节点");
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
            VoyageSerial++;                            // 开新程 = 一次进港(行情表据此重排, 见 SeaHud 的进港分支)
            StartFlagsOn();                            // 启动默认点亮地图旗标(大本营黄旗/到过红旗)
            if (audio != null) audio.PlayBgmDefault();   // 离开主页 → 主题曲(玩家在设置里选过则用所选)
            _home?.Hide();
            ArmClickLogFromArgs();                     // [闸门] 仅 -seaClickLog 时开窗(见 QaArmClickLog)
        }

        // 「继续辉煌」: 读档回上次局面; 没档/坏档留在首页给提示
        public void ContinueVoyage()
        {
            if (LoadGame())
            {
                VoyageSerial++;                        // 读档 = 一次进港: 档存在哪一港就该按那一港的持仓重排
                StartFlagsOn();                        // 读档起步同样默认点亮旗标
                if (audio != null) audio.PlayBgmDefault();
                _home?.Hide();
                ArmClickLogFromArgs();                 // [闸门] 仅 -seaClickLog 时开窗
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
            SeaRoute.PumpBuild(SeaRoute.PumpSliceMs);   // 推进寻路栅格(建完即空转) —— 每帧 2ms, 看不出来
            if (_qaAutoAt > 0 && Time.frameCount >= _qaAutoAt) { _qaAutoAt = 0; QaAutoPlay(); }
            if (_qaAutoPickAt > 0 && Time.frameCount >= _qaAutoPickAt) { _qaAutoPickAt = 0; QaAutoPick(); }
            QaSlowFrameWatch();
            QaBeat();
            HandleCamAndPick();
            TickFlagWaver();
            TickRouteLine();   // 计划航线虚线(只在起终点/状态真变时重建)
            if (State == Mode.Sailing) TickSailing();
            else if (State == Mode.Docked) PlaceShipAt(Current, Time.time);
            else AnchorBob(Time.time);   // Anchored: 船在海图点下抛锚, 随波轻晃不挪位
            // 球背面的港球/光环/旗/船会从球缘外面露出来(球体化带出的新毛病) → 逐帧收。
            //   放在"船已经摆好"之后: 这条剔除要读船的位置算朝向, 早一步就慢一帧。
            CullBackside();
            // 迷雾: 逐帧揭的是**跟着船走**的两种圈(航行=航道条带 16°; 抛锚=中等圈 24°)。
            //   泊港那圈(40°)不在这儿揭, 改在**抵达那一刻**揭一次(见 Arrive) —— 两个理由:
            //   ① 开局那圈只有 FogStartR(12°), 逐帧的泊港圈会在开局第一帧就糊上 40°, 小圈活不过一帧;
            //   ② 读档时雾以档为准(SeaFog.DecodeState), 逐帧的泊港圈会把一份"刚开局"的档顶成 40°,
            //      档里辛苦走出来的探索进度就这么白存了。
            if (_fog != null)
            {
                if (_ship != null && State != Mode.Docked)
                {
                    float rr = State == Mode.Sailing ? FogSailR : FogAnchorR;
                    // 船现在活在**球面**上, 雾活在**经纬**里 → 这里必须反推一次(全项目唯一需要的反推点)
                    SeaGlobe.ToLonLat(_ship.position, out double flon, out double flat);
                    _fog.RevealAt((float)flon, (float)flat, rr);   // 雾按经纬抹圈, 且抹的是**球面角半径**(见 SeaFog.RevealAt)
                }
                _fog.Tick(Time.deltaTime);
            }
        }

        // =============================================================
        // 港口标记 / 世界视觉
        // =============================================================

        // 【map 空间, 不是渲染空间】—— 这一层是"平面经纬图": x=经度, z=-纬度, 1 单位=1 度。
        //   SeaRoute.Route / DaysFor / VoyOrigin / RouteCacheKey / Depart 的起终点 / 探索旗点
        //   全都活在这个空间里, 球体化**不影响**它们, 所以它们一行都不用改。
        //   真正要摆到球面上的地方(港标/旗/船/航线/雾/相机)走 SeaGlobe.ToWorld —— 两者
        //   同名容易混, 所以这里特意叫 Map 不叫 World: 名字里带 World 的那个才是球面。
        public static Vector3 MapToWorld(Port p) => new Vector3(p.Lon, 0f, -p.Lat);

        // 黑雾雾壳就位(罩住整颗地球; 初始全黑, 见 SeaFog.cs)
        void BuildFogVisual()
        {
            if (_fog != null) return;
            _fog = new SeaFog();
            _fog.Build(_root);
        }

        // 以港为心揭一圈雾 —— 开局(FogStartR)与到港(FogDockR)各一次, 都是一次性事件。
        //   当场 Flush 而不是等 Tick 的节流: 那是"抹开途中别卡"的节流(≤11 次/秒), 事件式的揭圈
        //   要让玩家**这一帧**就看见雾散开, 否则到港后得干等 ~0.09s。
        void RevealFogAtPort(Port p, float radius)
        {
            if (_fog == null || p == null) return;
            _fog.RevealAt(p.Lon, p.Lat, radius);
            _fog.Flush();
        }

        // 该港是否已探明(黑雾已掀开、能看见它)? 没建雾(理论不会)一律视为可见。
        public bool CityRevealed(Port p)
        {
            if (p == null) return false;
            if (_fog == null) return true;
            return _fog.Revealed(p.Lon, p.Lat);
        }

        // =============================================================
        // 球缘外剔除 —— 把"投影落到地球剪影之外的东西"收起来。
        //   为什么非做不可: 相机站在球外, 一朵浮在球背面的港球会从**球缘外面**露出来 ——
        //   那条视线上压根没有地表挡着(视线根本不与地球相交), 深度测试无从发挥。
        //   画面上就是球缘外凭空多出一串小点, 像有东西漂在太空里(见 dev/shots/01 右侧那串)。
        //   平面图时代没有"球缘"这回事, 所以这是球体化**带出来的新毛病**, 不是旧疾。
        //
        //   判据: 从相机看该物体、与看球心的**夹角** ≤ 球缘半角 asin(R/|C|) ⟺ 它落在球盘里面。
        //   早先判的是"锚点法线朝不朝相机": 对**贴在地表**的东西两者完全等价(锚点正好压在剪影上时
        //   两个角恰好相等), 但对**架高**的东西判得太松 —— 港球抬 1.7、旗布抬到 5 上下, 锚点明明
        //   还朝着相机、没被球挡着, 却因为抬得高而投影出界。dev/shots/01 右侧那串小点就是这么来的
        //   (临时诊断逐个点过名: 全是 marker_*/ring 与 marker_* 港球, 无一例外)。
        //   所以改成**逐 Renderer 拿自己的 bounds 中心**判: 抬得高的部件先出界, 也就先收起来。
        //
        //   判成"该显示"却真在球背面的那些(投影还在剪影内), 由地球自己的深度挡住 ——
        //   海面是不透明几何、ZWrite 开着, 这条兜底是实打实的, 不是指望。
        //
        //   **用 Renderer.enabled 而不是 SetActive**: 关掉渲染、留住碰撞体, 于是
        //   "点一下远半球那座城"之类原本能做的事一件都不少(标记的球体上挂着点选用的 Collider)。
        // =============================================================
        readonly List<Renderer> _curbRen = new List<Renderer>();

        void AddCurb(Transform root)
        {
            if (root == null) return;
            var rs = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++) _curbRen.Add(rs[i]);
        }

        void CullBackside()
        {
            if (_cam == null) return;
            Vector3 camPos = _cam.transform.position;
            float cLen = camPos.magnitude;      // 球心恒在原点 → 到球心的距离就是模长
            if (cLen <= SeaGlobe.R) return;     // 相机竟在球内(玩家到不了: _dist 下限 6): 内外无从谈起, 整帧放行
            float limb = Mathf.Asin(SeaGlobe.R / cLen) * Mathf.Rad2Deg;   // 剪影半角; 自变量 <1 已由上一行保证
            Vector3 toCenter = -camPos;         // "球心在哪个方向" —— 注意不是"相机看向哪儿", 转球时两者会分开
            for (int i = 0; i < _curbRen.Count; i++)
            {
                var r = _curbRen[i];
                // 船会被重建(RebuildFleetVisual 销毁旧的) → 表里会留下已销毁的条目, 顺手剔掉,
                //   否则几轮买卖船之后这张表只涨不消。
                if (r == null) { _curbRen.RemoveAt(i); i--; continue; }
                // 每帧无条件写一遍(哪怕宿主物体正收着): 旗收着时写进去的值不渲染, 等它再插出来时
                //   已经是本帧算好的结果, 不会有一帧"拿着旧判据外露"的闪烁。
                r.enabled = Vector3.Angle(r.bounds.center - camPos, toCenter) <= limb;
            }
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

            BuildMarkTable();   // 先定每港的球/圈半径(相邻城的圈不相交), 再照着它建球标

            _curbRen.Clear();   // 世界重建 → 旧的剔除名单作废(船那条另在 BuildFleetVisual 里追加)

            foreach (var p in world.Ports)
            {
                var m = BuildMarker(p);
                _markers[p.Id] = m;
                AddCurb(m);      // 港球 + 光环: 转到球背面时收起来(见 CullBackside)
            }

            BuildCityFlags();   // 城市旗标(黄=大本营 / 红=到过)也随世界一起挂好, 初始只亮大本营
            foreach (var kv in _flagRoots) AddCurb(kv.Value);
        }

        // 让网格自己证明绕序朝外, 而不是靠人推。
        //   为什么要这么啰嗦: 球面绕序推错一次, 整颗球会**内外翻**; 而 SEA/Land 是 Cull Back,
        //   翻了的后果是"整颗地球只剩背面", 无头环境(编译/冒烟/SimCheck)全都照过, 只有眼睛能发现。
        //   判据: Unity 正面三角的几何法线 = cross(b-a, c-a)(左手系下由顶点序决定, 与 Cull Back 一致);
        //   拿它和"球心指向该点"比 —— 不算"没朝外"就整片翻过来(只换首尾索引, 不动顶点序)。
        static void FaceOutward(int[] tris, Vector3[] verts)
        {
            if (tris == null || tris.Length < 3) return;
            // 先找一个**非退化**的三角形。经/纬球的第 0 行是 vw 个**重合**的极点顶点,
            //   首三角 (极点a, 极点b, 次行c) 面积为零 → cross 为 0 → dot 为 0 → 判据默默失效
            //   (既不翻也不报错)。必须跳过它。
            int pick = -1;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 p0 = verts[tris[i]], p1 = verts[tris[i + 1]], p2 = verts[tris[i + 2]];
                if (Vector3.Cross(p1 - p0, p2 - p0).sqrMagnitude > 1e-12f) { pick = i; break; }
            }
            if (pick < 0) return;
            Vector3 a = verts[tris[pick]], b = verts[tris[pick + 1]], c = verts[tris[pick + 2]];
            // 判据的**经验依据**: 原来那张平面陆地网格用同样的 (a, b=a+1东, c=a+vw北) 顶点序,
            //   它的 cross(b-a, c-a) 指向 +Y(朝上 = 朝外), 而那张网格配 Cull Back 渲染是正常的。
            //   所以"cross(b-a,c-a) 与朝外方向同侧"就是这套代码里**正面**的定义。
            //   球面上同一套顶点序给出的是朝**内**(east×north 在球面局部系里是 -up), 于是这里该翻。
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), SeaGlobe.Up(a)) >= 0f) return;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int tmp = tris[i]; tris[i] = tris[i + 2]; tris[i + 2] = tmp;
            }
        }

        // 程序化海面: 一整颗**球**(球心=原点, 半径 R+OceanY), 不再是外扩的平板。
        //   · 经度 1° × 纬度 1° 一段 → 361×181 顶点。超过 65535, 必须 32 位索引。
        //   · UV 直接存**经纬归一值**: 波浪改在角域算(见 SEA/Water 注释)。平面上那套"世界 xz 波域"
        //     贴到球上, 极点处 xz→0 会让所有波挤成一团, 且随纬度横向拉伸。
        //   · 无碰撞体 —— 海面不该挡住港口点选。
        const float OceanY = -0.05f;   // 海面在 R 之下一点点: 平陆(R+0.06)与浪脊(最高 +0.03)仍在它之上
        void BuildOcean()
        {
            const int cols = 360, rows = 180;
            int vw = cols + 1, vh = rows + 1;
            var verts = new Vector3[vw * vh];
            var uvs = new Vector2[vw * vh];
            for (int r = 0; r < vh; r++)
            {
                double lat = 90.0 - 180.0 * r / rows;   // r 增 = 往南; 与下面的绕序配套
                for (int c = 0; c < vw; c++)
                {
                    double lon = -180.0 + 360.0 * c / cols;
                    int i = r * vw + c;
                    verts[i] = SeaGlobe.ToSurface(SeaGlobe.ToWorld(lon, lat), OceanY);
                    uvs[i] = new Vector2((float)(lon / 360.0 + 0.5), (float)(lat / 180.0 + 0.5));
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
            FaceOutward(tris, verts);

            var mesh = new Mesh();
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;   // 361×181 = 65341 > 65535 上限
            mesh.vertices = verts;
            mesh.uv = uvs;
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
            // 边缘渐变(用户选的处理): 陆数据只到 lon -110..140 / lat -40..70, 贴到球上会露出四道
            //   "被一刀切断的直边"(北美西缘 / 西伯利亚与本州东端 / 欧亚北缘 / 巴塔哥尼亚)。
            //   靠黑雾盖不住 —— 那四道边离最近的港只有 6~10°, 而靠港一揭就是 40° 的圈。
            //   所以在最外 EdgeFadeCells 圈把高度插到海床、颜色并入深海岩色: 刀切边变成"大陆架没入深海"。
            const int EdgeFadeCells = 3;   // 0.5°/格 → 离边 1.5° 之内开始下沉
            var edgeT = new float[vn];
            for (int y = 0; y < vh; y++)
                for (int x = 0; x < vw; x++)
                {
                    int dEdge = Mathf.Min(Mathf.Min(x, vw - 1 - x), Mathf.Min(y, vh - 1 - y));
                    edgeT[y * vw + x] = Mathf.Clamp01(dEdge / (float)EdgeFadeCells);
                }
            for (int y = 0; y < vh; y++)
            {
                double lat = lat0 + y * dLat;
                int row = y * vw;
                for (int x = 0; x < vw; x++)
                {
                    int i = row + x;
                    double lon = lon0 + x * dLon;
                    bool land = SeaMapGen.LandAt(lon, lat);
                    isLand[i] = land;
                    // 高度沿**半径**偏移 —— 球上的"上"就是法线, 原来加在 Y 上的量现在加在法线上
                    float h = Mathf.Lerp(SeaFloorY, land ? LandTopY : SeaFloorY, edgeT[i]);
                    pos[i] = SeaGlobe.ToSurface(SeaGlobe.ToWorld(lon, lat), h);
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
                    var baseC = new Color32((byte)Mathf.Clamp(c.r + nz, 0, 255),
                                            (byte)Mathf.Clamp(c.g + nz, 0, 255),
                                            (byte)Mathf.Clamp(c.b + nz, 0, 255), 255);
                    // 边缘渐变: 与高度下沉共用**同一个** edgeT → "沉下去"和"变深"同时发生
                    col[i] = Color32.Lerp(rockC, baseC, edgeT[i]);
                }
            }
            var tris = new int[cols * rows * 6];
            int t = 0;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    int a = y * vw + x, b = a + 1, c = a + vw, d = c + 1;
                    tris[t++] = a; tris[t++] = b; tris[t++] = c;   // y 增 = 纬度增 = 朝北
                    tris[t++] = b; tris[t++] = d; tris[t++] = c;
                }
            FaceOutward(tris, pos);   // 球面上"朝北"不再等于"朝外" → 交给网格自己判

            var mesh = new Mesh();
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;   // >65k 顶点必需 32 位索引
            mesh.vertices = pos;
            mesh.colors32 = col;
            mesh.triangles = tris;
            // 法线**显式给径向**: 球面正确的外法线就是它, 不该让 RecalculateNormals 从绕序去猜。
            //   猜反了整颗球日照翻面(暗面朝太阳), 而编译/冒烟/SimCheck 全绿 —— 只有眼睛能发现。
            var nrm = new Vector3[vn];
            for (int i = 0; i < vn; i++) nrm[i] = SeaGlobe.Up(pos[i]);
            mesh.normals = nrm;
            mesh.RecalculateBounds();

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
            // 港标挂上**球面局部系**(X=东 Y=法线 Z=北): 于是下面"球沿 +Y 浮起""光环铺在 XZ 平面"
            // 这些局部写法一行都不用改 —— 局部 +Y 自动变成"离地表向上", 局部 XZ 自动变成切平面。
            go.transform.position = SeaGlobe.ToWorld(p.Lon, p.Lat);
            go.transform.rotation = SeaGlobe.SurfaceRotation(p.Lon, p.Lat);

            var mk = MarkOf(p);
            float ballR = mk.BallR;                        // 球视觉半径(密港被压小, 见 BuildMarkTable)
            var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.name = "ball";
            ball.transform.SetParent(go.transform, false);
            ball.transform.localScale = Vector3.one * (ballR * 2f);   // 球网格半径=0.5×缩放 → 视觉半径 = ballR
            // 球底定在 BallFloatY(1.2): 平陆(0.06)与浪脊(+0.03)都远在其下 → 光球永远悬在大陆上方, 不被遮挡
            ball.transform.localPosition = new Vector3(0f, BallFloatY + ballR, 0f);
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
            float rr = mk.RingR;   // 圈半径: 已按"不与邻城相交"收过(BuildMarkTable); 同时也是点选命中半径
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

        // =============================================================
        // 每港的球半径 / 圈半径(全图一次算好, 之后画球、画圈、点选、地名浮高都查它)
        //   · 圈: 想要的半径 = 光球半径×1.35; 但**不得超过"到最近邻城距离"的 46%** ——
        //     两座邻城各自取 46% ⇒ 两圈半径和 ≤ 92% 距离, 中间永远留缝, 圈不会相交。
        //     (最近邻距离是取"自己的"，对自己与任意邻城 j 都有 dmin ≤ d_ij，故 r_i+r_j ≤ 0.92·d_ij。)
        //   · 球: 仍是"港越大球越大", 但保证圈能露在球外面一圈 —— 圈被挤小到跟球差不多时, 球跟着收,
        //     不会出现"球把圈整个吃掉"(珠三角那种密港就是这种情况)。
        //   · 这两半径同时就是点选命中半径(见 PortAtScreen): 画多大就点多大。
        //   单位: 这几个数照"度"估的, 而 R 取 180/π 让 1 世界单位 = 1° 弧长 → 数值原样继续可用。
        //   圈是画在港标**切平面**上的正圆(港标的局部 XZ 平面), 不是球面小圆; 差 sag ≈ rr²/(2R),
        //   rr=2 时 0.035 单位, 相对圈本身的高度 0.40 可忽略, 所以不值得为它改写线器。
        // =============================================================
        const float RingK = 1.35f;        // 圈半径 = 光球半径 × 此值(与旧版一致, 宽松处观感不变)
        const float RingGapK = 0.46f;     // 圈半径上限 = 最近邻距离 × 此值(<0.5 才有缝)
        const float RingMin = 0.28f;      // 圈半径下限(防极端密港把圈缩没; 现表最近的一对 0.92° 用不到它)
        const float BallInRingK = 0.72f;  // 球半径 ≤ 圈半径 × 此值(给圈留出可见的一环)

        void BuildMarkTable()
        {
            _mark.Clear();
            if (world == null) return;
            foreach (var p in world.Ports)
            {
                float want = MarkerRadius(p) * RingK;
                float ring = Mathf.Max(RingMin, Mathf.Min(want, NearestPortDist(p) * RingGapK));
                float ball = Mathf.Min(MarkerRadius(p) * 0.5f, ring * BallInRingK);
                _mark[p.Id] = new PortMark { BallR = ball, RingR = ring };
            }
        }

        // 到最近邻城的距离(**大圆度**; R 取 180/π 使得度与球面世界单位数值重合, 所以这个数直接当世界单位用)。
        //   原先是 map 空间的平面欧氏距离。在低纬、近距离下两者几乎一样(这也是它一直没出问题的原因),
        //   但高纬会差出 1/cosφ 倍 —— 挪威/冰岛一带量出来偏大, 圈就能画得比"两圈不相交"允许的更大。
        float NearestPortDist(Port p)
        {
            float best = float.MaxValue;
            foreach (var q in world.Ports)
            {
                if (q == p || q.Id == p.Id) continue;
                float d = SeaGlobe.GreatCircleDegLonLat(p.Lon, p.Lat, q.Lon, q.Lat);
                if (d < best) best = d;
            }
            return best == float.MaxValue ? 4f : best;   // 全图只剩一座城时的兜底
        }

        // 查表; 表还没建好(理论上只在世界未就绪时)就退回旧口径, 不让调用方拿到 0 半径
        PortMark MarkOf(Port p)
        {
            PortMark mk;
            if (_mark.TryGetValue(p.Id, out mk)) return mk;
            float r0 = MarkerRadius(p);
            return new PortMark { BallR = r0 * 0.5f, RingR = r0 * RingK };
        }

        public float MarkerBallTopY(Port p)
        {
            return BallFloatY + MarkOf(p).BallR * 2f;   // 球心 = BallFloatY + 半径(球底恒贴 BallFloatY) → 顶 = 底 + 直径
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
            //   偏移跟球半径走(球被压小的密港, 旗子也跟着贴回来, 不会孤零零飘在远处)
            float side = MarkOf(p).BallR * 2f + (home ? 0.7f : 0.4f);
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
            AddCurb(root);
        }
        void ShowExploreFlag(Vector3 w)
        {
            EnsureExploreFlag();
            // w 是 map 空间点 → 上球面并挂球面局部系(杆朝法线、旗面铺在切平面上, 局部几何原样成立)
            _exploreFlag.position = SeaGlobe.ToWorld(w.x, -w.z);
            _exploreFlag.rotation = SeaGlobe.SurfaceRotation(w.x, -w.z);
            _exploreFlag.gameObject.SetActive(true);
        }
        void HideExploreFlag()
        {
            if (_exploreFlag != null) _exploreFlag.gameObject.SetActive(false);
        }

        // =============================================================
        // 目标绿旗: 当前选中的那座"要去做买卖的城"。与探索红旗同形(杆 + 燕尾旗 + 水面光环), 只换正绿 ——
        //   "红旗 = 我要去的那片海(抛锚), 绿旗 = 我要去的那座城", 同屏也一眼分得清。
        //   插/收完全跟着 Dest 走(Dest 的 setter → SyncTargetFlag), 不靠各调用点手动补,
        //   所以点选 / SetDestination / 到港 / 读档 都是同一条路径, 不会漏插或漏收。
        // =============================================================
        static readonly Color ColTargetFlag = new Color(0.20f, 0.92f, 0.36f);   // 正绿(比陆地绿更亮更饱和, 压得住底色)

        void EnsureTargetFlag()
        {
            if (_targetFlag != null || _root == null) return;
            var root = new GameObject("target_flag").transform;
            root.SetParent(_root, false);
            root.gameObject.SetActive(false);
            const float s = 1.2f, baseY = 0.35f;   // 尺寸与探索红旗完全一致, 只差颜色
            float H = 2.5f * s;
            float L = 2.0f * s;
            float W = 1.05f * s;
            var pole = Box(StdMat(ColWoodFlag), new Vector3(0.13f, H, 0.13f));
            pole.SetParent(root, false);
            pole.localPosition = new Vector3(0f, baseY + H * 0.5f, 0f);
            var cloth = PennantMesh(ColTargetFlag, L, W * 0.5f);
            cloth.SetParent(root, false);
            cloth.localPosition = new Vector3(0f, baseY + H, 0f);
            cloth.localRotation = Quaternion.Euler(FlagTilt, 0f, 0f);
            _flagCloth["target"] = cloth;                  // 并入逐帧轻摆
            _flagPhase["target"] = Mathf.PI;               // 与探索旗(π/2)错开相位, 两旗同屏不齐刷刷

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
            lr.startColor = lr.endColor = ColTargetFlag;
            lr.startWidth = lr.endWidth = 0.09f;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            _targetFlag = root;
            AddCurb(root);
        }

        // Dest 的从属刷新(幂等, 随便多调): 有目标城 → 旗插到那城; 没目标城 → 收旗。
        void SyncTargetFlag()
        {
            if (_dest == null)
            {
                if (_targetFlag != null && _targetFlag.gameObject.activeSelf)
                    _targetFlag.gameObject.SetActive(false);
                return;
            }
            if (_root == null || world == null) return;   // 世界还没搭好 → 等世界就绪后再插(正常路径不会走到)
            EnsureTargetFlag();
            if (_targetFlag == null) return;
            _targetFlag.position = SeaGlobe.ToWorld(_dest.Lon, _dest.Lat);
            _targetFlag.rotation = SeaGlobe.SurfaceRotation(_dest.Lon, _dest.Lat);
            if (!_targetFlag.gameObject.activeSelf) _targetFlag.gameObject.SetActive(true);
        }

        // =============================================================
        // 计划航线: 选好目标就在海图上画一条**黄色虚线**, 把"从这儿到那儿怎么走"先摆给玩家看。
        //   · 所见即所行: 取的就是出航时船要走的那条折线 —— 同一个 SeaRoute.Route + 同一个缓存 key,
        //     命中同一个 SeaRouteData 对象(SeaRoute 内部按 key 缓存), 所以不可能"画一条、走另一条"。
        //   · 舟行其道: 出航后这条线原地不动, 船沿它一格一格驶过去(见 TickSailing → RoutePointAt)。
        //   · 虚线用自建网格(逐段小方块)而非 LineRenderer: LineRenderer 连点成线断不开,
        //     要做虚线得靠纹理平铺, 而平铺疏密随缩放漂, 不如自己按世界长度切方块来得可控。
        //   · 只画海上那一段, 不画出/进港的登陆腿 —— 那两段在陆地(港建在陆上), 画出来就是黄线压陆。
        //   · 压在黑雾之上、船之下(渲染次序明梯 3500 雾 → 3600 本线 → 3700 船):
        //     黑雾盖不住它(计划航线本就画在海图上), 但船开上来时是船盖着它。见 SeaRoute.shader。
        // =============================================================
        SeaRouteData PreviewRoute(out Vector3 origin, out Vector3 dest)
        {
            origin = RouteOrigin();
            dest = Dest != null ? MapToWorld(Dest) : ExploreWorld;
            if (world == null) return null;
            return SeaRoute.Route(origin.x, -origin.z, dest.x, -dest.z, RouteCacheKey(origin, dest));
        }

        // 这条线的"起点": 航行中固定为起锚点(整条航线定住, 船沿着它走), 泊港=当前港, 抛锚=船位
        Vector3 RouteOrigin()
        {
            if (State == Mode.Sailing) return DepartWorld;
            return VoyOrigin();
        }

        // 逐帧调, 但只有签名变了才真重建 —— 船在航道上走、镜头推拉都不触发
        void TickRouteLine()
        {
            string sig = RouteSig();
            if (sig == _routeSig) return;
            _routeSig = sig;
            RebuildRouteLine();
        }

        // 航线签名: 状态 + 起点(近似到 1 度, 吸收抛锚时的随波轻晃) + 终点
        string RouteSig()
        {
            if (_root == null || !HasSailTarget) return "none";
            var o = RouteOrigin();
            var d = Dest != null ? MapToWorld(Dest) : ExploreWorld;
            return (State == Mode.Sailing ? "S" : State == Mode.Anchored ? "A" : "D")
                + "|" + Mathf.RoundToInt(o.x) + "," + Mathf.RoundToInt(o.z)
                + "|" + (Dest != null ? Dest.Id : "flag")
                + "|" + Mathf.RoundToInt(d.x) + "," + Mathf.RoundToInt(d.z);
        }

        void HideRouteLine()
        {
            if (_routeLine != null && _routeLine.gameObject.activeSelf) _routeLine.gameObject.SetActive(false);
        }

        void EnsureRouteLine()
        {
            if (_routeLine != null || _root == null) return;
            var go = new GameObject("route_line");
            go.transform.SetParent(_root, false);
            _routeMesh = new Mesh { name = "route_line" };
            _routeMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;   // 跨洋长航线顶点数会过 65535
            go.AddComponent<MeshFilter>().sharedMesh = _routeMesh;
            var mr = go.AddComponent<MeshRenderer>();
            var sh = Shader.Find("SEA/Route");
            var mat = sh != null ? new Material(sh) : StdMat(ColRoute);   // 着色器丢了也别让整条线消失
            if (mat.HasProperty("_Color")) mat.color = ColRoute;
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            go.SetActive(false);
            _routeLine = go.transform;
        }

        void RebuildRouteLine()
        {
            if (_root == null || !HasSailTarget) { HideRouteLine(); return; }
            EnsureRouteLine();
            if (_routeLine == null || _routeMesh == null) return;

            Vector3 o, d;
            var rd = PreviewRoute(out o, out d);
            bool havePath = rd != null && rd.Pts.Count >= 2;
            if (!havePath && (o - d).sqrMagnitude < 1e-6f) { HideRouteLine(); return; }

            var verts = new List<Vector3>(512);
            var tris = new List<int>(768);
            const float period = RouteDashLen + RouteGapLen;
            float phase = 0f;   // 周期内相位 —— 跨折线段延续, 拐弯处虚线才是连着的, 不会被每段各自从头切
            // 只画"海上的那一段": Pts[0] 与 Pts[last] 是港口本身(港建在陆上), 所以首末两段是
            //   出港/进港的登陆腿 —— 照原样从头画到尾, 画面上就是一条黄虚线压在海岸上。
            //   改为只画 Pts[1] → Pts[last-1](两端都在吸附出来的海格上), 中间每一段都过
            //   SeaRoute 的弦级校验, 于是"画出来的线不压陆"。见 SeaRoute 类头 ②。
            //   船照旧走整条折线(RoutePointAt 不变): 出港/进港本来就得从港口开出去, 只是不画那两段。
            //   折线只剩两点(直线兜底 = 全程压陆)时一段都不画 —— 宁可没有线, 也不留一条压陆的线。
            // 画的是**加密后**的那份(与船同源), 只取中间段 [1] .. [Count-2] —— 见 BuildRoutePath。
            //   航行中船走的是 _routePath(出航时定下), 而这里重新算的路是"预览"口径;
            //   两者对同一组起终点取值相同(同一份 SeaRoute 缓存 + 同一次加密), 所以画与走仍是一致的。
            //   只有**航行中**才能直接用 _routePath: 那时航线已定住, 船正沿它走。
            //   泊港/抛锚时 _routePath 还是上一条航线的残留, 必须按当前 Dest 重算, 否则画的是旧路。
            var path = (State == Mode.Sailing && _routePath != null && _routePath.Count >= 2)
                     ? _routePath
                     : BuildRoutePath(rd != null ? rd.Pts : null);
            if (path.Count >= 3)
            {
                for (int i = 1; i <= path.Count - 3; i++)
                    DashSegment(verts, tris, path[i], path[i + 1], ref phase, period);
            }
            else
                HideRouteLine();   // 折线只剩两点(直线兜底 = 全程压陆) → 一段都不画; 绝不退成一条必然压陆的直线

            if (verts.Count == 0) { HideRouteLine(); return; }
            _routeMesh.Clear();
            _routeMesh.SetVertices(verts);
            _routeMesh.SetTriangles(tris, 0);
            _routeMesh.RecalculateBounds();
            if (!_routeLine.gameObject.activeSelf) _routeLine.gameObject.SetActive(true);
        }

        // 在一段球面短弧上按虚线相位切出若干小方块。
        //   入参 a/b 是 **map 空间**的两点(已加密到 ≤1° —— 见 SeaGlobe.ArcSubdivide 的弦高推导),
        //   输出顶点直接落在半径 R+RouteY 的球面上。
        //   phase 是"周期内走到哪了", 由调用方跨段带着走, 所以整条折线的虚实节奏是连续的
        //   (而不是每段重新起头, 拐点处冒出一截多余的实线)。
        void DashSegment(List<Vector3> verts, List<int> tris, Vector3 a, Vector3 b, ref float phase, float period)
        {
            Vector3 wa = SeaGlobe.ToWorld(a.x, -a.z);
            Vector3 wb = SeaGlobe.ToWorld(b.x, -b.z);
            // 弧长就是"度" —— R 取 180/π 正是为了这个(DegToWorld == 1), 所以虚线长度不必换算
            float len = SeaGlobe.GreatCircleDegLonLat(a.x, -a.z, b.x, -b.z);
            if (len < 1e-4f) return;
            Vector3 dir = (wb - wa).normalized;          // 弦方向 ≈ 弧方向(≤1° 时差 < 0.5°)
            Vector3 nrm = ((wa + wb) * 0.5f).normalized;  // 段中点的法线
            // 横向半宽: 球上的"(-dz,0,dx)"就是 cross(法线, 航向) —— 自动落在切平面里。
            //   (在 lon0/lat0 处验过: 两者都指向"面朝东时的右手边", 所以沿用原来的绕序仍然朝外。)
            Vector3 n = Vector3.Cross(nrm, dir).normalized * RouteHalfW;
            float cur = 0f;
            int guard = 0;   // 防御性上限: 万一段长/周期算岔了, 也不至于卡死主线程
            while (cur < len - 1e-4f && guard++ < 8192)
            {
                bool onDash = phase < RouteDashLen;
                float step = onDash ? RouteDashLen - phase : period - phase;
                float seg = Mathf.Min(step, len - cur);
                if (onDash)
                {
                    // 段内插值后**重新归一化回 R+RouteY 的球面**: ≤1° 的段上线性插值再归一,
                    //   偏差是 O(Δ²/8) ≈ 4e-5 单位, 远小于 RouteY(0.46), 不必上 slerp。
                    Vector3 p0 = Vector3.Lerp(wa, wb, cur / len).normalized * (SeaGlobe.R + RouteY);
                    Vector3 p1 = Vector3.Lerp(wa, wb, (cur + seg) / len).normalized * (SeaGlobe.R + RouteY);
                    int i0 = verts.Count;
                    verts.Add(p0 - n);
                    verts.Add(p0 + n);
                    verts.Add(p1 + n);
                    verts.Add(p1 - n);
                    tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
                    tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3);
                }
                cur += seg;
                phase += seg;
                if (phase >= period) phase -= period;
            }
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
            AddCurb(_ship);   // 船也会被转到球背面 —— 那时候它同样会从球缘外露出来
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
            RaiseShipAboveRoute(root);
            return root;
        }

        // 计划航线(黄虚线)排在 3600(见 SeaRoute.shader), 船得盖在它上面 → 整船材质提到 3700。
        //   只改"画的次序", 着色器的 ZTest 仍是 Standard 的 LEqual、ZWrite 照旧 ——
        //   该被陆地/港球挡住的照样挡住(那些是不透明, 早就写进深度缓冲了), 深度关系一点没动,
        //   变的只是"谁后画谁盖谁"。代价是船也会画在黑雾(3500)之上, 但船永远待在自己已探明的
        //   那一圈中央(SeaFog 每帧按船位揭图), 所以雾面上根本看不到船 —— 无副作用。
        //   每次重建船模(买船/卖船/读档)都会重跑, 不会漏。
        void RaiseShipAboveRoute(Transform ship)
        {
            if (ship == null) return;
            var done = new HashSet<Material>();     // 同一材质可能挂在多块板上(帆/木料), 去重
            foreach (var r in ship.GetComponentsInChildren<Renderer>(true))
            {
                var m = r.sharedMaterial;
                if (m == null || !done.Add(m)) continue;
                m.renderQueue = ShipRenderQueue;
            }
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
            Vector3 up, north, east;
            SeaGlobe.SurfaceFrame(p.Lon, p.Lat, out up, out north, out east);
            // 泊港时轻晃 + 船头朝向洋流(顺手朝东)。原来的世界 x/z 摆动换成**切平面的东/北**摆动:
            //   球上"东""北"随地点而变, 不能再用固定的世界轴。
            _ship.position = up * (SeaGlobe.R + 0.05f)
                           + east * (Mathf.Sin(t * 0.6f) * 0.12f)
                           + north * (Mathf.Cos(t * 0.7f) * 0.10f);
            // 基准艏向 = 东(原来 Euler(0,0,0) 就是局部 +X 对世界 +X = 东), 再叠 -20° 上下的绕法线偏航
            _ship.rotation = ShipRotationOnGlobe(up, east)
                           * Quaternion.Euler(0f, -20f + Mathf.Sin(t * 0.4f) * 4f, 0f);
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
            Vector3 up, north, east;
            SeaGlobe.SurfaceFrame(_anchor.x, -_anchor.z, out up, out north, out east);
            _ship.position = up * (SeaGlobe.R + 0.03f + Mathf.Sin(t * 0.8f) * 0.015f)
                           + east * (Mathf.Sin(t * 0.55f) * 0.10f)
                           + north * (Mathf.Cos(t * 0.45f) * 0.08f);
            _ship.rotation = ShipRotationOnGlobe(up, east) * Quaternion.Euler(0f, Mathf.Sin(t * 0.3f) * 4f, 0f);
            SetSailsRaised(true, t);
        }

        // =============================================================
        // 相机 & 点选
        // =============================================================
        // 把焦点摆到 (lon,lat) 并让"北"朝上 —— 转球后仍要一眼看出哪边是北, 否则像一颗无字的球
        void LookAtLonLat(double lon, double lat, float dist)
        {
            Vector3 up, north, east;
            SeaGlobe.SurfaceFrame(lon, lat, out up, out north, out east);
            _focus = up * SeaGlobe.R;
            _camUp = north;
            _dist = dist;
        }

        // 右键: 摆正成"北上南下", 并把舰队当前位置送到屏幕正中(即两条中线的交点)。
        //   为什么不直接 LookAtLonLat(舰队经纬, _dist): 那会**瞬移**, 而玩家刚拖过的球面朝向
        //   里带着他好不容易转出来的方位感; 一帧跳过去会让人"跟丢"自己刚才在看哪儿。
        //   这里只登记目标姿态, 由 TickAlign 逐帧转过去。
        void AlignNorthUpOnFleet()
        {
            if (_ship == null) return;
            Vector3 target = SeaGlobe.Up(_ship.position) * SeaGlobe.R;   // 舰队所在球面点(去掉 bob 抬升)
            SeaGlobe.ToLonLat(target, out double lo, out double la);
            Vector3 tu, tnorth, teast;
            SeaGlobe.SurfaceFrame(lo, la, out tu, out tnorth, out teast);
            _alignFrom = Quaternion.LookRotation(-SeaGlobe.Up(_focus), _camUp);
            // 目标姿态的"上"取该点的切向北 → 画面里北在上、南在下
            _alignTo = Quaternion.LookRotation(-tu, tnorth);
            _alignT = 0f; _alignDur = AlignSecs;
        }

        // 摆正过渡: 从头到尾只有一段四元数 slerp, 由它反推 _focus 与 _camUp。
        //   (镜头 forward = −法线 → up = −forward; 相机站位仍由 ApplyCamera 按 _focus+_dist 算)
        void TickAlign(float dt)
        {
            if (_alignT >= _alignDur) return;
            _alignT += dt;
            float s = Mathf.Clamp01(_alignT / _alignDur);
            s = s * s * (3f - 2f * s);                       // smoothstep: 起停都不突兀
            Quaternion q = Quaternion.Slerp(_alignFrom, _alignTo, s);
            Vector3 up = -(q * Vector3.forward);
            _focus = up * SeaGlobe.R;
            _camUp = q * Vector3.up;
        }

        // 球面上"船艏(局部 +X)指向某方向"的姿态。
        //   平图时代是 LookRotation(dir, up) * Euler(0,-90,0) —— 那套依赖**全局的 up**, 球上没有;
        //   这里显式拼三元组: 局部 +Y = 地表法线, 局部 +X = 艏向(先把 heading 投到切平面)。
        //   Unity 里 (right, up, forward) 满足 forward = cross(right, up), 所以取 forward = cross(艏向, 法线),
        //   LookRotation(forward, up) 反推出的 right 正好就是艏向。
        static Quaternion ShipRotationOnGlobe(Vector3 surfacePoint, Vector3 heading)
        {
            Vector3 up = surfacePoint.normalized;
            Vector3 fwd = heading - up * Vector3.Dot(heading, up);   // 投到切平面
            if (fwd.sqrMagnitude < 1e-10f) return Quaternion.LookRotation(Vector3.Cross(up, Vector3.right), up);
            fwd.Normalize();
            return Quaternion.LookRotation(Vector3.Cross(fwd, up), up);
        }

        // 世界总览(地球仪镜头): 整颗地球一次看全
        void FrameMap()
        {
            // 焦点取**图幅中心**(lon -110..140 / lat -40..70 → 15,15)而不是 (0,0):
            //   让有海图的那半球正对镜头, 而不是让大西洋中心对着你、把欧亚大陆转到侧后。
            LookAtLonLat((SeaMapGen.Lon0 + SeaMapGen.Lon1) * 0.5,
                         (SeaMapGen.Lat0 + SeaMapGen.Lat1) * 0.5, 78f);
        }

        // 开局/回港近景: 相机贴近船所在的那片海, 港球大、一点就能选目标(别一上来甩一颗看不清船的地球)
        void FrameHome()
        {
            var p = Current ?? world.FindPort(homePortId);
            if (p != null) LookAtLonLat(p.Lon, p.Lat, 28f);
            else { _dist = 28f; }
        }
        public void ViewWorld() { FrameMap(); }   // HUD"世界全景"
        public void ViewHome() { FrameHome(); }   // HUD"回港/就近"

        // ---- 闸门专用(只有 -seaShot 截图轮会调; 玩家路径碰不到) ----
        // 先把镜头**故意转乱**: 换一段焦点经度 + 压一圈滚转。
        //   为什么非要转乱: 开局本来就是北上南下、焦点也就在船那儿, 于是"右键摆正"前后两张图
        //   会长得一模一样 —— 那样截图证明不了任何事, 只能证明"没崩"。要先造出一个错的状态,
        //   再证明摆正把它修回来了。
        public void QaScrambleCam()
        {
            // 先绕该点法线滚 55°, 再绕世界 Y 转 38°(把焦点挪到另一段经度上)
            Quaternion q = Quaternion.AngleAxis(38f, Vector3.up) * Quaternion.AngleAxis(55f, SeaGlobe.Up(_focus));
            _focus = (q * _focus).normalized * SeaGlobe.R;
            _camUp = q * _camUp;
            _alignT = _alignDur;   // 上一次摆正若还挂着, 先掐掉, 免得它把刚转乱的状态又拧回去
        }
        public void QaAlignNorthUp() { AlignNorthUpOnFleet(); }
        // 舰队此刻挂在球面的哪儿? 给"右键摆正该把舰队送到画面正中"当**量尺**用 ——
        //   截图轮把它投影到屏幕上, 直接量出偏了几个像素(见 SeaShot.LogFleetCentering)。
        //   为什么非量不可: "焦点必落在视轴正中, 而焦点就是舰队所在的球面点"是一段**推理**;
        //   推理会错, 像素不会。
        public Vector3 QaFleetWorldPos() { return _ship != null ? _ship.position : Vector3.zero; }

        // 把舰队填到 n 艘(每船再分点货), 只为让舰队面板的版面有像素证据。
        //   为什么非得开这么个口子: 面板要证明的是"8~10 艘也摆得下、不压合计", 而 FleetTuning.fleetMaxShips
        //   现在是 5 —— 走 BuyShip 那条真路径最多到 5 艘, 6~10 艘的版面**根本走不到**,
        //   那样这版改动就只剩"我算过了"这种推理, 而这块的推理上一版就错过一次(perRow=2 固定,
        //   三艘船即溢出 —— 算的时候没人把 6 行参数放进 133 高的卡里量一遍)。
        //   这里只跳过"船队上限"这一道闸: 造船仍走 FleetBuilder.BuildShip, 装货仍走 FleetOps.AddCargo
        //   —— 与买船/装货同一份数据、同一条代码, 只是不查上限、不扣钱。
        public void QaFillFleetTo(int n, int cargoEach)
        {
            if (fleet == null || cat == null || cat.ShipOrder.Count == 0) return;
            while (fleet.Ships.Count < n)
            {
                string id = cat.ShipOrder[fleet.Ships.Count % cat.ShipOrder.Count];
                FleetBuilder.BuildShip(fleet, id, 0, flagship: false);
            }
            if (cargoEach > 0 && world != null && world.Goods != null)
            {
                int g = 0;
                foreach (var good in world.Goods)
                {
                    if (g >= 4) break;            // 4 个货种: 正好逼出"货单折行"那种最长的参数块
                    FleetOps.AddCargo(fleet, good.Id, cargoEach);
                    g++;
                }
            }
            RebuildFleetVisual();
            PushLog("[闸门] 舰队填到 " + fleet.Ships.Count + " 艘(每船分货 " + cargoEach + " 件/种, 至多 4 种)");
        }

        // [闸门] 黑雾探明状况一行 —— "开局只亮出发点周围一小圈"这条, 要么拿数字说话, 要么就是嘴上说说。
        //   为什么数**港**而不数格子: 格子数玩家看不见; 能去哪儿做买卖才是这一圈够不够大的判据。
        //   期望(母港塞维利亚, FogStartR=12°): seville(0°) + lisbon(2.8°) + bordeaux(8.4°) 三个,
        //   而 genoa(13.2°) 之外一个都不该亮 —— 那正是"再远就得自己驶过去"的证据。
        public string QaFogState()
        {
            if (_fog == null || world == null || world.Ports == null) return "fog=null";
            int n = 0; string names = "";
            foreach (var p in world.Ports)
                if (_fog.Revealed(p.Lon, p.Lat)) { n++; if (n <= 8) names += p.Name + " "; }
            return "雾已探明港 " + n + "/" + world.Ports.Count + " [" + names.TrimEnd() + "]";
        }

        public bool PointerOverUi() // SeaHud 设置
        {
            return UnityEngine.EventSystems.EventSystem.current != null
                && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
        }

        void HandleCamAndPick()
        {
            if (_cam == null) return;
            TickAlign(Time.deltaTime);   // 摆正过渡: 摆机位要在下面 ApplyCamera 之前算完
            // 原则: 弹出的对话框/信息就是最高层 —— 有浮层(行情/舰队/确认/出航检查)开着,
            // 底下海图的输入(滚轮缩放 / 拖移 / 点选)全部暂停, 交还给最上层 UI;
            // 平时指针悬在任一 UI(按钮/面板/日志)上, 同样只让那层响应, 别让地图跟着滚轮缩放。
            bool overUi = PointerOverUi();
            // [闸门] 左键按下这一瞬: 不管随后走哪条路(早退/收下/丢弃), 先把现场钉下来。
            //   放在 AnyTopOpen 早退**之前** —— 否则闸门①(有浮层开着把这一下吃掉)恰恰记不到。
            if (Input.GetMouseButtonDown(0))
                QaClickNote(_hud != null && _hud.AnyTopOpen
                        ? "左键按下 → 被闸门①吃掉(有浮层开着, 整张海图输入暂停)"
                        : overUi ? "左键按下 → 被闸门②吃掉(指针压在 UI 上, 这一下从未开始)"
                        : "左键按下 → 收下, 开始计时",
                    Input.mousePosition);
            if (_hud != null && _hud.AnyTopOpen)
            {
                _downHeld = false; _dragPan = false; _midHeld = false;
                ApplyCamera(State == Mode.Sailing ? _ship.position : _focus);
                return;
            }

            // 滚轮缩放(指针在海图上才缩放; 指针在 UI 上 = 滚轮只滚动 UI 那层)
            //   下限 6: 黑雾球壳在 R+2.8, 相机沿法线离地 6 时仍在壳外, 推再近雾也不会整片消失。
            //     此时球缘角 asin(R/(R+6)) ≈ 65°, 远大于半 fov 30° → 画面里看不到球缘, 就是一张局部海图。
            //   上限 160: 78 是"整颗地球"的取景, 再拉远只是把地球看小一圈(α_limb 25°→13°), 无害。
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (!overUi && Mathf.Abs(wheel) > 0.0001f)
                _dist = Mathf.Clamp(_dist * (1f - wheel * 0.48f), 6f, 160f);   // 每滚一格缩放倍率=原先×4

            // 右键(单击) = 把地球摆正: 北上南下 + 舰队居中。不吃拖拽 —— 右键按下即触发, 不用等松手。
            //   指针在海图上就认(不要求正好压在球面上): 右键本来就是"复位视角", 卡在球缘外反而不灵。
            if (Input.GetMouseButtonDown(1) && !overUi) AlignNorthUpOnFleet();

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
                    if (!_dragPan && Vector2.Distance(Input.mousePosition, _downScreen) > ClickSlopPx())
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
                    // 闸门③④在**松手这一刻**才定: 按下时收下了, 未必走得到点选。
                    //   PointerOverUi() 原来在这里还要再问一次, 现在问一次存下来两用(判定 + 记录),
                    //   不多一次射线 —— 而"松手时指针挪到了 UI 上"正是玩家最容易踩、也最难自己意识到的一种。
                    bool overUp = PointerOverUi();
                    QaClickNote(wasPan ? "左键松手 → 被闸门④吃掉(按住期间动超阈值, 判成拖球)"
                        : State == Mode.Sailing ? "左键松手 → 航行中不点选(要抛锚/靠港才能选目标)"
                        : overUp ? "左键松手 → 被闸门③吃掉(松手时指针压在 UI 上了)"
                        : "左键松手 → 走 PickAt(下面这一行必然有声)",
                        Input.mousePosition);
                    // 停着(泊港/抛锚)点选: 港球=去那座城; 海面=插红旗自由探索
                    if (!wasPan && State != Mode.Sailing && !overUp) PickAt(Input.mousePosition);
                }
            }

            // 相机对焦: 海上跟船
            ApplyCamera(State == Mode.Sailing ? _ship.position : _focus);
        }

        // 按下瞬间只记一个光标位置: 转球用**增量**法, 不需要"按下时的锚点"
        void GrabAt(Vector2 screen)
        {
            _panPrev = screen;
            // 玩家自己上手转球了 → 摆正过渡立刻让位。否则他一边拖、球一边往"北上南下"拧, 抢手。
            _alignT = _alignDur;
        }

        // 1:1 跟手(增量式, 防抖):
        //   每帧都用【同一帧的相机姿态】分别采样"上一光标位置"与"当前光标位置"的**球面命中点**,
        //   以两者为极点算出那段旋转, 把整颗球按它反过来转。
        //   不做"绝对锚点回算"(旧锚会随相机姿态每帧变化形成 ± 反馈环 → 静止时球来回振 = 抖动)。
        void PanGrab()
        {
            Vector2 now = Input.mousePosition;
            Vector3 a, b;
            bool ha = SphereAtScreen(_panPrev, out a);
            bool hb = SphereAtScreen(now, out b);
            _panPrev = now;
            if (!ha || !hb) return;   // 拖到球外的虚空(或推到球缘外)那一帧: 保持不动, 拖回球面再续
            Vector3 na = a.normalized, nb = b.normalized;
            if (Vector3.Dot(na, nb) > 0.999999f) return;   // 几乎没动(且避免下面的 cross 退化)
            // 绕 (na×nb) 转 **−θ**:
            //   光标右移 ⇒ 命中点 b 落在 a 之东 ⇒ 若正向转会推着焦点东移, 画面内容整体**左**移 = 与光标反向。
            //   要跟手就得反向, 所以取负。推过一遍: na=(0,0,-1) 时 cross(na,nb) 指 −Y,
            //   正向转把焦点送到 (sinθ,0,−cosθ) 即东移 —— 确认是反的。
            Quaternion q = Quaternion.AngleAxis(-Vector3.Angle(na, nb), Vector3.Cross(na, nb).normalized);
            _focus = (q * _focus).normalized * SeaGlobe.R;
            _camUp = q * _camUp;
        }

        // 屏幕像素 → 半径 R 球面上的点(拖拽抓点/插旗用)。
        //   和原来那个"射线 × y=0 平面"的版本有个要命的差别: 球上**点不到就真的点不到**,
        //   所以这里返回 bool —— 调用方必须自己处理未命中, 绝不能像老代码那样 return _target 兜底
        //   (那会把旗插到镜头焦点上, 一个玩家根本没指过的地方)。
        bool SphereAtScreen(Vector2 screen, out Vector3 hit)
        {
            hit = Vector3.zero;
            if (_cam == null) return false;
            var ray = _cam.ScreenPointToRay(screen);
            return SeaGlobe.RaySphere(ray.origin, ray.direction, out hit);
        }

        // 点选(停着时): 落在任一座城的"圈内"就算点中那座城(画多大就点多大) → 设为目标;
        //   没落进任何圈、且点在海上 = 插红旗自由探索。
        void PickAt(Vector2 screen)
        {
            if (State != Mode.Docked && State != Mode.Anchored) return;
            var p = PortAtScreen(screen);
            if (p != null)
            {
                // 泊港时点"自己正停着的那颗"没意义; 抛锚在外时当前港也算远处一座城, 可以回航
                if (State == Mode.Docked && p == Current) { Banner = "已在 " + p.Name + " 港"; return; }
                SetPortTarget(p);
                return;
            }
            TryPlantSeaFlag(screen);
        }

        // =============================================================
        // [闸门] 「进游戏后第一次点目的地城市没反应」取证 —— 左键点击路径记录仪
        //
        //   症状是"点了什么也没发生"。而 PickAt 是**有去无回必有声**的: 命中港 → 设目标 + 航线;
        //   落在海上 → 插红旗; 落在陆地/图幅外/球外 → 各有一句 Banner。所以"完全没有反应"
        //   只有一个含义 —— 这一下**根本没走到 PickAt**, 被四道闸门之一吃了:
        //     ① _hud.AnyTopOpen    : 有浮层开着(行情/舰队/确认…)→ 整张海图输入暂停, 按下就被丢
        //     ② overUi(按下那一刻) : 指针压在 UI 上 → 连 _downHeld 都不置, 这一下从未开始
        //     ③ overUi(松手那一刻) : 按下时在海图上、松手时压到了 UI → 已按下的这一下被丢
        //     ④ wasPan             : 按住期间位移超过 ClickSlopPx → 判成"拖球", 不点选
        //   要命的是这四道闸门看的全是**那一瞬**的状态(指针在哪儿、有没有浮层)。事后任何时刻
        //   再去问, 状态早变了 —— 玩家手一松、指针一挪, 现场就没了。所以记录仪必须挂在
        //   HandleCamAndPick 里, 和闸门同一行代码、同一帧问, 而不是挂在截图闸门里事后采样。
        //
        //   另有一个反向结论也靠它区分: 若日志里**一条"左键按下"都没有**, 说明点击连
        //   HandleCamAndPick 都没进 —— 那问题就不在这四道闸门, 而在输入投递层(窗口焦点 / 鼠标没送到),
        //   是另一条完全不同的线索。这两种情况非此即彼, 记一次就能分开。
        //
        //   开窗 = 启动时带 `-seaClickLog`(见 ArmClickLogFromArgs), 窗口 120 秒, 封顶 40 条。
        //   **结论已出, 所以默认不开**: 取证结果是"四道闸门全清白、点击确实走到了 PickAt",
        //   真因是主线程在算寻路栅格(见 SeaRoute.PumpBuild)。留着它是因为这套记录仪对
        //   "点了没反应"这一类症状是通用的第一刀 —— 下次再犯, 加个开关就有现场, 不必重写。
        //   但不能常态开: 它每次左键都要做一次射线 + 球面求交, 还要往日志里写十几行。
        public static bool ClickLogRequested =>
            System.Array.IndexOf(Environment.GetCommandLineArgs(), "-seaClickLog") >= 0;

        public void ArmClickLogFromArgs()
        {
            if (ClickLogRequested) QaArmClickLog(120f);
        }
        // =============================================================
        public void QaArmClickLog(float seconds)
        {
            _qaClickUntil = Time.realtimeSinceStartup + seconds;
            _qaClickNotes = 0;
            Debug.Log("[SEA] 点击路径记录仪: 开窗 " + seconds.ToString("F0")
                + "s —— 现在请点一下海图上要去的城市(点一下就好, 然后停手看日志)。");
        }
        bool QaClickLogOn => _qaClickUntil > 0f && Time.realtimeSinceStartup <= _qaClickUntil;

        // [闸门] 心跳: 每秒一行, 证明"Update 真在跑 / Unity 真看得见鼠标 / Unity 真认为自己在前台"。
        //   为什么非有它不可: 没有它,"点击没反应"有两种完全不同的解释 ——
        //     ① 点击到了, 被四道闸门之一吃了(改闸门);
        //     ② 游戏根本没在接收输入(窗口失焦 → Unity 默认不跑 Update 不收鼠标; 改的是窗口焦点/输入层)。
        //   这两者的修法南辕北辙, 而日志里"没有点击记录"这一条**同时符合两者** —— 心跳才能把②钉死。
        //   顺带一提: `Application.isFocused` 为假时, Unity 默认连 Update 都不调 —— 那时日志会直接
        //   **整段停住**(心跳也不再出现), 这本身就是"窗口没在前台"的铁证。
        // [闸门] 卡顿看门狗: 任何一帧超过 80ms 就记一行(附这一帧在干什么)。
        //   为什么非要有它: 上面那些计时只覆盖**我猜到的**地方(寻路/建线)。而"第一次点城会卡"
        //   完全可能是别处 —— 最典型的是**首次渲染某个 shader 的变体编译**(那是渲染线程的活,
        //   C# 侧一根计时器都量不到, 却在主线程上卡住整个游戏)。看门狗按帧计时, 不挑对象,
        //   凡是卡住的帧它都会记, 于是"卡在哪儿"由数据说话, 而不是由我的猜测决定。
        //   一格量的是"上一帧这个点到这一帧这个点"之间的墙钟时间, 也就是
        //   【上一帧 Update 的后半 + 上一帧渲染 + 这一帧 Update 的前半】。所以下面既报帧号区间,
        //   也报"寻路上次跑在第几帧" —— 帧号对得上就是寻路的账, 对不上就另有其人(渲染线程的嫌疑最大)。
        float _qaLastFrameAt;
        int _qaLastFrameNo;
        int _qaSlowFrames;
        void QaSlowFrameWatch()
        {
            float now = Time.realtimeSinceStartup;
            int fr = Time.frameCount;
            if (_qaLastFrameAt > 0f)
            {
                float ms = (now - _qaLastFrameAt) * 1000f;
                if (ms >= 80f && _qaSlowFrames < 60)
                {
                    _qaSlowFrames++;
                    Debug.Log("[SEA] 卡顿#" + _qaSlowFrames + " 帧" + _qaLastFrameNo + "→" + fr
                        + " 历时 " + ms.ToString("F0") + "ms"
                        + " | 状态=" + State + " 距地=" + _dist.ToString("F0")
                        + " 有目标=" + (Dest != null ? Dest.Id : "-")
                        + " | 栅格构建=" + SeaRoute.MsEnsure.ToString("F0") + "ms@帧" + SeaRoute.EnsureFrame
                        + " A*=" + SeaRoute.MsAstar.ToString("F0") + "ms@帧" + SeaRoute.AstarFrame
                        + " 展开" + SeaRoute.AstarExpanded + " 缓存=" + SeaRoute.CacheCount);
                }
            }
            _qaLastFrameAt = now;
            _qaLastFrameNo = fr;
        }

        float _qaBeatAt;
        void QaBeat()
        {
            if (!QaClickLogOn || Time.realtimeSinceStartup < _qaBeatAt) return;
            _qaBeatAt = Time.realtimeSinceStartup + 1f;
            Debug.Log("[SEA] 心跳 t=" + Time.realtimeSinceStartup.ToString("F0")
                + " 聚焦=" + Application.isFocused
                + " 鼠标=" + ((Vector2)Input.mousePosition).ToString("F0")
                + " 帧=" + Time.frameCount
                + " 距地=" + _dist.ToString("F0"));
        }

        // 把"这一刻"的四道闸门 + 这一点的点选结果一次问全。
        //   问 PortAtScreen 是**纯函数**调用, 不会改任何状态 —— 所以"记一笔"本身对玩法零影响。
        void QaClickNote(string what, Vector2 pos)
        {
            if (!QaClickLogOn || _qaClickNotes >= 40) return;
            _qaClickNotes++;
            Vector3 g;
            bool onGlobe = SphereAtScreen(pos, out g);
            var p = PortAtScreen(pos);
            Debug.Log("[SEA] 点击路径#" + _qaClickNotes + " " + what
                + " | 指针=" + pos.ToString("F0")
                + " 距地=" + _dist.ToString("F0")
                + " 状态=" + State
                + " | AnyTopOpen=" + (_hud != null && _hud.AnyTopOpen)
                + " 指针在UI上=" + PointerOverUi()
                + " 指针下=" + SeaHud.TopUiUnderPointer(pos)
                + " | _downHeld=" + _downHeld + " _dragPan=" + _dragPan
                + " | 这一点: 命中港=" + (p != null ? p.Name + "(" + p.Area.Name + ")" : "无")
                + " 球面命中=" + onGlobe);
        }

        // 屏幕点 → 命中哪座城(没落进任何圈就 null)。命中半径就是圈的绘制半径 MarkOf(p).RingR, 画多大点多大。
        //   球上有两条判据, 取先命中的那条:
        //   ① **弧长**: 光标在球面的落点与港心的大圆距离 ≤ RingR(度)。正对镜头时它与玩家看到的那圈严格同源
        //      —— 圈是画在切平面上的圆弧, 港心就在切点, 两者角半径一致。
        //   ② **屏幕**: 港心投影到屏幕后光标在其 RingR 对应的像素半径内。为什么需要它:
        //      球缘处地表几乎是侧对着镜头的, 1 像素能横跨好几度, ①在球缘退化到亚像素 → 贴边的港根本点不到。
        //      ②只对**朝向镜头**的港生效, 否则背半球那些港会透过地球被点到。
        Port PortAtScreen(Vector2 screen)
        {
            if (_cam == null || world == null) return null;
            Vector3 g;
            bool onGlobe = SphereAtScreen(screen, out g);
            double clon = 0.0, clat = 0.0;
            if (onGlobe) SeaGlobe.ToLonLat(g, out clon, out clat);
            var camTr = _cam.transform;
            Port best = null;
            float bestD = float.MaxValue;
            foreach (var p in world.Ports)
            {
                float ringR = MarkOf(p).RingR;
                float score = float.MaxValue;
                if (onGlobe)
                {
                    float arc = SeaGlobe.GreatCircleDegLonLat(clon, clat, p.Lon, p.Lat);
                    if (arc <= ringR) score = arc;
                }
                if (score == float.MaxValue)
                {
                    var pw = SeaGlobe.ToWorld(p.Lon, p.Lat);
                    Vector3 toCam = camTr.position - pw;
                    if (Vector3.Dot(pw.normalized, toCam) > 0f)   // 只认朝镜头的港
                    {
                        Vector3 sp = _cam.WorldToScreenPoint(pw);
                        if (sp.z > 0f)
                        {
                            float depth = toCam.magnitude;
                            float pxPerDeg = (Screen.height * 0.5f)
                                           / (Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * depth);
                            float dpx = Vector2.Distance(new Vector2(sp.x, sp.y), screen);
                            // 屏幕空间**下限** PickFloorPx(): "画多大就点多大"在拉远时会退化到几个像素
                            //   —— 78 处约 16px/°, 0.9° 的圈只有 14px 半径; 拉到 160 只剩 7px, 小港(RingMin
                            //   0.28°)更是 4~5px, 那已经不是在点, 是在穿针。用户报的"要点2次"就是这个。
                            //   加了下限后, 圈照旧可点(① 命中时 score 是"度" ≤ ringR, 永远压过这里的下限命中),
                            //   而"圈外一指宽"也算点中它 —— 谁离光标近谁赢, 密港区挑最近的, 正合玩家本意。
                            if (dpx <= Mathf.Max(ringR * pxPerDeg, PickFloorPx()) && pxPerDeg > 1f)
                                score = dpx / pxPerDeg;      // 折回"度", 好和 ① 一起比谁更近
                        }
                    }
                }
                // 两圈不相交 ⇒ 本来就只会命中一座; 距离比较是撞到 RingMin 下限的极致密港时的兜底(取最近的)
                if (score < bestD) { bestD = score; best = p; }
            }
            return best;
        }

        // =============================================================
        // 点选手感两个数(都按屏幕高度换算, 不写死像素)
        //   为什么不能写死: 这个项目从 1600×900 跑到了 2560×1440, 同一个"7px"在两种屏上是完全不同的
        //   手感 —— 1440 上 7px 只有屏宽的 0.27%, 手一抖就过阈值。凡是有"像素"味道的阈值都该跟着
        //   屏幕高度走, 这样换分辨率不用重新调。
        // =============================================================
        // 拖动阈值: 按下后位移超过它才算"拖海图", 没超过的松手 = 点选。
        //   原来写死 7px —— 玩家看着城市点下去、手抖几个像素, 这一下就被当成拖动**静默吞掉**
        //   (拖了 8px 球几乎没动, 也没有任何提示), 于是"得再点一次"。按屏高 1% 取, 1440 → 14px。
        float ClickSlopPx() { return Mathf.Max(6f, Screen.height * 0.010f); }
        // 可点半径下限(屏幕像素): 圈的像素半径小于它时按它算。见 PortAtScreen ② 的注释。
        float _qaPickFloor = -1f;    // [闸门] ≥0 时强制用这个下限(0 = 复现旧行为), <0 = 用默认
        float PickFloorPx()
        {
            return _qaPickFloor >= 0f ? _qaPickFloor : Mathf.Max(10f, Screen.height * 0.014f);   // 1440 → 20px
        }

        // [闸门] 「点城不太灵敏」的取证: 把"这座城此刻到底可点多大"量出来(像素)。
        //   为什么量得准: PortAtScreen 是**纯函数**(屏幕点 → 城), 不必模拟鼠标 —— 从港心的屏幕位置
        //   往右/往上一个像素一个像素地试, 试到不再命中它, 那个像素数就是它的可点半径。玩家感受到的
        //   就是这两个数。
        //   为什么同一个函数里报**两遍**(旧口径 0 下限 / 新口径): 两次用的是同一次取景、同一座城,
        //   只换那一个下限数 —— 这才是"改前改后"的对照, 而不是拿今天量到的数与记忆里的数比。
        public string QaPickProbe()
        {
            if (_cam == null || world == null) return "cam/world null";
            Port pick = null; float bestd = float.MaxValue; Vector2 sp0 = Vector2.zero;
            var c = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            foreach (var p in world.Ports)
            {
                Vector3 pw = SeaGlobe.ToWorld(p.Lon, p.Lat);
                if (Vector3.Dot(pw.normalized, _cam.transform.position - pw) <= 0f) continue;
                Vector3 sp = _cam.WorldToScreenPoint(pw);
                if (sp.z <= 0f) continue;
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), c);
                if (d < bestd) { bestd = d; pick = p; sp0 = new Vector2(sp.x, sp.y); }
            }
            if (pick == null) return "屏幕里没有朝镜头的城";

            float old = _qaPickFloor;
            _qaPickFloor = 0f;                       // 旧口径: 画多大点多大
            int oldR = SweepHit(pick, sp0);
            _qaPickFloor = -1f;                      // 新口径: 带屏幕下限
            int newR = SweepHit(pick, sp0);
            _qaPickFloor = old;

            float depth = (_cam.transform.position - SeaGlobe.ToWorld(pick.Lon, pick.Lat)).magnitude;
            float pxPerDeg = (Screen.height * 0.5f)
                / (Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * depth);
            return "距地=" + _dist.ToString("F0") + " 状态=" + State
                + " | 离屏心最近的城 " + pick.Name + "(尺寸" + pick.Size + ")"
                + " 圈半径=" + MarkOf(pick).RingR.ToString("F2") + "°"
                + " 该处=" + pxPerDeg.ToString("F1") + "px/°"
                + " | 可点半径: 旧 " + oldR + "px → 新 " + newR + "px"
                + " | 拖动阈值 " + ClickSlopPx().ToString("F0") + "px(原 7)";
        }

        // [闸门] 逐城自检: "屏幕点在城心 → 选中该城" 对**每一座**朝镜头的城都成立吗?
        //   为什么非要逐城问, 而不是只问离屏心最近的那一座(那是上面 QaPickProbe 干的):
        //     玩家嘴里的"点城不灵"里的"城"是**他自己挑的那座**, 不是屏心那座 —— 只测屏心,
        //     恰恰漏掉球缘(地表侧对镜头, 1 像素横跨好几度)和密港区(可点圈互相重叠)这些真正难点的位置。
        //   这也是无头环境里唯一能测"点选"的办法: PortAtScreen 是**纯函数**(屏幕点 → 城), 不必有鼠标。
        //     而"点击被浮层吃掉"那一类不归它管 —— 那要靠 QaClickNote 在真手上点的那一刻记, 两者互补。
        //   三种失败各有各的病: 点不中 = 半径太小/被剔除; 选中别家 = 邻城把点抢走了(密港区)。
        public string QaPortSelfPickAudit()
        {
            if (_cam == null || world == null) return "cam/world null";
            int facing = 0, ok = 0, dead = 0, wrong = 0;
            float minR = float.MaxValue; string minName = "-";
            var sb = new System.Text.StringBuilder();
            foreach (var p in world.Ports)
            {
                Vector3 pw = SeaGlobe.ToWorld(p.Lon, p.Lat);
                if (Vector3.Dot(pw.normalized, _cam.transform.position - pw) <= 0f) continue;   // 背半球
                Vector3 sp = _cam.WorldToScreenPoint(pw);
                if (sp.z <= 0f) continue;
                var v = new Vector2(sp.x, sp.y);
                if (v.x < 0f || v.x >= Screen.width || v.y < 0f || v.y >= Screen.height) continue;   // 出画
                facing++;
                var got = PortAtScreen(v);
                if (got == p) ok++;
                else if (got == null) { dead++; if (sb.Length < 500) sb.Append(p.Name).Append("点不中 "); }
                else { wrong++; if (sb.Length < 500) sb.Append(p.Name).Append("→").Append(got.Name).Append(' '); }
                int r = SweepHit(p, v);   // 往右试到不再命中它: 玩家实际感受到的可点半径(像素)
                if (r < minR) { minR = r; minName = p.Name; }
            }
            return "距地=" + _dist.ToString("F0") + " 画内且朝镜头的城 " + facing
                + " | 点城心选中自己 " + ok + " / 点不中 " + dead + " / 选中别家 " + wrong
                + " | 最窄可点半径 " + minR + "px(" + minName + ")"
                + (sb.Length > 0 ? " | " + sb.ToString().TrimEnd() : "");
        }

        // 从港心的屏幕点往右一像素一像素试, 返回"最后还命中它"的那一格(横向半径)。
        int SweepHit(Port p, Vector2 from)
        {
            int r = 0;
            for (int i = 1; i <= 200; i++)
            {
                if (PortAtScreen(from + new Vector2(i, 0f)) == p) r = i; else break;
            }
            return r;
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
        //   平图时代超出图幅是**钳到边界**的(那时可见的海面比图幅还大)。球上不再钳: 整颗球都能点,
        //   钳一下会让玩家点南太平洋却看见旗子吸到非洲边上去。改成明说"那一片还没有海图", 不装作能去。
        void TryPlantSeaFlag(Vector2 screen)
        {
            Vector3 g;
            if (!SphereAtScreen(screen, out g)) { Banner = "那儿是球外的虚空 —— 点一处海面才能插旗。"; return; }
            double lon, lat;
            SeaGlobe.ToLonLat(g, out lon, out lat);
            if (lon < SeaMapGen.Lon0 || lon > SeaMapGen.Lon1 || lat < SeaMapGen.Lat0 || lat > SeaMapGen.Lat1)
            { Banner = "那一片还没有海图 —— 在图幅之内点一处海面才能插旗。"; return; }
            if (SeaMapGen.LandAt(lon, lat)) { Banner = "那是陆地 —— 点一处海面才能插旗驶去。"; return; }
            Dest = null;
            ExploreWorld = new Vector3((float)lon, 0f, (float)-lat);
            ExploreSet = true;
            ShowExploreFlag(ExploreWorld);
            PushLog("🚩 海面插旗: 船将驶向这处(不是港口, 到点抛锚)。点别处海面可挪旗; 点发光圆球则改去那座城。");
        }

        // 地球仪镜头: 相机站在焦点**正上方、沿球面法线** _dist 处, 朝球心看, 上方向取 _camUp。
        //   "转球"= 挪焦点(PanGrab 转 _focus); "远近"= _dist。焦点永远精确落在半径 R 的球面上。
        void ApplyCamera(Vector3 focus)
        {
            if (_cam == null) return;
            // 归一: 拖拽是四元数连乘, 连乘上百次后 _focus 的模长会漂(还带着切向残余)。
            //   拉到半径 R 是一次干净的复位 —— 少了它, 球面点会随拖拽慢慢"浮起来"或"缩进去"。
            if (focus.sqrMagnitude < 1e-6f) focus = _focus;
            _focus = focus.normalized * SeaGlobe.R;
            Vector3 up = SeaGlobe.Up(_focus);
            // 持久 _camUp 投影回切平面: 它已被拖拽四元数带着转过, 这里只需去掉沿法线的分量。
            //   投影后退化(拖到极点附近, up 与 _camUp 几乎共线)才回退到"切向北"。
            Vector3 u = _camUp - up * Vector3.Dot(_camUp, up);
            if (u.sqrMagnitude < 1e-8f)
            {
                double lo, la;
                SeaGlobe.ToLonLat(_focus, out lo, out la);
                Vector3 nu, nn, ne;
                SeaGlobe.SurfaceFrame(lo, la, out nu, out nn, out ne);
                u = nn;
            }
            _camUp = u.normalized;
            _cam.transform.position = _focus + up * _dist;
            _cam.transform.rotation = Quaternion.LookRotation(-up, _camUp);
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
            if (State == Mode.Docked && Current != null) return MapToWorld(Current);
            if (_ship == null) return Vector3.zero;
            // 船位活在**球面**上, 模拟层只认经纬度 → 这里反推一次。
            //   (平图时代直接读 position.x/z 就行, 因为那时 x=经度 z=-纬度; 现在不再等价。)
            SeaGlobe.ToLonLat(_ship.position, out double lon, out double lat);
            return new Vector3((float)lon, 0f, (float)-lat);
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
                Vector3 to = Dest != null ? MapToWorld(Dest) : ExploreWorld;
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
            if (toPort) { var dw = MapToWorld(Dest); DestWorld = new Vector3(dw.x, 0f, dw.z); }
            else DestWorld = ExploreWorld;                        // 探索目标: 海图插旗点(到点抛锚)
            _route = SeaRoute.Route(DepartWorld.x, -DepartWorld.z, DestWorld.x, -DestWorld.z,
                                    RouteCacheKey(DepartWorld, DestWorld));   // 沿海/绕陆海路(船不走陆地)
            _routePath = BuildRoutePath(_route != null ? _route.Pts : null);   // 加密一次, 船与虚线共用
            PlannedDays = DaysFor(DepartWorld, DestWorld);
            _departAreaId = Current != null ? Current.Area.Id : _departAreaId;
            _voyageT = 0f;
            DaysDone = 0;
            SailProgress01 = 0f;
            State = Mode.Sailing;
            // 起锚瞬间把船摆正: 船艏(局部 +X)对准第一段航向, 平滑转向由此起步。
            //   方向必须在**球面空间**里取(两点球面位置相减 = 切向), 不能拿 map 空间的首段差 ——
            //   后者是经纬度差, 纬度越高越偏向"东偏", 船会以明显歪掉的角度起航。
            Vector3 d0 = Vector3.zero;
            if (_routePath != null && _routePath.Count >= 2)
            {
                Vector3 pa = SeaGlobe.ToWorld(_routePath[0].x, -_routePath[0].z);
                Vector3 pb = SeaGlobe.ToWorld(_routePath[1].x, -_routePath[1].z);
                d0 = pb - pa;
            }
            if (d0.sqrMagnitude < 1e-9f)
            {
                var w0 = VoyOrigin(); var w1 = DestWorld;   // 兜底: 没有海路就用起终点
                d0 = SeaGlobe.ToWorld(w1.x, -w1.z) - SeaGlobe.ToWorld(w0.x, -w0.z);
            }
            if (_routePath != null && _routePath.Count >= 1 && _ship != null)
            {
                Vector3 p0 = SeaGlobe.ToWorld(_routePath[0].x, -_routePath[0].z).normalized * (SeaGlobe.R + 0.05f);
                _ship.position = p0;
                if (d0.sqrMagnitude > 1e-9f) _ship.rotation = ShipRotationOnGlobe(p0, d0);
            }
            // 相机跟船, 但初始焦点摆在**航程中点**看: 一步就把出发地与目的地之间的海都框进来。
            //   中点走大圆(球面), 不是 map 空间的线性平均 —— 远洋航线上线性中点会偏出航线一截。
            //   _dist 的量纲也跟着换: 平图时代是"离焦点多远", 现在是"离地表多高"; 上限 160 = 滚轮上限,
            //   下限 6 = 黑雾球壳(R+2.8)之外, 所以出航取景再远也不会把整颗球甩出画面、再近也不会钻进雾壳。
            Vector3 mid = SeaGlobe.GreatCircleMid(DepartWorld, DestWorld);
            _focus = SeaGlobe.ToWorld(mid.x, -mid.z);
            float span = Vector3.Distance(DepartWorld, DestWorld);
            _dist = Mathf.Clamp(Mathf.Max(span, _route != null ? _route.Total * 0.45f : 0f) * 0.9f, 6f, 78f);
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
            return DaysFor(MapToWorld(a), MapToWorld(b));
        }

        // 沿海路折线上的参数点(u=0..1 按航程弧长分布); 无海路时退化为直线。
        //   走的是**加密后**的 _routePath, 与虚线同源。
        //   参数化用的是 _routePath 自己的折线长(而不是 _route.Total): 加密会改变折线长(高纬尤其明显,
        //   大圆东西向比 map 直线短得多), 若拿原始的 Total 来配这把尺子, 船会在 u 还没到 1 时就走完折线、
        //   提前杵在终点。这里只要求"u=1 恰好走到终点", 而 Day 数由 _route.Total 单独管(见 DaysFor)。
        Vector3 RoutePointAt(float u)
        {
            var pts = _routePath;
            if (pts == null || pts.Count == 0)
                return Vector3.Lerp(DepartWorld, DestWorld, u);
            if (pts.Count == 1) return pts[0];
            float total = 0f;
            for (int i = 1; i < pts.Count; i++) total += Vector3.Distance(pts[i - 1], pts[i]);
            if (total <= 0.0001f) return pts[pts.Count - 1];
            float target = Mathf.Clamp01(u) * total;
            float acc = 0f;
            for (int i = 1; i < pts.Count; i++)
            {
                float seg = Vector3.Distance(pts[i - 1], pts[i]);
                if (seg <= 0.0001f) continue;
                if (acc + seg >= target)
                {
                    float t = Mathf.Max(0f, (target - acc) / seg);
                    return Vector3.Lerp(pts[i - 1], pts[i], t);
                }
                acc += seg;
            }
            return pts[pts.Count - 1];
        }

        // 把海路折线加密到 ≤1°。只加密**中间**那段:
        //   首末两段是进出港的登陆腿(港建在陆上), 本来就短, 而且是不画的那两段 ——
        //   原样留着, 于是"要画的那一段"正好是结果里的 [1] .. [Count-2]。
        static List<Vector3> BuildRoutePath(List<Vector3> pts)
        {
            var outp = new List<Vector3>();
            if (pts == null || pts.Count == 0) return outp;
            if (pts.Count == 1) { outp.Add(pts[0]); return outp; }
            var mid = new List<Vector3>(pts.Count);
            for (int i = 1; i <= pts.Count - 2; i++) mid.Add(pts[i]);
            outp.Add(pts[0]);
            outp.AddRange(SeaGlobe.ArcSubdivide(mid, 1.0f));
            outp.Add(pts[pts.Count - 1]);
            return outp;
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
            // 船沿海路折线前移(绝不直线穿陆地); 浮沉很轻, 不再大起大落。
            //   RoutePointAt 给的是 map 空间点(模拟层口径), 这里才上球面 —— 抬升沿法线。
            Vector3 wp = RoutePointAt(p);
            Vector3 here = SeaGlobe.ToWorld(wp.x, -wp.z);
            Vector3 nrm = here.normalized;
            float bob = Mathf.Sin(_voyageT * 2.2f) * 0.018f;
            _ship.position = nrm * (SeaGlobe.R + 0.05f + bob);

            // 船头始终对准"再往前一小段"的走向(把船艏 x 轴转到行进方向), 转弯按限速渐变,
            //   而不是每帧死贴瞬时切线 —— 过折点不甩头、不来回摆。
            //   方向取两个**球面位置之差**(= 切向); 用 map 空间的差会随纬度越偏越歪。
            if (p < 1f)
            {
                float look = 0.005f;
                if (_route != null && _route.Total > 0.001f)
                    look = Mathf.Clamp(3.5f / _route.Total, 0.002f, 0.05f);
                Vector3 aheadMap = RoutePointAt(Mathf.Min(1f, p + look));
                Vector3 dir = SeaGlobe.ToWorld(aheadMap.x, -aheadMap.z) - here;
                if (dir.sqrMagnitude > 1e-10f)
                {
                    Quaternion want = ShipRotationOnGlobe(nrm, dir);
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
                // 到港落稳 → 推近到港区。只挪焦点不改 _camUp: 玩家自己转出来的朝向别被到港硬掰回去。
                _focus = SeaGlobe.ToWorld(arrive.Lon, arrive.Lat);
                _dist = Mathf.Min(_dist, 26f);
                PlaceShipAt(arrive, Time.time);
                RevealFogAtPort(arrive, FogDockR);   // 到港揭一大圈(泊港不再逐帧揭, 见 Update)
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
                _focus = SeaGlobe.ToWorld(_anchor.x, -_anchor.z);
                _dist = Mathf.Min(_dist, 26f);
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

        // ---- 给养分舱: 每艘船"名下"扛着多少载给养(纯展示口径) ----
        //   模拟层里给养是**全队一本账**(fleet.Prov, 存读档也是全队一份), 没有"哪艘船扛"这回事。
        //   可舰队页的船卡与右下角货仓速览都要按船报"仓位占了多少", 这份公账就得有个分摊口径。
        //   取"按各船**剩余舱位**分摊", 三条理由:
        //     · 收货时 CanPlace / FleetOps.AddCargo 本来就是按**剩余舱位**逐船塞的 —— 给养照同一条
        //       规则走, "货往哪儿塞、给养就往哪儿塞", 玩家不必去理解第二套口径;
        //     · 这是唯一能保证"每艘船报出来的占用都 ≤ 它自己的舱位上限"的口径。按舱位**上限**按比例
        //       分摊的话, 一艘塞满货的船会报出 260 + 给养 > 260、"剩"成负数 —— 一眼假;
        //     · 取整用最大余数法(先落整数下界, 余下那几载补给小数部分最大的船), Σ各船恒等于全队给养,
        //       于是船卡上的数字加起来正好是「全队合计」那一行报的那个数。
        //   代价(如实记下): 装卸货会改变各船的"剩余舱位", 于是各船名下那几载**会跟着挪** ——
        //   那是"按空舱分摊"的本义, 不是抖动。真觉得晃眼, 改成按 EffCapacity(舱位上限)分摊即可, 一行的事。
        //   全队一点空舱都没有(买货只查逐船舱位, 给养又先占了额度, 这个态是能走到的)时各船报 0:
        //   照实报"塞不下", 而不是编一个负数出来。
        public int[] ShipProvWeights()
        {
            var ships = fleet.Ships;
            int n = ships != null ? ships.Count : 0;
            var res = new int[n];
            int total = ProvWeight();
            if (n == 0 || total <= 0) return res;

            var room = new int[n];
            int roomSum = 0;
            for (int i = 0; i < n; i++)
            {
                room[i] = Mathf.Max(0, ships[i].EffCapacity - ships[i].OccupiedWeight(fleet.WeightOf));
                roomSum += room[i];
            }
            if (roomSum <= 0) return res;

            int placed = 0;
            var frac = new float[n];
            for (int i = 0; i < n; i++)
            {
                float exact = (float)total * room[i] / roomSum;
                int w = Mathf.Min(room[i], Mathf.FloorToInt(exact));
                res[i] = w; placed += w; frac[i] = exact - w;
            }
            // 余数最多 n-1 载, 给"小数部分最大"的那几艘(比例上最该多得的先拿);
            //   已经贴到自己舱位上限的船不再参与(它那小数为大是被 min 夹出来的, 不是真该多拿)。
            int left = Mathf.Min(total - placed, roomSum - placed);
            while (left > 0)
            {
                int best = -1;
                for (int i = 0; i < n; i++)
                    if (res[i] < room[i] && (best < 0 || frac[i] > frac[best])) best = i;
                if (best < 0) break;
                res[best]++; frac[best] = -1f; left--;
            }
            return res;
        }
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

        // ---- 半仓补员: 逐船看, 不足半仓的补到半仓, 已过半仓的**原样不动**(只补不裁) ----
        //   "半仓"取 CrewMax 的一半; 再兜一个 CrewMin 下限 —— 现有 13 种船型都满足 半仓 ≥ 下限,
        //   但下限是"这条船开不开得动"的硬线, 不该靠一张数据表顺手保证(将来加小船容易踩到)。
        //   为什么要有这个操作: 招满水手是给远洋/接舷准备的, 近海跑一趟根本用不上那么多人,
        //   而水手按人头吃给养、占仓位(见 ShipProvWeights), 满编跑短程纯亏。半仓是"够开"的档。
        public static int CrewHalfOf(ShipModel m)
        {
            if (m == null) return 0;
            return Mathf.Max(m.CrewMax / 2, m.CrewMin);
        }

        public long RecruitCostToHalf()
        {
            if (State != Mode.Docked) return -1L;
            long cost = 0;
            foreach (var s in fleet.Ships)
            {
                int need = CrewHalfOf(s.Model) - s.CrewAboard;
                if (need > 0) cost += (long)(need * cat.Tuning.RecruitCostBase);
            }
            return cost;
        }

        public void RecruitCrewHalf()
        {
            if (State != Mode.Docked) return;
            long cost = 0; int missing = 0;
            foreach (var s in fleet.Ships)
            {
                int need = CrewHalfOf(s.Model) - s.CrewAboard;
                if (need > 0) { missing += need; cost += (long)(need * cat.Tuning.RecruitCostBase); }
            }
            if (missing <= 0) { Banner = "各船水手都已过半仓, 无需补员"; return; }
            if (cost > fleet.Gold) { Banner = "现金不足补员(" + Money(cost) + ")"; return; }
            fleet.Gold -= cost;
            // 逐船"不足才补": 满编的船这一趟一个子儿都没花在它身上, 也就不该被顺手裁回半仓。
            foreach (var s in fleet.Ships)
            {
                int half = CrewHalfOf(s.Model);
                if (s.CrewAboard < half) s.CrewAboard = half;
            }
            PushLog($"在港口码头招募水手 ×{missing}(补到半仓), 花 {Money(cost)}");
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
                //   新档直接还原海图; 认不出雾数据的档(没有 fog 字段的老档 / 平面时代 250×163 的旧位图)
                //   → 至少让"到过的城"在雾里现形一圈(老玩家地图不被清零), 随后的 Update 还会在当前位置继续现形。
                if (_fog != null)
                {
                    bool hadFog = _fog.DecodeState(d.fog);   // 认得出才为 true(尺寸标签对不上 = 不认)
                    // 大本营虽不在 _cityFlags 里(它不算"到过"), 但"家在哪儿"无论如何该是已知的 ——
                    //   少了这一圈, 老档一读进来, 玩家最熟的那片海反而是黑的。
                    if (!hadFog)
                    {
                        var hp = world.FindPort(homePortId);
                        if (hp != null) _fog.RevealAt(hp.Lon, hp.Lat, FogDockR);
                    }
                    if (!hadFog && _cityFlags.Count > 0)
                        foreach (var id in _cityFlags)
                        {
                            var pp = world.FindPort(id);
                            if (pp == null) continue;
                            _fog.RevealAt(pp.Lon, pp.Lat, FogDockR);
                        }
                    _fog.Flush();
                }
                FrameHome(); ApplyCamera(_focus);
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
