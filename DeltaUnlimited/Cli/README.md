# Cli/ —— 命令行工具箱（功能介绍 + 二次使用手册）

> 定位：DeltaUnlimited 的全部 CLI 动词都在这里实现并注册（`Program.cs` 路由 → `Commands/*.cs`）。其中相当一部分**可以脱离跑刀流程单独复用**——本文既是功能介绍，也是日后二次使用的操作手册。
> 运行方式：`cd DeltaUnlimited` 后 `dotnet run -- <命令>`；或仓库根 `dotnet run --project DeltaUnlimited\DeltaUnlimited.csproj -- <命令>`。
> ⚠️ 两条铁律：① `dotnet run` 每次先重新编译——**旧实例（ailabel/roitune/GUI）开着会锁 exe**（MSB3027），换命令前先关旧窗口；② 游戏必须**窗口化**（独占全屏抓不到画面）。

## A. 屏幕采集与识别诊断（复用性 ★★★★★）

做界面自动化调试、做识别模板、查窗口——这一组是通吃的。

| 命令 | 用途 | 产物 / 复用场景 |
|---|---|---|
| `capture [标题关键字] [文件名]` | 抓取指定窗口画面 | `screenshots/captured/`——任何"我要看它现在长什么样"的场景 |
| `crop <x> <y> <w> <h> <源图> [输出] [缩放]` | 从截图裁小图（可按设计分辨率换算） | 制作 `assets/templates/` 识别模板的标准流程 |
| `tplprobe <截图> <模板> [阈值]` | 模板匹配诊断 | 输出置信度与位置——调模板阈值必用 |
| `ocr <截图> [--region x y w h] [关键词...]` | OCR 诊断 | 输出全部识别词 + 关键词命中——调 OCR 标记必用 |
| `annotate <元素名> [截图]` | 元素识别静态验证 | 按 `elements.json` 策略（coord/template）画框输出 |
| `windows` | 列出全部可见顶层窗口 | 查窗口标题关键字（配 `--win` 参数） |

**二次使用套路**：capture 截屏 → crop 裁小图 → tplprobe 验证 → 填进 `data/elements.json` 或 `screens.json` → annotate 复验。五步造出一个新识别元素。

## B. 输入与拟人化验证（复用性 ★★★★）

| 命令 | 用途 |
|---|---|
| `click <元素名>` | 真实点击（点前/点后自动截图，3 秒倒计时） |
| `clickat <x> <y>` | 绝对坐标点击 |
| `mousetest <x> <y>` | 拟人鼠标轨迹肉眼验收（不点击）——贝塞尔弧线+过冲+抖动全在 |
| `hold <键名> <毫秒>` | 长按测试（帧差验证是否生效） |
| `turn <dx> [dy]` | 视角转动测试——**标定/验证 `px_per_90deg` 的唯一手段** |
| `drill [圈数]` | 靶场自主行进循环（移动+贴墙守卫+自动重试全链验证） |
| `click_operator` | 干员选择链路测试 |

**二次使用场景**：换鼠标/换灵敏度/换分辨率后，用 `mousetest`→`click`→`turn`→`drill` 顺序回归一遍输入链路。

## C. AI 数据闭环（复用性 ★★★★★，本系列新星）

| 命令 | 用途 | 说明 |
|---|---|---|
| `airecord [--map] [--season] [--interval 毫秒] [--max-gb]` | **零按键素材录制器** | 自动存全帧（默认 1-2s 随机 + 帧差去重），正常打游戏即可；全帧将来直接喂 YOLO |
| `ailabel [--start unlabeled\|first\|行号]` | **图形化标注器** | 左手 A/S/D/F 打标 + 右手 ←/→ 翻页（兼容 1/2/0/B），X 丢弃、N 跳下一未标、M 清缺失帧、G 弹窗跳任意张（行号/文件名片段）、PgUp/PgDn ±100；启动默认跳第一个未标帧（续标不用找位置），`--start first` 从头复览、`--start 430` 落到指定行；裁 ROI 写回训练 manifest，支持回翻复标，即时落盘 |
| `roitune [截图路径]` | **ROI 调参器** | 实时移动绿框标定模型视野，2:1 锁自动联动 input_size，回车保存 `ai_vision.json` |
| `aicollect [--map] [--season]` | 边玩边标（备用） | 控制台抢焦点式直播标注，键位 1/2/0/b 与 ailabel 同义 |
| `aieval [模型] [数据集目录]` | 金标集门禁评估 | 混淆矩阵 + macro-F1（≥0.85 才过门禁） |

配套的 Python 侧（在 `ai-training/`，见其 README）：`prescreen.py`（VLM 初筛，需 vLLM 服务）、`audit.py`（人工 vs VLM 对账）、`train.py` / `export_onnx.py`（训练与导出）、`smoke_test.py`（链路自检）。

**二次使用场景**：这套"录制→初筛→标注→训练→门禁"五件套与游戏内容无强耦合——换一个游戏/换一个分类问题，改掉 ROI 与类别表即可复用整条流水线。

## D. 流程与运行时（跑刀专用）

| 命令 | 用途 |
|---|---|
| `chain <workflow.json> [--auto]` | JSON 状态机链路（enter_match v7：任意界面启动/中断恢复） |
| `patrol [秒]` | 反应式巡逻伺服（贴墙+横移双守卫；**AI 感知 observe-only 挂载点**） |
| `overlay` | 游戏画面上的悬浮状态面板 |
| `gui` | 图形控制面板（无参数/双击 exe 同） |
| `smoke` | 数据层冒烟测试 |

## 依赖的底层库（同仓库，本文不展开）

`Capture/`（BitBlt 窗口截屏 + DPI 换算）、`Vision/`（模板匹配 / OCR 封装 / 帧差 / AI 推理 OnnxRunner）、`Input/`（SendInput 拟人键鼠）、`Overlay/`（悬浮面板）、`Data/`（全部 JSON 配置的读写模型）。改这些 = 改所有命令的公共行为，动前先读 `docs/模块说明.md`。
