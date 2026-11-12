---
title: 坑-GUI双输出与黑窗口
tags: [踩坑, GUI]
status: 已解决
created: 2026-11-13
related: ["[[模块-GUI]]", "[[坑-exe被占用]]"]
---

# 坑：GUI 日志打两遍，发布版还附带一个黑窗口

> [!bug] 症状
> 两种启动方式两种怪相：从终端 `dotnet run -- gui` 启动时，**同一行日志出现两份**（终端一份、GUI 日志窗一份）；发布版双击 exe 启动时，GUI 正常但**附带一个多余的控制台窗口**（黑窗）。

## 根因

Windows 控制台的**继承语义**：

- 从终端启动：子进程自动附着（attach）到父进程控制台，`Console.Out` 照常写终端；而 GUI 又用 Tee 把 `Console.Out` 转发给日志窗订阅者——同一个输出源被两个下游消费 → 双输出；
- 双击启动：进程仍可能挂着一个没人用的控制台 → 黑窗，日志往不存在的可见层写。

本质是没想清楚"日志的单一来源是什么、谁有权消费"，而不是"打了两遍"的代码 bug。

## 修复方式（`dfa6e99`）

三层，围绕"单一来源、多下游订阅"：

1. **GUI 模式启动即 `FreeConsole()`**——脱离任何控制台，源头消除双输出与黑窗；
2. **`SuppressConsoleOutput()`**——FreeConsole 后原句柄已无效，把 `Console.Out/Error` 底层 Writer 换成 `TextWriter.Null`，防止后续日志写无效句柄抛异常；
3. **`ConsoleRelay`**（`Gui/ConsoleRelay.cs`）：TeeWriter 接管 `Console.Out/Error`——每行原样写回原输出 + 推给订阅者（GUI 日志窗）；订阅前的输出进**环形缓冲（200 行）**，首个订阅者接入时补发（不漏启动日志）；全程锁 + `ConcurrentQueue` 线程安全。

## 教训（以后怎么避免）

1. **日志永远单源多汇**：只往 `Console.Out` 打，谁看（终端/GUI/文件）由订阅决定——两条打印路径必然双输出；
2. **Tee 要容忍下游写失败**：对原输出的每笔写都包 try/catch，下游坏了不能拖垮日志源；
3. **订阅前的日志要兜底**：环形缓冲补发是零成本方案，否则"启动阶段为什么没日志"永远查不清。

## 出处

- Git：`dfa6e99`（FreeConsole 脱离控制台 + 发布版 release）
- 代码：`DeltaUnlimited/Gui/ConsoleRelay.cs`（Attach / SuppressConsoleOutput / TeeWriter / 环形缓冲）
