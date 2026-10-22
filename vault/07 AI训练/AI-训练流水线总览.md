---
title: AI-训练流水线总览
tags: [AI, 流水线, 总览]
status: 现行
created: 2026-10-23
related: ["[[AI-数据集与标注]]", "[[AI-VLM初筛]]", "[[AI-模型与门禁]]", "[[模块-AI视觉感知]]", "[[决策-AI视觉observe-only]]", "[[知识点-工程实践]]"]
---

# AI-训练流水线总览

> [!info] 一句话
> 这条流水线只回答一个问题：**怎么把"打游戏录下来的截图"变成"游戏里能跑的小模型"**——录制 → 初筛 → 人工打标 → 训练 → 导出 ONNX → 金标门禁 → 观察期上线 → 线上错误回流复标，八步一个循环，转一圈模型好一点。

## 为什么要有"流水线"，而不是"训一次就完事"

因为**游戏会更新**。赛季一换，光照、雾效、UI、地形贴图都变，昨天还 0.87 的模型今天可能就认不出墙了。所以真正要维护的不是"一个模型文件"，而是一条**能反复转的环**：这头喂新素材，那头出新模型，中间每一步都有据可查、有门禁拦着。

一句话记住这条环的性格：**慢的那半段（采集/标注/训练）在离线做，快的那半段（推理）在游戏里做，两半之间只靠一个 `.onnx` 文件交接。**

## 全链路一张图

```
 airecord        prescreen        ailabel         train.py        export_onnx.py      aieval          observe-only        import_jsonl.py
 录制全帧    →   VLM 初筛     →   人工打标    →    训练        →    导出 ONNX     →   金标门禁    →   观察期上线     →    回流复标
    │               │                │               │                │                 │                │                    │
 record/full/   prescreen.csv   passability/    runs/*.pt      assets/ai/models/  三档判定        PathWarning       复标队列
 record_manifest  （旁挂元数据）  images/ +                      + manifest 片段    （人决策）      + 矛盾率统计       （下一轮）
   .csv                          manifest.csv
```

注意图上**没有箭头从 prescreen 指向 manifest.csv**——这是故意的：VLM 的判断永远不写进训练标签（见 [[AI-VLM初筛]]）。

## 八个步骤：每步的产物与工具

| # | 步骤 | 工具 | 命令（在对应目录下） | 产物 | 谁消费 |
|---|---|---|---|---|---|
| 1 | 录制 | `airecord` | `dotnet run -- airecord --map 零号大坝 --season s5` | `ai-training/datasets/record/full/*.jpg` + `record_manifest.csv`（`relpath,label,map,season,created`） | 初筛、标注 |
| 2 | 初筛（可选） | `prescreen.py` + 本地 vLLM | `.venv-train/Scripts/python.exe src/prescreen.py` | `datasets/record/prescreen.csv`（旁挂，只增不改训练清单） | ailabel 右侧面板、`audit.py` |
| 3 | 人工标注 | `ailabel` | `dotnet run -- ailabel` | `datasets/passability/images/<类>/*.jpg` + `manifest.csv` + `label_map.csv` | `train.py` |
| 4 | 训练 | `train.py` | `python src/train.py --data datasets/passability --epochs 30` | `runs/mbv3s_best.pt`（带类别表/输入尺寸/混淆矩阵） | 导出、离线评估 |
| 5 | 导出 | `export_onnx.py` | `python src/export_onnx.py --ckpt runs/mbv3s_best.pt --out ../assets/ai/models/passability_v1.onnx` | `.onnx` + 打印 manifest 片段 | 手动同步两处配置 |
| 6 | 门禁 | `aieval` | `dotnet run -- aieval` | 混淆矩阵 + macro-F1 + 延迟 + 三档判定 | 人决定能否开 enforce |
| 7 | 上线观察 | `patrol` / `chain` | `dotnet run -- patrol` | `logs/ai/*.jsonl` + ROI 图环形缓冲；`SnapshotStats()` | DriftMonitor、收尾统计 |
| 8 | 回流 | `import_jsonl.py` | `python src/import_jsonl.py` | 复标队列（下一轮标注优先看这些） | 回到第 3 步 |

两个"随时能跑"的辅助脚本：

- `python src/smoke_test.py`：伪造小数据集，1 epoch 跑通"manifest → train → export → evaluate"，**不验证精度、只验证链路没断**（CPU 约 1 分钟）。
- `python src/audit.py`：把人工标签和 VLM 初筛对账，出"分流一致率 + 混淆矩阵"——这是检验质检员值不值得信的成绩单。

## "离线训练、运行时零 Python"是什么意思

| 阶段 | 在哪 | 用什么 | 跑多久 |
|---|---|---|---|
| 采集 / 初筛 / 标注 / 训练 / 导出 | `ai-training/`（Python 环境 `.venv-train`） | PyTorch、torchvision、vLLM（WSL） | 分钟到小时级，人守着 |
| 推理 | 游戏里（C# 进程） | ONNX Runtime（DirectML/CPU） | **约 0.8-0.9 ms/帧** |

交接物只有一个 `.onnx` 文件（约 6 MB）。所以：

- 玩游戏的机器**不需要装 Python、不需要装 torch**，更不需要显卡大模型；
- 训练端的 Python 一行都不会进游戏运行时；
- 换模型 = 换文件 + 改一行配置，不用重编译。

这也是为什么第 5 步要强调"手动同步两处"：`data/ai_vision.json` 的 `model` 字段和 `assets/ai/models/manifest.json` 的 `file/classes`。**文件换了、配置没换，跑的还是老模型**——这是最容易犯的错。

## 三条不能破的纪律

1. **标签只来自人工。** VLM 是质检员不是标注员，`prescreen.csv` 永远是旁挂元数据（红线，见 [[AI-VLM初筛]]）。
2. **金标集独立采集、绝不参与训练。** 混一次，门禁就永久失效（见 [[AI-数据集与标注]]）。
3. **三处常量必须一字不差。** 类别顺序 `passable=0 / blocked=1 / no_ground=2`、ImageNet mean/std、输入 112×224，在 `ai-training/src/model.py`、`Vision/Ai/PassabilitySensor.cs`、`data/ai_vision.json` + `assets/ai/models/manifest.json` 三处同步。改一处不改另一处，症状是"训练指标很好，实机像瞎了"。

## 现在走到哪一步了（2026-10 状态）

| 环节 | 当前状态 |
|---|---|
| 录制 | `record_manifest.csv` 现 3286 行；侦查报告口径的"原始录制帧 2581 张"是 2026-09-29 复核时的数 ❓待确认（后续批次持续累积） |
| 标注 | **1740 行**（passable 1205 / blocked 488 / no_ground 43 / background 4） |
| 金标集 | **376 张**（passable 250 / blocked 106 / no_ground 20） |
| 当前模型 | `passability_v1.onnx`（训练时 val macro-F1 0.8722，**金标 0.695**） |
| 门禁结论 | 0.695 落在 0.60-0.85 区间 → **强制仅观察**，`avoid.enforce=false` |
| 候选模型 | `passability_v2.onnx` 金标 0.657，未过执行线，配置仍用 v1 |
| 主短板 | blocked 召回（见 [[AI-模型与门禁]]） |

## 常见误解

| 误解 | 事实 |
|---|---|
| "训练一次，模型就定稿了" | 每赛季都要重建金标集、重训（漂移是确定会发生的事） |
| "val 0.87 挺高了，可以开 enforce 了" | 门禁只看**金标集**分数；val 偏高多半是相邻帧泄漏（见 [[AI-数据集与标注]]） |
| "标注越多越好" | **覆盖矩阵**（地图/时段/天气/遮挡/难例）比总量重要；只采好走的地方等于没采 |
| "VLM 都 8B 了，替人打标应该没问题" | P0/P0.5 已实测证伪，细粒度图标判定不合格（见 [[AI-VLM预标注探索]]） |
| "模型说不通就让它通" | 模型只产出预警，动作权在 C# 状态机和人工手里（红线） |

## 出处

- 文档：`docs/AI训练侦查报告.md` §0/§1/§6；`ai-training/README.md`「数据闭环」①-⑩；`docs/模块说明.md` §15；`docs/AI初筛操作手册.md` §2/§3；`docs/AI训练学习路径.md` §3
- 代码：`ai-training/src/{prescreen,train,export_onnx,evaluate,audit,smoke_test,import_jsonl}.py`；`DeltaUnlimited/Vision/Ai/AiVision.cs`、`OnnxRunner.cs`
- 数据：`data/ai_vision.json`；`assets/ai/models/manifest.json`；`ai-training/datasets/passability/manifest.csv`（1740 行）；`assets/ai/golden/labels.csv`（377 行含表头 = 376 张）
