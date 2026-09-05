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
        // 行情行高亮: 每行一条透明感应带(悬停微亮 / 点选那行金亮, 避免误操作) —— 感应带排在按钮之下
        readonly List<Image> _rowBand = new List<Image>();
        readonly List<RectTransform> _rowRt = new List<RectTransform>();
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
        // 贸易日报(翻页报纸)
        Text _paperMast, _paperSub, _paperBody, _paperFolio;
        Button _paperPrev, _paperNext;
        List<string> _paperPages = new List<string>();
        int _paperIdx = 0;
        string _paperSig = "";
        Text _pageScratch;                          // 分页测量用(与正文同宽同字号)
        float _pageW = 980f, _pageH = 408f;         // 报纸正文单页量度(与 vp/正文 Rect 对齐, 勿乱改)
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
        Button _fleetProv15, _fleetProv30, _fleetRecruit, _fleetDock, _fleetSail;

        // 花金操作确认弹窗
        GameObject _confirmGo;
        Text _confirmMsg;
        Button _confirmOk, _confirmCancel;
        System.Action _confirmAction;

        // 出航前检查浮层(水手 / 给养 vs 航程 / 货舱清单 → 玩家确认后才真正出航)
        GameObject _departGo;
        bool _departOpen;
        Text _departBody;

        // 航行日志(可滚动翻看 + 按日期分组)
        ScrollRect _logScroll;
        RectTransform _logTrack, _logThumb;
        int _logVersion = -1;      // 已渲染的日志版本(只在日志真的变时重排文字)
        bool _logPinned = true;    // 视图贴在最"新"(底部); 玩家往上翻历史时不打断

        // 港名标签
        readonly Dictionary<string, Text> _labels = new Dictionary<string, Text>();

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
                return;
            }

            // 兜底: 还没导进 logo 图时, 先放一段文字题名占位
            var t = AddText(root, "大航海时代 2026", 11, ColTitle, TextAnchor.UpperLeft);
            t.fontStyle = FontStyle.Bold;
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
            _fleetGo = Panel("fleet", root, ColPanel);
            RectAt(Rt(_fleetGo), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0, -4), new Vector2(600, 456));
            var head = AddText(_fleetGo.transform, "⚓ 舰队情况", 18, ColTitle, TextAnchor.MiddleCenter);
            RectAt(Rt(head.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -2), new Vector2(0, 26));
            var xb = MakeBtn(_fleetGo.transform, "close", "✕", 16, ColBtn, Color.white);
            RectAt(Rt(xb.gameObject), new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(-6, -2), new Vector2(30, 24));
            xb.onClick.AddListener(() => _fleetOpen = false);

            // 船卡区: 居中浮层主体 —— 每艘船一张卡 = 船图占位(上) + 该船参数(下)
            _shipRoot = Rt(NewRT("ships", _fleetGo.transform));
            var sr = _shipRoot;
            sr.anchorMin = new Vector2(0, 0); sr.anchorMax = new Vector2(1, 1);
            sr.offsetMin = new Vector2(22, 140); sr.offsetMax = new Vector2(-22, -36);

            // 全队合计(至多三行短句, 抬到按钮带(高40、顶缘y80)之上、船卡(底缘y140)之下;
            //  溢出不越过矩形下缘 → 文字绝不会再压到下方按钮)
            _fleetSum = AddText(_fleetGo.transform, "", 12, ColTxt, TextAnchor.UpperLeft);
            var fs = Rt(_fleetSum.gameObject);
            fs.anchorMin = new Vector2(0, 0); fs.anchorMax = new Vector2(1, 0);
            fs.pivot = new Vector2(0.5f, 0);
            fs.offsetMin = new Vector2(26, 90); fs.offsetMax = new Vector2(-26, 140);
            _fleetSum.horizontalOverflow = HorizontalWrapMode.Wrap;
            _fleetSum.verticalOverflow = VerticalWrapMode.Truncate;

            _fleetProv15 = MakeBtn(_fleetGo.transform, "prov15", "补给 15 日", 15, ColBtn, Color.white);
            _fleetProv30 = MakeBtn(_fleetGo.transform, "prov30", "补给 30 日", 15, ColBtn, Color.white);
            _fleetRecruit = MakeBtn(_fleetGo.transform, "crew", "招满水手", 15, ColGoldBtn, new Color(0.14f, 0.08f, 0.03f));
            PlaceFleetBtn(_fleetProv15, -156f, 142f);
            PlaceFleetBtn(_fleetProv30, -6f, 142f);
            PlaceFleetBtn(_fleetRecruit, 152f, 150f);
            _fleetProv15.onClick.AddListener(() => ConfirmProvision(15));
            _fleetProv30.onClick.AddListener(() => ConfirmProvision(30));
            _fleetRecruit.onClick.AddListener(ConfirmRecruit);

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
            if (n == 0) return;

            Rect rr = _shipRoot.rect;
            float W = Mathf.Max(60f, rr.width);
            int perRow = 2;
            int rows = (n + perRow - 1) / perRow;
            float gapX = 10f, gapY = 14f;
            float colW = (W - gapX * (perRow - 1)) / perRow;
            float availH = Mathf.Max(60f, rr.height);
            float rowH = Mathf.Max(60f, Mathf.Min(320f, (availH - gapY * (rows - 1)) / rows));

            for (int i = 0; i < n; i++)
            {
                int c = i % perRow, rw = i / perRow;
                float x = c * (colW + gapX);
                float y = rw * (rowH + gapY);
                BuildShipCard(ships[i], x, y, colW, rowH);
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

            // 船图占位: 一律 2:1(与船坞同源), 高按卡内顶带预算、宽据此缩放, 水平居中 —— 未来把中央换成该船真图
            float bandH = Mathf.Max(64f, rowH * 0.40f);
            float picW = Mathf.Min(colW, bandH * ShipPicAspect);
            float picH = Mathf.Max(40f, picW / ShipPicAspect);
            var pic = Panel("pic", col, new Color(0.34f, 0.44f, 0.54f, 1f));
            RectAt(Rt(pic), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, 0), new Vector2(picW, picH));
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

            // 名牌(旗舰金 / 僚舰浅蓝), 紧贴图下
            var head = AddText(col, "", 15, s.IsFlagship ? ColGold : new Color(0.80f, 0.93f, 1f), TextAnchor.MiddleCenter);
            RectAt(Rt(head.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                new Vector2(0, -(picH + 3)), new Vector2(0, 22));
            _shipHeads.Add(head);

            // 该船参数块
            var st = AddText(col, "", 13, ColTxt, TextAnchor.UpperLeft);
            RectAt(Rt(st.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                new Vector2(0, -(picH + 29)), new Vector2(0, Mathf.Max(40f, rowH - picH - 33)));
            st.horizontalOverflow = HorizontalWrapMode.Wrap;
            st.verticalOverflow = VerticalWrapMode.Overflow;
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
            int n = Mathf.Min(play.fleet.Ships.Count, _shipStat.Count);
            for (int i = 0; i < n; i++)
            {
                var s = play.fleet.Ships[i];
                _shipHeads[i].text = (s.IsFlagship ? "★ 旗舰" : "僚舰") + " · " + s.Model.Name;
                var sb = new StringBuilder(128);
                int occ = ShipWeight(s);
                sb.Append("<b>水手</b> ").Append(s.CrewAboard).Append(" / ").Append(s.Model.CrewMax).Append('\n');
                int kinds = s.Cargo.Count, units = 0;
                foreach (var kv in s.Cargo) units += kv.Value;
                sb.Append("<b>货仓</b> 槽位 ").Append(kinds).Append('/').Append(s.Model.Slots)
                  .Append(" · 共 ").Append(units).Append(" 件\n");
                sb.Append("<b>仓位</b> ").Append(occ).Append(" / ").Append(s.EffCapacity)
                  .Append(" 载 · 剩 ").Append(Math.Max(0, s.EffCapacity - occ));
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
            if (play.State != SeaPlay.Mode.Docked) { play.Banner = "航行中不能补给 —— 先靠港。"; return; }
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
            AskConfirm(sb.ToString(), () => play.ProvisionTo(nDays));
        }

        void ConfirmRecruit()
        {
            if (play.State != SeaPlay.Mode.Docked) { play.Banner = "航行中不能招募水手 —— 先靠港。"; return; }
            long cost = play.RecruitCostToFull();
            if (cost < 0) { play.Banner = "航行中不能招募水手 —— 先靠港。"; return; }
            if (cost == 0) { play.Banner = "水手已经满编, 无需招募。"; return; }
            AskConfirm("<b><color=#FFD76A>招募水手补齐</color></b>\n在码头把全舰队水手招满(旗舰 + 僚舰)。\n预计花费: <color=#FFD76A>" + Money(cost) + "</color> 金。",
                () => play.RecruitCrewFull());
        }

        // 休整 10 日: 同"花金确认"流程 —— 先把"这会花掉什么/错过什么"讲清楚, 玩家点头才拨时间
        void ConfirmRest()
        {
            if (play.State != SeaPlay.Mode.Docked) { play.Banner = "航行中不能休整 —— 先靠港。"; return; }
            string port = play.Current != null ? play.Current.Name : "本港";
            AskConfirm("<b><color=#FFD76A>休整 10 日</color></b>\n让全舰队在 " + port + " 停靠休整, 把日子拨快 10 天:\n"
                + "· 这 10 天各地行情 / 库存照常流转 ——\n  可能错过正在涨的差价窗口, 也可能赶上新到货;\n"
                + "· 休整本身 <color=#BFE0B0>不花金币、也不吃给养</color>;\n"
                + "· 水手工资按到港结薪, 与休整天数无关。\n\n确认后执行, 之后仍停在港内可继续买卖。",
                () => play.RestDays(10));
        }

        void AskConfirm(string msg, System.Action ok)
        {
            FoldFloats();   // 确认弹窗也是"窗": 先把行情/舰队/船坞/情报/出航检查收掉, 干净地只叠一层
            _confirmMsg.text = msg;
            _confirmAction = ok;
            _confirmGo.SetActive(true);
        }
        void OnConfirmOk()
        {
            var a = _confirmAction;
            _confirmGo.SetActive(false);
            _confirmAction = null;
            if (a != null) a();
        }
        void OnConfirmCancel()
        {
            _confirmGo.SetActive(false);
            _confirmAction = null;
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
            if (play == null || play.State != SeaPlay.Mode.Docked || play.Dest == null) return;
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
            var cur = play.Current; var dst = play.Dest;
            if (cur == null || dst == null || play.fleet == null) return "";
            int days = play.TravelDays(cur, dst);
            int crew = play.TotalSailors;
            int cmin = FleetCrewMin(), cmax = FleetCrewMax();
            int endu = play.EnduranceDays;
            var sb = new StringBuilder(360);
            sb.Append("<size=14><color=#FFD76A>").Append(cur.Name).Append("  →  ").Append(dst.Name)
              .Append("   · 约 ").Append(days).Append(" 日航程</color></size>\n");
            sb.Append("<b>水手</b> ").Append(crew).Append(" 人 · 全队下限 ").Append(cmin).Append(" · 满载 ").Append(cmax);
            sb.Append(crew < cmin ? "   <color=#FF7766>⚠ 低于下限, 开船吃力</color>\n" : "   <color=#BFE0B0>✓ 够开船</color>\n");
            sb.Append("<b>给养续航</b> 全队可撑 <color=#FFD76A>").Append(endu).Append("</color> 日");
            sb.Append(endu < days ? "   <color=#FF7766>⚠ 不足 " + days + " 日, 途中会断粮减员!</color>\n" : "   <color=#BFE0B0>✓ 够撑到港</color>\n");
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
            sb.Append("\n\n<color=#8FA8C2>确认后即正式出航。航行中按天吃给养、到港才结工资; 断粮会士气崩、人也会没。</color>");
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
        void BuildLog(Transform root)
        {
            var card = Panel("card_log", root, ColPanel);
            RectAt(Rt(card), new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(-10, -86), new Vector2(360, 286));
            _logRt = Rt(card);   // 地名标签 keep-out: 右侧日志永不被地名压住
            AddHead(card.transform, "航行日志");

            // 滚动视窗
            var vpGo = Panel("vp", card.transform, new Color(0, 0, 0, 0.22f));
            var vp = Rt(vpGo);
            vp.anchorMin = new Vector2(0, 0); vp.anchorMax = new Vector2(1, 1);
            vp.offsetMin = new Vector2(6, 6); vp.offsetMax = new Vector2(-20, -28);
            vpGo.AddComponent<Mask>().showMaskGraphic = false;
            _logScroll = vpGo.AddComponent<ScrollRect>();
            _logScroll.horizontal = false; _logScroll.vertical = true;
            _logScroll.movementType = ScrollRect.MovementType.Clamped;
            _logScroll.scrollSensitivity = 28f;

            var contentGo = NewRT("content", vpGo.transform);
            var content = Rt(contentGo);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0, 40);
            _logScroll.viewport = vp;
            _logScroll.content = content;

            _logText = AddText(contentGo.transform, "", 12, ColLog, TextAnchor.UpperLeft);
            var lt = Rt(_logText.gameObject);
            lt.anchorMin = Vector2.zero; lt.anchorMax = Vector2.one;
            lt.offsetMin = new Vector2(2, 2); lt.offsetMax = new Vector2(-2, -2);
            _logText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _logText.verticalOverflow = VerticalWrapMode.Overflow;

            // 滚动条: 轨道 + 滑块(拖滑块翻历史; 滚轮悬在日志上也可翻)
            _logTrack = Rt(Panel("bar", card.transform, new Color(0f, 0f, 0f, 0.5f)));
            _logTrack.anchorMin = new Vector2(1, 0); _logTrack.anchorMax = new Vector2(1, 1);
            _logTrack.offsetMin = new Vector2(-17, 32); _logTrack.offsetMax = new Vector2(-7, -32);
            _logThumb = Rt(Panel("knob", _logTrack, new Color(0.62f, 0.74f, 0.86f, 0.95f)));
            _logThumb.gameObject.AddComponent<SeaLogThumb>().hud = this;
            _logThumb.gameObject.SetActive(false);
        }

        void RefreshLog()
        {
            if (_logScroll == null) return;
            int n = play.Log.Count;
            if (n == 0)
            {
                if (_logVersion != 0)
                {
                    _logVersion = 0;
                    _logText.text = "……还没有航行日志。\n出海后这里会记下每天的动作,\n可滚动、按日期翻看。";
                    _logScroll.content.sizeDelta = new Vector2(0, _logText.preferredHeight + 8f);
                }
                UpdateLogThumb();
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
                        sb.Append("<size=11><color=#FFD76A>—— ").Append(DateOf(d)).Append(" ——</color></size>\n");
                    }
                    sb.Append(play.Log[i]).Append('\n');
                }
                _logText.text = sb.ToString();
                float ph = _logText.preferredHeight + 6f;
                _logScroll.content.sizeDelta = new Vector2(0, Mathf.Max(40f, ph));
                if (keepBottom) _logScroll.verticalNormalizedPosition = 0f;   // 贴到最新
            }
            _logPinned = _logScroll.verticalNormalizedPosition <= 0.02f;
            // 只要玩家没刻意往回翻, 每帧都把视图钉在最"新"(底部) —— 新记录一到就自动显示最新一条
            if (_logPinned && _logScroll.verticalNormalizedPosition > 0f)
                _logScroll.verticalNormalizedPosition = 0f;
            UpdateLogThumb();
        }

        void UpdateLogThumb()
        {
            if (_logScroll == null || _logThumb == null || _logTrack == null) return;
            float vpH = _logScroll.viewport.rect.height;
            float cH = _logScroll.content.rect.height;
            if (cH <= vpH + 1f) { _logThumb.gameObject.SetActive(false); return; }
            _logThumb.gameObject.SetActive(true);
            float frac = Mathf.Clamp(vpH / cH, 0.06f, 1f);
            float v = Mathf.Clamp01(_logScroll.verticalNormalizedPosition);
            var rt = Rt(_logThumb.gameObject);
            rt.anchorMin = new Vector2(0f, v); rt.anchorMax = new Vector2(1f, Mathf.Min(1f, v + frac));
            rt.offsetMin = new Vector2(2f, 0f); rt.offsetMax = new Vector2(-2f, 0f);
            rt.anchoredPosition = Vector2.zero; rt.pivot = new Vector2(0.5f, 0.5f);
        }

        public void OnLogThumbDrag(PointerEventData e)
        {
            if (_logScroll == null || _logTrack == null || _logThumb == null) return;
            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_logTrack, e.position, null, out local);
            float H = _logTrack.rect.height;
            float th = Rt(_logThumb.gameObject).rect.height;
            if (H - th <= 1f) return;
            float lo = -H * 0.5f + th * 0.5f, hi = H * 0.5f - th * 0.5f;
            float yc = Mathf.Clamp(local.y, lo, hi);
            float v = (yc - lo) / (H - th);
            _logScroll.verticalNormalizedPosition = v;
            _logPinned = v <= 0.02f;
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
            if (alsoConfirm && _confirmGo != null) { _confirmGo.SetActive(false); _confirmAction = null; }
        }

        void ToggleMarket()
        {
            if (_tradeOpen) { FoldFloats(true); return; }   // 再点一下收铺
            FoldFloats(true);
            _tradeOpen = true;
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

            // 数据列(滚动内容坐标): 商品 / 买价 / 卖价 / 成本(自己持仓的均价) / 持有 / 库存
            float[] cx = { 6f, 184f, 254f, 324f, 396f, 460f };
            float[] cw = { 170f, 62f, 62f, 64f, 56f, 56f };
            string[] cn = { "商品", "买价", "卖价", "成本", "持有", "库存" };
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
                HeaderCell(parent, cn[c], cx[c] + InsetX, cw[c], ca[c], 13, ColDim);
            // 买 / 卖三键组(数据列最右止于 516, 留 ~14 空隙再接按钮)
            const float BuyX = 530f, SellX = 706f;
            HeaderCell(parent, "买 入", BuyX + InsetX, 170f, TextAnchor.MiddleCenter, 13, new Color(0.70f, 0.92f, 1f));
            HeaderCell(parent, "卖 出", SellX + InsetX, 170f, TextAnchor.MiddleCenter, 13, ColTitle);

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
            _marketVp = vp;   // 行悬停判定: 指针是否落在这块滚动视窗里(滚到哪行就亮哪行)

            int i = 0;
            foreach (var g in play.world.Goods)
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

                var nm = RowCell(row, "", 15, ColTxt, TextAnchor.MiddleLeft, cx[0], cw[0]);
                var ak = RowCell(row, "", 15, Color.white, TextAnchor.MiddleRight, cx[1], cw[1]);
                var bd = RowCell(row, "", 15, Color.white, TextAnchor.MiddleRight, cx[2], cw[2]);
                var cs = RowCell(row, "", 14, ColDim, TextAnchor.MiddleRight, cx[3], cw[3]);
                var hd = RowCell(row, "", 14, ColTxt, TextAnchor.MiddleRight, cx[4], cw[4]);
                var st = RowCell(row, "", 13, ColDim, TextAnchor.MiddleRight, cx[5], cw[5]);
                _tName.Add(nm); _tAsk.Add(ak); _tBid.Add(bd); _tCost.Add(cs); _tHold.Add(hd); _tStock.Add(st);

                // 买 1 / 买 10 / 全仓
                var buys = new Button[3];
                for (int b = 0; b < 3; b++)
                {
                    int qty = b == 0 ? 1 : (b == 1 ? 10 : int.MaxValue);
                    var btn = MakeBtn(row, "b" + i + "_" + b, b == 0 ? "买1" : (b == 1 ? "买10" : "全买"),
                        13, ColBtn, Color.white, clickSfx: false);
                    PlaceRowButton(btn, BuyX + b * 58f);
                    var gg = g; var q = qty; int ri = i;
                    btn.onClick.AddListener(() => { _selRow = ri; play.BuyGood(gg, q); });
                    buys[b] = btn;
                }
                // 卖 1 / 卖 10 / 全清
                var sells = new Button[3];
                for (int s = 0; s < 3; s++)
                {
                    int qty = s == 0 ? 1 : (s == 1 ? 10 : int.MaxValue);
                    var btn = MakeBtn(row, "s" + i + "_" + s, s == 0 ? "卖1" : (s == 1 ? "卖10" : "全卖"),
                        13, ColGoldBtn, new Color(0.15f, 0.09f, 0.04f), clickSfx: false);
                    PlaceRowButton(btn, SellX + s * 58f);
                    var gg = g; var q = qty; int si = i;
                    btn.onClick.AddListener(() => { _selRow = si; play.SellGood(gg, q); });
                    sells[s] = btn;
                }
                _buy.Add(buys); _sell.Add(sells);
                i++;
            }
        }

        // =============================================================
        // 右下角"舰队货仓"速览: 行情开着时, 买/卖一入账就在这按船画"格子占用"
        //   —— 每船: 名称(旗舰★) + 货种 k/Slots + 占载 occ/Eff, 下一行格子条(█=已占, ░=空),
        //      再下一行列出每种货的名称 × 件数(占重)。给养是全队合算, 单列一行注明。
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
            var vpGo = Panel("vp", _holdGo.transform, new Color(0, 0, 0, 0.22f));   // 内深遮罩
            var vp = Rt(vpGo);
            vp.anchorMin = new Vector2(0f, 0f); vp.anchorMax = new Vector2(1f, 1f);
            vp.offsetMin = new Vector2(6f, 6f); vp.offsetMax = new Vector2(-6f, -28f);
            vpGo.AddComponent<Mask>().showMaskGraphic = false;   // 内容超高就裁掉, 不越界盖日志/码头
            _holdBody = AddText(vpGo.transform, "", 11, ColTxt, TextAnchor.UpperLeft);
            var b = Rt(_holdBody.gameObject);
            b.anchorMin = Vector2.zero; b.anchorMax = Vector2.one;
            b.offsetMin = new Vector2(8f, 8f); b.offsetMax = new Vector2(-8f, -8f);
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
            var sb = new StringBuilder(384);
            sb.Append("<size=12>货仓合计 <color=#FFD76A>").Append(used).Append("</color>/").Append(cap)
              .Append(" 载 · 空 <b>").Append(freeL).Append("</b></size>\n");
            if (play.ProvWeight() > 0)
                sb.Append("<size=10><color=#8FA8C2>其中给养占 ").Append(play.ProvWeight()).Append(" 载(全队合算, 不分船)</color></size>\n");
            int maxShow = Mathf.Min(3, ships.Count);
            for (int i = 0; i < maxShow; i++)
            {
                var s = ships[i];
                if (i > 0) sb.Append('\n');
                int occ = ShipWeight(s);
                int ec = Math.Max(1, s.EffCapacity);
                sb.Append("<size=12>").Append(s.IsFlagship ? "<color=#FFD76A>★</color>" : "<color=#8FA8C2>·</color>")
                  .Append(" <b>").Append(ShipShort(s.Model.Name)).Append("</b>")
                  .Append(s.IsFlagship ? " <color=#FFD76A>旗舰</color>" : " <color=#7FA0C0>僚舰</color>")
                  .Append("</size>  货种 <b>").Append(s.Cargo.Count).Append("</b>/").Append(s.Model.Slots)
                  .Append(" · 占 <b>").Append(occ).Append("</b>/").Append(ec).Append('\n');
                // 格子条: █ 已占载重, ░ 空载重(16 格按占载比例填)
                int cells = 16;
                int filled = Mathf.Clamp((int)Mathf.Ceil((float)occ / ec * cells), 0, cells);
                sb.Append("<size=10><color=#FFD76A>").Append(new string('█', filled)).Append("</color><color=#3D5568>")
                  .Append(new string('░', cells - filled)).Append("</color></size>\n");
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
            if (ships.Count > maxShow)
                sb.Append("\n<size=10><color=#8FA8C2>…其余 ").Append(ships.Count - maxShow).Append(" 艘见 ⚓舰队</color></size>");
            _holdBody.text = sb.ToString();
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

            // --- 泊港 / 航行 切换 ---
            bool docked = st == SeaPlay.Mode.Docked;
            _dockGo.SetActive(docked);
            _sailGo.SetActive(!docked);
            if (docked) RefreshDock();
            else { RefreshSail(); _yardOpen = false; }   // 一离港船坞就关

            // --- 舰队情况(浮层; 泊港/航行都能看; 到新港自动弹行情时让位) ---
            string curIdNow = play.Current != null ? play.Current.Id : "";
            if (docked && curIdNow != _lastPort) { _tradeOpen = true; _fleetOpen = false; _yardOpen = false; _newsOpen = false; }
            _lastPort = curIdNow;
            _fleetGo.SetActive(_fleetOpen);
            if (_fleetOpen) RefreshFleet();

            // --- 行情 ---
            _tradeGo.SetActive(docked && _tradeOpen);
            if (docked && _tradeOpen) RefreshTrade();

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
            if (_holdGo != null) _holdGo.SetActive(docked && _tradeOpen);
            if (docked && _tradeOpen && _holdBody != null) RefreshHold();

            // --- 出航前检查(顶层浮层; 一旦离港/换状态即收起) ---
            if (!docked) _departOpen = false;
            _departGo.SetActive(docked && _departOpen);

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
            string portLine = cur != null ? cur.Name + " · " + cur.Area.Name : "航行中";
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
            // 按钮可用性
            _dock["depart"].interactable = play.Dest != null;
            var cap = _dock["market"].GetComponentInChildren<Text>();
            if (cap != null) cap.text = _tradeOpen ? "收起行情" : "本港行情 ◈";

            string g;
            if (play.Dest == null)
            {
                g = _tradeOpen
                    ? "这是 <color=#FFD76A>" + play.Current.Name + "</color> 的行情: 绿色 ◆ = 本地盛产便宜。买 / 卖分栏各有 <color=#7FE0FF>1件 · 10件 · 全仓</color> 三键 —— 行情看够了点右上 <color=#7FE0FF>✕</color> 收起。"
                    : "点海图上任意一颗 <color=#7FE0FF>发光圆球</color>(另一个港口)把它设为目标 —— 设好后这里会亮起金色 <color=#FFD76A>出航</color>。想先看本地买卖? 点 <color=#FFD76A>本港行情</color>; 招水手 / 补给给养 / 看货舱都在左下 <color=#7FE0FF>⚓ 舰队</color>。";
            }
            else
            {
                int n = play.TravelDays(play.Current, play.Dest);
                g = "目标: <color=#FFD76A>" + play.Dest.Name + " · " + play.Dest.Area.Name + "</color> · 约 " + n + " 日航程 —— 给养够吗? 不够进左下 <color=#7FE0FF>⚓ 舰队</color> 补给, 然后按 <color=#FFD76A>出航 ▶</color>。";
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
            string dName = play.Dest != null ? play.Dest.Name : "??";
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

            var goods = play.world.Goods;
            for (int i = 0; i < goods.Count && i < _tName.Count; i++)
            {
                var g = goods[i];
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
                _tCost[i].color = avg >= 0 ? new Color(0.96f, 0.84f, 0.55f) : ColDim;
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
                    for (int s = 0; s < _sell[i].Length; s++) _sell[i][s].interactable = canSell;
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
                // 锚点 = 光球顶点(MarkerBallTopY 与 BuildMarker 同源), 再轻微上下浮动
                Vector3 top = mt.position + Vector3.up * (play.MarkerBallTopY(p) + Mathf.Sin(Time.time * 0.9f + p.Lon) * 0.05f);
                Vector3 sp = _cam.WorldToScreenPoint(top);
                bool behind = sp.z < 0.1f;
                bool cursorNear = !behind && Vector2.Distance(new Vector2(sp.x, sp.y), mouse) < 52f;
                // 浮层(行情/舰队/确认/出航检查)开着 → 所有地名全隐藏(本港/目标/悬停都不出现);
                // 平时: 当前港 / 目标港 / 鼠标近处 / 大港 常显。
                bool show = !anyOpen && !behind && (curId == p.Id || destId == p.Id || cursorNear || p.Size >= 3);
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
            // 顶上一条红蜡封条 + 墨绿刊名
            var seal = Panel("seal", win.transform, P_rust);
            RectAt(Rt(seal), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -1f), new Vector2(0f, 4f));
            var cap = AddText(win.transform, "情报站 · 绘图房的消息案", 18, P_navy, TextAnchor.UpperLeft);
            RectAt(Rt(cap.gameObject), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(18f, -12f), new Vector2(660f, 28f));
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
            _newsGo.SetActive(false);
        }

        static GameObject NewsPage(string n, Transform win)
        {
            var p = new GameObject("pg_" + n, typeof(RectTransform));
            p.transform.SetParent(win, false);
            var r = (RectTransform)p.transform;
            r.anchorMin = new Vector2(0f, 0f); r.anchorMax = new Vector2(1f, 1f);
            r.offsetMin = new Vector2(16f, 12f); r.offsetMax = new Vector2(-16f, -50f);
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
            _paperMast = AddText(paper.transform, "", 30, P_navy, TextAnchor.MiddleCenter);
            RectAt(Rt(_paperMast.gameObject), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -6f), new Vector2(0f, 40f));
            var mrule = Panel("mrule", paper.transform, P_rule);
            RectAt(Rt(mrule), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -50f), new Vector2(0f, 1f));
            _paperSub = AddText(paper.transform, "", 13, P_soft, TextAnchor.MiddleCenter);
            RectAt(Rt(_paperSub.gameObject), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -60f), new Vector2(0f, 22f));

            // 版面主体: 羊皮纸刊页, 蒙版裁切(一版一版翻, 不用滚)
            var vpGo = Panel("body", paper.transform, P_sheet);
            var vp = Rt(vpGo);
            vp.anchorMin = Vector2.zero; vp.anchorMax = Vector2.one;
            vp.offsetMin = new Vector2(10f, 48f); vp.offsetMax = new Vector2(-10f, -88f);
            vpGo.AddComponent<Mask>().showMaskGraphic = false;
            _paperBody = AddText(vpGo.transform, "", 12, P_ink, TextAnchor.UpperLeft);
            var bt = Rt(_paperBody.gameObject);
            bt.anchorMin = new Vector2(0f, 1f); bt.anchorMax = new Vector2(0f, 1f);
            bt.pivot = new Vector2(0f, 1f);
            bt.anchoredPosition = Vector2.zero;
            bt.sizeDelta = new Vector2(_pageW, 4000f);

            _paperPrev = MakeBtn(paper.transform, "prev", "◀ 上一版", 13, P_bronze, P_btnTx);
            RectAt(Rt(_paperPrev.gameObject), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(-160f, 8f), new Vector2(120f, 30f));
            _paperPrev.onClick.AddListener(() => PaperGo(-1));
            _paperFolio = AddText(paper.transform, "", 13, P_rust, TextAnchor.MiddleCenter);
            RectAt(Rt(_paperFolio.gameObject), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 6f), new Vector2(280f, 24f));
            _paperNext = MakeBtn(paper.transform, "next", "下一版 ▶", 13, P_bronze, P_btnTx);
            RectAt(Rt(_paperNext.gameObject), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(160f, 8f), new Vector2(120f, 30f));
            _paperNext.onClick.AddListener(() => PaperGo(1));
        }

        static float PaperLineH(string s)
        {
            int size = 12, i = s.IndexOf("<size=");
            if (i >= 0)
            {
                int j = s.IndexOf('>', i);
                int v;
                if (j > i + 6 && int.TryParse(s.Substring(i + 6, j - i - 6), out v) && v >= 8 && v <= 60) size = v;
            }
            return size * 1.28f;
        }
        static float PaperBlockH(string block)
        {
            float h = 0f;
            var lines = block.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                h += lines[i].Length == 0 ? 4f : PaperLineH(lines[i]);
            return h;
        }

        void PaginatePaper()
        {
            _paperPages.Clear();
            string body = _paperSig ?? "";
            if (body.Length == 0) { _paperPages.Add(""); return; }
            var parts = new List<string>();
            foreach (var b in body.Split(SeaPlay.IntelBlockSep))
            {
                var t = b.TrimStart('\n');
                if (t.Length > 0) parts.Add(t);
            }
            if (parts.Count == 0) { _paperPages.Add(""); return; }
            float budget = _pageH;
            var cur = new System.Text.StringBuilder(512);
            float curH = 0f;
            foreach (var b in parts)
            {
                float bh = PaperBlockH(b);
                if (cur.Length > 0 && curH + bh > budget)
                {
                    _paperPages.Add(cur.ToString());
                    cur.Length = 0; curH = 0f;
                }
                cur.Append(b).Append('\n');
                curH += bh;
            }
            if (cur.Length > 0) _paperPages.Add(cur.ToString());
            if (_paperPages.Count == 0) _paperPages.Add("");
        }

        void PaperGo(int d)
        {
            if (_paperPages.Count == 0) PaginatePaper();
            int n = _paperPages.Count;
            _paperIdx = Mathf.Clamp(_paperIdx + d, 0, n - 1);
            if (_paperBody != null) _paperBody.text = n > 0 ? _paperPages[_paperIdx] : "";
            if (_paperFolio != null) _paperFolio.text = n > 0 ? "—— 第 " + (_paperIdx + 1) + " / " + n + " 版 ——" : "";
            if (_paperPrev != null) _paperPrev.interactable = _paperIdx > 0;
            if (_paperNext != null) _paperNext.interactable = n > 0 && _paperIdx < n - 1;
        }

        void ShowNewsPaper(SeaIntelSave e)
        {
            if (play == null) return;
            if (e == null) e = play.IntelPaperTodayIssue();
            _openPaper = e;
            _newsPage = "paper";
            _paperMast.text = "《 贸 易 日 报 》";
            if (e != null)
            {
                _paperSig = e.body;
                _paperSub.text = e.title + " · " + e.sub;
            }
            else
            {
                _paperSig = "";
                _paperSub.text = "本期还没买到 —— 回大厅买一份今日《" + SeaPlay.IntelPaperName + "》。";
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
