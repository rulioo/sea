using System;
using System.IO;
using UnityEngine;

namespace Sea
{
    // =============================================================
    // 无头截图闸门 —— 只在带 `-seaShot <目录>` 启动时由 SeaPlay.Awake 挂上。
    //
    //   为什么非要有它: 球面相机一旦算出 NaN、或绕序翻反, 画面会**整片黑掉 / 整个翻成背面**,
    //   而进程照样活着、日志照样干净。"跑 25 秒没崩"这套冒烟对这两种最典型的坏法完全无感 ——
    //   构建出来的**像素**才是唯一能戳穿它的证据。
    //
    //   用法: Build\SEA.exe -seaShot dev\shots            开局跑一轮, 截 01~03 与 06~08
    //         Build\SEA.exe -seaShot dev\shots load       接着上一轮的存档, 截 04~05
    //   产物: <目录>/01_….png …  (截完看图, 别看日志)
    //
    //   除了看图, 还有两件事能自动量(都不靠眼睛):
    //     · 舰队居中偏差 —— 每张图按快门前往日志打一行「舰队居中[…]」, 直接给出偏了几个像素。
    //     · 球缘外亮像素 —— dev\limb_audit.ps1 <png> -Dist 78 数一遍剪影之外的亮点。
    //       为什么这条偏要放在**像素侧**、而不是像早先那样在引擎里枚举: HUD 标签挂的是
    //       CanvasRenderer, 而它不是 Renderer 的子类 —— FindObjectsByType<Renderer> 根本枚举不到,
    //       引擎内数得再准也整整漏掉一类(早期那版诊断就漏了)。像素不会漏。
    //       (-Dist 必须与那张图的取景一致: 整颗地球 = 78; 推近到 28 时剪影半径已 ~707px、
    //        超出屏幕半高, 根本不可能露出球缘外的东西, 脚本会自报"判定无效"而不是假报清白。)
    //
    //   为什么要有 `load` 这一轮: "进港时把有持仓的货排到最上面"这件事**只在换港那一刻发生**,
    //   而一轮进程里从头到尾停在同一港, 触发不到。可 quit→重开→读档正是玩家自己会做的事,
    //   走的就是这条真实路径 —— 所以这里不搞任何 QA 专用的"强制重排"后门, 而是老老实实起第二次进程。
    // =============================================================
    public sealed class SeaShot : MonoBehaviour
    {
        enum Act
        {
            Globe,            // 整颗地球取景, 浮层收起
            Home,             // 回本港局部取景, 浮层收起(球/地名标签/旗/船都在这张里)
            MarketAfterBuy,   // 买了货但**没换港** → 行情表该纹丝不动(钉住)
            HomeAfterLoad,    // 读档之后再来一张浮层收起的: 验标签与旗在球上摆得对不对
            MarketAfterLoad,  // 读档进港 → 手里的货该出现在最上面几行
            Scrambled,        // 把镜头转乱(换经度 + 滚转): 给"右键摆正"造一个可证伪的起点
            Aligned,          // 右键摆正之后: 应当北上南下、且舰队落在屏幕正中
            About,            // 「关于」弹层: 版本号与作者
        }

        struct Shot
        {
            public string Name;
            public Act What;
        }

        // 第一轮: 先看球(几何/绕序/球缘/黑雾边界全在 01 里), 再看局部, 验"钉住",
        //         最后三张是右键摆正(转乱 → 摆正)与「关于」。
        static readonly Shot[] PlanNew =
        {
            new Shot { Name = "01_开局整颗地球_HUD收起", What = Act.Globe },
            new Shot { Name = "02_回本港局部_HUD收起",   What = Act.Home  },
            new Shot { Name = "03_买货未换港_行情应钉住", What = Act.MarketAfterBuy },
            new Shot { Name = "06_右键前_镜头被转乱",     What = Act.Scrambled },
            new Shot { Name = "07_右键后_北上南下舰队居中", What = Act.Aligned },
            new Shot { Name = "08_关于面板",             What = Act.About },
        };

        // 第二轮: 读档 = 一次真实的"进港", 排序在这里生效。
        static readonly Shot[] PlanLoad =
        {
            new Shot { Name = "04_读档后局部_浮层收起",   What = Act.HomeAfterLoad   },
            new Shot { Name = "05_读档进港_持仓应置顶", What = Act.MarketAfterLoad },
        };

        const float Warmup = 3.0f;   // 开局头几帧还在建网格/传纹理, 立刻截会截到半成品
        const float Settle = 0.8f;   // 摆好机位到按下快门之间的等待: 波浪/雾/UI 版面都稳下来(一张图占两步, 所以每张要等两次)
        const float AfterLast = 0.6f; // 最后一张之后再多留几帧 —— CaptureScreenshot 是**帧末**才写的

        Shot[] _plan = PlanNew;   // 读档轮会换成 PlanLoad(不是 readonly)
        SeaPlay _play;
        SeaHud _hud;
        string _dir;
        bool _load;
        int _step = -1;      // -1 = 还没开局
        float _t;

        // 启动参数里有没有 -seaShot(SeaPlay.Awake 用它决定挂不挂这个组件)
        public static bool Requested()
        {
            var a = Environment.GetCommandLineArgs();
            return Array.IndexOf(a, "-seaShot") >= 0;
        }

        void Awake()
        {
            var args = Environment.GetCommandLineArgs();
            int k = Array.IndexOf(args, "-seaShot");
            if (k < 0 || k + 1 >= args.Length)
            {
                Debug.LogError("[SEA] -seaShot 后面没跟输出目录, 截图闸门不工作。");
                enabled = false;
                return;
            }
            // 取绝对路径: 相对路径是相对**进程当前目录**的, 而双击 exe / Start-Process 起的进程
            //   当前目录未必是 exe 所在处 —— 图会悄悄落到别的地方, 找半天。
            try
            {
                _dir = Path.GetFullPath(args[k + 1]);
                Directory.CreateDirectory(_dir);
            }
            catch (Exception e)
            {
                Debug.LogError("[SEA] 截图目录用不了: " + e.Message);
                enabled = false;
                return;
            }
            _load = k + 2 < args.Length && string.Equals(args[k + 2], "load", StringComparison.OrdinalIgnoreCase);
            if (_load) _plan = PlanLoad;

            _play = GetComponent<SeaPlay>();
            // HUD 未必和 SeaPlay 同一个物体(建场景那套是分开挂的), 所以按类型找而不是 GetComponent。
            _hud = UnityEngine.Object.FindFirstObjectByType<SeaHud>();
            AudioListener.volume = 0f;   // 自动跑图不该在半夜放出主题曲
            Debug.Log("[SEA] 截图闸门就绪 → " + _dir + (_load ? " (读档轮)" : " (开局轮)"));
        }

        // 步进表: **偶数步摆机位, 奇数步截图**, 一张图占两步。
        //   为什么非得分开两步: CaptureScreenshot 是"帧末"才写的, 而 Update 跑在渲染**之前** ——
        //   同一帧里先 Shoot 再把机位/浮层换成下一步的, 写出来的就是**下一步**的画面。
        //   (上一版把 Frame(i+1) 跟在 Shoot(i) 后面, 结果 02 里出现了 03 的行情表, 两张图一模一样。)
        void Update()
        {
            if (_play == null) { enabled = false; return; }
            _t += Time.unscaledDeltaTime;

            if (_step < 0)                                  // 阶段一: 等开局就绪
            {
                if (_t < Warmup) return;
                if (_load) _play.ContinueVoyage();          // 读档 → 这本身就是一次"进港"
                else _play.StartNewVoyage();                // 越过启动首页, 直接进游戏
                _step = 0; _t = 0f; Frame(0);
                return;
            }

            int total = _plan.Length * 2;
            if (_step < total)                              // 阶段二: 摆机位 → 等稳 → 截图
            {
                // 要"浮层收起"的机位得**每帧**收一次, 不能只收一次:
                //   读档那一轮, 到港自动弹行情表发生在我们收完浮层的**下一帧**, 收一次就会被它再顶开。
                int cur = _step >> 1;
                if (PanelsClosed(_plan[cur].What)) ClosePanels();
                if (_t < Settle) return;
                _t = 0f;
                if ((_step & 1) == 1) Shoot(_plan[cur].Name);
                else Frame(cur);
                _step++;
                return;
            }

            if (_t >= AfterLast)                            // 阶段三: 等最后一张落盘, 收工
            {
                Debug.Log("[SEA] 截图完毕, 退出。");
                Application.Quit(0);
            }
        }

        static bool PanelsClosed(Act a)
        {
            return a == Act.Globe || a == Act.Home || a == Act.HomeAfterLoad
                || a == Act.Scrambled || a == Act.Aligned || a == Act.About;
        }

        void ClosePanels()
        {
            if (_hud != null) _hud.QaClosePanels();
        }

        void Frame(int i)
        {
            switch (_plan[i].What)
            {
                case Act.Globe:
                    _play.ViewWorld();                       // 整颗地球
                    break;

                case Act.Home:
                case Act.HomeAfterLoad:
                    _play.ViewHome();                        // 回本港 / 就近
                    break;

                case Act.MarketAfterBuy:
                    // 买**列表末尾**两件货各 10 件, 但**不换港**。
                    //   排序按设计只在进港那一刻做一次, 所以这两件此刻应当仍在列表末尾(截不到),
                    //   而列表**顶上几行应当原样不动、持有列仍是"—"** —— 那正是"钉住"的证据。
                    BuyTailTwo();
                    // 存档: 第二轮(load)就指望它。存完**不换港**, 所以此刻列表还是没排过的那份。
                    if (!_play.SaveGame()) Debug.LogWarning("[SEA] 存档失败 —— 第二轮读档轮会没有档可读。");
                    if (_hud != null) _hud.QaOpenMarket();
                    _play.ViewHome();
                    break;

                case Act.MarketAfterLoad:
                    // 读档已经触发过一次真实的"进港排序"。手里的货必须挪到最上面两行。
                    //   (不自己开行情表: 到港本来就会自动弹开, 靠自动的才叫走真路径。)
                    if (_hud != null) _hud.QaOpenMarket();
                    _play.ViewHome();
                    break;

                case Act.Scrambled:
                    // 先回本港(定住视距与远近), 再**故意转乱** —— 这张是"摆正前"的对照底片。
                    _play.ViewHome();
                    _play.QaScrambleCam();
                    break;

                case Act.Aligned:
                    // 摆正过渡 0.55s, 而一张图要等 Settle(0.8s) → 按快门时早就转到位了。
                    _play.QaAlignNorthUp();
                    break;

                case Act.About:
                    _play.ViewHome();
                    SeaAbout.Show();
                    break;
            }
        }

        // 买列表**末尾**两件货。挑末尾是因为它是排序前离"最上面"最远的两行 ——
        //   排序一旦生效, 它们必须整体挪到前两行, 截图上一眼可判; 买本来就靠前的货则判不出来。
        void BuyTailTwo()
        {
            var goods = _play.world != null ? _play.world.Goods : null;
            if (goods == null || goods.Count < 2) { Debug.LogWarning("[SEA] 货表为空, 这一张验证不了排序。"); return; }
            for (int k = goods.Count - 1; k >= goods.Count - 2; k--)
            {
                var g = goods[k];
                long before = _play.GoodInHold(g.Id);
                _play.BuyGood(g, 10);
                Debug.Log("[SEA] 买入末位货 " + g.Name + " ×10: 持有 " + before + " → " + _play.GoodInHold(g.Id));
            }
        }

        void Shoot(string name)
        {
            LogFleetCentering(name);
            string p = Path.Combine(_dir, name + ".png");
            ScreenCapture.CaptureScreenshot(p);
            Debug.Log("[SEA] 截图: " + Path.GetFullPath(p));
        }

        // 舰队真落在画面正中了吗? 右键「摆正」的验收标准就是这一条, 而它最好用**像素**说话。
        //   SeaPlay 那边的推理是"焦点必落在视轴正中, 而摆正过渡的终点把焦点设成舰队所在球面点"——
        //   推理会错, 像素不会。所以按快门前把舰队投影到屏幕上量一次。
        //   每张图都量(不只摆正那张): 别的机位的偏差是另一个数, 对比着看才能说明"居中"是摆正换来的。
        void LogFleetCentering(string shot)
        {
            var cam = Camera.main;
            if (cam == null || _play == null) return;
            Vector3 sp = _play.QaFleetWorldPos();
            if (sp.sqrMagnitude < 1f) return;              // 原点埋在球心里, 舰队不可能在那儿 → 该值等于"没船"
            Vector3 v = cam.WorldToScreenPoint(sp);
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            float dx = v.x - cx, dy = v.y - cy;
            Debug.Log(string.Format(
                "[SEA] 舰队居中[{0}] 舰队屏坐标=({1:F0},{2:F0}) 画面中心=({3:F0},{4:F0}) 偏差={5:F1}px"
                + " (横{6:+0;-0} 纵{7:+0;-0}) 分辨率={8}×{9}{10}",
                shot, v.x, v.y, cx, cy, Mathf.Sqrt(dx * dx + dy * dy), dx, dy,
                Screen.width, Screen.height, v.z > 0f ? "" : " ⚠在相机身后"));
        }
    }
}
