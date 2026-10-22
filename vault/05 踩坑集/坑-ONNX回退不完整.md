---
title: 坑-ONNX回退不完整
tags: [踩坑]
status: 已解决
created: 2026-10-23
related: ["[[知识点-ONNX与DirectML]]", "[[模块-AI视觉感知]]", "[[决策-AI视觉observe-only]]", "[[坑-验证集泄漏]]", "[[AI-模型与门禁]]"]
---

# 坑：说好"GPU 失败自动回退 CPU"，低配机器上还是崩了

> [!bug] 症状
> 配置里写的是 `ep=auto`（DirectML GPU 优先，失败自动回退 CPU），文档还承诺"低配笔记本自动回退"。
> 结果在一台 DirectML 支持不好的机器上**直接崩**：异常一路抛到上层，程序退出。
> 另一头还有个更隐蔽的问题：`aieval` 报告里的"样本数"把没评估成功的行也算进分母了，**F1 因此虚高**。

## 根因

**① 回退只覆盖了"注册执行提供器"，没覆盖"创建 Session"**

`OnnxRunner` 建会话其实有**两步**：

1. `so.AppendExecutionProvider_DML(0)` —— 注册 DirectML 执行提供器（这一步被 `try` 包住了）；
2. `new InferenceSession(modelPath, so)` —— 真正创建会话（**这一步在 `try` 外面**）。

而真实世界的失败大多发生在第 2 步：某个算子 DML 不支持、驱动异常、显存不够。异常从 `try` 外面冒出去 → 直接崩。

> [!warning] 教训的形状
> "我 try 了哪里"不等于"这条路上有几处会抛异常"。回退路径必须覆盖**所有**可能失败的点。

**② 分母不诚实**

`AiEvaluator.Total` 用的是"标注行数"，但图片缺失/无法解码的行被**静默跳过**。分母虚大、分子不变 → F1 和样本量都在骗人。这和验证集泄漏是同一类错误：**指标好看，但不可信**。

## 修复方式

**① 把创建会话放进 try**（`OnnxRunner.cs`）：

```csharp
try {
    _session = new InferenceSession(modelPath, so);
}
catch (Exception ex) when (auto && dmlApplied) {
    // auto 模式：DML 会话创建失败（算子不支持/驱动异常）→ 回退 CPU 重建
    Console.WriteLine($"  ⚠️ DirectML 会话创建失败，回退 CPU：{ex.Message}");
    so = BuildOptions(threads, wantDml: false, autoFallback: true, out _);
    provider = "CPU";                               // 如实记下"最后跑的是 CPU"
    _session = new InferenceSession(modelPath, so);
}
```

（`Provider` 属性会如实报告最终生效的是 `DirectML` 还是 `CPU`，诊断时能看出来到底降级了没有。）

- `ep=auto`：DML 建会话失败 → 静默回退 CPU（与文档承诺一致）；
- `ep=dml`：**保持严格语义**，失败即抛——不能偷偷降级，否则"我在测 GPU"这件事就不成立了。

**② `Total` 改为实际评估成功数，新增 `Skipped`**：

```
样本 376 | macro-F1 0.695 | ...
  ⚠️ N 行标注对应的图片缺失/无法解码，已排除在样本量之外（清理 labels.csv 或补回图片）
```

全缺失时**直接报错**，不产出假指标。

## 教训（以后怎么避免）

1. **回退路径要覆盖所有可能失败的点。** 数一数"这条路上有几处会抛异常"，而不是数"我写了几处 try"。
2. **分母要诚实。** 评估报告的"样本数"必须是"真的评估了这么多"；否则 F1 一定偏高，而且高得没有规律。
3. **静默跳过是最危险的容错。** 它让问题看起来像"数据少一点"，而不是"数据有问题"——和"验证集泄漏导致指标虚高"是同一种病。
4. **两种模式要语义分明**：`auto` 可以降级，`dml` 必须严格。"聪明地帮用户降级"在测量场景里是缺陷。

## 出处

- Git：`fe79292`（OnnxRunner auto 回退补全 + AiEvaluator `Total/Skipped` 口径修正）
- 代码：`DeltaUnlimited/Vision/Ai/OnnxRunner.cs`（`BuildOptions` / `catch (Exception ex) when (auto && dmlApplied)`）
- 代码：`DeltaUnlimited/Vision/Ai/AiEvaluator.cs`（`Total` / `Skipped` 注释与报告输出）
- 数据：`data/ai_vision.json` → `"ep": "auto"`、`drift` 三档门禁
- 文档：`docs/模块说明.md` §15（OnnxRunner「DirectML(GPU) 优先 → CPU 兜底」、AiEvaluator 职责）
