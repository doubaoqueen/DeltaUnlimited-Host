---
title: 坑-exe被占用
tags: [踩坑]
status: 已解决
created: 2026-10-23
related: ["[[模块-GUI]]", "[[工具-CLI命令速查]]", "[[坑-链路校验回归]]", "[[知识点-工程实践]]", "[[路线图与待办]]"]
---

# 坑：编译报"文件被另一个进程使用"——其实是自己的程序还开着

> [!bug] 症状
> 改完代码 `dotnet build`，直接失败：

```
error MSB3027: 无法将“...\bin\Debug\net10.0\DeltaUnlimited.exe”复制到“...”。
         文件“...\DeltaUnlimited.exe”正由另一进程使用，因此该进程无法访问此文件。
```

> 代码一个字没错，编译却过不去。第二次遇到时还会怀疑是不是杀毒软件、是不是文件权限……其实都不是。

## 根因

**`dotnet run` 每次都会先重新编译**，产物落在同一个 `bin\Debug\net10.0\` 目录。而 Windows 不允许覆盖一个**正在执行的 exe**。

上一轮开着没关的东西里，任何一个都拿着这个 exe：

| 会锁住 exe 的 | 说明 |
|---|---|
| 图形控制面板（`gui` / 双击 exe） | 最常见的凶手——最小化到托盘了，看起来像关了 |
| `ailabel` 标注器 | 一开就挂着等你按键 |
| `airecord` 录制器 | 长期后台录制 |
| `roitune` ROI 调参器 | 实时预览窗 |
| `overlay` 悬浮状态面板 | 常驻进程 |

所以这不是代码问题，是**"上一轮没退干净"**。

## 修复方式（按顺序试）

1. **关旧实例**：GUI 走托盘右键「退出」（最小化 ≠ 退出）；控制台里 `Ctrl+C`；再重新 build。
2. **不想关、只想跑命令** → 换配置构建：`dotnet build -c Release` / `dotnet run -c Release -- <命令>`。Release 的输出目录不同，跟 Debug 实例互不干扰。
3. **必须杀进程**时：
   - 先看清是谁：`Get-Process DeltaUnlimited | Select-Object Id, MainWindowTitle`
   - **先问用户**（那可能是他正在用的 GUI 或正在录的一局），说明"要杀掉正在跑的实例、会中断当前操作"，得到同意再 `Stop-Process`。
   - ⛔ 不要默认"反正是我自己开的"——录制/标注进行到一半被杀，丢的是素材和标注进度。

> [!warning] 杀进程是"改用户现场"
> 能绕（换 Release）就不要杀；要杀就先问、先说清代价。

## 教训（以后怎么避免）

1. **构建前先想"谁可能锁着 exe"**——这是 Windows 上的常态，不是异常。
2. 报错里出现 **MSB3027**（无法复制到目标，文件被占用）/ **MSB3021**（无法把源文件复制到目标）这类"文件操作"字样时，先查进程，别改代码。
   > MSB3027 是本仓库文档里留档的那条（见 `DeltaUnlimited/Cli/README.md` 开头"两条铁律"）；MSB3021 属同一根因的另一半，❓本库未留原始终端输出。
3. **工具用完就关**。`ailabel` / `airecord` / GUI 这类"开着等你操作"的程序，最容易忘；写进操作习惯比事后排查便宜。
4. 采素材/跑长任务之前，先关旧窗口——`docs/K0采集清单.md` 开头专门提醒了这一条。

## 出处

- 文档：`DeltaUnlimited/Cli/README.md` 开头两条铁律（"旧实例（ailabel/roitune/GUI）开着会锁 exe（MSB3027），换命令前先关旧窗口"）
- 文档：`docs/K0采集清单.md` 开头（"采之前先关掉旧的 ailabel/roitune/GUI（会锁 exe）"）
- 文档：`docs/模块说明.md` §13（`dotnet build` / `dotnet run` 的构建运行方式）
- Git：`dfa6e99`（GUI 发布版 release 构建，跨配置构建的由来）
