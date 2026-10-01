using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sea
{
    // =============================================================
    // SEA · 商路试炼 HUD(纯代码 uGUI)
    //   目标是把"开船做生意"这圈讲明白、看得清:
    //     - 顶部试炼目标条(净赚进度)
    //     - 左上舰况/现金/日期, 右上航行日志
    //     - 底部操作条: 永远告诉你"下一步该做什么" + 一键出航
    //     - 本港行情表(左下, 到港自动弹开): ◆特产 ▲抢手, 颜色=便宜/贵
    //   无任何美术素材; 字 = OS 中日韩动态字体。
    // =============================================================
    [DefaultExecutionOrder(-80)]
    public sealed class SeaHud : MonoBehaviour
    {
        // 色板(航海·羊皮纸·金)
        static readonly Color ColGold = new Color(1f, 0.85f, 0.45f);
        static readonly Color ColTitle = new Color(0.99f, 0.80f, 0.40f);
        static readonly Color ColTxt = new Color(0.92f, 0.94f, 0.97f);
        static readonly Color ColDim = new Color(0.62f, 0.70f, 0.78f);
        static readonly Color ColPanel = new Color(0.05f, 0.075f, 0.115f, 0.90f);
        static readonly Color ColBtn = new Color(0.14f, 0.40f, 0.50f, 1f);
        static readonly Color ColGoldBtn = new Color(0.93f, 0.68f, 0.18f, 1f);
        static readonly Color ColGreen = new Color(0.55f, 1f, 0.62f);
        static readonly Color ColRed = new Color(1f, 0.60f, 0.55f);
        static readonly Color ColProd = new Color(0.52f, 0.95f, 0.74f);   // ◆ 特产
        static readonly Color ColImpo = new Color(1f, 0.74f, 0.44f);     // ▲ 抢手/需求
        static readonly Color ColLog = new Color(0.75f, 0.88f, 0.69f);   // 航行日志正文绿 = 基本信息卡"士气"健康值 #BFE0B0
        const float ShipPicAspect = 2f;   // 船图统一比例 宽:高 = 2:1(舰队卡/船坞行共用, 出图按此比例裁)

        // ---- 舰队情况浮层的版面常数 ----
        //   为什么卡高是**定死**的、而不是像早先那样"由容器高度除下来": 一张卡里那 4~6 行参数
        //   一行都不能少、也一行都不能越出卡外 —— 越出去就压到下方的「全队合计」, 那正是这版要修的毛病。
        //   (旧算法 perRow=2 固定, 三艘船就排成两行, rowH 被压到 133, 参数块却要 6 行 ≈ 93 ——
        //    溢出的那几行正好落在合计带上。行高由容器反推 = 船一多就必然溢出。)
        //   所以改成: 卡高按内容定死, 让**面板高度**跟着行数走。
        const float FleetPanelW = 1252f;      // 与底栏动作条同宽(1252) —— 浮层与底栏左右齐边
        const float FleetHeadH  = 36f;        // 头部(标题 + ✕)高度
        const float FleetCardsBottom = 140f;  // 船卡区下缘 = 合计带上沿(合计带 90~140, 按钮 40~80)
        const float FleetCardH  = 196f;       // 一张卡: 金框 2×2 + 留白 + 图带 56 + 名牌 22 + 参数块 ~98(够 5 行还余 ~12)
        const float FleetPicH   = 56f;        // 图带高(2:1 → 宽 112); 参数块优先, 图是占位框, 让它让位
        const int   FleetPerRowMax = 5;       // 每行至多 5 张 —— 10 艘正好两行, 再多也排得下
        const float FleetCardWMax = 390f;     // 船少时别把一张卡拉成一条
        const float FleetGapX = 12f, FleetGapY = 12f;
        // 船卡的金框与内衬(用户定: 「每艘船的图片和信息放在一个金色方框区域中」)。
        //   框由两层实心矩形叠出来 —— 底板涂金、内衬比它四边各缩 FleetCardEdge —— 不用描边精灵:
        //   九宫格 PNG 要进仓库、还得随分辨率缩放, 缩不好就糊边; 两层矩形在任何尺寸下都是硬边,
        //   颜色又与全项目调色板同源(金 = ColGoldBtn/ColTitle 一系)。
        const float FleetCardEdge = 2f;    // 金框线宽(露出来的那一圈)
        const float FleetCardPadX = 10f;   // 内容距金框内侧的左右留白
        const float FleetCardPadT = 5f;    // 上留白
        const float FleetCardPadB = 5f;    // 下留白
        static readonly Vector2 FleetPanelPos = new Vector2(0f, 56f);   // 可用区(底栏顶 112 ~ 屏顶 720)正中 = 416, 即中心 +56

        // 面板高度 = 船卡区下缘 + 船卡区(rows 行) + 头部
        static float FleetPanelH(int rows)
        {
            if (rows < 1) rows = 1;
            return FleetCardsBottom + rows * FleetCardH + (rows - 1) * FleetGapY + FleetHeadH;
        }

        SeaPlay play;
        Camera _cam;

        // 顶部试炼条
        Text _goalTitle, _goalSub;
        Image _goalFill;

        // 舰况 / 日志 / 航行
        Text _status, _logText, _banner, _sailTitle, _sailBar;
        GameObject _dockGo, _sailGo, _bannerGo, _logGo;
        Dictionary<string, Button> _dock = new Dictionary<string, Button>();

        // 行情
        GameObject _tradeGo;
        Text _tradeTitle;
        bool _tradeOpen;
        readonly List<Text> _tName = new List<Text>(), _tAsk = new List<Text>(),
                            _tBid = new List<Text>(), _tHold = new List<Text>(), _tStock = new List<Text>(),
                            _tCost = new List<Text>();   // 成本 = 持仓平均成本/件
        readonly List<Button[]> _buy = new List<Button[]>();    // 每行 [买1, 买10, 全买]
        readonly List<Button[]> _sell = new List<Button[]>();   // 每行 [卖1, 卖10, 全卖]
        readonly List<Text[]> _sellLbl = new List<Text[]>();    // 与 _sell 平行: 每键上的"卖1/卖10/全卖"字(无持仓时改暗淡, 别再亮白)
        readonly List<Image[]> _sellImg = new List<Image[]>();  // 与 _sell 平行: 每键的**底色**(手里有这件货才转金黄 —— 黄=有东西可卖)
        // 行情行高亮: 每行一条透明感应带(悬停微亮 / 点选那行金亮, 避免误操作) —— 感应带排在按钮之下
        readonly List<Image> _rowBand = new List<Image>();
        readonly List<RectTransform> _rowRt = new List<RectTransform>();
        // **行号 → 货** 的映射(行长 = 货总数的定长表, 由 SortMarketRows 填)。
        //   为什么要这张表: 行控件是在 BuildTrade 里一次性建好的, 之后再不会增删/挪位;
        //   而"有持仓的货排在上面"要求**行的顺序 ≠ 数据顺序**。所以让行号永远是行号,
        //   只在排序那一刻决定"这一行此刻装的是哪件货" —— 两件事解耦, 也就不存在
        //   "排序之后行控件和它绑定的货对不上号"这类错位。
        readonly List<Good> _rowGoods = new List<Good>();
        // 这张表在**进港那一刻**填一次, 之后整个停靠期间钉住不动 ——
        //   否则买一手货就重排, 鼠标底下的行会当场跳走, 下一击必然点错货。
        //   只在换港(和首次构建)时重排, 见 Update 的 curIdNow != _lastPort 分支。
        RectTransform _marketVp;
        int _hoverRow = -1, _selRow = -1;

        // 买入时的右下角"舰队货仓"速览(行情开着才显示): 用格子条画占载, 下挂各船货物的名称/件数/占重
        GameObject _holdGo;
        Text _holdBody;

        // 情报站(贸易日报 / 全球商情报 / 我的订阅)浮层: 大厅 / 我的订阅(封面墙) / 报纸翻页 / 商情报表格
        GameObject _newsGo, _newsHome, _newsPaper, _newsReport, _newsSubs;
        bool _newsOpen;
        string _newsPage = "home";                  // 铺内当前页: home / subs / paper / report
        Button _newsBuyBtn, _newsRptBtn, _newsSubsBtn;   // 大厅三档主按钮(购/阅 动态换字)
        // 我的订阅(封面墙, 打开时重建)
        RectTransform _subsRoot;
        int _subsSig = -1;                          // 已渲染条数(变化才重建封面)
        // 贸易日报(翻页报纸): 第1版 = 晨报速递整版(单栏通栏), 第2版起 = 分港行市 每版 4 栏 × 每栏至多 3 城
        Text[] _paperCols;                          // 分栏正文(至多 4 栏并排, 蒙版裁切; 头版只启用第0栏并拉通到整幅宽)
        Text _paperFolio;                           // 底部刊脚(报名 · 期日 · 版次)
        Button _paperPrev, _paperNext;
        List<string[]> _paperColPages = new List<string[]>();  // 每页各栏文本(page[c], c < 该页实际栏数 _paperColN[i])
        List<int> _paperColN = new List<int>();     // 与 _paperColPages 平行: 每页实际栏数(头版=1, 行市版=4)
        List<int> _paperFonts = new List<int>();    // 与 _paperColPages 平行: 每版整版统一字号(测高=渲染同字号)
        int _paperIdx = 0;
        string _paperSig = "", _paperIssue = "";    // 正文快照 / 底部刊脚期日
        float _pageW = 980f, _pageH = 408f;                  // 版面量度: 整幅宽 / 每版高(栏宽按页栏数实时算, 见 ColWidthFor)
        const int PaperCols = 4;                    // 行市版固定 4 栏(每版 8 城平衡铺开, 一栏 2 城)
        const float PaperGap = 12f;                 // 栏沟(头版单栏无沟)
        const float PaperHeadLead = 1f;             // 头版(晨报速递)行距(行间纵向间隔倍数)
        const float PaperMarketLead = 1.30f;        // 行市版(第2版起)行距: 供需各行上下间隔加宽, 城市块更好扫读
        // 全球商情报(地区 × 商品一张表)
        Text _rptCap, _rptNote, _rptAreaLbl;
        RectTransform _rptVp;                       // 表格滚动视窗
        RectTransform _rptRows;                     // 表格内容容器(切地区时重建行)
        int _rptArea = 0, _rptAreaN = 0;            // 当前看哪个航区
        int _rptSig = -1;                           // (地区+航区数)重建印记
        SeaIntelSave _openPaper, _openReport;       // 当前正在看的刊物


        // 舰队情况面板(一艘船一张"船图占位 + 该船参数"的卡, 下方全队合计)
        GameObject _fleetGo;
        bool _fleetOpen;
        RectTransform _shipRoot;                 // 船卡容器(卡按船数 2×N 排布)
        Text _fleetSum;                          // 全队合计(船卡下方、按钮上方)
        readonly List<Text> _shipHeads = new List<Text>();   // 每卡一行名牌
        readonly List<Text> _shipStat = new List<Text>();    // 每卡该船参数块
        string _shipSig = "";
        Button _fleetProv15, _fleetProv30, _fleetRecruit, _fleetRecruitHalf, _fleetDock, _fleetSail;

        // 花金操作确认弹窗
        GameObject _confirmGo;
        Text _confirmMsg;
        Button _confirmOk, _confirmCancel;
        System.Action _confirmAction;

        // 出航前检查浮层(水手 / 给养 vs 航程 / 货舱清单 → 玩家确认后才真正出航)
        GameObject _departGo;
        bool _departOpen;
        Text _departBody;

        // 右侧两张卡(航行日志 / 舰队货仓)的可滚动装配 —— 同一份代码装两遍, 见 MakeScroller
        Scroller _logSc, _holdSc;
        int _logVersion = -1;      // 已渲染的日志版本(只在日志真的变时重排文字)
        bool _logPinned = true;    // 视图贴在最"新"(底部); 玩家往上翻历史时不打断

        // 行情表的滚动视窗(20 件货一屏放不下) —— 开铺时要把它卷回第一行
        ScrollRect _tradeScroll;

        // 港名标签
        readonly Dictionary<string, Text> _labels = new Dictionary<string, Text>();

        GameObject _logoGo;   // 左上题名(最后挂的, 永远在最上层) —— 舰队面板开着时让它让位, 见 Update 那段

        // 常驻 HUD 面板的矩形(地名标签禁区): 只要标签的屏幕框压到这些框, 就不显示该标签
        //   —— 保证按钮/日志/舰况/目标条等永不被地图地名盖住(地名垫在所有面板之下 + 此处再加道保险)
        RectTransform _goalRt, _statusRt, _logRt, _dockRt, _sailRt;

        // 船坞(购新船 / 出售旧船 / 改装: 桅帆提速 · 火炮防盗 · 装甲 · 货舱)
        GameObject _yardGo;
        bool _yardOpen;
        ShipState _yardSel;                  // 左栏当前选中待处理(改/卖/立旗)的船
        Text _yardTitle, _yardCash;
        ScrollRect _yardScroll;              // 右栏"可购船表"滚动视窗
        RectTransform _yardContent;
        string _yardListSig = "";            // 右栏清单按 港区+船表 重建
        readonly List<Transform> _yardOwnGo = new List<Transform>();   // 在役船行(每行一个容器, 最多 5)
        readonly List<Image> _yardOwnBand = new List<Image>();         // 每行选中高亮带
        readonly List<Text> _yardOwnName = new List<Text>();           // 每行名牌
        readonly List<Text> _yardOwnInfo = new List<Text>();           // 每行参数小字
        readonly List<Image> _yardOwnPic = new List<Image>();          // 每行船图(精灵层, 随该槽位船型刷新)
        readonly List<Text> _yardOwnPh = new List<Text>();             // 每行船图占位小字
        Text _yardActName;                   // 选中船摘要(改装/出售操作条)
        Button _yardFlagSel, _yardSellSel;   // 选中船: 任旗 / 出售
        readonly Button[] _yardRefit = new Button[4];   // 0桅帆 1火炮 2装甲 3货舱
        readonly List<Transform> _yardBuyGo = new List<Transform>();   // 右栏可购船行
        readonly List<Text> _yardBuyName = new List<Text>();
        readonly List<Text> _yardBuyInfo = new List<Text>();
        readonly List<Button> _yardBuyBtn = new List<Button>();
        readonly List<ShipModel> _yardBuyModel = new List<ShipModel>();

        bool _built;
        string _lastPort = "__";      // 检测"新到一港" → 自动弹行情
        int _lastVoyage;              // 上一次看到的 SeaPlay.VoyageSerial(开局/读档 = 一次进港)
        string _qaVoyageEdge = "(还没发生过进港边沿)";   // [闸门] 见 Update 进港分支
        int _qaVoyageEdges;
        bool _goalCongrats;
        float _bannerAt = -99f; string _bannerText = "";

        static Sprite s_white;
        static Font s_cjk;

        void Awake() { play = GetComponent<SeaPlay>(); }

        // 顶层浮层是否开着(行情 / 舰队 / 确认 / 出航检查)。开着 = 它就是"最高层":
        //   · 底下海图的地名标签全隐藏(地名不跃层透射)
        //   · 底下海图的输入(滚轮缩放 / 拖移 / 点选)全暂停
        public bool AnyTopOpen
        {
            get
            {
                if (_tradeGo != null && _tradeGo.activeSelf) return true;
                if (_fleetGo != null && _fleetGo.activeSelf) return true;
                if (_yardGo != null && _yardGo.activeSelf) return true;
                if (_confirmGo != null && _confirmGo.activeSelf) return true;
                if (_departGo != null && _departGo.activeSelf) return true;
                if (_newsGo != null && _newsGo.activeSelf) return true;
                if (play != null && play.SettingsOpen) return true;   // 设置弹层也算顶层: 藏地名 + 停地图输入
                return false;
            }
        }

        void LateUpdate()
        {
            if (play == null || play.world == null || play.engine == null) return;
            if (!_built) BuildAll();
            Refresh();
        }


        // =============================================================
        // 搭建
        // =============================================================
        void BuildAll()
        {
            _built = true;
            _cam = Camera.main;
            EnsureEventSystem();

            var cvGo = new GameObject("HUD Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            cvGo.transform.SetParent(transform, false);
            var cv = cvGo.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 20;
            var sc = cvGo.GetComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1280, 720);
            sc.matchWidthOrHeight = 0.5f;

            var root = Rt(NewRT("root", cvGo.transform));
            RootRect(root);

            BuildGoal(root);
            BuildPortLabels(play.world);   // 地名标签最先挂 → 下面所有面板(行情/舰队/确认)都盖在它上面

            // 玩家基本信息卡 → 顶部正中(试炼卡已移到右上, 卡自身贴顶; 右缘 x910 不压右上日志/试炼列, 下隔横幅区不碰行情框顶 y144)
            _status = BuildCard(root, "card_fleet", 540, 56,
                "text", 12, ColTxt, TextAnchor.UpperLeft, out var statusGo);
            RectAt(Rt(statusGo), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -8), new Vector2(540, 56));
            Fill(Rt(_status.gameObject), 14, 14, 5, 5);
            _statusRt = Rt(statusGo);   // 地名标签 keep-out 用

            BuildLog(root);   // 右上航行日志: 可滚动翻看, 每条带日期(见 BuildLog/RefreshLog)

            // 横幅(顶部试炼条正下方)
            _bannerGo = Panel("banner", root.transform, new Color(0.02f, 0.03f, 0.05f, 0f));
            RectAt(Rt(_bannerGo), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -96), new Vector2(900, 44));
            _banner = AddText(_bannerGo.transform, "", 26, new Color(1f, 0.86f, 0.42f), TextAnchor.MiddleCenter);
            Fill(Rt(_banner.gameObject), 0, 0, 0, 0);

            // ---- 底部操作条(泊港动作 / 航行进度共用这块地皮; 拉宽到近屏边, 好放右下角的「⚙ 设置」) ----
            _dockGo = Panel("dock", root.transform, ColPanel);
            RectAt(Rt(_dockGo), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(0, 10), new Vector2(1252, 102));
            _dockRt = Rt(_dockGo);   // 地名标签 keep-out: 底栏永不被城市名压住
            BuildDock(_dockGo.transform);

            _sailGo = Panel("sail", root.transform, ColPanel);
            RectAt(Rt(_sailGo), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(0, 10), new Vector2(1252, 96));
            _sailRt = Rt(_sailGo);   // 地名标签 keep-out: 航行进度条同上
            _sailTitle = AddText(_sailGo.transform, "", 20, ColGold, TextAnchor.MiddleCenter);
            RectAt(Rt(_sailTitle.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -18), new Vector2(0, 26));
            _sailBar = AddText(_sailGo.transform, "", 16, ColTxt, TextAnchor.MiddleCenter);
            RectAt(Rt(_sailBar.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -52), new Vector2(0, 22));
            // 航行中也开着"舰队情况"入口(看信息不花钱; 招募/补给需靠港)
            _fleetSail = MakeBtn(_sailGo.transform, "fleetSail", "⚓ 舰队", 15, ColBtn, Color.white);
            RectAt(Rt(_fleetSail.gameObject), new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(-140, 8), new Vector2(120, 80));
            _fleetSail.onClick.AddListener(ToggleFleet);
            // 「⚙ 设置」移到航行条右下角(与舰队入口同一黑底; 原右上角齿轮已由 SeaSettings 移除)
            var sailSet = MakeBtn(_sailGo.transform, "set", "⚙ 设置", 15, ColBtn, Color.white);
            RectAt(Rt(sailSet.gameObject), new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(-10, 8), new Vector2(116, 80));
            sailSet.onClick.AddListener(OpenSettings);

            // ---- 本港行情(左下; 到港自动弹开) ----
            _tradeGo = Panel("market", root.transform, new Color(0.02f, 0.04f, 0.065f, 0.95f));
            RectAt(Rt(_tradeGo), new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0),
                new Vector2(10, 124), new Vector2(900, 452));
            BuildTrade(_tradeGo.transform);
            BuildHold(root.transform);   // 右下角货仓速览: 行情开着时, 买/卖后这里按船刷新占用
            // 行情开/关只由底部操作条左侧的「本港行情」按钮负责(不再放右下角重复开关)

            // ---- 舰队情况(居中浮层) ----
            BuildFleet(root.transform);
            // ---- 花金操作确认(最上层浮层) ----
            BuildConfirm(root.transform);
            // 出航前检查(在确认弹窗之后建, 盖在它上面; 比地图地名更晚挂 → 不会让地名透上来)
            BuildDepart(root.transform);
            // 船坞浮层(买/卖/改装)
            BuildYard(root.transform);
            // 情报站(全球商报 / 分析报)
            BuildIntel(root.transform);
            // 左上游戏名(最后挂, 盖在所有 HUD 上、永远清晰)
            BuildTitle(root.transform);
        }

        void BuildTitle(Transform root)
        {
            // 左上角游戏名 = logo.png(Resources/logo/, 等比例缩放, 上限约 380×56, 别压到下方舰况卡)
            // 注意尺寸: 保持宽高比; 超过上限按上限等缩。logo 尚未导入(无 .meta / 首次读)时退回文字题名。
            var tex = Resources.Load<Texture2D>("logo/logo");
            if (tex != null && tex.width > 0)
            {
                float asp = tex.width / (float)Mathf.Max(1, tex.height);
                float w = 700f, h = w / asp;      // 约旧版的 2 倍(旧 ≈130×54 → 新 ≈259×108)
                if (h > 108f) { h = 108f; w = h * asp; }
                if (w > 720f) { w = 720f; h = w / asp; }
                var go = NewRT("logo", root);
                var img = go.AddComponent<Image>();
                img.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                img.preserveAspect = true;
                img.raycastTarget = false;   // 名牌图不挡底下地图点选
                RectAt(Rt(go), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                    new Vector2(12, -10), new Vector2(w, h));
                _logoGo = go;
                return;
            }

            // 兜底: 还没导进 logo 图时, 先放一段文字题名占位
            var t = AddText(root, "大航海时代 2026", 11, ColTitle, TextAnchor.UpperLeft);
            t.fontStyle = FontStyle.Bold;
            _logoGo = t.gameObject;
            var ol = t.gameObject.AddComponent<Outline>();
            ol.effectColor = new Color(0f, 0.02f, 0.05f, 0.85f);
            ol.effectDistance = new Vector2(0.7f, -0.7f);
            RectAt(Rt(t.gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(10, -7), new Vector2(300, 18));
        }

        // =============================================================
        // 舰队情况面板: 舰队/货舱/水手 + 招募水手、买补给按钮(花金先确认)
        // =============================================================
        void BuildFleet(Transform root)
        {
            // 宽 = 底栏同宽; 高**随行数变** —— 每次船队变动由 EnsureFleetShipCards 重设(这里先按两行建)。
            //   竖直位置钉在"底栏之上那块可用区"(底栏顶 112 ~ 屏顶 720, 中心 416)的正中:
            //   于是面板长高长矮都是**上下对称**地长 —— 10 艘(560 高)也上不撞屏顶、下不压底栏。
            _fleetGo = Panel("fleet", root, ColPanel);
            RectAt(Rt(_fleetGo), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                FleetPanelPos, new Vector2(FleetPanelW, FleetPanelH(2)));
            var head = AddText(_fleetGo.transform, "⚓ 舰队情况", 18, ColTitle, TextAnchor.MiddleCenter);
            RectAt(Rt(head.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -2), new Vector2(0, 26));
            var xb = MakeBtn(_fleetGo.transform, "close", "✕", 16, ColBtn, Color.white);
            RectAt(Rt(xb.gameObject), new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(-6, -2), new Vector2(30, 24));
            xb.onClick.AddListener(() => _fleetOpen = false);

            // 船卡区: 居中浮层主体 —— 每艘船一张卡 = 船图占位(上) + 该船参数(下)
            //   下缘 = 合计带上沿, 上缘 = 头部下沿; 卡区高度正好由行数决定(见 FleetPanelH)。
            _shipRoot = Rt(NewRT("ships", _fleetGo.transform));
            var sr = _shipRoot;
            sr.anchorMin = new Vector2(0, 0); sr.anchorMax = new Vector2(1, 1);
            sr.offsetMin = new Vector2(22, FleetCardsBottom); sr.offsetMax = new Vector2(-22, -FleetHeadH);

            // 全队合计(至多三行短句, 抬到按钮带(高40、顶缘y80)之上、船卡(底缘y140)之下;
            //  溢出不越过矩形下缘 → 文字绝不会再压到下方按钮)
            _fleetSum = AddText(_fleetGo.transform, "", 12, ColTxt, TextAnchor.UpperLeft);
            var fs = Rt(_fleetSum.gameObject);
            fs.anchorMin = new Vector2(0, 0); fs.anchorMax = new Vector2(1, 0);
            fs.pivot = new Vector2(0.5f, 0);
            fs.offsetMin = new Vector2(26, FleetCardsBottom - 50f); fs.offsetMax = new Vector2(-26, FleetCardsBottom);
            _fleetSum.horizontalOverflow = HorizontalWrapMode.Wrap;
            _fleetSum.verticalOverflow = VerticalWrapMode.Truncate;

            _fleetProv15 = MakeBtn(_fleetGo.transform, "prov15", "补给 15 日", 15, ColBtn, Color.white);
            _fleetProv30 = MakeBtn(_fleetGo.transform, "prov30", "补给 30 日", 15, ColBtn, Color.white);
            _fleetRecruit = MakeBtn(_fleetGo.transform, "crew", "招满水手", 15, ColGoldBtn, new Color(0.14f, 0.08f, 0.03f));
            // 半仓补员: 招满的"减配版" —— 同一族操作放右手边, 但用回普通色, 一眼分得清主次。
            _fleetRecruitHalf = MakeBtn(_fleetGo.transform, "crewHalf", "半仓水手", 15, ColBtn, Color.white);
            // 一行四键居中: 键宽 138、缝 12 → 整排 588, 左缘 -294, 键心间距 150。
            PlaceFleetBtn(_fleetProv15, -225f, 138f);
            PlaceFleetBtn(_fleetProv30, -75f, 138f);
            PlaceFleetBtn(_fleetRecruit, 75f, 138f);
            PlaceFleetBtn(_fleetRecruitHalf, 225f, 138f);
            _fleetProv15.onClick.AddListener(() => ConfirmProvision(15));
            _fleetProv30.onClick.AddListener(() => ConfirmProvision(30));
            _fleetRecruit.onClick.AddListener(ConfirmRecruit);
            _fleetRecruitHalf.onClick.AddListener(ConfirmRecruitHalf);

            _fleetGo.SetActive(false);
        }

        // 按当前船队建"船卡"(数量/船型变化才重建; 平时只刷名牌与参数文字)
        void EnsureFleetShipCards()
        {
            var ships = play.fleet.Ships;
            if (ships == null) return;
            var sb = new StringBuilder();
            foreach (var s in ships) sb.Append(s.Model.Id).Append('|');
            string sig = sb.ToString();
            if (sig == _shipSig && _shipHeads.Count == ships.Count) return;
            _shipSig = sig;
            if (_shipRoot == null) return;
            foreach (Transform ch in _shipRoot) Destroy(ch.gameObject);
            _shipHeads.Clear(); _shipStat.Clear();
            int n = ships.Count;

            // 行列: 每行至多 5 列 → 行数 = ceil(n/5); 列数再把 n **平摊到各行**上
            //   (6 艘 = 3+3, 不是"5+1 孤零零一张")。列数由 ceil(n/rows) 反推, 恒有 cols*rows ≥ n。
            int rows = 1, cols = 1;
            if (n > 0)
            {
                rows = (n + FleetPerRowMax - 1) / FleetPerRowMax;
                cols = (n + rows - 1) / rows;
            }
            // 面板高度跟着行数走 —— 卡区永远正好装下这些行, 不多也不少(多出来的高度全在卡区里,
            //   绝不会挤到下面的合计带/按钮带上去)。
            var fr = Rt(_fleetGo);
            float wantH = FleetPanelH(rows);
            if (fr != null && !Mathf.Approximately(fr.sizeDelta.y, wantH))
                fr.sizeDelta = new Vector2(FleetPanelW, wantH);
            if (n == 0) return;

            float W = Mathf.Max(60f, _shipRoot.rect.width);
            float colW = Mathf.Min(FleetCardWMax, (W - FleetGapX * (cols - 1)) / cols);

            for (int i = 0; i < n; i++)
            {
                int rw = i / cols, c = i % cols;
                int cnt = Mathf.Min(cols, n - rw * cols);                 // 这一行实际摆几张
                float rowW = cnt * colW + (cnt - 1) * FleetGapX;
                float x = (W - rowW) * 0.5f + c * (colW + FleetGapX);      // 不满的行居中, 不靠左
                float y = rw * (FleetCardH + FleetGapY);
                BuildShipCard(ships[i], x, y, colW, FleetCardH);
            }
        }

        // ---- 船图: Resources/ships/<船id>.png, 懒加载缓存; 缺图回 null → 各处显示占位文字 ----
        readonly Dictionary<string, Sprite> _shipSprites = new Dictionary<string, Sprite>();
        Sprite ShipSprite(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Sprite s;
            if (_shipSprites.TryGetValue(id, out s)) return s;
            var tex = Resources.Load<Texture2D>("ships/" + id);
            if (tex == null) return null;
            s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            _shipSprites[id] = s;
            return s;
        }
        // 把某艘船的图填进「图区」: art=精灵 Image(盖在深色底上, 等比例不拉伸), ph=占位小字。有图→显图藏字; 无图→藏图显占位。
        void SetShipArt(Image art, Text ph, string modelId, string emptyPh)
        {
            Sprite sp = ShipSprite(modelId);
            if (sp != null)
            {
                art.sprite = sp; art.color = Color.white;
                art.type = Image.Type.Simple; art.preserveAspect = true;
                art.enabled = true;
                if (ph != null) ph.text = "";
            }
            else
            {
                art.sprite = null; art.enabled = false;
                if (ph != null) ph.text = emptyPh;
            }
        }

        void BuildShipCard(ShipState s, float x, float y, float colW, float rowH)
        {
            var col = Rt(NewRT("ship", _shipRoot));
            col.anchorMin = new Vector2(0, 1); col.anchorMax = new Vector2(0, 1);
            col.pivot = new Vector2(0, 1);
            col.anchoredPosition = new Vector2(x, -y);
            col.sizeDelta = new Vector2(colW, rowH);

            // 金框 + 内衬(用户定: 每艘船的图片和信息都装在一个金色方框里)。
            //   先建底板、再建内衬 —— GUI 按层级顺序画, 所以先建的在下、后建的在上, 内容再压在两者之上。
            //   内衬故意偏不透明(0.95): 浮层本身 0.90 半透, 底下海图会淡淡透出来, 卡里再透就显脏。
            var plate = Panel("card", col, ColGoldBtn);
            Fill(Rt(plate), 0, 0, 0, 0);
            var face = Panel("face", plate.transform, new Color(0.085f, 0.115f, 0.16f, 0.95f));
            Fill(Rt(face), FleetCardEdge, FleetCardEdge, FleetCardEdge, FleetCardEdge);

            // 内容一律从金框内侧再让进留白; 卡窄到装不下图时以宽为准(再等比缩)。
            float inW = Mathf.Max(40f, colW - 2f * (FleetCardEdge + FleetCardPadX));
            float padT = FleetCardEdge + FleetCardPadT;
            float mgn = -2f * (FleetCardEdge + FleetCardPadX);   // 名牌/参数块的宽度增量(两侧各让一次)

            // 船图占位: 一律 2:1(与船坞同源), 高 60(宽据此 = 120), 水平居中 —— 未来把中央换成该船真图。
            //   为什么图带只有 60: 参数块优先, 卡里的硬约束是"那几行一行都不能少";
            //   图是占位框, 缩它代价最小。
            float picH = Mathf.Min(FleetPicH, rowH * 0.34f);
            float picW = picH * ShipPicAspect;
            if (picW > inW) { picW = inW; picH = picW / ShipPicAspect; }
            var pic = Panel("pic", col, new Color(0.34f, 0.44f, 0.54f, 1f));
            RectAt(Rt(pic), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -padT), new Vector2(picW, picH));
            var mat = Panel("mat", pic.transform, new Color(0.045f, 0.075f, 0.115f, 1f));
            Fill(Rt(mat), 2, 2, 2, 2);
            // 真图精灵(盖深色底上, 等比例); 占位文字在其上, 无图才显
            var artGo = NewRT("art", mat.transform);
            var art = artGo.AddComponent<Image>();
            art.raycastTarget = false;
            Fill(Rt(artGo), 0, 0, 0, 0);
            var ph = AddText(mat.transform, "", 11, new Color(0.48f, 0.60f, 0.70f, 0.92f), TextAnchor.MiddleCenter);
            Fill(Rt(ph.gameObject), 6, 6, 4, 4);
            SetShipArt(art, ph, s.Model.Id, "—— 船 图 占 位 ——\n(此框将换上该船真图)");

            // 名牌(旗舰金 / 僚舰浅蓝), 紧贴图下。名牌也 Truncate: 万一船名长到折行,
            //   折出来的第二行否则会画进下面参数块的地界里(AddText 默认是 Overflow)。
            var head = AddText(col, "", 15, s.IsFlagship ? ColGold : new Color(0.80f, 0.93f, 1f), TextAnchor.MiddleCenter);
            RectAt(Rt(head.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1f),
                new Vector2(0, -(padT + picH + 3)), new Vector2(mgn, 22));
            head.verticalOverflow = VerticalWrapMode.Truncate;
            _shipHeads.Add(head);

            // 该船参数块: 高度 = 卡高 - 上面那些 - 金框与下留白, 够装 5 行(水手/货仓/仓位 + 货单一到两行)。
            //   5 行是实测出来的上限(见 dev/fleet_grid_audit.ps1 的行带一节: 每行 ~16.5 canvas):
            //   货单最多 4 种 + 省略号, 卡一窄就会折成两行 —— 那是最坏情况, 不是 4 行。
            //   末行的 **Truncate** 是保险丝: 万一以后正文再加一行, 被切掉的是这一行的尾巴,
            //   而不是像旧版那样整块**溢出卡外**压到「全队合计」上。
            float stTop = padT + picH + 3 + 22 + 3;
            var st = AddText(col, "", 13, ColTxt, TextAnchor.UpperLeft);
            RectAt(Rt(st.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                new Vector2(0, -stTop), new Vector2(mgn, Mathf.Max(40f, rowH - stTop - (FleetCardEdge + FleetCardPadB))));
            st.horizontalOverflow = HorizontalWrapMode.Wrap;
            st.verticalOverflow = VerticalWrapMode.Truncate;
            _shipStat.Add(st);
        }

        void PlaceFleetBtn(Button b, float cx, float w)
        {
            RectAt(Rt(b.gameObject), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(cx, 40), new Vector2(w, 40));
        }

        void RefreshFleet()
        {
            if (play.fleet == null) return;
            EnsureFleetShipCards();
            var w = play.world;
            var prov = play.ShipProvWeights();   // 每艘船名下的给养载重(全队给养按空舱分摊, 见 SeaPlay)
            int n = Mathf.Min(play.fleet.Ships.Count, _shipStat.Count);
            for (int i = 0; i < n; i++)
            {
                var s = play.fleet.Ships[i];
                _shipHeads[i].text = (s.IsFlagship ? "★ 旗舰" : "僚舰") + " · " + s.Model.Name;
                var sb = new StringBuilder(160);
                int cargoW = ShipWeight(s);
                int provW = i < prov.Length ? prov[i] : 0;
                int occ = cargoW + provW;        // 仓位 = 货 + 给养: 给养同样吃舱位(用户点名要看得见)
                sb.Append("<b>水手</b> ").Append(s.CrewAboard).Append('/').Append(s.Model.CrewMax).Append('\n');
                int kinds = s.Cargo.Count, units = 0;
                foreach (var kv in s.Cargo) units += kv.Value;
                sb.Append("<b>货仓</b> ").Append(kinds).Append('/').Append(s.Model.Slots).Append(" 种\n");
                sb.Append("<b>仓位</b> ").Append(occ).Append('/').Append(s.EffCapacity)
                  .Append(" 载 · 剩 ").Append(Math.Max(0, s.EffCapacity - occ)).Append('\n');
                // 把上面那个"仓位"拆开给玩家看: 哪几载是货、哪几载是给养。
                //   为什么单起一行而不是接在"仓位"后面: 卡最窄时内宽只有 208(5 列时),
                //   "仓位 148/232 载 · 剩 84 · 货 120 + 给养 28" 那一串会折成两行 —— 一样占两行,
                //   还读不清。拆成两行反而每行都短, 且"120 + 28 = 148"上下对着能自己验算。
                sb.Append("<color=#8FA8C2>其中 货 ").Append(cargoW).Append(" (").Append(units)
                  .Append("件) + 给养 ").Append(provW).Append("</color>");
                if (kinds == 0)
                {
                    sb.Append("\n<color=#8FA8C2>(空舱待装)</color>");
                }
                else
                {
                    sb.Append('\n').Append("<color=#8FA8C2>");
                    int k = 0;
                    foreach (var kv in s.Cargo)
                    {
                        if (k >= 4) { sb.Append("…"); break; }
                        if (k > 0) sb.Append(", ");
                        Good gg; sb.Append(w.GoodById.TryGetValue(kv.Key, out gg) ? gg.Name : kv.Key).Append('×').Append(kv.Value);
                        k++;
                    }
                    sb.Append("</color>");
                }
                _shipStat[i].text = sb.ToString();
            }
            _fleetSum.text = FleetSumText();
        }

        string FleetSumText()
        {
            // 至多三行短句(矩形已 Truncate, 再多也绝不压到下栏按钮)
            var sb = new StringBuilder(160);
            int crew = play.fleet.TotalCrew();
            sb.Append("<b>全队合计</b> 水手 <color=#FFD76A>").Append(crew).Append("</color> · 士气 <color=")
              .Append(play.Morale < 50 ? "#FF7766" : "#BFE0B0").Append(">").Append((int)play.Morale).Append("</color>")
              .Append(" · 病号 ").Append(play.SickCount).Append(" · 货种 ").Append(HoldKinds()).Append(" 类");
            sb.Append('\n').Append("装货 <b>").Append(play.CargoWeight()).Append("</b>+给养 <b>")
              .Append(play.ProvWeight()).Append("</b> / 总载 <b>").Append(play.TotalCapacity())
              .Append("</b> · 剩 ").Append(Math.Max(0, play.FreeLoad()))
              .Append(" · 续航 <color=#FFD76A>").Append(play.EnduranceDays).Append("</color> 日");
            var t = play.cat.Tuning.Provision;
            float fm = (play.fleet.Bonus != null && play.fleet.Bonus.FoodUseMul > 0f) ? play.fleet.Bonus.FoodUseMul : 1f;
            sb.Append('\n').Append("给养够全队 食物 <b>").Append(SupplyDays(crew, play.fleet.Prov.Food, t["food"].PerCrewPerDay * fm))
              .Append("</b> · 水 <b>").Append(SupplyDays(crew, play.fleet.Prov.Water, t["water"].PerCrewPerDay))
              .Append("</b> · 茶 <b>").Append(SupplyDays(crew, play.fleet.Prov.Tea, t["tea"].PerCrewPerDay))
              .Append("</b> 日");
            return sb.ToString();
        }

        int ShipWeight(ShipState s)
        {
            int wsum = 0;
            foreach (var kv in s.Cargo)
            {
                Good gg; if (play.world.GoodById.TryGetValue(kv.Key, out gg)) wsum += kv.Value * gg.Weight;
            }
            return wsum;
        }

        int SupplyDays(int crew, float prov, float rate)
        {
            if (crew <= 0 || rate <= 0f) return 0;
            return (int)(prov / (crew * rate));
        }

        // =============================================================
        // 花金操作确认弹窗
        // =============================================================
        void BuildConfirm(Transform root)
        {
            _confirmGo = Panel("confirmOverlay", root, new Color(0f, 0f, 0f, 0.45f));
            RootRect(Rt(_confirmGo));
            // 卡片放宽加高(休整/补给说明好几行): 消息区加大、左对齐、下方留足按钮带 —— 文字不再压按钮
            var card = Panel("card", _confirmGo.transform, new Color(0.05f, 0.075f, 0.115f, 0.99f));
            RectAt(Rt(card), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0, 0), new Vector2(540, 320));
            var title = AddText(card.transform, "—— 确认操作 ——", 17, ColTitle, TextAnchor.MiddleCenter);
            RectAt(Rt(title.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -2), new Vector2(0, 26));
            _confirmMsg = AddText(card.transform, "", 14, ColTxt, TextAnchor.UpperLeft);   // 左对齐
            var cm = Rt(_confirmMsg.gameObject);
            cm.anchorMin = Vector2.zero; cm.anchorMax = Vector2.one;
            cm.offsetMin = new Vector2(36, 112); cm.offsetMax = new Vector2(-36, -50);
            _confirmCancel = MakeBtn(card.transform, "cancel", "取消", 16, new Color(0.22f, 0.26f, 0.30f), Color.white);
            _confirmOk = MakeBtn(card.transform, "ok", "确定", 16, ColGoldBtn, new Color(0.14f, 0.08f, 0.03f));
            RectAt(Rt(_confirmCancel.gameObject), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(-118f, 26), new Vector2(196, 44));
            RectAt(Rt(_confirmOk.gameObject), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(118f, 26), new Vector2(196, 44));
            _confirmCancel.onClick.AddListener(OnConfirmCancel);
            _confirmOk.onClick.AddListener(OnConfirmOk);
            _confirmGo.SetActive(false);
        }

        void ConfirmProvision(int nDays)
        {
            if (play.State != SeaPlay.Mode.Docked) { play.Banner = "补给给养得靠港 —— 现在在海上(航行或抛锚)不行, 先驶回一座城。"; return; }
            var lines = play.ProvisionLines(nDays);
            if (lines.Count == 0) { play.Banner = "给养已够, 暂无需补给。"; return; }
            var sb = new StringBuilder(360);
            int crew = play.fleet.TotalCrew();
            long cost = 0; float wt = 0f;
            foreach (var p in lines) { cost += p.Cost; wt += p.Weight; }
            sb.Append("<b><color=#FFD76A>出航给养 · 补足整队 ").Append(nDays).Append(" 日</color></b>\n");
            sb.Append("全队 <b>").Append(crew).Append("</b> 名水手 × ").Append(nDays)
              .Append(" 日, 三种给养各按人·日份补齐存量:\n");
            sb.Append("占载公式 = 水手 × 日 × (食0.03 + 水0.02 + 茶0.01 载) = <color=#FFD76A>0.06 载/人·日</color>\n");
            foreach (var p in lines)
                sb.Append("<size=12>· ").Append(p.Cn).Append(" +<b>").Append(p.Add.ToString("0.#"))
                  .Append("</b> 份 → 占 <b>").Append(p.Weight.ToString("0.#")).Append("</b> 载 · ")
                  .Append(Money(p.Cost)).Append(" 金</size>\n");
            sb.Append("\n<color=#FFD76A>本次购入共占约 <b>").Append(wt.ToString("0.#")).Append("</b> 载 · 花费 ")
              .Append(Money(cost)).Append("</color>\n<color=#8FA8C2>买完即吃即占仓位; 现存给养照常续用。</color>");
            AskConfirm(sb.ToString(), () => play.ProvisionTo(nDays), BackTo.Fleet);
        }

        void ConfirmRecruit()
        {
            if (play.State != SeaPlay.Mode.Docked) { play.Banner = "招募水手得靠港 —— 现在在海上(航行或抛锚)不行, 先驶回一座城。"; return; }
            long cost = play.RecruitCostToFull();
            if (cost < 0) { play.Banner = "招募水手得靠港 —— 现在在海上(航行或抛锚)不行, 先驶回一座城。"; return; }
            if (cost == 0) { play.Banner = "水手已经满编, 无需招募。"; return; }
            AskConfirm("<b><color=#FFD76A>招募水手补齐</color></b>\n在码头把全舰队水手招满(旗舰 + 僚舰)。\n预计花费: <color=#FFD76A>" + Money(cost) + "</color> 金。",
                () => play.RecruitCrewFull(), BackTo.Fleet);
        }

        // 半仓补员: 逐船补到半仓。**只补不裁** —— 已过半仓的船原样不动(玩家满编跑过远洋、
        //   现在想省给养, 可以自己按"裁"的那套来, 这个键不该替他把人赶下船, 那是不可逆的)。
        void ConfirmRecruitHalf()
        {
            if (play.State != SeaPlay.Mode.Docked) { play.Banner = "招募水手得靠港 —— 现在在海上(航行或抛锚)不行, 先驶回一座城。"; return; }
            long cost = play.RecruitCostToHalf();
            if (cost < 0) { play.Banner = "招募水手得靠港 —— 现在在海上(航行或抛锚)不行, 先驶回一座城。"; return; }
            if (cost == 0) { play.Banner = "各船水手都已过半仓, 无需补员。"; return; }
            AskConfirm("<b><color=#FFD76A>招募水手到半仓</color></b>\n逐船看: <color=#7FE0FF>不足半仓的补到半仓</color>, 已过半仓的<color=#8FA8C2>原样不动(不裁人)</color>。\n"
                + "半仓 = 该船满编的一半(且不低于该船最低配员); 近海短程够开, 又比满编省给养、省仓位。\n"
                + "预计花费: <color=#FFD76A>" + Money(cost) + "</color> 金。",
                () => play.RecruitCrewHalf(), BackTo.Fleet);
        }

        // 休整 10 日: 同"花金确认"流程 —— 先把"这会花掉什么/错过什么"讲清楚, 玩家点头才拨时间
        void ConfirmRest()
        {
            if (play.State != SeaPlay.Mode.Docked) { play.Banner = "休整得靠港 —— 现在不在港内(海上抛锚/航行), 先驶回一座城。"; return; }
            string port = play.Current != null ? play.Current.Name : "本港";
            AskConfirm("<b><color=#FFD76A>休整 10 日</color></b>\n让全舰队在 " + port + " 停靠休整, 把日子拨快 10 天:\n"
                + "· 这 10 天各地行情 / 库存照常流转 ——\n  可能错过正在涨的差价窗口, 也可能赶上新到货;\n"
                + "· 休整本身 <color=#BFE0B0>不花金币、也不吃给养</color>;\n"
                + "· 水手工资按到港结薪, 与休整天数无关。\n\n确认后执行, 之后仍停在港内可继续买卖。",
                () => play.RestDays(10));
        }

        // 确认弹窗是**从哪一层**弹出来的 —— 确认/取消之后要回到那一层去。
        //   用户报的病例: 舰队页里点「补给 15 日」→ 确定 → 行情/舰队/船坞全收了, 人停在光秃秃的
        //   海图上; 想看补给后的仓位得自己再点一次「⚓ 舰队」。可这道确认本就是舰队页的**子对话**,
        //   关掉子对话回父页才是常理 —— 不回去等于"点一下按钮就把脚下的地板抽了"。
        //   (休整 10 日是从底栏点的, 底栏一直在, 所以它仍旧 BackTo.None。)
        enum BackTo { None, Fleet }
        BackTo _confirmBack;

        void AskConfirm(string msg, System.Action ok, BackTo back = BackTo.None)
        {
            FoldFloats();   // 确认弹窗也是"窗": 先把行情/舰队/船坞/情报/出航检查收掉, 干净地只叠一层
            _confirmMsg.text = msg;
            _confirmAction = ok;
            _confirmBack = back;
            _confirmGo.SetActive(true);
        }
        void OnConfirmOk()
        {
            var a = _confirmAction;
            _confirmGo.SetActive(false);
            _confirmAction = null;
            if (a != null) a();
            ApplyConfirmBack();
        }
        void OnConfirmCancel()
        {
            _confirmGo.SetActive(false);
            _confirmAction = null;
            ApplyConfirmBack();
        }
        // 回到"弹这道确认的那一层"。放在动作**之后**跑: 动作里若又弹了别的层(目前没有),
        //   也不该被这里盖掉 —— 谁最后说话谁算。
        void ApplyConfirmBack()
        {
            var b = _confirmBack; _confirmBack = BackTo.None;
            if (b == BackTo.Fleet) _fleetOpen = true;
        }

        // =============================================================
        // 出航前检查: 水手 / 给养 vs 航程 / 货舱清单 → 玩家确认才真正 Depart
        // =============================================================
        void BuildDepart(Transform root)
        {
            _departGo = Panel("departOverlay", root, new Color(0f, 0f, 0f, 0.55f));
            RootRect(Rt(_departGo));
            var card = Panel("card", _departGo.transform, new Color(0.05f, 0.075f, 0.115f, 0.99f));
            RectAt(Rt(card), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0, 10), new Vector2(660, 500));
            var head = AddText(card.transform, "—— 出航前检查 · 确认即起锚 ——", 18, ColTitle, TextAnchor.MiddleCenter);
            RectAt(Rt(head.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -2), new Vector2(0, 28));
            _departBody = AddText(card.transform, "", 15, ColTxt, TextAnchor.UpperLeft);
            var db = Rt(_departBody.gameObject);
            db.anchorMin = Vector2.zero; db.anchorMax = Vector2.one;
            db.offsetMin = new Vector2(30, 84); db.offsetMax = new Vector2(-30, -42);
            var cancel = MakeBtn(card.transform, "cancel", "先不出发", 16, new Color(0.22f, 0.26f, 0.30f), Color.white);
            var go = MakeBtn(card.transform, "go", "出航 ▶", 19, ColGoldBtn, new Color(0.15f, 0.09f, 0.04f));
            RectAt(Rt(cancel.gameObject), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(-120f, 30), new Vector2(220, 44));
            RectAt(Rt(go.gameObject), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(120f, 30), new Vector2(220, 44));
            cancel.onClick.AddListener(() => _departOpen = false);
            go.onClick.AddListener(DepartConfirmed);
            _departGo.SetActive(false);
        }

        void OpenDepartCheck()
        {
            if (play == null || play.State == SeaPlay.Mode.Sailing || !play.HasSailTarget) return;
            FoldFloats(true);   // 出航检查当最高层, 行情/舰队/船坞/情报站/确认 全收掉, 不重叠
            _departBody.text = DepartReviewText();
            _departOpen = true;
        }

        void DepartConfirmed()
        {
            _departOpen = false;
            play.Depart();
        }

        string DepartReviewText()
        {
            if (play.fleet == null || !play.HasSailTarget) return "";
            bool fromPort = play.State == SeaPlay.Mode.Docked && play.Current != null;
            string fromName = fromPort ? play.Current.Name : "海上锚地(🚩)";
            bool toPort = play.Dest != null;
            string toName = toPort ? play.Dest.Name + " · " + play.Dest.Area.Name : "海图 🚩 目标点(抛锚)";
            int days = play.TargetDays;
            int crew = play.TotalSailors;
            int cmin = FleetCrewMin(), cmax = FleetCrewMax();
            int endu = play.EnduranceDays;
            var sb = new StringBuilder(360);
            sb.Append("<size=14><color=#FFD76A>").Append(fromName).Append("  →  ").Append(toName)
              .Append("   · 约 ").Append(days).Append(" 日航程</color></size>\n");
            sb.Append("<b>水手</b> ").Append(crew).Append(" 人 · 全队下限 ").Append(cmin).Append(" · 满载 ").Append(cmax);
            sb.Append(crew < cmin ? "   <color=#FF7766>⚠ 低于下限, 开船吃力</color>\n" : "   <color=#BFE0B0>✓ 够开船</color>\n");
            sb.Append("<b>给养续航</b> 全队可撑 <color=#FFD76A>").Append(endu).Append("</color> 日");
            sb.Append(endu < days ? "   <color=#FF7766>⚠ 不足 " + days + " 日, 途中会断粮减员!</color>\n"
                                  : "   <color=#BFE0B0>✓ 够撑到目的地</color>\n");
            sb.Append("<b>货舱</b> 装货 ").Append(play.CargoWeight()).Append(" + 给养 ").Append(play.ProvWeight())
              .Append(" / 总载 ").Append(play.TotalCapacity()).Append("  (空舱位 ").Append(Math.Max(0, play.FreeLoad())).Append(")\n");
            // 逐样提醒船上货物
            int k = 0;
            foreach (var g in play.world.Goods)
            {
                int have = play.GoodInHold(g.Id);
                if (have <= 0) continue;
                if (k >= 6) { sb.Append("<color=#9FB6CF>  …等其余货物(详见 ⚓舰队)。</color>"); k++; break; }
                if (k > 0) sb.Append('\n');
                sb.Append("  <color=#CFE0FF>· ").Append(g.Name).Append(" ×").Append(have).Append("</color>");
                k++;
            }
            if (k == 0) sb.Append("  (空舱出航)");
            sb.Append("\n\n<color=#8FA8C2>确认后即起锚。航行中按天吃给养、到港才结工资; 断粮会士气崩、人也会没。");
            if (!toPort) sb.Append("\n🚩 这趟不去城: 到点只抛锚停船, 想买卖/补给得再驶回一座城。");
            sb.Append("</color>");
            return sb.ToString();
        }

        int FleetCrewMin()
        {
            int s = 0; foreach (var sh in play.fleet.Ships) s += sh.Model.CrewMin; return s;
        }
        int FleetCrewMax()
        {
            int s = 0; foreach (var sh in play.fleet.Ships) s += sh.Model.CrewMax; return s;
        }

        // =============================================================
        // 航行日志: 滚动视窗 + 滑块 + 日期分组(可回翻看"某天做了哪些事")
        // =============================================================
        // -------------------------------------------------------------
        // 右侧卡片(航行日志 / 舰队货仓)共用的"可滚动"装配
        // -------------------------------------------------------------
        sealed class Scroller
        {
            public ScrollRect Scroll;
            public RectTransform Viewport, Content, Track, Thumb;
        }

        // 把一张右缘卡片变成"滚轮可翻、拖拽可翻、带滚动条"的面板。
        //   card  —— 外卡(ScrollRect 挂在它身上, 见下)
        //   id    —— 子物体命名前缀
        //   pad   —— 视窗相对卡片的四边内缩 (左, 下, 右, 上); 右边要留出滚动条的位置
        //
        // ScrollRect 挂**外卡**而不是视窗 —— 这是本方法唯一值得解释的一句。
        //   滚轮事件由 EventSystem 从"指针压住的那个 raycast 目标"往上找第一个 IScrollHandler。
        //   原来挂在视窗上时, 指针只要落在视窗之外、卡片之内(标题条 "航行日志" 那 28px、
        //   四周那几像素边距、滚动条左侧的空隙), 就一路找到 Canvas 也没人接 —— 那一圈白滚。
        //   玩家看到的现象正是"这日志滚不动"(其实只是大半张卡滚不动)。
        //   挂卡片 = 整张卡哪儿都能滚; 裁剪照旧由视窗的 Mask 负责(viewport 仍然指向它)。
        Scroller MakeScroller(RectTransform card, string id, Vector4 pad)
        {
            var sc = new Scroller();

            var vpGo = Panel(id + "_vp", card, new Color(0, 0, 0, 0.22f));   // 内深遮罩
            var vp = Rt(vpGo);
            vp.anchorMin = Vector2.zero; vp.anchorMax = Vector2.one;
            vp.offsetMin = new Vector2(pad.x, pad.y);
            vp.offsetMax = new Vector2(-pad.z, -pad.w);
            vpGo.AddComponent<Mask>().showMaskGraphic = false;

            var contentGo = NewRT(id + "_content", vpGo.transform);
            var content = Rt(contentGo);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0, 40);

            var scroll = card.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28f;
            scroll.viewport = vp;
            scroll.content = content;

            // 滚动条: 轨道 + 滑块(拖滑块翻页; 滚轮悬在这张卡上任意处都能翻)
            sc.Track = Rt(Panel(id + "_bar", card, new Color(0f, 0f, 0f, 0.5f)));
            sc.Track.anchorMin = new Vector2(1, 0); sc.Track.anchorMax = new Vector2(1, 1);
            sc.Track.offsetMin = new Vector2(-17, 32); sc.Track.offsetMax = new Vector2(-7, -32);
            sc.Thumb = Rt(Panel(id + "_knob", sc.Track, new Color(0.62f, 0.74f, 0.86f, 0.95f)));
            var th = sc.Thumb.gameObject.AddComponent<SeaScrollThumb>();
            th.scroll = scroll; th.track = sc.Track; th.thumb = sc.Thumb;
            sc.Thumb.gameObject.SetActive(false);

            sc.Scroll = scroll; sc.Viewport = vp; sc.Content = content;
            return sc;
        }

        // 按当前位置摆滑块; 内容不够高(没得滚)就把整条滚动条藏掉
        void UpdateThumb(Scroller sc)
        {
            if (sc == null || sc.Scroll == null || sc.Thumb == null || sc.Track == null) return;
            float vpH = sc.Scroll.viewport.rect.height;
            float cH = sc.Scroll.content.rect.height;
            if (cH <= vpH + 1f) { sc.Thumb.gameObject.SetActive(false); return; }
            sc.Thumb.gameObject.SetActive(true);

            float trackH = sc.Track.rect.height;
            float frac = Mathf.Clamp(vpH / cH, 0.06f, 1f);
            float thumbH = Mathf.Max(18f, frac * trackH);   // 18 = 还能抓住的最小高度
            float v = Mathf.Clamp01(sc.Scroll.verticalNormalizedPosition);
            var rt = sc.Thumb;
            // 底边锚定 + 位移, 不要写成"上下各给一个锚点"。
            //   锚点写法 (anchorMin.y=v, anchorMax.y=v+frac) 在 v=1 时两个锚点重合 →
            //   高度算成 0, 滑块整个凭空消失; 而"卷到顶"恰恰是货仓卡每次开铺的默认位置
            //   (见 Update 里那条开铺回顶), 于是那张卡的滑块永远看不见。
            //   位移写法在 v=0 / v=1 两头都成立。
            rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(-4f, thumbH);        // 横向 = 轨道宽 - 4(左右各留 2)
            rt.anchoredPosition = new Vector2(0f, v * (trackH - thumbH));
        }

        // [闸门] 右侧两张卡(日志 / 货仓)的可滚动状态 —— 内容多高、视窗多高、滑块露没露、停在哪。
        //   截图里轨道只有十来个像素宽, 滑块有没有"肉眼判"不稳(上一版就是因为判错了才漏掉
        //   "卷到顶时滑块高度算成 0"这个 bug); 这行数字是它的纸面证据。
        public string QaScrollState()
        {
            return "日志[" + CardScrollText(_logSc) + "] 货仓[" + CardScrollText(_holdSc) + "]";
        }

        static string CardScrollText(Scroller sc)
        {
            if (sc == null || sc.Scroll == null) return "未建";
            float vpH = sc.Scroll.viewport != null ? sc.Scroll.viewport.rect.height : -1f;
            float cH = sc.Scroll.content != null ? sc.Scroll.content.rect.height : -1f;
            bool thumb = sc.Thumb != null && sc.Thumb.gameObject.activeSelf;
            return "内容" + cH.ToString("F0") + "/视窗" + vpH.ToString("F0")
                + (cH > vpH + 1f ? " 可滚" : " 不用滚")
                + " 滑块" + (thumb ? "露·高" + sc.Thumb.rect.height.ToString("F0") : "藏")
                + " 位" + sc.Scroll.verticalNormalizedPosition.ToString("F2");
        }

        // [闸门] 航行日志的滚动: 灌满 → 往上翻 → 看它**待不待得住**。
        //   这张卡每帧都有一条"玩家没往回翻就把视图钉到最新"的规则(见 RefreshLog), 所以这里
        //   最该证伪的坏法不是"滚不动", 而是"刚翻上去、下一帧又被钉回底部" —— 同帧里设一次再读
        //   一次是看不出这个的, 只有分帧读两次位置才戳得穿。
        //   为什么非要灌 40 条: 日常跑图那点日志量只有 235/视窗 252, 压根不溢出 —— 不灌就测不到。
        int _qaLogStage = -1;
        float _qaLogStageAt;

        public void QaLogScrollTick()
        {
            if (_logSc == null || _logSc.Scroll == null) return;
            // 头一次调用只起表(与 QaMarketScrollTick 同理: 否则第 0 步会和起表同帧发生, 白等一轮)
            if (_qaLogStage < 0) { _qaLogStage = 0; _qaLogStageAt = Time.unscaledTime; return; }
            if (_qaLogStage > 4) return;
            if (Time.unscaledTime - _qaLogStageAt < 0.10f) return;
            _qaLogStageAt = Time.unscaledTime;
            var scroll = _logSc.Scroll;
            switch (_qaLogStage)
            {
                case 0:
                    for (int i = 0; i < 40; i++)
                        play.PushLog("闸门灌日志 " + (i + 1) + "/40 —— 只为把这张卡撑到溢出");
                    // 这里**故意不报** CardScrollText: content 高度要等 RefreshLog 重排完文字才长出来,
                    //   同帧读到的是灌之前那 235 —— 会印出一句自相矛盾的"灌完 40 条…不用滚"。下一步再报。
                    break;
                case 1:
                    Debug.Log("[SEA] 日志滚动: 灌完 40 条后 " + CardScrollText(_logSc)
                        + " · 默认位 " + scroll.verticalNormalizedPosition.ToString("F3")
                        + " (该是 0.000 = 贴最新那条)");
                    break;
                case 2:
                    scroll.verticalNormalizedPosition = 1f;       // 玩家往上翻历史
                    break;
                case 3:
                    Debug.Log("[SEA] 日志滚动: 翻到顶之后 pos=" + scroll.verticalNormalizedPosition.ToString("F3")
                        + (scroll.verticalNormalizedPosition > 0.99f
                            ? "  <== 待住了(没被每帧钉回底部)" : "  <== 被钉回去了!"));
                    scroll.verticalNormalizedPosition = 0.5f;     // 停中段, 好让截图拍下"确实翻上去了"
                    break;
                default:
                    Debug.Log("[SEA] 日志滚动: 中段停稳 pos=" + scroll.verticalNormalizedPosition.ToString("F3")
                        + " " + CardScrollText(_logSc));
                    _qaLogStage = 9;
                    return;
            }
            _qaLogStage++;
        }

        void BuildLog(Transform root)
        {
            var card = Panel("card_log", root, ColPanel);
            RectAt(Rt(card), new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(-10, -86), new Vector2(360, 286));
            _logRt = Rt(card);   // 地名标签 keep-out: 右侧日志永不被地名压住
            AddHead(card.transform, "航行日志");

            _logSc = MakeScroller(Rt(card), "log", new Vector4(6f, 6f, 20f, 28f));

            // 字号 12 → 11: 右侧这两张卡是"查阅用"的, 一屏多塞几行比字大更要紧
            //   (配合滚轮翻看, 见 MakeScroller)。
            _logText = AddText(_logSc.Content, "", 11, ColLog, TextAnchor.UpperLeft);
            var lt = Rt(_logText.gameObject);
            lt.anchorMin = Vector2.zero; lt.anchorMax = Vector2.one;
            lt.offsetMin = new Vector2(2, 2); lt.offsetMax = new Vector2(-2, -2);
            _logText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _logText.verticalOverflow = VerticalWrapMode.Overflow;
        }

        void RefreshLog()
        {
            if (_logSc == null || _logSc.Scroll == null) return;
            var scroll = _logSc.Scroll;
            int n = play.Log.Count;
            if (n == 0)
            {
                if (_logVersion != 0)
                {
                    _logVersion = 0;
                    _logText.text = "……还没有航行日志。\n出海后这里会记下每天的动作,\n可滚动、按日期翻看。";
                    _logSc.Content.sizeDelta = new Vector2(0, _logText.preferredHeight + 8f);
                }
                UpdateThumb(_logSc);
                return;
            }
            if (play.LogVersion != _logVersion)
            {
                bool keepBottom = _logPinned;   // 玩家正往回翻历史时, 新记录来了不打断
                _logVersion = play.LogVersion;
                var sb = new StringBuilder(512);
                int prevDay = int.MinValue;
                for (int i = n - 1; i >= 0; i--)   // 旧 → 新, 新的在底部
                {
                    int d = i < play.LogDay.Count ? play.LogDay[i] : -1;
                    if (d != prevDay)
                    {
                        if (i != n - 1) sb.Append('\n');   // 两组日期之间空一行
                        prevDay = d;
                        sb.Append("<size=10><color=#FFD76A>—— ").Append(DateOf(d)).Append(" ——</color></size>\n");
                    }
                    sb.Append(play.Log[i]).Append('\n');
                }
                _logText.text = sb.ToString();
                float ph = _logText.preferredHeight + 6f;
                _logSc.Content.sizeDelta = new Vector2(0, Mathf.Max(40f, ph));
                if (keepBottom) scroll.verticalNormalizedPosition = 0f;   // 贴到最新
            }
            // _logPinned 每帧从**当前位置**重算, 所以拖滑块(SeaScrollThumb)不用自己回报 ——
            //   那一帧翻到哪, 下一帧这里就读到哪。
            _logPinned = scroll.verticalNormalizedPosition <= 0.02f;
            // 只要玩家没刻意往回翻, 每帧都把视图钉在最"新"(底部) —— 新记录一到就自动显示最新一条
            if (_logPinned && scroll.verticalNormalizedPosition > 0f)
                scroll.verticalNormalizedPosition = 0f;
            UpdateThumb(_logSc);
        }

        static string DateOf(int d)
        {
            if (d < 0) return "……更早 ……";
            int y = SimEngine.StartYear + d / SimEngine.DaysPerYear;
            int m = (d % SimEngine.DaysPerYear) / 30 + 1;
            int dd = d % 30 + 1;
            return y + " 年 " + m + " 月 " + dd + " 日";
        }

        // =============================================================
        // 行情表单元格助手
        // =============================================================
        Text RowCell(Transform row, string s, int size, Color col, TextAnchor align, float x, float w)
        {
            var t = AddText(row, s, size, col, align);
            var r = Rt(t.gameObject);
            r.anchorMin = new Vector2(0, 1); r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, 0); r.sizeDelta = new Vector2(w, 22);
            return t;
        }

        void HeaderCell(Transform parent, string s, float x, float w, TextAnchor align, int size, Color col)
        {
            var t = AddText(parent, s, size, col, align);
            var r = Rt(t.gameObject);
            r.anchorMin = new Vector2(0, 1); r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, -37); r.sizeDelta = new Vector2(w, 24);
        }

        void PlaceRowButton(Button b, float x)
        {
            var r = Rt(b.gameObject);
            r.anchorMin = new Vector2(0, 1); r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, 1); r.sizeDelta = new Vector2(54, 20);
        }

        Text BuildCard(Transform root, string name, float w, float h, string tname,
            int size, Color col, TextAnchor align, out GameObject go)
        {
            go = Panel(name, root, ColPanel);
            var t = AddText(go.transform, "", size, col, align);
            return t;
        }

        // 右上: 试炼卡(黑框与航行日志同款; 蹲在日志正上方 x910..1270, 上下留间距)
        void BuildGoal(Transform root)
        {
            var card = Panel("goal", root.transform, ColPanel);
            RectAt(Rt(card), new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-10, -6), new Vector2(360, 74));   // y6..80; 底与日志顶(y86)留 6
            _goalRt = Rt(card);   // 地名标签 keep-out: 右上试炼卡

            _goalTitle = AddText(card.transform, "", 13, ColGold, TextAnchor.MiddleCenter);
            RectAt(Rt(_goalTitle.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                new Vector2(0, -10), new Vector2(0, 18));
            _goalTitle.horizontalOverflow = HorizontalWrapMode.Overflow;   // 窄卡内不换行、不出框
            _goalSub = AddText(card.transform, "", 12, ColTxt, TextAnchor.MiddleCenter);
            RectAt(Rt(_goalSub.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                new Vector2(0, -34), new Vector2(0, 16));
            _goalSub.horizontalOverflow = HorizontalWrapMode.Overflow;

            var track = Panel("track", card.transform, new Color(0, 0, 0, 0.6f));
            RectAt(Rt(track), new Vector2(0.05f, 0), new Vector2(0.95f, 0), new Vector2(0.5f, 0),
                new Vector2(0, 6), new Vector2(0, 5));
            var fill = NewRT("fill", track.transform);
            fill.AddComponent<Image>().sprite = White();
            _goalFill = fill.GetComponent<Image>();
            _goalFill.color = new Color(0.35f, 0.85f, 0.45f);
            var fr = Rt(fill);
            fr.anchorMin = new Vector2(0, 0); fr.anchorMax = new Vector2(0, 1);
            fr.pivot = new Vector2(0, 0.5f);
            fr.anchoredPosition = new Vector2(0, 0);
            fr.sizeDelta = new Vector2(0, 0);
        }

        void AddHead(Transform panel, string cap)
        {
            var t = AddText(panel, "—— " + cap + " ——", 13, new Color(0.98f, 0.78f, 0.40f), TextAnchor.MiddleCenter);
            RectAt(Rt(t.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -2), new Vector2(0, 18));
        }

        // 泊港操作条
        void BuildDock(Transform panel)
        {
            var guide = AddText(panel, "", 15, new Color(0.92f, 0.96f, 1f), TextAnchor.MiddleCenter);
            // 引导语与黑框顶缘留出 9px 呼吸边距(不再贴边)
            RectAt(Rt(guide.gameObject), new Vector2(0.02f, 1), new Vector2(0.98f, 1), new Vector2(0.5f, 1),
                new Vector2(0, -9), new Vector2(0, 22));

            // 一行按钮, 居中排列(招募水手/买补给已收进「⚓ 舰队」面板; 买/卖船、改装在「🛠 船坞」)
            var def = new[]
            {
                new[] { "depart", "出航 ▶", "g" },
                new[] { "market", "本港行情", "" },
                new[] { "fleet",  "⚓ 舰队", "" },
                new[] { "yard",   "🛠 船坞", "" },
                new[] { "rest",   "休整10日", "" },
                new[] { "intel",  "📰 情报站", "" },
                new[] { "world",  "世界全景", "" },
            };
            float[] wd = { 158, 118, 104, 116, 104, 118, 112 };
            float gap = 8f; float total = 0f;
            for (int i = 0; i < wd.Length; i++) total += wd[i] + gap; total -= gap;
            float x = -total * 0.5f;

            for (int i = 0; i < def.Length; i++)
            {
                bool gold = def[i][2] == "g";
                var b = MakeBtn(panel, "btn_" + def[i][0], def[i][1], gold ? 18 : 15,
                    gold ? ColGoldBtn : ColBtn, gold ? new Color(0.12f, 0.07f, 0.03f) : Color.white);
                RectAt(Rt(b.gameObject), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                    new Vector2(x + wd[i] * 0.5f, 10), new Vector2(wd[i], 40));
                _dock[def[i][0]] = b;
                x += wd[i] + gap;
            }

            _dock["depart"].onClick.AddListener(OpenDepartCheck);   // 出航前先弹"检查确认", 确认后才 Depart
            _dock["market"].onClick.AddListener(ToggleMarket);
            _dock["fleet"].onClick.AddListener(ToggleFleet);
            _dock["yard"].onClick.AddListener(ToggleYard);
            _dock["intel"].onClick.AddListener(ToggleIntel);
            _dock["rest"].onClick.AddListener(ConfirmRest);
            _dock["world"].onClick.AddListener(() => play.ViewWorld());

            // 「⚙ 设置」放本条右下角(底栏已拉宽到近屏边; 同一黑底按钮行, 高度对齐其他动作钮)
            var gear = MakeBtn(panel, "set", "⚙ 设置", 15, ColBtn, Color.white);
            RectAt(Rt(gear.gameObject), new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(-12, 10), new Vector2(116, 40));
            gear.onClick.AddListener(OpenSettings);
        }

        void OpenSettings()   // 底部两操作条里的「⚙ 设置」共用: 调起同一物体上的设置菜单
        {
            FoldFloats(true);   // 设置是全屏弹窗: 先收掉下面的浮层, 不重叠显示
            var st = GetComponent<SeaSettings>();
            if (st != null) st.OpenMenu();
        }

        // ---- 浮层互斥: 底部菜单任一新窗打开, 先把其它全部收掉, 绝不两个弹窗叠着 ----
        void FoldFloats(bool alsoConfirm = false)
        {
            _tradeOpen = false; _fleetOpen = false; _yardOpen = false; _newsOpen = false; _departOpen = false;
            if (alsoConfirm && _confirmGo != null)
            {
                _confirmGo.SetActive(false); _confirmAction = null;
                _confirmBack = BackTo.None;   // 对话框被强行收掉(不是玩家点的确定/取消) → 别留着"回哪一层"的念头
            }
        }

        void ToggleMarket()
        {
            if (_tradeOpen) { FoldFloats(true); return; }   // 再点一下收铺
            FoldFloats(true);
            _tradeOpen = true;
        }

        // =============================================================
        // [闸门] 「行情每次打开都回到数据第一行」的取证 —— 玩家报的是"关掉再打开, 它还停在
        //   上次翻到的地方"。这里走玩家那条路(_tradeOpen 翻掉; 面板的 SetActive 与闭→开那条
        //   边沿都在 Update 里, 一处不绕), 只把"翻到底"这一步替玩家做了。
        //
        //   为什么非要**分帧**: 边沿判定看的是"上一帧开着没有"。同一帧里关掉再打开 = 压根没有
        //   边沿, 测出来必然"没回顶" —— 那是假失败, 会把一个对的实现判成错的。所以每步之间留
        //   0.10s(≈6 帧), 让 Update 真跑过几轮; 五步合计 0.5s, 在那一格(0.8s)里跑完, 快门按下
        //   时铺已经开在顶上 —— 图与日志说的才是同一时刻的事。
        //   为什么中间(s2)还要报一次"翻过之后停在底部": 反证 RefreshTrade 没有每帧把表钉回顶。
        //   真钉了的话玩家压根滚不动这张 20 行的表(船坞那份短表才敢每帧钉, 见 RefreshYard) ——
        //   那是这一头的坏法, 与"不回顶"正好相反, 两头的证据都得留。
        // =============================================================
        // 按快门前探一次(与那张图同一时刻): 图里看到的那几行, 与这行 pos 得对得上。
        public string QaMarketScrollState()
        {
            if (_tradeScroll == null) return "scroll=null";
            return "pos=" + _tradeScroll.verticalNormalizedPosition.ToString("F3")
                + " 表开着=" + (_tradeGo != null && _tradeGo.activeSelf)
                + " 标题=" + (_tradeTitle != null ? _tradeTitle.text : "?");
        }

        int _qaScrollStage = -1;      // -1 没跑; 0..4 走到第几步; >4 跑完
        float _qaScrollStageAt;
        public void QaMarketScrollTick()
        {
            if (_tradeScroll == null) return;
            // 头一次调用只是**起表**(把计时起点定下来), 不做事 —— 否则第一步会与起表同帧发生,
            //   那 0.10s 的节流就从"起表那一刻"开始算了。
            if (_qaScrollStage < 0) { _qaScrollStage = 0; _qaScrollStageAt = Time.unscaledTime; return; }
            if (_qaScrollStage > 4) return;
            if (Time.unscaledTime - _qaScrollStageAt < 0.10f) return;
            _qaScrollStageAt = Time.unscaledTime;
            switch (_qaScrollStage)
            {
                case 0:
                    QaClosePanels();    // 让开舰队页(上一张图开着, 不收掉它盖在行情上头)
                    QaOpenMarket();
                    break;
                case 1:
                    _tradeScroll.verticalNormalizedPosition = 0f;   // 替玩家"翻到最下面"
                    break;
                case 2:
                    Debug.Log("[SEA] 行情滚动: 翻到底之后 pos="
                        + _tradeScroll.verticalNormalizedPosition.ToString("F3") + " (该还是 0.000)");
                    QaClosePanels();    // 收铺
                    break;
                case 3:
                    QaOpenMarket();     // 重新开铺 —— 该自己回到第一行
                    break;
                default:
                    float p = _tradeScroll.verticalNormalizedPosition;
                    Debug.Log("[SEA] 行情滚动: 收铺重开后 pos=" + p.ToString("F3")
                        + (p > 0.999f ? "  <== 回了数据第一行" : "  <== 没回顶!"));
                    _qaScrollStage = 9; // 测完, 别再走一遍
                    return;
            }
            _qaScrollStage++;
        }

        // 把行情表卷回**数据第一行**(表头在滚动区之外, 本来就一直看得见, 不用管它)。
        //   为什么是"开铺那一下"而不是每帧: RefreshTrade 每帧都跑, 在里面摆平 = 每帧钉回顶,
        //   玩家压根滚不动这张 20 行的表(船坞那份清单是短表, 才敢那么干, 见 RefreshYard)。
        //   为什么用 normalizedPosition: ScrollRect 的 setter 自己会 EnsureLayoutHasRebuilt
        //   (必要时 ForceUpdateCanvases), 所以面板刚 SetActive(true) 的同一帧里设也算数 ——
        //   开铺那一帧正是调用点。y=1 是顶端(content 的 pivot 在上, anchoredPosition 0 = 顶)。
        void ScrollMarketTop()
        {
            if (_tradeScroll != null) _tradeScroll.verticalNormalizedPosition = 1f;
        }

        void ToggleIntel()
        {
            if (_newsOpen) { FoldFloats(true); return; }   // 再点一下收铺
            FoldFloats(true);
            _newsOpen = true;
            ShowNewsHome();   // 开铺默认落在"大厅"
        }

        void ToggleFleet()   // 浮层互斥: 舰队 ↔ 行情/船坞/情报站/出航检查 不同开
        {
            if (_fleetOpen) { FoldFloats(true); return; }
            FoldFloats(true);
            _fleetOpen = true;
        }

        void ToggleYard()
        {
            if (_yardOpen) { FoldFloats(true); return; }
            FoldFloats(true);
            _yardOpen = true;
            _yardSel = null;
        }

        // =============================================================
        // 船坞浮层: 买新船 / 卖旧船 / 改装(改良桅帆提速 · 增配火炮防盗 · 装甲 · 货舱)
        //   左栏 = 我的船队(点一行选中, 下栏做操作); 右栏 = 船坞可购的新船清单
        // =============================================================
        void BuildYard(Transform root)
        {
            _yardGo = Panel("yard", root, new Color(0.028f, 0.05f, 0.085f, 0.97f));
            RectAt(Rt(_yardGo), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0, -4), new Vector2(1120, 540));   // 拉宽: 右栏可购船行要放 2:1 船图占位

            _yardTitle = AddText(_yardGo.transform, "", 18, ColTitle, TextAnchor.MiddleCenter);
            RectAt(Rt(_yardTitle.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -2), new Vector2(0, 26));
            var xb = MakeBtn(_yardGo.transform, "close", "✕", 16, ColBtn, Color.white);
            RectAt(Rt(xb.gameObject), new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(-6, -2), new Vector2(30, 26));
            xb.onClick.AddListener(() => _yardOpen = false);

            _yardCash = AddText(_yardGo.transform, "", 12, ColDim, TextAnchor.UpperLeft);
            RectAt(Rt(_yardCash.gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(16, -36), new Vector2(430, 20));

            // 左栏: 我的船队
            var lh = AddText(_yardGo.transform, "◆ 我的船队 · 点一行选中 → 下栏 任旗 / 出售 / 改装", 12, ColDim, TextAnchor.UpperLeft);
            RectAt(Rt(lh.gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(16, -62), new Vector2(430, 20));
            for (int i = 0; i < 5; i++) BuildYardOwnRow(i);

            // 左栏底部操作区(底色先画, 文字/按钮排其上)
            var act = Panel("actbg", _yardGo.transform, new Color(0, 0, 0, 0.22f));
            RectAt(Rt(act), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(16, -396), new Vector2(428, 140));

            _yardActName = AddText(_yardGo.transform, "", 12, ColTxt, TextAnchor.UpperLeft);
            RectAt(Rt(_yardActName.gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(24, -402), new Vector2(412, 42));

            _yardFlagSel = MakeBtn(_yardGo.transform, "flagS", "⭐ 立为旗舰", 13, ColGoldBtn, new Color(0.14f, 0.08f, 0.03f));
            RectAt(Rt(_yardFlagSel.gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(24, -446), new Vector2(180, 38));
            _yardFlagSel.onClick.AddListener(() => { if (_yardSel != null) play.MakeFlagship(_yardSel); });

            _yardSellSel = MakeBtn(_yardGo.transform, "sellS", "💰 出售此船", 13, ColBtn, Color.white);
            RectAt(Rt(_yardSellSel.gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(212, -446), new Vector2(224, 38));
            _yardSellSel.onClick.AddListener(() =>
            {
                if (_yardSel != null) { if (play.SellShip(_yardSel)) _yardSel = null; }
            });

            var kinds = new[] { RefitKind.Speed, RefitKind.Fire, RefitKind.Armor, RefitKind.Capacity };
            float[] rx = { 24f, 130f, 236f, 342f };
            for (int k = 0; k < 4; k++)
            {
                int kk = k;
                _yardRefit[k] = MakeBtn(_yardGo.transform, "refit" + k, "", 12, ColBtn, Color.white);
                RectAt(Rt(_yardRefit[k].gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                    new Vector2(rx[k], -488), new Vector2(100, 44));
                _yardRefit[k].onClick.AddListener(() => { if (_yardSel != null) play.DoRefit(_yardSel, kinds[kk]); });
            }

            // 右栏: 可购新船清单(随面板拉宽到 1120, 行内给 2:1 船图让位)
            var rh = AddText(_yardGo.transform, "◆ 本港船坞 · 可购新船(灰/红字 = 暂不能买, 附原因)", 12, ColDim, TextAnchor.UpperLeft);
            RectAt(Rt(rh.gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(470, -62), new Vector2(632, 20));

            var vpGo = Panel("vp", _yardGo.transform, new Color(0, 0, 0, 0.22f));
            RectAt(Rt(vpGo), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(470, -88), new Vector2(632, 444));
            vpGo.AddComponent<Mask>().showMaskGraphic = false;
            _yardScroll = vpGo.AddComponent<ScrollRect>();
            _yardScroll.horizontal = false; _yardScroll.vertical = true;
            _yardScroll.movementType = ScrollRect.MovementType.Clamped;
            _yardScroll.scrollSensitivity = 24f;
            var contentGo = NewRT("content", vpGo.transform);
            var content = Rt(contentGo);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0, 40);
            _yardScroll.viewport = Rt(vpGo);
            _yardScroll.content = content;
            _yardContent = content;

            _yardGo.SetActive(false);
        }

        // 在役船一行: 左侧 2:1 船图占位 + 名牌/参数; 整条可点选(任旗/出售走下方操作条, 行内不放重复钮)
        void BuildYardOwnRow(int i)
        {
            var row = NewRT("own" + i, _yardGo.transform);
            var rr = Rt(row);
            rr.anchorMin = new Vector2(0, 1); rr.anchorMax = new Vector2(0, 1); rr.pivot = new Vector2(0, 1);
            rr.anchoredPosition = new Vector2(16, -90 - i * 62);
            rr.sizeDelta = new Vector2(428, 58);

            // 2:1 船图占位(外框 + 深色内底 + 小字提示; 未来换成该船真图)
            float picH = rr.rect.height - 8f;
            float picW = picH * ShipPicAspect;
            var pic = Panel("pic", row.transform, new Color(0.34f, 0.44f, 0.54f, 1f));
            RectAt(Rt(pic), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(8, -5), new Vector2(picW, picH));
            pic.GetComponent<Image>().raycastTarget = false;
            var mat = Panel("mat", pic.transform, new Color(0.045f, 0.075f, 0.115f, 1f));
            Fill(Rt(mat), 1, 1, 1, 1);
            mat.GetComponent<Image>().raycastTarget = false;
            // 真图精灵(盖深色底上, 等比例); 占位文字在其上, 无图才显 —— 该槽位船型随船队变化, 每帧在 RefreshYard 重填
            var artGo = NewRT("art", mat.transform);
            var art = artGo.AddComponent<Image>();
            art.raycastTarget = false;
            Fill(Rt(artGo), 0, 0, 0, 0);
            var ph = AddText(mat.transform, "", 8, new Color(0.48f, 0.60f, 0.70f, 0.92f), TextAnchor.MiddleCenter);
            Fill(Rt(ph.gameObject), 2, 2, 0, 0);
            ph.horizontalOverflow = HorizontalWrapMode.Overflow;
            _yardOwnPic.Add(art);
            _yardOwnPh.Add(ph);

            var pick = NewRT("pick", row.transform);
            var img = pick.AddComponent<Image>();
            img.sprite = White();
            img.raycastTarget = true;
            img.color = new Color(1, 1, 1, 0.02f);
            var pr = Rt(pick);
            pr.anchorMin = Vector2.zero; pr.anchorMax = Vector2.one;
            pr.offsetMin = Vector2.zero; pr.offsetMax = Vector2.zero;
            var btn = pick.AddComponent<Button>();
            btn.targetGraphic = img;
            var cc = btn.colors;
            cc.normalColor = Color.white; cc.highlightedColor = new Color(1.1f, 1.1f, 1.1f);
            cc.pressedColor = new Color(0.85f, 0.85f, 0.85f); cc.disabledColor = new Color(0.4f, 0.4f, 0.4f, 0.35f);
            btn.colors = cc;
            int idx = i;
            btn.onClick.AddListener(() => { if (idx < play.fleet.Ships.Count) _yardSel = play.fleet.Ships[idx]; });
            _yardOwnBand.Add(img);
            _yardOwnGo.Add(row.transform);

            float tx = 8f + picW + 12f;
            var nm = AddText(row.transform, "", 13, ColTxt, TextAnchor.UpperLeft);
            RectAt(Rt(nm.gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(tx, -4), new Vector2(424 - tx, 18));
            _yardOwnName.Add(nm);
            var inf = AddText(row.transform, "", 10, new Color(0.72f, 0.80f, 0.90f, 0.95f), TextAnchor.UpperLeft);
            RectAt(Rt(inf.gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(tx, -24), new Vector2(424 - tx, 30));
            inf.horizontalOverflow = HorizontalWrapMode.Wrap;
            _yardOwnInfo.Add(inf);
        }

        void RefreshYard()
        {
            if (play.fleet == null || play.cat == null) return;
            var cur = play.Current;
            string pname = cur != null ? cur.Name : "—";
            string aname = cur != null && cur.Area != null ? cur.Area.Name : "—";
            if (_yardTitle != null) _yardTitle.text = "🛠 船坞 · " + pname + " · " + aname;
            int n = play.fleet.Ships.Count;
            if (_yardCash != null)
                _yardCash.text = "现金 <color=#FFD76A>" + Money(play.Cash) + "</color> · 在役 <b>" + n + "</b>/" + play.FleetShipMax
                    + " 艘 · 改装一栏 = 该项造价(金币)。" + (n >= play.FleetShipMax ? " 船队已满, 先卖旧船再买新。" : "");

            // 选中船兜底: 默认旗舰
            if (n == 0) _yardSel = null;
            else if (_yardSel == null || !play.fleet.Ships.Contains(_yardSel)) _yardSel = play.fleet.Ships[0];

            for (int i = 0; i < _yardOwnGo.Count; i++)
            {
                bool has = i < n;
                _yardOwnGo[i].gameObject.SetActive(has);
                if (!has) continue;
                var s = play.fleet.Ships[i];
                _yardOwnName[i].text = (s.IsFlagship ? "<color=#FFD76A>★ 旗舰</color> " : "") + s.Model.Name;
                _yardOwnInfo[i].text = "速 " + s.EffSpeed.ToString("0.0") + " 节 · 火 " + s.EffFire + " · 甲 " + s.EffArmor
                    + " · 仓 " + ShipWeight(s) + "/" + s.EffCapacity + " · 改 " + s.AppliedCount + "/" + s.Model.RefitSlots;
                if (_yardOwnPic.Count > i && _yardOwnPh.Count > i)
                    SetShipArt(_yardOwnPic[i], _yardOwnPh[i], s.Model.Id, "⚓ 船图占位");
                bool sel = s == _yardSel;
                _yardOwnBand[i].color = sel
                    ? new Color(1f, 0.84f, 0.36f, 0.24f)
                    : (i % 2 == 1 ? new Color(1, 1, 1, 0.055f) : new Color(1, 1, 1, 0.02f));
            }

            // 操作条: 选中船摘要 + 任旗/出售/四改
            if (_yardSel != null)
            {
                var s = _yardSel;
                _yardActName.text = "选中: 「" + s.Model.Name + "」 速 " + s.EffSpeed.ToString("0.0") + " 节 · 火力 " + s.EffFire
                    + " · 护甲 " + s.EffArmor + " · 已改装 " + s.AppliedCount + "/" + s.Model.RefitSlots + " 槽\n出售折价 "
                    + Money(play.ShipSellValue(s)) + "金" + (s.Cargo.Count > 0 ? " (先清空货舱才能出售)" : "");
                _yardFlagSel.gameObject.SetActive(!s.IsFlagship);
                _yardSellSel.interactable = !s.IsFlagship && s.Cargo.Count == 0;
                _yardSellSel.GetComponentInChildren<Text>().text = s.Cargo.Count > 0 ? "💼 舱内有货, 不可售" : "💰 出售此船";
                var kinds = new[] { RefitKind.Speed, RefitKind.Fire, RefitKind.Armor, RefitKind.Capacity };
                for (int k = 0; k < 4; k++)
                {
                    long cost = play.RefitCostPreview(s, kinds[k]);
                    bool can = s.CanRefit && cost >= 0;
                    _yardRefit[k].interactable = can;
                    _yardRefit[k].GetComponentInChildren<Text>().text =
                        SeaPlay.RefitCn(kinds[k]) + (can ? " " + Money(cost) : (s.CanRefit ? "" : " 槽满"));
                }
            }
            else
            {
                _yardActName.text = "……没有可处理的船(船队为空时先购新船入列)。";
                _yardFlagSel.gameObject.SetActive(false);
                _yardSellSel.interactable = false;
                for (int k = 0; k < 4; k++) _yardRefit[k].interactable = false;
            }

            // 右栏清单(港区或船表变了才重建行)
            string sig = "a:" + (cur != null && cur.Area != null ? cur.Area.Id : "-");
            foreach (var id in play.cat.ShipOrder) sig += "|" + id;
            if (sig != _yardListSig) { _yardListSig = sig; RebuildYardList(); }

            for (int i = 0; i < _yardBuyModel.Count; i++)
            {
                var m = _yardBuyModel[i];
                string why;
                bool can = play.ShipBuyable(m, out why);
                _yardBuyName[i].text = "<color=" + (can ? "#FFE9B0" : "#8FA0B8") + ">" + m.Name + "</color>"
                    + " <color=#FFD76A>" + Money(m.Price) + "金</color>";
                _yardBuyInfo[i].text = "载 " + m.Capacity + " · 货槽 " + m.Slots + " · 速 " + m.Speed.ToString("0.0")
                    + " 节 · 火 " + m.Fire + " · 甲 " + m.Armor + " · 水手 " + m.CrewMin + "–" + m.CrewMax
                    + " · " + m.Tier + "级" + CnShipType(m.Type) + "\n" + m.Year + " 年下水 · 改造槽 " + m.RefitSlots
                    + (can ? "" : "\n<color=#FF8877>✗ " + why + "</color>");
                _yardBuyBtn[i].interactable = can;
                _yardBuyBtn[i].GetComponentInChildren<Text>().text = can ? "购买" : "暂不可";
            }
        }

        void RebuildYardList()
        {
            if (_yardContent == null || play.cat == null) return;
            foreach (Transform ch in _yardContent) Destroy(ch.gameObject);
            _yardBuyGo.Clear(); _yardBuyName.Clear(); _yardBuyInfo.Clear(); _yardBuyBtn.Clear(); _yardBuyModel.Clear();

            int idx = 0;
            foreach (var id in play.cat.ShipOrder)
            {
                var m = play.cat.FindShip(id);
                if (m == null) continue;
                var row = NewRT("buy" + idx, _yardContent);
                var rr = Rt(row);
                rr.anchorMin = new Vector2(0, 1); rr.anchorMax = new Vector2(1, 1); rr.pivot = new Vector2(0.5f, 1);
                rr.anchoredPosition = new Vector2(0, -idx * 86);
                rr.sizeDelta = new Vector2(0, 82);
                // 行底(左右颜色带区分明暗行)
                var bg = Panel("bg", row.transform, idx % 2 == 1 ? new Color(1, 1, 1, 0.04f) : new Color(1, 1, 1, 0.015f));
                Fill(Rt(bg), 0, 0, 0, 0);

                // 2:1 船图占位(行首, 竖直居中; 与舰队/在役同比例 —— 未来换上该船型真图)
                float rw = Mathf.Max(400f, rr.rect.width);
                float picH = Mathf.Min(66f, rr.rect.height - 8f);
                float picW = picH * ShipPicAspect;
                var pic = Panel("pic", row.transform, new Color(0.34f, 0.44f, 0.54f, 1f));
                RectAt(Rt(pic), new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                    new Vector2(10, 0), new Vector2(picW, picH));
                pic.GetComponent<Image>().raycastTarget = false;
                var mat = Panel("mat", pic.transform, new Color(0.045f, 0.075f, 0.115f, 1f));
                Fill(Rt(mat), 1, 1, 1, 1);
                mat.GetComponent<Image>().raycastTarget = false;
                // 真图精灵(盖深色底上, 等比例); 占位文字在其上, 无图才显
                var artGo = NewRT("art", mat.transform);
                var art = artGo.AddComponent<Image>();
                art.raycastTarget = false;
                Fill(Rt(artGo), 0, 0, 0, 0);
                var ph = AddText(mat.transform, "", 9, new Color(0.48f, 0.60f, 0.70f, 0.92f), TextAnchor.MiddleCenter);
                Fill(Rt(ph.gameObject), 2, 2, 0, 0);
                ph.horizontalOverflow = HorizontalWrapMode.Overflow;
                SetShipArt(art, ph, m.Id, "⚓ 船图占位");

                float tx = 10f + picW + 14f;
                float txtW = Mathf.Max(240f, rw - tx - 120f);   // 留出右侧「购买」钮与边距

                var nm = AddText(row.transform, "", 14, ColTxt, TextAnchor.UpperLeft);
                RectAt(Rt(nm.gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(tx, -2), new Vector2(txtW, 22));
                nm.horizontalOverflow = HorizontalWrapMode.Overflow;   // 名牌+价格不换行, 不压信息行
                _yardBuyName.Add(nm);

                var info = AddText(row.transform, "", 10, new Color(0.76f, 0.83f, 0.92f, 0.95f), TextAnchor.UpperLeft);
                RectAt(Rt(info.gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(tx, -26), new Vector2(txtW, 54));
                info.verticalOverflow = VerticalWrapMode.Overflow;
                _yardBuyInfo.Add(info);

                var buy = MakeBtn(row.transform, "buy", "购买", 14, ColGoldBtn, new Color(0.13f, 0.08f, 0.03f), clickSfx: false);
                RectAt(Rt(buy.gameObject), new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                    new Vector2(-8, 0), new Vector2(96, 46));
                string mid = m.Id;
                buy.onClick.AddListener(() => { play.BuyShip(mid); });
                _yardBuyBtn.Add(buy);
                _yardBuyModel.Add(m);
                _yardBuyGo.Add(row.transform);
                idx++;
            }
            _yardContent.sizeDelta = new Vector2(0, Mathf.Max(40f, idx * 86f));
            if (_yardScroll != null) _yardScroll.verticalNormalizedPosition = 1f;   // 停在清单最上
        }

        static string CnShipType(string t)
        {
            switch (t)
            {
                case "merchant": return "商船";
                case "warship": return "战船";
                case "scout": return "快船";
                default: return t ?? "";
            }
        }

        void BuildTrade(Transform parent)
        {
            // 行情表排版约定:
            //   · 滚动区(vp)相对面板左缘内缩 InsetX=4 → header 坐标 = 数据列坐标 + InsetX, 逐列一一对应;
            //   · 每行 = 一行一条货, 文字与 +1/+10/全 按钮都在这条行内垂直居中对齐(不会再错位到下一行);
            //   · 买/卖按 1 件、10 件、全仓三档, 商品名列收窄, 后列紧挨着, 不再"名和价隔很远"。
            const float InsetX = 4f;
            const float RowPitch = 26f;

            // 数据列(滚动内容坐标): 商品 / 库存 / 买价 / 售价 / 持有 / 成本(自己持仓的均价)
            //   左起 = 看货(是什么、港里有多少、进出什么价), 右起 = 自己(手里几件、什么成本) —— 一眼从"看货"扫到"我的账"。
            float[] cx = { 6f, 184f, 254f, 324f, 396f, 460f };
            float[] cw = { 170f, 62f, 62f, 64f, 56f, 56f };
            string[] cn = { "商品", "库存", "买价", "售价", "持有", "成本" };
            TextAnchor[] ca = {
                TextAnchor.MiddleLeft, TextAnchor.MiddleRight, TextAnchor.MiddleRight,
                TextAnchor.MiddleRight, TextAnchor.MiddleRight, TextAnchor.MiddleRight };

            _tradeTitle = AddText(parent, "", 17, ColTitle, TextAnchor.MiddleCenter);
            RectAt(Rt(_tradeTitle.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -2), new Vector2(0, 22));

            // 关闭钮: 只留一个 ✕(要选港就把行情收起来, 引导语会说)
            var xb = MakeBtn(parent, "close", "✕", 16, ColBtn, Color.white);
            RectAt(Rt(xb.gameObject), new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(-6, -2), new Vector2(30, 24));
            xb.onClick.AddListener(() => _tradeOpen = false);

            // 列头底条 + 列头(几何与滚动行完全同列)
            var hb = Panel("hdr", parent, new Color(0.10f, 0.16f, 0.22f, 0.55f));
            RectAt(Rt(hb), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(2, -36), new Vector2(-4, 26));
            for (int c = 0; c < cn.Length; c++)
                HeaderCell(parent, cn[c], cx[c] + InsetX, cw[c], ca[c], 13, c == 4 ? ColGold : ColDim);   // 持有 列头金黄
            // 买 / 卖三键组(数据列最右止于 516, 留 ~14 空隙再接按钮)。
            //   键色不再按"买/卖"分工: **青 = 常态**(与底栏动作键同色), **金黄 = 这一行手里有货可卖**
            //   —— 黄在这里是"能动手"的状态色, 由 RefreshTrade 逐行刷(见那里的 canSell 分支)。
            const float BuyX = 530f, SellX = 706f;
            HeaderCell(parent, "买 入", BuyX + InsetX, 170f, TextAnchor.MiddleCenter, 13, ColTitle);
            HeaderCell(parent, "卖 出", SellX + InsetX, 170f, TextAnchor.MiddleCenter, 13, new Color(0.70f, 0.92f, 1f));

            // 滚动区
            var vpGo = Panel("vp", parent, new Color(0, 0, 0, 0.22f));
            var vp = Rt(vpGo);
            vp.anchorMin = Vector2.zero; vp.anchorMax = Vector2.one;
            vp.offsetMin = new Vector2(4, 4); vp.offsetMax = new Vector2(-4, -64);
            vpGo.AddComponent<Mask>().showMaskGraphic = false;
            var scroll = vpGo.AddComponent<ScrollRect>();
            scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;

            var contentGo = NewRT("content", vpGo.transform);
            var content = Rt(contentGo);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0, play.world.Goods.Count * RowPitch + 4);
            scroll.viewport = vp;
            scroll.content = content;
            _tradeScroll = scroll;   // 开铺时卷回第一行用(见 ScrollMarketTop; Update 里那条闭→开的边沿)
            _marketVp = vp;   // 行悬停判定: 指针是否落在这块滚动视窗里(滚到哪行就亮哪行)

            // 行数 = 货总数, 一次建足; **每行装哪件货**由 SortMarketRows 决定, 这里只管把行摆好。
            //   注意行控件不可再增删 —— 排序是"换行里装的东西", 不是"重排行控件"。
            for (int i = 0; i < play.world.Goods.Count; i++)
            {
                var row = Rt(NewRT("row" + i, content));
                row.anchorMin = new Vector2(0, 1); row.anchorMax = new Vector2(1, 1);
                row.pivot = new Vector2(0.5f, 1);
                row.anchoredPosition = new Vector2(0, -(i * RowPitch + 2)); row.sizeDelta = new Vector2(0, 24);
                _rowRt.Add(row);
                // 整行透明感应带(排在文字与按钮之下): 承载"悬停微亮 / 点选金亮", 每帧按指针所在行上色
                var band = NewRT("band", row).AddComponent<Image>();
                band.sprite = White();
                band.color = new Color(1, 1, 1, i % 2 == 1 ? 0.05f : 0.02f);
                band.raycastTarget = true;
                var br = Rt(band.gameObject);
                br.anchorMin = Vector2.zero; br.anchorMax = Vector2.one;
                br.offsetMin = Vector2.zero; br.offsetMax = Vector2.zero;
                _rowBand.Add(band);
                var pick = band.gameObject.AddComponent<SeaRowPick>();
                pick.hud = this; pick.index = i;

                // 顺序必须与 cn[] 逐列对齐(RefreshTrade 按同名 List 逐行填数)
                var nm = RowCell(row, "", 15, ColTxt, TextAnchor.MiddleLeft, cx[0], cw[0]);      // 商品
                var st = RowCell(row, "", 13, ColDim, TextAnchor.MiddleRight, cx[1], cw[1]);     // 库存
                var ak = RowCell(row, "", 15, Color.white, TextAnchor.MiddleRight, cx[2], cw[2]); // 买价
                var bd = RowCell(row, "", 15, Color.white, TextAnchor.MiddleRight, cx[3], cw[3]); // 售价
                var hd = RowCell(row, "", 14, ColGold, TextAnchor.MiddleRight, cx[4], cw[4]);    // 持有数 金黄
                var cs = RowCell(row, "", 14, ColDim, TextAnchor.MiddleRight, cx[5], cw[5]);     // 成本(持仓均价)
                _tName.Add(nm); _tStock.Add(st); _tAsk.Add(ak); _tBid.Add(bd); _tHold.Add(hd); _tCost.Add(cs);

                // 买 1 / 买 10 / 全仓
                var buys = new Button[3];
                for (int b = 0; b < 3; b++)
                {
                    int qty = b == 0 ? 1 : (b == 1 ? 10 : int.MaxValue);
                    var btn = MakeBtn(row, "b" + i + "_" + b, b == 0 ? "买1" : (b == 1 ? "买10" : "全买"),
                        13, ColBtn, Color.white, clickSfx: false);   // 青底白字 = 与底栏动作键同色
                    PlaceRowButton(btn, BuyX + b * 58f);
                    // **点下去那一刻**才去 _rowGoods 里取货, 而不是建行时把货捕获进来:
                    //   行号是稳定的, 行里装谁是可变的 —— 捕获旧货的话, 换港重排之后
                    //   按钮会拿着上一港的行序去买卖风马牛不相及的货。
                    var q = qty; int ri = i;
                    btn.onClick.AddListener(() =>
                    {
                        if (ri >= _rowGoods.Count) return;
                        _selRow = ri; play.BuyGood(_rowGoods[ri], q);
                    });
                    buys[b] = btn;
                }
                // 卖 1 / 卖 10 / 全清
                var sells = new Button[3];
                var sellLbls = new Text[3];
                var sellImgs = new Image[3];
                for (int s = 0; s < 3; s++)
                {
                    int qty = s == 0 ? 1 : (s == 1 ? 10 : int.MaxValue);
                    var btn = MakeBtn(row, "s" + i + "_" + s, s == 0 ? "卖1" : (s == 1 ? "卖10" : "全卖"),
                        13, ColBtn, Color.white, clickSfx: false);   // 建时一律青底; "有货转金黄"由 RefreshTrade 逐行上色
                    PlaceRowButton(btn, SellX + s * 58f);
                    var q = qty; int si = i;
                    btn.onClick.AddListener(() =>
                    {
                        if (si >= _rowGoods.Count) return;
                        _selRow = si; play.SellGood(_rowGoods[si], q);
                    });
                    sells[s] = btn;
                    sellLbls[s] = btn.GetComponentInChildren<Text>();   // 留住字色引用: 无持仓时整键转暗淡
                    sellImgs[s] = btn.image;                            // 留住底色引用: 有持仓时整键转金黄
                }
                _buy.Add(buys); _sell.Add(sells); _sellLbl.Add(sellLbls); _sellImg.Add(sellImgs);
            }
            SortMarketRows();   // 建完先排一次; 之后每次**进港**再排(见 Update 的换港分支)
        }

        // =============================================================
        // 行情行序: **手里有货的排在最上面**, 其余按原始数据序跟在后面。
        //   为什么是"持有"而不是"港内库存": 进港看行情的第一件事是"我这船货在这儿值多少、要不要脱手",
        //     所以该被顶到眼前的是**我舱里的货**, 不是这座港碰巧囤了什么。
        //   为什么两趟拷贝而不是 Sort 比较器: List.Sort 不稳定, 同组内会乱序 ——
        //     行与行之间来回换位会让熟悉了位置的人每次都重新找货。两趟拷贝是**稳定分堆**, 组内保持原序。
        //   调用时机 = 只在这一刻: 进港 / 首次构建。买卖途中**绝不**重排 —— 否则买一手货行就跳走,
        //     鼠标底下的那行换了货, 下一次点击必然买错东西。
        // =============================================================
        void SortMarketRows()
        {
            var all = play.world.Goods;
            _rowGoods.Clear();
            for (int k = 0; k < all.Count; k++)
                if (play.GoodInHold(all[k].Id) > 0) _rowGoods.Add(all[k]);
            for (int k = 0; k < all.Count; k++)
                if (play.GoodInHold(all[k].Id) <= 0) _rowGoods.Add(all[k]);
            // 行序变了, 旧的"选中/悬停行号"指着的是别的货了 —— 清掉, 免得高亮停在不相干的货上
            _selRow = -1; _hoverRow = -1;
        }

        // =============================================================
        // 下面两个方法**只给无头截图闸门(SeaShot)用**, 正常玩法里没有任何调用点。
        //   为什么需要: 到港会自动弹开行情表, 而截图要判的恰恰是"球画对没有 / 货排对没有"——
        //   不收起来就只截得到一张表格, 球整颗被盖住, 目视复核无从谈起。
        //
        //   为什么是"收浮层"而不是"整块画布 SetActive(false)": **地名标签也挂在这块画布下**,
        //   整块关掉会连标签一起关掉 —— 而标签的球面朝向与背面剔除(阶段2 改过的那部分)
        //   正是要看的东西之一。收浮层则球、标签、旗、船全在。
        // =============================================================
        public void QaClosePanels()
        {
            _tradeOpen = false; _newsOpen = false; _fleetOpen = false;
            _departOpen = false; _yardOpen = false;
        }

        // [闸门] 「进游戏/读档后头一下点城点不中」的取证。
        //   要回答的只有一个问题: 玩家第一下点城的那一瞬, 海图输入是不是被某个浮层整个锁着?
        //   链路是 HandleCamAndPick 开头那句 `if (_hud.AnyTopOpen) 早退` —— 只要有一层浮层开着,
        //   点城/拖球/滚轮全都收不到, 而且**一点反馈都没有**, 玩家只会觉得"点了没反应"。
        //   所以这里把六层浮层的在场情况、AnyTopOpen、以及"进港边沿到底把谁弹开了"(见 Update 的
        //   进港分支, 那份是钉住的, 不受截图闸门每帧收浮层的影响)一次报全。
        public string QaFirstClickProbe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("AnyTopOpen=").Append(AnyTopOpen)
              .Append(" 行情=").Append(On(_tradeGo, _tradeOpen))
              .Append(" 舰队=").Append(On(_fleetGo, _fleetOpen))
              .Append(" 船坞=").Append(On(_yardGo, _yardOpen))
              .Append(" 情报=").Append(On(_newsGo, _newsOpen))
              .Append(" 出航检查=").Append(On(_departGo, _departOpen))
              .Append(" 确认=").Append(On(_confirmGo, false))
              .Append(" 设置=").Append(play != null && play.SettingsOpen)
              .Append(" | 玩家状态=").Append(play != null ? play.State.ToString() : "?")
              .Append(" 指针=").Append(((Vector2)Input.mousePosition).ToString("F0"))
              .Append(" 指针在UI上=")
              .Append(UnityEngine.EventSystems.EventSystem.current != null
                      && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())
              .Append(" 指针下最上层=").Append(TopUiUnderPointer(Input.mousePosition))
              .Append(" | 边沿: ").Append(_qaVoyageEdge);
            return sb.ToString();
        }

        // 指针底下压着的那块 UI 是谁 —— "指针在UI上=true" 只说明"有东西", 不说"是什么"。
        //   而这两件事要分开看: 压在日志/货仓卡上是正常的(那两张卡本来就该点得到),
        //   压在某个**全屏透明底板**上才是病(那会让海图永远点不中, 且看不见摸不着)。
        // 传点而不是内部取 Input.mousePosition: SeaPlay 的点击记录仪要问的是"那一刻**那一点**",
        //   而它记的那一点就是当时的光标位置 —— 传参才能保证两边问的是同一点。
        public static string TopUiUnderPointer(Vector2 pos)
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) return "无EventSystem";
            var ped = new PointerEventData(es) { position = pos };
            var hits = new System.Collections.Generic.List<RaycastResult>();
            es.RaycastAll(ped, hits);
            if (hits.Count == 0) return "无(在海图上)";
            var g = hits[0].gameObject;
            return g.name + " @ " + (g.transform.parent != null ? g.transform.parent.name : "-")
                + " 共" + hits.Count + "层";
        }

        // 浮层"开着没有"要报两个数: 组件 activeSelf(渲染/挡点击的真实依据) + 那个 bool 意图。
        //   两个不一致本身就是病(比如 _tradeOpen=true 但 _tradeGo 没激活), 分开报才看得见。
        static string On(GameObject go, bool want)
        {
            if (go == null) return "无组件";
            return (go.activeSelf ? "开" : "关") + (go.activeSelf == want ? "" : "(意图" + (want ? "开" : "关") + "!)");
        }

        // 把行情表叫回眼前。
        public void QaOpenMarket()
        {
            _tradeOpen = true;
        }

        // 把舰队情况面板叫回眼前(截图闸门量"8~10 艘也摆得下"用)。
        //   走的仍是 Update 里那条真路径(_fleetOpen=true → SetActive + RefreshFleet),
        //   不另开一条"直接摆卡"的近道 —— 否则量到的就不是玩家看到的那个版面。
        public void QaOpenFleet()
        {
            _tradeOpen = false;
            _fleetOpen = true;
        }

        // [闸门] 走一遍"舰队页 → 点补给/招募 → 点确定"的真按钮链路, 把每一步的状态报成一行字。
        //   用户报的病例: 点完确定, 舰队页没了。所以这里**点的是真按钮的 onClick**(不是直接调
        //   play.ProvisionTo): 抄近道就正好绕开"确认弹窗 → 回调 → 回到哪一层"这段真正会出错的地方,
        //   那这段闸门就等于没测。which = prov15 / prov30 / crew / crewHalf / all(四个依次走一遍)。
        //   注意打开面板只设 _fleetOpen: 面板的 SetActive/刷新在 Update 里那条真路径上,
        //   所以"确认后有没有回到舰队页"要**下一帧**看面板探针(QaFleetPanelState), 这里先给 _fleetOpen。
        //   招募那两个键还额外报一遍**逐船水手**(补员数字才是这两个键的真凭据, "面板回到舰队页"只说对了一半)。
        public string QaFleetConfirmFlow(string which)
        {
            var sb = new StringBuilder(200);
            string[] order = which == "all" ? new[] { "crewHalf", "crew", "prov15", "prov30" } : new[] { which };
            foreach (var k in order)
            {
                Button b = k == "prov30" ? _fleetProv30
                    : k == "crew" ? _fleetRecruit
                    : k == "crewHalf" ? _fleetRecruitHalf
                    : _fleetProv15;
                if (b == null) { sb.Append("flow[").Append(k).Append("] 按钮不在\n"); continue; }
                _fleetOpen = true;
                // 半仓补员要两种船同时在队才测得全: 一条**不足半仓**(该被补到半仓)、
                //   一条**超过半仓**(必须原样不动)。只摆前一种的话, "只补不裁"这半边等于没验。
                if (k == "crewHalf") MakeCrewSpread();
                string before = k == "crewHalf" || k == "crew" ? " 船员前[" + CrewSnapshot() + "]" : "";
                b.onClick.Invoke();                        // ① 点按钮 → 该弹出确认
                bool dlg = _confirmGo != null && _confirmGo.activeSelf;
                sb.Append("flow[").Append(k).Append("] 弹确认=").Append(dlg)
                  .Append("(此时舰队页=").Append(_fleetOpen).Append(')');
                if (dlg) OnConfirmOk();                    // ② 点「确定」
                sb.Append(" → 确定后 舰队页=").Append(_fleetOpen)
                  .Append(" 确认弹窗=").Append(_confirmGo != null && _confirmGo.activeSelf);
                if (before.Length > 0) sb.Append(before).Append(" → 后[").Append(CrewSnapshot()).Append(']');
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        // 造出半仓补员的两种样本: 0 号压到最低配员(不足半仓, 该被补到半仓线),
        //   1 号招到满编(超过半仓, 必须一个不动)。一次调用里两类船同时在队, 逐船判才看得出。
        void MakeCrewSpread()
        {
            var ships = play != null && play.fleet != null ? play.fleet.Ships : null;
            if (ships == null || ships.Count == 0) return;
            ships[0].CrewAboard = ships[0].Model.CrewMin;
            if (ships.Count > 1) ships[1].CrewAboard = ships[1].Model.CrewMax;
        }

        // 逐船水手: 现员/半仓线/满编。半仓线是 SeaPlay.CrewHalfOf(不是这里另算一遍) —— 报的就是执行时用的那个数。
        string CrewSnapshot()
        {
            var ships = play != null && play.fleet != null ? play.fleet.Ships : null;
            if (ships == null) return "无船队";
            var sb = new StringBuilder();
            foreach (var s in ships)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(s.Model.Id).Append(' ').Append(s.CrewAboard)
                  .Append("/半").Append(SeaPlay.CrewHalfOf(s.Model))
                  .Append("/满").Append(s.Model.CrewMax);
            }
            return sb.ToString();
        }

        // 舰队面板此刻的**真实**状态, 给截图轮当量尺: 开没开 / 面板多大 / 摆了几张卡 / 是哪个实例。
        //   为什么连"多大"也要报: 这版改的就是"面板得长到装下 8~10 张卡", 而"够不够长"最容易被
        //   一句推理糊弄过去("尺寸都算好了") —— 报出来的是运行期 rect, 不是我心算的那个数。
        //   实例 id 是为了排"截图轮手上的 HUD 和画面上那个不是同一个"这种病(症状: 面板明明开了却看不见)。
        public string QaFleetPanelState()
        {
            var r = _fleetGo != null ? Rt(_fleetGo) : null;
            return "hud#" + GetInstanceID()
                 + " fleetOpen=" + _fleetOpen
                 + " active=" + (_fleetGo != null && _fleetGo.activeSelf)
                 + " size=" + (r != null ? r.sizeDelta.x.ToString("F0") + "x" + r.sizeDelta.y.ToString("F0") : "-")
                 + " cards=" + _shipHeads.Count
                 + " built=" + _built;
        }

        // =============================================================
        // 右下角"舰队货仓"速览: 行情开着时, 买/卖一入账就在这按船画"格子占用"
        //   —— 每船: 名称(旗舰★) + 货种 k/Slots + 占载 occ/Eff, 下一行格子条(█=货, ▓=给养, ░=空),
        //      再下一行列出每种货的名称 × 件数(占重)。
        //   给养在模拟层是全队一本账, 这里按"各船空舱"分摊到船后逐船显示(口径见 SeaPlay.ShipProvWeights),
        //   于是 occ 与格子条都含它 —— 玩家能直接看出"哪艘船的仓位被给养吃掉了多少"。
        // =============================================================
        void BuildHold(Transform root)
        {
            // 日志同款"暗色卡片": 外卡 ColPanel + 金字描头 + 内凹深色遮罩视窗(内容超高由 Mask 裁掉)
            _holdGo = Panel("holdview", root, ColPanel);
            var hr = Rt(_holdGo);
            hr.anchorMin = new Vector2(1f, 0f); hr.anchorMax = new Vector2(1f, 0f);   // 右下角
            hr.pivot = new Vector2(1f, 0f);
            // 与右上的航行日志卡同一根右缘(x=-10)同一宽度(360): 货仓速览与日志黑框在视觉上"对齐、同宽"
            hr.anchoredPosition = new Vector2(-10f, 126f);   // 日志(y348+)之下、行情表(至x910)右旁的空地
            hr.sizeDelta = new Vector2(360f, 214f);
            AddHead(_holdGo.transform, "舰队货仓");

            // 与日志同一套"可滚动"装配: 十艘船的货单在这张 214 高的卡里本来就放不下,
            //   以前只列 3 艘、剩下的让玩家去舰队页看; 现在滚轮/拖拽能翻, 就全列出来。
            _holdSc = MakeScroller(hr, "hold", new Vector4(6f, 6f, 20f, 28f));

            // 字号 11 → 10(右侧这两张卡统一小一号, 与日志同理)
            _holdBody = AddText(_holdSc.Content, "", 10, ColTxt, TextAnchor.UpperLeft);
            var b = Rt(_holdBody.gameObject);
            b.anchorMin = Vector2.zero; b.anchorMax = Vector2.one;
            // 右缘收到 -6: 视窗已为滚动条让开 20, 再加 6 = 26, 而轨道只占到距右缘 7..17 → 不叠字
            b.offsetMin = new Vector2(8f, 8f); b.offsetMax = new Vector2(-6f, -8f);
            _holdBody.horizontalOverflow = HorizontalWrapMode.Wrap;
            _holdBody.verticalOverflow = VerticalWrapMode.Overflow;
            _holdGo.SetActive(false);
        }

        void RefreshHold()
        {
            var ships = play.fleet.Ships;
            if (ships == null || ships.Count == 0) { _holdBody.text = ""; return; }
            var w = play.world;
            int cap = Mathf.Max(1, play.TotalCapacity());
            int used = play.CargoWeight() + play.ProvWeight();
            int freeL = Math.Max(0, play.FreeLoad());
            var prov = play.ShipProvWeights();   // 同上: 各船名下的给养载重
            var sb = new StringBuilder(384);
            sb.Append("<size=11>货仓合计 <color=#FFD76A>").Append(used).Append("</color>/").Append(cap)
              .Append(" 载 · 空 <b>").Append(freeL).Append("</b></size>\n");
            if (play.ProvWeight() > 0)
                sb.Append("<size=9><color=#8FA8C2>其中给养占 ").Append(play.ProvWeight())
                  .Append(" 载 —— 各船 ▓ 段即该船名下的给养(按空舱分摊)</color></size>\n");
            // 不再只列 3 艘: 这张卡现在能滚(见 BuildHold → MakeScroller), 十艘全列出来也翻得到,
            //   没有理由再把玩家赶去舰队页看后几艘。
            for (int i = 0; i < ships.Count; i++)
            {
                var s = ships[i];
                if (i > 0) sb.Append('\n');
                int cargoW = ShipWeight(s);
                int provW = i < prov.Length ? prov[i] : 0;
                int occ = cargoW + provW;                 // 占 = 货 + 给养(与舰队页船卡同一口径)
                int ec = Math.Max(1, s.EffCapacity);
                sb.Append("<size=11>").Append(s.IsFlagship ? "<color=#FFD76A>★</color>" : "<color=#8FA8C2>·</color>")
                  .Append(" <b>").Append(ShipShort(s.Model.Name)).Append("</b>")
                  .Append(s.IsFlagship ? " <color=#FFD76A>旗舰</color>" : " <color=#7FA0C0>僚舰</color>")
                  .Append("</size>  货种 <b>").Append(s.Cargo.Count).Append("</b>/").Append(s.Model.Slots)
                  .Append(" · 占 <b>").Append(occ).Append("</b>/").Append(ec).Append('\n');
                // 格子条: █ 货 / ▓ 给养 / ░ 空(16 格按占载比例填) —— 给养那一段单独上色, 一眼能看出
                //   "这艘船有多少仓位是给养吃掉的"; 只用 █/░ 两色的话给养就被算进货里了, 看不出来。
                int cells = 16;
                int barCargo = Mathf.Clamp((int)Mathf.Ceil((float)cargoW / ec * cells), 0, cells);
                int barAll = Mathf.Clamp((int)Mathf.Ceil((float)occ / ec * cells), barCargo, cells);
                int barProv = barAll - barCargo, barFree = cells - barAll;
                sb.Append("<size=9><color=#FFD76A>").Append(new string('█', barCargo))
                  .Append("</color><color=#6FB6E8>").Append(new string('▓', barProv))
                  .Append("</color><color=#3D5568>").Append(new string('░', barFree))
                  .Append("</color>  <color=#8FA8C2>货 ").Append(cargoW).Append(" · 给养 ").Append(provW)
                  .Append("</color></size>\n");
                // 货名清单: 名称 × 件数(占重); 最多列 6 种, 其余省略
                if (s.Cargo.Count == 0)
                {
                    sb.Append("<color=#8FA8C2>· 空舱待装</color>");
                }
                else
                {
                    sb.Append("<color=#9FB6CF>");
                    int k = 0;
                    foreach (var kv in s.Cargo)
                    {
                        if (k == 6) { sb.Append("…(更多)"); break; }
                        if (k > 0) sb.Append("    ");
                        Good gg; w.GoodById.TryGetValue(kv.Key, out gg);
                        string nm = gg != null ? gg.Name : kv.Key;
                        int wu = (gg != null ? Mathf.Max(1, gg.Weight) : 1) * kv.Value;
                        sb.Append("<color=#D7E6FF>").Append(nm).Append("</color>×").Append(kv.Value).Append(" 占").Append(wu);
                        k++;
                    }
                    sb.Append("</color>");
                }
            }
            _holdBody.text = sb.ToString();
            // content 必须跟着文字一起长 —— 否则滚动量还按上一次的高度算, 后几艘就滚不到底。
            //   首帧量 preferredHeight 时可能还没拿到正确宽度(会量出个偏大的值), 但本方法每帧都跑,
            //   下一帧就正过来了, 不必专门 ForceUpdateCanvases。
            _holdSc.Content.sizeDelta = new Vector2(0f, Mathf.Max(40f, _holdBody.preferredHeight + 16f));
            UpdateThumb(_holdSc);
        }

        // 船名取主名(遇 "A·B" 取 A), 右栏速览里省地方
        static string ShipShort(string n)
        {
            int i = n == null ? -1 : n.IndexOf('·');
            return i > 0 ? n.Substring(0, i) : n;
        }

        static void Place(Text t, float x, float w, float top, float h)
        {
            var r = Rt(t.gameObject);
            r.anchorMin = new Vector2(0, 1); r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = new Vector2(x + w * 0.5f, -(8 + top + h * 0.5f));
            r.sizeDelta = new Vector2(w, h);
        }
        static void Place(RectTransform r, float x, float w, float top, float h)
        {
            r.anchorMin = new Vector2(0, 1); r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, -(8 + top + h)); r.sizeDelta = new Vector2(w, h);
        }

        void BuildPortLabels(World world)
        {
            var cv = transform.GetComponentInChildren<Canvas>().transform;
            var rootGo = NewRT("portlabels", cv);
            RootRect(Rt(rootGo));
            foreach (var p in world.Ports)
            {
                var t = AddText(rootGo.transform, p.Name, 15, Color.white, TextAnchor.MiddleCenter);
                var o = t.gameObject.AddComponent<Outline>();
                o.effectColor = new Color(0, 0, 0, 0.85f);
                o.effectDistance = new Vector2(0.7f, -0.7f);
                var r = Rt(t.gameObject);
                r.anchorMin = Vector2.zero; r.anchorMax = Vector2.zero;
                r.pivot = new Vector2(0.5f, 0f);
                r.sizeDelta = new Vector2(150, 18);
                t.gameObject.SetActive(false);
                _labels[p.Id] = t;
            }
            // 让地名标签垫在所有 HUD 面板之下(HUD 常驻面板/按钮永不被城市名压住):
            //   先把标签移成 Canvas 第一个子节点; 之后所有面板都在它上面一层画。
            //   (标签 3D 视角投影到屏幕, 仍在海图之上 —— 只是不再跃到按钮/日志上。)
            rootGo.transform.SetSiblingIndex(0);
        }

        // =============================================================
        // 每帧刷新
        // =============================================================
        void Refresh()
        {
            var st = play.State;

            // --- 顶部试炼条(细条: 题 + 一行进度, 短句不换行) ---
            float frac = Mathf.Clamp01((float)play.ProfitSoFar / SeaPlay.ProfitTarget);
            _goalTitle.text = play.GoalMet
                ? "<b>目标达成!</b> 已净赚 " + Money(play.ProfitSoFar)
                : "<b>试炼 · 目标净赚 " + Money(SeaPlay.ProfitTarget) + "</b>  ·  从 " + Money(play.StartWorth) + " 起步";
            _goalSub.text = play.GoalMet
                ? "船长, 你已是一号人物 —— 把商路铺向全世界。"
                : "已净赚 <color=#FFD76A>" + Money(play.ProfitSoFar) + "</color> · 完成 " + (int)(frac * 100) + "%";
            if (!_goalCongrats && play.GoalMet)
            {
                _goalCongrats = true;
                play.Banner = "🏆 试炼达成! 净赚 " + Money(play.ProfitSoFar) + " —— 海上第一商人的第一步。";
                play.PushLog("🎉 达成试炼目标! 身家 " + Money(play.NetWorth()));
            }
            var tr = Rt(_goalFill.transform.parent.gameObject);
            float maxW = Mathf.Max(0f, tr.rect.width);
            Rt(_goalFill.gameObject).sizeDelta = new Vector2(maxW * frac, 0);
            _goalFill.color = frac >= 1f ? new Color(0.95f, 0.78f, 0.25f) : new Color(0.30f, 0.85f, 0.42f);

            // --- 左上: 现金/舰况 ---
            _status.text = StatusText();

            // --- 日志(带日期、可滚动翻看历史) ---
            RefreshLog();

            // --- 停泊(泊港 / 海上抛锚同一条底操作条) ⇄ 航行 切换 ---
            bool docked = st == SeaPlay.Mode.Docked;     // 真·在港: 买卖/船坞/情报/休整才可用
            bool sailing = st == SeaPlay.Mode.Sailing;
            bool stopped = !sailing;                     // 泊港或抛锚都能选目标 → 出航
            _dockGo.SetActive(stopped);
            _sailGo.SetActive(sailing);
            if (stopped) RefreshDock();
            else { RefreshSail(); _yardOpen = false; }   // 一离港船坞就关

            // --- 舰队情况(浮层; 泊港/航行都能看; 到新港自动弹行情时让位) ---
            string curIdNow = play.Current != null ? play.Current.Id : "";
            // "进港"有两条来源, 都得排一次:
            //   ① 海上到港 —— 港口 id 变了;
            //   ② 开局 / 读档 —— 港口 id 可能**根本没变**(启动首页那几帧 Current 已是母港), 靠 VoyageSerial 认出来。
            //   少了 ②,"存一份在母港的档再读回来"这条路上手里的货永远排不到最上面。
            if (docked && (curIdNow != _lastPort || play.VoyageSerial != _lastVoyage))
            {
                // 这一跳里"排一次序"和"弹一次行情"是**两件事**, 分开判 —— 它俩曾经被绑在一起, 于是
                //   开局/读档也被当成"到港"把行情弹了出来: 玩家刚进游戏、正要点第一座城, 整个海图
                //   输入却被这层浮层锁着(AnyTopOpen → HandleCamAndPick 早退), 而且**一点反馈都没有**,
                //   表现就是"点城要点好几次才选中"。排序要, 弹窗不要。
                bool newPort = curIdNow != _lastPort;                   // ① 海上驶到一港:
                bool voyageEdge = play.VoyageSerial != _lastVoyage;     // ② 开局 / 读档(港没变)
                _fleetOpen = false; _yardOpen = false; _newsOpen = false;
                SortMarketRows();   // 进港排一次: 手里有货的置顶, 之后整个停靠期间钉住不重排
                // 只有**真换了港**才自动弹; 开局/读档一律不弹(读档落在别的港也算读档: 玩家的下一动
                //   是点目的地, 不是看行情; 真想看随时点「本港行情」)。
                _tradeOpen = !voyageEdge && newPort;
                // [闸门] 把"这一刻真路径把面板开成了什么样"钉下来。为什么非得钉一份而不是到截图时再看:
                //   截图闸门每帧都在替玩家收浮层(QaClosePanels), 直接读当场状态永远是"收好了" ——
                //   量到的是闸门自己干的活, 不是玩家真走的这条路。
                //   这里量的是病灶本身: 开局/读档也是"一次进港", 于是自动弹行情把海图输入整个吃掉
                //   (AnyTopOpen → HandleCamAndPick 早退), 玩家点第一座城点不动就是这个。
                _qaVoyageEdge = "第" + (++_qaVoyageEdges) + "次 " + (voyageEdge ? "开局/读档" : "换港")
                    + "边沿(港=" + curIdNow + ") → 排序✓ 弹行情=" + _tradeOpen
                    + " [fleet=" + _fleetOpen + " news=" + _newsOpen + " yard=" + _yardOpen + "]";
            }
            _lastPort = curIdNow;
            _lastVoyage = play.VoyageSerial;
            _fleetGo.SetActive(_fleetOpen);
            if (_fleetOpen) RefreshFleet();
            // 面板顶缘(宽体能装 10 艘卡的必然)伸进了左上题名那块地界 —— 开着时让题名先让位。
            //   不让的话: 题名是**最后挂**的、永远在最上层, 于是它正好压在 1 号船卡的船图上,
            //   看起来像贴错位置的贴纸。收起来再放回去, 别的一概不动(行情表够不着题名, 不用管)。
            if (_logoGo != null && _logoGo.activeSelf == _fleetOpen) _logoGo.SetActive(!_fleetOpen);

            // --- 行情 ---
            // 开铺就回到数据第一行。三条开铺的路(点「本港行情」/ 进港自动弹 / QA 钩子)在这里收口:
            //   面板每帧被 SetActive 一次, 于是"上一帧开着没有"就是那条**闭→开**的边沿, 不必在三个
            //   调用点各写一遍(将来再多一条开铺的路, 这里自动也管)。面板在航行中被 SetActive(false)、
            //   到港再开, 同样算一次闭→开 —— 换港后回到第一行也正是要的。
            bool tradeWasOn = _tradeGo.activeSelf;
            _tradeGo.SetActive(docked && _tradeOpen);
            if (docked && _tradeOpen) RefreshTrade();
            if (docked && _tradeOpen && !tradeWasOn) ScrollMarketTop();

            // --- 船坞(浮层; 靠港才能买/卖/改装) ---
            if (_yardGo != null)
            {
                _yardGo.SetActive(docked && _yardOpen);
                if (docked && _yardOpen) RefreshYard();
            }

            // --- 情报站(浮层; 靠港才营业, 离港自动收铺) ---
            if (!docked) _newsOpen = false;
            if (_newsGo != null)
            {
                _newsGo.SetActive(docked && _newsOpen);
                if (docked && _newsOpen) ApplyNewsPage();   // 只负责让 三页/大厅 那页显示
            }

            // --- 右下角货仓速览: 行情开着时按船画格子占用(买/卖入账后下一帧即刷新) ---
            bool holdWasOn = _holdGo != null && _holdGo.activeSelf;
            if (_holdGo != null) _holdGo.SetActive(docked && _tradeOpen);
            if (docked && _tradeOpen && _holdBody != null) RefreshHold();
            // 刚开铺那一下把它卷回**顶上**(合计那一段): 这张卡现在能滚, 不收一下就会停在
            //   上次翻到的地方 —— 玩家一开行情看到的是半截船名, 找不到"货仓合计"。
            //   (与行情表开铺回第一行同一条道理, 见 ScrollMarketTop。)
            if (docked && _tradeOpen && !holdWasOn && _holdSc != null)
                _holdSc.Scroll.verticalNormalizedPosition = 1f;

            // --- 出航前检查(顶层浮层; 一旦起航/换状态即收起) ---
            if (sailing) _departOpen = false;
            _departGo.SetActive(stopped && _departOpen);

            // --- 横幅 ---
            if (!string.IsNullOrEmpty(play.Banner))
            {
                _bannerText = play.Banner; _bannerAt = Time.time;
                play.Banner = null;
            }
            float age = Time.time - _bannerAt;
            if (age >= 0f && age < 3.2f)
            {
                _bannerGo.SetActive(true);
                _banner.text = _bannerText;
                var c = _banner.color; c.a = Mathf.Clamp01(1f - (age - 2.4f)); _banner.color = c;
            }
            else _bannerGo.SetActive(false);

            PlacePortLabels();
        }

        string StatusText()   // 顶部正中信息卡: 三行小字(现金/日期港 / 货舱·水手·士气·病号·续航·货种)
        {
            var sb = new StringBuilder(220);
            var cur = play.Current;
            string portLine;
            if (play.State == SeaPlay.Mode.Docked) portLine = cur != null ? cur.Name + " · " + cur.Area.Name : "—";
            else if (play.State == SeaPlay.Mode.Anchored) portLine = "海上 🚩 抛锚";
            else portLine = "航行中";
            sb.Append("<size=15><color=#FFD76A>💰 现金 ").Append(Money(play.Cash)).Append("</color></size>\n");
            sb.Append(play.engine.Year).Append(" 年 ").Append(play.engine.Month).Append(" 月 ").Append(play.engine.DayOfMonth).Append(" 日  ·  ").Append(portLine).Append("\n");
            int cap = play.TotalCapacity();
            sb.Append("货舱 ").Append(play.CargoWeight() + play.ProvWeight()).Append("/").Append(cap)
              .Append("(空 ").Append(Math.Max(0, play.FreeLoad())).Append(") · 水手 ").Append(play.TotalSailors)
              .Append(" · 士气 <color=").Append(play.Morale < 50 ? "#FF7766" : "#BFE0B0").Append(">").Append((int)play.Morale).Append("</color>")
              .Append(" · 病号 ").Append(play.SickCount)
              .Append(" · 续航 <color=#FFD76A>").Append(play.EnduranceDays).Append("</color>日 · 货种 ").Append(HoldKinds()).Append(" 类");
            return sb.ToString();
        }

        void RefreshDock()
        {
            // 港口专属动作(行情/休整/船坞/情报)只有真在港才可用; 海上抛锚停着仍能选目标出航
            bool port = play.State == SeaPlay.Mode.Docked;
            _dock["market"].interactable = port;
            _dock["rest"].interactable = port;
            _dock["yard"].interactable = port;
            _dock["intel"].interactable = port;

            // 出航: 选中一座城 或 海面插旗点 都能出航
            _dock["depart"].interactable = play.HasSailTarget;

            var cap = _dock["market"].GetComponentInChildren<Text>();
            if (cap != null) cap.text = port ? (_tradeOpen ? "收起行情" : "本港行情 ◈") : "本港行情 ◈";

            string g;
            if (play.Dest != null)
            {
                int n = play.TargetDays;
                g = "目标: <color=#FFD76A>" + play.Dest.Name + " · " + play.Dest.Area.Name + "</color> · 约 " + n
                  + " 日航程 —— 给养够吗? 不够进左下 <color=#7FE0FF>⚓ 舰队</color> 补给, 然后按 <color=#FFD76A>出航 ▶</color>。";
            }
            else if (play.ExploreSet)
            {
                int n = play.TargetDays;
                g = "<color=#FF7766>🚩 探索目标</color>: 海图上插旗那处(非港口) · 约 " + n
                  + " 日航程, 到点抛锚。按 <color=#FFD76A>出航 ▶</color> 驶去; 点别处海面可挪旗, 点发光圆球则改去那座城。";
            }
            else if (port)
            {
                g = _tradeOpen
                    ? "这是 <color=#FFD76A>" + play.Current.Name + "</color> 的行情: 绿色 ◆ = 本地盛产便宜。买 / 卖分栏各有 <color=#7FE0FF>1件 · 10件 · 全仓</color> 三键 —— 行情看够了点右上 <color=#7FE0FF>✕</color> 收起。"
                    : "点海图上任意一颗 <color=#7FE0FF>发光圆球</color>(另一座城)设为航行目标; 或点任意一处<color=#FF7766>海面</color>插 <color=#FF7766>🚩 红旗</color> 自由探索(不靠港、到点抛锚) —— 设好后这里亮起金色 <color=#FFD76A>出航 ▶</color>。想先看本地买卖? 点 <color=#FFD76A>本港行情</color>。";
            }
            else
            {
                g = "船在海上<color=#FF7766>抛锚</color>, 此处 <color=#FF7766>🚩 红旗</color> 就是船所在。点一座<color=#7FE0FF>发光圆球</color>(城市)驶去做买卖/补给, 或点一处<color=#FF7766>海面</color>插新旗继续自由探索 —— 然后按 <color=#FFD76A>出航 ▶</color>。";
            }
            SetGuide(g);
        }

        void SetGuide(string s)
        {
            // guide 是 dock 面板第一个子 Text
            var g = _dockGo.transform.Find("txt") as RectTransform;
            if (g == null) return;
            var t = g.GetComponent<Text>();
            if (t != null) t.text = s;
        }

        void RefreshSail()
        {
            float pr = Mathf.Clamp01(play.SailProgress01);
            string dName = play.Dest != null ? play.Dest.Name : (play.ExploreSet ? "海图 🚩 目标点" : "??");
            if (_sailTitle != null)
                _sailTitle.text = "驶向 " + dName + " · 第 " + play.DaysDone + " / " + play.PlannedDays + " 日";
            if (_sailBar != null)
            {
                int bar = (int)(pr * 26f);
                _sailBar.text = "<color=#FFD76A>" + new string('█', bar) + "</color>" + new string('░', 26 - bar) + "  " + (int)(pr * 100) + "%";
            }
        }

        void RefreshTrade()
        {
            var cur = play.Current;
            if (cur == null) return;
            _tradeTitle.text = "本港行情 · " + cur.Name + " · " + cur.Area.Name
                + "    (◆特产便宜 / ▲这里抢手 → 运到这里来卖)";

            // 按 **行** 刷, 不是按数据序刷: 第 i 行装的是 _rowGoods[i](排序后可能是任何一件货)。
            //   行控件与按钮都按行号走, 所以这里是**唯一**需要认表的地方。
            for (int i = 0; i < _rowGoods.Count && i < _tName.Count; i++)
            {
                var g = _rowGoods[i];
                int ak = play.engine.AskPrice(cur, g);
                int bd = play.engine.BidPrice(cur, g);
                int st = play.engine.BuyStock(cur, g);
                int hold = play.GoodInHold(g.Id);

                float ratio = (float)ak / Mathf.Max(1, g.BasePrice);
                _tAsk[i].text = ak.ToString();
                _tAsk[i].color = ratio < 0.8f ? ColGreen : ratio > 1.3f ? ColRed : Color.white;
                _tBid[i].text = bd.ToString();
                _tBid[i].color = ratio > 1.3f ? ColGreen : ratio < 0.8f ? ColRed : Color.white;
                _tHold[i].text = hold > 0 ? hold.ToString() : "";
                _tStock[i].text = st.ToString();
                // 成本 = 自己持仓的买入均价(方便算利润); 没持仓 / 账本缺记录 → "—"
                int avg = hold > 0 ? play.AvgHoldCost(g.Id) : -1;
                _tCost[i].text = avg >= 0 ? avg.ToString() : "—";
                _tCost[i].color = avg >= 0 ? Color.white : ColDim;   // 成本数用白色(与买/卖价中性色一致, 不再偏暖金)
                bool produced = Contains(cur.Specialties, g.Id);
                bool imported = Contains(cur.Imports, g.Id);
                _tName[i].color = produced ? ColProd : imported ? ColImpo : ColTxt;
                _tName[i].text = g.Name + (produced ? " ◆" : imported ? " ▲" : "");
                // 买1/买10/全买 三键同用一组可用性; 卖1/卖10/全卖 另同组。
                // 至少买得起 1 件且有舱位 + 港内有货才能买; 手里有货才能卖。
                if (i < _buy.Count)
                {
                    bool canBuy = st > 0 && play.FreeLoad() > 0 && play.Cash >= ak;
                    for (int b = 0; b < _buy[i].Length; b++) _buy[i][b].interactable = canBuy;
                }
                if (i < _sell.Count)
                {
                    bool canSell = hold > 0;
                    for (int s = 0; s < _sell[i].Length; s++)
                    {
                        _sell[i][s].interactable = canSell;
                        // 键底色: 手里有这件货 → **金黄**(买入键原来那个黄: 意思变成"这一行有东西可卖");
                        //   没货 → 青底(与底栏动作键同色)。黄 = 能动手, 于是整张表一眼扫出"哪几行是我的货"——
                        //   而排序恰好把手里有货的顶到最上面几行, 两者指同一批行, 不打架。
                        //   (金黄与 interactable 是同一个条件, 所以不会出现"金黄的灰键"那种读不清的搭配。)
                        var im = (i < _sellImg.Count && s < _sellImg[i].Length) ? _sellImg[i][s] : null;
                        if (im != null)
                        {
                            Color wantFill = canSell ? ColGoldBtn : ColBtn;
                            if (im.color != wantFill) im.color = wantFill;
                        }
                        // 没持仓的货: 卖出键不只置灰底, 键字也转暗淡 —— 不再一片亮白诱人误点。
                        //   有货时键字转**深褐**(金底上再放白字会糊成一片, 这正是买入键原来的配色)。
                        var lb = (i < _sellLbl.Count && s < _sellLbl[i].Length) ? _sellLbl[i][s] : null;
                        if (lb != null)
                        {
                            Color want = canSell ? new Color(0.15f, 0.09f, 0.04f) : new Color(0.45f, 0.52f, 0.60f, 0.85f);
                            if (lb.color != want) lb.color = want;
                        }
                    }
                }
            }
            RefreshRowHighlights();
        }

        // ---- 行情行高亮: 悬停微亮 / 点中选中金亮(防止点错行) ----
        public void OnRowClicked(int i)
        {
            if (i >= 0 && i < _rowBand.Count) _selRow = i;
        }

        void RefreshRowHighlights()
        {
            if (_marketVp == null || _rowRt.Count == 0) return;
            Vector2 m = Input.mousePosition;
            int h = -1;
            // 指针必须在滚动视窗里, 再找它正压着的那一行(滚轮滚动/上下拖动时也跟得准)
            if (RectTransformUtility.RectangleContainsScreenPoint(_marketVp, m, null))
            {
                for (int i = 0; i < _rowRt.Count; i++)
                    if (RectTransformUtility.RectangleContainsScreenPoint(_rowRt[i], m, null)) { h = i; break; }
            }
            _hoverRow = h;
            for (int i = 0; i < _rowBand.Count; i++)
            {
                var c = RowBandColor(i);
                if (_rowBand[i].color != c) _rowBand[i].color = c;
            }
        }

        Color RowBandColor(int i)
        {
            if (i == _selRow) return new Color(1f, 0.84f, 0.36f, 0.24f);    // 选中的行: 金亮
            if (i == _hoverRow) return new Color(1f, 0.95f, 0.80f, 0.13f);  // 悬停的行: 微微亮
            return i % 2 == 1 ? new Color(1f, 1f, 1f, 0.05f) : new Color(1f, 1f, 1f, 0.02f);
        }

        int HoldKinds()
        {
            var seen = new HashSet<string>();
            foreach (var s in play.fleet.Ships)
                foreach (var k in s.Cargo.Keys) seen.Add(k);
            return seen.Count;
        }

        void PlacePortLabels()
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;
            var world = play.world;
            string curId = play.Current != null ? play.Current.Id : "";
            string destId = play.Dest != null ? play.Dest.Id : "";
            float scale = 1f;
            var cv = transform.GetComponentInChildren<Canvas>();
            if (cv != null) scale = Mathf.Max(0.05f, cv.scaleFactor);
            Vector2 mouse = Input.mousePosition;
            bool anyOpen = AnyTopOpen;   // 任一信息/对话框浮层开着 → 地名全藏(地名永不在面板上跃层)

            // 地名 keep-out: 常驻 HUD 面板的屏幕矩形(顶部目标条 / 左上舰况 / 右日志 / 底部码头条或航行条)。
            //   地名已垫在这些面板之下; 这里再多藏掉"正好压住面板框"的标签, 免得面板边上半截字透出来。
            var block = new List<Rect>(5);
            AddBlock(block, _goalRt);
            AddBlock(block, _statusRt);
            AddBlock(block, _logRt);
            AddBlock(block, _dockRt, _dockGo);
            AddBlock(block, _sailRt, _sailGo);

            foreach (var p in world.Ports)
            {
                if (!_labels.TryGetValue(p.Id, out var lb)) continue;
                var mt = play.MarkerTransform(p.Id);
                if (mt == null) { lb.gameObject.SetActive(false); continue; }
                // 锚点 = 光球顶点(MarkerBallTopY 与 BuildMarker 同源), 再轻微上下浮动。
                //   港标的局部 +Y 就是该处的地表法线(见 SeaPlay.BuildMarker 挂的球面局部系),
                //   所以"往球球顶点上方偏"要沿 mt.up, 不能再沿世界 +Y。
                Vector3 nrm = mt.up;
                Vector3 top = mt.position + nrm * (play.MarkerBallTopY(p) + Mathf.Sin(Time.time * 0.9f + p.Lon) * 0.05f);
                Vector3 sp = _cam.WorldToScreenPoint(top);
                // 背面判定: WorldToScreenPoint 只是**投影**, 地球另一面的港照样有一个合法屏幕坐标,
                //   光看 sp.z 挡不住(它们仍在相机前方) → 那些地名会浮在球面上, 像贴在正面的假地名。
                //   平图时代没有这个问题(没有"另一面")。
                //   判据用"地表法线与'港→相机'同侧", 而不是"与相机视线同向": 后者在画面边缘
                //   (fov 60° 的四角)会把明明还看得见的港误判成背面, 一圈港名会莫名其妙消失。
                bool facing = Vector3.Dot(nrm, _cam.transform.position - mt.position) > 0f;
                bool behind = sp.z < 0.1f || !facing;
                bool cursorNear = !behind && Vector2.Distance(new Vector2(sp.x, sp.y), mouse) < 52f;
                // 浮层(行情/舰队/确认/出航检查)开着 → 所有地名全隐藏(本港/目标/悬停都不出现);
                // 平时: 当前港 / 目标港 / 鼠标近处 / 大港 常显 —— 但一律还要过了黑雾闸: 未探明的港连名字都不露。
                bool show = !anyOpen && !behind && play.CityRevealed(p) && (curId == p.Id || destId == p.Id || cursorNear || p.Size >= 3);
                var rr = Rt(lb.gameObject);
                rr.anchoredPosition = new Vector2(sp.x / scale, sp.y / scale + 15f);   // 固定 +15px → 名字恒在光球顶上方不叠字
                lb.color = curId == p.Id ? new Color(1f, 0.9f, 0.5f) : destId == p.Id ? new Color(1f, 1f, 1f) : play.PortColor(p);
                lb.fontSize = (curId == p.Id || destId == p.Id) ? 17 : 14;
                if (show && block.Count > 0)
                {
                    // 标签屏幕框(宽150字位、高18, 底缘在光球顶点上方 15px); 与任一常驻面板相交 → 不放(不盖按钮/日志)
                    Rect lr = new Rect(sp.x - 75f * scale, sp.y + 15f * scale, 150f * scale, 18f * scale);
                    for (int bi = 0; bi < block.Count; bi++)
                        if (lr.Overlaps(block[bi])) { show = false; break; }
                }
                lb.gameObject.SetActive(show);
            }
        }

        static void AddBlock(List<Rect> list, RectTransform rt, GameObject gate = null)
        {
            if (rt == null) return;
            if (gate != null && !gate.activeSelf) return;
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            float x0 = c[0].x, x1 = c[0].x, y0 = c[0].y, y1 = c[0].y;
            for (int i = 1; i < 4; i++)
            {
                x0 = Mathf.Min(x0, c[i].x); x1 = Mathf.Max(x1, c[i].x);
                y0 = Mathf.Min(y0, c[i].y); y1 = Mathf.Max(y1, c[i].y);
            }
            list.Add(Rect.MinMaxRect(x0, y0, x1, y1));
        }

        // =============================================================
        // 情报站 · 质感「羊皮海图」: 整窗老羊皮纸 + 赭墨正文 + 墨绿刊头/表头,
        //   航海红(涨/封蜡) × 常绿(跌/产地), 青铜钮、红蜡封条 —— 一块绘图房案。
        //   大厅(贸易日报 / 全球商情报 / 我的订阅) / 报纸翻页 / 表格 / 封面书架。
        //   每份刊物进 play.IntelArchive(随存读档); 日报存正文快照, 商情报留订阅记录、
        //   阅读时按最新行情重绘 → 我的订阅即历史卷宗抽屉。
        // =============================================================
        static readonly Color
            P_parch = new Color(0.855f, 0.760f, 0.575f, 1f),   // 窗/桌面 羊皮纸
            P_sheet = new Color(0.93f, 0.860f, 0.690f, 1f),    // 纸页/服务卡(亮一档)
            P_sheetD = new Color(0.82f, 0.720f, 0.520f, 1f),   // 表带/选中(暗一档)
            P_ink   = new Color(0.235f, 0.170f, 0.090f, 1f),   // 正文墨
            P_soft  = new Color(0.450f, 0.360f, 0.200f, 1f),   // 注文/次要
            P_navy  = new Color(0.100f, 0.290f, 0.350f, 1f),   // 墨绿: 刊头/表头
            P_rust  = new Color(0.600f, 0.240f, 0.130f, 1f),   // 航海红: 涨/重点/封蜡
            P_moss  = new Color(0.220f, 0.450f, 0.300f, 1f),   // 常绿: 跌/产地
            P_gold  = new Color(0.580f, 0.410f, 0.100f, 1f),   // 报价强调(深金)
            P_bronze = new Color(0.400f, 0.270f, 0.140f, 1f),  // 按钮青铜
            P_btnTx = new Color(0.970f, 0.930f, 0.800f, 1f),   // 钮上米字
            P_rule  = new Color(0.520f, 0.400f, 0.190f, 1f);   // 细线
        static readonly Color
            P_coverNavy  = new Color(0.100f, 0.280f, 0.340f, 1f),  // 商情报封面
            P_coverOlive = new Color(0.300f, 0.370f, 0.200f, 1f),  // 日报封面(单号)
            P_coverWine  = new Color(0.520f, 0.180f, 0.120f, 1f);  // 日报封面(双号)
        List<SeaArea> _rptAreas = new List<SeaArea>();   // 航区顺序(按港口首现, dict 转序)
        static readonly float[] RptW = { 150f, 150f, 80f, 50f, 150f, 80f, 50f, 270f };  // 商情报列宽(合计 980)
        static readonly string[] RptHd = { "货物", "最低进价产地", "供价", "走势", "最高求价销地", "求价", "走势", "单件毛利" };
        static readonly TextAnchor[] RptAl = {
            TextAnchor.MiddleLeft, TextAnchor.MiddleLeft, TextAnchor.MiddleRight, TextAnchor.MiddleCenter,
            TextAnchor.MiddleLeft, TextAnchor.MiddleRight, TextAnchor.MiddleCenter, TextAnchor.MiddleRight };

        List<SeaArea> AreaOrder()
        {
            var list = new List<SeaArea>();
            if (play == null || play.world == null) return list;
            foreach (var p in play.world.Ports)
                if (p.Area != null && !list.Contains(p.Area)) list.Add(p.Area);
            return list;
        }

        void BuildIntel(Transform root)
        {
            _newsGo = Panel("newsOverlay", root, new Color(0f, 0f, 0f, 0.52f));
            RootRect(Rt(_newsGo));
            var win = Panel("win", _newsGo.transform, P_parch);
            RectAt(Rt(win), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0f, 4f), new Vector2(1040f, 600f));
            // 顶上一条红蜡封条; 绘图房名号不占报头, 挪到本窗左下角当一行小注(建页后置顶, 见下)
            var seal = Panel("seal", win.transform, P_rust);
            RectAt(Rt(seal), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -1f), new Vector2(0f, 4f));
            var cap = AddText(win.transform, "情报站 · 绘图房的消息案", 12, P_soft, TextAnchor.MiddleLeft);
            RectAt(Rt(cap.gameObject), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(16f, 6f), new Vector2(320f, 18f));
            var close = MakeBtn(win.transform, "close", "✕", 15, P_bronze, P_btnTx);
            RectAt(Rt(close.gameObject), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-14f, -8f), new Vector2(42f, 38f));
            close.onClick.AddListener(() => _newsOpen = false);

            _newsHome = NewsPage("home", win.transform);
            _newsPaper = NewsPage("paper", win.transform);
            _newsReport = NewsPage("report", win.transform);
            _newsSubs = NewsPage("subs", win.transform);
            BuildNewsHome(_newsHome.transform);
            BuildNewsPaper(_newsPaper.transform);
            BuildNewsReport(_newsReport.transform);
            BuildNewsSubs(_newsSubs.transform);
            ApplyNewsPage();
            cap.transform.SetAsLastSibling();   // 左下角小注浮在所有纸页之上(该角落无正文)
            _newsGo.SetActive(false);
        }

        static GameObject NewsPage(string n, Transform win)
        {
            var p = new GameObject("pg_" + n, typeof(RectTransform));
            p.transform.SetParent(win, false);
            var r = (RectTransform)p.transform;
            r.anchorMin = new Vector2(0f, 0f); r.anchorMax = new Vector2(1f, 1f);
            r.offsetMin = new Vector2(16f, 26f); r.offsetMax = new Vector2(-16f, -50f);
            // 底边留 26px 给窗口左下角那行「绘图房消息案」小注(纸张内容不与之相碰)
            return p;
        }

        void ApplyNewsPage()
        {
            bool h = _newsPage == "home", p = _newsPage == "paper",
                  r = _newsPage == "report", s = _newsPage == "subs";
            if (_newsHome != null) _newsHome.SetActive(h);
            if (_newsPaper != null) _newsPaper.SetActive(p);
            if (_newsReport != null) _newsReport.SetActive(r);
            if (_newsSubs != null) _newsSubs.SetActive(s);
        }

        static void BtnText(Button b, string s)
        {
            if (b == null) return;
            var t = b.GetComponentInChildren<Text>();
            if (t != null) t.text = s;
        }
        static void ClearKids(Transform t)
        {
            if (t == null) return;
            for (int i = t.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
        }

        // ---------------- 大厅: 三份服务(像卷宗抽屉上叠的纸页) ----------------
        void ShowNewsHome()
        {
            _newsPage = "home";
            ApplyNewsPage();
            RefreshNewsHomeButtons();
        }
        void RefreshNewsHomeButtons()
        {
            if (play == null || _newsBuyBtn == null) return;
            bool ownP = play.IntelPaperOwnedToday;
            bool ownR = play.IntelReportMonthOwned;
            int cnt = play.IntelArchiveCount;
            long gold = play.fleet != null ? play.fleet.Gold : 0;
            BtnText(_newsBuyBtn, ownP
                ? "翻阅今日《" + SeaPlay.IntelPaperName + "》\n(今日已购)"
                : "买下今日《" + SeaPlay.IntelPaperName + "》\n" + SeaPlay.IntelPaperCost + " 金");
            BtnText(_newsRptBtn, ownR
                ? "查看本月《" + SeaPlay.IntelReportName + "》\n(本月已订)"
                : "订阅本月《" + SeaPlay.IntelReportName + "》\n" + SeaPlay.IntelReportCost + " 金");
            BtnText(_newsSubsBtn, cnt > 0
                ? "📚 我的订阅\n已收 " + cnt + " 卷 · 拆封 ▸"
                : "📚 我的订阅\n卷宗还空着");
            _newsBuyBtn.interactable = ownP || gold >= SeaPlay.IntelPaperCost;
            _newsRptBtn.interactable = ownR || gold >= SeaPlay.IntelReportCost;
            _newsSubsBtn.interactable = true;
        }

        void BuildNewsHome(Transform home)
        {
            float rowH = 152f, gap = 10f;
            BuildServiceRow(home, 8f, rowH,
                "《 贸 易 日 报 》 · 每日一刊",
                "全海最贱的进价在哪、最抢手的货该往哪卖 —— 一版说清, 上岸随手捎一份。\n"
                + "· 各港 特产供货/缺货求购 + 进价·存量 + 求价·约可收量\n"
                + "· 晨报速递: 抄底坐标 / 出手坐标 / 单件毛利榜\n"
                + "<color=#9E4A24>刊价 " + SeaPlay.IntelPaperCost + " 金 · 明日出新刊</color> · 纸面只反映付印那刻的行情",
                ref _newsBuyBtn);
            BuildServiceRow(home, 8f + rowH + gap, rowH,
                "《 全 球 商 情 报 》 · 按月订阅",
                "一个航区一张表: 在产/在求的货横着排开, 供价到求价一眼看穿。\n"
                + "· 各货: 最低进价产地 → 最高求价销地 → 单件毛利\n"
                + "· 涨跌箭头 = 相对约 7 天前的行情走向\n"
                + "<color=#9E4A24>月订 " + SeaPlay.IntelReportCost + " 金</color> · 表格随最新行情重绘",
                ref _newsRptBtn);
            BuildServiceRow(home, 8f + 2 * (rowH + gap), rowH,
                "《 我 的 订 阅 》 · 历史刊物都收在卷宗里",
                "买过的每一份 日报 / 商情报 都留了档, 想翻哪本就点哪本的封皮。\n"
                + "· 旧《贸易日报》: 打开即那一期正文, 按版面翻页\n"
                + "· 旧《全球商情报》: 记下订阅月份, 打开以最新行情重绘\n"
                + "<color=#1A454F>随存读档</color> · 卷宗永久保留",
                ref _newsSubsBtn);
            _newsBuyBtn.onClick.AddListener(OnNewsBuyPaper);
            _newsRptBtn.onClick.AddListener(OnNewsBuyReport);
            _newsSubsBtn.onClick.AddListener(OnNewsOpenSubs);
        }

        void BuildServiceRow(Transform home, float bottomY, float rowH, string title, string desc, ref Button btn)
        {
            var row = Panel("row", home, P_sheet);
            RectAt(Rt(row), new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, bottomY), new Vector2(0f, rowH));
            var t = AddText(row.transform, title, 18, P_navy, TextAnchor.UpperLeft);
            RectAt(Rt(t.gameObject), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(18f, -10f), new Vector2(660f, 26f));
            var b = AddText(row.transform, desc, 12, P_ink, TextAnchor.UpperLeft);
            RectAt(Rt(b.gameObject), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(18f, -42f), new Vector2(650f, rowH - 54f));
            btn = MakeBtn(row.transform, "buy", "……", 13, P_bronze, P_btnTx);
            RectAt(Rt(btn.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-22f, 0f), new Vector2(228f, rowH - 24f));
        }

        void OnNewsBuyPaper()
        {
            if (play == null) return;
            if (play.IntelPaperOwnedToday) { var e = play.IntelPaperTodayIssue(); if (e != null) ShowNewsPaper(e); return; }
            var got = play.IntelBuyPaper();
            if (got != null) ShowNewsPaper(got);
        }
        void OnNewsBuyReport()
        {
            if (play == null) return;
            if (play.IntelReportMonthOwned) { var e = play.IntelReportMonthIssue(); if (e != null) ShowNewsReport(e); return; }
            var got = play.IntelBuyReport();
            if (got != null) ShowNewsReport(got);
        }
        void OnNewsOpenSubs()
        {
            _newsPage = "subs";
            RebuildSubsList();
            ApplyNewsPage();
        }

        // ---------------- 我的订阅: 卷宗抽屉(历史刊物, 随存读档) ----------------
        void BuildNewsSubs(Transform subs)
        {
            var back = MakeBtn(subs.transform, "back", "◀ 大厅", 14, P_bronze, P_btnTx);
            RectAt(Rt(back.gameObject), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(2f, -6f), new Vector2(100f, 32f));
            back.onClick.AddListener(ShowNewsHome);
            var hd = AddText(subs.transform, "《 我 的 订 阅 》", 19, P_navy, TextAnchor.MiddleCenter);
            RectAt(Rt(hd.gameObject), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -4f), new Vector2(0f, 28f));
            var hrule = Panel("hrule", subs.transform, P_rule);
            RectAt(Rt(hrule), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -38f), new Vector2(0f, 1f));

            var vpGo = Panel("vp", subs.transform, P_sheet);
            var vp = Rt(vpGo);
            vp.anchorMin = Vector2.zero; vp.anchorMax = Vector2.one;
            vp.offsetMin = new Vector2(12f, 8f); vp.offsetMax = new Vector2(-12f, -48f);
            vpGo.AddComponent<Mask>().showMaskGraphic = false;
            var sr = vpGo.AddComponent<ScrollRect>();
            sr.horizontal = false; sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 30f;
            var content = NewRT("content", vpGo.transform);
            _subsRoot = Rt(content);
            _subsRoot.anchorMin = new Vector2(0f, 1f); _subsRoot.anchorMax = new Vector2(1f, 1f);
            _subsRoot.pivot = new Vector2(0.5f, 1f);
            _subsRoot.anchoredPosition = Vector2.zero;
            _subsRoot.sizeDelta = new Vector2(0f, 200f);
            sr.viewport = vp; sr.content = _subsRoot;
        }

        void RebuildSubsList()
        {
            if (play == null || _subsRoot == null) return;
            ClearKids(_subsRoot);
            var list = play.IntelArchiveCount > 0 ? play.IntelArchiveNewestFirst() : new List<SeaIntelSave>();
            int n = list.Count;
            var tip = AddText(_subsRoot, n == 0
                ? "卷宗还空着 —— 去《情报站》大厅买《贸易日报》或订《全球商情报》, 它们会一份份收进这里。"
                : "已收 " + n + " 卷 · 点封皮即可拆开(最新在前)", 12, P_soft, TextAnchor.UpperLeft);
            RectAt(Rt(tip.gameObject), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(10f, -6f), new Vector2(940f, 24f));
            if (n == 0) { _subsRoot.sizeDelta = new Vector2(0f, 60f); return; }

            const float cw = 306f, ch = 156f, gap = 14f;
            int cols = 3;
            for (int i = 0; i < n; i++)
            {
                int col = i % cols, row = i / cols;
                float x = 8f + col * (cw + gap);
                float y = -38f - row * (ch + gap);
                SubsCover(_subsRoot, x, y, cw, ch, list[i]);
            }
            int rows = (n + cols - 1) / cols;
            _subsRoot.sizeDelta = new Vector2(0f, 38f + rows * (ch + gap) + 12f);
        }

        void SubsCover(Transform parent, float x, float y, float w, float h, SeaIntelSave e)
        {
            bool rep = e != null && e.kind == "report";
            Color paperC = rep ? P_coverNavy : (e != null && e.key % 2 == 0 ? P_coverWine : P_coverOlive);
            Color ribbon = rep ? P_gold : (e != null && e.key % 2 == 0 ? P_gold : P_rust);
            Color cream = new Color(0.98f, 0.95f, 0.86f, 1f);
            var go = NewRT("cover", parent);
            RectAt(Rt(go), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(x, y), new Vector2(w, h));
            var bg = go.AddComponent<Image>();
            bg.sprite = White();
            bg.color = paperC;
            // 顶部色带(书脊)
            var rib = NewRT("rib", go.transform).AddComponent<Image>();
            rib.sprite = White();
            rib.color = ribbon;
            RectAt(Rt(rib.gameObject), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -1f), new Vector2(0f, 6f));

            string title = e != null ? e.title : "……";
            string sub = e != null
                ? (rep ? "订阅存档 · " : "正文快照 · ") + (e.sub ?? "")
                : "";
            var tag = AddText(go.transform, rep ? "⚜ 全球商情报" : "📰 贸易日报", 12, cream, TextAnchor.MiddleLeft);
            RectAt(Rt(tag.gameObject), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(12f, -14f), new Vector2(w - 24f, 20f));
            var tl = AddText(go.transform, title, 16, cream, TextAnchor.MiddleLeft);
            RectAt(Rt(tl.gameObject), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(12f, -42f), new Vector2(w - 24f, 26f));
            var sl = AddText(go.transform, sub, 11, new Color(0.86f, 0.82f, 0.70f, 1f), TextAnchor.UpperLeft);
            RectAt(Rt(sl.gameObject), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(12f, -84f), new Vector2(w - 24f, 42f));
            var foot = AddText(go.transform, rep ? "本月行情一表观 · 拆封 ▸" : "翻一页, 回到那一日 ▸", 12, new Color(0.96f, 0.90f, 0.72f, 1f), TextAnchor.MiddleRight);
            RectAt(Rt(foot.gameObject), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-14f, -22f), new Vector2(w - 28f, 20f));
            var open = MakeBtn(go.transform, "open", "", 12, new Color(1f, 1f, 1f, 0.012f), P_btnTx, clickSfx: false);
            RectAt(Rt(open.gameObject), new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero);
            open.onClick.AddListener(() => OpenSubsIntel(e));
        }

        void OpenSubsIntel(SeaIntelSave e)
        {
            if (e == null) return;
            if (e.kind == "report") ShowNewsReport(e);
            else ShowNewsPaper(e);
        }

        // ---------------- 《贸易日报》: 翻页报纸(正文按"块"排成版面, 羊皮纸刊) ----------------
        void BuildNewsPaper(Transform paper)
        {
            var back = MakeBtn(paper.transform, "back", "◀ 大厅", 14, P_bronze, P_btnTx);
            RectAt(Rt(back.gameObject), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(8f, -6f), new Vector2(100f, 32f));
            back.onClick.AddListener(ShowNewsHome);
            // 版面量度: 整幅宽 / 每版高(略小于 vp 裁口, 防裁字); 栏宽随页栏数算(头版单栏 = 整幅, 行市版 4 栏 = 窄栏)
            _pageW = 980f; _pageH = 408f;

            // 版面主体: 羊皮纸刊页, 蒙版裁切(一版一版翻, 不用滚)。顶部整幅让给正文,
            // 报名/期日不再浮在每版顶部, 而是随"刊脚"压在报纸最下方。
            var vpGo = Panel("body", paper.transform, P_sheet);
            var vp = Rt(vpGo);
            vp.anchorMin = Vector2.zero; vp.anchorMax = Vector2.one;
            vp.offsetMin = new Vector2(6f, 68f); vp.offsetMax = new Vector2(-6f, -44f);
            vpGo.AddComponent<Mask>().showMaskGraphic = false;

            // 正文栏: 栏间留浅沟、不画任何竖线。建 4 栏并排, 每版翻到时再按该版栏数重排几何(头版 1 栏拉通, 行市版 4 栏)。
            _paperCols = new Text[PaperCols];
            float cw0 = ColWidthFor(PaperCols);
            for (int c = 0; c < PaperCols; c++)
            {
                var col = AddText(vpGo.transform, "", 12, P_ink, TextAnchor.UpperLeft);
                RectAt(Rt(col.gameObject), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(6f + c * (cw0 + PaperGap), 0f), new Vector2(cw0, _pageH));
                _paperCols[c] = col;
            }

            // 报纸最下方刊脚: 《贸易日报》 · 期日 · 第 X/N 版(随翻版更新)
            _paperFolio = AddText(paper.transform, "", 13, P_rust, TextAnchor.MiddleCenter);
            RectAt(Rt(_paperFolio.gameObject), new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 44f), new Vector2(0f, 20f));

            _paperPrev = MakeBtn(paper.transform, "prev", "◀ 上一版", 13, P_bronze, P_btnTx);
            RectAt(Rt(_paperPrev.gameObject), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(-158f, 8f), new Vector2(120f, 30f));
            _paperPrev.onClick.AddListener(() => PaperGo(-1));
            _paperNext = MakeBtn(paper.transform, "next", "下一版 ▶", 13, P_bronze, P_btnTx);
            RectAt(Rt(_paperNext.gameObject), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(158f, 8f), new Vector2(120f, 30f));
            _paperNext.onClick.AddListener(() => PaperGo(1));
        }

        // ---- 报纸排版内核: 按真实栏宽折行测高(与渲染同一字体/字号/富文本) ----
        //   关键约定: 正文里不掺 <size> 富文本(测高引擎根本不认它, 会造成"测 12、渲 8"错位);
        //   字号一律由「整版统一字号 px」控制 —— 先设到分栏 Text 上再量, 测高与渲染逐字节一致。
        //   注意: 带富文本(<color>/<b>)的多行整串一次 GetPreferredHeight 会把行高虚高
        //   (9 行逐行各 19px, 整串却报 ~267px —— Unity 对硬换行 + 标签段会多算空行)。
        //   硬换行使各行排版互不影响 → 逐行测高求和才是真实堆叠高度; 超宽行会自行折行并被逐行计入。
        float PaperGenH(string s, int px, float w)
        {
            var col = (_paperCols != null && _paperCols.Length > 0) ? _paperCols[0] : null;
            if (col == null) return 16f;                       // 兜底(栏未建, 理论不会走到)
            col.fontSize = px;
            var gs = col.GetGenerationSettings(new Vector2(w, 100000f));
            var gen = new TextGenerator();
            if (s.IndexOf('\n') < 0) return gen.GetPreferredHeight(s, gs);
            float h = 0f;
            foreach (var ln in s.Split('\n'))
                h += gen.GetPreferredHeight(ln, gs);
            return h;
        }
        // 某页栏数对应的栏宽: 头版单栏 = 整幅宽; 多栏并排则扣去栏沟平分。
        float ColWidthFor(int n) { return n <= 1 ? _pageW : (_pageW - PaperGap * (n - 1)) / n; }
        // 块归类: 晨报速递/刊首提示(头版通栏) vs 城市行情块(走 4 栏, 顶满整版)。
        //   城市块首行以粗体 ◆城名(或 ✖封锁城)开头; 速递标题是粗体 ◆ 后接"晨报速递"; 刊首提示没有粗体头标。
        static bool LooksFront(string t)
        {
            int i = t.IndexOf("<b>◆", System.StringComparison.Ordinal);
            if (i >= 0)
            {
                string after = t.Substring(i + 4).TrimStart();
                return after.StartsWith("晨报速递", System.StringComparison.Ordinal);
            }
            return t.IndexOf("<b>✖", System.StringComparison.Ordinal) < 0;
        }
        // 去掉整版 <size=N></size> 富文本(老快照/旧正文都兼容), 色与粗体保留
        static string PaperCleanSize(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf("<size", System.StringComparison.Ordinal) < 0) return s;
            s = System.Text.RegularExpressions.Regex.Replace(s, "<size=\\d+>", "");
            return System.Text.RegularExpressions.Regex.Replace(s, "</size>", "");
        }
        // 老快照"长句规整"为当前短句(只缩短、不增字; 新刊本来就用短句 → 规则不命中 = 原样返回):
        //   · 删掉整行的 ━━ 大陆分段头(旧版每个分区/城块首行那条大字分隔线)
        //   · "供货 X 金 · 存量 Y 件" → "供 X · 存 Y"; "求购 X 金 · 约可收 Y 件" → "求 X · 约收 Y"
        //   短句在 236px 窄栏一货一行不折行 → 城市块不再两倍高, 每版才能排得下 8 城(旧版就是被折行撑爆的)。
        static string PaperSlimLegacy(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var keep = new System.Collections.Generic.List<string>();
            foreach (var raw in s.Split('\n'))
            {
                string line = raw;
                string plain = System.Text.RegularExpressions.Regex.Replace(line, "<[^>]+>", "");
                string tr = plain.Trim();
                if (tr.Length >= 2 && tr[0] == '━' && tr[tr.Length - 1] == '━') continue;   // ━━ 大陆 ━━ 整行删
                if (tr.Length == 0) continue;                                               // 规整后空行一并删
                if (line.IndexOf("● 特产", System.StringComparison.Ordinal) >= 0 && line.IndexOf("供货 ", System.StringComparison.Ordinal) >= 0)
                {
                    line = line.Replace("供货 ", "供 ");
                    line = line.Replace(" 金 · 存量 ", " · 存 ");
                    if (line.EndsWith(" 件", System.StringComparison.Ordinal)) line = line.Substring(0, line.Length - 2);
                }
                else if (line.IndexOf("▲ 缺货", System.StringComparison.Ordinal) >= 0 && line.IndexOf("求购 ", System.StringComparison.Ordinal) >= 0)
                {
                    line = line.Replace("求购 ", "求 ");
                    line = line.Replace(" 金 · 约可收 ", " · 约收 ");
                    if (line.EndsWith(" 件", System.StringComparison.Ordinal)) line = line.Substring(0, line.Length - 2);
                }
                keep.Add(line);
            }
            return string.Join("\n", keep);
        }
        // ---- 排版(报纸版口): 头版整版字号 11, 行市版 8 城/版、字号 15→9 自动适配, 主次靠颜色/粗体 ----
        //   第1版 = 晨报速递整版: 单栏通栏, 晨报块自上而下排满整幅宽, 放不下才再开一张头版。
        //   第2版起 = 分港行市: 每版正好 8 个城市的供需信息(尾版剩几放几)。8 城在 4 栏里按
        //   "当前最矮栏"平衡铺开(栏序不偏重); 8 城放不下当前字号 → 整版收字号(15→9)直到同版放稳,
        //   上一版尾部永不因放不下而被挤去下一页; 一城一块永不拆版。
        void PaginatePaper()
        {
            _paperColPages.Clear(); _paperColN.Clear(); _paperFonts.Clear();
            var parts = new List<string>();
            string body = _paperSig ?? "";
            if (body.Length > 0)
            {
                foreach (var b in body.Split(SeaPlay.IntelBlockSep))
                {
                    var t = PaperSlimLegacy(PaperCleanSize(b)).Trim();
                    if (t.Length > 0) parts.Add(t);
                }
            }
            // 分群: 晨报速递/刊首(头版通栏) vs 城市行情块(4 栏)
            var front = new List<string>();
            var cities = new List<string>();
            foreach (var t in parts)
                (LooksFront(t) ? front : cities).Add(t);
            if (front.Count == 0 && cities.Count == 0)
                front.Add(string.IsNullOrEmpty(_paperIssue)
                    ? "今日报纸还没送到案头。"
                    : "本刊正文尚缺 —— 回大厅买今日《" + SeaPlay.IntelPaperName + "》。");

            float cap = _pageH;
            const int Pf = 11;                     // 头版(晨报速递)整版字号; 行市版按 8 城一版另行定字号
            const int CityPerPage = 8;             // 行市版版口: 第 2 版起每版正好 8 个城市的供需信息
            const int CityFontMax = 15, CityFontMin = 9;   // 行市版字号可调域(第2版起放大: 8 城放得下尽量放大到 15, 挤不下才收到 9)

            // 行距是"测高即渲染"的一环: 头版量/渲用 PaperHeadLead, 行市版量/渲用 PaperMarketLead。
            if (_paperCols != null)
            {
                for (int c = 0; c < _paperCols.Length; c++)
                    if (_paperCols[c] != null) _paperCols[c].lineSpacing = PaperMarketLead;
            }

            void AddPage(string[] cols, int colN) { AddPageF(cols, colN, Pf); }
            void AddPageF(string[] cols, int colN, int px)
            {
                _paperColPages.Add(cols);
                _paperColN.Add(colN);
                _paperFonts.Add(px);
            }
            // 块间必以硬换行相接(块尾无 \n 则补一个), 使块各行独立、测高可按块累加
            string JoinB(string cur, string add)
            {
                if (cur == null || cur.Length == 0) return add;
                return cur.EndsWith("\n", System.StringComparison.Ordinal) ? cur + add : cur + "\n" + add;
            }
            // 把 [s0, s0+take) 的城块在 4 栏里"当前最矮栏"平衡铺开(栏序不偏重一侧);
            //   4 栏每栏都 ≤ 版高才算放得稳 → 返回栏文本; 否则返回 null(由调用方收字号重排)。
            string[] PackColumns(List<string> all, int s0, int take, int px, float w)
            {
                var txt = new string[PaperCols];
                var used = new float[PaperCols];
                for (int i = 0; i < take; i++)
                {
                    string blk = all[s0 + i];
                    float h = PaperGenH(blk, px, w);
                    int best = 0;
                    for (int c = 1; c < PaperCols; c++)
                        if (used[c] < used[best]) best = c;
                    txt[best] = JoinB(txt[best], blk);
                    used[best] += h;
                }
                for (int c = 0; c < PaperCols; c++)
                    if (used[c] > cap + 0.01f) return null;
                return txt;
            }

            // —— 头版: 晨报速递 · 单栏通栏(整幅宽) ——
            if (_paperCols != null && _paperCols[0] != null) _paperCols[0].lineSpacing = PaperHeadLead;
            string tcur = ""; float tu = 0f;
            for (int k = 0; k < front.Count; k++)
            {
                float h = PaperGenH(front[k], Pf, _pageW);
                if (tcur.Length > 0 && tu + h > cap)
                {
                    var cols = new string[PaperCols];
                    cols[0] = tcur;
                    AddPage(cols, 1);
                    tcur = ""; tu = 0f;
                }
                tcur = JoinB(tcur, front[k]);
                tu += h;
            }
            if (tcur.Length > 0)
            {
                var cols = new string[PaperCols];
                cols[0] = tcur;
                AddPage(cols, 1);
            }

            // —— 分港行市版: 每版 8 城(第 2 版即 8 城满版) ——
            //   尾版不做"剩几放几"(52 城 → 前 6 版各 8 + 尾版孤零零 4 城会显得空):
            //   把城市按整版数尽量均摊 —— 52 城 ÷ 7 版 = 每版 7~8 城(前几版 8、后几版 7), 尾版同样够满。
            //   每批在 4 栏里按"当前最矮栏"平衡铺开; 整批放不下当前字号 → 整版收字号(15→9)重排,
            //   直到同版放稳 —— 保证一城不拆版、上一版尾部不会被挤去下一页。
            float gw = ColWidthFor(PaperCols);
            // 测高复用 col0, 此刻切回行市版行距(头版测高刚把它设成 PaperHeadLead)
            if (_paperCols != null && _paperCols[0] != null) _paperCols[0].lineSpacing = PaperMarketLead;
            int nPages = (cities.Count + CityPerPage - 1) / CityPerPage;   // ≥1(cities 非空时)
            int lo = cities.Count / nPages;          // 每版起码 lo 城(向下取整)
            int hi = lo + 1;
            int nBig = cities.Count - lo * nPages;   // 前 nBig 版排 hi 城(≈8), 其后版排 lo 城(≈8 略少 1)
            for (int pi = 0, start = 0; pi < nPages && start < cities.Count; pi++)
            {
                int take = pi < nBig ? hi : lo;
                string[] cols = null;
                int px = Pf;
                for (int f = CityFontMax; f >= CityFontMin; f--)     // 从大到小试字号, 第一个放稳整版城市数的定稿
                {
                    cols = PackColumns(cities, start, take, f, gw);
                    if (cols != null) { px = f; break; }
                }
                if (cols != null)
                {
                    AddPageF(cols, PaperCols, px);
                    start += take;
                    continue;
                }
                // 极端兜底: 整批城市连 9 号(最小字号)都挤不进一版(几乎不可能) → 退回按栏高自然贪心翻页, 不裁字不丢城
                var txt = new string[PaperCols];
                var used = new float[PaperCols];
                int cc = 0, placed = 0;
                for (int k = start; k < cities.Count; k++)
                {
                    float h = PaperGenH(cities[k], CityFontMin, gw);
                    while (true)
                    {
                        if (used[cc] + h <= cap)
                        {
                            txt[cc] = JoinB(txt[cc], cities[k]);
                            used[cc] += h; placed++;
                            break;
                        }
                        if (placed > 0)
                        {
                            if (cc < PaperCols - 1) { cc++; continue; }
                            AddPageF(txt, PaperCols, CityFontMin);
                            txt = new string[PaperCols]; used = new float[PaperCols];
                            cc = 0; placed = 0;
                            continue;
                        }
                        txt[cc] = JoinB(txt[cc], cities[k]);
                        used[cc] += h; placed++;
                        break;
                    }
                }
                if (placed > 0) AddPageF(txt, PaperCols, CityFontMin);
                break;
            }
            if (_paperColPages.Count == 0) AddPage(new string[PaperCols], 1);
        }

        void PaperGo(int d)
        {
            int n = _paperColPages.Count;
            if (n == 0) { PaginatePaper(); n = _paperColPages.Count; }
            _paperIdx = Mathf.Clamp(_paperIdx + d, 0, n - 1);
            var page = _paperColPages[Mathf.Min(_paperIdx, n - 1)];
            // 每版翻到时按该版实际栏数重排几何 + 切整版统一字号(排版时测高用的就是它)
            int colN = (_paperColN != null && _paperIdx < _paperColN.Count) ? _paperColN[_paperIdx] : PaperCols;
            float cw = ColWidthFor(colN);
            int f = (_paperFonts != null && _paperIdx < _paperFonts.Count) ? _paperFonts[_paperIdx] : 11;
            for (int c = 0; c < PaperCols; c++)
            {
                if (_paperCols == null || c >= _paperCols.Length || _paperCols[c] == null) continue;
                var col = _paperCols[c];
                if (c < colN)
                {
                    col.gameObject.SetActive(true);
                    col.fontSize = f;
                    // 行距与排版测高对齐: 头版(单栏通栏)用 PaperHeadLead, 行市版(多栏)用 PaperMarketLead
                    col.lineSpacing = colN > 1 ? PaperMarketLead : PaperHeadLead;
                    RectAt(Rt(col.gameObject), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                        new Vector2(6f + c * (cw + PaperGap), 0f), new Vector2(cw, _pageH));
                    col.text = page != null && c < page.Length ? (page[c] ?? "") : "";
                }
                else
                {
                    col.text = "";
                    col.gameObject.SetActive(false);
                }
            }
            string issue = _paperIssue.Length > 0 ? " · " + _paperIssue : "";
            if (_paperFolio != null)
                _paperFolio.text = "《 贸 易 日 报 》" + issue + "    第 " + (_paperIdx + 1) + " / " + n + " 版";
            if (_paperPrev != null) _paperPrev.interactable = _paperIdx > 0;
            if (_paperNext != null) _paperNext.interactable = _paperIdx < n - 1;
        }

        void ShowNewsPaper(SeaIntelSave e)
        {
            if (play == null) return;
            if (e == null) e = play.IntelPaperTodayIssue();
            _openPaper = e;
            _newsPage = "paper";
            if (e != null)
            {
                _paperSig = e.body;
                _paperIssue = (e.title ?? "") + (string.IsNullOrEmpty(e.sub) ? "" : " · " + e.sub);
            }
            else
            {
                _paperSig = "";
                _paperIssue = "";
            }
            PaginatePaper();
            _paperIdx = 0;
            PaperGo(0);
            ApplyNewsPage();
        }

        // ---------------- 《全球商情报》: 一个航区 × 全部货物 一张表 ----------------
        void BuildNewsReport(Transform report)
        {
            var back = MakeBtn(report.transform, "back", "◀ 大厅", 14, P_bronze, P_btnTx);
            RectAt(Rt(back.gameObject), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(8f, -6f), new Vector2(100f, 32f));
            back.onClick.AddListener(ShowNewsHome);
            _rptCap = AddText(report.transform, "", 18, P_navy, TextAnchor.MiddleCenter);
            RectAt(Rt(_rptCap.gameObject), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -4f), new Vector2(0f, 28f));
            _rptNote = AddText(report.transform, "", 12, P_soft, TextAnchor.MiddleCenter);
            RectAt(Rt(_rptNote.gameObject), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -34f), new Vector2(0f, 22f));

            var pv = MakeBtn(report.transform, "aprev", "◀", 14, P_bronze, P_btnTx, clickSfx: false);
            RectAt(Rt(pv.gameObject), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(-160f, -62f), new Vector2(56f, 30f));
            pv.onClick.AddListener(() => StepReportArea(-1));
            var nx = MakeBtn(report.transform, "anext", "▶", 14, P_bronze, P_btnTx, clickSfx: false);
            RectAt(Rt(nx.gameObject), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(160f, -62f), new Vector2(56f, 30f));
            nx.onClick.AddListener(() => StepReportArea(1));
            _rptAreaLbl = AddText(report.transform, "", 15, P_ink, TextAnchor.MiddleCenter);
            RectAt(Rt(_rptAreaLbl.gameObject), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -62f), new Vector2(300f, 30f));

            // 表头行(墨绿, 不随表滚)
            var hRow = Rt(NewRT("thead", report.transform));
            RectAt(hRow, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -100f), new Vector2(0f, 22f));
            float hx = 0f;
            for (int i = 0; i < RptW.Length; i++)
            {
                RowCell(hRow, RptHd[i], 12, P_navy, RptAl[i], 12f + hx, RptW[i]);
                hx += RptW[i];
            }

            var vpGo = Panel("vp", report.transform, P_sheet);
            var vp = Rt(vpGo);
            vp.anchorMin = Vector2.zero; vp.anchorMax = Vector2.one;
            vp.offsetMin = new Vector2(12f, 8f); vp.offsetMax = new Vector2(-12f, -132f);
            vpGo.AddComponent<Mask>().showMaskGraphic = false;
            var sr = vpGo.AddComponent<ScrollRect>();
            sr.horizontal = false; sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 24f;
            var content = NewRT("content", vpGo.transform);
            _rptRows = Rt(content);
            _rptRows.anchorMin = new Vector2(0f, 1f); _rptRows.anchorMax = new Vector2(1f, 1f);
            _rptRows.pivot = new Vector2(0.5f, 1f);
            _rptRows.anchoredPosition = Vector2.zero;
            _rptRows.sizeDelta = new Vector2(0f, 40f);
            sr.viewport = vp; sr.content = _rptRows;
        }

        void ShowNewsReport(SeaIntelSave e)
        {
            if (play == null) return;
            if (e == null) e = play.IntelReportMonthIssue();
            _openReport = e;
            _newsPage = "report";
            _rptAreas = AreaOrder();
            if (_rptAreas.Count > 0)
            {
                if (play.Current != null && play.Current.Area != null)
                {
                    int cur = _rptAreas.IndexOf(play.Current.Area);
                    if (cur >= 0) _rptArea = cur;
                }
            }
            RefreshReportHead(e);
            RebuildReportList();
            ApplyNewsPage();
        }

        void RefreshReportHead(SeaIntelSave e)
        {
            if (play == null) return;
            string subj = e != null && !string.IsNullOrEmpty(e.title) ? " · " + e.title : "";
            _rptCap.text = "《 " + SeaPlay.IntelReportName + " 》" + subj;
            int d = play.HistNewestDay >= 0 ? play.HistNewestDay
                : (play.engine != null ? play.engine.Day : 0);
            _rptNote.text = "<color=#9E4A24><b>行情统计至 " + SeaPlay.IntelDateText(d) + "</b></color>"
                + " · 供→求按本航区逐货找最低最高 · ▲▼ 相对约 7 日前 · 随最新行情重绘";
        }

        void StepReportArea(int d)
        {
            if (_rptAreas == null || _rptAreas.Count == 0) return;
            _rptArea = (_rptArea + d + _rptAreas.Count) % _rptAreas.Count;
            RebuildReportList();
        }

        void RefreshAreaPicker()
        {
            if (_rptAreas == null) _rptAreas = AreaOrder();
            if (_rptAreas.Count == 0) { if (_rptAreaLbl != null) _rptAreaLbl.text = "航区: ——"; return; }
            _rptArea = Mathf.Clamp(_rptArea, 0, _rptAreas.Count - 1);
            var a = _rptAreas[_rptArea];
            if (_rptAreaLbl != null)
                _rptAreaLbl.text = "航区: " + (a != null ? a.Name : "?") + "   (" + (_rptArea + 1) + "/" + _rptAreas.Count + ")";
        }

        int TrendAsk(int pi, int gi)
        {
            int n = play != null ? play.HistCount : 0;
            if (pi < 0 || n < 2) return 0;
            int k2 = n - 1, k1 = k2 - 7; if (k1 < 0) k1 = 0;
            int a0 = play.HistAskValue(k1, pi, gi), a1 = play.HistAskValue(k2, pi, gi);
            if (a0 < 0 || a1 < 0) return 0;
            return a1 > a0 ? 1 : (a1 < a0 ? -1 : 0);
        }
        int TrendBid(int pi, int gi)
        {
            int n = play != null ? play.HistCount : 0;
            if (pi < 0 || n < 2) return 0;
            int k2 = n - 1, k1 = k2 - 7; if (k1 < 0) k1 = 0;
            int b0 = play.HistBidValue(k1, pi, gi), b1 = play.HistBidValue(k2, pi, gi);
            if (b0 < 0 || b1 < 0) return 0;
            return b1 > b0 ? 1 : (b1 < b0 ? -1 : 0);
        }
        static string TrendMark(int d) { return d > 0 ? "▲" : (d < 0 ? "▼" : "—"); }
        static Color TrendCol(int d) { return d > 0 ? P_rust : (d < 0 ? P_moss : P_soft); }

        void RebuildReportList()
        {
            if (play == null || play.world == null || _rptRows == null) return;
            var w = play.world;
            if (_rptAreas == null) _rptAreas = AreaOrder();
            if (_rptAreas.Count == 0) return;
            _rptArea = Mathf.Clamp(_rptArea, 0, _rptAreas.Count - 1);
            var area = _rptAreas[_rptArea];
            RefreshAreaPicker();

            int G = w.Goods.Count;
            var askMin = new int[G]; var bidMax = new int[G];
            var askPi = new int[G]; var bidPi = new int[G];
            var askCity = new string[G]; var bidCity = new string[G];
            var hasA = new bool[G]; var hasB = new bool[G];
            for (int gi = 0; gi < G; gi++) { askMin[gi] = int.MaxValue; bidMax[gi] = -1; askPi[gi] = -1; bidPi[gi] = -1; }
            var en = play.engine;
            for (int pi = 0; pi < w.Ports.Count; pi++)
            {
                var p = w.Ports[pi];
                if (p == null || p.Area != area || p.Blocked) continue;
                for (int gi = 0; gi < G; gi++)
                {
                    var g = w.Goods[gi];
                    if (g == null) continue;
                    var c = en.GetCell(p, g);
                    if (c.Produced)
                    {
                        hasA[gi] = true;
                        int a = en.AskPrice(p, g);
                        if (a >= 0 && a < askMin[gi]) { askMin[gi] = a; askPi[gi] = pi; askCity[gi] = p.Name; }
                    }
                    if (c.Needed)
                    {
                        hasB[gi] = true;
                        int b = en.BidPrice(p, g);
                        if (b > bidMax[gi]) { bidMax[gi] = b; bidPi[gi] = pi; bidCity[gi] = p.Name; }
                    }
                }
            }

            ClearKids(_rptRows);
            int shown = 0;
            for (int gi = 0; gi < G; gi++)
            {
                bool a = hasA[gi], b = hasB[gi];
                if (!a && !b) continue;
                var row = Rt(NewRT("row" + shown, _rptRows));
                row.anchorMin = new Vector2(0f, 1f); row.anchorMax = new Vector2(1f, 1f);
                row.pivot = new Vector2(0.5f, 1f);
                row.anchoredPosition = new Vector2(0f, -(shown * 26f + 2f));
                row.sizeDelta = new Vector2(0f, 24f);
                int ad = a ? TrendAsk(askPi[gi], gi) : 0;
                int bd = b ? TrendBid(bidPi[gi], gi) : 0;
                bool both = a && b;
                int margin = both ? bidMax[gi] - askMin[gi] : 0;
                Color marCol = both && margin > 0 ? P_rust : P_soft;
                string aPrice = a ? askMin[gi].ToString() : "—";
                string bPrice = b ? bidMax[gi].ToString() : "—";
                float x = 0f;
                RowCell(row, w.Goods[gi].Name, 13, P_ink, RptAl[0], x, RptW[0]); x += RptW[0];
                RowCell(row, a ? askCity[gi] : "—", 12, P_soft, RptAl[1], x, RptW[1]); x += RptW[1];
                RowCell(row, aPrice, 13, P_gold, RptAl[2], x, RptW[2]); x += RptW[2];
                RowCell(row, a ? TrendMark(ad) : "—", 13, a ? TrendCol(ad) : P_soft, RptAl[3], x, RptW[3]); x += RptW[3];
                RowCell(row, b ? bidCity[gi] : "—", 12, P_soft, RptAl[4], x, RptW[4]); x += RptW[4];
                RowCell(row, bPrice, 13, P_gold, RptAl[5], x, RptW[5]); x += RptW[5];
                RowCell(row, b ? TrendMark(bd) : "—", 13, b ? TrendCol(bd) : P_soft, RptAl[6], x, RptW[6]); x += RptW[6];
                RowCell(row, both ? margin.ToString() : "—", 13, marCol, RptAl[7], x, RptW[7]);
                shown++;
            }
            _rptRows.sizeDelta = new Vector2(0f, shown * 26f + 4f);
            if (shown == 0)
            {
                var none = AddText(_rptRows, "本航区暂无在产 / 在求的货物。", 13, P_soft, TextAnchor.MiddleCenter);
                RectAt(Rt(none.gameObject), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(0f, 0f), new Vector2(0f, 40f));
            }
        }


        // =============================================================
        // 小工具
        // =============================================================
        static string Money(long v)
        {
            if (v >= 10000) return (v / 10000.0).ToString("0.0") + "万";
            return v.ToString();
        }

        static bool Contains(string[] a, string v)
        {
            if (a == null) return false;
            foreach (var x in a) if (x == v) return true;
            return false;
        }

        void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            go.transform.SetParent(transform, false);
        }

        static Font Cjk()
        {
            if (s_cjk != null) return s_cjk;
            try
            {
                s_cjk = Font.CreateDynamicFontFromOSFont(new[]
                {
                    "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "SimHei",
                    "Source Han Sans SC", "WenQuanYi Micro Hei", "sans-serif"
                }, 18);
            }
            catch { s_cjk = null; }
            if (s_cjk == null) s_cjk = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return s_cjk;
        }

        static Sprite White()
        {
            if (s_white != null) return s_white;
            var tx = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var px = new[] { Color.white, Color.white, Color.white, Color.white };
            tx.SetPixels(px); tx.Apply();
            s_white = Sprite.Create(tx, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));
            return s_white;
        }

        static GameObject NewRT(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        static void RootRect(RectTransform r)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
        }

        static RectTransform Rt(GameObject g) => (RectTransform)g.transform;

        GameObject Panel(string name, Transform parent, Color c)
        {
            var go = NewRT(name, parent);
            var img = go.AddComponent<Image>();
            img.sprite = White();
            img.color = c;
            return go;
        }

        Text AddText(Transform parent, string s, int size, Color col, TextAnchor align)
        {
            var go = NewRT("txt", parent);
            var t = go.AddComponent<Text>();
            t.font = Cjk();
            t.fontSize = size;
            t.color = col;
            t.alignment = align;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            t.supportRichText = true;
            t.text = s;
            return t;
        }

        static void RectAt(RectTransform r, Vector2 min, Vector2 max, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            r.anchorMin = min; r.anchorMax = max;
            r.pivot = pivot;
            r.anchoredPosition = pos;
            r.sizeDelta = size;
        }

        static void Fill(RectTransform r, float l, float r2, float t, float b)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.pivot = new Vector2(0.5f, 0.5f);
            r.offsetMin = new Vector2(l, b);
            r.offsetMax = new Vector2(-r2, -t);
        }

        Button MakeBtn(Transform parent, string nm, string caption, int size, Color fill, Color textCol, bool clickSfx = true)
        {
            var go = NewRT(nm, parent);
            var img = go.AddComponent<Image>();
            img.sprite = White();
            img.color = fill;
            var b = go.AddComponent<Button>();
            b.targetGraphic = img;
            if (clickSfx)
                b.onClick.AddListener(() => { if (SeaAudio.Instance != null) SeaAudio.Instance.SfxClick(); });
            var cols = b.colors;
            cols.normalColor = Color.white;
            cols.highlightedColor = new Color(1.18f, 1.18f, 1.18f);
            cols.pressedColor = new Color(0.82f, 0.82f, 0.82f);
            cols.disabledColor = new Color(0.4f, 0.4f, 0.4f, 0.35f);
            b.colors = cols;
            var t = AddText(go.transform, caption, size, textCol, TextAnchor.MiddleCenter);
            Fill(Rt(t.gameObject), 0, 0, 0, 0);
            return b;
        }
    }
}
