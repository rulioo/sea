using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sea
{
    // =============================================================
    // SEA · 启动首页(纯代码 uGUI, Canvas sortingOrder 40 = 最高层)
    //   打开游戏先盖一整屏黑色:
    //     - 中央黑色大框内放大 logo(Resources/logo/logo.png)
    //     - 四个按钮: 新开航程 ▶ / 继续辉煌 📂 / 设置 ⚙ / 退出 🚪
    //     - 主页背景曲固定 head.wav(SeaPlay.ShowHome 里先播好)
    //   「设置」= 音乐音效小卡(音乐/音效音量滑杆 + 全部静音 + 返回)。
    //   由 SeaPlay.Start 在开局时 AddComponent 并 Open(); 不需要它时 Hide()。
    // =============================================================
    public sealed class SeaHome : MonoBehaviour
    {
        SeaPlay _play;
        SeaAudio _audio;

        bool _built;
        GameObject _overlay, _mainCard, _audioCard;
        Text _hint;

        Text _bgmVal, _sfxVal, _muteLbl;
        Button _muteBtn;

        static Sprite s_white;
        static Font s_font;

        // 色板(与 HUD/设置一致: 深海蓝底 + 航海金)
        static readonly Color ColPage = new Color(0.012f, 0.02f, 0.032f, 1f);   // 整屏黑底
        static readonly Color ColFrame = new Color(0.02f, 0.02f, 0.028f, 1f);   // logo 黑框
        static readonly Color ColCard = new Color(0.045f, 0.07f, 0.10f, 0.985f); // 音频小卡
        static readonly Color ColBtn = new Color(0.16f, 0.44f, 0.55f, 1f);
        static readonly Color ColGoldBtn = new Color(0.93f, 0.68f, 0.18f, 1f);
        static readonly Color ColRed = new Color(0.55f, 0.22f, 0.20f, 1f);
        static readonly Color ColGold = new Color(1f, 0.85f, 0.45f);
        static readonly Color ColTxt = new Color(0.93f, 0.95f, 0.98f);
        static readonly Color ColDim = new Color(0.66f, 0.72f, 0.80f);
        static readonly Color ColFill = new Color(0.95f, 0.78f, 0.30f, 1f);

        void Awake()
        {
            _play = GetComponent<SeaPlay>();
            _audio = GetComponent<SeaAudio>();
        }

        public bool Showing => _overlay != null && _overlay.activeSelf;

        // 打开首页(世界已在底下搭好, 由黑底盖住)
        public void Open()
        {
            if (!_built) BuildAll();
            _overlay.SetActive(true);
            if (_audio == null) _audio = SeaAudio.Instance;
            if (_audio != null) _audio.PlayBgmByName("head");   // 首页固定片头曲
            ShowMain();
        }

        public void Hide()
        {
            if (_overlay != null) _overlay.SetActive(false);
        }

        // 首页上的提示行(无档/读档失败/开局失败等), 会切回四键主视图
        public void Hint(string msg)
        {
            if (_hint != null) _hint.text = msg ?? "";
            ShowMain();
        }

        void ShowMain()
        {
            if (_audioCard != null) _audioCard.SetActive(false);
            if (_mainCard != null) _mainCard.SetActive(true);
        }

        void ShowAudio()
        {
            if (_mainCard != null) _mainCard.SetActive(false);
            if (_audioCard != null) _audioCard.SetActive(true);
            RefreshAudioUI();
        }

        // =============================================================
        // 搭建
        // =============================================================
        void BuildAll()
        {
            _built = true;
            EnsureEventSystem();

            var cvGo = new GameObject("Home Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            cvGo.transform.SetParent(transform, false);
            var cv = cvGo.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 40;   // 盖在 HUD(20) 与 设置(30) 之上
            var sc = cvGo.GetComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1280, 720);
            sc.matchWidthOrHeight = 0.5f;

            var root = Rt(NewRT("root", cvGo.transform));
            FillRect(root);

            // 整屏黑底 + 拦点击
            _overlay = Panel("overlay", root, ColPage);
            FillRect(Rt(_overlay));
            _overlay.GetComponent<Image>().raycastTarget = true;

            _mainCard = NewRT("main", _overlay.transform);
            FillRect(Rt(_mainCard));

            BuildLogo();
            BuildButtons();
            _hint = AddText(_mainCard.transform, "", 15, new Color(1f, 0.66f, 0.56f), TextAnchor.MiddleCenter);
            RectAt(Rt(_hint.gameObject), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(0, 26), new Vector2(820, 44));
            _hint.horizontalOverflow = HorizontalWrapMode.Wrap;

            BuildAudioCard();
        }

        // 中央 logo: 金色细沿框 + 黑色大框 + 放大 logo(无图时退文字题名)
        void BuildLogo()
        {
            var rim = Panel("logo_rim", _mainCard.transform, new Color(0.52f, 0.42f, 0.20f, 0.9f));
            RectAt(Rt(rim), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0, 150), new Vector2(916, 312));

            var frame = Panel("logo_frame", _mainCard.transform, ColFrame);
            RectAt(Rt(frame), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0, 150), new Vector2(900, 300));

            var tex = Resources.Load<Texture2D>("logo/logo");
            if (tex == null)
            {
                var t = AddText(frame.transform, "大航海时代2026", 62, ColGold, TextAnchor.MiddleCenter);
                RectAt(Rt(t.gameObject), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(0, 0), new Vector2(820, 200));
                return;
            }

            var go = NewRT("logoImg", frame.transform);
            var img = go.AddComponent<Image>();
            img.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            img.raycastTarget = false;

            // 放大版: 在框内(≈820×230)按宽高比尽量撑大
            float boxW = 820f, boxH = 230f;
            float asp = tex.width / (float)Mathf.Max(1, tex.height);
            float ratio = boxW / boxH;
            float w = boxW, h = boxH;
            if (asp > ratio) h = boxW / asp;
            else w = boxH * asp;
            RectAt(Rt(go), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(w, h));
        }

        void BuildButtons()
        {
            float[] ys = { -60f, -134f, -208f, -282f };
            MakeMainBtn("btnNew", "⚓  新开航程", ColGoldBtn, new Color(0.16f, 0.09f, 0.02f), ys[0])
                .onClick.AddListener(() => { if (_play != null) _play.StartNewVoyage(); });
            MakeMainBtn("btnCont", "📂  继续辉煌", ColBtn, Color.white, ys[1])
                .onClick.AddListener(() => { if (_play != null) _play.ContinueVoyage(); });
            MakeMainBtn("btnSet", "🎵  设置", ColBtn, Color.white, ys[2])
                .onClick.AddListener(ShowAudio);
            MakeMainBtn("btnQuit", "🚪  退出游戏", ColRed, Color.white, ys[3])
                .onClick.AddListener(() => { StartCoroutine(QuitSoon()); });
        }

        Button MakeMainBtn(string nm, string caption, Color fill, Color txt, float y)
        {
            var b = MakeBtn(_mainCard.transform, nm, caption, 23, fill, txt);
            RectAt(Rt(b.gameObject), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0, y), new Vector2(520, 60));
            return b;
        }

        IEnumerator QuitSoon()
        {
            yield return new WaitForSeconds(0.4f);
            Application.Quit();
        }

        // ---- 音频设置小卡 ----
        void BuildAudioCard()
        {
            _audioCard = Panel("audioCard", _overlay.transform, ColCard);
            RectAt(Rt(_audioCard), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0, 0), new Vector2(640, 470));
            _audioCard.SetActive(false);

            var t = AddText(_audioCard.transform, "设  置 · 音乐与音效", 22, ColGold, TextAnchor.MiddleCenter);
            RectAt(Rt(t.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                new Vector2(0, -6), new Vector2(0, 38));

            var note = AddText(_audioCard.transform, "(主页正放「片头 · 港口」; 音量即时生效, 进游戏后可用 ⚙设置 换 BGM 曲目)",
                13, ColDim, TextAnchor.MiddleCenter);
            RectAt(Rt(note.gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0, -50), new Vector2(600, 22));
            note.horizontalOverflow = HorizontalWrapMode.Overflow;

            _bgmVal = BuildVolumeSlider(_audioCard.transform, "音乐音量", 96,
                v => { if (_audio != null) _audio.SetBgmVolume(v); }, out _);
            _sfxVal = BuildVolumeSlider(_audioCard.transform, "音效音量", 164,
                v => { if (_audio != null) _audio.SetSfxVolume(v); }, out _);

            _muteBtn = MakeBtn(_audioCard.transform, "mute", "", 16, ColBtn, Color.white);
            RectAt(Rt(_muteBtn.gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0, -226), new Vector2(320, 50));
            _muteBtn.onClick.AddListener(() => { if (_audio != null) _audio.ToggleMute(); });
            _muteLbl = _muteBtn.GetComponentInChildren<Text>();

            var back = MakeBtn(_audioCard.transform, "back", "← 返回", 17, ColBtn, Color.white);
            RectAt(Rt(back.gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0, -400), new Vector2(200, 52));
            back.onClick.AddListener(ShowMain);

            RefreshAudioUI();
        }

        // 一行滑杆: 左标签 + 底轨(SeaVolumeBar 可拖可点) + 右百分比
        Text BuildVolumeSlider(Transform parent, string label, float yTop, Action<float> onSet, out Image fill)
        {
            var lb = AddText(parent, label, 16, ColTxt, TextAnchor.MiddleLeft);
            RectAt(Rt(lb.gameObject), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(36, -yTop), new Vector2(150, 30));

            var barGo = Panel("bar_" + label, parent, new Color(0, 0, 0, 0.55f));
            RectAt(Rt(barGo), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(190, -yTop), new Vector2(370, 30));
            barGo.GetComponent<Image>().raycastTarget = true;

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
            float start = _audio != null
                ? (label == "音乐音量" ? _audio.BgmVolume : _audio.SfxVolume)
                : 0.7f;
            bar.Setup(fill, start, onSet);

            var val = AddText(parent, "", 15, ColGold, TextAnchor.MiddleRight);
            RectAt(Rt(val.gameObject), new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-32, -yTop), new Vector2(70, 30));
            return val;
        }

        void RefreshAudioUI()
        {
            if (_audio == null) return;
            if (_bgmVal != null) _bgmVal.text = Mathf.RoundToInt(_audio.BgmVolume * 100) + "%";
            if (_sfxVal != null) _sfxVal.text = Mathf.RoundToInt(_audio.SfxVolume * 100) + "%";
            if (_muteLbl != null) _muteLbl.text = "全部静音: " + (_audio.Muted ? "开(静音中)" : "关");
            if (_muteBtn != null)
                _muteBtn.GetComponent<Image>().color = _audio.Muted ? ColGoldBtn : ColBtn;
        }

        void LateUpdate()
        {
            if (!_built || _overlay == null || !_overlay.activeSelf) return;
            if (_audioCard != null && _audioCard.activeSelf) RefreshAudioUI();
        }

        // =============================================================
        // 通用小工具(镜像 SeaHud / SeaSettings)
        // =============================================================
        void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            go.transform.SetParent(transform, false);
        }

        static Font F()
        {
            if (s_font != null) return s_font;
            try
            {
                s_font = Font.CreateDynamicFontFromOSFont(new[]
                {
                    "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "SimHei",
                    "Source Han Sans SC", "WenQuanYi Micro Hei", "sans-serif"
                }, 18);
            }
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
