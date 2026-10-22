---
title: 工具-CLI命令速查
tags: [工具, CLI]
status: 现行
created: 2026-10-23
related: ["[[架构-总览]]", "[[模块-chain引擎]]", "[[模块-GUI]]", "[[模块-小地图定位器]]", "[[工具-Python脚本清单]]", "[[数据-数据表清单]]", "[[坑-exe被占用]]"]
---

# 工具-CLI命令速查

> [!info] 一句话
> 一共 **26 个命令**（含 `gui`），每个命令都是一次"可以独立跑的小实验"：截一张图、验一个模板、点一个按钮、跑一段链路、标一批帧——**它们是这个项目的"手术器械"，不是只有自动跑刀时才用**。

## 0. 怎么跑（两条铁律先记住）

```powershell
# 写法 A（推荐）：先 cd DeltaUnlimited，再
dotnet run -- <命令> [参数]

# 写法 B：仓库根目录
dotnet run --project DeltaUnlimited\DeltaUnlimited.csproj -- <命令> [参数]
```

> [!warning] 两条铁律
> ① `dotnet run` 每次都重新编译——**GUI / ailabel / roitune 开着会锁 exe**（MSB3027），换命令前先关旧窗口（[[坑-exe被占用]]）。
> ② 游戏必须**窗口化**，独占全屏截不到画面（截回来全黑）。

其他约定：

- 无参数或 `gui` → 图形控制面板；未知命令 → 打印用法（`Program.cs:21-27, 70`）。
- 退出码：`0` 正常 / `1` 抛异常（完整堆栈打到 stderr）/ `130` 急停（Ctrl+C 协作式收尾，`Program.cs:73-81`）。
- 路径一般写**相对仓库根**的路径（如 `screenshots/captured/x.png`、`assets/templates/y.png`）。

## A. 进场与流程

| 命令 | 干什么 | 典型用法 |
|---|---|---|
| `gui` | 图形控制面板（工作流/模式/开始/急停/暂停面板/日志/托盘） | `dotnet run -- gui`（双击 exe 同） |
| `chain <工作流> [--auto]` | 按 `workflows/*.json` 步骤表执行链路；半自动会在 `pause`/`mark_unknown` 停 | `dotnet run -- chain enter_match.json --auto` |
| `patrol [秒] [阈值] [连续] [90°px] [窗口]` | 反应式巡逻：持续冲刺 + 贴墙/横移双守卫自动右转；**AI 感知的挂载点**（observe-only） | `dotnet run -- patrol 60`（默认 60 秒 / 阈值 4.0 / 连续 3 / 用标定值） |
| `drill [圈数] [窗口]` | 靶场四边回路：每步动作帧差验证，失败重试一次后中止 | `dotnet run -- drill 2` |
| `overlay` | 游戏画面上的悬浮状态面板（另一个进程，常驻；置顶+穿透+可拖） | 另开终端 `dotnet run -- overlay`，Ctrl+C 退出 |

## B. 录制 / 标注 / AI 训练

| 命令 | 干什么 | 典型用法 | 产物 |
|---|---|---|---|
| `airecord [--map M] [--season S] [--interval ms] [--max-gb N] [--win K] [--diff D]` | **零按键**素材录制：正常打游戏，自动按 1-2 秒随机存全帧（帧差去重、容量上限） | `dotnet run -- airecord --map 零号大坝 --season S10` | `ai-training/datasets/record/full/*.jpg` + `record_manifest.csv` |
| `ailabel [--start unlabeled/first/行号] [--only-usable] [--gold-first]` | 图形标注器：A/S/D/F 打标、X 丢弃、N 下一未标、G 跳转、PgUp/PgDn 快翻；右侧显示该帧 VLM 初筛建议 | `dotnet run -- ailabel`（默认跳第一个未标） | 裁 ROI 写回训练 manifest |
| `aicollect [--map M] [--season S] [--win K]` | 边玩边标（**备用**）：控制台抢焦点，1/2/0/b 即时落盘 | `dotnet run -- aicollect` | `datasets/passability/` |
| `roitune [截图路径]` | ROI 调参器：实时/静态移动绿框，2:1 锁联动 `input_size`，回车写回 `ai_vision.json` | `dotnet run -- roitune` 或 `dotnet run -- roitune screenshots/captured/x.png` | 写 `data/ai_vision.json` |
| `aiprobe [--model 模型] [图片...]` | 亲眼看模型判定：实时（游戏画面逐帧）或静态（整帧截图） | `dotnet run -- aiprobe` / `dotnet run -- aiprobe shots/a.png` | 只读，不写运行日志 |
| `aieval [模型] [数据集目录]` | 金标集门禁评估：混淆矩阵 + macro-F1 + 延迟 + 三档判定 | `dotnet run -- aieval`（缺省用 `ai_vision.json` 的模型与金标目录） | 控制台报告 |

## C. 截图与识别诊断（复用性最高的一组）

| 命令 | 干什么 | 典型用法 | 产物 / 备注 |
|---|---|---|---|
| `capture [标题关键字] [文件名]` | 抓指定窗口画面（不给关键字 = 截整个桌面） | `dotnet run -- capture 三角洲行动 hall.png` | `screenshots/captured/`；含"疑似黑屏"检测 |
| `crop <x> <y> <w> <h> <源图> [输出] [缩放]` | 从截图裁模板小图（可按缩放换算） | `dotnet run -- crop 1660 880 70 18 screenshots/captured/hall.png assets/templates/x.png` | 输出路径默认 `screenshots/cropped.png` |
| `tplprobe <截图> <模板> [阈值]` | 模板匹配诊断：置信度 + 中心 + 模板尺寸 | `dotnet run -- tplprobe screenshots/captured/x.png assets/templates/depart_button.png` | 阈值默认 0.85 |
| `ocr <截图> [--region x y w h] [关键词...]` | OCR 诊断：全部识别词 + 每个关键词的严格/候选命中 + 混淆提示 | `dotnet run -- ocr shots/x.png --region 1300 880 620 200 出发` | 打印识别耗时 |
| `annotate <元素名> [截图]` | 元素识别静态验证：按 `elements.json` 策略画十字/绿框 | `dotnet run -- annotate depart_button` | `screenshots/annotated/<元素名>.png`；只支持 coord/template |
| `windows` | 列出全部可见顶层窗口（标题 + 句柄） | `dotnet run -- windows` | 查窗口标题关键字用 |

**五步造一个新识别元素**（`Cli/README.md` §A 的"二次使用套路"）：

```
capture 截图 → crop 裁小图 → tplprobe 验证命中 → 填进 data/elements.json 或 data/screens.json → annotate 复验
```

## D. 小地图定位

| 命令 | 干什么 | 典型用法 | 产物 |
|---|---|---|---|
| `localize <截图> [--map] [--scales a,b,..] [--minscore] [--topk] [--prior x,y[,scale[,r]]]` | 单帧定位诊断：小地图 ↔ 官方底图，输出候选与地图坐标 | `dotnet run -- localize screenshots/captured/x.png --topk 3` | `screenshots/captured/localize_<图名>.png`（底图画框） |
| `localizetrack <起> <止> [--maxspeed] [--map] [--scales]` | 按时间段批量定位（带双帧首锚/速度门/重锚定），输出轨迹 CSV + 跳变统计 | `dotnet run -- localizetrack "2026-10-12 00:28:20" "2026-10-12 00:30:30"` | `logs/localizetrack_*.csv` |

细节见 [[模块-小地图定位器]] §6。

## E. 输入验证与标定

| 命令 | 干什么 | 典型用法 | 备注 |
|---|---|---|---|
| `click <元素名> [窗口关键字]` | 真实点击（点前/点后自动截图，3 秒倒计时可 Ctrl+C 取消） | `dotnet run -- click depart_button` | 定位委托 `ElementClicker`（ocr 主 / coord 兜） |
| `clickat <x> <y>` | 屏幕绝对坐标拟人化点击（干员头像这类无文字元素用） | `dotnet run -- clickat 960 540` | 会告诉你"✅ 已点击 / ❌ 被安全网拦截" |
| `click_operator <类型> <序号>` | 干员选择链路测试（OCR 找类型标签 → 相对偏移点头像） | `dotnet run -- click_operator 突击 0` | 序号从 0 起 |
| `mousetest <x> <y>` | 拟人鼠标轨迹肉眼验收（**不点击**，可反复跑看随机性） | `dotnet run -- mousetest 1200 700` | 屏幕坐标 |
| `hold <键名或语义键> <毫秒>` | 长按测试（帧差验证是否生效） | `dotnet run -- hold move_forward 1500` | 语义键查 `game_ops.json` |
| `turn <dx> [dy]` | 视角转动测试（帧差验证）——**标定 `px_per_90deg` 的唯一手段** | `dotnet run -- turn 1000 0` | 见 [[标定-转向灵敏度]] |

输入链路的回归顺序（换鼠标/灵敏度/分辨率后）：`mousetest` → `click` → `turn` → `drill`。

## F. 其他

| 命令 | 干什么 |
|---|---|
| `smoke` | 数据层冒烟：加载全部 JSON 并打印统计（元素数/按键数/物品数/预设/点位/节点图） |

## G. 命令总数与"文档对不上"的地方

- 代码里实际注册的动词（`Program.cs:43-71`）：**25 个 + `gui` = 26 个**。
- `Cli/Usage.cs` 里默认打印的用法清单**只列了 20 个**（漏 `clickat` / `click_operator` / `localize` / `localizetrack` / `tplprobe` / `mousetest`）——以 `Program.cs` 路由和本篇为准。
- `docs/模块说明.md` §2 开头写"12 个命令"，但下面表格列了 21 个；实际 26 个。文档数字已过期。

## 出处

- 代码：`DeltaUnlimited/Program.cs:21-27`（无参数/gui → GUI）、`29-39`（CLI 初始化 + 未标定警告）、`43-71`（**命令路由权威表**）、`73-81`（异常与退出码）
- 代码：`DeltaUnlimited/Cli/Usage.cs:8-30`（默认用法清单，20 项）
- 代码：各命令参数解析：`CaptureCommand.cs:8-19`、`CropCommand.cs:10-17`、`TplProbeCommand.cs:12-15`、`OcrProbeCommand.cs:13-26`、`AnnotateCommand.cs:11-14`、`ClickCommand.cs:12-14`、`ClickAtCommand.cs:10-13`、`OperatorPickCommand.cs:13`、`MouseTestCommand.cs:10-13`、`HoldCommand.cs:12-17`、`TurnCommand.cs:11-20`、`DrillCommand.cs:15-24`、`PatrolCommand.cs:18-24`、`AiRecordCommand.cs:20-26`、`AiLabelCommand.cs:25-26, 62-78`、`AiCollectCommand.cs:26-28`、`RoiTuneCommand.cs:22`、`AiProbeCommand.cs:20-26`、`AiEvalCommand.cs:15-16`、`LocalizeCommand.cs:13-47`、`LocalizeTrackCommand.cs:14-38`、`OverlayCommand.cs:12-21`、`SmokeCommand.cs:8`
- 文档：`DeltaUnlimited/Cli/README.md`（A-D 四组命令 + 二次使用套路 + 两条铁律）
- 文档：`README.md`「CLI 命令速览」（注意其中 `dotnet run` 必须指定项目的说明）
- 文档：`docs/模块说明.md` §2（命令层表格，数字"12 个"已过期）
