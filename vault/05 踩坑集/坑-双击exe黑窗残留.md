---
title: 坑-双击exe黑窗残留
tags: [踩坑, GUI, 环境]
status: 已解决
created: 2026-11-13
related: ["[[坑-GUI双输出与黑窗口]]", "[[模块-GUI]]"]
---

# 坑：双击 exe 后黑窗一直残留（GUI 都出来了它还在）

> [!bug] 症状
> 双击 `DeltaUnlimited.exe`（bin 或 release），GUI 控制面板正常出现，**但旁边跟着一个 cmd 样的黑窗，GUI 关了它还在**。09-26 明明修过"双击黑窗立即消失"（[[坑-GUI双输出与黑窗口]]），v0.6.1 又复发。

## 根因

两件事叠在一起：

1. **exe 是控制台子系统**（csproj `OutputType=Exe`，PE 子系统 = 3）。任何一次"没有父控制台"的启动（双击/ explorer 调起），Windows 都要**给它配一个终端窗口**；
2. **Win11 的默认终端应用 = Windows Terminal**。启动时分配的是 WT 宿主窗（不是老 conhost）。程序里的 `FreeConsole()` 能让 **conhost** 窗立即关闭（09-26 修复的原理），但 **WT 宿主的窗不跟关**——客户端 `FreeConsole` 脱离后，WT 窗挂着不退。

排查时的坑中坑：用 EnumWindows 按**进程 PID** 过滤可见窗口找不到它——WT 窗属于 `WindowsTerminal.exe`，不属于本进程；类名也不是 `ConsoleWindowClass`。按 PID 过滤会系统性漏掉这种"宿主窗"。

## 修复方式（双轨，2026-11-13，tag v0.6.2）

| 轨道 | 做法 |
|---|---|
| **日常双击** | 发布 **WinExe 版**：`dotnet publish -c Release -r win-x64 --self-contained false -p:OutputType=WinExe -o release`。GUI 子系统（PE 字节验证 3→2）——OS **从不分配**控制台，黑窗从机制上不存在。产物 `release\DeltaUnlimited.exe` 就是日常双击入口 |
| **CLI 不受影响** | 开发期 CLI 仍是控制台构建（`dotnet run -- xxx`），输出正常；发布版 exe 从 cmd 调 CLI 命令时用 `AttachConsole(ATTACH_PARENT_PROCESS)` 接回父控制台让输出可见（`Cli/ConsoleAttach.cs`；重定向/管道时跳过） |

验证：发布版 PE 子系统 = 2；explorer 调起 → GUI 正常出现、无崩溃；GUI 进程可见窗只有表单 + ComboLBox。

## 教训（以后怎么避免）

1. **"FreeConsole 关黑窗"依赖宿主实现**——conhost 时代成立，WT 时代不成立。写"关闭窗口"类结论时标注验证环境；
2. **GUI 日常入口应当是 GUI 子系统（WinExe）构建**，控制台构建只服务 CLI/开发——两个诉求别挤同一个产物；
3. 排查"多出来的窗"时别只按自己进程 PID 过滤——宿主窗（Windows Terminal/conhost）属于别的进程。

## 出处

- 事件日志：无崩溃记录（GUI 本体一直正常）；
- PE 字节验证：旧 release exe 子系统 3 → 新发布 2（2026-11-13）；
- 代码：`DeltaUnlimited/Cli/ConsoleAttach.cs`、`Program.cs`（CLI 路径 TryAttachParent）；
- Git：v0.6.2（发布命令见 README"构建与测试"）。
