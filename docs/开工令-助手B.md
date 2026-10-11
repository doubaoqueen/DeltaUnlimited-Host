# 开工令 · 助手B（跑刀工程线 S0 续工）

> 依据：`docs/协作分工.md` §3（线2 任务卡与红线）、`docs/跑刀方案.md`（用户 v1）、`docs/跑刀工程提案.md`（技术草案）。
> 状态：提案 §9 分叉点已全部拍板（分表=`group` 字段 ✓、伺服频率=8Hz ✓、会话限额=240 分钟/0 局 ✓），开工前置条件满足。
> 开工前**先读**：`docs/协作分工.md`（铁律+红线+交付格式）→ `docs/跑刀工程提案.md`（施工依据）→ 本开工令。

## 0. 现状说明（主力已先行完成的部分，**不要重做**）

为配合主线节奏，S0 的一部分已由主力完成并入库（master `dc495e6`）。你接手**剩余部分**。先读这些文件理解既定接口：

| 已完成 | 位置 |
|---|---|
| ServoLoop 周期循环（8Hz、急停检查、到达/卡死/超时/阻塞结局、delegate 注入可测） | `DeltaUnlimited/Vision/Servo/ServoLoop.cs` |
| 伺服控制器纯函数（P 控制+死区 40px+抖动+钳制 260px、IsArrived/IsStuck） | `DeltaUnlimited/Vision/Servo/ServoController.cs` |
| ServoTypes（ServoOutcome/ServoResult/ServoFrame/ServoAction） | `DeltaUnlimited/Vision/Servo/ServoTypes.cs` |
| screens 分表（`ScreenDef.Group`，默认 "lobby"，Detector 带 group 过滤） | `DeltaUnlimited/Data/ScreenModels.cs`、`Vision/ScreenDetector.cs` |
| 会话限额（`SessionLimits`：240 分钟/0 局） | `DeltaUnlimited/Data/RuntimeModels.cs`、`data/runtime.json` |
| 已有 12 个伺服测试 + 反射测试钉死"ServoAction 不得有点击字段" | `tests/DU.Tests/` |

**当前测试基线：161 用例全绿**（协作分工.md 里写的 99 是旧数）。

## 1. 你的任务（按顺序交付，每项都要单测）

| # | 任务 | 对应提案 | 要点 |
|---|---|---|---|
| 1 | **EconomyAgent 纯函数**：`ShouldPick(itemName, quality, backpackState)` / `ShouldDiscard(...)` / `TimeToExtract(...)` | §5 | 白名单起步、不做价值函数；每个规则一行 JSON 数据（`data/item_catalog.json` 模型 + 默认白名单）+ 一个 xUnit 钉住 |
| 2 | **感知信号框架**：三档（廉价/中等/昂贵）+ 五要素（检测区域/阈值/防抖窗口/失败回落/真机出处）落表 | §2 | 实现 K0 无关的廉价信号纯函数：①受击红屏检测（边缘 ROI 红色像素占比 + 连续 N 帧确认，阈值带随机余量）；②容器高光/物资光柱 HSV 统计；③FrameDiff 双守卫**复用** patrol 既有实现（不重写）。交互提示模板检测只留接口+五要素配置结构，模板素材等 K0 |
| 3 | **`data/runtime.json` 新增 `servo` 段** + `RuntimeModels.ServoConfig` | §4 | 转向增益范围/死区/噪声/每周期最大动作量钳制/漫游停留时长范围——**全部随机范围**（铁律），只增字段不改既有值 |
| 4 | **fixtures 管线**：`tests/fixtures/` 目录结构 + 读取约定 + 一个"无素材时跳过"的示例测试 | §6 | 素材等用户 K0 采集后入库，现在只搭骨架 |
| 5 | **`FollowMarkerOp` 薄壳**：把 ServoLoop 三件套（capture/act/perceive）接成可注入的 op；perceive 的"导航标记模板匹配"留接口（K0 后由主线填实现） | §1 | 纯函数+注入 fake context 单测，覆盖四结局：成功/超时/急停/卡死；**不写搜刮、不写点击**（ServoAction 反射测试保持绿） |

**红线（线2 特有，重复一遍）**：不写任何搜刮/拾取/点击代码（K1 跑通前）；不动 `workflows/enter_match.json`；不预设路线表、不背点位表；`ServoAction` 不得新增点击字段；所有拟人参数必须随机范围。

## 2. 文件白名单

`DeltaUnlimited/Vision/`（新增感知信号相关）、`DeltaUnlimited/Data/`（EconomyAgent 模型、item_catalog 模型、RuntimeModels servo 段）、`tests/DU.Tests/`（新增）、`data/runtime.json`（只增 servo 段）、`data/item_catalog.json`（新增文件，默认白名单）。白名单外一个字节不动；`data/screens.json` 的 group 字段已完成，勿再改。

## 3. 交付格式（与线1 一致）

每项完成后提交：**改动文件清单 + 每个条目改了什么/为什么 + `dotnet test tests\DU.Tests` 测试结果**。不 commit、不 push、不删文件、不移动文件；产出交给主力 review 后统一入库。遇到方案级分歧写"需主力决策"报告，不得自行发明方案。
