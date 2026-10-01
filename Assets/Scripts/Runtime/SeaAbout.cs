using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sea
{
    // =============================================================
    // SEA · 「关于」弹层(版本号 + 作者)
    //
    //   为什么自己建 Canvas 而不是塞进 SeaSettings: 启动首页(SeaHome)有它**自己**一份设置小卡,
    //   跟游戏内的 ⚙设置 是两套界面。关于这东西只该有一份文字来源, 于是做成能自立的弹层,
    //   两处入口都只是调 SeaAbout.Show() —— 以后改作者/版本只动这一个文件。
    //
    //   版本号的**唯一真源是 ProjectSettings 的 bundleVersion**(发版只改那一处),
    //   这里读 Application.version —— 打包后它返回的就是 bundleVersion。
    //   安装包不归 Unity 管, 另需同步 install/sea_install.iss 的 AppVersion。
    // =============================================================
    public sealed class SeaAbout : MonoBehaviour
    {
        static SeaAbout s_inst;
        bool _built;
        GameObject _overlay;
        Text _verTxt;

        static Sprite s_white;
        static Font s_font;

        static readonly Color ColPanel = new Color(0.055f, 0.09f, 0.14f, 0.98f);
        static readonly Color ColBtn = new Color(0.16f, 0.44f, 0.55f, 1f);
        static readonly Color ColGold = new Color(1f, 0.85f, 0.45f);
        static readonly Color ColTxt = new Color(0.93f, 0.95f, 0.98f);
        static readonly Color ColDim = new Color(0.62f, 0.70f, 0.78f);

        const string Author = "wx: rulioo521235";

        // 从任何地方调起。实例没了(或场景重载后被销毁)就现建一个 —— Show() 永远能用。
        public static void Show()
        {
            if (s_inst == null)
            {
                var go = new GameObject("SeaAbout");
                s_inst = go.AddComponent<SeaAbout>();   // AddComponent 会立刻跑 Awake, 那里认下 s_inst
            }
            s_inst.Open();
        }

        // 关掉(与 Show 对称)。给截图闸门连拍两张浮层用 —— 上一张不收掉就会盖在下一张上。
        public static void Hide()
        {
            if (s_inst != null) s_inst.Close();
        }

        void Awake() { s_inst = this; }

        // 开关都不自己放音效: 入口按钮是 MakeBtn 造的, 它挂的监听里已经有一声 click 了,
        //   这里再补一声就是"咔咔"两下。版本号在**每次打开时**重读 —— 免得以后做成
        //   "不重启换版本"(编辑器里改 bundleVersion)时显示的还是旧值。
        void Open()
        {
            if (!_built) Build();
            if (_verTxt != null) _verTxt.text = "版本  " + Application.version;
            _overlay.SetActive(true);
        }

        void Close() { _overlay.SetActive(false); }

        // =============================================================
        // 搭建(一次性)
        // =============================================================
        void Build()
        {
            _built = true;
            EnsureEventSystem();

            var cvGo = new GameObject("About Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            cvGo.transform.SetParent(transform, false);
            var cv = cvGo.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 70;   // 压过设置弹层(60): 从设置里点开的关于, 得盖在设置上面
            var sc = cvGo.GetComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1280, 720);
            sc.matchWidthOrHeight = 0.5f;

            var root = Rt(NewRT("root", cvGo.transform));
            FillRect(root);

            _overlay = Panel("overlay", root, new Color(0.01f, 0.02f, 0.04f, 0.62f));
            FillRect(Rt(_overlay));
            _overlay.GetComponent<Image>().raycastTarget = true;   // 挡住底下的地图/设置, 只让卡片可点

            var card = Panel("card", _overlay.transform, ColPanel);
            RectAt(Rt(card), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(560, 400));

            // 标题
            var title = AddText(card.transform, "⚓  大航海时代2026", 28, ColGold, TextAnchor.MiddleCenter);
            RectAt(Rt(title.gameObject), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                new Vector2(0, -34), new Vector2(0, 44));

            // 一条细金线, 把标题与信息分开
            var rule = Panel("rule", card.transform, new Color(1f, 0.85f, 0.45f, 0.35f));
            RectAt(Rt(rule), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0, -84), new Vector2(380, 2));

            _verTxt = AddText(card.transform, "版本  " + Application.version, 22, ColTxt, TextAnchor.MiddleCenter);
            RectAt(Rt(_verTxt.gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0, -132), new Vector2(480, 34));

            var au = AddText(card.transform, "作者  " + Author, 20, ColTxt, TextAnchor.MiddleCenter);
            RectAt(Rt(au.gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0, -180), new Vector2(480, 32));

            var sub = AddText(card.transform, "航海贸易 · 探索 · 舰队经营\n内容均为原创", 15, ColDim, TextAnchor.MiddleCenter);
            RectAt(Rt(sub.gameObject), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0, -240), new Vector2(480, 60));

            var back = MakeBtn(card.transform, "back", "返  回", 18, ColBtn, Color.white);
            RectAt(Rt(back.gameObject), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(0, 28), new Vector2(200, 48));
            back.onClick.AddListener(Close);

            _overlay.SetActive(false);
        }

        void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            go.transform.SetParent(transform, false);
        }

        // ---------- 素材小工具(镜像 SeaSettings / SeaHome) ----------
        static Font F()
        {
            if (s_font != null) return s_font;
            try
            {
                s_font = Font.CreateDynamicFontFromOSFont(new[] {
                    "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "SimHei",
                    "Source Han Sans SC", "WenQuanYi Micro Hei", "sans-serif" }, 18);
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
            return b;
        }
    }
}
