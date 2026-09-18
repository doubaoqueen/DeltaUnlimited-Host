using DeltaUnlimited.Capture;
using DeltaUnlimited.Cli;
using DeltaUnlimited.Data;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>click：真实点击（点前/点后自动截图，默认 3 秒倒计时）。定位与点击委托 ElementClicker（ocr 主 / coord 兜）。</summary>
public static class ClickCommand
{
    public static void Run(DataStore data, string repoRoot, string[] cmdArgs)
    {
        if (cmdArgs.Length < 2) throw new ArgumentException("用法: click <元素名> [窗口关键字(默认 三角洲行动)]");
        string elementName = cmdArgs[1];
        string winKeyword = cmdArgs.Length > 2 ? cmdArgs[2] : "三角洲行动";

        var table = data.LoadElements();
        if (!table.Elements.TryGetValue(elementName, out var def))
            throw new ArgumentException($"元素表里没有 “{elementName}”。现有: {string.Join(", ", table.Elements.Keys)}");

        var runtime = data.LoadRuntime();
        IntPtr hwnd = CaptureService.FindWindowByTitle(winKeyword);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{winKeyword}” 的窗口");

        // 第 1 步：点击前截图（确认现场，人工可查）
        string prePath = Path.Combine(repoRoot, "screenshots", "captured", $"pre_{elementName}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        Console.WriteLine("第 1 步: 点击前截图...");
        Console.WriteLine(CaptureService.CaptureWindowClient(winKeyword, prePath));

        // 第 2 步：3 秒倒计时（可 Ctrl+C 取消）
        Console.WriteLine($"第 2 步: 3 秒后点击元素 “{elementName}”（strategy={def.Strategy}），可 Ctrl+C 取消...");
        Thread.Sleep(3000);

        // 第 3 步：定位 + 点击（ocr 主 / coord 兜）
        Console.WriteLine("第 3 步: 定位并点击...");
        ElementClicker.Click(hwnd, table, elementName, runtime.DesignWidth, runtime.DesignHeight, repoRoot);

        // 第 4 步：点击后截图（画面是否切换，人工确认）
        Thread.Sleep(1200);
        string postPath = Path.Combine(repoRoot, "screenshots", "captured", $"post_{elementName}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        Console.WriteLine("第 4 步: 点击后截图...");
        Console.WriteLine(CaptureService.CaptureWindowClient(winKeyword, postPath));

        Console.WriteLine($"\n请人工确认 {postPath} 画面是否已切换 —— 若已切换说明点中了！");
    }
}
