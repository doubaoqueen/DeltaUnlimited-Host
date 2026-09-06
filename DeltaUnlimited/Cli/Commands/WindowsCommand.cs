using DeltaUnlimited.Capture;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>windows：列出当前可见窗口。</summary>
public static class WindowsCommand
{
    public static void Run()
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
}
