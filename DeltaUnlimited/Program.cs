using DeltaUnlimited.Capture;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
using DeltaUnlimited.Overlay;
using DeltaUnlimited.Vision;
using OpenCvSharp;

// ===== DeltaUnlimited 开发期 CLI =====
// 用法：
//   dotnet run                                  → Phase 0 数据层冒烟
//   dotnet run -- annotate <元素名> [截图路径]    → 静态识别冒烟（输出标注图）
//   dotnet run -- capture [标题关键字] [文件名]   → 截屏冒烟（桌面 / 指定窗口客户区）
//   dotnet run -- windows                        → 列出当前可见窗口（找游戏窗口标题用）

var root = DataStore.FindRoot();
var store = new DataStore(root);
var command = args.Length > 0 ? args[0].ToLowerInvariant() : "smoke";

try
{
    switch (command)
    {
        case "smoke":
            RunSmoke(store);
            break;

        case "annotate":
            RunAnnotate(store, root, args);
            break;

        case "capture":
            RunCapture(root, args);
            break;

        case "click":
            RunClick(store, root, args);
            break;

        case "hold":
            RunHold(store, root, args);
            break;

        case "turn":
            RunTurn(root, args);
            break;

        case "drill":
            RunDrill(store, root, args);
            break;

        case "patrol":
            RunPatrol(store, root, args);
            break;

        case "chain":
            RunChain(store, root, args);
            break;

        case "crop":
            RunCrop(root, args);
            break;

        case "overlay":
            RunOverlay(store, root);
            break;

        case "windows":
            RunListWindows();
            break;

        default:
            PrintUsage();
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"❌ {ex.Message}");
    Environment.ExitCode = 1;
}

// ===== 命令实现 =====

void RunClick(DataStore data, string repoRoot, string[] cmdArgs)
{
    if (cmdArgs.Length < 2) throw new ArgumentException("用法: click <元素名> [窗口关键字(默认 三角洲行动)]");
    string elementName = cmdArgs[1];
    string winKeyword = cmdArgs.Length > 2 ? cmdArgs[2] : "三角洲行动";

    var table = data.LoadElements();
    if (!table.Elements.TryGetValue(elementName, out var def))
        throw new ArgumentException($"元素表里没有 “{elementName}”。现有: {string.Join(", ", table.Elements.Keys)}");
    if (def.Strategy != "coord" || def.Params.X is null || def.Params.Y is null)
        throw new InvalidDataException($"元素 {elementName} 需要 coord 策略且 x/y 已填写才能点击");

    var runtime = data.LoadRuntime();

    IntPtr hwnd = CaptureService.FindWindowByTitle(winKeyword);
    if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{winKeyword}” 的窗口");

    var r1 = CaptureService.GetClientScreenRect(hwnd)
        ?? throw new InvalidOperationException("窗口不可用（最小化？）");
    int absX = (int)Math.Round(r1.X + def.Params.X.Value * (r1.W / (double)runtime.DesignWidth));
    int absY = (int)Math.Round(r1.Y + def.Params.Y.Value * (r1.H / (double)runtime.DesignHeight));
    Console.WriteLine($"计划点击: 元素 “{elementName}” 画面内 ({def.Params.X}, {def.Params.Y}) → 屏幕 ({absX}, {absY})");

    // 第 1 步：点击前截图（确认现场，人工可查）
    string prePath = Path.Combine(repoRoot, "screenshots", "captured", $"pre_{elementName}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
    Console.WriteLine("第 1 步: 点击前截图...");
    Console.WriteLine(CaptureService.CaptureWindowClient(winKeyword, prePath));

    // 第 2 步：3 秒倒计时（可 Ctrl+C 取消）
    Console.WriteLine("第 2 步: 3 秒后点击，可 Ctrl+C 取消...");
    Thread.Sleep(3000);

    // 第 3 步：置顶 + 重新定位（防窗口被移动）+ 点击
    CaptureService.RaiseWindow(hwnd);
    Thread.Sleep(250);
    var r2 = CaptureService.GetClientScreenRect(hwnd);
    if (r2 is null)
    {
        CaptureService.UnraiseWindow(hwnd);
        throw new InvalidOperationException("点击前窗口不可用（最小化？）");
    }
    absX = (int)Math.Round(r2.Value.X + def.Params.X.Value * (r2.Value.W / (double)runtime.DesignWidth));
    absY = (int)Math.Round(r2.Value.Y + def.Params.Y.Value * (r2.Value.H / (double)runtime.DesignHeight));
    Console.WriteLine($"第 3 步: 点击 屏幕({absX}, {absY})...");
    InputService.ClickAt(absX, absY);
    CaptureService.UnraiseWindow(hwnd);

    // 第 4 步：点击后截图（画面是否切换，人工确认）
    Thread.Sleep(1200);
    string postPath = Path.Combine(repoRoot, "screenshots", "captured", $"post_{elementName}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
    Console.WriteLine("第 4 步: 点击后截图...");
    Console.WriteLine(CaptureService.CaptureWindowClient(winKeyword, postPath));

    Console.WriteLine($"\n请人工确认 {postPath} 画面是否已切换 —— 若已切换说明点中了！");
}

void RunSmoke(DataStore data)
{
    Console.WriteLine("✅ DeltaUnlimited BotHost 启动");

    var elements = data.LoadElements();
    Console.WriteLine($"[elements.json] 元素识别表: {elements.Elements.Count} 个");
    foreach (var (name, def) in elements.Elements)
        Console.WriteLine($"  - {name}: strategy={def.Strategy}");

    var runtime = data.LoadRuntime();
    Console.WriteLine($"[runtime.json] 90°px={runtime.PxPer90Deg} 窗口关键字={runtime.WindowKeyword}");

    var ops = data.LoadGameOps();
    Console.WriteLine($"[game_ops.json] 按键 {ops.KeyMap.Count} 个, 复合动作 {ops.Actions.Count} 个");
    Console.WriteLine($"  interact -> {ops.KeyMap.GetValueOrDefault("interact", "?")}");

    var items = data.LoadItemValues();
    Console.WriteLine($"[item_values.json] 材料 {items.Materials.Count} 种");

    var points = data.LoadZeroDamPoints();
    Console.WriteLine($"[zero_dam_points.json] 撤离点 {points.ExtractPoints.Count}, 搜刮点 {points.LootPoints.Count}");

    var wf = data.LoadWorkflow("demo_smoke.json");
    Console.WriteLine($"[demo_smoke.json] '{wf.Name}': {wf.Nodes.Count} 节点, {wf.Edges.Count} 边");

    Console.WriteLine("\nPhase 0 数据层验证完成 ✅");
    Console.WriteLine("提示：dotnet run -- annotate hall_match_button  可跑静态视觉冒烟");
}

void RunAnnotate(DataStore data, string repoRoot, string[] cmdArgs)
{
    if (cmdArgs.Length < 2) throw new ArgumentException("用法: annotate <元素名> [截图相对路径(默认 screenshots/hall.png)]");

    string elementName = cmdArgs[1];
    string imageRel = cmdArgs.Length > 2 ? cmdArgs[2] : "screenshots/hall.png";

    var table = data.LoadElements();
    if (!table.Elements.TryGetValue(elementName, out var def))
        throw new ArgumentException($"元素表里没有 “{elementName}”。现有: {string.Join(", ", table.Elements.Keys)}");

    string imagePath = Path.Combine(repoRoot, imageRel);
    string outPath = Path.Combine(repoRoot, "screenshots", "annotated", $"{elementName}.png");

    switch (def.Strategy)
    {
        case "coord":
        {
            int? x = def.Params.X, y = def.Params.Y;
            if (x is null || y is null)
                throw new InvalidDataException($"元素 {elementName} 的 x/y 还没填（elements.json），无法标注");
            ImageAnnotator.AnnotatePoint(imagePath, outPath, x.Value, y.Value, elementName);
            Console.WriteLine($"✅ coord 标注完成: ({x}, {y})  输出: {outPath}");
            break;
        }

        case "template":
        {
            string? tplRel = def.Params.Template;
            if (string.IsNullOrEmpty(tplRel)) throw new InvalidDataException($"元素 {elementName} 的 template 路径没填");
            string tplPath = Path.Combine(repoRoot, tplRel);
            double threshold = def.Params.Threshold ?? 0.8;
            var r = ImageAnnotator.AnnotateTemplate(imagePath, outPath, tplPath, threshold, elementName);
            if (r.Found)
                Console.WriteLine($"✅ template 命中! 置信度 {r.Confidence:F3}  中心 ({r.CenterX}, {r.CenterY})  框 {r.W}x{r.H}  输出: {outPath}");
            else
                Console.WriteLine($"❌ 未命中（最佳置信度 {r.Confidence:F3} < 阈值 {threshold}）。输出: {outPath}");
            break;
        }

        default:
            throw new NotSupportedException($"annotate 暂不支持 strategy={def.Strategy}（现支持 coord/template；ocr 需要接入 OCR 引擎，color 待实现）");
    }
}

void RunCapture(string repoRoot, string[] cmdArgs)
{
    string? keyword = cmdArgs.Length > 1 ? cmdArgs[1] : null;
    string outName = cmdArgs.Length > 2 ? cmdArgs[2] : $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.png";
    string outPath = Path.Combine(repoRoot, "screenshots", "captured", outName);

    CaptureService.CaptureOutcome outcome = string.IsNullOrEmpty(keyword)
        ? CaptureService.CaptureDesktop(outPath)
        : CaptureService.CaptureWindowClient(keyword, outPath);

    Console.WriteLine(outcome);
    Console.WriteLine("提示: 若疑似黑屏，检查是否窗口化/游戏是否被独占全屏，或换关键字再试");
}

// ===== 靶场自主行进循环 drill：每步动作帧差确认，失败重试一次后自动中止 =====

void RunDrill(DataStore data, string repoRoot, string[] cmdArgs)
{
    int rounds = cmdArgs.Length > 1 && int.TryParse(cmdArgs[1], out int rv) && rv > 0 ? rv : 2;
    var runtime = data.LoadRuntime();
    string winKeyword = cmdArgs.Length > 2 ? cmdArgs[2] : runtime.WindowKeyword;

    const int fwdMs = 1600;      // 每段冲刺前进时长
    const double minScore = 3.0; // 帧差阈值：低于此值视为"没动"
    const int settleMs = 450;    // 动作结束后的稳定等待
    int turnPx = (int)Math.Round(runtime.PxPer90Deg); // 右转 90° 的像素量（runtime.json 标定，与 EDPI 相关）

    var ops = data.LoadGameOps();
    string Resolve(string semantic) => ops.KeyMap.GetValueOrDefault(semantic, semantic);

    IntPtr hwnd = CaptureService.FindWindowByTitle(winKeyword);
    if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{winKeyword}” 的窗口");

    // 急停：Ctrl+C 时先释放所有按键再退出（防止按键卡死）
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        InputService.ReleaseAllHeldKeys();
        Console.WriteLine("\n⛔ 手动急停，已释放所有按键");
        Environment.Exit(130);
    };

    Console.WriteLine($"靶场自主行进循环：{rounds} 圈（每圈 = 冲刺前进 + 右转90° 的 4 边回路）");
    Console.WriteLine("2 秒后开始；Ctrl+C 随时急停；若贴墙/卡住会自动重试后中止。请盯紧屏幕！");
    Thread.Sleep(2000);

    string fwdKey = Resolve("move_forward");
    string sprintKey = Resolve("sprint");

    for (int round = 1; round <= rounds; round++)
    {
        for (int side = 1; side <= 4; side++)
        {
            Console.WriteLine($"\n[第 {round}/{rounds} 圈 · 边 {side}/4]");
            if (!MotionVerified(hwnd, winKeyword, repoRoot, "冲刺前进", () => InputService.HoldKeys(new[] { fwdKey, sprintKey }, fwdMs), settleMs, minScore))
                throw new InvalidOperationException("前进两次验证失败，已自动中止（画面未变化：贴墙了？焦点丢了？）");
            if (!MotionVerified(hwnd, winKeyword, repoRoot, "右转约90°", () => InputService.MoveRelative(turnPx, 0), settleMs, minScore))
                throw new InvalidOperationException("转向两次验证失败，已自动中止（画面未变化）");
        }
        Console.WriteLine($"\n✅ 第 {round} 圈完成");
    }
    Console.WriteLine($"\n🎉 自主行进循环完成 {rounds} 圈，共 {rounds * 4} 段动作全部帧差验证通过");
}

/// <summary>执行一个动作并用帧差验证：失败自动重试一次（重新抢焦点），仍失败则留证据帧并返回 false。</summary>
bool MotionVerified(IntPtr hwnd, string winKeyword, string repoRoot, string desc, Action action, int settleMs, double minScore)
{
    bool RunOnce(string label, out double score)
    {
        using var pre = CaptureService.CaptureWindowMat(winKeyword);
        bool focused = InputService.EnsureForeground(hwnd);
        Thread.Sleep(250);
        try
        {
            action();
        }
        finally
        {
            InputService.ReleaseAllHeldKeys();
        }
        Thread.Sleep(settleMs);
        using var post = CaptureService.CaptureWindowMat(winKeyword);
        score = FrameDiff.Score(pre, post);
        Console.WriteLine($"  {label}帧差 {score:F2} {(score >= minScore ? "✅" : "❌")}{(focused ? "" : "（⚠️ 焦点丢失）")}");
        return score >= minScore;
    }

    Console.WriteLine($"动作: {desc}");
    if (RunOnce("", out double s1)) return true;

    Console.WriteLine("  第一次未见效果，重试一次...");
    if (RunOnce("重试", out double s2)) return true;

    // 留证据后判定失败
    try
    {
        string ev = Path.Combine(repoRoot, "screenshots", "captured", $"drill_fail_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        var d = Path.GetDirectoryName(ev);
        if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
        using var frame = CaptureService.CaptureWindowMat(winKeyword);
        if (Cv2.ImWrite(ev, frame)) Console.WriteLine($"  证据帧已保存: {ev}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  证据保存失败: {ex.Message}");
    }
    return false;
}

// ===== 反应式巡逻 patrol v2：双守卫（贴墙停 + 横移低效），转向量可标定 =====
// 守卫1：画面连续低变化 → 判定完全贴墙卡住
// 守卫2：长时间（slowDriftSec）没有高速运动样本 → 判定贴墙横移/低效前进（帧差无法区分"朝前走"与"横移"，用速度谱启发式补洞）
// 转角与 EDPI 相关：同 px 在不同 DPI/灵敏度下转角不同，用第 4 参数传入标定后的 90°px

void RunPatrol(DataStore data, string repoRoot, string[] cmdArgs)
{
    var runtime = data.LoadRuntime();
    double maxSeconds = cmdArgs.Length > 1 && double.TryParse(cmdArgs[1], out double ms1) ? ms1 : 60;
    double stuckThreshold = cmdArgs.Length > 2 && double.TryParse(cmdArgs[2], out double st) ? st : 4.0;
    int needLow = cmdArgs.Length > 3 && int.TryParse(cmdArgs[3], out int nl) ? nl : 3;
    int turnPx = cmdArgs.Length > 4 && int.TryParse(cmdArgs[4], out int tp) ? tp : (int)Math.Round(runtime.PxPer90Deg);
    string winKeyword = cmdArgs.Length > 5 ? cmdArgs[5] : runtime.WindowKeyword;

    const double sampleEveryMs = 350;
    const double highScore = 8.0;     // ≥ 此值 = 明确的高速前进（开阔地冲刺实测 ~27）
    const double slowDriftSec = 6.0;  // 这么久没有高速样本 → 疑似贴墙横移/低效前进
    const int maxEvents = 20;         // 事件过多 → 场地过小/打转，中止

    var ops = data.LoadGameOps();
    string Resolve(string semantic) => ops.KeyMap.GetValueOrDefault(semantic, semantic);
    string fwdKey = Resolve("move_forward");
    string sprintKey = Resolve("sprint");

    IntPtr hwnd = CaptureService.FindWindowByTitle(winKeyword);
    if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{winKeyword}” 的窗口");

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        InputService.ReleaseAllHeldKeys();
        Console.WriteLine("\n⛔ 手动急停，已释放所有按键");
        Environment.Exit(130);
    };

    Console.WriteLine($"反应式巡逻 v2：最长 {maxSeconds}s | 守卫1 贴墙: 连续 {needLow} 次差 < {stuckThreshold} | 守卫2 横移: {slowDriftSec}s 无 ≥{highScore} 样本 | 转向 {turnPx}px(标定)");
    Console.WriteLine("开始后角色将持续冲刺前进；受阻自动转向。Ctrl+C 急停。");
    Console.WriteLine("2 秒后开始，请盯紧屏幕！");
    Thread.Sleep(2000);

    var start = DateTime.Now;
    int samples = 0, lowRun = 0, stuckEvents = 0, driftEvents = 0;
    double diffSum = 0, lastHighAt = 0;
    Mat? prev = null;

    void Escalate(double t, string reason)
    {
        stuckEvents++;
        int total = stuckEvents + driftEvents;
        Console.WriteLine($"  ⚠️ [t={t:F1}s] {reason}（贴墙#{stuckEvents} / 横移#{driftEvents}）→ 松开前进键");
        StatusLog.Append(repoRoot, $"⚠️ {reason}（贴墙#{stuckEvents}/横移#{driftEvents}）→ 右转");
        InputService.ReleaseAllHeldKeys();
        Thread.Sleep(400);
        Console.WriteLine("  ↻ 右转中...");
        InputService.MoveRelative(turnPx, 0);
        Thread.Sleep(400);
        InputService.PressKeys(new[] { fwdKey, sprintKey });
        lowRun = 0;
        lastHighAt = t;
        prev?.Dispose();
        prev = null; // 重置参考帧，避免起步阶段误判
        Console.WriteLine("  ▶ 继续冲刺前进");
        if (total >= maxEvents)
            throw new InvalidOperationException($"受阻事件已达 {total} 次仍反复卡住——场地可能过小/在角落打转，请换开阔区域后重试");
    }

    try
    {
        InputService.EnsureForeground(hwnd);
        InputService.PressKeys(new[] { fwdKey, sprintKey });
        Console.WriteLine("▶ 开始冲刺前进...");
        StatusLog.Append(repoRoot, "▶ 巡逻开始：持续冲刺前进，受阻自动转向");

        while ((DateTime.Now - start).TotalSeconds < maxSeconds)
        {
            Thread.Sleep((int)sampleEveryMs);
            // 首个采样带置顶等待；后续窗口已在前台，跳过置顶提速
            Mat cur = CaptureService.CaptureWindowMat(winKeyword, prev is null);
            if (prev is not null)
            {
                double d = FrameDiff.Score(prev, cur);
                samples++;
                diffSum += d;
                double t = (DateTime.Now - start).TotalSeconds;
                if (d < stuckThreshold)
                {
                    lowRun++;
                    Console.WriteLine($"  [t={t:F1}s] 运动差 {d:F2}（低变化 {lowRun}/{needLow}）");
                }
                else
                {
                    lowRun = 0;
                    if (d >= highScore) lastHighAt = t;
                    if (samples % 8 == 0)
                    {
                        Console.WriteLine($"  [t={t:F1}s] 运动差 {d:F2}（移动中·心跳）");
                        StatusLog.Append(repoRoot, $"巡逻 t={t:F1}s 运动差 {d:F2}");
                    }
                }

                if (lowRun >= needLow)
                    Escalate(t, "判定贴墙卡住（画面连续低变化）");
                else if (samples > 8 && t - lastHighAt >= slowDriftSec)
                    Escalate(t, "疑似贴墙横移/低效前进（长时间无高速运动样本）");
            }
            prev?.Dispose();
            prev = cur;
        }
    }
    finally
    {
        prev?.Dispose();
        InputService.ReleaseAllHeldKeys();
    }

    double avg = samples > 0 ? diffSum / samples : 0;
    Console.WriteLine($"\n🏁 巡逻结束：运行 {(DateTime.Now - start):hh\\:mm\\:ss} | 采样 {samples} 次 | 平均运动差 {avg:F2} | 贴墙事件 {stuckEvents} | 横移/低效事件 {driftEvents}");
    StatusLog.Append(repoRoot, $"🏁 巡逻结束：采样 {samples} 平均差 {avg:F2} 贴墙 {stuckEvents} 横移 {driftEvents}");
    Console.WriteLine("转角标定（与 EDPI=DPI×游戏灵敏度 相关，换设置需重标）:");
    Console.WriteLine("  面朝固定参照物 → 重复执行 turn 1000 0 直到回到原方向，记次数 n（转一圈约需 n 次）");
    Console.WriteLine($"  → 90° 的 px ≈ 250×n，当前标定值 {runtime.PxPer90Deg} 已存 data/runtime.json（EDPI 变化时更新它，或用第 4 参临时覆盖）");
    Console.WriteLine("校准提示: 贴墙不识别→阈值调大; 开阔地假贴墙→调小/加严: patrol 60 2.5 4");
}

// ===== 链路执行 chain：按 JSON 步骤顺序执行（半自动默认带人工确认点，--auto 全自动） =====

void RunChain(DataStore data, string repoRoot, string[] cmdArgs)
{
    if (cmdArgs.Length < 2) throw new ArgumentException("用法: chain <workflow文件> [--auto]");
    string chainFile = cmdArgs[1];
    bool auto = cmdArgs.Any(a => a.Equals("--auto", StringComparison.OrdinalIgnoreCase));

    var chain = data.LoadChain(chainFile);
    var runtime = data.LoadRuntime();
    var elements = data.LoadElements();
    var screens = data.LoadScreens();
    string winKeyword = runtime.WindowKeyword;

    IntPtr hwnd = CaptureService.FindWindowByTitle(winKeyword);
    if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{winKeyword}” 的窗口");

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        InputService.ReleaseAllHeldKeys();
        Console.WriteLine("\n⛔ 手动急停，已释放所有按键");
        Environment.Exit(130);
    };

    Console.WriteLine($"执行链路: {chain.Name}（{chain.Steps.Count} 步） 模式: {(auto ? "全自动" : "半自动（pause 步骤回车继续）")}");
    Console.WriteLine("Ctrl+C 随时急停。");

    int FindStep(string id)
    {
        int idx = chain.Steps.FindIndex(x => x.Id == id);
        if (idx < 0) throw new InvalidDataException($"链路中不存在步骤 id: “{id}”");
        return idx;
    }

    int i = 0;
    while (i < chain.Steps.Count)
    {
        var s = chain.Steps[i];
        Console.WriteLine($"\n[步骤 {i + 1}/{chain.Steps.Count}] {s.Op}");
        StatusLog.Append(repoRoot, $"链路 {chainFile} 步骤 {i + 1}/{chain.Steps.Count} {s.Op}");
        switch (s.Op)
        {
            case "key":
                InputService.EnsureForeground(hwnd);
                InputService.PressKey(s.Key ?? throw new InvalidDataException("key 步骤缺少 key 字段"));
                StatusLog.Append(repoRoot, $"按键 {s.Key}");
                break;

            case "click_element":
                ClickElementStep(hwnd, elements, s.Element ?? throw new InvalidDataException("click_element 步骤缺少 element 字段"), runtime.DesignWidth, runtime.DesignHeight);
                StatusLog.Append(repoRoot, $"点击元素 {s.Element}");
                break;

            case "wait":
                break; // 统一在步骤末尾按 wait_ms 等待

            case "detect":
            {
                using var frame = CaptureService.CaptureWindowMat(winKeyword);
                using var norm = FrameTools.Normalize(frame, runtime.DesignWidth, runtime.DesignHeight);
                var scan = ScreenDetector.Scan(norm, screens, repoRoot);
                var hit = scan.FirstOrDefault(c => c.Matched);
                string msg;
                if (hit is not null)
                {
                    var actions = screens.Screens[hit.Name].Actions;
                    msg = $"识别界面: {hit.Name}（置信度 {hit.Confidence:F3}）可用操作: {string.Join(" / ", actions)}";
                }
                else
                {
                    var top = string.Join("  ", scan.Take(3).Select(c => $"{c.Name}={c.Confidence:F2}"));
                    msg = $"识别界面: 未知（最接近: {top}）";
                }
                Console.WriteLine($"  👁 {msg}");
                StatusLog.Append(repoRoot, msg);
                break;
            }

            case "if_screen":
            {
                string want = s.Screen ?? throw new InvalidDataException("if_screen 步骤缺少 screen 字段");
                using var frame = CaptureService.CaptureWindowMat(winKeyword);
                using var norm = FrameTools.Normalize(frame, runtime.DesignWidth, runtime.DesignHeight);
                var guess = ScreenDetector.Detect(norm, screens, repoRoot);
                string got = guess?.Name ?? "unknown";
                Console.WriteLine($"  👁 界面判断: 当前 {got} / 期望 {want} → {(got == want ? "命中 ✅" : "未命中 ❌")}");
                StatusLog.Append(repoRoot, $"界面判断: 当前 {got} / 期望 {want} {(got == want ? "命中" : "未命中")}");
                if (got == want && !string.IsNullOrEmpty(s.JumpTo))
                {
                    Console.WriteLine($"  ↪ 跳转到 “{s.JumpTo}”");
                    i = FindStep(s.JumpTo);
                    continue;
                }
                break;
            }

            case "pause":
                if (!auto)
                {
                    Console.WriteLine($"  ⏸ {s.Message ?? "人工确认点"} —— 按回车继续（Ctrl+C 中止）");
                    Console.ReadLine();
                }
                break;

            case "if_template":
            {
                string tpl = s.Template ?? throw new InvalidDataException("if_template 步骤缺少 template 字段");
                string tplPath = Path.Combine(repoRoot, tpl);
                using var frame = CaptureService.CaptureWindowMat(winKeyword);
                using var norm = FrameTools.Normalize(frame, runtime.DesignWidth, runtime.DesignHeight);
                var mr = TemplateMatcher.Match(norm, tplPath, s.Threshold ?? 0.85);
                Console.WriteLine($"  模板检测: {tpl} 置信度 {mr.Confidence:F3} → {(mr.Found ? "命中 ✅" : "未命中 ❌")}");
                StatusLog.Append(repoRoot, $"模板检测 {tpl}: {mr.Confidence:F3} {(mr.Found ? "命中" : "未命中")}");
                if (mr.Found && !string.IsNullOrEmpty(s.JumpTo))
                {
                    Console.WriteLine($"  ↪ 跳转到 “{s.JumpTo}”");
                    i = FindStep(s.JumpTo);
                    continue;
                }
                break;
            }

            case "jump":
            {
                string to = s.JumpTo ?? throw new InvalidDataException("jump 步骤缺少 jump_to 字段");
                Console.WriteLine($"  ↪ 无条件跳转到 “{to}”");
                i = FindStep(to);
                continue;
            }

            default:
                throw new InvalidDataException($"未知步骤 op: {s.Op}");
        }

        if (s.WaitMs is int w && w > 0) Thread.Sleep(w);
        if (!string.IsNullOrEmpty(s.Capture))
        {
            string p = Path.Combine(repoRoot, "screenshots", "captured", $"chain_{i + 1}_{s.Capture}_{DateTime.Now:HHmmss}.png");
            Console.WriteLine(CaptureService.CaptureWindowClient(winKeyword, p));
        }
        i++;
    }
    Console.WriteLine($"\n🎉 链路 {chain.Name} 执行完成");
}

/// <summary>点击一个 coord 策略元素：窗口置顶 → 设计分辨率坐标缩放 → 实时换算 → 拟人化点击。</summary>
void ClickElementStep(IntPtr hwnd, ElementsTable table, string elementName, int designW, int designH)
{
    if (!table.Elements.TryGetValue(elementName, out var def))
        throw new ArgumentException($"元素表里没有 “{elementName}”");
    if (def.Strategy != "coord" || def.Params.X is null || def.Params.Y is null)
        throw new InvalidDataException($"元素 {elementName} 需要 coord 策略且 x/y 已填写");

    CaptureService.RaiseWindow(hwnd);
    Thread.Sleep(250);
    InputService.EnsureForeground(hwnd); // 部分游戏非前台时忽略鼠标点击
    Thread.Sleep(200);
    var r = CaptureService.GetClientScreenRect(hwnd);
    if (r is null)
    {
        CaptureService.UnraiseWindow(hwnd);
        throw new InvalidOperationException("点击前窗口不可用（最小化？）");
    }
    int absX = (int)Math.Round(r.Value.X + def.Params.X.Value * (r.Value.W / (double)designW));
    int absY = (int)Math.Round(r.Value.Y + def.Params.Y.Value * (r.Value.H / (double)designH));
    InputService.ClickAt(absX, absY);
    CaptureService.UnraiseWindow(hwnd);
}

// ===== 素材裁剪 crop：从截图裁模板小图（模板匹配素材制作工具） =====

void RunCrop(string repoRoot, string[] cmdArgs)
{
    if (cmdArgs.Length < 6) throw new ArgumentException("用法: crop <x> <y> <w> <h> <源图相对路径> [输出相对路径] [缩放系数]");
    int x = int.Parse(cmdArgs[1]);
    int y = int.Parse(cmdArgs[2]);
    int w = int.Parse(cmdArgs[3]);
    int h = int.Parse(cmdArgs[4]);
    string srcRel = cmdArgs[5];
    string outRel = cmdArgs.Length > 6 ? cmdArgs[6] : "screenshots/cropped.png";
    double scale = cmdArgs.Length > 7 && double.TryParse(cmdArgs[7], out double sc) && sc > 0 ? sc : 1.0;

    using var img = Cv2.ImRead(Path.Combine(repoRoot, srcRel), ImreadModes.Color);
    if (img.Empty()) throw new FileNotFoundException($"图片读取失败: {srcRel}");
    using var cropped = new Mat(img, new Rect(x, y, w, h));

    Mat saveMat = cropped;
    Mat? resized = null;
    if (Math.Abs(scale - 1.0) > 0.001)
    {
        resized = new Mat();
        Cv2.Resize(cropped, resized, new Size((int)Math.Round(w * scale), (int)Math.Round(h * scale)), 0, 0, InterpolationFlags.Linear);
        saveMat = resized;
    }

    string outAbs = Path.Combine(repoRoot, outRel);
    var d = Path.GetDirectoryName(outAbs);
    if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
    try
    {
        if (!Cv2.ImWrite(outAbs, saveMat)) throw new IOException($"保存失败: {outAbs}");
        Console.WriteLine($"✅ 裁剪完成: {outRel}（{saveMat.Width}x{saveMat.Height}，缩放 x{scale}）");
    }
    finally
    {
        resized?.Dispose();
    }
}

// ===== 悬浮状态面板 overlay：常驻进程，把 logs/status.log 尾部渲染到游戏画面上 =====

void RunOverlay(DataStore data, string repoRoot)
{
    var runtime = data.LoadRuntime();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        StatusOverlay.Stop();
        Environment.Exit(0);
    };

    Console.WriteLine($"等待游戏窗口（标题含 “{runtime.WindowKeyword}”）... 游戏没开时每 2 秒重试；Ctrl+C 退出");
    (int X, int Y, int W, int H)? rect = null;
    IntPtr gameHwnd = IntPtr.Zero;
    while (rect is null)
    {
        gameHwnd = CaptureService.FindWindowByTitle(runtime.WindowKeyword);
        rect = CaptureService.GetClientScreenRect(gameHwnd);
        if (rect is null)
        {
            Console.WriteLine("  未找到游戏窗口，2 秒后重试...");
            Thread.Sleep(2000);
        }
    }

    Console.WriteLine($"✅ 悬浮面板启动于游戏客户区 ({rect.Value.X},{rect.Value.Y}) {rect.Value.W}x{rect.Value.H}（置顶+鼠标穿透，只覆盖游戏）");
    Console.WriteLine("状态来源: logs/status.log —— 运行 chain/patrol/click 等命令会实时刷新。Ctrl+C 关闭。");
    StatusOverlay.Start(rect.Value.X, rect.Value.Y, rect.Value.W, rect.Value.H);

    string logPath = Path.Combine(repoRoot, "logs", "status.log");
    while (true)
    {
        try
        {
            var tail = new List<string>();
            if (File.Exists(logPath))
            {
                string[] all = File.ReadAllLines(logPath);
                int start = Math.Max(0, all.Length - 12);
                for (int i = start; i < all.Length; i++) tail.Add(all[i]);
            }
            if (tail.Count == 0) tail.Add("（暂无日志 —— 运行 chain/patrol 后这里会实时显示）");
            StatusOverlay.SetStatus("DeltaUnlimited 状态", tail.ToArray());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [overlay] 读取日志失败: {ex.Message}");
        }
        Thread.Sleep(500);
    }
}

void RunListWindows()
{
    var wins = CaptureService.ListTopLevelWindows();
    if (wins.Count == 0)
    {
        Console.WriteLine("当前没有可见的顶层窗口");
        return;
    }
    Console.WriteLine($"当前可见窗口 {wins.Count} 个:");
    foreach (var w in wins)
        Console.WriteLine($"  0x{w.Hwnd.ToInt64():X}  {w.Title}");
}

// ===== 靶场动作测试（hold/turn）：动作前后各取一帧（内存），帧差自动判断是否生效 =====

void RunHold(DataStore data, string repoRoot, string[] cmdArgs)
{
    if (cmdArgs.Length < 3) throw new ArgumentException("用法: hold <键名或语义键> <毫秒> [窗口关键字(默认 三角洲行动)]");
    string keyArg = cmdArgs[1];
    if (!int.TryParse(cmdArgs[2], out int ms) || ms <= 0) throw new ArgumentException("时长需为正整数毫秒");
    string winKeyword = cmdArgs.Length > 3 ? cmdArgs[3] : "三角洲行动";

    string key = data.LoadGameOps().KeyMap.GetValueOrDefault(keyArg, keyArg);
    RunInputProbe(repoRoot, winKeyword, $"长按 {keyArg} → {key} {ms}ms", () => InputService.HoldKey(key, ms));
}

void RunTurn(string repoRoot, string[] cmdArgs)
{
    if (cmdArgs.Length < 2) throw new ArgumentException("用法: turn <dx> [dy] [窗口关键字(默认 三角洲行动)]");
    int dx = int.Parse(cmdArgs[1]);
    int dy = 0;
    int idx = 2;
    if (cmdArgs.Length > 2 && int.TryParse(cmdArgs[2], out int parsedDy))
    {
        dy = parsedDy;
        idx = 3;
    }
    string winKeyword = cmdArgs.Length > idx ? cmdArgs[idx] : "三角洲行动";
    RunInputProbe(repoRoot, winKeyword, $"视角转动 ({dx}, {dy})", () => InputService.MoveRelative(dx, dy));
}

void RunInputProbe(string repoRoot, string winKeyword, string describe, Action action)
{
    IntPtr hwnd = CaptureService.FindWindowByTitle(winKeyword);
    if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{winKeyword}” 的窗口");

    Console.WriteLine($"动作: {describe}");
    Console.WriteLine("第 1 步: 采集动作前帧（内存）...");
    using var pre = CaptureService.CaptureWindowMat(winKeyword);

    Console.WriteLine("第 2 步: 抢占键盘焦点...");
    InputService.EnsureForeground(hwnd);
    Thread.Sleep(300);

    Console.WriteLine("第 3 步: 执行动作...");
    action();
    Thread.Sleep(500);

    Console.WriteLine("第 4 步: 采集动作后帧并计算帧差...");
    using var post = CaptureService.CaptureWindowMat(winKeyword);
    double score = FrameDiff.Score(pre, post);
    string verdict = score >= 3.0
        ? "✅ 画面明显变化，动作疑似生效"
        : score >= 0.8
            ? "🟡 有轻微变化（可能动作幅度小/已到边界）"
            : "❓ 几乎无变化（检查焦点、按键名、是否已站在墙边）";
    Console.WriteLine($"帧差(0-255): {score:F2}  {verdict}");

    if (score >= 3.0)
    {
        string evPath = Path.Combine(repoRoot, "screenshots", "captured", $"probe_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        var dir = Path.GetDirectoryName(evPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (Cv2.ImWrite(evPath, post))
            Console.WriteLine($"已保存证据帧: {evPath}");
    }
}

void PrintUsage()
{
    Console.WriteLine("""
        DeltaUnlimited 开发期 CLI
          (无参数)                         Phase 0 数据层冒烟
          annotate <元素名> [截图路径]       静态识别冒烟 → screenshots/annotated/
          capture  [标题关键字] [文件名]     截屏冒烟 → screenshots/captured/
          click <元素名> [窗口关键字]        真实点击（点前/点后自动截图，默认 3 秒倒计时）
          hold <键名> <毫秒> [窗口关键字]     长按测试（帧差验证，如: hold move_forward 1500）
          turn <dx> [dy] [窗口关键字]         视角转动测试（帧差验证，如: turn 800 0）
          drill [圈数] [窗口关键字]           靶场自主行进循环（帧差确认+自动重试+急停）
          patrol [秒数] [阈值] [连续] [90°px] [窗口]   反应式巡逻 v2：贴墙停+横移低效双守卫，自动转向
          chain <workflow文件> [--auto]        按 JSON 步骤执行链路（detect/if_screen 分支/jump）
          crop <x> <y> <w> <h> <源图> [输出] [缩放]   从截图裁模板小图（可缩放换算设计分辨率）
          overlay                           悬浮状态面板（游戏画面上实时显示识别/操作日志）
          windows                           列出可见窗口
        """);
}
