using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sea
{
    // =============================================================
    // SEA · 设置菜单(齿轮 → 设置)
    //   主菜单: 保存进度 / 载入进度 / 音乐音效 / 退出游戏
    //   音乐音效: 选 BGM 曲、音乐与音效两个音量滑杆、全部静音
    //   显示: 默认全屏; 勾选「窗口模式」→ 窗口化(顶部出现系统标题栏), 随 PlayerPrefs 存。
    //   SeaPlay 会把它 AddComponent 到同一物体上(再盖一块 60 层 Canvas = 全局最高)。
    // =============================================================
    [DefaultExecutionOrder(-40)]
    public sealed class SeaSettings : MonoBehaviour
    {
        SeaPlay play;
        SeaAudio audio;

        bool _built, _open, _audioOpen;
        GameObject _overlay, _mainCard, _audioCard;
        Text _hint;
        Text _flagLbl;   // 地图旗标开关按钮上的文字(设置主卡)
        Text _winLbl;    // 窗口模式开关按钮上的文字(设置主卡)

        // 设置弹层此刻是否开着(HUD AnyTopOpen 并入 → 开着时吞掉地图输入并藏掉地名)
        public bool OpenNow => _open;

        // 音乐音效页引用
        readonly List<Button> _tracks = new List<Button>();
        Button _muteBtn; Text _muteLbl;
        Image _bgmFill, _sfxFill;
        Text _bgmVal, _sfxVal;
        readonly List<Image> _trackImgs = new List<Image>();

        static Sprite s_white;
        static Font s_font;
        static readonly Color ColPanel = new Color(0.055f, 0.09f, 0.14f, 0.98f);
        static readonly Color ColBtn = new Color(0.16f, 0.44f, 0.55f, 1f);
        static readonly Color ColAct = new Color(0.93f, 0.68f, 0.18f, 1f);
        static readonly Color ColGold = new Color(1f, 0.85f, 0.45f);
        static readonly Color ColTxt = new Color(0.93f, 0.95f, 0.98f);
        static readonly Color ColDim = new Color(0.62f, 0.70f, 0.78f);
        static readonly Color ColFill = new Color(0.95f, 0.78f, 0.30f, 1f);

        // ---- 全屏 / 窗口模式: 启动默认全屏; 勾选「窗口模式」→ 桌面窗口(顶部出现系统标题栏)。
        //      开关随 PlayerPrefs 存, 下次启动自动恢复(没存过 = 全屏)。 ----
        const string PrefWin = "SEA_windowed";
        public static bool DisplayWindowed => PlayerPrefs.GetInt(PrefWin, 0) != 0;   // 缺省 0 → 全屏(首页设置卡也读它)
        public static void SetDisplayWindowed(bool win)
        {
            PlayerPrefs.SetInt(PrefWin, win ? 1 : 0);
            PlayerPrefs.Save();
            ApplyWindowed(win);
        }
        // 启动调用: 应用存档的显示模式; 没存过 → 默认全屏
        public static void ApplyStartupDisplay() => ApplyWindowed(DisplayWindowed);
        public static void ApplyWindowed(bool win)
        {
            if (Application.isEditor) return;   // 编辑器里不折腾全屏/分辨率
            if (!win)
            {
                // 全屏: 必须以显示器原生分辨率打开。只设 Screen.fullScreen=true 会沿用
                // 上一步的窗口分辨率(如 1600×900), 由显卡拉伸撑满全屏 → 文字发虚。
                // 边框无窗 FullScreenWindow 逐点对桌面像素渲染, 最清晰且切换不黑屏。
                int dw = Screen.currentResolution.width, dh = Screen.currentResolution.height;
                if (dw > 0 && dh > 0) Screen.SetResolution(dw, dh, FullScreenMode.FullScreenWindow);
                else Screen.fullScreen = true;   // 兜底(理论走不到)
                return;
            }
            int cw = Screen.currentResolution.width, ch = Screen.currentResolution.height;
            int w = Mathf.Min(1600, cw);                 // 窗口上限 1600 宽, 桌面不够就随桌面
            int h = Mathf.RoundToInt(w * (9f / 16f));
            if (w >= cw || h >= ch) { w = cw; h = ch; }  // 桌面偏小 → 直接整桌面窗口
            Screen.fullScreen = false;                   // 窗口化 → 顶部出现系统标题栏
            Screen.SetResolution(w, h, false);
        }

        void Awake() { play = GetComponent<SeaPlay>(); }

        void LateUpdate()
        {
            if (play == null || play.engine == null) return;
            if (!_built) BuildAll();
            if (_open && _audioOpen) RefreshAudioUI();
        }

        // =============================================================
        // 搭建
        // =============================================================
        void BuildAll()
        {
            _built = true;
            audio = play.audio != null ? play.audio : SeaAudio.Ensure(this);
            EnsureEventSystem();

            var cvGo = new GameObject("Settings Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            cvGo.transform.SetParent(transform, false);
            var cv = cvGo.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 60;   // 全局最高层(高于 HUD 20 / 首页 40): 地图地名不可能再盖住设置弹层
            var sc = cvGo.GetComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1280, 720);
            sc.matchWidthOrHeight = 0.5f;

            var root = Rt(NewRT("root", cvGo.transform));
            FillRect(root);

            // 全屏遮罩(打开时挡住地图)
            _overlay = Panel("overlay", root, new Color(0.01f, 0.02f, 0.04f, 0.62f));
            FillRect(Rt(_overlay));
            _overlay.GetComponent<Image>().raycastTarget = true;   // 挡住地图点击, 自身子卡片仍可点
            _overlay.SetActive(false);

            BuildMainCard();
            BuildAudioCard();
        }

        // 供 HUD 右下操作条内的「⚙ 设置」按钮调用(原右上角齿轮已移入底部黑框)
        public void OpenMenu()
        {
            if (!_built) BuildAll();
            OpenMain();
        }

        void OpenMain()
        {
            _audioOpen = false;
            _overlay.SetActive(true);
            _open = true;
            _mainCard.SetActive(true);
            _audioCard.SetActive(false);
            _hint.text = "";
            RefreshFlagLabel();
            RefreshWindowLabel();
        }
        void Close() { _open = false; _audioOpen = false; _overlay.SetActive(false); }

        // 地图旗标: 大本营黄旗 + 到过城市红旗(开关随存档)
        void ToggleMapFlags()
        {
            if (play == null) return;
            play.FlagsOn = !play.FlagsOn;
            play.ApplyCityFlags();
            RefreshFlagLabel();
        }
        void RefreshFlagLabel()
        {
            if (_flagLbl == null || play == null) return;
            _flagLbl.text = play.FlagsOn
                ? "🚩  地图旗标: 开(大本营插黄旗, 到过的城市插红旗)"
                : "🚩  地图旗标: 关";
        }

        void OpenAudio()
        {
            _audioOpen = true;
            _mainCard.SetActive(false);
            _audioCard.SetActive(true);
            if (SeaAudio.Instance != null) SeaAudio.Instance.SfxPopup();
            RefreshAudioUI();
        }
        void BackToMain() { _audioOpen = false; _mainCard.SetActive(true); _audioCard.SetActive(false); }

        void BuildMainCard()
        {
            _mainCard = Card("mainCard", "设  置");
            _mainCard.transform.SetParent(_overlay.transform, false);

            // 关闭 X(卡片右上)
            MakeBtn(_mainCard.transform, "x", "✕", 16, ColBtn, Color.white)
                .onClick.AddListener(() => { if (SeaAudio.Instance != null) SeaAudio.Instance.SfxClick(); Close(); });
            RectAt(Rt(_mainCard.transform.Find("x").gameObject), new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(1, 1), new Vector2(-6, -6), new Vector2(46, 38));

            // 四个主按钮(文字)
            MakeBtn(_mainCard.transform, "save", "💾  保存进度", 20, ColBtn, Color.white);
            RectAt(Rt(_mainCard.transform.Find("save").gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0.5f, 1), new Vector2(0, -84), new Vector2(520, 56));
            _mainCard.transform.Find("save").GetComponent<Button>()
                .onClick.AddListener(OnSave);

            MakeBtn(_mainCard.transform, "load", "📂  载入进度", 20, ColBtn, Color.white);
            RectAt(Rt(_mainCard.transform.Find("load").gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0.5f, 1), new Vector2(0, -150), new Vector2(520, 56));
            _mainCard.transform.Find("load").GetComponent<Button>()
                .onClick.AddListener(OnLoad);

            MakeBtn(_mainCard.transform, "music", "🎵  音乐音效", 20, ColBtn, Color.white);
            RectAt(Rt(_mainCard.transform.Find("music").gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0.5f, 1), new Vector2(0, -216), new Vector2(520, 56));
            _mainCard.transform.Find("music").GetComponent<Button>()
                .onClick.AddListener(OpenAudio);

            MakeBtn(_mainCard.transform, "exit", "🚪  退出游戏", 20, new Color(0.55f, 0.22f, 0.2f, 1f), Color.white);
            RectAt(Rt(_mainCard.transform.Find("exit").gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0.5f, 1), new Vector2(0, -282), new Vector2(520, 56));
            _mainCard.transform.Find("exit").GetComponent<Button>()
                .onClick.AddListener(OnExit);

            // 地图旗标开关: 大本营(黄, 稍大) + 每新到城市插小红旗
            var flag = MakeBtn(_mainCard.transform, "flag", "", 16, ColBtn, Color.white);
            RectAt(Rt(flag.gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0, -348), new Vector2(520, 52));
            _flagLbl = flag.GetComponentInChildren<Text>();
            flag.onClick.AddListener(ToggleMapFlags);

            // 窗口模式复选框: ☐ 全屏(启动默认) / ☑ 窗口化运行 → 顶部出现系统标题栏
            var win = MakeBtn(_mainCard.transform, "win", "", 16, ColBtn, Color.white);
            RectAt(Rt(win.gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0, -406), new Vector2(520, 52));
            _winLbl = win.GetComponentInChildren<Text>();
            win.onClick.AddListener(ToggleWindowed);

            _hint = AddText(_mainCard.transform, "", 15, new Color(1f, 0.62f, 0.55f), TextAnchor.MiddleCenter);
            RectAt(Rt(_hint.gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0, -470), new Vector2(560, 58));
            _hint.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        // 窗口模式开关: 在 全屏 / 窗口化(带标题栏) 间切换并落盘
        void ToggleWindowed()
        {
            if (SeaAudio.Instance != null) SeaAudio.Instance.SfxClick();
            SetDisplayWindowed(!DisplayWindowed);
            RefreshWindowLabel();
        }
        void RefreshWindowLabel()
        {
            if (_winLbl == null) return;
            bool win = DisplayWindowed;
            _winLbl.text = (win ? "☑" : "☐") + "  窗口模式: "
                + (win ? "窗口化运行(顶部出现标题栏)" : "全屏运行(默认)");
        }

        void OnSave()
        {
            if (SeaAudio.Instance != null) SeaAudio.Instance.SfxSave();
            bool ok = play.SaveGame();
            if (!ok) _hint.text = "没能保存: " + (play.Banner ?? "");
            else { Close(); }
        }
        void OnLoad()
        {
            if (SeaAudio.Instance != null) SeaAudio.Instance.SfxClick();
            bool ok = play.LoadGame();
            if (!ok) _hint.text = "没能载入: " + (play.Banner ?? "");
            else { Close(); }
        }
        void OnExit()
        {
            if (SeaAudio.Instance != null) SeaAudio.Instance.SfxExit();
            StartCoroutine(QuitSoon());
        }
        IEnumerator QuitSoon()
        {
            yield return new WaitForSeconds(0.4f);
            Application.Quit();
        }

        void BuildAudioCard()
        {
            _audioCard = Card("audioCard", "音乐 与 音效");
            _audioCard.transform.SetParent(_overlay.transform, false);
            _audioCard.SetActive(false);

            // 返回按钮
            var back = MakeBtn(_audioCard.transform, "back", "← 返回", 16, ColBtn, Color.white);
            back.onClick.AddListener(BackToMain);
            RectAt(Rt(back.gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0, -420), new Vector2(200, 48));

            // 曲目标签
            var tlab = AddText(_audioCard.transform, "背景音乐(点选播放)", 15, ColGold, TextAnchor.MiddleLeft);
            RectAt(Rt(tlab.gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(46, -66), new Vector2(380, 24));

            int n = audio != null ? audio.TrackCount : 0;
            if (n == 0)
            {
                var none = AddText(_audioCard.transform, "(Resources/bgm 下没找到音乐文件)", 14, ColDim, TextAnchor.MiddleCenter);
                RectAt(Rt(none.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                    new Vector2(0, -116), new Vector2(0, 22));
            }
            else
            {
                float bw = n > 1 ? (560f - (n - 1) * 14f) / n : 280f;
                float startX = -(n * bw + (n - 1) * 14f) * 0.5f;
                for (int i = 0; i < n; i++)
                {
                    int idx = i;
                    var b = MakeBtn(_audioCard.transform, "trk" + i, (i + 1) + ". " + audio.TrackLabel(i), 15, ColBtn, Color.white);
                    b.onClick.AddListener(() => { if (audio != null) audio.PlayBgmTrack(idx); });
                    RectAt(Rt(b.gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                        new Vector2(startX + i * (bw + 14f) + bw * 0.5f, -110), new Vector2(bw, 48));
                    _tracks.Add(b);
                    _trackImgs.Add(b.GetComponent<Image>());
                }
            }

            // 两条音量滑杆
            _bgmVal = BuildSlider(_audioCard.transform, "音乐音量", 190, v => { if (audio != null) audio.SetBgmVolume(v); }, out _bgmFill);
            _sfxVal = BuildSlider(_audioCard.transform, "音效音量", 268, v => { if (audio != null) audio.SetSfxVolume(v); }, out _sfxFill);

            // 全部静音
            _muteBtn = MakeBtn(_audioCard.transform, "mute", "", 16, ColBtn, Color.white);
            RectAt(Rt(_muteBtn.gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0, -336), new Vector2(300, 50));
            _muteBtn.onClick.AddListener(() => { if (audio != null) audio.ToggleMute(); });
            _muteLbl = _muteBtn.GetComponentInChildren<Text>();

            RefreshAudioUI();
        }

        // 建一行滑杆: 左标签 + 底轨(可拖) + 右百分比
        Text BuildSlider(Transform parent, string label, float yTop, System.Action<float> onSet, out Image fill)
        {
            var lb = AddText(parent, label, 16, ColTxt, TextAnchor.MiddleLeft);
            RectAt(Rt(lb.gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(46, -yTop), new Vector2(150, 30));

            var barGo = Panel("bar_" + label, parent, new Color(0, 0, 0, 0.55f));
            RectAt(Rt(barGo), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(200, -yTop), new Vector2(360, 30));
            var barImg = barGo.GetComponent<Image>();
            barImg.raycastTarget = true;

            var fgo = NewRT("fill", barGo.transform);
            fill = fgo.AddComponent<Image>();
            fill.sprite = White();
            fill.color = ColFill;
            fill.raycastTarget = false;
            var fr = Rt(fgo);
            fr.anchorMin = new Vector2(0, 0); fr.anchorMax = new Vector2(0, 1);
            fr.pivot = new Vector2(0, 0.5f);
            fr.anchoredPosition = Vector2.zero;
            fr.sizeDelta = new Vector2(20, 0);

            var bar = barGo.AddComponent<SeaVolumeBar>();
            float start = audio != null ? (label == "音乐音量" ? audio.BgmVolume : audio.SfxVolume) : 0.7f;
            bar.Setup(fill, start, onSet);

            var val = AddText(parent, "", 15, ColGold, TextAnchor.MiddleRight);
            RectAt(Rt(val.gameObject), new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-40, -yTop), new Vector2(70, 30));
            return val;
        }

        void RefreshAudioUI()
        {
            if (audio == null) return;
            // 当前曲目高亮
            for (int i = 0; i < _tracks.Count; i++)
            {
                bool on = i == audio.TrackIndex;
                if (_trackImgs.Count > i)
                {
                    _trackImgs[i].color = on ? ColAct : ColBtn;
                    var t = _tracks[i].GetComponentInChildren<Text>();
                    if (t != null) t.color = on ? new Color(0.15f, 0.08f, 0.04f) : Color.white;
                }
            }
            if (_muteLbl != null)
                _muteLbl.text = "全部静音: " + (audio.Muted ? "开(静音中)" : "关");
            if (_muteBtn != null)
                _muteBtn.GetComponent<Image>().color = audio.Muted ? ColAct : ColBtn;

            // 滑杆当前值(百分比文字)
            if (_bgmVal != null) _bgmVal.text = Mathf.RoundToInt(audio.BgmVolume * 100) + "%";
            if (_sfxVal != null) _sfxVal.text = Mathf.RoundToInt(audio.SfxVolume * 100) + "%";
        }

        // =============================================================
        // 卡片通用
        // =============================================================
        GameObject Card(string name, string title)
        {
            var c = Panel(name, null, ColPanel);
            RectAt(Rt(c), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0, 10), new Vector2(640, 540));
            var t = AddText(c.transform, "⚓ " + title, 24, ColGold, TextAnchor.MiddleCenter);
            RectAt(Rt(t.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                new Vector2(0, -6), new Vector2(0, 40));
            return c;
        }

        void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            go.transform.SetParent(transform, false);
        }

        // ---------- 素材小工具 ----------
        static Font F()
        {
            if (s_font != null) return s_font;
            try { s_font = Font.CreateDynamicFontFromOSFont(new[] {
                "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "SimHei",
                "Source Han Sans SC", "WenQuanYi Micro Hei", "sans-serif" }, 18); }
            catch { s_font = null; }
            if (s_font == null) s_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return s_font;
        }
        static Sprite White()
        {
            if (s_white != null) return s_white;
            var tx = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tx.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
            tx.Apply();
            s_white = Sprite.Create(tx, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));
            return s_white;
        }
        static GameObject NewRT(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }
        static RectTransform Rt(GameObject g) => (RectTransform)g.transform;
        static void FillRect(RectTransform r)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
        }
        static void RectAt(RectTransform r, Vector2 min, Vector2 max, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            r.anchorMin = min; r.anchorMax = max;
            r.pivot = pivot;
            r.anchoredPosition = pos;
            r.sizeDelta = size;
        }
        GameObject Panel(string name, Transform parent, Color c)
        {
            var go = NewRT(name, parent);
            var img = go.AddComponent<Image>();
            img.sprite = White();
            img.color = c;
            img.raycastTarget = false;
            return go;
        }
        Text AddText(Transform parent, string s, int size, Color col, TextAnchor align)
        {
            var go = NewRT("txt", parent);
            var t = go.AddComponent<Text>();
            t.font = F();
            t.fontSize = size;
            t.color = col;
            t.alignment = align;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            t.raycastTarget = false;
            t.text = s;
            return t;
        }
        Button MakeBtn(Transform parent, string nm, string caption, int size, Color fill, Color textCol)
        {
            var go = NewRT(nm, parent);
            var img = go.AddComponent<Image>();
            img.sprite = White();
            img.color = fill;
            var b = go.AddComponent<Button>();
            b.targetGraphic = img;
            var cols = b.colors;
            cols.highlightedColor = new Color(1.15f, 1.15f, 1.15f);
            cols.pressedColor = new Color(0.82f, 0.82f, 0.82f);
            b.colors = cols;
            var t = AddText(go.transform, caption, size, textCol, TextAnchor.MiddleCenter);
            FillRect(Rt(t.gameObject));
            b.onClick.AddListener(() => { if (SeaAudio.Instance != null) SeaAudio.Instance.SfxClick(); });
            return b;
        }
    }
}
