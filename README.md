# DeltaUnlimited

三角洲行动 · 烽火地带模式"跑刀"流程的自动化辅助（BetterGI 式）—— **只做拾取与撤离流程自动化，不包含任何战斗/击杀辅助**（红线见 `docs/项目大纲.md`）。

> ⚠️ 合规提示：任何游戏自动化都有违反用户协议、封号的风险。本项目仅用于个人学习与合规场景；请低频使用、风险自担。

## 能力现状（v0.4.0，开发期 CLI）

| 模块 | 能力 |
|---|---|
| 视觉识别 | 窗口自动定位 + GDI 截屏（1920×1080 基准缩放）；**OCR 为主（Windows.Media.Ocr）+ 模板匹配兜底 + 坐标最后兜底**的三层识别策略；界面识别表 `data/screens.json` 驱动（12 个界面真机标定：特勤处/模式选择/空格弹层/备战×2/地图池/部署面板/配装/入局提醒/匹配中/干员选择/结算画面），支持任意标记命中 + `require_all` 防串台 |
| 输入 | SendInput 键鼠模拟：**拟人鼠标轨迹**（随机贝塞尔弧线 + 缓动 + 过冲收回 + 逐点抖动，全程绝对坐标不受系统指针加速影响）、点击、长按、组合键；所有拟人化参数在 `runtime.json → humanizer` 可调（⚠️ 铁律：参数必须保持随机范围，禁止固定值） |
| 链路 | `enter_match.json` v5 状态机：**任意界面启动/中断恢复都自动接续**——模式选择→点烽火地带→选零号大坝→开始行动→确认配装→入局提醒→出发→匹配→干员选择；点击后 `expect_screen` 轮询验证、失败软跳转+人工确认；未知界面自动 `mark_unknown`（截图+OCR 词表留证）；结算画面自动空格跳过 |
| 安全 | Ctrl+C 急停自动释放所有按键；所有未知/异常界面默认暂停人工确认（fail-open）；拟人化+低频使用 |
| 可观测 | 控制台 + 按日滚动 `logs/status_*.log`；游戏画面上可拖动的悬浮状态面板（`overlay`）；`ocr`/`tplprobe` 诊断命令；链路每步截图留证 |
| 里程碑 | ✅ 进场链路端到端真机验证：从任意界面一路自动运行到干员选择界面（2026-09-21） |

## 设计哲学

- **数据驱动**：界面/元素/坐标/流程全部在 `data/*.json`、`workflows/*.json` 里。加新界面 = 加标记；加新按钮 = 加元素；加新流程 = 写链路；换识别方式不改代码。
- **识别分层**：OCR 严格命中才可行动；只"沾边"的候选只告警不行动；模板兜底；坐标是最后手段；全部失败 → 人工确认。
- **人可打断**：半自动默认，每个异常点人工回车继续；随时 Ctrl+C。
- 详细设计见 `docs/模块说明.md`、`docs/项目大纲.md`。

## 目录结构

```
DeltaUnlimited/       主程序（Program.cs + Cli/ + Capture/Vision/Input/Overlay/Data）
tests/DU.Tests/       xUnit 测试（79 用例，视觉/输入/数据层回归）
data/                 运行时数据（elements/screens/zones/runtime/game_ops/loadout_preset 等）
workflows/            链路定义（enter_match.json 等）
assets/templates/     识别模板小图
docs/                 项目大纲/模块说明/界面元素清单/导航设计/配装设计等
```

## 构建与测试

```
dotnet build                # 仓库根（经 DeltaUnlimited.slnx；或 dotnet build DeltaUnlimited/DeltaUnlimited.csproj）
dotnet test tests\DU.Tests  # 单元测试（79 用例）
```

## CLI 命令速览

> ⚠️ `dotnet run` 必须指定项目（`dotnet build` 认根目录的 `DeltaUnlimited.slnx`，但 `dotnet run` 不认解决方案）。两种等价写法：
> - 仓库根目录：`dotnet run --project DeltaUnlimited\DeltaUnlimited.csproj -- <命令>`
> - 或先 `cd DeltaUnlimited`，再 `dotnet run -- <命令>`（下面示例用简写）

```
dotnet run -- chain enter_match.json   # 进场链路 v5（任意界面启动，--auto 全自动）
dotnet run -- capture [关键字]          # 截屏冒烟
dotnet run -- ocr <截图> [--region x y w h] [关键词...]   # OCR 诊断（词表+关键词验证）
dotnet run -- tplprobe <截图> <模板> [阈值]               # 模板匹配诊断（置信度/位置）
dotnet run -- crop <x> <y> <w> <h> <截图> [输出] [缩放]   # 裁模板小图
dotnet run -- annotate <元素>           # 静态标注验证
dotnet run -- click <元素>              # 真实点击（点前/点后截图，3 秒倒计时）
dotnet run -- mousetest <x> <y>         # 拟人鼠标轨迹肉眼验收（不点击）
dotnet run -- hold <键> <毫秒>          # 长按测试（帧差验证）
dotnet run -- turn <dx> [dy]            # 视角转动测试（标定 px_per_90deg 用）
dotnet run -- drill [圈数]              # 靶场四边回路
dotnet run -- patrol [秒]               # 靶场反应式巡逻
dotnet run -- overlay                   # 悬浮状态面板（另开终端常驻）
dotnet run -- windows                   # 列出可见顶层窗口
```

## 待办路线

1. **干员选择自动化**：滚轮翻干员 → 锁定 → 出击（等干员界面截图/坐标标定）
2. **自动配装**：制式套装方案已接通链路（配装→制式套装→均衡预设→确认配装，确认才扣券），待真机实测（`docs/配装设计.md`）
3. **跑图导航**：跑向撤离点第一跳（撤离点模板识别 + 转向标定，`docs/导航设计.md`）
4. 长按空格 UI 变体（待坐标标定）
5. 全局急停热键（F8，不依赖控制台焦点）
6. 链路结构/速度优化：`switch_screen` 查表跳转（70+ 步 → 40 步）、轮询与等待参数（链路拉通后统一做）
7. **GUI**：BetterGI 式桌面窗口 + 悬浮面板 + 一键开始/急停（远期 ComfyUI 节点编排器）；CLI 保留为诊断后门

## 版本

当前 **v0.4.0**（csproj `<Version>` 维护，启动时打印）。变更历史见 git log。
