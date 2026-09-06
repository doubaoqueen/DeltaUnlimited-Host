using DeltaUnlimited.Cli;
using DeltaUnlimited.Cli.Commands;
using DeltaUnlimited.Data;

// ===== DeltaUnlimited 入口：只负责解析命令并分发到各命令类（具体实现见 Cli/Commands/） =====

var root = DataStore.FindRoot();
var store = new DataStore(root);
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
        case "overlay": OverlayCommand.Run(store, root); break;
        case "windows": WindowsCommand.Run(); break;
        default: Usage.Print(); break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"❌ {ex.Message}");
    Environment.ExitCode = 1;
}
