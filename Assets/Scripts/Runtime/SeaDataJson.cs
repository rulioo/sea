using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sea
{
    // =============================================================
    // Unity 侧数据装载(JsonUtility)
    // 数据文件在 Assets/Resources/SeaData/*.json, 顶层都是数组;
    // JsonUtility 不能直接解析"顶层数组", 这里用 {"items": [...]} 包一层。
    // 与 Sea.Core 的 DTO 契约共用一套字段(camelCase, [Serializable])。
    // =============================================================

    [Serializable]
    public sealed class JsonWrap<T> { public T[] items; }

    public static class SeaJson
    {
        // 读 Resources/SeaData/<name>.json 顶层数组 → T[]
        public static T[] ReadArray<T>(string name)
        {
            var asset = Resources.Load<TextAsset>("SeaData/" + name);
            if (asset == null) return null;
            var wrap = JsonUtility.FromJson<JsonWrap<T>>("{\"items\":" + asset.text + "}");
            return wrap == null ? new T[0] : (wrap.items ?? new T[0]);
        }

        public static MarketTuningD ReadTuning()
        {
            var asset = Resources.Load<TextAsset>("SeaData/MarketTuning");
            if (asset == null) return null;
            return JsonUtility.FromJson<MarketTuningD>(asset.text);
        }

        public static FleetTuningD ReadFleetTuning()
        {
            var asset = Resources.Load<TextAsset>("SeaData/FleetTuning");
            if (asset == null) return null;
            return JsonUtility.FromJson<FleetTuningD>(asset.text);
        }

        // 缺失则返回 null(调用方给出中文错误)
        public static string[] MissingFiles()
        {
            var names = new[] { "Goods", "Areas", "Ports", "Events", "MarketTuning", "Ships", "Crews", "FleetTuning" };
            var missing = new List<string>();
            foreach (var n in names)
                if (Resources.Load<TextAsset>("SeaData/" + n) == null) missing.Add(n + ".json");
            return missing.ToArray();
        }
    }

    // M1 舰队装载: Ships/Crews/FleetTuning(DTO) → FleetCatalog。
    // weightOf: 货重映射(接 World.Goods 后给真实 weight; 未接则默认 1)。
    public static class SeaFleetLoader
    {
        public static bool TryLoad(Func<string, int> weightOf, out FleetCatalog catalog, out FleetTuning tuning, out string error)
        {
            catalog = null;
            tuning = null;
            error = "";

            try
            {
                var ships = SeaJson.ReadArray<ShipD>("Ships");
                var crews = SeaJson.ReadArray<CrewD>("Crews");
                var tuningD = SeaJson.ReadFleetTuning();
                if (ships == null || ships.Length == 0)
                {
                    error = "Ships.json 解析为空";
                    return false;
                }
                catalog = FleetCatalog.Build(ships, crews, tuningD, weightOf);
                tuning = catalog.Tuning;
                return true;
            }
            catch (Exception ex)
            {
                error = "舰队数据反序列化失败: " + ex.Message;
                return false;
            }
        }
    }

    // 一条龙: Resources → DTO → World + SimEngine。编辑器菜单/运行时共用。
    public static class SeaBootstrap
    {
        public static bool TryLoad(int seed, out World world, out SimEngine engine, out string error)
        {
            world = null;
            engine = null;
            error = "";

            var missing = SeaJson.MissingFiles();
            if (missing.Length > 0)
            {
                error = "缺少数据文件(需位于 Assets/Resources/SeaData/): " + string.Join(", ", missing);
                return false;
            }

            try
            {
                var goods = SeaJson.ReadArray<GoodD>("Goods");
                var areas = SeaJson.ReadArray<AreaD>("Areas");
                var ports = SeaJson.ReadArray<PortD>("Ports");
                var events = SeaJson.ReadArray<EventD>("Events");
                var tuningD = SeaJson.ReadTuning();

                if (goods == null || goods.Length == 0)
                {
                    error = "Goods.json 解析为空";
                    return false;
                }

                world = WorldBuilder.Build(goods, areas, ports, events);
                if (world.Ports.Count == 0) { error = "Ports.json 未产生任何港口"; world = null; return false; }

                engine = new SimEngine(world, MarketTuning.From(tuningD), seed);
                return true;
            }
            catch (Exception ex)
            {
                error = "数据反序列化失败: " + ex.Message;
                return false;
            }
        }
    }
}
