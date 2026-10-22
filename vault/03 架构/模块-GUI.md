---
title: 模块-GUI
tags: [模块, GUI]
status: 现行
created: 2026-10-23
related: ["[[架构-总览]]", "[[模块-chain引擎]]", "[[模块-截图与窗口]]", "[[工具-CLI命令速查]]", "[[坑-exe被占用]]", "[[01 项目总览]]"]
---

# 模块-GUI

> [!info] 一句话
> 图形控制面板不是另一套程序，而是**同一套核心换了个入口**：选工作流 → 点开始（后台线程跑 chain）→ 日志实时滚 → 急停永远有效；半自动时会在窗口顶部弹出"继续/中止"面板，关窗口则收进系统托盘。

## 1. 它负责什么、为什么需要它

CLI 的问题是：跑一次链路要开终端、敲命令、盯着一屏文字，急停还得记住 Ctrl+C。GUI 解决三件事：

| 需求 | 做法 |
|---|---|
| 不用记命令 | 工作流下拉框自动扫 `workflows/*.json`；模式下拉框只有"半自动/全自动"两项 |
| 急停要顺手 | 一个红色 ⛔ 急停按钮，和 Ctrl+C、暂停面板的"中止"**三路同源**（都走 `CommandUtil.RequestStop`） |
| 出问题要看得到 | 日志窗实时滚 + 托盘气泡通知（链路完成/停止/失败） |

## 2. 启动流程（`Gui/GuiApp.cs`）

```
ConsoleRelay.Attach()                // 接管 Console.Out/Error 做分流（原输出照写 + 每行推给订阅者）
FreeConsole()                        // 脱离控制台：双击 exe 时黑窗口立即消失
ConsoleRelay.SuppressConsoleOutput() // 原控制台写入器换 Null，避免每行写无效句柄
InputService.Configure(humanizer)    // 注入拟人化参数 + DPI 感知
Ocr.Initialize(runtime.OcrEngine)    // 初始化 OCR
px_per_90deg <= 0 → 打印未标定警告
专用 STA 线程 → Application.Run(new MainForm(...))   // 控制台入口线程是 MTA，不能直接跑 WinForms
主线程 ui.Join()
```

（`GuiApp.cs:14-35`）

## 3. 主窗口有什么（`Gui/MainForm.cs`）

| 区域 | 内容 | 说明 |
|---|---|---|
| 顶部控制区 | 工作流下拉框（宽 240）、模式下拉框（宽 210）、▶ 开始、⛔ 急停、清空日志 | 运行时把工作流/模式下拉框禁用，防运行中切换 |
| 暂停面板（顶部，默认隐藏） | 提示文字 + ▶ 继续 + ⛔ 中止链路 | **非模态**：急停按钮始终可用；中止 = 打断暂停闸门 + 置急停标志 |
| 中间 | 日志窗（只读多行 TextBox，Consolas 9pt，深色） | 封顶 **3000 行**，超出裁掉头部；每 100ms 批量刷新一次（不在链路线程里碰 UI） |
| 底部 | 状态栏 | "就绪 / 运行中 / 暂停点 / 链路完成 / 已急停 / 失败" |
| 系统托盘 | 双击恢复；右键菜单：显示面板 / 开始 enter_match / 急停 / 退出 | 链路结束弹气泡（3 秒） |

细节：

- **工作流清单**：`WorkflowCatalog.List(repoRoot)` 只列**顶层含 `steps` 数组**的 JSON（节点图格式如 `demo_smoke.json`、损坏文件一律不进清单，防止"空跑成功"）；按文件名排序；默认选中 `enter_match.json`（`WorkflowCatalog.cs:11-38`）。
- **点击开始**：先把本次选择写入 `data/gui_settings.json`（失败静默）→ `CommandUtil.ResetStop()` → `SetRunning(true)` → `Task.Run(() => ChainCommand.Run(store, root, ["chain", wf, auto ? "--auto" : ""]))`。链路在后台 Task 上跑，UI 不卡（`MainForm.cs:166-201`）。
- **点击急停**：`CommandUtil.RequestStop()` → 置标志 + `ReleaseAllHeldKeys()` + 打断暂停闸门。链路线程会在下一个步骤边界/轮询点看到标志，抛 `ChainStoppedException` 收尾（[[模块-chain引擎]]）。
- **关窗口**：默认**不退出**，收进托盘（`e.Cancel = true`）。真退出只能走托盘菜单"退出"——退出时会请求急停并**等链路线程最多 2 秒**收尾，避免按键释放存在竞态窗口（`MainForm.cs:297-320`）。
- **记住窗口位置**：只有正常态（非最大化/最小化）才写回坐标；下次启动会校验"尺寸 ≥ 最小尺寸 且 与某块屏幕工作区相交"，否则回退默认居中（`MainForm.cs:101-115`）。

## 4. 暂停点是怎么连起来的

半自动模式下 chain 里的 `pause` / `mark_unknown` 会调 `CommandUtil.WaitForResume(message)`：

```
CLI：没有注入 PauseHandler → 自己轮询回车（可被急停打断）
GUI：MainForm 构造时 CommandUtil.PauseHandler = PauseGate.Wait
      → 链路线程阻塞在 ManualResetEventSlim 上
      → PauseGate.PauseRequested 事件（链路线程触发）→ BeginInvoke 转到 UI 线程显示面板
      → 用户点"继续" → PauseGate.Resume()   / 点"中止" → Interrupt() + RequestStop()
```

（`Cli/CommandUtil.cs:41-64, 73-114`；`MainForm.cs:152-154, 225-240`）

一个健壮性细节：如果窗口已经销毁/句柄没建好，`OnPauseRequested` 会直接 `PauseGate.Interrupt()` 放行，避免链路线程永久阻塞（`MainForm.cs:228-232`）。

## 5. 设置记忆：`data/gui_settings.json`

```json
{ "schema_version": 1, "last_workflow": "enter_match.json", "auto_mode": false,
  "window_x": 1330, "window_y": 664, "window_w": 1180, "window_h": 760 }
```

- 字段定义在 `Data/GuiSettingsModels.cs`；
- 读取用 `LoadJsonOrDefault`：**文件缺失/损坏/空 json 一律回退默认值**，绝不打断启动（`DataStore.cs:60-73, 92-96`）；
- 它是**本机个人设置**，已 gitignore（`GuiSettingsModels.cs:5-6`），不入库、不参与发布数据。

## 6. 状态浮层（Overlay）与 GUI 的关系

`overlay` 是**另一个进程**（另开一个终端常驻），不是主窗口的一部分：

| 项 | 事实 |
|---|---|
| 启动 | `dotnet run -- overlay`，等游戏窗口出现（每 2 秒重试），拿到客户区后 `StatusOverlay.Start(x,y,w,h)` |
| 窗口特性 | 分层窗口：置顶 + 鼠标穿透 + 不抢焦点；面板区域可拖动（`WM_NCHITTEST`：面板 = `HTCAPTION`，其余穿透） |
| 渲染 | GDI，后台线程 20ms 消息泵 + 渲染循环 |
| 内容 | 读最新按日日志 `logs/status_*.log` 的**最后 12 行**，每 500ms 刷新一次 |
| 与截图的关系 | 注册截图钩子，`BitBlt` 那一瞬间隐藏自己，避免面板被截进帧（[[模块-截图与窗口]] §5） |

（`Cli/Commands/OverlayCommand.cs:11-58`；`docs/模块说明.md` §7）

## 7. GUI 与 CLI 的分工

| | CLI | GUI |
|---|---|---|
| 定位 | 诊断后门 / 标定工具 / 批处理 | 日常使用入口 |
| 命令 | 26 个动词（[[工具-CLI命令速查]]） | 只有"跑哪条链路 + 半自动/全自动" |
| 输出 | 控制台 + 日志文件 | 日志窗（同一份日志，经 ConsoleRelay 分流） |
| 急停 | Ctrl+C | 按钮 / Ctrl+C / 暂停面板中止 |
| 共用 | 截屏、识别、输入、数据、链路、急停、暂停闸门、OCR、AI 感知 | 同左 |

> [!warning] 两条运行铁律（与 CLI 相同）
> ① `dotnet run` 每次都会重新编译，**GUI/ailabel/roitune 开着会锁 exe**（MSB3027），换命令前先关旧窗口（[[坑-exe被占用]]）；
> ② 游戏必须**窗口化**，独占全屏截不到画面。

## 8. 还没做的

| 项 | 状态 |
|---|---|
| G3 打磨：状态可视化（AI 感知/伺服心跳/剩余限额） | ❌ 未做，目前只有文字状态栏 |
| 三套配装预设的 GUI 选择（均衡/生存/火力） | ❌ 数据槽 `loadout_preset.json → presets` 为空，待标定另两张卡的按钮坐标 |
| 悬浮面板整合进主窗口 | ❌ 现在仍是独立 `overlay` 进程 |
| 全局急停热键（F8，不依赖窗口焦点） | ❌ 待办（`README.md` 待办第 5 条） |

## 出处

- 代码：`DeltaUnlimited/Gui/GuiApp.cs:14-35`（控制台分流 / FreeConsole / STA 线程）
- 代码：`DeltaUnlimited/Gui/MainForm.cs:16`（日志封顶 3000 行）、`41-162`（控件构建与设置恢复）、`166-201`（StartChain 后台 Task）、`203-216`（结束通知）、`218-252`（暂停面板/中止/运行态切换）、`256-275`（日志批量刷新）、`279-336`（托盘/关闭/退出收尾）
- 代码：`DeltaUnlimited/Gui/WorkflowCatalog.cs:11-38`（只列 steps 格式）
- 代码：`DeltaUnlimited/Gui/ConsoleRelay.cs`（Tee + 200 行环形缓冲 + 订阅补发）
- 代码：`DeltaUnlimited/Cli/CommandUtil.cs:10-29`（StopRequested / RequestStop / AbortIfStopped）、`41-64`（WaitForResume）、`73-114`（PauseGate）
- 代码：`DeltaUnlimited/Cli/Commands/OverlayCommand.cs:11-58`（等待窗口 / 日志尾部 / 500ms 刷新）
- 代码：`DeltaUnlimited/Data/GuiSettingsModels.cs`、`Data/DataStore.cs:60-73, 92-96`
- 数据：`data/gui_settings.json`
- 文档：`docs/模块说明.md` §7（可观测层）、§14（图形控制面板：文件职责与设计要点）；`README.md`（GUI 能力与待办）
