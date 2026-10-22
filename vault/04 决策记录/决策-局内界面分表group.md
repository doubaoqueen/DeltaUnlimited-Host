---
title: 决策-局内界面分表group
tags: [决策, 识别, 数据表]
status: 现行
created: 2026-10-23
related: ["[[决策-双引擎架构]]", "[[标定-界面标记与分组]]", "[[模块-视觉识别]]", "[[坑-背包界面不是小地图]]", "[[数据-数据表清单]]"]
---

# 决策：局内界面用 `group` 字段分表

> [!info] 一句话
> 不给对局内界面单独建一张 `screens_match.json`，而是在 `data/screens.json` 里给每个界面加一个 `group` 字段（`lobby` 大厅 / `match` 局内），识别时只扫本组。**改动最小，省一半 OCR，还能让"局内认不出"走完全不同的兜底。**

## 一、问题从哪来

`screens.json` 原本只服务大厅：从特勤处到干员选择，一共 13 个界面。到了跑刀，对局内又冒出 4 个界面：

| 界面 id | 是什么 | 怎么认出它（严格关键词） |
|---|---|---|
| `match_backpack` | 按 Tab 打开的背包 | `本局收获` + `胸挂` + `背包`（三词齐备防串台） |
| `match_map` | 按 M 打开的地图屏 | `未开启` + `高价值点位` |
| `match_settlement_success` | 撤离成功结算 | `撤离点` + `本局收获` |
| `match_settlement_fail` | 撤离失败/被淘汰结算 | `淘汰者` + `对局时长` |

如果全塞进一张表、每次识别都扫全部 17 个界面，会有两个问题：

1. **浪费**：一次 `Detect` 约 7 次 OCR、600-700ms；局内链路每次只要认那 4 个，多扫 13 个纯属白烧；
2. **危险**：大厅的"认不出"和局内的"认不出"，**处理方式完全不同**（下一节）。

## 二、真正的关键：局内 unknown 不能说"按 Tab"

进场链路 v7 有一个兜底：**未知界面 → 按 Tab 回特勤处**，靠 `switch_screen` 的 `default` 分支 + 熔断（`max_loops=3`）。

这在局内是**错的**：

- 局内按 Tab 会打开背包（或计分板），不会"回大厅"；
- 而且会把局内状态搅乱——本来只是认不出一帧，按了 Tab 之后背包界面盖住小地图，定位也跟着废。

所以局内的 unknown 兜底必须是：**停住 + 交人工**（`mark_unknown` 留证）。

> [!important] 这条是"进场链路不能直接复用到局内"的最重要一条
> 不是因为技术不同，是因为**同一个按键在两个场景里含义不同**。分表就是把这件事显式化。

## 三、两个方案怎么比

| 方案 | 优点 | 缺点 |
|---|---|---|
| **A. 独立 `screens_match.json`** | 概念上最干净，一眼知道"局内是另一套" | 加载器、schema 校验、`UnconfiguredScreens()` 诊断、fixture 回归全要两套；两套表迟早漂移（改了一边忘了另一边） |
| **B. `screens.json` 加 `group` 字段**（选了这个） | 改一张表、一套校验、一套诊断；`Detect(group:"match")` 一行过滤 | 表变长；靠字段注释维持"局内语义不同"的可见性 |

选 B 的直接理由就是工程提案里那句：**校验逻辑改动最小**。

## 四、落地长什么样

- 数据：`ScreenDef.Group`，默认值 `"lobby"`（老条目不用改，自动归大厅）；
- 识别：`ScreenDetector.Scan/Detect` 多一个可选参数 `group`——非空时只扫该组，空时保持旧行为（全表扫）；
- 判定：`InGroup()` 是纯函数，可单测；
- 调用方：局内链路与 `HudStateGate` 都传 `group: "match"`。

```csharp
// 局内只扫 match 组（示意，实际在 ScreenDetector 内部）
var guess = ScreenDetector.Detect(frame, screens, root, group: "match");
```

## 五、意外收获：`match_backpack` 兼职"HUD 状态门"

定位器需要知道"这一帧有没有小地图"。而**按 Tab 打开背包时，左上角不是小地图**（是背包 UI），定位器会把 UI 纹理当小地图去匹配，得到"高分但完全错误"的位置。

于是 `HudStateGate` 直接复用分表：先 `Detect(group:"match")`，命中 `match_backpack` / `match_map` / `match_settlement_*` 就判定"无小地图，跳过定位"。识别不到界面时 fail-open（当正常 HUD 处理），交给轨迹过滤兜底。

一个界面条目，同时服务了两条线——这是分表带来的额外便宜。

## 六、代价与限制

- **条目还很少**：只有 4 个。拾取面板、死亡/观战界面还没标定（K0 清单里列了，但没采到足够素材）→ ❓待确认；
- **`group` 只管"识别范围"**，不管"兜底动作"。局内 unknown 要停住，仍需要在局内 workflow 的 `default` 分支里写对——分表只是把语义差异摆到台面上；
- **默认值有坑**：新条目忘写 `group` 就会被当大厅界面，在局内被误扫。目前靠注释和评审兜着。

## 出处

- `docs/跑刀方案.md` §3（"对局内识别分表（关键）"：加 `group` 字段或独立文件、两条理由、局内 unknown ≠ 按 Tab）
- `docs/跑刀工程提案.md` §4（数据契约：`screens.json` 加 `group` 字段，链局内只扫 `match` 组）、§9 第 1 条（分表方式待拍板：group 字段 vs 独立文件，倾向 group）
- `docs/小地图定位P0验证.md` §7.5（HUD 状态门：用 `group:"match"` 识别界面 → 判断有无小地图）
- 数据：`data/screens.json`（13 个 `lobby` + 4 个 `match`，每条带实测注释）
- 代码：`DeltaUnlimited/Data/ScreenModels.cs`（`Group` 默认 `lobby` 与注释）、`Vision/ScreenDetector.cs`（`InGroup`/`Scan(group)`/`Detect(group)`）、`Vision/Localization/HudStateGate.cs`
- Git：`dc495e6`（分表字段上线）、`d209f5f`（match 组 4 界面标定）、`5bd910e`（HUD 状态门）
