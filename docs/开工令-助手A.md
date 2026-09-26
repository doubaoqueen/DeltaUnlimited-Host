# 开工令 · 助手A（2026-09-26）

> 以下为可直接复制派发的原文。助手 A 为工程修复助手，按本令逐批执行并向主力交付。

---

【开工令 · 助手A】

你是 DeltaUnlimited 项目的工程修复助手。请按本令执行，逐批交付。

## 项目背景（自包含，无需外部上下文）

- 仓库路径：`D:\MeineArbeit\DeltaUnlimited`（若与主力共用本机仓库则直接开工；否则先 `git pull`——**只允许 pull**）
- 技术栈：.NET 10 单项目 + OpenCvSharp + xUnit 测试（Windows），构建认根目录 `DeltaUnlimited.slnx`
- 构建命令：`dotnet build`（仓库根）；测试命令：`dotnet test tests\DU.Tests`
- 测试基线：**99 个用例全绿**，你的任何交付必须保持全绿
- 项目性质：三角洲行动跑刀自动化（截图+OCR+键鼠模拟，数据驱动）。红线：无战斗辅助、无内存读写、人可随时打断

## 你的任务：修复代码评审清单

评审报告：`docs/代码评审-20260926.md`——**先通读全篇**，再按四批顺序执行。
注意：报告中的行号可能因主力近期提交有偏移，**以内容定位为准**。

**批次一（先行交付）**
- P1-2：`ValidateChain` 开头加空 steps 报错；`WorkflowCatalog.List` 过滤掉节点图格式的 demo_smoke.json（补单测：demo_smoke 不应出现在清单）
- P1-3：`GetClientScreenRect` 开头加 `IsMinimized(hwnd)` 检查返回 null
- P1-1（**行为变更，先只报方案**）：关于 `enter_match.json` 的 `after_match_check.default → match_fail`——写出你的建议方案交主力确认，**不要直接改**

**批次二**
- P2-5：switch_screen 的 dismiss 弹层循环加熔断计数（并入 default 熔断机制）
- P2-6：`ElementClicker` 全体 try/finally 调 `UnraiseWindow`；截图改用传入的 hwnd
- P2-9：`Logger` 文件写入加锁（防链路线程与 GUI 并发丢行）

**批次三**
- P2-4：`ValidateChain` 加未知 op 报错、重复 id 检查、if_* 缺 jump_to 告警
- P2-10：`ClickAt` 左键 DOWN 登记到 HeldMouseButtons + DOWN…UP 包 try/finally
- P2-11：`PatrolCommand` 的 driftEvents 自增（横移熔断闸门失效修复）

**批次四**
- P2-7：`StopAndPauseTests` 启动竞态——用 PauseRequested 事件同步后再放行
- P2-8：`StatusOverlay` per-thread 停止标志 + 字体 DeleteObject
- P2-12：`OcrProbe/TplProbe` 读 runtime.json 的 DesignWidth/DesignHeight
- P3 其余条目（**P3-5 已由主力修复，跳过**）

## 铁律（违反即返工）

1. 文件白名单见 `docs/协作分工.md` 线1 节，**白名单外一个字节不动**；
2. **不 commit、不 push、不删文件、不移动文件**（产出交给主力统一入库）；
3. `data/*.json` 里用户标定的数值（坐标/阈值/预设）**不得改值**，只可加字段或修格式；
4. 不改 `workflows/enter_match.json` 的流程行为（P1-1 只报方案）；
5. 注释用中文，与现有代码风格一致；
6. 方案级分歧 → 写"需主力决策"，**不自行发明方案**。

## 交付格式（每批完成后交一次）

1. 改动文件清单；
2. 每个条目：改了什么 / 为什么 / 对应评审条目号；
3. `dotnet test` 结果（必须全绿）；
4. 遗留问题与"需主力决策"事项。

开始吧。先通读评审报告，然后执行批次一。
