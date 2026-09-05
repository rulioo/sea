using System;
using System.Collections.Generic;

namespace Sea
{
    // =============================================================
    // 世界内容模型(引擎内对象, 由 SeaData 契约构建)
    // =============================================================

    public enum NeedClass { Necessity, Material, Cloth, Indulgence, Luxury }

    public enum Climate { Cold, Temp, Tropic, TempSouth }

    public sealed class Good
    {
        public string Id, Name, En, Cat, Desc;
        public NeedClass Need;
        public int BasePrice, Weight;

        public static NeedClass ParseNeed(string s)
        {
            switch (s)
            {
                case "necessity": return NeedClass.Necessity;
                case "material": return NeedClass.Material;
                case "cloth": return NeedClass.Cloth;
                case "indulgence": return NeedClass.Indulgence;
                default: return NeedClass.Luxury;
            }
        }

        public bool IsPremium // 季节敏感品: 嗜好 + 珍品
            => Need == NeedClass.Indulgence || Need == NeedClass.Luxury;
    }

    public sealed class SeaArea
    {
        public string Id, Name, En, Tier, Desc;
    }

    public sealed class Port
    {
        public string Id, Name, En, Desc;
        public SeaArea Area;
        public float Lat, Lon;
        public int Size;               // 1..3
        public Climate Climate;
        public string[] Specialties;   // 商品 id
        public string[] Imports;       // 商品 id
        public bool Blocked;           // 大事件封锁期间 = true
    }

    public enum CellKind { Producer, Importer, Neutral }

    // 单个 (港口×商品) 的市场格子
    public sealed class MarketCell
    {
        public Port Port;
        public Good Good;
        public bool Produced;      // 本港特产
        public bool Needed;        // 本地百姓需求
        public float EqBase;       // 由角色决定的长期均衡倍率
        public float Eq;           // 当前均衡倍率(含随机漂移)
        public float R;            // 当下挂牌倍率
        public float Stock;        // 可买库存
        public float Cap;          // 库存上限
        public float RestockPerDay;

        public CellKind Kind => Produced ? CellKind.Producer : (Needed ? CellKind.Importer : CellKind.Neutral);
    }

    public struct TradeResult
    {
        public int Units;   // 实际成交件数
        public int Gold;    // 买方付出的金币 或 卖方得到的金币
    }

    public sealed class GameEvent
    {
        public EventD D;
        public bool IsActive(int day) => ScheduleActive(D.schedule, day);

        public static bool ScheduleActive(EventScheduleD s, int day)
        {
            if (s == null) return false;
            if (day < 0) return false;
            switch (s.mode)
            {
                case "once":
                    return day >= s.dayOffset && day < s.dayOffset + Math.Max(1, s.duration);
                case "yearly":
                {
                    int dur = Math.Max(1, s.duration);
                    if (dur >= 360) return true;
                    int offset = (Math.Max(1, s.monthStart) - 1) * 30; // 月→一年内起始日(每月30日)
                    int el = day % 360;
                    int startThisYear = day - el + offset;
                    int start = (el >= offset) ? startThisYear : startThisYear - 360;
                    return day >= start && day < start + dur;
                }
                case "period":
                {
                    int period = Math.Max(1, s.periodDays);
                    int dur = Math.Max(1, s.duration);
                    if (day < s.firstDay) return false;
                    int rel = day - s.firstDay;
                    int k = rel / period;
                    int off = rel - k * period;
                    return off < dur;
                }
                default: return false;
            }
        }

        // 该事件是否命中 (port, good) 的市场格子
        public bool Matches(Port p, Good g, bool produced, bool needed)
        {
            var sc = D.scope;
            bool regionOk;
            if (sc.ports != null && sc.ports.Length > 0)
                regionOk = ArrHas(sc.ports, p.Id);
            else if (sc.areas != null && sc.areas.Length > 0)
                regionOk = ArrHas(sc.areas, p.Area.Id);
            else
                regionOk = true;

            if (!regionOk) return false;

            bool noGoodFilter = (sc.goods == null || sc.goods.Length == 0)
                             && (sc.needClasses == null || sc.needClasses.Length == 0);
            bool goodOk = noGoodFilter
                || ArrHas(sc.goods, g.Id)
                || ArrHas(sc.needClasses, GoodNameOfNeed(g.Need));
            if (!goodOk) return false;

            string role = D.effect.role ?? "";
            if (role.Length > 0)
            {
                if (role == "producer" && !produced) return false;
                if (role == "importer" && !needed) return false;
            }
            return true;
        }

        static string GoodNameOfNeed(NeedClass n)
        {
            switch (n)
            {
                case NeedClass.Necessity: return "necessity";
                case NeedClass.Material: return "material";
                case NeedClass.Cloth: return "cloth";
                case NeedClass.Indulgence: return "indulgence";
                default: return "luxury";
            }
        }

        static bool ArrHas(string[] a, string v)
            => a != null && Array.IndexOf(a, v) >= 0;
    }

    // =============================================================
    // 世界：装载数据后的总状态
    // =============================================================

    public sealed class World
    {
        public List<Good> Goods = new List<Good>();
        public Dictionary<string, Good> GoodById = new Dictionary<string, Good>();
        public Dictionary<string, SeaArea> Areas = new Dictionary<string, SeaArea>();
        public List<Port> Ports = new List<Port>();
        public Dictionary<string, Port> PortById = new Dictionary<string, Port>();
        public List<GameEvent> Events = new List<GameEvent>();

        // port.Id -> good 数组(与 Goods 同序)
        public Dictionary<string, MarketCell[]> Market = new Dictionary<string, MarketCell[]>();

        public int GoodIndex(string id) => GoodById.ContainsKey(id) ? Goods.IndexOf(GoodById[id]) : -1;

        public Port FindPort(string id) => PortById.TryGetValue(id, out var p) ? p : null;
    }

    // =============================================================
    // 经济引擎：tick / 交易 / 事件 / 价格查询
    // =============================================================

    public sealed class SimEngine
    {
        public const int DaysPerYear = 360;   // 游戏历: 12 月 × 30 日
        public const int StartYear = 1550;

        public World W;
        public MarketTuning T;
        public int Day { get; private set; } = 0;

        // 读档: 把游戏历拨回存档那天(市场格逐格恢复由调用方做, 这里只回日历与事件时钟)。
        public void SetDay(int day) { Day = day < 0 ? 0 : day; }

        readonly Random _rng;

        public SimEngine(World w, MarketTuning t, int seed)
        {
            W = w;
            T = t;
            _rng = new Random(seed);
            InitMarket();
        }

        // 生成所有 (港×货) 市场格子: 角色 eq、库存上限与回补速率
        void InitMarket()
        {
            foreach (var p in W.Ports)
            {
                var cells = new MarketCell[W.Goods.Count];
                for (int i = 0; i < cells.Length; i++)
                {
                    var g = W.Goods[i];
                    bool prod = Array.IndexOf(p.Specialties, g.Id) >= 0;
                    bool need = Array.IndexOf(p.Imports, g.Id) >= 0;
                    var cell = new MarketCell
                    {
                        Port = p,
                        Good = g,
                        Produced = prod,
                        Needed = need
                    };
                    cell.EqBase = T.EqForRole(cell.Kind, g.Need, prod && need);
                    cell.Eq = cell.EqBase;
                    cell.R = cell.EqBase;
                    cell.Cap = prod
                        ? T.ProducerCapBySize[Math.Max(0, Math.Min(2, p.Size - 1))]
                        : T.OtherCapBySize[Math.Max(0, Math.Min(2, p.Size - 1))];
                    cell.RestockPerDay = prod ? T.ProducerRestockPerDay : T.OtherRestockPerDay;
                    cell.Stock = cell.Cap * (prod ? 0.8f : 0.6f);
                    cells[i] = cell;
                }
                W.Market[p.Id] = cells;
            }
        }

        // ---------- 时间 ----------
        public int Year => StartYear + Day / DaysPerYear;
        public int Month => (Day % DaysPerYear) / 30 + 1;      // 1..12
        public int DayOfMonth => Day % 30 + 1;                 // 1..30

        public void AdvanceDays(int n)
        {
            for (int i = 0; i < n; i++) TickOneDay();
        }

        void TickOneDay()
        {
            int day = Day;
            // 1) 事件活动表: block 港 / (事件匹配在格子循环里即时判定更省)
            var blockedPorts = new HashSet<string>();
            foreach (var e in W.Events)
            {
                if (e.D.effect.block && e.IsActive(day))
                {
                    var sc = e.D.scope;
                    if (sc.ports != null && sc.ports.Length > 0)
                        foreach (var pid in sc.ports) blockedPorts.Add(pid);
                    else
                        foreach (var p in W.Ports)
                            if (sc.areas == null || sc.areas.Length == 0 || Array.IndexOf(sc.areas, p.Area.Id) >= 0)
                                blockedPorts.Add(p.Id);
                }
            }
            int mi = (day % DaysPerYear) / 30;

            foreach (var p in W.Ports)
            {
                p.Blocked = blockedPorts.Contains(p.Id);
                var cells = W.Market[p.Id];
                float[] season = SeasonArray(p.Climate);
                for (int gi = 0; gi < cells.Length; gi++)
                {
                    var c = cells[gi];
                    UpdateCell(c, mi, season);
                }
            }
            Day = day + 1;
        }

        void UpdateCell(MarketCell c, int mi, float[] season)
        {
            // 均衡漂移: Eq 向 EqBase 均值回复 + 白噪声(行情偶然好/坏)
            c.Eq += (c.EqBase - c.Eq) * T.EqMeanPull + NextGauss() * T.EqDriftSigma;

            // 事件乘数(相乘)
            float eventMul = 1f;
            float refillMul = 1f;
            foreach (var e in W.Events)
            {
                if (!e.IsActive(Day)) continue;
                if (!e.Matches(c.Port, c.Good, c.Produced, c.Needed)) continue;
                eventMul *= e.D.effect.eqMul > 0 ? e.D.effect.eqMul : 1f;
                if (e.D.effect.refillMul > 0) refillMul *= e.D.effect.refillMul;
            }

            // 季节: 仅对 嗜好/珍品 生效
            float seasonMul = c.Good.IsPremium ? season[mi] : 1f;

            float eqEff = c.Eq * seasonMul * eventMul;

            // 价格倍率向有效目标回归
            c.R += (eqEff - c.R) * T.RatioRegressionPerDay;
            c.R = Clamp(c.R, T.PriceRatioMin, T.PriceRatioMax);

            // 库存回补(量: 特产快、其他慢)
            float rate = c.RestockPerDay * refillMul;
            if (rate > 0) c.Stock += (c.Cap - c.Stock) * Math.Min(1f, rate);
            c.Stock = Clamp(c.Stock, 0f, c.Cap);
        }

        // ---------- 价格查询 ----------
        public float CurrentRatio(Port p, Good g) => GetCell(p, g).R;
        public float EqTarget(Port p, Good g) => GetCell(p, g).Eq;

        public int AskPrice(Port p, Good g)  // 买入价
            => (int)Math.Round(g.BasePrice * GetCell(p, g).R);
        public int BidPrice(Port p, Good g)  // 卖出价
            => (int)Math.Round(g.BasePrice * GetCell(p, g).R * T.SellSpreadFactor);

        public MarketCell GetCell(Port p, Good g) => W.Market[p.Id][W.GoodIndex(g.Id)];

        // 该港现价 vs 基准价的比例(用于行情色标/排序)
        public float RatioOf(Port p, Good g) => GetCell(p, g).R;

        // ---------- 交易 ----------
        public int BuyStock(Port p, Good g) => (int)Math.Floor(GetCell(p, g).Stock);

        // 逐单位成交, 每次买入推动 r 上行(越买越贵)。受库存与现金双重约束。
        public TradeResult Buy(Port p, Good g, int qty, int availableCash)
        {
            var res = new TradeResult();
            var c = GetCell(p, g);
            if (p.Blocked || qty <= 0 || c.Stock <= 0 || availableCash <= 0) return res;
            int can = Math.Min(qty, (int)Math.Floor(c.Stock));
            for (int i = 0; i < can; i++)
            {
                int price = AskPrice(p, g);
                if (res.Gold + price > availableCash) break;
                res.Gold += price;
                res.Units++;
                c.Stock -= 1f;
                c.R = Clamp(c.R + T.BuyRatioPushPerUnit, T.PriceRatioMin, T.PriceRatioMax);
            }
            return res;
        }

        // 逐单位卖出(百姓收购不限量), 每次卖出压低 r(倾销自砸价)。
        public TradeResult Sell(Port p, Good g, int qty)
        {
            var res = new TradeResult { Units = 0 };
            var c = GetCell(p, g);
            if (p.Blocked || qty <= 0) return res;
            for (int i = 0; i < qty; i++)
            {
                res.Gold += BidPrice(p, g);
                res.Units++;
                c.Stock = Clamp(c.Stock + 1f, 0f, c.Cap);
                c.R = Clamp(c.R + T.SellRatioPushPerUnit, T.PriceRatioMin, T.PriceRatioMax);
            }
            return res;
        }

        // ---------- 事件活动状态(供 UI/测试) ----------
        public List<GameEvent> ActiveEvents() => W.Events.FindAll(e => e.IsActive(Day));

        // ---------- 工具 ----------
        float[] SeasonArray(Climate cl) => T.Season(cl);

        float NextGauss()
        {
            // Box-Muller
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = _rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    // =============================================================
    // 调参解析(缺省兜底)
    // =============================================================

    public sealed class MarketTuning
    {
        public float SellSpreadFactor = 0.94f;
        public float RatioRegressionPerDay = 0.10f;
        public float PriceRatioMin = 0.25f;
        public float PriceRatioMax = 3.0f;
        public float SpecialtyEq = 0.60f;
        public float SpecialtyNeededEq = 0.85f;
        public float NeedNecessityEq = 1.40f;
        public float NeedClothEq = 1.45f;
        public float NeedMaterialEq = 1.30f;
        public float NeedIndulgenceEq = 1.65f;
        public float NeedLuxuryEq = 1.72f;
        public float NeutralEq = 0.98f;
        public float BuyRatioPushPerUnit = 0.006f;
        public float SellRatioPushPerUnit = -0.008f;
        public float EqDriftSigma = 0.005f;
        public float EqMeanPull = 0.02f;
        public float[] ProducerCapBySize = { 36, 60, 90 }; // size1..3
        public float[] OtherCapBySize = { 8, 14, 24 };
        public float ProducerRestockPerDay = 0.35f;
        public float OtherRestockPerDay = 0.06f;
        // 下标 0..11 = 1..12 月, 针对 need∈{indulgence,luxury}
        public float[] SeasonCold = new float[12];
        public float[] SeasonTemp = new float[12];
        public float[] SeasonTropic = new float[12];
        public float[] SeasonTempS = new float[12];

        static readonly float[] Ones12 = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };

        public float[] Season(Climate cl)
        {
            switch (cl)
            {
                case Climate.Cold: return SeasonCold;
                case Climate.Tropic: return SeasonTropic;
                case Climate.TempSouth: return SeasonTempS;
                default: return SeasonTemp;
            }
        }

        // 该港该货当天的"角色 eq"
        public float EqForRole(CellKind kind, NeedClass need, bool alsoNeededLocally)
        {
            if (kind == CellKind.Producer)
                return alsoNeededLocally ? SpecialtyNeededEq : SpecialtyEq;
            if (kind == CellKind.Importer)
            {
                switch (need)
                {
                    case NeedClass.Necessity: return NeedNecessityEq;
                    case NeedClass.Cloth: return NeedClothEq;
                    case NeedClass.Material: return NeedMaterialEq;
                    case NeedClass.Indulgence: return NeedIndulgenceEq;
                    default: return NeedLuxuryEq;
                }
            }
            return NeutralEq;
        }

        public static MarketTuning From(MarketTuningD d)
        {
            var t = new MarketTuning();
            if (d == null) return t;
            t.SellSpreadFactor = d.sellSpreadFactor > 0 ? d.sellSpreadFactor : t.SellSpreadFactor;
            t.RatioRegressionPerDay = d.ratioRegressionPerDay > 0 ? d.ratioRegressionPerDay : t.RatioRegressionPerDay;
            t.PriceRatioMin = d.priceRatioMin > 0 ? d.priceRatioMin : t.PriceRatioMin;
            t.PriceRatioMax = d.priceRatioMax > 0 ? d.priceRatioMax : t.PriceRatioMax;
            t.SpecialtyEq = d.specialtyEq > 0 ? d.specialtyEq : t.SpecialtyEq;
            t.SpecialtyNeededEq = d.specialtyNeededEq > 0 ? d.specialtyNeededEq : t.SpecialtyNeededEq;
            t.NeedNecessityEq = d.needNecessityEq > 0 ? d.needNecessityEq : t.NeedNecessityEq;
            t.NeedClothEq = d.needClothEq > 0 ? d.needClothEq : t.NeedClothEq;
            t.NeedMaterialEq = d.needMaterialEq > 0 ? d.needMaterialEq : t.NeedMaterialEq;
            t.NeedIndulgenceEq = d.needIndulgenceEq > 0 ? d.needIndulgenceEq : t.NeedIndulgenceEq;
            t.NeedLuxuryEq = d.needLuxuryEq > 0 ? d.needLuxuryEq : t.NeedLuxuryEq;
            t.NeutralEq = d.neutralEq > 0 ? d.neutralEq : t.NeutralEq;
            t.BuyRatioPushPerUnit = d.buyRatioPushPerUnit > 0 ? d.buyRatioPushPerUnit : t.BuyRatioPushPerUnit;
            t.SellRatioPushPerUnit = d.sellRatioPushPerUnit != 0 ? d.sellRatioPushPerUnit : t.SellRatioPushPerUnit;
            t.EqDriftSigma = d.eqDriftSigma >= 0 ? d.eqDriftSigma : t.EqDriftSigma;
            t.EqMeanPull = d.eqMeanPull > 0 ? d.eqMeanPull : t.EqMeanPull;

            if (d.stock != null)
            {
                if (d.stock.producerCapBySize != null && d.stock.producerCapBySize.Length == 3)
                    t.ProducerCapBySize = d.stock.producerCapBySize;
                if (d.stock.otherCapBySize != null && d.stock.otherCapBySize.Length == 3)
                    t.OtherCapBySize = d.stock.otherCapBySize;
                if (d.stock.producerRestockPerDay > 0) t.ProducerRestockPerDay = d.stock.producerRestockPerDay;
                if (d.stock.otherRestockPerDay > 0) t.OtherRestockPerDay = d.stock.otherRestockPerDay;
            }
            t.SeasonCold = Pick(d.seasonPremiumMul?.cold, Ones12);
            t.SeasonTemp = Pick(d.seasonPremiumMul?.temp, Ones12);
            t.SeasonTropic = Pick(d.seasonPremiumMul?.tropic, Ones12);
            t.SeasonTempS = Pick(d.seasonPremiumMul?.tempS, Ones12);
            return t;
        }

        static float[] Pick(float[] a, float[] def) => a != null && a.Length == 12 ? a : def;
    }

    // =============================================================
    // 装载: DTO → 引擎世界(纯计算, 不含 IO)
    // =============================================================

    public static class WorldBuilder
    {
        public static World Build(GoodD[] goods, AreaD[] areas, PortD[] ports, EventD[] events)
        {
            var w = new World();

            if (goods != null)
                foreach (var g in goods)
                {
                    if (w.GoodById.ContainsKey(g.id)) continue;
                    var gg = new Good
                    {
                        Id = g.id, Name = g.name, En = g.en, Cat = g.cat, Desc = g.desc,
                        Need = Good.ParseNeed(g.need),
                        BasePrice = g.basePrice, Weight = g.weight
                    };
                    w.Goods.Add(gg);
                    w.GoodById[gg.Id] = gg;
                }

            if (areas != null)
                foreach (var a in areas)
                    w.Areas[a.id] = new SeaArea { Id = a.id, Name = a.name, En = a.en, Tier = a.tier, Desc = a.desc };

            if (ports != null)
                foreach (var p in ports)
                {
                    var po = new Port
                    {
                        Id = p.id, Name = p.name, En = p.en, Desc = p.desc,
                        Lat = p.lat, Lon = p.lon, Size = p.size,
                        Specialties = p.specialties ?? new string[0],
                        Imports = p.imports ?? new string[0]
                    };
                    po.Area = w.Areas.TryGetValue(p.areaId, out var a) ? a : new SeaArea { Id = p.areaId };
                    po.Climate = ParseClimate(p.climate);
                    w.Ports.Add(po);
                    w.PortById[po.Id] = po;
                }

            if (events != null)
                foreach (var e in events)
                    w.Events.Add(new GameEvent { D = e });

            return w;
        }

        public static Climate ParseClimate(string s)
        {
            switch (s)
            {
                case "cold": return Climate.Cold;
                case "tropic": return Climate.Tropic;
                case "tempS": return Climate.TempSouth;
                default: return Climate.Temp;
            }
        }
    }
}
