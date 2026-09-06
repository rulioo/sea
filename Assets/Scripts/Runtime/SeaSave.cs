using System;
using System.Collections.Generic;

namespace Sea
{
    // =============================================================
    // SEA · 存档结构(JsonUtility 友好: [Serializable] + public 字段, 无 Dictionary)
    //   真身存成文本文件放 Application.persistentDataPath/sea_save.json
    // =============================================================
    [Serializable]
    public class SeaSaveData
    {
        public int version = 1;

        // 时钟 / 位置 / 试炼
        public int day;                 // 引擎日历第几天(自 1550-01-01)
        public string portId;           // 当前所在港
        public long startWorth;         // 开局身家(试炼进度跨档延续)

        // 舰队
        public long gold;
        public int morale;
        public int sick;
        public int foodOutDays, waterOutDays, teaOutDays;
        public float food, water, tea;
        public List<SeaShipSave> ships = new List<SeaShipSave>();

        // 持仓成本账本(件均成本用于行情表显示"成本"列; 卖出/被劫自动摊薄)
        public List<SeaBasisSave> basis = new List<SeaBasisSave>();

        // 全港行情快照: 按 Ports 顺序 × Goods 顺序铺平
        public float[] eq;
        public float[] r;
        public float[] stock;

        // 情报站刊物档案(我的订阅): 随存读档
        public List<SeaIntelSave> intel = new List<SeaIntelSave>();

        // 地图旗标(设置开关): 大本营黄旗 + 到过的城市红旗, 随存读档
        public bool flagsOn = true;
        public List<string> flagVisited = new List<string>();

        // 黑雾遮罩: 已探明海图网格(位图→base64; 空 = 尚未探索 / 旧档)
        public string fog = "";
    }

    [Serializable]
    public class SeaIntelSave
    {
        public string kind;       // "paper" 贸易日报(正文快照) / "report" 商情报(订阅记录)
        public int key;           // paper=engine.Day / report=月份键(Year*12+Month)
        public string title;      // 封面题(日期 / 年月)
        public string sub;        // 副题(刊价 · 期别)
        public string body;       // paper: 当期正文(块分隔); report: 空
        public long paid;         // 实付金
    }

    [Serializable]
    public class SeaBasisSave
    {
        public string good;
        public long qty;
        public long cost;
    }

    [Serializable]
    public class SeaShipSave
    {
        public string model;
        public int crew;
        public int damage;
        public List<SeaCargoSave> cargo = new List<SeaCargoSave>();
    }

    [Serializable]
    public class SeaCargoSave
    {
        public string good;
        public int qty;
    }
}
