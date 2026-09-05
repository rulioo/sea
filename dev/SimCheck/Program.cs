using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Sea;

namespace Sea.SimCheck
{
    // =============================================================
    // 无 Unity 的验证台:
    //   1. 加载并校验全部数据
    //   2. 跑 3 年经济模拟, 断言数值健康(无 NaN / 价格钳制 / 库存钳制)
    //   3. 验证大事件方向与封锁
    //   4. 几条典型航线毛利 + 倾销砸价 + 商人Bot
    // =============================================================

    internal static class Program
    {
        static int _errors;

        static int Main()
        {
            string dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
            Console.WriteLine("== SEA SimCheck ==");
            Console.WriteLine("数据目录: " + dataDir);

            try
            {
                var goods = Load<GoodD[]>("Goods.json");
                var areas = Load<AreaD[]>("Areas.json");
                var ports = Load<PortD[]>("Ports.json");
                var events = Load<EventD[]>("Events.json");
                var tuningD = Load<MarketTuningD>("MarketTuning.json");
                var shipDs = Load<ShipD[]>("Ships.json");
                var crewDs = Load<CrewD[]>("Crews.json");
                var fleetTuningD = Load<FleetTuningD>("FleetTuning.json");

                Validate(goods, areas, ports, events);

                var world = WorldBuilder.Build(goods, areas, ports, events);
                var tuning = MarketTuning.From(tuningD);
                var sim = new SimEngine(world, tuning, 12345);

                Console.WriteLine($"世界装载: {world.Goods.Count} 商品 / {world.Areas.Count} 航区 / {world.Ports.Count} 港口 / {world.Events.Count} 事件");

                RunFleetChecks(areas, ports, shipDs, crewDs, fleetTuningD);
                RunScenarioChecks(sim);
                RunPriceIntegrity(sim);
                RunBot(world, tuning);
                ProbeLisbonFresh(goods, areas, ports, events, tuningD);   // 临时探针: 里斯本开局行情/买测

                Console.WriteLine();
                if (_errors == 0) { Console.WriteLine("RESULT: ALL CHECKS PASSED"); return 0; }
                Console.WriteLine($"RESULT: FAILED with {_errors} error(s)");
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 2;
            }
        }

        // 临时探针(诊断用, 核对完即删): 全新世界 day0 里斯本行情与买10件结果
        static void ProbeLisbonFresh(GoodD[] goods, AreaD[] areas, PortD[] ports, EventD[] events, MarketTuningD tuningD)
        {
            var w = WorldBuilder.Build(goods, areas, ports, events);
            var sim = new SimEngine(w, MarketTuning.From(tuningD), 4321);
            Port lp = null;
            foreach (var p in w.Ports) if (p.Id == "lisbon") { lp = p; break; }
            if (lp == null) { Console.WriteLine("探针: 无里斯本港"); return; }
            Console.WriteLine($"== 里斯本买测: blocked={lp.Blocked} day={sim.Day} ==");
            foreach (var g in w.Goods)
            {
                int ask = sim.AskPrice(lp, g), bid = sim.BidPrice(lp, g), st = sim.BuyStock(lp, g);
                var c = sim.GetCell(lp, g);
                var res = sim.Buy(lp, g, 10, 250000);
                Console.WriteLine($"  {g.Name,-6} ask={ask,5} bid={bid,5} stock={st,5} prod={c.Produced,5} need={c.Needed,5}  buy10->{res.Units,2}件 {res.Gold,6}金");
            }
        }

        static JsonSerializerOptions Opt() => new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            IncludeFields = true,
            AllowTrailingCommas = true
        };

        static T Load<T>(string file)
        {
            string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", file));
            return JsonSerializer.Deserialize<T>(text, Opt());
        }

        // ---------- 数据一致性 ----------
        static void Validate(GoodD[] goods, AreaD[] areas, PortD[] ports, EventD[] events)
        {
            var gs = new HashSet<string>();
            foreach (var g in goods) gs.Add(g.id);
            var aset = new HashSet<string>();
            foreach (var a in areas) aset.Add(a.id);

            Check(gs.Count == 39, "商品应为 39, 实际 " + gs.Count);
            Check(aset.Count == 8, "航区应为 8, 实际 " + aset.Count);
            Check(ports.Length == 52, "港口应为 52, 实际 " + ports.Length);
            Check(events.Length == 16, "事件应为 16, 实际 " + events.Length);

            foreach (var p in ports)
            {
                Must(aset.Contains(p.areaId), $"港口 {p.id} 的航区 {p.areaId} 不存在");
                foreach (var s in p.specialties) Must(gs.Contains(s), $"港口 {p.id} 特产 {s} 不存在");
                foreach (var i in p.imports) Must(gs.Contains(i), $"港口 {p.id} 需求 {i} 不存在");
                foreach (var s in p.specialties)
                    foreach (var i in p.imports)
                        Must(s != i, $"港口 {p.id} 的 {s} 既是特产又是需求(自产自需需标注)");
            }
            foreach (var e in events)
            {
                foreach (var g in e.scope?.goods ?? Array.Empty<string>()) Must(gs.Contains(g), $"事件 {e.id} 引用商品 {g} 不存在");
                foreach (var pid in e.scope?.ports ?? Array.Empty<string>())
                    Must(Array.Exists(ports, p => p.id == pid), $"事件 {e.id} 引用港口 {pid} 不存在");
            }
            Console.WriteLine($"数据校验: {( _errors == 0 ? "通过" : "有错")}");
        }

        // ---------- 场景断言 ----------
        static void RunScenarioChecks(SimEngine sim)
        {
            Console.WriteLine();
            Console.WriteLine("== 场景断言 ==");

            // 0 天典型航线毛利(产地买入价 vs 需求地卖出价) —— 必须为正
            var day0 = sim.Day;
            var routes = new (string a, string b, string g, string name)[]
            {
                ("nagasaki","guangzhou","silver","长崎白银→广州"),
                ("guangzhou","nagasaki","silk","广州丝绸→长崎"),
                ("goa","venice","pepper","果阿胡椒→威尼斯"),
                ("calicut","genoa","ginger","卡利卡特生姜→热那亚"),
                ("mocha","istanbul","coffee","摩卡咖啡→伊斯坦布尔"),
                ("ternate","surat","cloves","特尔纳特丁香→苏拉特"),
                ("colombo","bordeaux","cinnamon","科伦坡肉桂→波尔多"),
                ("elmina","seville","gold","埃尔米纳黄金→塞维利亚"),
                ("ambon","lisbon","nutmeg","安汶肉豆蔻→里斯本"),
                ("hamburg","athens","amber","汉堡琥珀→雅典"),
                ("quanzhou","busan","tea","泉州茶叶→釜山"),
                ("veracruz","seville","cocoa","韦拉克鲁斯可可→塞维利亚"),
            };
            foreach (var r in routes)
            {
                int ask = sim.AskPrice(sim.W.FindPort(r.a), sim.W.GoodById[r.g]);
                int bid = sim.BidPrice(sim.W.FindPort(r.b), sim.W.GoodById[r.g]);
                int unit = bid - ask;
                bool ok = unit > 0;
                if (ok) Console.WriteLine($"   ✓ {r.name}: 产地买 {ask} → 销地卖 {bid}, 单件毛利 {unit}");
                else { _errors++; Console.WriteLine($"   ✗ {r.name}: 毛利 {unit} (买{ask}/卖{bid})"); }
            }

            // 威尼斯瘟疫(第730天起120天, 止于850) → 封锁
            sim.AdvanceDays(760);
            var venice = sim.W.FindPort("venice");
            Check(venice.Blocked, $"第{sim.Day}天 威尼斯应处于封锁(瘟疫窗口内)");
            var trade = sim.Buy(venice, sim.W.GoodById["gold"], 5, 100000);
            Check(trade.Units == 0, "封锁中的威尼斯不应能交易");
            Console.WriteLine($"   封锁验证: 威尼斯 Blocked={venice.Blocked}, 当前 {sim.Year}年{sim.Month}月");

            sim.AdvanceDays(120); // 越过 850, 瘟疫解除
            Check(!sim.W.FindPort("venice").Blocked, $"第{sim.Day}天 瘟疫结束, 威尼斯应恢复交易");

            // 香料群岛台风(520..595) 特尔纳特丁香应显著上涨(与事件前对比)
            var ternate = sim.W.FindPort("ternate");
            var s2 = new SimEngine(sim.W, sim.T, 777);
            float before = s2.CurrentRatio(ternate, sim.W.GoodById["cloves"]);
            s2.AdvanceDays(540);
            float after = s2.CurrentRatio(ternate, sim.W.GoodById["cloves"]);
            Check(after > before + 0.15f, $"台风事件应推高特尔纳特丁香价: {before:F2}→{after:F2}");
            Console.WriteLine($"   台风事件: 特尔纳特丁香 {before:F2} → {after:F2} (eqMul 1.9)");
            Console.WriteLine("   场景断言完成");
        }

        // ---------- 数值健康 ----------
        static void RunPriceIntegrity(SimEngine sim)
        {
            Console.WriteLine();
            Console.WriteLine("== 数值健康: 再跑 2 年 ==");
            int badCell = 0, badRatio = 0;
            for (int i = 0; i < 720; i++)
            {
                sim.AdvanceDays(1);
                foreach (var p in sim.W.Ports)
                {
                    foreach (var c in sim.W.Market[p.Id])
                    {
                        if (float.IsNaN(c.R) || float.IsNaN(c.Eq) || float.IsNaN(c.Stock)) badCell++;
                        if (c.R < sim.T.PriceRatioMin - 0.001f || c.R > sim.T.PriceRatioMax + 0.001f) badRatio++;
                        if (c.Stock < -0.001f || c.Stock > c.Cap + 0.001f) badCell++;
                        if (c.R < 0.01f) badRatio++;
                    }
                }
            }
            Check(badCell == 0, $"数值越界格 {badCell}");
            Check(badRatio == 0, $"价格钳制失效 {badRatio}");
            Console.WriteLine($"   模拟至 {sim.Year}年{sim.Month}月 第{sim.Day}天, 逐日tick正常; 活跃事件 {sim.ActiveEvents().Count} 条");
            foreach (var e in sim.ActiveEvents())
                Console.WriteLine($"     [在发] {e.D.name}");
        }

        // ---------- 倾销砸价 + 商人 Bot ----------
        static void RunBot(World world, MarketTuning tuning)
        {
            Console.WriteLine();
            Console.WriteLine("== 倾销会砸价 ==");
            var eng = new SimEngine(world, tuning, 555);
            var london = world.FindPort("london");
            var wine = world.GoodById["wine"];
            float rBefore = eng.CurrentRatio(london, wine);
            var cargo = new List<(Port p, Good g)>();
            for (int i = 0; i < 40; i++) cargo.Add((london, wine));
            foreach (var (p, g) in cargo) eng.Sell(p, g, 1);
            float rAfter = eng.CurrentRatio(london, wine);
            Check(rAfter < rBefore, $"倾销40件后伦敦酒价应从 {rBefore:F3} 降到 {rAfter:F3}");
            Console.WriteLine($"   伦敦葡萄酒 r: {rBefore:F3} → {rAfter:F3}");

            Console.WriteLine();
            Console.WriteLine("== 商人 Bot: 1 年(每5天看一次行情, 买特产卖需求) ==");
            var bot = new MerchantBot(eng, 30000, 60, 123);
            bot.Run(360);
            Console.WriteLine($"   Bot 终局: 现金 {bot.Cash}  净资产 {bot.Cash}  共成交 {bot.Trades} 笔");
            Check(bot.Cash > 30000, "Bot 应通过做差价盈利(>启动资金)");
        }

        // ---------- M1 舰队/船员/补给(结构 + 计时剧本) ----------
        static readonly string[] BuffWhitelist =
        {
            "moraleCap","battleWin","buyDiscount","sellBonus","ransomCut","stormLossCut",
            "sightMul","fireMul","scurvyLossMul","treatCostCut","refitCostCut","speedAdd",
            "provisionCostCut","foodUseMul","monsoonSpeedAdd","boardingPower","freeIntel"
        };

        static void RunFleetChecks(AreaD[] areas, PortD[] ports, ShipD[] ships, CrewD[] crews, FleetTuningD tuneD)
        {
            Console.WriteLine();
            Console.WriteLine("== M1 舰队/船员 ==");

            var areaIds = new HashSet<string>();
            foreach (var a in areas) areaIds.Add(a.id);
            var portIds = new HashSet<string>();
            foreach (var pt in ports) portIds.Add(pt.id);
            var buffOk = new HashSet<string>(BuffWhitelist);

            // ---------- 结构 ----------
            Check(ships.Length == 15, $"船型应为 15, 实际 {ships.Length}");
            var seen = new HashSet<string>();
            foreach (var s in ships)
            {
                Must(seen.Add(s.id), $"船 id 重复 {s.id}");
                Must(s.type == "merchant" || s.type == "warship" || s.type == "scout", $"船 {s.id} type 非法 {s.type}");
                Must(s.tier >= 1 && s.tier <= 4, $"船 {s.id} tier {s.tier} 越界");
                Must(s.crewMin <= s.crewMax, $"船 {s.id} crewMin>crewMax");
                Must(s.capacity > 0 && s.speed > 0 && s.price > 0 && s.armor >= 0, $"船 {s.id} 载重/航速/价格/护甲非法");
                Must(s.refitSlots >= 0, $"船 {s.id} refitSlots<0");
                foreach (var au in s.areaUnlock ?? Array.Empty<string>())
                    Must(areaIds.Contains(au), $"船 {s.id} 入手航区 {au} 不存在");
            }

            var crewSeen = new HashSet<string>();
            foreach (var c in crews)
            {
                Must(crewSeen.Add(c.id), $"船员 id 重复 {c.id}");
                Must(portIds.Contains(c.originPort), $"船员 {c.id} 出身港 {c.originPort} 不存在");
                Must(c.stat != null && AllStatIn(c.stat, 40, 95), $"船员 {c.id} stat 应全在 40..95");
                Must(c.salary > 0, $"船员 {c.id} salary<=0");
                foreach (var ap in c.hire?.atPorts ?? Array.Empty<string>())
                    Must(portIds.Contains(ap), $"船员 {c.id} 招募港 {ap} 不存在");
                foreach (var va in c.hire?.visitedAreaIds ?? Array.Empty<string>())
                    Must(areaIds.Contains(va), $"船员 {c.id} 拜访航区 {va} 不存在");
                if (!string.IsNullOrEmpty(c.hire?.soldAtPortId))
                    Must(portIds.Contains(c.hire.soldAtPortId), $"船员 {c.id} soldAtPortId {c.hire.soldAtPortId} 不存在");
                foreach (var b in c.buffs ?? Array.Empty<CrewBuffD>())
                    Must(b != null && buffOk.Contains(b.kind), $"船员 {c.id} buff kind 非法 {b?.kind}");
            }
            Check(crews.Length == 10 && crewSeen.Count == 10, $"船员应为 10 且 id 唯一, 实际 {crewSeen.Count}");

            Must(tuneD != null && tuneD.fleetMaxShips == 5, "FleetTuning.fleetMaxShips 应为 5");
            Must(tuneD.crew != null && tuneD.crew.sailorSalaryPerSettlement > 0 && tuneD.crew.recruitCostBase > 0, "FleetTuning.crew 量纲非法");
            foreach (var pk in new[] { tuneD.provision?.food, tuneD.provision?.water, tuneD.provision?.tea })
                Must(pk != null && pk.unitWeight > 0 && pk.price > 0 && pk.perCrewPerDay > 0, "补给(食/水/茶) 单位重/单价/日耗 须 >0");
            var sumW = (tuneD.provision?.food?.unitWeight ?? 0f) + (tuneD.provision?.water?.unitWeight ?? 0f)
                     + (tuneD.provision?.tea?.unitWeight ?? 0f);
            Must(Math.Abs(sumW - 0.06f) < 1e-5f, $"三给养份重合计应 0.06 载/人·日(实际 {sumW})");
            var kinds = tuneD.refit?.kinds ?? Array.Empty<RefitKindD>();
            Check(kinds.Length == 4, $"改造应 4 类, 实际 {kinds.Length}");
            foreach (var k in kinds)
            {
                Must(k != null && k.costMinPct > 0 && k.costMaxPct >= k.costMinPct, $"改造 {k?.kind} costPct 区间非法");
                Must(k != null && k.effectMin > 0 && k.effectMax >= k.effectMin, $"改造 {k?.kind} effect 区间非法");
            }
            var pir = tuneD.pirate;
            Check(pir != null && pir.dangerByArea.Length == 8, "危险度表应为 8 航区");
            var dangAreas = new HashSet<string>();
            foreach (var d in pir?.dangerByArea ?? Array.Empty<DangerByAreaD>())
            {
                Must(areaIds.Contains(d.areaId) && dangAreas.Add(d.areaId), $"危险度表航区 {d.areaId} 非法/重复");
                Must(d.danger > 0 && d.danger <= 1, $"危险度 {d.areaId}={d.danger} 越界 (0,1]");
            }
            Must(pir.pursuitSpeedFactor > 1, "pursuitSpeedFactor 应>1");
            Must(pir.ransomMinPct > 0 && pir.ransomMinPct <= pir.ransomMaxPct && pir.ransomMaxPct < 1, "赎金比例区间非法");
            Must(pir.cargoLossLosePct > 0 && pir.cargoLossLosePct < 1, "cargoLossLosePct 越界");
            Check(AnchorsOk(pir.attackWinAnchors) && AnchorsOk(pir.defenseWinAnchors), "胜负率锚点应随比值单调递增且 chance∈(0,1]");

            // ---------- 目录装载 ----------
            var cat = FleetCatalog.Build(ships, crews, tuneD);
            Check(cat.Tuning.FleetMaxShips == 5 && cat.Ships.Count == 15 && cat.Crews.Count == 10, "目录装载应 15 舰 / 10 船员");

            // ---------- ④A 硬续航 ----------
            {
                var f = FleetBuilder.Start(cat, "m2_caravel", 80, 1_000_000,
                    new ProvisionStock { Food = 3600f, Water = 100000f, Tea = 100000f });
                Check(FleetOps.EnduranceDays(f) == 45, $"A: 卡拉维尔 crew80 +45 日食物 → 续航 45(实际 {FleetOps.EnduranceDays(f)})");
            }

            // ---------- ④B 断粮 ----------
            {
                var f = FleetBuilder.Start(cat, "m2_caravel", 80, 1_000_000,
                    new ProvisionStock { Food = 1600f, Water = 100000f, Tea = 100000f });
                Check(FleetOps.EnduranceDays(f) == 20, $"B: 食物只够 20 日(实际 {FleetOps.EnduranceDays(f)})");
                for (int i = 0; i < 20; i++) FleetOps.AdvanceOneSeaDay(f);
                Check(Math.Abs(f.Morale - 85f) < 0.01f, $"B: 第 20 日断粮士气 -15 → 85(实际 {f.Morale})");
                int crewBefore = f.TotalCrew();
                FleetOps.AdvanceOneSeaDay(f);
                Check(f.TotalCrew() < crewBefore, $"B: 断粮次日起减员 {crewBefore} → {f.TotalCrew()}");
            }

            // ---------- ④C 缺茶 / 断水计时(潜伏期后各自主惩罚) ----------
            {
                var f = FleetBuilder.Start(cat, "m2_caravel", 80, 1_000_000,
                    new ProvisionStock { Food = 100000f, Water = 100000f, Tea = 1f });
                for (int i = 0; i < 2; i++) FleetOps.AdvanceOneSeaDay(f);
                Check(Math.Abs(f.Morale - 100f) < 0.01f, $"C: 缺茶潜伏期(前 2 日)士气不变(实际 {f.Morale})");
                FleetOps.AdvanceOneSeaDay(f);
                Check(Math.Abs(f.Morale - 97f) < 0.01f, $"C: 缺茶≥3 日后每日士气 -3 → 第 3 日 97(实际 {f.Morale})");
            }
            {
                var f = FleetBuilder.Start(cat, "m2_caravel", 80, 1_000_000,
                    new ProvisionStock { Food = 100000f, Water = 1f, Tea = 100000f });
                for (int i = 0; i < 3; i++) FleetOps.AdvanceOneSeaDay(f);
                Check(Math.Abs(f.Morale - 95f) < 0.01f && f.Sick == 1 && f.Career.ScurvyEver,
                    $"C: 断水第 3 日士气 -5 且 +1 病号(士气{f.Morale}/病号{f.Sick})");
                for (int i = 0; i < 4; i++) FleetOps.AdvanceOneSeaDay(f);   // 共第 7 日
                Check(f.Sick == 3 && Math.Abs(f.Morale - 75f) < 0.01f,
                    $"C: 断水第 7 日 病号 3、士气 75(病号{f.Sick}/士气{f.Morale})");
            }

            // ---------- 结薪=按进港; 海上日不扣薪 ----------
            {
                var f = FleetBuilder.Start(cat, "m2_caravel", 80, 1_000_000,
                    new ProvisionStock { Food = 100000f, Water = 100000f, Tea = 100000f });
                for (int i = 0; i < 5; i++) FleetOps.AdvanceOneSeaDay(f);
                Check(f.Gold == 1_000_000, "海上 5 日不产生工资");
                long wage = FleetOps.SettleAtPort(f, "seville");
                Check(wage == 640, $"进港结薪: 80 水手×8 = 640(实际 {wage})");
                Check(f.Gold == 1_000_000 - 640, "进港后金库扣 640");
            }

            // ---------- 招募门槛 + 结薪含 NPC ----------
            {
                var f = FleetBuilder.Start(cat, "m2_caravel", 80, 1_000_000, new ProvisionStock { });
                var marta = cat.Crews["crew_marta"];
                f.Career.SoldBoxes = 49;
                Check(!FleetOps.RecruitAvailable(cat, marta, f.Career, "amsterdam"), "玛尔塔: 累计售 49 件 → 拒聘");
                f.Career.SoldBoxes = 50;
                Check(FleetOps.RecruitAvailable(cat, marta, f.Career, "amsterdam"), "玛尔塔: 累计售 50 件 → 可聘");
                Check(FleetOps.Hire(f, "crew_marta", "amsterdam"), "Hire 玛尔塔成功");
                Check(f.Bonus.ApplyAsk(100) == 96, $"玛尔塔 -4% 买价: Ask(100)=96(实际 {f.Bonus.ApplyAsk(100)})");
                long wage = FleetOps.SettleAtPort(f, "seville");
                Check(wage == 640 + 900, $"结薪含在册 NPC: 640+900=1540(实际 {wage})");
            }

            // ---------- FleetBonus 聚合: 玛尔塔+6%卖 / 萨利玛+5%卖 → +11%, freeIntel ----------
            {
                var f = FleetBuilder.Start(cat, "m2_caravel", 80, 1_000_000, new ProvisionStock { });
                f.Career.SoldBoxes = 50;
                f.Career.SoldAtPort["calicut"] = 5;
                Check(FleetOps.Hire(f, "crew_marta", "amsterdam"), "聚合前置: 招玛尔塔");
                Check(FleetOps.Hire(f, "crew_salima", "calicut"), "聚合前置: 招萨利玛");
                Check(Math.Abs(f.Bonus.SellBonus - 0.11f) < 1e-4f, $"卖价加成应 0.06+0.05=0.11(实际 {f.Bonus.SellBonus:F3})");
                Check(f.Bonus.FreeIntel, "freeIntel 聚合应置位");
                Check(f.Bonus.ApplyBid(100) == 111, $"Bid(100)×(1+0.11)=111(实际 {f.Bonus.ApplyBid(100)})");
            }

            // ---------- 改造: 费=船价×costMin, 槽满拒 ----------
            {
                var f = FleetBuilder.Start(cat, "m2_caravel", 80, 1_000_000, new ProvisionStock { });
                var s = f.Flagship;  // m2_caravel: price45000, refitSlots2
                int capBase = s.EffCapacity;
                var r1 = FleetOps.RefitCalc(f, s, RefitKind.Capacity);
                Check(r1 != null && r1.Cost == 6750, $"改造费应 45000×0.15=6750(实际 {r1?.Cost})");
                Check(s.EffCapacity > capBase, $"改造容量生效 {capBase} → {s.EffCapacity}");
                FleetOps.RefitCalc(f, s, RefitKind.Capacity);
                Check(s.AppliedCount == 2, $"refit 槽位计数 2(=refitSlots), 实际 {s.AppliedCount}");
                var r3 = FleetOps.RefitCalc(f, s, RefitKind.Fire);
                Check(r3 == null && s.AppliedCount == 2, "槽满后再改造应被拒");
            }

            // ---------- 海盗: 遭遇/胜率量纲 + 避战 + 同 seed 同结果 ----------
            {
                float e0 = FleetFormulas.EncounterChance(0f, 0L);
                float em = FleetFormulas.EncounterChance(0.8f, 20000L);
                float e1 = FleetFormulas.EncounterChance(1f, 50000L);
                Check(e0 >= 0f && e1 <= 1f + 1e-6f && e0 <= em && em <= e1 + 1e-6f,
                    $"遭遇率 ∈[0,1] 且随危险度不减: {e0:F2}/{em:F2}/{e1:F2}");

                var anchors = cat.Tuning.DefenseAnchors;
                float prev = 0f; bool mono = true;
                foreach (var ratio in new[] { 0.5f, 0.9f, 1.0f, 1.2f, 1.6f })
                {
                    float wc = FleetFormulas.WinChance(anchors, ratio);
                    if (wc < prev - 1e-6f) mono = false;
                    prev = wc;
                }
                Check(mono && prev > 0f && prev <= 1f, $"防守胜率随攻防比单调不减(末点 {prev:F2})");

                var fleeF = FleetBuilder.Start(cat, "s1_sloop", 10, 100000, new ProvisionStock { });
                var flee = FleetOps.AutoResolve(fleeF, "med", 9999, 7.0f, 42);
                Check(flee.Escaped, $"全队最高速 9 ≥ 敌速7×1.15 → 应避战(实际 Escaped={flee.Escaped})");

                var f = FleetBuilder.Start(cat, "m2_caravel", 80, 1_000_000, new ProvisionStock { });
                int put = FleetOps.AddCargo(f, "tea", 50);
                Check(put == 50, $"装货应满 50(实际 {put})");
                var o1 = FleetOps.AutoResolve(f, "westAfrica", 5000, 20f, 7);
                var f2 = FleetBuilder.Start(cat, "m2_caravel", 80, 1_000_000, new ProvisionStock { });
                FleetOps.AddCargo(f2, "tea", 50);
                var o2 = FleetOps.AutoResolve(f2, "westAfrica", 5000, 20f, 7);
                Check(o1.Defeated && o1.CargoLostUnits == o2.CargoLostUnits && o1.RansomPaid == o2.RansomPaid,
                    $"同 seed 同结果(Defeated={o1.Defeated}, 货损 {o1.CargoLostUnits}, 赎金 {o1.RansomPaid})");
                Check(o1.Defeated && o1.CargoLostUnits >= 1 && f.Morale < 100f, "落败应丢货且士气下降");
            }

            Console.WriteLine("   舰队规则检查完成");
        }

        static bool AnchorsOk(AnchorD[] a)
        {
            if (a == null || a.Length == 0) return false;
            float prevR = float.MinValue, prevC = 0f;
            foreach (var x in a)
            {
                if (x.chance <= 0f || x.chance > 1f) return false;
                if (x.ratio <= prevR || x.chance <= prevC) return false;
                prevR = x.ratio; prevC = x.chance;
            }
            return true;
        }

        static bool AllStatIn(CrewStatD s, int lo, int hi)
        {
            return s.nav >= lo && s.nav <= hi && s.fight >= lo && s.fight <= hi
                && s.trade >= lo && s.trade <= hi && s.med >= lo && s.med <= hi
                && s.charm >= lo && s.charm <= hi && s.lead >= lo && s.lead <= hi;
        }

        // ---------- 工具 ----------
        static void Check(bool cond, string msg)
        {
            if (cond) Console.WriteLine("   ✓ " + msg);
            else { _errors++; Console.WriteLine("   ✗ FAIL: " + msg); }
        }

        static void Must(bool cond, string msg)   // 数据校验: 失败才输出
        {
            if (!cond) { _errors++; Console.WriteLine("   ✗ FAIL: " + msg); }
        }
    }

    // 简化商人: 不建模航行, 只验证"买低卖高"能持续运转且价格机制稳定
    internal sealed class MerchantBot
    {
        readonly SimEngine _e;
        public int Cash;
        readonly int _cap;
        readonly Random _rng;
        public int Trades;

        public MerchantBot(SimEngine e, int startCash, int capacity, int seed)
        {
            _e = e; Cash = startCash; _cap = capacity; _rng = new Random(seed);
        }

        public void Run(int days)
        {
            for (int d = 0; d < days; d++)
            {
                if (_e.Day % 5 == 0) TradeOnce();
                _e.AdvanceDays(1);
            }
        }

        void TradeOnce()
        {
            // 随机挑一个特产港的某个特产, 找一个需求该货且卖价最高的港口出货
            var p = _e.W.Ports[_rng.Next(_e.W.Ports.Count)];
            if (p.Specialties.Length == 0) return;
            var gid = p.Specialties[_rng.Next(p.Specialties.Length)];
            var g = _e.W.GoodById[gid];

            Port best = null; int bestBid = -1;
            foreach (var q in _e.W.Ports)
            {
                if (q.Id == p.Id) continue;
                int bid = _e.BidPrice(q, g);
                if (bid > bestBid) { bestBid = bid; best = q; }
            }
            if (best == null) return;

            int ask = _e.AskPrice(p, g);
            if (bestBid <= ask) return;               // 无利可图
            int qty = Math.Min(_cap, Math.Min(24, _e.BuyStock(p, g)));
            var bought = _e.Buy(p, g, qty, Cash);
            if (bought.Units <= 0) return;
            Cash -= bought.Gold;
            var sold = _e.Sell(best, g, bought.Units);
            Cash += sold.Gold;
            Trades += bought.Units;
        }
    }
}
