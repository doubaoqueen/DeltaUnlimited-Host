---
title: 模块-chain引擎
tags: [模块, 链路]
status: 现行
created: 2026-10-23
related: ["[[架构-总览]]", "[[模块-伺服循环]]", "[[模块-视觉识别]]", "[[模块-数据表]]", "[[模块-GUI]]", "[[决策-双引擎架构]]", "[[坑-链路校验回归]]", "[[工具-CLI命令速查]]"]
---

# 模块-chain引擎

> [!info] 一句话
> 把"进场要走哪些步骤"从代码里搬到一张 JSON 步骤表（`workflows/*.json`）里，由一个很小的解释器按顺序跑；每一步只做一件事，跳转/分支/暂停全靠数据描述——**改流程不改代码**。

## 1. 它负责什么、为什么需要它

进场每局都要跑一遍：模式选择 → 点烽火地带 → 选零号大坝 → 开始行动 → 配装 → 入局提醒 → 出发 → 匹配 → 选干员。如果写成 C# 代码，改一个界面就要重新编译，谁也受不了。

所以这里选了一个**最笨但最好查**的方案：一张顺序执行的步骤表 + `jump_to` 跳转。它不是"状态机框架"，就是一个 `while (i < steps.Count) { switch (steps[i].op) ... }` 的循环（`ChainCommand.cs:54-419`）。好处是：出错时控制台会打印"第 37/68 步 click_element"，日志里也有同一句，一眼知道卡在哪。

一条链路长这样（`workflows/enter_match.json`，当前 **68 步**）：

```json
{
  "name": "进场链路 v7（switch_screen 一次识别查表分流）",
  "steps": [
    { "id": "detect_start", "op": "switch_screen", "max_loops": 3, "default": "unknown_tab",
      "branches": { "plaza_ready": "do_loadout", "matching": "wait_match" } },
    { "id": "do_loadout", "op": "click_element", "element": "plaza_loadout_button",
      "expect_screen": "loadout", "timeout_ms": 8000, "wait_ms": 600, "jump_to": "do_loadout_fail" }
  ]
}
```

## 2. op 全表（14 种，代码里就是 14 个 `case`）

| op | 干什么 | 关键字段 | 失败怎么走 |
|---|---|---|---|
| `key` | 抢焦点后按一下键 | `key`（支持 `"ctrl+left"` 组合串） | 按键名不认识 → 加载期就报错 |
| `click_element` | 点元素表里的元素 | `element`、`expect_screen`、`timeout_ms`(默认 8000)、`retries`(默认 0)、`jump_to` | 有 `jump_to` 软跳转；没有则**中止链路** |
| `wait` | 空操作（等待统一在步骤末尾做） | `wait_ms` | — |
| `wait_screen` | 轮询等某界面出现/消失 | `screen`、`absent`(true=等消失)、`timeout_ms`(默认 120000)、`jump_to` | 超时有 `jump_to` 就跳，否则中止 |
| `detect` | 识别当前界面并打印（含未配置标记的界面诊断） | — | — |
| `if_screen` | 当前界面 == 期望 → 跳转 | `screen`、`jump_to`(必填) | 未命中 → 顺序下一步 |
| `if_template` | 模板命中 → 跳转 | `template`、`threshold`(默认 0.85)、`jump_to`(必填) | 未命中 → 下一步 |
| `if_ocr` | 关键词严格命中（可多词计数） → 跳转 | `keywords`、`min_count`(默认 1)、`jump_to`(必填) | OCR 不可用 → 判未命中（fail-open） |
| `pause` | 人工确认点（半自动回车继续） | `message` | `--auto` 下直接跳过 |
| `jump` | 无条件跳转 | `jump_to`(必填) | — |
| `mark_unknown` | 未知界面留证：截图 + 候选 + 全帧 OCR 词表 | `message` | `--auto` 下不等人工 |
| `switch_screen` | 一次截图+识别，查 `branches` 表分流 | `branches`、`default`(必填)、`max_loops`(默认 3) | 熔断 → 人工确认或中止 |
| `pick_operator` | 干员选择（OCR 定位类型标签 → 相对偏移点头像） | 读 `operator_presets.json`，无必填字段 | 未配置 → 跳过保持当前干员 |
| `follow_marker` | 同步阻塞跑一轮导航标记跟随伺服 | `template`、`timeout_ms`(默认 180000)、`branches`、`default` | 结局无分支且无 `default` → 中止 |

> [!tip] `follow_marker` 是唯一的"重"op
> 它一个步骤里就跑完整个 8Hz 伺服循环（[[模块-伺服循环]]），结局用 `success/timeout/stopped/stuck/danger/error` 六种键名进 `branches`（小写，代码 `ChainCommand.cs:145` 用 `Outcome.ToString().ToLowerInvariant()`）。只有 `success` 会顺序落到下一步。

## 3. 每一步的收尾：wait_ms 与 capture

所有 op 执行完（`continue` 跳走的不算）都会做两件事（`ChainCommand.cs:412-418`）：

1. `wait_ms` > 0 → `Thread.Sleep(wait_ms)`；
2. `capture` 非空 → 截一张客户区图存到 `screenshots/captured/chain_<步号>_<capture>_<时分秒>.png`。

这两个字段是调链路节奏的"旋钮"：界面切换慢就给 `wait_ms`，想看现场就加 `capture`。**`wait_ms` 只在没有 `expect_screen` 时才重要**——有 `expect_screen` 时点击后已经在轮询验证了，不需要盲等（`docs/模块说明.md` §3"提速设计"）。

## 4. 点击后的验证：expect_screen / timeout_ms / retries

这是整条链路最关键的安全设计。`click_element` 的伪代码：

```
for attempt in 0..retries:
    ClickElementStep(...)              // 点击本身有落点校验，可能根本没点下去
    if 没有 expect_screen: 成功
    if WaitForScreen(expect_screen, timeout_ms): 成功
    else: sleep 800ms 再试
失败:
    存证据帧 screenshots/captured/chain_fail_*.png
    有 jump_to → 跳过去（通常是 mark_unknown 或者换条路重判）
    没有      → 抛异常中止整条链路
```

`enter_match.json` 里两种用法都有：`pick_zerodam`（点零号大坝）用 `retries: 1` + `jump_to: recheck_pick`，因为美术字卡片偶尔点空；`preset_apply`（点均衡卡消耗按钮）故意 `retries` 缺省（0）且 `timeout_ms: 2500`——**防双击**：这个按钮点一次就扣券回配装界面，绝不能因为超时短就手滑点第二次。

## 5. wait_screen 与"变更门"提速

`wait_screen` / `expect_screen` 共用一个轮询函数 `WaitForScreenState`（`ChainCommand.cs:438-503`），逻辑：

1. 先截一帧、归一化；
2. 缩到 **1/6** 做帧差（约 2ms）——`ChangeGate.ShouldDetect` 说"画面没动"就跳过整轮识别（一次 `Detect` ≈ 600-700ms）；
3. 该识别时识别；界面**变了**才打一行日志（可见的轮询心跳）；
4. 命中/消失 → 返回；否则 `sleep(poll_interval_ms)`（现 300ms）再来。

变更门的兜底是三条：首轮必扫、连续跳过 5 轮强制扫、距上次识别超 3 秒强制扫（`ChangeGate.cs:20-51`）。语义保守——**宁可多扫一次，不可漏掉界面切换**。等待期间的"空格继续"弹层还会被自动按掉（见下）。

## 6. switch_screen：12 个 if_screen 合并成 1 步

老写法是"12 个 `if_screen` 串起来"，每次都要重新截图+识别。现在一次识别后查表：

```json
{ "id": "detect_start", "op": "switch_screen", "max_loops": 3, "default": "unknown_tab",
  "branches": { "plaza_first": "prep", "plaza_ready": "do_loadout", "mode_select": "pick_mode", ... } }
```

两个自动行为：

- **弹层自动关闭**：识别到的界面若在 `screens.json` 里带 `dismiss` 键（如 `space_continue → "space"`），先按键关掉、`continue` 重跑本步骤重识别；这个"关闭重检"和 `default` 兜底**共用同一个熔断计数**，防止界面被误判成弹层时无限按空格（`ChainCommand.cs:354-371`）。
- **熔断**：`default` 连续触发超过 `max_loops`（默认 3）→ 半自动模式下弹人工确认并清零计数；`--auto` 下直接中止（`ChainStoppedException`），杜绝"匹配超时→重等→再超时"死循环（`ChainCommand.cs:385-397`）。

> [!warning] default 兜底必须存在
> `switch_screen` 没有匹配分支又没配 `default` 会直接抛错——加载期校验就拦（`ChainCommand.cs:595-596`）。`enter_match.json` 的 default 是 `unknown_tab`（按 Tab 回特勤处）；**局内链路绝不能这么兜底**，因为局内按 Tab 是计分板，所以才有 `screens.json` 的 `group` 分表（[[决策-局内界面分表group]]）。

## 7. 加载期校验（ValidateChain）：启动就炸，而不是运行时盲等

`ValidateChain`（`ChainCommand.cs:510-663`）在**第一条指令执行前**把整张表交叉检查一遍，攒够错误一次性抛出：

| 检查 | 为什么 |
|---|---|
| `steps` 为空 | 防止节点图格式（如 `demo_smoke.json`）误入 chain"空跑成功" |
| 重复 `id` | `FindStep` 只取第一个，重复 id 会让跳转悄悄落错步 |
| `click_element` 的元素存在、且策略参数齐全（ocr 有 keywords / coord 有 x,y / template 文件真实存在） | 缺一个就会在半夜三点炸在真机上 |
| `if_screen`/`wait_screen`/`expect_screen` 引用的界面存在**且至少有一个标记** | 引用无标记界面 = 永远等不到 |
| `if_ocr` 有 keywords 且 `min_count ≥ 1` | — |
| `if_template` 模板文件存在 | — |
| `key` 的每个 token 都能解析成虚拟键码（鼠标 left/right/middle 例外） | 拼错键名会静默按不动 |
| `switch_screen` 的 branches 键都是已知界面、跳转目标都存在、`max_loops ≥ 1` | — |
| `follow_marker` 模板存在、branches 键是合法伺服结局、跳转目标存在 | — |
| `if_*` 必须带 `jump_to` | 否则判断结果无处使用 = 空转 |
| 所有 `jump_to` / `default` / `expect_screen` 指向的 id 存在 | — |
| 未知 op | 拼错 `swich_screen` 不再静默放行 |

**死步骤可达性检查**（`FindUnreachableSteps`，`ChainCommand.cs:668-721`）是**告警不拦截**：从第 1 步出发，沿"顺序落到下一步"和所有跳转边（jump / switch_screen 分流 / if_* / wait_screen 超时 / click_element 软跳转）算可达集合，到不了的步骤打印一行 `⚠️ 链路校验告警`。它救过一次真实事故：匹配超时的人工确认点失联成了死分支（[[坑-链路校验回归]]）。

## 8. 半自动 pause 与 `--auto`

- 默认**半自动**：`pause` 和 `mark_unknown` 会停下来等人回车（CLI 轮询回车、Ctrl+C 急停；GUI 里则是暂停面板的"继续/中止"按钮，见 [[模块-GUI]]）。
- `chain <文件> --auto`：跳过所有人确认；但**熔断点没有人工出口**，所以 `--auto` 下熔断直接中止，而不是无限重试。
- 急停：`CommandUtil.InstallEmergencyStop()` 注册 Ctrl+C，只置标志 + 释放所有按键，不再 `Environment.Exit`；每个步骤边界和每个轮询周期都会 `AbortIfStopped()` 抛 `ChainStoppedException`，上层捕获后 `ReleaseAllHeldKeys()` 收尾，进程退出码保持 **130**（`Program.cs:81`）。

## 9. 写一条新链路的检查清单

- [ ] 顶层是 `{"name": ..., "steps": [...]}`，不是节点图的 `nodes/edges`
- [ ] 每个要跳转到的步骤都有 `id`，且**不重复**
- [ ] 每个 `if_*` 都带 `jump_to`
- [ ] `switch_screen` 一定有 `default`，且 `max_loops` 想清楚（默认 3）
- [ ] 所有 `click_element` 尽量配 `expect_screen`；点一下就会产生副作用的按钮（扣券/消耗）**不要**配 `retries`
- [ ] 失败路径通向 `mark_unknown`（留证 + 人工），而不是直接结束
- [ ] 用 `smoke` 或直接 `chain` 跑一遍：加载期校验会先把拼写错误挑出来

## 出处

- 代码：`DeltaUnlimited/Cli/Commands/ChainCommand.cs` —— op 分支 `60-410`；`wait_ms`/`capture` 收尾 `412-418`；`ChainStoppedException` 收尾 `422-427`；`WaitForScreenState`（变更门 + 弹层自动关）`438-503`；`ValidateChain` `510-663`；`FindUnreachableSteps` `668-721`
- 代码：`DeltaUnlimited/Cli/ElementClicker.cs:22-101`（点击前三步降级与"仅非前台才置顶"）
- 代码：`DeltaUnlimited/Vision/ChangeGate.cs:20-51`（变更门三条兜底）
- 代码：`DeltaUnlimited/Data/ChainModels.cs`（全部步骤字段定义）
- 代码：`DeltaUnlimited/Data/ScreenModels.cs:35-44`（`dismiss` / `group`）
- 数据：`workflows/enter_match.json`（68 步，v7 分流结构）、`workflows/demo_smoke.json`（节点图格式，会被 `WorkflowCatalog` 过滤掉）
- 文档：`docs/模块说明.md` §3（op 表、提速设计、耗时表）、§10（链路定义）
- 文档：`docs/跑刀工程提案.md` §1（`follow_marker` 薄 op 与线程模型）
