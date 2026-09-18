using System.Reflection;
using DeltaUnlimited.Cli;
using DeltaUnlimited.Cli.Commands;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
using DeltaUnlimited.Overlay;
using DeltaUnlimited.Vision.Ocr;

// ===== DeltaUnlimited 入口：只负责启动初始化、版本信息、命令路由分发 =====

var root = DataStore.FindRoot();
var store = new DataStore(root);
Logger.Init(root);

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
Console.WriteLine($"DeltaUnlimited v{version}（开发期 CLI）");

// OCR 引擎初始化（Windows.Media.Ocr 先行，Paddle 后补）
Ocr.Initialize();

// 拟人化参数注入（runtime.json 可覆盖）
var runtime = store.LoadRuntime();
InputService.Configure(runtime.Humanizer ?? new HumanizerConfig());

// #12: 转向未标定警告
if (runtime.PxPer90Deg <= 0)
{
    Console.WriteLine("⚠️ 转向参数未标定（data/runtime.json 的 px_per_90deg ≤ 0）：请运行 turn 命令标定，或手动编辑该值。");
}

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "smoke";

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
        case "overlay": OverlayCommand.Run(store, root); break;
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
