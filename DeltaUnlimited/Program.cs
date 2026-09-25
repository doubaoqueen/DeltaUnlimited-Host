using System.Reflection;
using DeltaUnlimited.Cli;
using DeltaUnlimited.Cli.Commands;
using DeltaUnlimited.Data;
using DeltaUnlimited.Gui;
using DeltaUnlimited.Input;
using DeltaUnlimited.Overlay;
using DeltaUnlimited.Vision.Ocr;

// ===== DeltaUnlimited 入口：只负责启动初始化、版本信息、命令路由分发 =====
// 无参数（双击 exe）或 gui 命令 → 图形控制面板；其余命令走 CLI（诊断后门，行为不变）。

var root = DataStore.FindRoot();
var store = new DataStore(root);
Logger.Init(root);

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

// 先读运行时配置（OCR 引擎选择、拟人化参数注入）
var runtime = store.LoadRuntime();
var command = args.Length > 0 ? args[0].ToLowerInvariant() : "gui";

if (command == "gui")
{
    GuiApp.Run(store, root, runtime, version);
    return;
}

Console.WriteLine($"DeltaUnlimited v{version}（开发期 CLI）");

// CLI：注入拟人化参数 + 初始化 OCR 引擎（runtime.json 的 ocr_engine 可切换：windows 主 / paddle 待门控失败后接入）
InputService.Configure(runtime.Humanizer ?? new HumanizerConfig());
Ocr.Initialize(runtime.OcrEngine);

// #12: 转向未标定警告
if (runtime.PxPer90Deg <= 0)
{
    Console.WriteLine("⚠️ 转向参数未标定（data/runtime.json 的 px_per_90deg ≤ 0）：请运行 turn 命令标定，或手动编辑该值。");
}

try
{
    switch (command)
    {
        case "smoke": SmokeCommand.Run(store); break;
        case "annotate": AnnotateCommand.Run(store, root, args); break;
        case "capture": CaptureCommand.Run(root, args); break;
        case "click": ClickCommand.Run(store, root, args); break;
        case "hold": HoldCommand.Run(store, root, args); break;
        case "turn": TurnCommand.Run(root, args); break;
        case "drill": DrillCommand.Run(store, root, args); break;
        case "patrol": PatrolCommand.Run(store, root, args); break;
        case "chain": ChainCommand.Run(store, root, args); break;
        case "crop": CropCommand.Run(root, args); break;
        case "ocr": OcrProbeCommand.Run(root, args); break;
        case "tplprobe": TplProbeCommand.Run(root, args); break;
        case "overlay": OverlayCommand.Run(store, root); break;
        case "mousetest": MouseTestCommand.Run(args); break;
        case "clickat": ClickAtCommand.Run(args); break;
        case "click_operator": OperatorPickCommand.Run(store, root, args); break;
        case "windows": WindowsCommand.Run(); break;
        default: Usage.Print(); break;
    }
}
catch (Exception ex)
{
    // #3: 打印完整异常（消息 + 堆栈 + 内部异常），出问题直接定位
    Console.Error.WriteLine(ex.ToString());
    Environment.ExitCode = 1;
}

// 协作式急停（Ctrl+C）后优雅退出：保持传统退出码 130
if (CommandUtil.StopRequested) Environment.ExitCode = 130;
