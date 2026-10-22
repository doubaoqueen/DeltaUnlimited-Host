---
title: 模块-AI视觉感知
tags: [模块, AI]
status: 现行
created: 2026-10-23
related: ["[[架构-总览]]", "[[模块-伺服循环]]", "[[模块-感知信号]]", "[[决策-AI视觉observe-only]]", "[[AI-模型与门禁]]", "[[AI-训练流水线总览]]", "[[知识点-ONNX与DirectML]]", "[[知识点-轻量CNN与MobileNet]]", "[[标定-可通行ROI]]", "[[坑-ONNX回退不完整]]"]
---

# 模块-AI视觉感知

> [!info] 一句话
> 用一个小 CNN（MobileNetV3-Small，输入只有 112×224）判断"准星前面这块地能不能走"，三分类 `passable / blocked / no_ground`，单帧推理约 0.9ms；但它**只是传感器**——产出一句预警，动不动手永远由 C# 状态机决定。

## 1. 它负责什么、为什么需要它

规则式守卫（贴墙看帧差）是"撞上了才知道"；可通行性模型想在**撞上之前**给一句"前面可能是墙"。这是跑刀从"撞墙转向"升级到"提前减速"的关键一步。

代价是引入了一个会"漂"的东西：赛季更新、光照明暗、地图改动都会让模型悄悄变瞎。所以整个模块的设计重点其实不是模型，而是**三道防线**：

1. 它不能直接动：`enforce=true` 的语义只是"提前拉动**既有**守卫（Escalate）"，不是新增决策逻辑；
2. 它随时能关：`ai_vision.enabled=false` 即完全回退原始守卫，零代码改动；
3. 它自己会关自己：金标集门禁（启动时）+ 矛盾率监测（运行时）都会把 `enabled` 写回 false。

## 2. 数据流

```
原始窗口帧（BGRA，任意分辨率）
  │ FrameTools.Normalize → 设计分辨率 1920×1080
  │ 裁 ROI [592,656,648,324]（标准 2:1，roitune 标定）
  │ Resize → 112×224 → BGR2RGB → CHW float → (x/255 − ImageNet均值) / 标准差
  ▼
ONNX 会话（DirectML 优先 / CPU 兜底，4 线程，固定缓冲零拷贝）
  ▼ softmax → 3 个概率
PassabilitySensor → PassabilityResult(Class, Probs, Confidence, LatencyMs)
  ▼
PassabilityFilter（时域滤波：k-of-n + 动静分离 + 弃权帧）
  ▼
PathWarning(Level=None|Blocked, Confidence)   ← 消费方：patrol / follow_marker
  ▼
AiLogSink（JSONL 永久层 + ROI 图环形层）
```

## 3. 推理封装：`OnnxRunner`

| 项 | 事实 |
|---|---|
| 执行提供者 | `ep=auto` → 先试 DirectML（GPU，任意 DX12 显卡零依赖），失败回退 CPU；`dml` = 只 DML（失败抛异常）；`cpu` = 只 CPU |
| 回退两层 | ① `AppendExecutionProvider_DML` 失败 → 回退 CPU；② DML 会话创建失败（算子不支持/驱动异常）→ 重建 CPU 会话（`OnnxRunner.cs:26-44, 66-84`） |
| 线程 | `runtime_threads = 4`；小模型用默认全核反而更慢、p99 抖动更大 |
| 缓冲 | `FixedBufferOnnxValue` 共享输入/输出数组，**零拷贝**；模型只读一次 |
| 预热 | `Warmup()` 用固定种子噪声跑一次（DML 首跑含着色器编译，可达数百 ms） |
| 输出类别数 | 从模型元数据读（`OutputMetadata[..].Dimensions[1]`），缺省 3 |

输入形状是 `[1, 3, inputH, inputW]` = `[1,3,112,224]`（**H 在前**），这一点必须和训练端一致，否则不报错但结果全错。

## 4. 预处理：三个"必须一字不差"

`PassabilitySensor` 里的常量与 `ai-training/src/model.py`、`export_onnx.py` 严格对齐（`PassabilitySensor.cs:10-12`，`model.py:4-7`）：

| 项 | 值 |
|---|---|
| 类别顺序 | `passable=0, blocked=1, no_ground=2`（与 C# 枚举、`CLASSES` 元组一致） |
| 均值（RGB） | 0.485 / 0.456 / 0.406 |
| 标准差（RGB） | 0.229 / 0.224 / 0.225 |
| 输入尺寸 | 112×224（H×W） |

顺序：`Resize → BGR2RGB → 按通道拆分 → /255 → (x−mean)/std → CHW 填入缓冲`。任何一步改错，模型不会报错，只会"看起来随机"——所以这三处是代码评审的重点。

## 5. 时域滤波：`PassabilityFilter`

参数来自 `ai_vision.filter`：`window=5`、`k=3`、`motion_diff_threshold=12.0`，外加 `min_confidence=0.7`。

规则（`PassabilityFilter.cs:27-39`）：

| 情况 | 处理 |
|---|---|
| 类别是 `no_ground`（弃权：天空/贴脸遮挡/UI 覆盖） | **不推进窗口**（不下结论） |
| 本帧帧差 > 12.0（有移动物体进入） | **压制本帧**（动静分离：敌人/投掷物不该被当成地形） |
| 置信度 < 0.7 | **不推进窗口**（低置信等于弃权） |
| 其余 | 进窗口；最近 `window` 帧里 ≥`k` 帧 blocked → 预警升级为 `Blocked` |

`Reset()` 在守卫触发/传送后调用（旧窗口状态作废）。`Current` 在两次 Push 之间保持不变。

预警级别只有 `None` / `Blocked` 两级；`Warn`（远端级）是给 v2 双带 ROI 预留的（`AiTypes.cs:18-23`）。

## 6. 门面：`AiVision`（生命周期 + 两道门禁 + 日志）

**生命周期**（`AiVision.cs:40-126`）：

```
EnsureStarted(data, repoRoot)     // 懒加载、幂等、每进程只尝试一次（失败不反复重建会话）
  ├─ LoadAiVision → enabled=false 就退出（"已关闭"）
  ├─ 模型文件不存在 → 退出（"模块停用"）
  ├─ OnnxRunner + Warmup
  ├─ 金标集门禁（assets/ai/golden/labels.csv 存在时跑 AiEvaluator）
  └─ Available = true，StatusText 记一行人类可读状态

Observe(rawFrame, frameDiff)      // 每采样帧喂一帧 → 滤波 → PathWarning，永不抛异常
RecordGuardFired()                // 守卫触发时回填"地面真值"，喂 DriftMonitor
SnapshotStats()                   // (帧数, blocked 预警数, 矛盾数)
```

**金标集三档门禁**（`ai_vision.drift`，金标集必须独立采集、绝不混入训练集）：

| macro-F1 | 行为 |
|---|---|
| ≥ 0.85（`min_macro_f1`） | 允许 `enforce=true`（配置里还得 `avoid.enforce=true`） |
| 0.60 – 0.85（`min_macro_f1_observe`） | **本会话强制仅观察**：预警只记录，`Enforce` 恒 false |
| < 0.60 | **自动禁用**（疑似赛季漂移），提示用新素材重训 |

样本数 <10 时门禁不生效（避免小样本误判，`AiVision.cs:92`）。

**运行时第二道闸：矛盾率**（`DriftMonitor`，`ai_vision.drift`）：

- 以"贴墙/横移守卫触发"作为免费的**地面真值**：守卫说撞了、模型却很平静 = 一次**假阴性矛盾**；
- 10 分钟滑窗、样本 ≥20 才判定，矛盾率 ≥25% → `Disable()`；
- `Disable()` 会**把 `enabled=false` 写回 `data/ai_vision.json`**（与手动一键关闭同一开关），重启后保持关闭，直到重训后人工恢复（`AiVision.cs:151-181`）。

**日志回流**（`AiLogSink`）：

- JSONL 永久层：每天一个 `logs/ai/<yyyyMMdd>/ai_<day>.jsonl`，每帧一行（类别/概率/耗时/帧差/预警/模型/EP/图名），永久保留；
- 图像层：**blocked 全存**，另外每 50 帧存一张未标注 ROI 作背景负样本池；JPEG q80；总量超 `ring_mb=2048` 按文件名（含时间戳）删最旧；
- 所有写操作加锁（"Logger 无锁并发写会静默丢行"的教训）；
- 下游：`python src/import_jsonl.py`（`ai-training/src/import_jsonl.py`）把这些行汇总成人工复标队列。

## 7. 谁在用它、现在到底生效到什么程度

| 消费方 | 用法 | 现状 |
|---|---|---|
| `patrol` 命令 | 每采样帧 `AiVision.Observe`；`Enforce` 时预警走既有 `Escalate`（贴墙分支），否则只打印"observe 模式仅记录" | ✅ 已接（`PatrolCommand.cs:98-106`） |
| `follow_marker` | `ServoFrame.HitBlocked` → 控制器松前进键 | ❌ 目前恒 false（K1 后再接） |
| `aiprobe` / `aieval` / `roitune` | 离线验证/评估/调 ROI | ✅ 已接 |

**当前模型**（`data/ai_vision.json` 说明字段）：`passability_v1.onnx` 金标 macro-F1 **0.8722**，`avoid.enforce=false` → observe-only 观察期；`passability_v2.onnx` 金标 0.657（落观察档，未过执行线）；`passability_v0_placebo.onnx` 是管线联调占位（随机权重，不可用于判断）。

## 8. 训练侧只认一件事：接口契约

这个模块能被训练侧安全替换，靠的是三条硬契约（详见 [[AI-训练流水线总览]]）：

1. 类别顺序 `passable/blocked/no_ground`；
2. 输入尺寸 `112×224` 与 ImageNet 归一化常量；
3. 输出是 3 类 logits（C# 侧自己做 softmax）。

其它都能变：换骨干（`--model tiny` 对照）、换数据集、换 epoch。换模型后**必须**同步两处：`data/ai_vision.json` 的 `model` 字段、`assets/ai/models/manifest.json`（`export_onnx.py` 的注释就写着这句）。

## 9. 常见错误对照

| 症状 | 可能原因 | 查什么 |
|---|---|---|
| `StatusText` 说"初始化失败" | 模型路径不对 / ORT 原生库缺失 / EP 异常 | 控制台那行 `⚠️ AI 感知：初始化失败（…）` |
| 一直 `no_ground` | ROI 裁到 HUD / 贴脸看墙 / 输入尺寸或归一化常量不一致 | `roitune` 看绿框；对比 `model.py` 常量 |
| `blocked` 满天飞 | 置信度阈值太低 / 滤波 window/k 太松 / 模型过拟合某地图 | `aiprobe` 实时看概率；`aieval` 看混淆矩阵 |
| 突然自己关了 | 矛盾率超限（`Disable` 写了 `enabled=false`） | `data/ai_vision.json` 的 `enabled`；`logs/ai` 里的 JSONL |
| 启动就被禁 | 金标 macro-F1 < 0.60 | `aieval` 跑一遍金标集 |
| 推理很慢/抖动 | 跑在 CPU 上 / 线程数不对 | `Provider` 打印的是 `DirectML` 还是 `CPU`；`runtime_threads` |

## 出处

- 代码：`DeltaUnlimited/Vision/Ai/AiVision.cs:40-59`（EnsureStarted 幂等）、`61-126`（Start/门禁/状态文本）、`129-149`（Observe 永不抛）、`151-181`（RecordGuardFired / Disable 写回）、`184-185`（SnapshotStats）
- 代码：`DeltaUnlimited/Vision/Ai/OnnxRunner.cs:24-62`（EP 选择/零拷贝/输出维度）、`66-84`（DML 回退）、`92-109`（Run/Warmup）、`111-127`（Softmax）
- 代码：`DeltaUnlimited/Vision/Ai/PassabilitySensor.cs:10-12`（ImageNet 常量）、`19-33`（ROI 夹取）、`36-58`（Predict / PredictFull）、`61-85`（FillChw）
- 代码：`DeltaUnlimited/Vision/Ai/PassabilityFilter.cs:14-46`；`DriftMonitor.cs:14-67`；`AiTypes.cs:4-28`；`AiLogSink.cs:27-113`；`AiEvaluator.cs:22-94`
- 代码：`DeltaUnlimited/Cli/Commands/PatrolCommand.cs:39`（EnsureStarted）、`98-106`（observe/enforce 分支）、`148-152`（统计）
- 数据：`data/ai_vision.json`（全部配置与说明字段：ROI/输入/门禁/日志）
- 数据：`assets/ai/models/manifest.json`、`assets/ai/golden/labels.csv`
- 文档：`docs/模块说明.md` §15（AI 视觉感知模块全表）；`docs/AI训练侦查报告.md`、`docs/AI训练学习路径.md`、`docs/AI初筛操作手册.md`
