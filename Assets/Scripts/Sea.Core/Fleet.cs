using System;
using System.Collections.Generic;

namespace Sea
{
    // =============================================================
    // M1 舰队规则层(纯 System.*, 不引用 UnityEngine)
    // 契约: docs/FleetAndCrewDesign.md §10"已定取舍"
    //  1) 结薪=按进港   2) 海盗自动查表(IBattleAction 缝留手操 M3)
    //  3) 茶双用途(货物/补给两套粒度, 本阶段不转换)
    //  4) 数值全为占位, SimCheck 只查结构与量纲, 不查平衡
    // buff 白名单(含义/正负在此注释为契约, 引擎只认这些 kind):
    //  moraleCap(+士气上限) battleWin(+海战胜率加成)
    //  buyDiscount(-买入%) sellBonus(+卖出%) ransomCut(-赎金%)
    //  stormLossCut(-风暴货损) sightMul(+视野) fireMul(+火力%)
    //  scurvyLossMul(-坏血减员) treatCostCut(-就医费)
    //  refitCostCut(-改造费) speedAdd(+航速节) provisionCostCut(-给养采购%)
    //  foodUseMul(×食物日耗, 小于1省粮) monsoonSpeedAdd(季风+速)
    //  boardingPower(+接舷) freeIntel(+免费行情情报)
    //  ——部分(monsoonSpeedAdd/sightMul/stormLossCut 等)要等 M2 移动/接舷才消费,
    //    数据先占位聚合; 聚合对所有 kind 均为"同 kind 求和/乘积按语义":
    //    速率类(foodUseMul/speedAdd/monsoonSpeedAdd)相乘, 其余相加。
    // =============================================================

    public enum RefitKind { Capacity, Fire, Armor, Speed }

    // ---------- 目录条目: 船型 ----------

    public sealed class ShipModel
    {
        public string Id, Name, En, Type, Desc;
        public int Tier;              // 1..4
        public int Slots;             // 同时可装货种上限
        public int Capacity;          // 载货量(载重)上限
        public int CrewMin, CrewMax;
        public float Speed;           // 节(基态)
        public int Guns, Fire, Armor; // 口径数/火力/护甲(基态)
        public int Price;
        public int RefitSlots;        // 可改造槽位上限
        public string[] AreaUnlock;
        public int Year;

        public static ShipModel From(ShipD d)
        {
            return new ShipModel
            {
                Id = d.id, Name = d.name, En = d.en, Type = d.type, Desc = d.desc,
                Tier = d.tier, Slots = d.slots, Capacity = d.capacity,
                CrewMin = d.crewMin, CrewMax = d.crewMax, Speed = d.speed,
                Guns = d.guns, Fire = d.fire, Armor = d.armor, Price = d.price,
                RefitSlots = d.refitSlots, AreaUnlock = d.areaUnlock ?? new string[0], Year = d.year
            };
        }
    }

    // ---------- 状态: 单船 ----------

    // 单条船的一次改造结果(槽位内)
    public sealed class RefitApplied
    {
        public RefitKind Kind;
        public int Cost;
        public float EffectValue; // capacity/fire/armor=加成的百分比(0.175=17.5%), speed=加节
    }

    public sealed class ShipState
    {
        public ShipModel Model;
        public int CrewAboard;                       // ∈ [Model.CrewMin, Model.CrewMax]
        public readonly List<RefitApplied> Refits = new List<RefitApplied>();
        // 货账: goodId -> 件数(逐船); 同一船同时装的货种数 ≤ Model.Slots
        public readonly Dictionary<string, int> Cargo = new Dictionary<string, int>();
        public int Damage;                           // 0..Model.Armor*10 的伤损刻度(占位, M2 战斗用)

        public bool IsFlagship;                      // 队首 = 旗舰

        public int AppliedCount => Refits.Count;
        public bool CanRefit => AppliedCount < Model.RefitSlots;

        float CapPct, FirePct, ArmorPct;             // 累计加成(0.175=+17.5%)
        float SpeedAdd;                              // 累计加节

        public int EffCapacity => (int)Math.Round(Model.Capacity * (1f + CapPct));
        public int EffFire     => (int)Math.Round(Model.Fire * (1f + FirePct));
        public int EffArmor    => (int)Math.Round(Model.Armor * (1f + ArmorPct));
        public float EffSpeed  => Model.Speed + SpeedAdd;

        public void ApplyRefit(RefitApplied r)
        {
            switch (r.Kind)
            {
                case RefitKind.Capacity: CapPct += r.EffectValue; break;
                case RefitKind.Fire: FirePct += r.EffectValue; break;
                case RefitKind.Armor: ArmorPct += r.EffectValue; break;
                case RefitKind.Speed: SpeedAdd += r.EffectValue; break;
            }
            Refits.Add(r);
        }

        public int CargoUnitCount { get { int n = 0; foreach (var kv in Cargo) n += kv.Value; return n; } }
        public int CargoKinds => Cargo.Count;

        // 已占载重 = Σ 件数×单件重(重量映射由舰队级注入)
        public int OccupiedWeight(Func<string, int> weightOf)
        {
            int w = 0;
            foreach (var kv in Cargo) w += kv.Value * weightOf(kv.Key);
            return w;
        }
    }

    // ---------- 状态: 舰队级 ----------

    public sealed class ProvisionSpec
    {
        public string Kind;          // food/water/tea
        public float UnitWeight;     // 每份(1水手1日)占载重
        public float Price;          // 每份采购价
        public float PerCrewPerDay;  // 每水手每日消耗份数
    }

    // 库存: 每份 = 1 水手 1 日的该给养(人·日份)。三种日耗均为 crew 份 → 三人份总重 0.06 载/水手·日。
    public sealed class ProvisionStock
    {
        public float Food, Water, Tea;
        public float Get(string kind)
        {
            switch (kind)
            {
                case "food": return Food;
                case "water": return Water;
                case "tea": return Tea;
                default: return 0;
            }
        }
        public void Set(string kind, float v)
        {
            switch (kind)
            {
                case "food": Food = v; break;
                case "water": Water = v; break;
                case "tea": Tea = v; break;
            }
        }
    }

    public sealed class CareerLog
    {
        public long SoldBoxes;                 // 累计售出货物件数
        public int BattleWins;                 // 海战胜场
        public int SailDays;                   // 累计海上航行日
        public long RefitGold;                 // 累计改造花费
        public bool ScurvyEver;                // 是否经历过坏血
        public int GuildLevel = 1;
        public readonly HashSet<string> VisitedAreas = new HashSet<string>();
        public readonly HashSet<string> VisitedPorts = new HashSet<string>();
        public readonly Dictionary<string, int> SoldAtPort = new Dictionary<string, int>(); // 港口累计出货件数

        public void NoteSettlement(string portId)
        {
            VisitedPorts.Add(portId);
        }
        public void NoteSailDay() { SailDays++; }
        public void NoteSold(string portId, int qty)
        {
            SoldBoxes += qty;
            if (portId != null && portId.Length > 0)
            {
                VisitedPorts.Add(portId);
                int n; SoldAtPort.TryGetValue(portId, out n); SoldAtPort[portId] = n + qty;
            }
        }
        public void NoteBattleWin() { BattleWins++; }
    }

    // 舰队级船员(在册 NPC)。普通水手只算数量, 不进此表。
    public sealed class CrewMember
    {
        public CrewD D;
        public CrewD Data => D;
    }

    // 在册 NPC 聚合出来的被动加成。速率类相乘, 其余相加(见文件头注释)。
    public sealed class FleetBonus
    {
        // 数值型(相加)
        public float MoraleCap, BuyDiscount, SellBonus, RansomCut, StormLossCut, FireMul;
        public float ScurvyLossMul, TreatCostCut, RefitCostCut, ProvisionCostCut, BoardingPower;
        public float BattleWinBonus, MonsoonSpeedAdd;
        // 速率型(相乘) —— 多源用乘法避免重复累减
        public float FoodUseMul = 1f;
        public float SightMul = 1f;
        public bool FreeIntel;

        public static FleetBonus From(IEnumerable<CrewD> crew)
        {
            var b = new FleetBonus();
            if (crew == null) return b;
            foreach (var c in crew)
            {
                if (c?.buffs == null) continue;
                foreach (var bf in c.buffs)
                {
                    if (bf == null) continue;
                    switch (bf.kind)
                    {
                        case "moraleCap": b.MoraleCap += bf.delta; break;
                        case "battleWin": b.BattleWinBonus += bf.delta; break;
                        case "buyDiscount": b.BuyDiscount += bf.delta; break;
                        case "sellBonus": b.SellBonus += bf.delta; break;
                        case "ransomCut": b.RansomCut += bf.delta; break;
                        case "stormLossCut": b.StormLossCut += bf.delta; break;
                        case "sightMul": b.SightMul *= Math.Abs(bf.delta) < 1e-6f ? 1f : bf.delta; break;
                        case "fireMul": b.FireMul += bf.delta; break;
                        case "scurvyLossMul": b.ScurvyLossMul += bf.delta; break;
                        case "treatCostCut": b.TreatCostCut += bf.delta; break;
                        case "refitCostCut": b.RefitCostCut += bf.delta; break;
                        case "speedAdd": break; // 由 Dino 直接作用于旗舰/全队, FleetBonus 聚合段交给 FleetState.StatSpeedAdd
                        case "provisionCostCut": b.ProvisionCostCut += bf.delta; break;
                        case "foodUseMul": b.FoodUseMul *= bf.delta; break;
                        case "monsoonSpeedAdd": b.MonsoonSpeedAdd += bf.delta; break;
                        case "boardingPower": b.BoardingPower += bf.delta; break;
                        case "freeIntel": if (bf.delta > 0) b.FreeIntel = true; break;
                    }
                }
            }
            return b;
        }

        // 直接数值(buyDiscount 等)供引擎/测试复用: 均为 0..1 比例, 不处理 speedAdd。
        public int ApplyAsk(int ask)  // 买入价: 折扣封顶 50%
        {
            float disc = BuyDiscount > 0.5f ? 0.5f : BuyDiscount;
            int v = (int)Math.Floor(ask * (1f - disc));
            return v < 1 ? 1 : v;
        }
        public int ApplyBid(int bid)  // 卖出价
        {
            int v = (int)Math.Floor(bid * (1f + SellBonus));
            return v < 0 ? 0 : v;
        }
    }

    public sealed class FleetState
    {
        public FleetCatalog Cat;
        public readonly List<ShipState> Ships = new List<ShipState>();   // [0]=旗舰
        public readonly List<CrewMember> Crew = new List<CrewMember>();  // 在册 NPC
        public readonly ProvisionStock Prov = new ProvisionStock();
        public readonly CareerLog Career = new CareerLog();
        public float Morale;
        public int Sick;                       // 病号人数(坏血等)
        public long Gold;

        // 逐日缺补计时(单位: 天)
        public int FoodOutDays;                // 食物断供累计天数(断粮: 当日重击、次日起减员)
        public int WaterOutDays;               // 水断供累计天数(断水: 潜伏后每日掉士气 + 病号)
        public int TeaOutDays;                 // 茶叶断供累计天数(缺茶: 潜伏后每日掉士气)

        public ShipState Flagship => Ships.Count > 0 ? Ships[0] : null;
        public FleetBonus Bonus;               // 由 Crew 重算
        public Func<string, int> WeightOf;     // 单件货重(默认 1), SeaLoader 接 World 后给真值

        public int TotalCrew()
        {
            int n = 0;
            foreach (var s in Ships) n += s.CrewAboard;
            return n;
        }
        public int TotalCargoUnits()
        {
            int n = 0;
            foreach (var s in Ships) n += s.CargoUnitCount;
            return n;
        }
        public int TotalCargoWeight()
        {
            int w = 0;
            foreach (var s in Ships) w += s.OccupiedWeight(WeightOf);
            return w;
        }
        public int FreeWeight()
        {
            int used = 0, cap = 0;
            foreach (var s in Ships) { used += s.OccupiedWeight(WeightOf); cap += s.EffCapacity; }
            return cap - used;
        }
        public float FleetTopSpeed()
        {
            float hi = 0f;
            foreach (var s in Ships) if (s.EffSpeed > hi) hi = s.EffSpeed;
            return hi;
        }
        public float FleetFire()  // 全队火力(Σeff)
        {
            float f = 0f;
            foreach (var s in Ships) f += s.EffFire;
            return f;
        }

        // 卸掉一件货(逐船从队尾回填, 尽量少破坏结构); 返回 true=卸成
        public bool RemoveUnit(string goodId, int qty)
        {
            for (int k = Ships.Count - 1; k >= 0 && qty > 0; k--)
            {
                var s = Ships[k];
                int have;
                if (s.Cargo.TryGetValue(goodId, out have) && have > 0)
                {
                    int take = have < qty ? have : qty;
                    s.Cargo[goodId] = have - take;
                    qty -= take;
                    if (s.Cargo[goodId] == 0) s.Cargo.Remove(goodId);
                }
            }
            return qty <= 0;
        }

        // 自动卸货直到不超上限(用于战败货损后压仓)。
        public void DropCargoByWeight(int dropWeight)
        {
            int toDrop = dropWeight;
            foreach (var s in Ships)
            {
                while (toDrop > 0 && s.Cargo.Count > 0)
                {
                    // 任取一种逐件丢(近似); M2 让玩家挑
                    var e = s.Cargo.GetEnumerator();
                    e.MoveNext();
                    var gid = e.Current.Key;
                    int n = s.Cargo[gid];
                    int drop = Math.Min(n, toDrop);
                    toDrop -= drop;
                    if (n - drop == 0) s.Cargo.Remove(gid); else s.Cargo[gid] = n - drop;
                }
                if (toDrop <= 0) break;
            }
        }

        public void RecomputeBonus()
        {
            var ds = new List<CrewD>();
            foreach (var m in Crew) ds.Add(m.D);
            Bonus = FleetBonus.From(ds);
        }
    }

    // ---------- 调参 ----------

    public sealed class FleetTuning
    {
        public int Version = 1;
        public int FleetMaxShips = 5;
        public float RecruitCostBase = 8f;
        public float SailorSalaryPerSettlement = 8f;
        public int MoraleMax = 100;
        public readonly Dictionary<string, ProvisionSpec> Provision = new Dictionary<string, ProvisionSpec>()
        {
            { "food",  new ProvisionSpec { Kind = "food",  UnitWeight = 0.03f, Price = 0.5f, PerCrewPerDay = 1.0f } },
            { "water", new ProvisionSpec { Kind = "water", UnitWeight = 0.02f, Price = 0.2f, PerCrewPerDay = 1.0f } },
            { "tea",   new ProvisionSpec { Kind = "tea",   UnitWeight = 0.01f, Price = 1.0f, PerCrewPerDay = 1.0f } },
        };
        // 四种改造: 费=船价×[costMin..costMax]; 效果 capacity/fire/armor 为加%区间, speed 为加节区间
        public readonly Dictionary<string, RefitRates> Refit = new Dictionary<string, RefitRates>()
        {
            { "capacity", new RefitRates { Kind = RefitKind.Capacity, CostMinPct = 0.15f, CostMaxPct = 0.30f, EffectMin = 0.10f, EffectMax = 0.25f } },
            { "fire",     new RefitRates { Kind = RefitKind.Fire,     CostMinPct = 0.15f, CostMaxPct = 0.30f, EffectMin = 0.10f, EffectMax = 0.25f } },
            { "armor",    new RefitRates { Kind = RefitKind.Armor,    CostMinPct = 0.15f, CostMaxPct = 0.30f, EffectMin = 0.10f, EffectMax = 0.25f } },
            { "speed",    new RefitRates { Kind = RefitKind.Speed,    CostMinPct = 0.15f, CostMaxPct = 0.30f, EffectMin = 0.50f, EffectMax = 1.50f } },
        };

        public readonly Dictionary<string, float> DangerByArea = new Dictionary<string, float>();
        public float PursuitSpeedFactor = 1.15f;
        public float RansomMinPct = 0.05f;
        public float RansomMaxPct = 0.15f;
        public float CargoLossLosePct = 0.15f;
        public readonly List<WinAnchor> AttackAnchors = new List<WinAnchor>();
        public readonly List<WinAnchor> DefenseAnchors = new List<WinAnchor>();

        public float DangerOf(string areaId)
        {
            float d;
            return DangerByArea.TryGetValue(areaId ?? "", out d) ? d : 0f;
        }

        public static FleetTuning From(FleetTuningD d)
        {
            var t = new FleetTuning();
            if (d == null) return t;
            if (d.version > 0) t.Version = d.version;
            if (d.fleetMaxShips > 0) t.FleetMaxShips = d.fleetMaxShips;
            if (d.crew != null)
            {
                if (d.crew.recruitCostBase > 0) t.RecruitCostBase = d.crew.recruitCostBase;
                if (d.crew.sailorSalaryPerSettlement > 0) t.SailorSalaryPerSettlement = d.crew.sailorSalaryPerSettlement;
                if (d.crew.moraleMax > 0) t.MoraleMax = d.crew.moraleMax;
            }
            if (d.provision != null)
            {
                ApplyProv(t.Provision["food"], d.provision.food);
                ApplyProv(t.Provision["water"], d.provision.water);
                ApplyProv(t.Provision["tea"], d.provision.tea);
            }
            if (d.refit?.kinds != null)
            {
                foreach (var k in d.refit.kinds)
                {
                    if (k == null || k.kind == null) continue;
                    if (t.Refit.ContainsKey(k.kind)) t.Refit[k.kind] = RefitRates.From(k);
                }
            }
            if (d.pirate != null)
            {
                if (d.pirate.pursuitSpeedFactor > 0) t.PursuitSpeedFactor = d.pirate.pursuitSpeedFactor;
                if (d.pirate.ransomMinPct > 0) t.RansomMinPct = d.pirate.ransomMinPct;
                if (d.pirate.ransomMaxPct > 0) t.RansomMaxPct = d.pirate.ransomMaxPct;
                if (d.pirate.cargoLossLosePct > 0) t.CargoLossLosePct = d.pirate.cargoLossLosePct;
                if (d.pirate.dangerByArea != null)
                    foreach (var x in d.pirate.dangerByArea)
                        if (x != null && x.areaId != null) t.DangerByArea[x.areaId] = x.danger;
                SetAnchors(t.AttackAnchors, d.pirate.attackWinAnchors);
                SetAnchors(t.DefenseAnchors, d.pirate.defenseWinAnchors);
            }
            return t;
        }

        static void ApplyProv(ProvisionSpec dst, ProvisionItemD src)
        {
            if (src == null) return;
            if (src.unitWeight > 0) dst.UnitWeight = src.unitWeight;
            if (src.price > 0) dst.Price = src.price;
            if (src.perCrewPerDay > 0) dst.PerCrewPerDay = src.perCrewPerDay;
        }
        static void SetAnchors(List<WinAnchor> dst, AnchorD[] src)
        {
            dst.Clear();
            if (src == null) return;
            foreach (var a in src) if (a != null) dst.Add(new WinAnchor { Ratio = a.ratio, Chance = a.chance });
        }
    }

    public sealed class RefitRates
    {
        public RefitKind Kind;
        public float CostMinPct, CostMaxPct, EffectMin, EffectMax;
        public static RefitRates From(RefitKindD d)
        {
            return new RefitRates
            {
                Kind = ParseKind(d.kind),
                CostMinPct = d.costMinPct > 0 ? d.costMinPct : 0.15f,
                CostMaxPct = d.costMaxPct > 0 ? d.costMaxPct : 0.30f,
                EffectMin = d.effectMin, EffectMax = d.effectMax
            };
        }
        public static RefitKind ParseKind(string s)
        {
            switch (s)
            {
                case "capacity": return RefitKind.Capacity;
                case "fire": return RefitKind.Fire;
                case "armor": return RefitKind.Armor;
                default: return RefitKind.Speed;
            }
        }
    }

    public struct WinAnchor
    {
        public float Ratio;
        public float Chance;
    }

    // ---------- 公式(纯函数, 可无状态断言) ----------

    public static class FleetFormulas
    {
        public const int TeaOutMoraleLatency = 3;      // 茶叶断供≥3天才开始掉士气
        public const int TeaOutMoralePerDay = 3;       // 之后每日士气 -3
        public const int WaterOutLatency = 3;          // 断水≥3天才开始不适
        public const int WaterMoralePerDay = 5;        // 之后每日士气 -5(脱水乏力)
        public const int WaterSickInterval = 2;        // 之后每 2 日 +1 病号(脱水)
        public const int FoodMoraleHit = 15;           // 断粮当日士气 -15
        public const float FoodCrewLossPerDay = 0.20f; // 断粮次日起每日减员上限 20%

        // 硬续航(全口径, 天): min over 三类 floor(库存份 / (总水手×每水手日耗))
        //   水手 = 0 → 没有消耗者也谈不上续航: 返回 0 而非天文数字(不可出航, 不给 21 亿日误导)。
        public static int EnduranceDays(ProvisionStock prov, int totalCrew,
            float foodRate, float waterRate, float teaRate)
        {
            if (totalCrew <= 0) return 0;
            int dF = FloorDiv(prov.Food, totalCrew * foodRate);
            int dW = FloorDiv(prov.Water, totalCrew * waterRate);
            int dT = FloorDiv(prov.Tea, totalCrew * teaRate);
            int m = Math.Min(Math.Min(dF, dW), dT);
            return m < 0 ? 0 : m;
        }

        static int FloorDiv(float numer, float denom)
        {
            if (denom <= 0f) return numer > 0 ? int.MaxValue : 0;
            return (int)Math.Floor(numer / denom);
        }

        // 锚点插值求胜率(单调; 越界截尾到两端锚点)
        public static float WinChance(IList<WinAnchor> anchors, float ratio)
        {
            if (anchors == null || anchors.Count == 0) return 0.5f;
            if (ratio <= anchors[0].Ratio) return anchors[0].Chance;
            var last = anchors[anchors.Count - 1];
            if (ratio >= last.Ratio) return last.Chance;
            for (int i = 1; i < anchors.Count; i++)
            {
                if (ratio <= anchors[i].Ratio)
                {
                    var a = anchors[i - 1]; var b = anchors[i];
                    if (b.Ratio - a.Ratio < 1e-6f) return b.Chance;
                    float t = (ratio - a.Ratio) / (b.Ratio - a.Ratio);
                    return a.Chance + (b.Chance - a.Chance) * t;
                }
            }
            return last.Chance;
        }

        // 遭遇概率: 基地危险度 × 货值热度(货值越大越招海盗), 封顶 1
        public static float EncounterChance(float areaDanger, long cargoValue)
        {
            float heat = 0.7f + 0.3f * Math.Min(1f, cargoValue / 50000f);
            float v = areaDanger * heat;
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        // 我方进攻战力 = 全队火力 × (1 + 旗舰水手战力系数) × 士气/100
        public static float FleetPower(FleetState f)
        {
            float ff = f.FleetFire();
            var fl = f.Flagship;
            if (fl == null) return ff;
            float crewCoef = fl.Model.CrewMax > 0 ? fl.CrewAboard / (float)fl.Model.CrewMax : 0f;
            return ff * (1f + crewCoef) * (f.Morale / 100f);
        }
    }

    // ---------- 目录 ----------

    public sealed class FleetCatalog
    {
        public readonly Dictionary<string, ShipModel> Ships = new Dictionary<string, ShipModel>();
        public readonly Dictionary<string, CrewD> Crews = new Dictionary<string, CrewD>();
        public readonly List<string> ShipOrder = new List<string>();
        public readonly List<string> CrewOrder = new List<string>();
        public FleetTuning Tuning;
        public Func<string, int> WeightOf = g => 1;

        public ShipModel FindShip(string id)
        {
            ShipModel m;
            return Ships.TryGetValue(id, out m) ? m : null;
        }

        public static FleetCatalog Build(ShipD[] ships, CrewD[] crews, FleetTuningD tuningD, Func<string, int> weightOf = null)
        {
            var c = new FleetCatalog { Tuning = FleetTuning.From(tuningD) };
            if (weightOf != null) c.WeightOf = weightOf;
            if (ships != null)
                foreach (var s in ships)
                {
                    if (s == null || c.Ships.ContainsKey(s.id)) continue;
                    var m = ShipModel.From(s);
                    c.Ships[m.Id] = m;
                    c.ShipOrder.Add(m.Id);
                }
            if (crews != null)
                foreach (var cr in crews)
                {
                    if (cr == null || c.Crews.ContainsKey(cr.id)) continue;
                    c.Crews[cr.id] = cr;
                    c.CrewOrder.Add(cr.id);
                }
            return c;
        }
    }

    // ---------- 装载: 造初始舰队 ----------

    public static class FleetBuilder
    {
        // 新建一条"新造/购得"船并加入舰队; 船由 modelId 决定, 初始水手取 crewMin(可后续 RaiseCrew)。
        public static ShipState BuildShip(FleetState f, string modelId, int crewAboard, bool flagship = false)
        {
            var m = f.Cat.FindShip(modelId);
            if (m == null) return null;
            if (crewAboard < m.CrewMin) crewAboard = m.CrewMin;
            if (crewAboard > m.CrewMax) crewAboard = m.CrewMax;
            var s = new ShipState { Model = m, CrewAboard = crewAboard, IsFlagship = flagship };
            if (flagship && f.Ships.Count == 0) f.Ships.Insert(0, s);
            else f.Ships.Add(s);
            return s;
        }

        // 初始舰队(标准开局: 一条指定船作旗舰 + 给养)
        public static FleetState Start(FleetCatalog cat, string flagshipId, int flagshipCrew, long gold, ProvisionStock prov)
        {
            var f = new FleetState { Cat = cat, Gold = gold, WeightOf = cat.WeightOf, Morale = cat.Tuning.MoraleMax };
            f.Bonus = FleetBonus.From(null);
            BuildShip(f, flagshipId, flagshipCrew, flagship: true);
            f.Prov.Food = prov.Food;
            f.Prov.Water = prov.Water;
            f.Prov.Tea = prov.Tea;
            return f;
        }
    }

    // =============================================================
    // 舰队操作(逐日/进港/招募/改造/海盗) —— 方法挂 FleetState, 便于后续 M2 接入。
    // =============================================================

    public static class FleetOps
    {
        // ---------- 载货 ----------

        // 装货: 按"货种≤slots"与"载重≤capacity"逐船分摊。装不满的在旗舰先填。
        // 返回实际装入件数。goodId 只认目录里 WeightOf 认识的键(未知按 1 计)。
        public static int AddCargo(FleetState f, string goodId, int qty)
        {
            if (qty <= 0 || f.Ships.Count == 0) return 0;
            int w = f.WeightOf(goodId);
            int placed = 0;
            for (int k = 0; k < f.Ships.Count && qty > 0; k++)
            {
                var s = f.Ships[k];
                if (!s.Cargo.ContainsKey(goodId) && s.Cargo.Count >= s.Model.Slots) continue; // 货种满
                int remainCap = s.EffCapacity - s.OccupiedWeight(f.WeightOf);
                int fit = w > 0 ? remainCap / w : 0;
                int put = fit < qty ? fit : qty;
                if (put <= 0) continue;
                int have;
                s.Cargo.TryGetValue(goodId, out have);
                s.Cargo[goodId] = have + put;
                qty -= put; placed += put;
            }
            return placed;
        }

        // ---------- 续航与逐日 ----------

        // 三类口径硬续航(把 Ships 满员视为续航上限不现实, 用"当前水手"算)
        public static int EnduranceDays(FleetState f)
        {
            var t = f.Cat.Tuning;
            return FleetFormulas.EnduranceDays(f.Prov, f.TotalCrew(),
                t.Provision["food"].PerCrewPerDay * FoodUseMul(f),
                t.Provision["water"].PerCrewPerDay,
                t.Provision["tea"].PerCrewPerDay);
        }

        static float FoodUseMul(FleetState f)
            => f.Bonus != null && f.Bonus.FoodUseMul > 0 ? f.Bonus.FoodUseMul : 1f;

        // 消耗一天给养并推进缺补计时。固定顺序: 耗三类→判缺补(见契约注释)。
        public static void AdvanceOneSeaDay(FleetState f)
        {
            var t = f.Cat.Tuning;
            int crew = f.TotalCrew();
            float fm = FoodUseMul(f);

            float food = t.Provision["food"].PerCrewPerDay * fm * crew;
            float water = t.Provision["water"].PerCrewPerDay * crew;
            float tea = t.Provision["tea"].PerCrewPerDay * crew;

            f.Prov.Food = Sub(f.Prov.Food, food);
            f.Prov.Water = Sub(f.Prov.Water, water);
            f.Prov.Tea = Sub(f.Prov.Tea, tea);

            f.Career.NoteSailDay();

            // 断供计时(供应恢复则清零)
            bool foodOut = f.Prov.Food <= 0f;
            bool waterOut = f.Prov.Water <= 0f;
            bool teaOut = f.Prov.Tea <= 0f;
            f.FoodOutDays = foodOut ? f.FoodOutDays + 1 : 0;
            f.WaterOutDays = waterOut ? f.WaterOutDays + 1 : 0;
            f.TeaOutDays = teaOut ? f.TeaOutDays + 1 : 0;

            if (f.Morale > t.MoraleMax) f.Morale = t.MoraleMax;

            // 断粮: 当日一次士气重击; 次日起减员(20%/日)
            if (foodOut)
            {
                if (f.FoodOutDays == 1)
                    f.Morale = Sub(f.Morale, FleetFormulas.FoodMoraleHit);
                else if (f.FoodOutDays >= 2)
                    LoseSailors(f, Math.Max(1, (int)Math.Ceiling(crew * FleetFormulas.FoodCrewLossPerDay)));
            }

            // 缺茶 ≥3 日: 每日士气 -3
            if (f.TeaOutDays >= FleetFormulas.TeaOutMoraleLatency)
                f.Morale = Sub(f.Morale, FleetFormulas.TeaOutMoralePerDay);

            // 断水 ≥3 日: 每日士气 -5; 每 2 日 +1 病号(脱水乏力, 记入病号档案)
            if (f.WaterOutDays >= FleetFormulas.WaterOutLatency)
            {
                f.Morale = Sub(f.Morale, FleetFormulas.WaterMoralePerDay);
                if ((f.WaterOutDays - FleetFormulas.WaterOutLatency) % FleetFormulas.WaterSickInterval == 0)
                {
                    int add = 1;
                    // 医官(scurvyLossMul>0)压低病倒; 占位: 有医官则不添新病(简化, M2 细化为减员削减)
                    if (f.Bonus != null && f.Bonus.ScurvyLossMul > 0f) add = 0;
                    if (add > 0)
                    {
                        f.Sick++;
                        f.Career.ScurvyEver = true;
                    }
                }
            }
        }

        static float Sub(float a, float b) => a > b ? a - b : 0f;

        // 减员: 从尾船起退到 crewMin 再减, 直到不够就全裁(死人)。返回实际减掉人数。
        static int LoseSailors(FleetState f, int n)
        {
            int lost = 0;
            for (int k = f.Ships.Count - 1; k >= 0 && n > 0; k--)
            {
                var s = f.Ships[k];
                int possible = s.CrewAboard;
                int take = possible < n ? possible : n;
                s.CrewAboard -= take;
                lost += take; n -= take;
            }
            return lost;
        }

        // ---------- 进港结算 ----------

        // 停港: 结薪(§10.1: 只在此扣工资), 治病, 士气回 80, 顺带登记到港。
        // 返回本次停靠支出(工资+医疗)。
        public static long SettleAtPort(FleetState f, string portId)
        {
            var t = f.Cat.Tuning;
            long wage = (long)Math.Floor(t.SailorSalaryPerSettlement * f.TotalCrew());
            foreach (var m in f.Crew) wage += m.D.salary;
            long medCost = 0;
            if (f.Sick > 0)
            {
                float cut = f.Bonus != null ? (f.Bonus.TreatCostCut > 1f ? 1f : f.Bonus.TreatCostCut) : 0f;
                medCost = (long)(f.Sick * 100 * (1f - cut));
                f.Sick = 0;
            }
            long spend = wage + medCost;
            if (f.Gold >= spend) f.Gold -= spend; else f.Gold = 0;
            f.Career.NoteSettlement(portId);
            // 士气在港内休整回升到 80(高于则不动)
            if (f.Morale < 80) f.Morale = 80;
            return spend;
        }

        // ---------- 招募 ----------

        // 该 NPC 当前是否可被招募(职业门槛全看 CareerLog; atPort 可空跳过港口检查)
        public static bool RecruitAvailable(FleetCatalog cat, CrewD c, CareerLog log, string atPortId = null)
        {
            if (c == null || c.hire == null) return false;
            var h = c.hire;
            if (log.GuildLevel < h.minLevel) return false;
            if (log.SailDays < h.minSailDays) return false;
            if (log.SoldBoxes < h.minSoldBoxes) return false;
            if (log.BattleWins < h.minBattleWins) return false;
            if (log.RefitGold < h.minRefitGold) return false;
            if (h.scurvySeen && !log.ScurvyEver) return false;
            if (h.visitedAreaIds != null)
                foreach (var a in h.visitedAreaIds)
                    if (!log.VisitedAreas.Contains(a)) return false;
            if (atPortId != null && atPortId.Length > 0)
            {
                if (h.atPorts != null && !Contains(h.atPorts, atPortId)) return false;
                if (h.soldAtPortId != null && h.soldAtPortId.Length > 0)
                {
                    int n;
                    log.SoldAtPort.TryGetValue(h.soldAtPortId, out n);
                    if (n < h.minSoldAtPort) return false;
                }
            }
            return true;
        }

        static bool Contains(string[] a, string v)
        {
            if (a == null) return false;
            foreach (var x in a) if (x == v) return true;
            return false;
        }

        // 招募: 通过门槛+未在队+队上限内则入队。返回是否成功。
        public static bool Hire(FleetState f, string crewId, string atPortId = null)
        {
            CrewD cd;
            if (!f.Cat.Crews.TryGetValue(crewId, out cd)) return false;
            foreach (var m in f.Crew) if (m.D.id == crewId) return false;
            if (!RecruitAvailable(f.Cat, cd, f.Career, atPortId)) return false;
            f.Crew.Add(new CrewMember { D = cd });
            f.RecomputeBonus();
            return true;
        }

        // ---------- 改造 ----------

        // 给某船做一次改造。成功返回 Applied; 槽满/没钱/未知kind 返回 null。
        public static RefitApplied RefitCalc(FleetState f, ShipState s, RefitKind kind)
        {
            if (s == null || !s.CanRefit) return null;
            RefitRates r = null;
            foreach (var kv in f.Cat.Tuning.Refit)
                if (kv.Value.Kind == kind) { r = kv.Value; break; }
            if (r == null) return null;
            // 确定性取值: 成本取下限、效果取区间中值(M1 全为占位)
            float costPct = r.CostMinPct;
            long cost = (long)Math.Round(s.Model.Price * costPct);
            float cut = f.Bonus != null ? f.Bonus.RefitCostCut : 0f;
            if (cut > 0)
            {
                long saved = (long)(cost * (cut > 1f ? 1f : cut));
                cost -= saved;
            }
            if (f.Gold < cost) return null;
            float eff;
            switch (kind)
            {
                case RefitKind.Capacity: eff = (r.EffectMin + r.EffectMax) * 0.5f; break;
                case RefitKind.Fire: eff = (r.EffectMin + r.EffectMax) * 0.5f; break;
                case RefitKind.Armor: eff = (r.EffectMin + r.EffectMax) * 0.5f; break;
                default: eff = (r.EffectMin + r.EffectMax) * 0.5f; break;
            }
            var ap = new RefitApplied { Kind = kind, Cost = (int)cost, EffectValue = eff };
            f.Gold -= cost;
            f.Career.RefitGold += cost;
            s.ApplyRefit(ap);
            return ap;
        }

        // ---------- 海盗(自动查表; M3 手操走 IBattleAction) ----------

        public static float EncounterChance(FleetState f, string areaId)
            => FleetFormulas.EncounterChance(f.Cat.Tuning.DangerOf(areaId), CargoValue(f));

        // 全队货值估算(单价未知, M1 占位: 件数×10)
        public static long CargoValue(FleetState f)
        {
            long v = 0;
            foreach (var s in f.Ships)
                foreach (var kv in s.Cargo)
                    v += kv.Value * 10L;
            return v;
        }

        // 主动决定: 是否开战/逃跑(为接口占位 —— M3 由玩家选择)
        public interface IBattleAction { }

        // 自动结算一场海盗遭遇。areaId 决定基准危险度; pirSpeed 用于是否可逃。
        // seed 决定本次随机(同 seed 同结果)。returns 结果对象。
        public static PirateOutcome AutoResolve(FleetState f, string areaId, int pirPower, float pirSpeed, int seed)
        {
            var t = f.Cat.Tuning;
            var o = new PirateOutcome();

            // 跑得掉先跑(全队最高速 ≥ 海盗速×系数)
            if (pirSpeed > 0 && f.FleetTopSpeed() >= pirSpeed * t.PursuitSpeedFactor)
            {
                o.Escaped = true;
                return o;
            }

            var rng = new Random(seed);
            float ourPower = FleetFormulas.FleetPower(f) * (1f + (f.Bonus != null ? f.Bonus.BattleWinBonus : 0f));
            float ratio = pirPower > 0 ? ourPower / pirPower : 99f;

            // 我方在危险海域为"防守方"(被袭); 没有主动进攻模式(M1 简化)
            float chance = FleetFormulas.WinChance(t.DefenseAnchors, ratio);
            bool won = rng.NextDouble() < chance;
            if (won)
            {
                o.Victory = true;
                f.Career.NoteBattleWin();
                f.Morale = Math.Min(t.MoraleMax, f.Morale + 10);
                return o;
            }

            // 落败: 损失货值 cargoLossLosePct(先尝试交赎金抵一点)
            o.Defeated = true;
            long lossVal = (long)(CargoValue(f) * t.CargoLossLosePct);
            long ransom = 0;
            if (lossVal > 0 && f.Gold > 0)
            {
                // 赎金(占总货物损失价值, 但≤当前现金); 医官/账房可砍
                float cut = f.Bonus != null ? (f.Bonus.RansomCut > 0 ? f.Bonus.RansomCut : 0f) : 0f;
                long demand = (long)(lossVal * (t.RansomMinPct + (t.RansomMaxPct - t.RansomMinPct) * rng.NextDouble()) * (1f - cut));
                if (demand < 0) demand = 0;
                long pay = demand < f.Gold ? demand : f.Gold;
                f.Gold -= pay;
                ransom = pay;
                // 赎金抵一部分货损
                lossVal -= (long)(pay * 2f);
                if (lossVal < 0) lossVal = 0;
            }
            o.RansomPaid = ransom;
            // 按件数折货损(每件 10): 丢掉大致等值货物
            int drop = (int)(lossVal / 10L);
            if (drop > 0)
            {
                f.DropCargoByWeight(drop);
                o.CargoLostUnits = drop;
            }
            f.Morale = Math.Max(0f, f.Morale - 25);
            return o;
        }
    }

    public sealed class PirateOutcome
    {
        public bool Escaped;      // 速度够, 未接战
        public bool Victory;      // 战而胜
        public bool Defeated;     // 战败(含受创)
        public long RansomPaid;   // 实际付的赎金
        public int CargoLostUnits;
    }
}
