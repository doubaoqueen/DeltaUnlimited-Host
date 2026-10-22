---
title: 知识点-ONNX与DirectML
tags: [知识点, AI, 部署, ONNX]
status: 现行
created: 2026-10-23
related: ["[[AI-模型与门禁]]", "[[模块-AI视觉感知]]", "[[知识点-轻量CNN与MobileNet]]", "[[坑-ONNX回退不完整]]", "[[AI-训练流水线总览]]"]
---

# 知识点：ONNX 与 DirectML

> [!info] 一句话
> **ONNX 是模型的"通用文件格式"**（像 PDF），**ONNX Runtime 是读这个文件的播放器**，**DirectML 是让播放器用显卡干活的后端**——三者合起来，才让"Python 训练的模型在 C# 里跑"成为一句 `new InferenceSession(...)`。

## 为什么需要它（在这个项目里）

训练用的是 Python（PyTorch），游戏里跑的是 C#。这两个世界没法直接互相调用。如果强行让 C# 里跑 PyTorch，就得把 Python 运行时、torch 库、CUDA 依赖全塞进游戏进程——体积极大、启动极慢、还容易崩。

所以采用"**离线训练、运行时零 Python**"：

```
PyTorch 训练  →  导出 .onnx（一个 6MB 的文件）  →  C# 里用 ONNX Runtime 加载
```

模型只交付一个文件。玩家机器上**不需要 Python、不需要 torch、不需要 CUDA 工具链**。

## 原理（够用就好）

### ONNX：把模型"冻结"成计算图

神经网络本质是一张**计算图**：输入 → 一堆算子（卷积/矩阵乘/激活/池化）→ 输出。ONNX 就是把这张图连同**权重数值**一起序列化下来：

- 图的节点 = 算子（Conv、MatMul、Relu…，有标准定义）；
- 图的边 = 张量（形状、类型都写死）；
- 权重 = 一堆 float。

所以 ONNX 文件是**自包含**的：给一段输入，谁都能算出同样的输出（误差范围内）。它不带训练能力，也没有 Python。

> [!note] 导出时钉住的"契约"
> 本项目导出时显式指定了 `input_names=["input"]`、`output_names=["logits"]`、`opset_version=17`、`dynamo=False`。这些就是"文件格式的条款"——C# 端按 `input` 灌数据、按 `logits` 取结果，谁改了名字，另一端就找不到。

### ONNX Runtime：读文件、跑图

**ONNX Runtime（ORT）** 是微软开源的推理引擎。它做三件事：读 ONNX 图 → 做图优化（算子融合、常量折叠）→ 挑一个后端执行。

**后端 = Execution Provider（EP）**。同一个模型可以跑在不同硬件上：

| EP | 用什么算 | 特点 |
|---|---|---|
| **CPU** | 处理器 | 一定有、一定能跑；小模型其实够快 |
| **DirectML** | 任意 DX12 显卡 | Windows 原生、**不需要装 CUDA**；N 卡/A 卡/I 卡/核显都能用 |
| CUDA | 仅 NVIDIA + CUDA 工具链 | 最快，但要装几个 GB 的依赖 |
| TensorRT | 仅 NVIDIA + 专门编译 | 最快但最麻烦 |

本项目选 **DirectML 优先 + CPU 兜底**，理由很实际：

1. 目标机器是 Windows 游戏机，DirectML 零依赖（DirectX 12 自带）；
2. 不绑定显卡品牌（用户可能是 N 卡也可能是 A 卡/核显）；
3. 小模型（6MB、112×224）在 DirectML 上已经"够快"，追求极致没意义。

### 为什么钉版本 1.24.4

`Microsoft.ML.OnnxRuntime.DirectML` 的版本线有个特点：**1.24.4 是 DML 独立线的最后一版，自带原生库、零系统依赖**——项目文档里就是这么记的。

钉版本的实际收益：**换机器、重装系统、CI 上重新还原，行为一致**。这类"底层依赖"最怕"昨天好好的今天不一样"，钉版是最便宜的稳定手段。

> [!warning] 钉版的代价
> 安全更新/性能提升拿不到，需要人工定期评估。但对一个本地工具来说，稳定 > 新。

### 为什么线程数固定 4

`runtime_threads: 4` 是实测结论，不是拍脑袋：

- 默认（全核）在这个小模型上**反而更慢**，而且 **p99 延迟抖动大**（线程调度开销 > 并行收益）；
- 固定 4 线程后，延迟稳定在 ~0.8-0.9ms；
- 对 5-10Hz 的伺服循环来说，"稳定"比"平均最快"重要得多。

这是个通用经验：**小模型 + 短推理，并行度不是越高越好**。

## 在本项目中的用法

`OnnxRunner` 是整个 AI 运行时唯一碰 ONNX 的地方，封装了四件事：

| 能力 | 实现 |
|---|---|
| **三种 EP 语义** | `ep=auto`（先试 DirectML，失败自动回退 CPU）/ `dml`（严格，失败抛异常）/ `cpu`（只用 CPU）。回退是**两层**：EP 注册失败回退，会话创建失败也回退 |
| **暴露实际生效后端** | `Provider` 属性（"DirectML" / "CPU"），写进状态栏与 `logs/ai`，出问题时一眼看出跑在哪 |
| **零拷贝缓冲** | `FixedBufferOnnxValue` + 复用的 `float[]` 输入/输出缓冲；每次推理只填数、不分配 |
| **预热 + 计时** | `Warmup()` 用随机噪声空跑一次（DML 首跑要编译着色器，可达数百 ms）；`Run()` 返回 `(probs, ms)`，延迟直接可观测 |

配套的 `PassabilitySensor` 负责"喂什么"：裁 ROI → 缩放 → BGR2RGB → `/255` → 减均值除标准差 → CHW 排列，然后 `Run(fillInput)` 把数据写进共享缓冲。

输出是 3 个 logits，`OnnxRunner.Softmax` 转成概率，`ArgMax` 取类别——**这三步都在 C# 里做**（导出时没把 softmax 塞进图里），所以类别顺序必须与训练端一致，见 [[AI-模型与门禁]]。

## 常见误解 / 坑

> [!bug] 坑 1：以为"回退"是自动的、完整的
> 回退要写两层才算完整：**EP 注册**（`AppendExecutionProvider_DML` 抛异常）和**会话创建**（`new InferenceSession` 抛异常）。只处理第一层，遇到"算子不被 DML 支持/驱动异常"就会在构造时崩掉。
> `OnnxRunner` 里两个 `catch` 就是为此。这也是踩过的坑，见 [[坑-ONNX回退不完整]]。

> [!warning] 误解 2："有 GPU 就一定比 CPU 快"
> 对**小模型 + 单张推理**，GPU 的优势经常被"数据传输 + 启动开销"吃掉。本项目实测 4 线程 CPU 约 0.8-0.9ms，DirectML 也在同一量级。所以 CPU 兜底不是"降级体验"，而是"够用"。

> [!bug] 坑 3：以为 ONNX 里包含预处理
> **不包含。** 归一化（mean/std）、通道序、缩放全在 C# 侧手写。ONNX 只保证"给它 1×3×112×224 的 float，它给你 3 个 logits"。预处理写错，模型会安静地给出错误答案。

> [!bug] 坑 4：忘了 warmup
> DML 首次推理要编译着色器，可能几百毫秒。如果第一次调用发生在伺服循环里，会明显卡一下。所以初始化时先 `Warmup()`。

> [!note] 坑 5：把 op 名字写死两次
> `_session.InputNames[0]` 是运行时读的，比硬编码 `"input"` 稳。若以后模型换了输入名，硬编码的代码会静默失败或抛"找不到输入"。

## 一句话记住

**ONNX = 模型的 PDF，ONNX Runtime = 播放器，DirectML = 用显卡播放的后端；钉版本求稳，4 线程求稳，回退要写两层，预处理不在文件里。**

## 出处

- 代码：`DeltaUnlimited/Vision/Ai/OnnxRunner.cs`（`BuildOptions` 双层回退、`Provider`、`FixedBufferOnnxValue`、`Warmup`、`Softmax`）、`PassabilitySensor.cs`（`FillChw`）、`AiVision.cs`（`EnsureStarted` 里创建 runner + warmup）
- 数据：`data/ai_vision.json`（`ep: "auto"`、`runtime_threads: 4`、说明字段写明"实测 4 最优"）
- 文档：`docs/模块说明.md` §15（"钉包 1.24.4，DML 独立线最后一版，自带原生库零系统依赖"）；`docs/AI训练学习路径.md` §2「第 3 层」（ONNX 是什么、EP 概念、预处理一致性铁律）；`ai-training/src/export_onnx.py`（导出参数与 manifest 片段）
- 外部：[ONNX Runtime DirectML Execution Provider](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)
