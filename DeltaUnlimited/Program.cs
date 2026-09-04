using DeltaUnlimited.Capture;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
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

    IntPtr hwnd = CaptureService.FindWindowByTitle(winKeyword);
    if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{winKeyword}” 的窗口");

    var r1 = CaptureService.GetClientScreenRect(hwnd)
        ?? throw new InvalidOperationException("窗口不可用（最小化？）");
    int absX = r1.X + def.Params.X.Value;
    int absY = r1.Y + def.Params.Y.Value;
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
    absX = r2.Value.X + def.Params.X.Value;
    absY = r2.Value.Y + def.Params.Y.Value;
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
    string winKeyword = cmdArgs.Length > 2 ? cmdArgs[2] : "三角洲行动";

    const int fwdMs = 1600;      // 每段冲刺前进时长
    const int turnPx = 800;      // 每次右转像素量（约 90°，取决于游戏内灵敏度）
    const double minScore = 3.0; // 帧差阈值：低于此值视为"没动"
    const int settleMs = 450;    // 动作结束后的稳定等待

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
          windows                           列出可见窗口
        """);
}
