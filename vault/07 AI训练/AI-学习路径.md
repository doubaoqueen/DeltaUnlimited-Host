---
title: AI-学习路径
tags: [AI, 学习, 入门, 路线]
status: 现行
created: 2026-10-23
related: ["[[AI-训练流水线总览]]", "[[AI-数据集与标注]]", "[[AI-模型与门禁]]", "[[知识点-图像基础]]", "[[知识点-轻量CNN与MobileNet]]", "[[工具-Python脚本清单]]"]
---

# AI-学习路径（非科班版）

> [!info] 一句话
> 你不需要先学完机器学习再动手——**最好的教材就是这条已经建好的流水线**：每周给自己一个小目标，跑通一段、看懂一段，九周之后你能把"一张截图 → ONNX → PathWarning"这条链完整讲出来。

## 先说三句定心的话

1. **不学推导公式。** 不需要手推反向传播，不需要会写优化器。你需要的是"知道每一步在干什么、出问题时知道去哪看"。
2. **先跑通，再补知识。** 遇到看不懂的概念，回到知识地图查它属于哪一层，只补那一层。
3. **本项目的 AI 是"感知传感器"**：不学策略、不学强化学习、不碰战斗辅助。你学的一切都围绕一件事——把图喂给小模型、拿到一个分类、交回给 C# 状态机。

## 知识地图：四层，按需补

| 层 | 学什么 | 对应仓库环节 | 达标标准（会做什么） | 什么时候补 |
|---|---|---|---|---|
| **0 项目与工具** | 项目文档、CLI、Git | `docs/*`、`Cli/`、日常提交 | 能独立完成一次采集并提交 | 开工前置 |
| **1 图像与数据** | 图像基础、数据集工程、标注 | `aicollect`/`airecord`/`ailabel`、`manifest.csv` | 能组织一次符合覆盖矩阵的采集，能清洗数据、建评测集 | **最先补**（比模型知识重要） |
| **2 模型与训练** | 训练在发生什么、指标含义 | `ai-training/src/*.py` | 能读懂 `train.py` 每一行、看懂训练日志、独立微调一次 YOLO | 第一个采集夜之后 |
| **3 部署与 C# 衔接** | ONNX、预处理一致性、后处理 | `Vision/Ai/*` | 能解释模型从 ONNX 到 `PathWarning` 的完整链路 | 观察期（observe-only） |

## 九周计划：每周一个小目标

对应 `docs/AI训练学习路径.md` §3 的九步管线，每步拆成一个"周末能做完"的小目标。

| 周 | 小目标（做完打勾） | 主要动作 | 对应知识层 | 自测标准 |
|---|---|---|---|---|
| **W1** | 环境能跑、链路自检绿 | 建 `.venv-train`（`--system-site-packages` + pillow）；`python src/smoke_test.py` | 0 | 看到 `✅ 冒烟通过：manifest → train → export → evaluate 全链路可执行` |
| **W2** | 完成第一个采集夜 | `dotnet run -- airecord --map 零号大坝 --season s5`，正常打游戏 1-2 小时；顺手 OBS 录整局 | 1 | `datasets/record/full/` 有几百张帧、`record_manifest.csv` 多出对应行 |
| **W3** | 能独立打标并说清三类边界 | `dotnet run -- ailabel`，按覆盖矩阵标；**blocked 优先** | 1 | 能对着一张帧解释"为什么它是 no_ground 而不是 blocked" |
| **W4** | 第一次训练，能读日志 | `python src/train.py --data datasets/passability --epochs 30` | 2 | 能说出 loss 卡住 / val F1 不涨分别可能是什么原因 |
| **W5** | 导出并让游戏用上新模型 | `export_onnx.py` → 同步 `ai_vision.json` + `manifest.json` → `dotnet run -- aiprobe` 亲眼看判定 | 2 + 3 | `aiprobe` 实时画面上的判定跟你肉眼判断大体一致 |
| **W6** | 建金标集、过门禁 | 从**新采集**（不是训练集）挑约 100 张建金标集 → `dotnet run -- aieval` | 1 + 3 | 能读懂 aieval 打出的混淆矩阵与三档判定 |
| **W7** | observe-only 跑几局，学会看现场 | `dotnet run -- patrol`；看 `logs/ai`、`SnapshotStats()`、矛盾数 | 3 | 能解释"矛盾率"是什么意思、为什么它能当免费真值 |
| **W8** | 回流复标，迭代 v2 | `python src/import_jsonl.py` → 回 W3 复标 → 回 W4 重训；加 map/season 分组评估 | 2 + 3 | 能说清新版比旧版好在哪（哪一类提升了） |
| **W9** | 判断"能不能开 enforce" | 综合金标分数 + 真机误报 + 矛盾率，写一段结论（开/不开，为什么） | 3 | 结论有数字支撑，而不是"感觉差不多了" |

> [!tip] 每周只要求一件事
> 不求一次做完九周。**卡住就停在那周**，把那一周的自测标准做到，比囫囵吞完九周有用得多。

## 现成练习题（都是本项目里真实存在的操作）

| # | 练习 | 怎么做 | 你会学到 |
|---|---|---|---|
| 1 | **改一个阈值看效果** | 把 `ai_vision.json` 的 `min_confidence` 从 0.70 调到 0.55 / 0.85，各跑一次 `aiprobe` | 阈值是"宁可弃权还是宁可误报"的旋钮；没有免费午餐 |
| 2 | **用 `audit.py` 看混淆矩阵** | 标完一轮后跑 `python src/audit.py`，看"VLM 建议 × 人工标签"的矩阵 | 混淆矩阵比单一准确率信息量大得多：它告诉你**哪两类在互相认错** |
| 3 | **故意制造一次泄漏** | 临时把 `dataset.py` 的 `_session_key` 改成"按文件名随机"，对比 val 分数 | 亲手看到 val 从 0.87 涨到 0.95+ 而金标不动——泄漏长什么样 |
| 4 | **读一张 ONNX 的身份证** | 打开 `assets/ai/models/manifest.json`，对照 `ai_vision.json` 和 `model.py` | 三处常量同步铁律；`classes` 顺序为什么致命 |
| 5 | **给 `PassabilityFilter` 调窗口** | `window: 5 → 3`、`k: 3 → 2`，观察预警变灵敏还是变吵 | 时域滤波 = 用"时间"换"稳定"，代价是反应变慢 |
| 6 | **跑一次 `smoke_test.py` 再故意弄坏一处** | 把 `INPUT_SIZE` 改成 `(224,112)` 跑一遍 | 输入尺寸不匹配会在哪一步炸、报什么错 |
| 7 | **数一数类别不平衡有多严重** | 用 Excel 打开 `manifest.csv`，按 `label` 做数据透视 | 为什么需要 `class_weights` 和 `weight_cap` |
| 8 | **看一次金标门禁的降级** | 临时把 `drift.min_macro_f1` 调到 0.95 跑 `aiprobe` | 观察期是怎么被"强制"出来的（`_observeOnly`） |

## 最容易踩的八个坑（血泪清单）

1. **BGR/RGB 搞反**：症状是"训练指标很好，实机表现像瞎了"。仓库已对齐，别破坏。
2. **只改一端常量**：mean/std、输入尺寸、类别顺序，三处必须同步（`model.py` ↔ `PassabilitySensor` ↔ `ai_vision.json` + `manifest.json`）。
3. **金标集混进训练集**：门禁立刻形同虚设，赛季漂移检测失效。
4. **被 val 指标冲昏头**：相邻帧泄漏会让 val 虚高，以金标 + 真机观察为准。
5. **只采"好走的地方"**：blocked / no_ground 缺失 = 模型在最需要它的地方失明。
6. **一训练完就开 enforce**：必须 observe-only 观察期先行，让矛盾率说话。
7. **把模型当决策器**：红线。模型只产出预警，动作控制权永远在 C# 状态机与人工。
8. **数据集随手外发/上云**：录屏可能含队友昵称；`images/` 已 gitignore，按 README 约定另盘备份。

## 资源清单

| 资源 | 怎么用 |
|---|---|
| 《动手学深度学习》中文版（d2l.ai，免费） | 只读卷积网络部分（约 4-7 章），概念为主，**不用全啃** |
| [Ultralytics 官方文档](https://docs.ultralytics.com) | `train/val/predict/export` 四命令 + 数据集格式；N5 阶段主教材 |
| 任意 OpenCV 中文入门 | BGR/颜色空间、resize、`matchTemplate`（对应 `TemplateMatcher`） |
| ONNX Runtime 文档 Execution Providers 一章 | DirectML 是什么、为什么钉版本 |
| **项目内教材（最优）** | `ai-training/src/` 十来个 py、`Vision/Ai/` 全部 C#、`docs/跑刀方案.md`——全是为你这个场景写的，比任何外部教程都贴身 |

## 出处

- 文档：`docs/AI训练学习路径.md`（§0 怎么用、§1 知识地图四层、§2 各层展开、§3 九步任务、§4 资源、§5 八个坑）；`ai-training/README.md`（环境与数据闭环 ①-⑩）；`docs/AI训练侦查报告.md` §3.2/§4.1
- 代码：`ai-training/src/{smoke_test,train,export_onnx,evaluate,audit,import_jsonl}.py`；`DeltaUnlimited/Vision/Ai/PassabilityFilter.cs`、`AiVision.cs`
- 数据：`data/ai_vision.json`（阈值与门禁项）、`assets/ai/models/manifest.json`
