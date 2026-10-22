---
title: AI-数据集与标注
tags: [AI, 数据集, 标注]
status: 现行
created: 2026-10-23
related: ["[[AI-训练流水线总览]]", "[[AI-模型与门禁]]", "[[AI-VLM初筛]]", "[[坑-验证集泄漏]]", "[[标定-可通行ROI]]", "[[数据-数据表清单]]"]
---

# AI-数据集与标注

> [!info] 一句话
> 本项目的数据集就是一本"**前方能不能走**"的图鉴：每张截图被人工打成三类之一，训练时按**采集会话**整段切分（防止相邻帧泄漏），另有一份独立采集的"期末考卷"叫金标集。

## 数据集在学什么：三分类

模型只看画面里一块固定区域（ROI，见 [[标定-可通行ROI]]），回答一个问题：**往前走会被挡住吗？**

| 类别 | 编号 | 含义 | 当前数量 |
|---|---|---|---|
| `passable` | 0 | 有可通行的地面/通路 | 1205 |
| `blocked` | 1 | 前方被挡：墙、箱子、栅栏、贴脸怼墙 | 488 |
| `no_ground` | 2 | **弃权**：天空、面板遮挡、一团模糊、看不清地面 | 43 |
| `background` | — | 负样本池（每 50 帧自动存一张），**不参与监督训练** | 4 |

合计 **1740 行**（`ai-training/datasets/passability/manifest.csv`）。注意 `no_ground` 不是"地面被删了"，它是"**我看不出来**"这一档——它让模型学会**承认自己看不出来**，比硬猜成 passable 安全得多。

> [!warning] 数量严重不均衡
> passable 是 blocked 的 2.5 倍、no_ground 的 28 倍。这不是错误，是现实（能走的地方本来就多）。训练端用**逆频率类别权重 + 上限截断**处理，详见 [[知识点-轻量CNN与MobileNet]]。

## 数据集长什么样

```
ai-training/datasets/passability/
├── images/
│   ├── passable/  roi_20260930_223128_073.jpg   ← 已裁好的 ROI 小图（648×324）
│   ├── blocked/   roi_...
│   └── no_ground/ roi_...
└── manifest.csv                                  ← 唯一权威标签清单
```

`manifest.csv` 六列，每列都有用途，不能随手删：

| 列 | 例子 | 干什么用 |
|---|---|---|
| `relpath` | `images\blocked\roi_20260930_223128_073.jpg` | 图片相对路径 |
| `label` | `blocked` | **人工标签**（训练唯一依据） |
| `map` | `零号大坝` | 分组评估（防"只在这一张图好"） |
| `season` | `s5` | 赛季切片评估（漂移分析） |
| `source` | `ailabel` / `aicollect` | 来源追溯 |
| `created` | `2026-09-30 22:31:28` | **采集时刻**——会话切分靠它（见下） |

还有一份 `datasets/record/label_map.csv`，把"录制帧 → 裁剪出的训练图"对上号，`audit.py` 靠它做人工 vs VLM 对账。它**不是标签源**，只是映射表。

## 标签是从哪来的

只有一条路：**人在 `ailabel` 里按键盘**。

| 键 | 含义 |
|---|---|
| `A` / `S` / `D` / `F` | 打标（三类 + 丢弃等，键位细节见 `DeltaUnlimited/Cli/README.md`） |
| `X` | 丢弃这一帧（糊图、串台、没信息） |
| `N` | 跳到下一个未标帧 |
| `←` / `→` | 翻页 |

打标时它会**自动裁 ROI 写回 `passability/`**，复标时会自动把旧文件搬走——所以"改标签"只要重标一次，不用手动删图。

另有一条备用路线 `aicollect`（边玩边按 1/2/0/b 打标），但控制台会抢焦点、打断游戏，日常推荐 `airecord` 录完再 `ailabel` 慢慢标。

> [!tip] 打标优先顺序
> **blocked 优先**（怼墙/贴箱子/栅栏）。它是最缺的样本，也是实机最要紧的样本——模型认不出"前面是墙"，就会一直往前蹭。

## 类别顺序：一个字母都不能错

```
ai-training/src/model.py     CLASSES = ("passable", "blocked", "no_ground")
DeltaUnlimited/Vision/Ai/AiTypes.cs   Passable = 0, Blocked = 1, NoGround = 2
assets/ai/models/manifest.json        "classes": ["passable", "blocked", "no_ground"]
```

ONNX 输出的三个数就是按这个顺序排的。**只要有一处顺序不同**，模型会把 blocked 当 passable 报——症状是"预警完全反了"，而且训练日志看不出任何异常。改顺序 = 两端同时改 + 重训 + 重跑门禁。

## 金标集：门禁的"期末考卷"

| 项 | 值 |
|---|---|
| 位置 | `assets/ai/golden/`（`labels.csv` 只有 `file,label` 两列） |
| 规模 | **376 张**（passable 250 / blocked 106 / no_ground 20） |
| 来源 | **独立采集**、人工反复核对，**绝不参与训练**、**绝不用 VLM 判断** |
| 用途 | `dotnet run -- aieval` 离线门禁；`AiVision` 启动时自动回放做赛季漂移检测 |
| 维护 | **每赛季重建**（旧考卷考不出新版本的问题） |

为什么必须独立采集：金标集如果从训练集里抽，等于"考题就是练习题"，分数必然虚高，门禁形同虚设。这也是为什么 `prescreen.py` 的红线里专门有一条"金标集不许用 VLM 结果"。

## 验证集怎么切：按会话，不按行

这是本项目数据工程上最值钱的一条经验，也是踩过两次的坑（见 [[坑-验证集泄漏]]）。

**问题**：`airecord` 每 1-2 秒存一帧，同一段路的相邻帧几乎一模一样。如果按行随机切 15% 做验证集，验证集里会混进训练集的"近邻帧"——模型等于开卷考试，val 指标虚高。

**做法**：`dataset.py: stratified_split` 按**采集会话**（精确到分钟）整段分给 train 或 val，同时贪心保证每类在 val 里有配额。

**两次修正（很重要）**：

1. `ad9b5c1` 先把"按行随机切"改成"按会话整段切"；
2. 2026-09-30 发现**会话键取错了时间戳**——`dataset.py` 从 ROI 文件名解析时间，而 `ailabel` 写进文件名的是**裁剪导出的时刻**，不是采集时刻（采集时刻在 `manifest.created` 列，旧的 `load_manifest` 把它丢了）。实测 1736 条标注里 **47/71 个采集分钟被旧键打散成多个"会话"**，同一采集 burst 的相邻帧仍可能跨 train/val。

修正后改用 `created` 做会话键：**会话数 66→71、最大会话 74→32 帧**（与 1-2 秒的采集节奏吻合，说明切分终于对了）。

> [!bug] 这笔账最后体现在哪
> 训练时 val macro-F1 = 0.8722，金标集只有 **0.695**。这个 0.18 的落差，一部分是"考卷更难"，一部分就是泄漏的账单。所以**门禁只认金标，不认 val**。

## 采集覆盖：数量之外还有"维度"

`docs/AI训练侦查报告.md` §3.2 定的覆盖矩阵（模型只对**运行时分布**鲁棒，所以以跑 bot 的那套画质为主，留 10-20% 其他条件做泛化）：

| 维度 | 要求 |
|---|---|
| 地图 | 零号大坝先采到饱和 → 长弓溪谷 → 巴克什/航天基地，每图至少一个完整"采集夜" |
| 时段/天气 | 白天 / 黄昏 / 夜战 / 雾天 |
| 角色状态 | 平地跑 / 站定 / 冲刺 / 蹲 / 贴墙 / 门内外 / 楼梯 / 坡道 / 水边 / 集装箱顶 / 楼顶边缘 / 跳落前 |
| HUD 状态 | 裸 HUD / 低血 / 红屏残留 / 开小地图 / 拾取面板覆盖 / 背包打开（多数应进 no_ground） |
| 难例 | **贴脸墙、UI 遮挡、加载瞬间、天空**——no_ground 的主粮，不主动采就永远缺 |

数量建议（看门禁指标，别教条）：可通行性三分类**起步每类 1500 张（总计 6000-8000）**，目标每类 3000+ 且每张地图都有覆盖。按 4Hz 采集节奏，一个两小时的采集夜能出几千张。

## 训练端会怎么"折腾"这些图

| 增广 | 参数 | 为什么 |
|---|---|---|
| `Resize` | 到 112×224 | **必须**：推理端永远把 ROI 缩到 INPUT_SIZE，训练端不缩就是 train/serve 偏斜 |
| `ColorJitter` | 0.35 / 0.35 / 0.25 / 0.05 | 抗赛季光照、雾效漂移 |
| `RandomJpeg` | p=0.5，质量 35-90 | 对齐采集端 q80 与实况码流压缩差异 |
| `RandomHorizontalFlip` | — | 最便宜的泛化（左右墙长一样） |

验证集只做 `Resize + ToTensor + Normalize`，不做增广。

## 常见坑

| 症状 | 原因 |
|---|---|
| val 奇高、金标很低 | 相邻帧泄漏（会话键错了）或金标混进训练集 |
| 模型总把 blocked 说成 passable | 类别顺序不同步；或 blocked 样本太少/太单一 |
| 训练时偶尔跳过图片 | manifest 里残留失效路径（删图忘删行）；`dataset.py` 会换一张并限量告警 |
| no_ground 的 F1 是 0 | val 里完全没有该类（`per_class_f1` 记 0 而不是 1——早期版本记 1 会让 macro-F1 虚高、门禁误放行） |
| 换了新数据但指标没变 | 忘同步 `ai_vision.json` 的 model 字段，跑的还是旧模型 |

## 出处

- 文档：`docs/AI训练侦查报告.md` §1（含 2026-09-29 复核与 2026-09-30 防泄漏二次修正）、§3.1-§3.4、§4.1、§7；`docs/AI训练学习路径.md` §2「第 1 层」；`ai-training/README.md`「目录约定」「红线」
- 代码：`ai-training/src/dataset.py`（`load_manifest_sessions` / `_session_key` / `stratified_split` / `make_transform` / `compute_class_weights`）、`model.py`；`DeltaUnlimited/Vision/Ai/AiTypes.cs`
- 数据：`ai-training/datasets/passability/manifest.csv`（1740 行）、`assets/ai/golden/labels.csv`（377 行含表头 = 376 张）、`ai-training/datasets/record/label_map.csv`
- Git：`ad9b5c1`（切分改为按采集会话整段）；2026-09-30 会话键修正（`created` 取代文件名时间戳）
