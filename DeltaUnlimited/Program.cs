using DeltaUnlimited.Capture;
using DeltaUnlimited.Data;
using DeltaUnlimited.Vision;

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

void PrintUsage()
{
    Console.WriteLine("""
        DeltaUnlimited 开发期 CLI
          (无参数)                         Phase 0 数据层冒烟
          annotate <元素名> [截图路径]       静态识别冒烟 → screenshots/annotated/
          capture  [标题关键字] [文件名]     截屏冒烟 → screenshots/captured/
          windows                           列出可见窗口
        """);
}
