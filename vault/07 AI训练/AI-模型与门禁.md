---
title: AI-模型与门禁
tags: [AI, 模型, 门禁, ONNX, 上线]
status: 现行
created: 2026-10-23
related: ["[[AI-训练流水线总览]]", "[[AI-数据集与标注]]", "[[模块-AI视觉感知]]", "[[决策-AI视觉observe-only]]", "[[知识点-ONNX与DirectML]]", "[[知识点-轻量CNN与MobileNet]]", "[[坑-ONNX回退不完整]]"]
---

# AI-模型与门禁

> [!info] 一句话
> 游戏里真正跑的是个 6 MB 的小 CNN（MobileNetV3-Small，输入 112×224，输出三类）。它能不能"碰动作"，不看训练日志有多好看，只看**金标集门禁**给不给过——目前 v1 金标 0.695，落在观察档，所以只记录、不动作。

## 模型本身：为什么这么小

| 项 | 值 | 为什么 |
|---|---|---|
| 结构 | **MobileNetV3-Small**（ImageNet 预训练起步） | 小数据集上迁移学习明显优于从零训练；体积 6 MB 级 |
| 输入 | **112×224（H×W）**，RGB，NCHW | ROI 是 648×324 的 2:1 横带，等比缩到约 1/2.9 —— 见 [[标定-可通行ROI]] |
| 输出 | 3 类 softmax：`passable / blocked / no_ground` | 顺序与 C# 枚举严格一致 |
| 预处理 | `(pixel/255 - ImageNet_mean) / ImageNet_std` | 与 `PassabilitySensor.Mean/Std` 一字不差 |
| 速度 | **约 0.8-0.9 ms/帧**（4 线程） | 伺服 5-10Hz 下等于免费 |
| 档位 | `mbv3s`（默认）/ `tiny`（极简 CNN，约 0.2ms，算力受限时用） | 两种都在 `model.py` 里，命令行 `--model` 切换 |

为什么不上更大的模型：**12 GB 显卡 + 实时性 + 收益**。这个任务只是"前方有没有挡住"，不是识别 80 类物体。业界经验在这也成立：可通行性这种二/三分类，轻量骨干 + 好数据 > 大骨干 + 一般数据。

模型文件三个，别搞混：

| 文件 | 是什么 |
|---|---|
| `passability_v1.onnx` | **当前正式模型**（训练时 val macro-F1 0.8722；金标 0.695） |
| `passability_v2.onnx` | 候选模型（金标 0.657，未过执行线，配置未采用） |
| `passability_v0_placebo.onnx` | 管线联调占位，**随机权重**，不可用于判断 |

## 运行时怎么跑：ONNX Runtime + DirectML

```
原始帧 → FrameTools.Normalize(1920×1080) → 裁 ROI(592,656,648,324)
       → Resize 112×224 → BGR2RGB → CHW + ImageNet 归一化
       → OnnxRunner.Run（DirectML 优先，失败回退 CPU）→ softmax → 三分类
```

`OnnxRunner` 的四个关键决定：

- **钉版 1.24.4**：DirectML 独立线最后一版，自带原生库、零系统依赖；
- **`ep=auto`**：先试 DirectML（任意 DX12 显卡），失败自动回退 CPU——低配笔记本也能跑（三种语义：`auto` 回退 / `dml` 严格抛错 / `cpu` 只用 CPU）；
- **线程数固定 4**：实测 4 最优，默认全核反而更慢、p99 抖动更大；
- **零拷贝 + 预热**：`FixedBufferOnnxValue` 复用输入/输出缓冲；构造后 `Warmup()` 一次（DML 首跑要编译着色器，可达数百毫秒）。

任何一帧推理抛异常，`AiVision.Observe` 都吞掉并返回"无预警"——**fail-open，绝不让感知模块把主流程带崩**。

## 滤波：别让单帧噪声驱动动作

模型输出只是"这一帧的看法"。`PassabilityFilter` 做两件事：

| 机制 | 参数（`ai_vision.json`） | 作用 |
|---|---|---|
| 时域滤波 k-of-n | `window: 5`，`k: 3` | 5 帧里至少 3 帧说 blocked 才报 blocked——滤掉偶发误判 |
| 动静分离 | `motion_diff_threshold: 12.0` | 帧差 >12（画面在大幅变化，比如转身/冲刺）时**压制预警**，避免"自己在动"被当成"前面有墙" |
| 最低置信 | `min_confidence: 0.7` | 低于它的归结为 no_ground（弃权） |

## 门禁：三档，按金标集分数说话

启动时 `AiVision.EnsureStarted` 会用金标集（376 张独立采集的"期末考卷"）回放一遍，拿 macro-F1 决定本进程的权限：

| macro-F1 | 行为 | 对应配置 |
|---|---|---|
| **≥ 0.85** | 允许 `enforce=true`（配置还得另开 `avoid.enforce`） | `drift.min_macro_f1` |
| **0.60 – 0.85** | **本会话强制仅观察**：预警只记录，`Enforce` 恒为 false | `drift.min_macro_f1_observe` |
| **< 0.60** | **自动禁用**（不加载模型，完全回退原始守卫），提示用新素材重训 | 同下 |

还有第二道闸，看的是**运行期**而不是离线：

```
贴墙守卫触发（Escalate） → RecordGuardFired()
        │  模型说"没事"但守卫触发了 = 一次"矛盾"（假阴性）
        ▼
DriftMonitor：窗口 10 分钟 / 最少 20 个样本
        │  矛盾率 ≥ 25%
        ▼
自动禁用 + 把 enabled=false 写回 data/ai_vision.json（重启后仍关闭，直到人工恢复）
```

> [!warning] 两条禁用路径不一样，别混
> - **金标 < 0.60**：本次启动不启用模块（不写配置，下次启动会重新评一次）；
> - **运行期矛盾率超限**：`Disable()` 会**写回 `ai_vision.json`**，与手动一键关闭同一个开关——这是故意的，防止"重启一下又偷偷开了"。

### 门禁为什么认金标不认 val

因为 val 可能被相邻帧泄漏污染（见 [[AI-数据集与标注]]）。数字摆在这：训练时 val macro-F1 **0.8722**，金标只有 **0.695**。差 0.18。如果按 val 决策，这个模型早就该开 enforce 了；按金标，它只配观察。

金标 0.695 的构成里，**主短板是 blocked 召回**（`assets/ai/models/manifest.json` 原文）。也就是说：它更容易把"挡住"看成"能走"——对跑刀来说这是最危险的方向（会一直往墙上蹭）。这也是下一轮迭代的第一优先级：补 blocked 难例（怼墙/贴箱子/栅栏）。

## 当前状态一张表

| 项 | 值 |
|---|---|
| 配置 | `data/ai_vision.json`：`enabled=true`、`model=assets/ai/models/passability_v1.onnx`、`ep=auto`、`runtime_threads=4` |
| 门禁分数 | 金标 macro-F1 **≈0.695** → 观察档 |
| 是否碰动作 | **`avoid.enforce=false`，且本会话被强制仅观察** |
| 现场表现 | 预警照常记录进 `logs/ai`（JSONL + ROI 图环形缓冲 2GB），巡逻收尾打印 `SnapshotStats()`（帧数/预警数/矛盾数） |
| 下一版 | 用 1740 帧新数据重训、补 blocked、加 map/season 分组评估 |

## 红线（贴在模块头上的那句）

> 模型只是**感知传感器**，只产出 `PathWarning`，**永不直接驱动动作**；决策权 100% 在 C# 状态机。
> `enforce=true` 的语义是"**预测层提前拉动既有守卫（Escalate）**"，不是新增一套决策逻辑。
> 任何失败都 fail-open 回退原始守卫，`enabled=false` 即完全回到没有 AI 的状态。

## 常见误解

| 误解 | 事实 |
|---|---|
| "val 0.87 就是准" | 门禁只看金标；val 偏高可能是泄漏 |
| "金标 0.695 才刚及格，是不是模型很差" | 0.695 是"三类里最弱那类拖的"，且它现在**不参与动作**，只贡献日志与矛盾率 |
| "分数低就调低阈值放它过" | 调 `min_macro_f1` 等于自欺；正确做法是补数据重训 |
| "开了 enforce 模型就能自己躲墙" | enforce 只是把预警接到**已有**的 Escalate 守卫上，仍然走原有暂停/转向逻辑 |
| "换新模型只要换 onnx 文件" | 还要同步 `ai_vision.json` 的 model 与 `manifest.json` 的 classes；顺序/常量不一致会静默错 |

## 出处

- 文档：`docs/模块说明.md` §15（门面生命周期、三档门禁表、命令与训练管线）；`docs/AI训练侦查报告.md` §1 现状盘点、§4.1、§5 三层指标、§7 风险；`docs/AI训练学习路径.md` §2「第 3 层」
- 代码：`DeltaUnlimited/Vision/Ai/{AiVision,OnnxRunner,PassabilitySensor,PassabilityFilter,DriftMonitor,AiEvaluator,AiLogSink,AiTypes}.cs`；`ai-training/src/{model,export_onnx}.py`
- 数据：`data/ai_vision.json`；`assets/ai/models/manifest.json`（含 `trained_on_game_version`、`current_gate`、`dataset_snapshot`）；`assets/ai/golden/labels.csv`
