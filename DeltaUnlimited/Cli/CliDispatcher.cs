using DeltaUnlimited.Cli.Commands;
using DeltaUnlimited.Data;

namespace DeltaUnlimited.Cli;

/// <summary>CLI 命令分发器：原 Program.cs 的 switch 迁移至此，CLI 入口与 GUI 命令台共用（单一事实来源）。
/// KnownVerbs 是全部合法动词清单；gui 不入分发（入口特判直接进 GUI），chain 保留在清单里（GUI 顶栏是它的专属入口）。</summary>
public static class CliDispatcher
{
    /// <summary>全部合法动词（与 Dispatch 的 switch 分支一一对应；新增命令必须同时补这里与 switch，测试会同步校验）。</summary>
    public static readonly string[] KnownVerbs =
    {
        "smoke", "annotate", "capture", "click", "hold", "turn", "drill", "patrol",
        "aicollect", "airecord", "ailabel", "roitune", "aiprobe", "autorun_walk", "aieval",
        "chain", "crop", "ocr", "tplprobe", "localize", "localizetrack", "overlay",
        "mousetest", "clickat", "click_operator", "windows",
    };

    public static void Dispatch(string command, DataStore store, string root, string[] args)
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
            case "aicollect": AiCollectCommand.Run(store, root, args); break;
            case "airecord": AiRecordCommand.Run(store, root, args); break;
            case "ailabel": AiLabelCommand.Run(store, root, args); break;
            case "roitune": RoiTuneCommand.Run(store, root, args); break;
            case "aiprobe": AiProbeCommand.Run(store, root, args); break;
            case "autorun_walk": AutorunWalkCommand.Run(store, root, args); break;
            case "aieval": AiEvalCommand.Run(store, root, args); break;
            case "chain": ChainCommand.Run(store, root, args); break;
            case "crop": CropCommand.Run(root, args); break;
            case "ocr": OcrProbeCommand.Run(root, args); break;
            case "tplprobe": TplProbeCommand.Run(root, args); break;
            case "localize": LocalizeCommand.Run(root, args); break;
            case "localizetrack": LocalizeTrackCommand.Run(root, args); break;
            case "overlay": OverlayCommand.Run(store, root); break;
            case "mousetest": MouseTestCommand.Run(args); break;
            case "clickat": ClickAtCommand.Run(args); break;
            case "click_operator": OperatorPickCommand.Run(store, root, args); break;
            case "windows": WindowsCommand.Run(); break;
            default: Usage.Print(); break;
        }
    }
}
