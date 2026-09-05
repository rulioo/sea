using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Sea
{
    // =============================================================
    // SEA 运行时入口:
    //  1. 运行时自动装载 SeaData(Resources) → World + SimEngine
    //  2. 把 52 港按经纬度投到场景(等距圆柱投影), 标记可按航区着色
    //  3. OnGUI 调试台: 时钟/时间推进/自动播放 + 点港看行情面板
    // 非商用教学骨架, 无美术依赖, 所有内容由数据驱动。
    // =============================================================
    [DefaultExecutionOrder(-100)]
    public sealed class SeaGame : MonoBehaviour
    {
        [Tooltip("模拟随机种子, 相同种子 = 相同行情走向")]
        public int seed = 15500712;
        [Tooltip("自动播放时每天多少游戏日(秒)")]
        public float autoDaysPerSecond = 14f;

        public World World { get; private set; }
        public SimEngine Engine { get; private set; }
        public string LoadError { get; private set; }

        // ---- 场景标记 ----
        Transform _root;
        readonly Dictionary<string, GameObject> _markers = new Dictionary<string, GameObject>();
        readonly Dictionary<string, Color> _areaColor = new Dictionary<string, Color>();
        readonly List<string> _areaOrder = new List<string>();
        Port _selected;
        bool _loading;

        // ---- 相机轨道 ----
        Camera _cam;
        Vector3 _target = Vector3.zero;
        float _dist = 150f, _yaw = -35f, _pitch = 58f;

        // ---- 自动播放 ----
        float _autoAccum;
        bool _auto;

        // ---- GUI ----
        readonly StringBuilder _sb = new StringBuilder(512);
        GUIStyle _styleBox, _styleTitle, _styleArea, _styleRow, _styleWarn;
        Font _font;
        Vector2 _scroll;

        // 点击坐标(等距圆柱投影): x = 经度, z = -纬度, 使北在上、东在右
        static Vector3 LonLatToWorld(float lon, float lat) => new Vector3(lon * 1.0f, 0f, -lat * 1.0f);

        // 运行时兜底已迁移到 SeaPlay.SeaLauncher; 若场景里仍存旧版 SeaGame,
        // SeaPlay.Awake 会把它清掉。本类保留供兼容引用(不再自启)。

        void Start()
        {
            if (_loading) return;
            _loading = true;

            // 保证场景里有相机可看
            EnsureCamera();

            LoadError = null;
            if (!SeaBootstrap.TryLoad(seed, out var w, out var eng, out var err))
            {
                LoadError = err;   // 数据缺失: 停摆但保留错误提示(Update 已被 Engine==null 挡住)
                return;
            }
            World = w;
            Engine = eng;
            BuildMap();
            FrameMap();
            _loading = false;
        }

        void EnsureCamera()
        {
            if (Camera.main != null) { _cam = Camera.main; return; }
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            _cam = go.AddComponent<Camera>();
            _cam.backgroundColor = new Color(0.05f, 0.08f, 0.16f);
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 1000f;
        }

        // =============================================================
        // 地图标记
        // =============================================================
        void BuildMap()
        {
            _root = new GameObject("SeaMap").transform;

            var palette = new[]
            {
                new Color(0.36f, 0.66f, 0.95f), new Color(0.95f, 0.55f, 0.35f),
                new Color(0.45f, 0.85f, 0.55f), new Color(0.93f, 0.80f, 0.35f),
                new Color(0.85f, 0.45f, 0.62f), new Color(0.62f, 0.52f, 0.92f),
                new Color(0.40f, 0.82f, 0.85f), new Color(0.72f, 0.60f, 0.44f),
            };

            foreach (var p in World.Ports)
            {
                if (!_areaColor.ContainsKey(p.Area.Id))
                {
                    _areaColor[p.Area.Id] = palette[_areaOrder.Count % palette.Length];
                    _areaOrder.Add(p.Area.Id);
                }
            }

            foreach (var p in World.Ports)
            {
                var pos = LonLatToWorld(p.Lon, p.Lat);
                float r = 0.55f + p.Size * 0.35f;   // 城邦越大标记越大
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = p.Id;
                go.transform.SetParent(_root, false);
                go.transform.position = pos + Vector3.up * r; // 半球浮起便于点选
                go.transform.localScale = Vector3.one * r;
                if (go.TryGetComponent<Collider>(out var col)) col.isTrigger = false;
                if (go.TryGetComponent<Renderer>(out var ren))
                    ren.material = MakeMat(_areaColor[p.Area.Id]);
                _markers[p.Id] = go;
            }

            // 淡蓝"海面"打底 + 简单经纬参考, 方便看懂方位
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "SeaGround";
            ground.transform.SetParent(_root, false);
            ground.transform.localScale = new Vector3(300f, 0.2f, 160f);
            ground.transform.position = new Vector3(0f, -0.35f, 0f);
            if (ground.TryGetComponent<Renderer>(out var gren))
            {
                gren.material = MakeMat(new Color(0.10f, 0.28f, 0.46f, 0.85f));
                gren.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        static Material MakeMat(Color c)
        {
            var shader = Shader.Find("Standard");
            var m = shader != null ? new Material(shader) : new Material(Shader.Find("Sprites/Default"));
            m.color = c;
            return m;
        }

        // 相机初始框住地图
        void FrameMap()
        {
            float minLon = float.MaxValue, maxLon = float.MinValue;
            float minLat = float.MaxValue, maxLat = float.MinValue;
            foreach (var p in World.Ports)
            {
                minLon = Mathf.Min(minLon, p.Lon); maxLon = Mathf.Max(maxLon, p.Lon);
                minLat = Mathf.Min(minLat, p.Lat); maxLat = Mathf.Max(maxLat, p.Lat);
            }
            var c = LonLatToWorld((minLon + maxLon) * 0.5f, (minLat + maxLat) * 0.5f);
            _target = new Vector3(c.x, 0f, c.z);
            _dist = Mathf.Max(60f, (maxLon - minLon) * 0.9f);
            ApplyCamera();
        }

        void Update()
        {
            if (Engine == null) return;

            // 自动推进
            if (_auto)
            {
                _autoAccum += Time.deltaTime * autoDaysPerSecond;
                int n = (int)_autoAccum;
                if (n > 0) { Engine.AdvanceDays(n); _autoAccum -= n; }
            }

            HandleMouse();
            ApplyCamera();
            UpdateSelectionScale();
        }

        void UpdateSelectionScale()
        {
            if (_markers.Count == 0) return;
            foreach (var kv in _markers)
            {
                var p = World.FindPort(kv.Key);
                float r = 0.55f + p.Size * 0.35f;
                float k = (p == _selected) ? 1.45f : 1f;
                kv.Value.transform.localScale = Vector3.one * r * k;
            }
        }

        void HandleMouse()
        {
            if (_cam == null) return;
            // 左键点选港口
            if (Input.GetMouseButtonDown(0))
            {
                if (GuiHover()) return;
                var ray = _cam.ScreenPointToRay(Input.mousePosition);
                if (Physics.Raycast(ray, out var hit, 1000f) && hit.collider != null)
                {
                    var id = hit.collider.gameObject.name;
                    var p = World != null ? World.FindPort(id) : null;
                    if (p != null) _selected = p;
                }
            }
            // 右键拖动旋转
            if (Input.GetMouseButton(1))
            {
                _yaw += Input.GetAxis("Mouse X") * 3.2f;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * 2.4f, 8f, 89f);
            }
            // 滚轮缩放
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(wheel) > 0.0001f)
            {
                _dist = Mathf.Clamp(_dist * (1f - wheel * 0.12f), 8f, 600f);
            }
            // 中键平移(粗略)
            if (Input.GetMouseButton(2))
            {
                var axis = Input.GetAxis("Mouse X");
                _target += _cam.transform.right * -axis * (_dist * 0.004f);
            }
        }

        void ApplyCamera()
        {
            if (_cam == null) return;
            float ph = _pitch * Mathf.Deg2Rad, th = _yaw * Mathf.Deg2Rad;
            var offset = new Vector3(
                _dist * Mathf.Cos(ph) * Mathf.Sin(th),
                _dist * Mathf.Sin(ph),
                _dist * Mathf.Cos(ph) * Mathf.Cos(th));
            _cam.transform.position = _target + offset;
            _cam.transform.LookAt(_target, Vector3.up);
        }

        // ---------- GUI ----------
        void OnGUI()
        {
            EnsureStyles();
            if (LoadError != null)
            {
                GUI.Box(new Rect(10, 10, 560, 34), "数据加载失败: " + LoadError, _styleWarn);
                GUI.Label(new Rect(10, 50, 700, 30), "请确认 Assets/Resources/SeaData/*.json 齐全(项目需被 Unity 导入过一次)。", _styleArea);
                return;
            }
            if (Engine == null) return;

            DrawClockBar();
            DrawEventFeed();
            DrawSelectedPanel();
            DrawHint();
        }

        void DrawClockBar()
        {
            string date = $"{Engine.Year} 年 {Engine.Month} 月 {Engine.DayOfMonth} 日";
            string day = $"模拟第 {Engine.Day} 日 · 一年 = 360 日(12 月 × 30 日)";
            GUI.Box(new Rect(10, 10, 360, 96), "", _styleBox);
            GUI.Label(new Rect(24, 18, 340, 28), "SEA · 大航海模拟  " + date, _styleTitle);
            GUI.Label(new Rect(24, 46, 340, 20), day, _styleArea);

            var r1 = new Rect(24, 70, 60, 24);
            if (GUI.Button(r1, "+1日")) Engine.AdvanceDays(1);
            var r2 = new Rect(90, 70, 60, 24);
            if (GUI.Button(r2, "+7日")) Engine.AdvanceDays(7);
            var r3 = new Rect(156, 70, 66, 24);
            if (GUI.Button(r3, "+30日")) Engine.AdvanceDays(30);
            var r4 = new Rect(230, 70, 84, 24);
            _auto = GUI.Toggle(r4, _auto, _auto ? "自动 · 停" : "自动播放");
        }

        void DrawEventFeed()
        {
            var acts = Engine.ActiveEvents();
            if (acts.Count == 0) return;
            var names = acts.ConvertAll(e => e.D.name);
            GUI.Label(new Rect(10, 114, 640, 24), "进行中事件: " + string.Join("、", names), _styleWarn);
        }

        void DrawSelectedPanel()
        {
            if (_selected == null) return;
            var p = _selected;
            float w = 420f;
            var boxRect = new Rect(Screen.width - w - 10, 10, w, Screen.height - 20);
            GUI.Box(boxRect, "", _styleBox);

            var inner = new Rect(boxRect.x + 10, boxRect.y + 10, boxRect.width - 20, boxRect.height - 20);
            GUI.BeginGroup(inner);
            float x = 0;
            float yTop = 0;
            _sb.Length = 0;

            string title = $"{p.Name}  ({p.Area.Name} · {SizeDesc(p)})  封锁:{p.Blocked}";
            GUI.Label(new Rect(x, yTop, inner.width, 26), title, _styleTitle);
            yTop += 30;
            GUI.Label(new Rect(x, yTop, inner.width, 20), p.Desc, _styleArea);
            yTop += 26;

            _sb.AppendLine("特产/需求行情(进价←产 · 出价←需):");
            _sb.AppendLine();
            var cells = World.Market[p.Id];
            for (int i = 0; i < cells.Length; i++)
            {
                var c = cells[i];
                if (!c.Produced && !c.Needed) continue;
                int ask = Engine.AskPrice(p, c.Good);
                int bid = Engine.BidPrice(p, c.Good);
                int stock = (int)Math.Floor(c.Stock);
                string tag = c.Produced ? (c.Needed ? "产·需" : "特产") : "需求";
                _sb.AppendFormat("  [{0,-3}] {1,-5}  进{2,3}  出{3,3}  库{4,2}/{5,2}  R={6:0.00}\n",
                    tag, c.Good.Name, ask, bid, stock, (int)c.Cap, c.R);
            }
            GUILayout.BeginArea(new Rect(x, yTop, inner.width, inner.height - yTop - 8));
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label(_sb.ToString(), _styleRow);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            GUI.EndGroup();
        }

        void DrawHint()
        {
            GUI.Label(new Rect(12, Screen.height - 40, 560, 20),
                "左键点选港口 · 右键拖动旋转 · 滚轮缩放 · 中键平移 · 空格=自动播放(未实现热键则用按钮)", _styleArea);
            if (_selected != null)
                GUI.Label(new Rect(12, Screen.height - 20, 560, 18), "当前选中: " + _selected.Name, _styleTitle);
        }

        bool GuiHover()
        {
            // Input.mousePosition 原点在左下, GUI Rect 原点在左上 → 先翻转 Y
            var guiPos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (_selected != null)
            {
                float w = 420f;
                var boxRect = new Rect(Screen.width - w - 10, 10, w, Screen.height - 20);
                if (boxRect.Contains(guiPos)) return true;
            }
            var topRect = new Rect(0, 0, 420, 220);
            if (topRect.Contains(guiPos)) return true;
            return false;
        }

        void EnsureStyles()
        {
            if (_styleTitle != null) return;
            _font = CjkFont();
            var src = GUI.skin.label;
            _styleBox = new GUIStyle(GUI.skin.box);
            _styleTitle = new GUIStyle(src) { fontSize = 17, fontStyle = FontStyle.Bold };
            _styleTitle.normal.textColor = new Color(1f, 0.92f, 0.55f);
            _styleArea = new GUIStyle(src) { fontSize = 13 };
            _styleArea.normal.textColor = new Color(0.82f, 0.92f, 1f);
            _styleRow = new GUIStyle(src) { fontSize = 14, richText = false };
            _styleWarn = new GUIStyle(src) { fontSize = 15, fontStyle = FontStyle.Bold };
            _styleWarn.normal.textColor = new Color(1f, 0.5f, 0.4f);
            if (_font != null)
            {
                _styleBox.font = _font;
                _styleTitle.font = _font;
                _styleArea.font = _font;
                _styleRow.font = _font;
                _styleWarn.font = _font;
            }
        }

        static Font CjkFont()
        {
            // 让中文字形有保障: 逐个候选 OS 中文字体, 找不到则退回默认(编辑器下常可显示)
            var cand = new[]
            {
                "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑",
                "SimHei", "Noto Sans CJK SC", "PingFang SC", "WenQuanYi Micro Hei"
            };
            foreach (var name in cand)
            {
                try
                {
                    var f = Font.CreateDynamicFontFromOSFont(name, 16);
                    if (f != null) return f;
                }
                catch { }
            }
            return null;
        }

        static string SizeDesc(Port p)
        {
            switch (p.Size)
            {
                case 1: return "小镇";
                case 2: return "中型商港";
                case 3: return "大都会";
                default: return "?" + p.Size;
            }
        }
    }
}
