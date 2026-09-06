using DeltaUnlimited.Capture;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>click：真实点击（点前/点后自动截图，默认 3 秒倒计时）。</summary>
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
        if (def.Strategy != "coord" || def.Params.X is null || def.Params.Y is null)
            throw new InvalidDataException($"元素 {elementName} 需要 coord 策略且 x/y 已填写才能点击");

        var runtime = data.LoadRuntime();

        IntPtr hwnd = CaptureService.FindWindowByTitle(winKeyword);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{winKeyword}” 的窗口");

        var r1 = CaptureService.GetClientScreenRect(hwnd)
            ?? throw new InvalidOperationException("窗口不可用（最小化？）");
        int absX = (int)Math.Round(r1.X + def.Params.X.Value * (r1.W / (double)runtime.DesignWidth));
        int absY = (int)Math.Round(r1.Y + def.Params.Y.Value * (r1.H / (double)runtime.DesignHeight));
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
        absX = (int)Math.Round(r2.Value.X + def.Params.X.Value * (r2.Value.W / (double)runtime.DesignWidth));
        absY = (int)Math.Round(r2.Value.Y + def.Params.Y.Value * (r2.Value.H / (double)runtime.DesignHeight));
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
}
