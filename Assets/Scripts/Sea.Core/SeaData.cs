using System;

namespace Sea
{
    // =============================================================
    // 数据契约 (Data Transfer Objects)
    // 字段名与 Assets/Data/*.json 一一对应 (小驼峰)。
    // 不引用 UnityEngine，可用 System.Text.Json(dotnet) 与
    // Unity JsonUtility 双端反序列化。
    // =============================================================

    [Serializable]
    public class GoodD
    {
        public string id;
        public string name;
        public string en;
        public string cat;        // 展示用大类: food/drink/material/cloth/craft/precious/tropical/spice/farEast
        public string need;       // 需求类型: necessity/material/cloth/indulgence/luxury
        public int basePrice;
        public int weight;        // 占货舱容量
        public string desc;
    }

    [Serializable]
    public class AreaD
    {
        public string id;
        public string name;
        public string en;
        public string tier;       // 入门/中级/高级/终极
        public string desc;
    }

    [Serializable]
    public class PortD
    {
        public string id;
        public string name;
        public string en;
        public string areaId;
        public float lat;
        public float lon;
        public int size;          // 1 小镇 / 2 中型 / 3 大都会
        public string climate;    // cold / temp / tropic / tempS
        public string[] specialties;   // 本港物产(便宜好进货)
        public string[] imports;       // 百姓需求(贵、好出货)
        public string desc;
    }

    [Serializable]
    public class EventScopeD
    {
        public string[] areas;
        public string[] ports;
        public string[] goods;
        public string[] needClasses;
    }

    [Serializable]
    public class EventEffectD
    {
        public string role;       // "" 全部 / producer / importer
        public float eqMul;       // 目标价倍率乘数 (>1 涨 / <1 跌)
        public float refillMul;   // 库存回补倍率 (<1 减产缺货)
        public bool block;        // true = 禁止该港交易
    }

    [Serializable]
    public class EventScheduleD
    {
        public string mode;       // once / yearly / period
        public int dayOffset;     // once
        public int duration;      // 事件持续天数
        public int monthStart;    // yearly 起始月份 1..12
        public int firstDay;      // period 首次发生日
        public int periodDays;    // period 周期天数
    }

    [Serializable]
    public class EventD
    {
        public string id;
        public string name;
        public string desc;
        public bool telegraph;    // 是否在酒馆上"传闻"
        public EventScopeD scope;
        public EventEffectD effect;
        public EventScheduleD schedule;
    }

    [Serializable]
    public class MarketTuningD
    {
        public int version;
        public float sellSpreadFactor;
        public float ratioRegressionPerDay;
        public float priceRatioMin;
        public float priceRatioMax;
        // eq(目标价倍率) 基础值按"角色"取值 —— 本港特产/百姓需求/无关
        public float specialtyEq;
        public float specialtyNeededEq;
        public float needNecessityEq;
        public float needClothEq;
        public float needMaterialEq;
        public float needIndulgenceEq;
        public float needLuxuryEq;
        public float neutralEq;
        public float buyRatioPushPerUnit;
        public float sellRatioPushPerUnit;
        public float eqDriftSigma;
        public float eqMeanPull;
        public MarketStockD stock;
        public SeasonPremiumMulD seasonPremiumMul;
    }

    [Serializable]
    public class MarketStockD
    {
        public float[] producerCapBySize;  // 下标 0.. 对应 size 1..3 的产港库存上限
        public float[] otherCapBySize;     // 非产港库存上限
        public float producerRestockPerDay;
        public float otherRestockPerDay;
    }

    [Serializable]
    public class SeasonPremiumMulD
    {
        // 下标 0..11 = 1..12 月。对 need∈{indulgence,luxury} 的行情季节倍率。
        public float[] cold;
        public float[] temp;
        public float[] tropic;
        public float[] tempS;
    }

    // ---------- M1 舰队/船员 DTO(唯一真源 Ships.json/Crews.json/FleetTuning.json) ----------

    [Serializable]
    public class ShipD
    {
        public string id;
        public string name;
        public string en;
        public string type;      // merchant / warship / scout
        public int tier;         // 1..4
        public int slots;        // 同时可装货种上限
        public int capacity;     // 载货量上限
        public int crewMin;
        public int crewMax;
        public float speed;      // 节
        public int guns;
        public int fire;
        public int armor;
        public int price;
        public int refitSlots;   // 可改造槽位上限
        public string[] areaUnlock;  // 可入手航区 id
        public int year;
        public string desc;
    }

    [Serializable]
    public class CrewBuffD
    {
        public string kind;      // 白名单见 Fleet.cs 契约注释
        public float delta;
    }

    [Serializable]
    public class CrewStatD
    {
        public int nav;
        public int fight;
        public int trade;
        public int med;
        public int charm;        // 魅力
        public int lead;
    }

    [Serializable]
    public class CrewHireD
    {
        public string[] atPorts;
        public int minLevel;
        public int minSailDays;
        public int minSoldBoxes;
        public int minBattleWins;
        public int minRefitGold;
        public bool scurvySeen;
        public string[] visitedAreaIds;
        public string soldAtPortId;  // "" = 不限
        public int minSoldAtPort;
    }

    [Serializable]
    public class CrewD
    {
        public string id;
        public string name;
        public string en;
        public string originPort;  // 故乡(映射本项目真实港口 id)
        public string role;
        public CrewStatD stat;
        public CrewHireD hire;
        public int salary;
        public CrewBuffD[] buffs;
        public string bio;
        public string tone;
    }

    [Serializable]
    public class ProvisionItemD
    {
        public float unitWeight;     // 每单位占载重
        public float price;          // 每单位补给采购价
        public float perCrewPerDay;  // 每水手每日消耗(单位数)
    }

    [Serializable]
    public class ProvisionProfileD
    {
        public ProvisionItemD food;
        public ProvisionItemD water;
        public ProvisionItemD tea;
    }

    [Serializable]
    public class CrewTuningD
    {
        public float recruitCostBase;        // 招募一名普通水手基础花费
        public float sailorSalaryPerSettlement;  // 进港结算每水手工资
        public int moraleMax;
    }

    [Serializable]
    public class RefitKindD
    {
        public string kind;          // capacity / fire / armor / speed
        public float costMinPct;     // 费用=船价×[costMinPct, costMaxPct]
        public float costMaxPct;
        public float effectMin;      // capacity/fire/armor=加%、speed=加节
        public float effectMax;
    }

    [Serializable]
    public class RefitTuningD
    {
        public RefitKindD[] kinds;
    }

    [Serializable]
    public class DangerByAreaD
    {
        public string areaId;
        public float danger;         // 遭遇海盗基准概率
    }

    [Serializable]
    public class AnchorD
    {
        public float ratio;          // 攻防比值
        public float chance;         // 胜率
    }

    [Serializable]
    public class PirateTuningD
    {
        public DangerByAreaD[] dangerByArea;
        public float pursuitSpeedFactor;  // 逃生需 舰队最高速 ≥ 海盗速×此值
        public float ransomMinPct;
        public float ransomMaxPct;
        public float cargoLossLosePct;    // 战败损失货值比例
        public AnchorD[] attackWinAnchors;    // 我方进攻: 攻防比→胜率锚点
        public AnchorD[] defenseWinAnchors;   // 我方防守(避战): 攻防比→胜率锚点
    }

    [Serializable]
    public class FleetTuningD
    {
        public int version;
        public int fleetMaxShips;
        public CrewTuningD crew;
        public ProvisionProfileD provision;
        public RefitTuningD refit;
        public PirateTuningD pirate;
    }
}
