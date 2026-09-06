using DeltaUnlimited.Capture;
using DeltaUnlimited.Input;
using DeltaUnlimited.Vision;
using OpenCvSharp;

namespace DeltaUnlimited.Cli;

/// <summary>动作探针：动作前后各取一帧（内存），帧差自动判断是否生效（hold/turn 共用）。</summary>
public static class InputProbe
{
    public static void Run(string repoRoot, string winKeyword, string describe, Action action)
    {
        IntPtr hwnd = CaptureService.FindWindowByTitle(winKeyword);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{winKeyword}” 的窗口");

        Console.WriteLine($"动作: {describe}");
        Console.WriteLine("第 1 步: 采集动作前帧（内存）...");
        using var pre = CaptureService.CaptureWindowMat(winKeyword);

        Console.WriteLine("第 2 步: 抢占键盘焦点...");
        InputService.EnsureForeground(hwnd);
        Thread.Sleep(300);

        Console.WriteLine("第 3 步: 执行动作...");
        action();
        Thread.Sleep(500);

        Console.WriteLine("第 4 步: 采集动作后帧并计算帧差...");
        using var post = CaptureService.CaptureWindowMat(winKeyword);
        double score = FrameDiff.Score(pre, post);
        string verdict = score >= 3.0
            ? "✅ 画面明显变化，动作疑似生效"
            : score >= 0.8
                ? "🟡 有轻微变化（可能动作幅度小/已到边界）"
                : "❓ 几乎无变化（检查焦点、按键名、是否已站在墙边）";
        Console.WriteLine($"帧差(0-255): {score:F2}  {verdict}");

        if (score >= 3.0)
        {
            string evPath = Path.Combine(repoRoot, "screenshots", "captured", $"probe_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            var dir = Path.GetDirectoryName(evPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (Cv2.ImWrite(evPath, post))
                Console.WriteLine($"已保存证据帧: {evPath}");
        }
    }
}
