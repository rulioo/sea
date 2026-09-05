# SEA · 大航海时代4 玩法复刻(Unity 工程骨架)

用 Unity3D 复刻《大航海时代4》的**核心玩法骨架**:一张世界地图,每个港口的
物产特点与当地百姓需求构成贸易基础;随时间推移需求与价格持续变化,并被
**大事件**与**季节**扰动。本项目为**玩法神似、内容原创**的教学骨架 —— 全部
港口/商品/事件/调参均为原创设计,不含原作角色、剧情或素材,可直接商用延伸。

> 完整设计见 [docs/GameDesign.md](docs/GameDesign.md)。
> 需求清单见 [req.txt](req.txt)。

---

## 一、目录结构

```
req.txt                     需求(设计输入)
docs/GameDesign.md          设计文档(玩法/数据/经济/架构/路线图)
Assets/
  Resources/SeaData/        数据唯一真源(全部 .json, TextAsset)
    Goods.json              39 种商品
    Areas.json              8 大航区
    Ports.json              52 港(经纬/规模/气候/特产/需求/描述)
    Events.json             16 条大事件规则
    MarketTuning.json       经济参数(角色均衡价、季节曲线、库存等)
  Scripts/
    Sea.Core/               纯 C# 经济核心(零 UnityEngine 依赖)
      Sea.Core.asmdef       noEngineReferences: true
      SeaData.cs            DTO 契约(与 JSON 一一对应)
      SeaEngine.cs          世界/市场/价格/交易/事件/时间(1550 起, 360日/年)
    Runtime/                Unity 运行时(装载 + 世界地图 + 调试台)
      Sea.Runtime.asmdef
      SeaDataJson.cs        Resources → JsonUtility → World/SimEngine
      SeaGame.cs            地图标记、点击看行情、时间推进、自动播放
    Editor/                 Unity 编辑器工具
      Sea.Editor.asmdef
      SeaMenu.cs            菜单 SEA: 校验数据 / 搭建主场景
  Scenes/                   (由菜单"搭建主场景"生成 Main.unity)
dev/SimCheck/               无头验证(纯 C# 跑经济引擎)
  Program.cs / SimCheck.csproj
art_design/                 原创美术设定稿(文字概念稿, 可交画师/AI 出图)
  README.md                 风格口径·色板·模板·生产清单
  characters/               主角/十船员/港口众生
  props/                    航海道具/货品包装/补给与装备
```

## 二、怎么跑

### 1) 验证经济引擎(无需 Unity,需 .NET 8)

```bash
cd dev/SimCheck
dotnet run -c Release
# 期望末尾输出: RESULT: ALL CHECKS PASSED
```

会校验:数据结构完整性、12 条经典航线正收益、瘟疫封港→解封、
台风抬价(特尔纳特丁香 0.60→1.03)、价格无 NaN/越界、倾销压价、
商人 Bot 盈利,共 ~1600+ 模拟日。

### 2) 在 Unity 中看地图与行情

1. Unity Hub → Add → 本目录 → 用 **Unity 6000.0.30f1** 打开。
   > 第一次打开会做资源导入(稍等)。若报脚本错误请先看步骤 3。
2. 菜单 **SEA ▸ 搭建主场景 Main.unity**(自动建相机+光+SeaGame 并存到 `Assets/Scenes/Main.unity`)。
3. 点击 **Play**:
   - 世界地图上 52 港按航区着色(左上角为对应色系)。
   - **左键点港口** → 右侧出现该港行情面板(特产/需求、进/出价、库存)。
   - 顶部时钟: **+1日 / +7日 / +30日 / 自动播放**。
   - 右键拖动转视角、滚轮缩放、中键平移。
   - 红色大字是当前进行中的大事件。
   - 若没建场景直接 Play,组件也会自动生成(AutoBoot)。

> 地图只是"把经纬度摊到 3D"的占位示意,正式版可用真实海图/着色器/UI 地图替换。

### 3) 数据自检(编辑器内)

菜单 **SEA ▸ 校验数据** → 立即跑 结构完整性 + 推进一整年 + 买一卖一。

---

## 三、加新内容(全部数据驱动, 改 JSON 即可)

| 想改什么 | 改哪个文件 | 注意 |
|---|---|---|
| 新商品 | `Goods.json` | 字段 `id/name/en/cat/need/basePrice/weight/desc`, `need` ∈ necessity/material/cloth/indulgence/luxury |
| 新港口 | `Ports.json` | `areaId` 须存在于 `Areas.json`; `climate` ∈ cold/temp/tropic/tempS; `specialties`=本港产出, `imports`=百姓需求 |
| 新航区 | `Areas.json` | 需给若干港口的 `areaId` 引用它, 否则是空航区 |
| 新大事件 | `Events.json` | 见 `docs/GameDesign.md §7` 与文件内注释; `schedule.mode` ∈ once/yearly/period |
| 经济手感 | `MarketTuning.json` | 均衡价倍率/回归速度/季节曲线/库存参数, 字段注释见 DTO |

改完重新跑 **SimCheck**(决定性的完整性闸门)+ 编辑器 **SEA ▸ 校验数据**。

---

## 四、经济模型一句话

每港×每货一个 `MarketCell`,由"角色"决定长期均衡价倍率:
**本港特产**便宜(`0.60`)利于进货, **百姓需求**价高(`1.30–1.72` 按需求层级)利于出货,
无关商品居中。每天价格向"均衡 × 季节(仅珍品/嗜好) × 事件"回归,买卖行为会反向
推动价格(买多涨、倾销跌),库存快慢回补 —— 因此做差价、看航线、踩事件就是全部玩法。
详细公式见 [docs/GameDesign.md §8](docs/GameDesign.md)。

---

## 五、Roadmap(设计文档 §9 的落地点)

- M0(本仓库): 数据 + 核心引擎 + SimCheck + 工程骨架 ✓
- M1: 航线 UI(两地差价提示)、船/舱位/货量、城市界面
- M2: 舰队移动与自动贸易脚本、声望/占有率
- M3: 海战简版、剧情大事件可视化、存档读档
