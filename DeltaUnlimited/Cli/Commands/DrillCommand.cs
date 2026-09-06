using DeltaUnlimited.Capture;
using DeltaUnlimited.Cli;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
using DeltaUnlimited.Vision;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>drill：靶场自主行进循环（每步动作帧差确认，失败重试一次后自动中止）。</summary>
public static class DrillCommand
{
    public static void Run(DataStore data, string repoRoot, string[] cmdArgs)
    {
        int rounds = cmdArgs.Length > 1 && int.TryParse(cmdArgs[1], out int rv) && rv > 0 ? rv : 2;
        var runtime = data.LoadRuntime();
        string winKeyword = cmdArgs.Length > 2 ? cmdArgs[2] : runtime.WindowKeyword;

        const int fwdMs = 1600;      // 每段冲刺前进时长
        const double minScore = 3.0; // 帧差阈值：低于此值视为"没动"
        const int settleMs = 450;    // 动作结束后的稳定等待
        int turnPx = (int)Math.Round(runtime.PxPer90Deg); // 右转 90° 的像素量（runtime.json 标定，与 EDPI 相关）

        var ops = data.LoadGameOps();
        string fwdKey = ops.KeyMap.GetValueOrDefault("move_forward", "w");
        string sprintKey = ops.KeyMap.GetValueOrDefault("sprint", "shift");

        IntPtr hwnd = CaptureService.FindWindowByTitle(winKeyword);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{winKeyword}” 的窗口");

        CommandUtil.InstallEmergencyStop();

        Console.WriteLine($"靶场自主行进循环：{rounds} 圈（每圈 = 冲刺前进 + 右转90° 的 4 边回路）");
        Console.WriteLine("2 秒后开始；Ctrl+C 随时急停；若贴墙/卡住会自动重试后中止。请盯紧屏幕！");
        Thread.Sleep(2000);

        for (int round = 1; round <= rounds; round++)
        {
            for (int side = 1; side <= 4; side++)
            {
                Console.WriteLine($"\n[第 {round}/{rounds} 圈 · 边 {side}/4]");
                if (!MotionVerified(hwnd, winKeyword, repoRoot, "冲刺前进", () => InputService.HoldKeys(new[] { fwdKey, sprintKey }, fwdMs), settleMs, minScore))
                    throw new InvalidOperationException("前进两次验证失败，已自动中止（画面未变化：贴墙了？焦点丢了？）");
                if (!MotionVerified(hwnd, winKeyword, repoRoot, "右转约90°", () => InputService.MoveRelative(turnPx, 0), settleMs, minScore))
                    throw new InvalidOperationException("转向两次验证失败，已自动中止（画面未变化）");
            }
            Console.WriteLine($"\n✅ 第 {round} 圈完成");
        }
        Console.WriteLine($"\n🎉 自主行进循环完成 {rounds} 圈，共 {rounds * 4} 段动作全部帧差验证通过");
    }

    /// <summary>执行一个动作并用帧差验证：失败自动重试一次（重新抢焦点），仍失败则留证据帧并返回 false。</summary>
    private static bool MotionVerified(IntPtr hwnd, string winKeyword, string repoRoot, string desc, Action action, int settleMs, double minScore)
    {
        bool RunOnce(string label, out double score)
        {
            using var pre = CaptureService.CaptureWindowMat(winKeyword);
            bool focused = InputService.EnsureForeground(hwnd);
            Thread.Sleep(250);
            try
            {
                action();
            }
            finally
            {
                InputService.ReleaseAllHeldKeys();
            }
            Thread.Sleep(settleMs);
            using var post = CaptureService.CaptureWindowMat(winKeyword);
            score = FrameDiff.Score(pre, post);
            Console.WriteLine($"  {label}帧差 {score:F2} {(score >= minScore ? "✅" : "❌")}{(focused ? "" : "（⚠️ 焦点丢失）")}");
            return score >= minScore;
        }

        Console.WriteLine($"动作: {desc}");
        if (RunOnce("", out _)) return true;

        Console.WriteLine("  第一次未见效果，重试一次...");
        if (RunOnce("重试", out _)) return true;

        // 留证据后判定失败
        try
        {
            string ev = Path.Combine(repoRoot, "screenshots", "captured", $"drill_fail_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            var d = Path.GetDirectoryName(ev);
            if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
            using var frame = CaptureService.CaptureWindowMat(winKeyword);
            if (Cv2.ImWrite(ev, frame)) Console.WriteLine($"  证据帧已保存: {ev}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  证据保存失败: {ex.Message}");
        }
        return false;
    }
}
