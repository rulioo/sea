using System;
using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;

namespace Sea
{
    // =============================================================
    // SEA · 编辑器工具
    //   菜单 SEA ▸ 校验数据…   : 数据/世界自检(同 SimCheck 思路, 编辑器内快速跑)
    //   菜单 SEA ▸ 搭建主场景… : 生成 Assets/Scenes/Main.unity(相机+光+SeaPlay+SeaHud)
    // =============================================================
    public static class SeaMenu
    {
        const string ScenePath = "Assets/Scenes/Main.unity";

        [MenuItem("SEA/校验数据 (数据+世界+冒烟)", false, 10)]
        public static void ValidateAll()
        {
            var ok = SeaBootstrap.TryLoad(15500712, out var w, out var eng, out var err);
            if (!ok) { EditorUtility.DisplayDialog("SEA 校验", "数据装载失败:\n" + err, "确定"); return; }

            try
            {
                int goods = w.Goods.Count, ports = w.Ports.Count,
                    areas = w.Areas.Count, events = w.Events.Count;

                var issues = new System.Collections.Generic.List<string>();
                if (goods == 0) issues.Add("无商品");
                if (ports == 0) issues.Add("无港口");
                if (areas == 0) issues.Add("无航区");
                foreach (var g in w.Goods)
                {
                    bool anyProducer = false;
                    foreach (var p in w.Ports)
                        if (Array.IndexOf(p.Specialties, g.Id) >= 0) { anyProducer = true; break; }
                    if (!anyProducer) issues.Add("商品无人生产: " + g.Id);
                }
                foreach (var p in w.Ports)
                    if (p.Specialties.Length == 0 && p.Imports.Length == 0)
                    { issues.Add("港口无任何贸易属性: " + p.Id); }

                // 舰队冒烟(M1): Ships/Crews/FleetTuning 装载 + 标准卡拉维尔满员 45 日给养 → 续航 45
                string enduranceLine = "- 舰队冒烟: 未执行";
                var weightByGood = new System.Collections.Generic.Dictionary<string, int>();
                foreach (var g in w.Goods) weightByGood[g.Id] = g.Weight;
                if (SeaFleetLoader.TryLoad(id => weightByGood.TryGetValue(id, out var wv) ? wv : 1,
                        out var cat, out var fTun, out var fErr))
                {
                    int ships = cat.ShipOrder.Count, crews = cat.CrewOrder.Count;
                    float rate = fTun.Provision["food"].PerCrewPerDay; // 1.0
                    var demo = FleetBuilder.Start(cat, "m2_caravel", 80, 100000,
                        new ProvisionStock { Food = 80f * rate * 45f, Water = 100000f, Tea = 100000f });
                    enduranceLine = $"- 舰队冒烟: {ships} 舰 / {crews} 船员目录; 卡拉维尔 45 日给养 → 续航 {FleetOps.EnduranceDays(demo)} 日";
                    if (FleetOps.EnduranceDays(demo) != 45) issues.Add("舰队冒烟续航 ≠ 45");
                }
                else issues.Add("舰队数据装载失败: " + fErr);

                // 冒烟: 推进整年 + 手工一买一卖
                eng.AdvanceDays(366);
                Port first = w.Ports[0];
                Good g0 = first.Specialties.Length > 0 ? w.GoodById[first.Specialties[0]] : null;
                int buyU = 0, sellU = 0;
                if (g0 != null)
                {
                    buyU = eng.Buy(first, g0, 5, 50000).Units;
                    sellU = eng.Sell(first, g0, 2).Units;
                }

                string msg = $"- 商品 {goods} / 港口 {ports} / 航区 {areas} / 事件 {events}\n"
                           + "- 推进 366 日, 无异常\n"
                           + $"- 冒烟交易: 买入 {buyU} 件, 卖出 {sellU} 件\n"
                           + enduranceLine + "\n"
                           + (issues.Count > 0 ? "- 问题:\n  · " + string.Join("\n  · ", issues) : "- 无结构性问题 ✓");
                if (issues.Count > 0)
                    Debug.LogWarning("[SEA] 校验发现潜在问题:\n" + msg);
                else
                    Debug.Log("[SEA] 校验通过:\n" + msg);
                EditorUtility.DisplayDialog("SEA 校验", msg, "确定");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("SEA 校验", "校验中抛出异常: " + ex.Message, "确定");
            }
        }

        [MenuItem("SEA/搭建主场景 Main.unity", false, 30)]
        public static void BuildMainScene()
        {
            if (!System.IO.Directory.Exists("Assets/Scenes"))
                System.IO.Directory.CreateDirectory("Assets/Scenes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 相机(SeaGame.Start 会兜底, 这里也放一个便于编辑视口)
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.transform.position = new Vector3(0f, 120f, -140f);
            camGo.transform.rotation = Quaternion.Euler(40f, 0f, 0f);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.08f, 0.16f);
            cam.farClipPlane = 1000f;
            camGo.AddComponent<AudioListener>();

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            lightGo.transform.rotation = Quaternion.Euler(55f, -35f, 0f);

            var seaGo = new GameObject("SEA · 旗舰航线");
            seaGo.AddComponent<SeaPlay>();
            seaGo.AddComponent<SeaHud>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            Selection.activeGameObject = seaGo;
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("SEA", "已生成并保存:\n" + ScenePath + "\n点击 Play 即可游玩(相机+光+SeaPlay/SeaHud 已就位)。", "确定");
        }

        // 打包 Windows 独立版(真正可双击运行的 exe, 不依赖 Unity 编辑器)。
        // 批处理调用: Unity -batchmode -nographics -quit -projectPath <proj>
        //             -executeMethod Sea.SeaMenu.BuildStandalone -logFile build.log
        // 产物: Build/SEA.exe + Build/SEA_Data/(含 Resources 里的全部 JSON 数据)。
        // 只靠运行时 Shader.Find 用的着色器, 不引用的话打包会被裁掉 → 提前登记进 Always Included。
        // 内建 Standard/Sprites 与项目自写 SEA/Water、SEA/Land 都在列。
        static void EnsureAlwaysIncludedShaders()
        {
            var assets = UnityEditor.AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (assets == null || assets.Length == 0) return;
            var so = new UnityEditor.SerializedObject(assets[0]);
            var prop = so.FindProperty("m_AlwaysIncludedShaders");
            if (prop == null) return;
            var want = new[]
            {
                Shader.Find("Standard"), Shader.Find("Sprites/Default"),
                Shader.Find("SEA/Water"), Shader.Find("SEA/Land"),
            };
            bool changed = false;
            foreach (var w in want)
            {
                if (w == null) continue;
                bool has = false;
                for (int j = 0; j < prop.arraySize; j++)
                    if (prop.GetArrayElementAtIndex(j).objectReferenceValue == w) { has = true; break; }
                if (has) continue;
                prop.InsertArrayElementAtIndex(prop.arraySize);
                prop.GetArrayElementAtIndex(prop.arraySize - 1).objectReferenceValue = w;
                changed = true;
            }
            if (changed)
            {
                so.ApplyModifiedProperties();
                UnityEditor.AssetDatabase.SaveAssets();
                Debug.Log("[SEA] 已登记 Always Included Shaders");
            }
        }

        [MenuItem("SEA/构建独立版 (Windows x64)", false, 45)]
        public static void BuildStandalone()
        {
            EnsureAlwaysIncludedShaders();
            var opts = new UnityEditor.BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Main.unity" },
                locationPathName = "Build/SEA.exe",
                target = UnityEditor.BuildTarget.StandaloneWindows64,
                options = UnityEditor.BuildOptions.None,
            };
            var report = UnityEditor.BuildPipeline.BuildPlayer(opts);
            var r = report.summary.result;
            bool ok = r == UnityEditor.Build.Reporting.BuildResult.Succeeded;
            Debug.Log("[SEA] 独立版构建 " + (ok ? "成功" : "失败") + " @ " + opts.locationPathName + "  (" + r + ", " + report.summary.totalSize / 1048576 + " MB)");
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("SEA", ok
                    ? "构建成功:\n" + System.IO.Path.GetFullPath(opts.locationPathName)
                    : "构建失败: " + r + "\n详见 Console / 构建日志。", "确定");
            if (!ok) EditorApplication.Exit(1);
        }

        // 供「一键试玩」cmd 在启动时调用: 打开主场景并自动进入 Play。
        // 退出 Play 后回到编辑态, Ctrl+P 可再次试玩; AutoBoot 保证任意场景都能自建整套试玩。
        [MenuItem("SEA/直接进入试玩 (自动 Play)", false, 40)]
        public static void OpenSceneAndPlay()
        {
            try
            {
                if (System.IO.File.Exists(ScenePath))
                    EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SEA] 打开主场景失败, 将用当前场景(SeaLauncher 仍会自动搭建): " + ex.Message);
            }

            if (Application.isBatchMode) return;   // 批处理(编译冒烟)不自动进 Play
            EditorApplication.delayCall += () =>
            {
                if (!EditorApplication.isPlaying)
                {
                    Debug.Log("[SEA] 已自动进入 Play 试玩; 退出 Play 后回到编辑态, 可 Ctrl+P 再试。");
                    EditorApplication.isPlaying = true;
                }
            };
        }
    }
}
