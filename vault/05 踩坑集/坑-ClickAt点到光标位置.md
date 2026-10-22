---
title: 坑-ClickAt点到光标位置
tags: [踩坑]
status: 已解决
created: 2026-10-23
related: ["[[模块-输入拟人化]]", "[[模块-截图与窗口]]", "[[坑-干员标签OCR不稳]]", "[[知识点-坐标系统与地图]]", "[[标定-界面标记与分组]]"]
---

# 坑：点击落在了鼠标当时所在的地方，不是我要点的目标

> [!bug] 症状
> 链路跑到"选干员"那一步，日志明明打印了"✅ 已点击"，游戏里被选中的却是**另一个干员**；更糟的时候鼠标停在屏幕角落，点下去什么都没发生，程序还以为成功了。
> 全程没有任何报错——**程序真诚地认为自己点对了**。

## 根因

两个错误叠在一起，单独一个都不会这么难查。

**① 设计坐标没换算成屏幕坐标**

整个项目的识别、模板、坐标都长在 **1920×1080 设计分辨率**上（`data/runtime.json` 的 `design_width/design_height`）。但鼠标要的是"屏幕绝对坐标"。只要游戏窗口不是"左上角对齐 + 正好 1920×1080"（窗口化、拖动过、DPI 缩放），这两套坐标就不是一回事。

当时 `click_element` 那条路径已经做了换算，**干员点击这条路径漏了**——`OperatorPicker` 直接把识别出来的设计坐标 `(ax, ay)` 当屏幕坐标喂给 `ClickAt`。

**② `ClickAt` 点的是"光标此刻在哪"，而且不检查**

`ClickAt` 做的事是：把鼠标移过去 → 停顿一下 → 按下左键 → 抬起。
问题在"按下"这一步：`SendInput` 的 `MOUSEEVENTF_LEFTDOWN` **没有坐标参数**——它点的是操作系统光标当前所在的位置。所以只要"移动"这一半有任何偏差（窗口缩放、DPI 拉伸、游戏抢光标、拟人轨迹的末点丢失），错误就会被**静默执行**成一次"点错地方"。

## 修复方式

**统一换算函数**（`CaptureService.MapDesignToScreen`）：

```
屏幕坐标 = 客户区屏幕原点 + 设计坐标 × (客户区尺寸 / 设计尺寸)
```

纯函数（客户区矩形由调用方传入）、有单测，`ElementClicker` 和 `OperatorPicker` 都改用它；干员循环里**每次重新取客户区**（窗口可能被拖动/改大小）。

**`ClickAt` 变成"带安全网的点击"**：

| 步骤 | 行为 |
|---|---|
| 1 | 拟人轨迹移动到目标 |
| 2 | 读回光标实际落点，与目标比对 |
| 3 | 偏差 > `humanizer.mouse.verify_tolerance_px`（默认 **6px**）→ 用绝对定位**直接钉正一次**（无拟人、无过冲） |
| 4 | 再读回校验 → 还偏 → **放弃点击，返回 `false`** |
| 5 | 只有校验通过的位置才会真的按下左键 |

调用方拿到 `false` 就**不当成功**：链路交给后续等待/重试，干员选择直接"保持当前干员"退出。

**诊断**：`LogWindowAnchor()` 打一行"屏幕 / DPI / 窗口矩形 / 客户区 / 缩放比"，下次偏了一眼能看出来。

## 教训（以后怎么避免）

> [!warning] 凡是"点某个位置"，都要有落点校验
> 顺序永远是：**移动 → 问"我到底停在哪" → 对得上才按下去**。没有校验的点击 = 盲点；盲点错了不会报错，只会悄悄改掉游戏状态（选错干员、误开局、误点"出发"）。

1. 坐标要分清三种：**设计坐标 / 客户区坐标 / 屏幕坐标**。跨这个边界必须显式换算，而且换算函数要写成纯函数 + 单测（这里就是 `InputServiceTests` 里的 `MapDesignToScreen` 用例）。
2. **失败要能"什么都不做"**。`ClickAt` 返回 `false` 比"点错但继续跑"安全一个数量级——点错的代价往往不可逆。
3. 遇到"日志说成功、画面不对"，先怀疑"日志说的成功是什么标准"。这里的成功原本只是"事件发出去了"，不是"点对了地方"。

## 出处

- Git：`415e37f`（点击安全网 + 窗口坐标系换算，P0）
- 代码：`DeltaUnlimited/Input/InputService.cs`（`ClickAt` / `VerifyLanding`）、`DeltaUnlimited/Capture/CaptureService.cs`（`MapDesignToScreen` / `LogWindowAnchor`）、`DeltaUnlimited/Cli/OperatorPicker.cs`、`DeltaUnlimited/Cli/ElementClicker.cs`
- 数据：`data/runtime.json` → `humanizer.mouse.verify_tolerance_px = 6`
- 测试：`tests/DU.Tests/InputServiceTests.cs`（坐标换算 / 容差默认值）
- 文档：`docs/模块说明.md` §6「窗口坐标系（2026-09 修）」
