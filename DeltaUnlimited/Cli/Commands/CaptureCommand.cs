using DeltaUnlimited.Capture;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>capture：截屏冒烟（桌面 / 指定窗口客户区）。</summary>
public static class CaptureCommand
{
    public static void Run(string repoRoot, string[] cmdArgs)
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
}
