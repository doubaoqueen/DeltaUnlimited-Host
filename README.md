# DeltaUnlimited

三角洲行动 · 烽火地带模式"跑刀"流程的自动化辅助 —— **只做拾取与撤离流程自动化，不包含任何战斗/击杀辅助**（红线见 `docs/项目大纲.md`）。

> ⚠️ 合规提示：任何游戏自动化都有违反用户协议、封号的风险。本项目仅用于个人学习与合规场景；请低频使用、风险自担。

## 能力现状（开发期 CLI）

| 模块 | 能力 |
|---|---|
| 视觉 | 窗口自动定位 + GDI 实时截屏（窗口化）；设计分辨率 1920×1080 基准的坐标缩放层；模板匹配（内存缓存）；**界面识别**（`data/screens.json` 标记驱动） |
| 输入 | 鼠标绝对/相对移动（缓动拟人化 + 指针加速防漂移）、点击、长按、组合键（含鼠标键 token）；拟人化参数全部可配置（`runtime.json → humanizer`） |
| 决策雏形 | 顺序链路 runner（`chain`，支持 detect / if_screen / if_template 分支、jump、expect_screen 点击后验证重试）；靶场巡逻双守卫（贴墙/横移检测） |
| 可观测 | 统一日志（控制台 + 按日滚动 `logs/status_*.log`）；游戏画面上可拖动的悬浮状态面板（`overlay`） |
| 里程碑 | 进场链路第一跳全自动：大厅 → Tab → 识别常态广场 → 自动点"出发" → 选人界面 |

## 目录结构

```
src/ 布局（迁移中，当前主项目在 DeltaUnlimited/）
DeltaUnlimited/       主程序（Program.cs 入口 + Cli/ 命令 + Capture/Vision/Input/Overlay/Data）
tests/DU.Tests/       xUnit 测试（12+ 用例）
data/                 运行时数据（elements/game_ops/screens/runtime 等 JSON）
workflows/            链路定义（enter_match.json 等）
assets/templates/     识别模板小图
docs/                 规划与规格文档（早期大纲为 Python 版，见迁移注记）
```

## 构建与测试

```
dotnet build                # 仓库根（或 dotnet build DeltaUnlimited/DeltaUnlimited.csproj）
dotnet test tests\DU.Tests  # 单元测试
```

## CLI 命令速览

```
dotnet run -- capture  [窗口关键字]   截屏冒烟
dotnet run -- annotate <元素>        静态标注验证
dotnet run -- click <元素>           真实点击（点前/点后截图）
dotnet run -- chain enter_match.json 执行进场链路（--auto 全自动）
dotnet run -- patrol [秒]            靶场反应式巡逻
dotnet run -- drill [圈数]           靶场四边回路
dotnet run -- overlay                悬浮状态面板（需另开终端）
```

## 待办路线

1. 选人界面最后一跳（进场链路补全）
2. 搜刮/撤离链路：拾取提示（OCR）、撤离点识别
3. OCR 引擎接入（局部文字关键词）
4. 运行期截图保留策略（环形保留）
5. 全局急停热键（F8，不依赖控制台焦点）

## 版本

当前 v0.3.0（csproj `<Version>` 维护，启动时打印）。
