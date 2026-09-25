using DeltaUnlimited.Capture;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>click_operator：手动测试干员选择（运行时 OCR 定位标签→点头像；倒计时 15s 内跑）。
/// 用法: click_operator <类型> <序号(0起)>，如 click_operator 突击 0。</summary>
public static class OperatorPickCommand
{
    public static void Run(DataStore data, string repoRoot, string[] cmdArgs)
    {
        if (cmdArgs.Length < 3) throw new ArgumentException("用法: click_operator <类型> <序号(0起)>，如 click_operator 突击 0");
        string type = cmdArgs[1];
        int index = int.Parse(cmdArgs[2]);

        var runtime = data.LoadRuntime();
        var table = data.LoadOperatorPresets();
        var hwnd = CaptureService.FindWindowByTitle(runtime.WindowKeyword);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{runtime.WindowKeyword}” 的窗口");

        InputService.EnsureForeground(hwnd);
        bool ok = OperatorPicker.TryPick(hwnd, runtime.WindowKeyword, runtime, table,
            new OperatorPick { Type = type, Index = index });
        Console.WriteLine(ok ? "✅ 已点击目标干员头像" : "❌ 未完成选择（保持当前干员）");
    }
}
