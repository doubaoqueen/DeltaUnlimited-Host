
## 🔴 严重问题（必须修，否则部署不了/会崩/功能用不了）

### 1. csproj 没有配置任何资源文件复制 —— 发布后直接找不到配置

**位置**：`DeltaUnlimited.csproj` 全文

你的 csproj 里只有 PackageReference，**没有任何 `<Content>` / `<None CopyToOutputDirectory>` 配置**。这意味着 `dotnet build` / `dotnet publish` 之后，输出目录里**没有** `data/`、`workflows/`、`assets/` 文件夹。

开发时能跑是因为 `DataStore.FindRoot()` 从 `bin/Debug/net10.0/` 向上回溯，一直找到项目根目录才找到 `data/elements.json`。这是**靠目录结构巧合在工作**，不是靠正确的部署配置。

publish 目录里没有 data/，程序一启动就抛 `DirectoryNotFoundException: 找不到 data/elements.json`。

**改法**：csproj 里加：

```
<ItemGroup>
  <None Include="..\data\**\*" CopyToOutputDirectory="PreserveNewest" LinkBase="data" />
  <None Include="..\workflows\**\*" CopyToOutputDirectory="PreserveNewest" LinkBase="workflows" />
  <None Include="..\assets\**\*" CopyToOutputDirectory="PreserveNewest" LinkBase="assets" />
</ItemGroup>
```



### 2. 组合键解析是坏的 —— quick_pickup 等键位根本用不了

**位置**：`game_ops.json` 第15行 + `InputService.MapKeyName()`

`game_ops.json` 里定义了 `"quick_pickup": "ctrl+left"`，但 `InputService.MapKeyName(string key)` 只能解析**单个**键名（单字符或常用名），遇到 `"ctrl+left"` 这种字符串直接走 `default: return 0`，然后抛 `ArgumentException: 不认识的按键名: ctrl+left`。

`RunHold` 里 `Resolve("quick_pickup")` 返回 `"ctrl+left"` 字符串，然后 `HoldKey("ctrl+left", ms)` → `MapKeyName` 返回0 → 崩。

**改法**：要么 MapKeyName 支持 `+` 分割解析组合键，要么 game_ops.json 里把组合键改成数组形式（`["ctrl", "left"]`），然后 HoldKeys 直接传数组。后者更干净。

---

### 3. 顶层异常只打印 Message，不打印 StackTrace —— 出问题根本不知道哪行崩的

**位置**：`Program.cs` 第76-80行

```
catch (Exception ex)
{
    Console.Error.WriteLine($"❌ {ex.Message}");
    Environment.ExitCode = 1;
}
```

只打印 `ex.Message`，比如 `"没找到标题含"三角洲行动"的窗口"`，但**不打印是哪一行、哪个调用链抛的**。调试的时候你得自己猜是 RunClick 里还是 CaptureService 里崩的。

**改法**：`Console.Error.WriteLine(ex.ToString());` 一行搞定，包含消息+堆栈+内部异常。

---

### 4. TemplateMatcher 每次匹配都从磁盘读模板 —— 循环里反复 IO

**位置**：`TemplateMatcher.Match()` 第13行

```
using var tpl = Cv2.ImRead(templatePath, ImreadModes.Color);
```

`ScreenDetector.Scan()` 对每个 screen 的每个 marker 都调一次 `TemplateMatcher.Match()`，每次都 `Cv2.ImRead` 读磁盘。chain 循环里每跑一个 `if_screen` / `detect` 步骤，就把所有模板重新读一遍。

3个screen × 2个marker = 6次磁盘IO per detect步骤。跑10步就是60次。

**改法**：加静态缓存 `Dictionary<string, Mat>`，key是templatePath，第一次读了之后缓存，后续直接用。注意Mat的生命周期管理（程序退出时Dispose，或者用弱引用/懒加载）。

---

### 5. elements.json 和 screens.json 里留了半成品空配置 —— 静默失败

**位置**：`data/elements.json` 第8行 `hall_match_button` 的 x/y 是 `null`；`data/screens.json` 里 lobby/map_select/loadout/char_select 的 markers 是 `[]`

- `click hall_match_button` → 直接抛 `InvalidDataException: 元素 hall_match_button 的 x/y 还没填`
- `if_screen map_select` → ScreenDetector 里 `if (def.Markers.Count == 0) continue;` 直接跳过，永远返回"未命中"，**不报错不提示**，你还以为是识别率低

半成品配置留在表里，用的时候要么崩要么静默失败，比删掉更坑。

**改法**：没做完的元素/界面，要么从 JSON 里删掉，要么加一个 `"enabled": false` 字段，代码里检测到 disabled 就明确提示"该界面暂未配置模板"。不要留 null/空数组让代码静默跳过。

---

## 🟡 中等问题（应该修，影响稳定性/可维护性/体验）

### 6. Program.cs 是 800 行巨型文件，15个命令全堆在顶层

**位置**：`Program.cs` 全文

RunClick、RunSmoke、RunAnnotate、RunCapture、RunDrill、RunPatrol、RunChain、RunCrop、RunOverlay、RunHold、RunTurn、RunInputProbe、PrintUsage、ClickElementStep、MotionVerified —— **15个本地函数全部塞在一个文件里**，用 top-level statements 写的。

这不是"脚本"了，这是一个有完整架构的项目，所有命令堆在一个文件里，找东西靠 Ctrl+F，改一个命令怕影响别的。

**改法**：拆成 `Commands/` 目录，每个命令一个文件（`ClickCommand.cs`、`ChainCommand.cs`、`PatrolCommand.cs` 等），实现一个 `ICommand` 接口（`void Run(DataStore, string, string[])`），Program.cs 只做命令路由分发。20行搞定入口，剩下的各管各的。

---

### 7. chain 执行器没有超时和重试 —— 游戏一卡全乱

**位置**：`Program.cs` RunChain()

现在的逻辑：点击 → `Thread.Sleep(wait_ms)` → 下一步。

- 如果游戏卡顿、界面没切换，sleep 完了继续点下一步，全部点空
- 如果点击没点中（坐标偏移、窗口焦点丢了），没有重试，直接往下走
- 没有任何"操作后验证效果"的机制

你在 `drill` 和 `patrol` 里做了帧差验证（MotionVerified），但 **chain 执行器里完全没有**。click_element 点完就 sleep，不验证画面是否切换。

**改法**：click_element 步骤支持可选的 `expect_screen` 字段，点击后轮询检测目标界面，N秒内命中则继续，超时则重试点击（最多3次），仍失败则截图留证+中止。这才是可靠的自动化，不是"点了就当成功了"。

---

### 8. 没有任何单元测试

**位置**：整个项目没有 tests/ 目录

`FrameDiff.Score()`、`InputService.MapKeyName()`、`DataStore` JSON 解析、`TemplateMatcher`（可以用测试图）、坐标缩放换算逻辑 —— 这些都是纯函数，写单元测试成本极低。

没有测试的后果：你重构 Program.cs 拆分命令的时候，没有安全网，改坏了某个命令的逻辑不知道，等实际跑的时候才崩。

**改法**：建 `DeltaUnlimited.Tests/` 项目，用 xUnit 或 NUnit，先给纯函数写测试。MapKeyName 的边界情况（单字符、组合键、未知键）、FrameDiff 的相同帧/不同帧/尺寸不一致，这些都好测。

---

### 9. 日志系统是两套，且没有级别/滚动

**位置**：`StatusLog.cs` + 各处 `Console.WriteLine`

- `Console.WriteLine`：打控制台，不写文件
- `StatusLog.Append`：写文件，不打控制台
- 有些信息只打控制台（比如 CaptureService 的 debug 窗口列表），有些只写日志（chain 步骤状态）
- 没有日志级别（INFO/WARN/ERROR），全是纯文本
- `status.log` 无限增长，没有滚动/截断

**改法**：写一个轻量 `Logger` 类（或用 Serilog），统一入口 `Logger.Info()` / `Logger.Warn()` / `Logger.Error()`，同时输出到控制台和文件。文件按日期滚动（`status_20260906.log`），保留最近N天。把所有 Console.WriteLine 和 StatusLog.Append 全部替换成 Logger 调用。

---

### 10. CaptureService 里的 debug 输出会刷屏

**位置**：`CaptureService.CaptureWindowClient()` 第138-141行

```
Console.WriteLine($"[debug] 控制台窗口=0x{ownConsole.ToInt64():X}");
foreach (var w in wins)
    Console.WriteLine($"[debug]   0x{w.Hwnd.ToInt64():X} ...");
```

每次截图都列出**所有顶层窗口**。chain 跑10步截10次图，控制台刷10遍窗口列表。真正的错误信息被淹没。

**改法**：用 `#if DEBUG` 条件编译包裹，或者用 Logger.Debug() 级别控制，Release 构建不输出。

---

### 11. 拟人化参数全是硬编码魔法数字

**位置**：`InputService.cs` 各处

`Rng.Next(150, 320)`（点击后停顿）、`Rng.Next(100, 220)`（按下到抬起）、`Rng.Next(35, 90)`（按键时长）、`Rng.Next(3, 11)`（鼠标每步间隔）、`0.92 + Rng.NextDouble() * 0.16`（长按时长抖动范围）—— 全部裸写在代码里。

想调拟人化强度（比如表弟觉得太慢了想快点，或者想更像人一点），得改代码重新编译。

**改法**：建一个 `HumanizerConfig` 类，从 JSON 读取所有随机范围参数，InputService 的所有 Rng.Next 都从配置里取范围。这样调参不用改代码。

---

### 12. runtime.json 的 px_per_90deg 默认值是你个人的标定值

**位置**：`data/runtime.json` 第5行 `"px_per_90deg": 4500`

这个值 = 你的鼠标DPI × 游戏内灵敏度。表弟的电脑设置不一样，这个值就是错的，转向角度完全不对。

现在代码里如果 runtime.json 有值就直接用，不检测是否是默认值/未标定。

**改法**：默认值设为 `0` 或 `null`，程序启动时检测到 `px_per_90deg <= 0` 就打印警告："转向参数未标定，请先运行 turn 命令标定，或编辑 data/runtime.json"。不要用你个人的4500当默认值坑别人。

---

### 13. 没有版本号，程序启动不打印版本

**位置**：`DeltaUnlimited.csproj` + `Program.cs`

csproj 里没有 `<Version>` / `<AssemblyVersion>`，程序启动时 `RunSmoke` 只打印 "✅ DeltaUnlimited BotHost 启动"，不打印版本号。

表弟那边出了问题，你问他"你用的哪个版本"，他答不上来。

**改法**：csproj 加 `<Version>0.3.0</Version>`，Program.cs 启动时打印 `DeltaUnlimited v0.3.0`。用 `Assembly.GetExecutingAssembly().GetName().Version` 读取。

---

## 🟢 轻微问题（有空再修，代码质量/规范）

### 14. 命名空间和文件夹结构不匹配

代码在 `Core/Capture/` 目录，命名空间是 `DeltaUnlimited.Capture`，少了 `Core`。应该统一成 `DeltaUnlimited.Core.Capture`。

### 15. ImageAnnotator 和 TemplateMatcher 重复定义了 MatchResult

`ImageAnnotator.cs` 第29行和 `TemplateMatcher.cs` 第8行各定义了一个完全一样的 `MatchResult` record。提取到 `Vision/MatchResult.cs` 公共文件。

### 16. WorkflowModels.cs 注释写"与 Python 版一致"

第6行注释 `与 Python 版一致`，但项目已经纯C#了，没有Python版。删掉过时注释。

### 17. 项目大纲.md 全篇写 Python 技术栈，实际是 C#

这个之前提过。大纲里 PySide6、opencv-python、pytest 全是 Python，但代码是 C# + WPF/WinForms + OpenCvSharp + xUnit。要么更新大纲，要么在大纲开头注明"本文档为早期规划，实际已迁移至 C# 技术栈"。

### 18. 仓库根目录没有 README.md

find 的结果里没有 README.md。GitHub 项目的门面，别人点进来第一眼看到的是 docs/ 目录列表，不知道项目干嘛的、怎么编译、怎么用。必须补。

### 19. screenshots/captured/ 和 logs/ 可能没加 .gitignore

运行时产生的截图和日志不应该提交到 git。确认 .gitignore 里有 `screenshots/captured/`、`logs/`、`bin/`、`obj/`。

### 20. detect 步骤和 if_screen 功能重叠

detect 步骤只识别界面并打印，不做任何决策。真正的决策是 if_screen。detect 更像是个诊断命令，放在 chain 里意义不大。要么合并到 if_screen，要么明确标注为"仅调试用"。

---

## 修复优先级建议

| 优先级 | 问题 | 预计工作量 |
| --- | --- | --- |
| P0 立刻修 | #1 csproj资源复制、#2 组合键解析、#3 异常打印堆栈 | 30分钟 |
| P1 本周修 | #4 模板缓存、#5 半成品配置清理、#7 chain超时重试、#9 日志统一 | 4-6小时 |
| P2 近期重构 | #6 Program.cs拆分、#8 单元测试、#11 拟人化配置化、#12 标定参数检测 | 1-2天 |
| P3 有空再说 | #10 debug输出、#13 版本号、#14-20 规范类 | 零散时间 |

> 
