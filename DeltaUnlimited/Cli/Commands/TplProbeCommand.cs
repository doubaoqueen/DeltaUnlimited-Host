using DeltaUnlimited.Capture;
using DeltaUnlimited.Cli;
using DeltaUnlimited.Data;
using DeltaUnlimited.Vision;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>tplprobe：模板匹配诊断探针。
/// 静态：tplprobe <截图相对路径> <模板相对路径> [阈值] —— 单张截图出置信度与位置（标定用）。
/// 实时：tplprobe --live <模板相对路径> [阈值] [x y w h] —— 对游戏窗口逐帧匹配（~400ms/帧），
/// 人走到哪模板就验到哪（如站进撤离区看倒计时命中翻转）。纯只读，不注入输入。</summary>
public static class TplProbeCommand
{
    public static void Run(string repoRoot, string[] cmdArgs)
    {
        if (cmdArgs.Length >= 3 && cmdArgs[1] == "--live")
        {
            RunLive(repoRoot, cmdArgs);
            return;
        }
        if (cmdArgs.Length < 3) throw new ArgumentException(
            "用法: tplprobe <截图相对路径> <模板相对路径> [阈值(默认0.85)]；" +
            "实时模式: tplprobe --live <模板相对路径> [阈值(默认0.80)] [x y w h 搜索区域]");

        string imageRel = cmdArgs[1];
        string tplRel = cmdArgs[2];
        double threshold = cmdArgs.Length > 3 && double.TryParse(cmdArgs[3], out double t) ? t : 0.85;

        using var img = Cv2.ImRead(Path.Combine(repoRoot, imageRel), ImreadModes.Color);
        if (img.Empty()) throw new FileNotFoundException($"图片读取失败: {imageRel}");
        var runtime = new DataStore(repoRoot).LoadRuntime(); // 探针与真实点击路径用同一设计分辨率，避免结论分叉（评审 P2-12）
        using var norm = FrameTools.Normalize(img, runtime.DesignWidth, runtime.DesignHeight);

        var r = TemplateMatcher.Match(norm, Path.Combine(repoRoot, tplRel), threshold);
        Console.WriteLine($"模板 {tplRel} 在 {imageRel} 上（阈值 {threshold:F2}）:");
        Console.WriteLine($"  置信度 {r.Confidence:F3} → {(r.Found ? "命中 ✅" : "未命中 ❌")}  中心 @ ({r.CenterX},{r.CenterY})  模板 {r.W}x{r.H}");
    }

    /// <summary>实时模式：对游戏窗口循环匹配，人眼对照"画面里有/没有"与探针命中/未命中是否一致。</summary>
    private static void RunLive(string repoRoot, string[] cmdArgs)
    {
        string tplRel = cmdArgs[2];
        double threshold = cmdArgs.Length > 3 && double.TryParse(cmdArgs[3], out var t) ? t : 0.80;
        int[]? region = cmdArgs.Length >= 8 && cmdArgs.Skip(4).Take(4).All(a => int.TryParse(a, out _))
            ? cmdArgs.Skip(4).Take(4).Select(int.Parse).ToArray()
            : null;
        string tplPath = Path.Combine(repoRoot, tplRel);
        if (!File.Exists(tplPath)) throw new FileNotFoundException($"模板不存在: {tplPath}");

        var runtime = new DataStore(repoRoot).LoadRuntime();
        IntPtr hwnd = CaptureService.FindWindowByTitle(runtime.WindowKeyword);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"没找到标题含 “{runtime.WindowKeyword}” 的窗口（先开游戏）");
        CaptureService.LogWindowAnchor(hwnd, runtime.DesignWidth, runtime.DesignHeight);
        CommandUtil.InstallEmergencyStop();
        Console.WriteLine($"实时匹配中：模板 {tplRel}（阈值 {threshold:F2}" +
                          (region is null ? "" : $"，区域 [{region[0]},{region[1]},{region[2]},{region[3]}]") +
                          "）。q 或 Ctrl+C 结束——把目标物拿进/拿出画面，看判定是否跟着翻。");

        long frames = 0, hits = 0;
        bool canWatchKeys = !Console.IsInputRedirected;   // 管道/无控制台环境下只能 Ctrl+C，不能 q
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!CommandUtil.StopRequested)
        {
            if (canWatchKeys && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q) break;
            using Mat raw = CaptureService.CaptureWindowMat(hwnd, raiseAndWait: false);
            using var norm = FrameTools.Normalize(raw, runtime.DesignWidth, runtime.DesignHeight);
            var r = TemplateMatcher.Match(norm, tplPath, threshold, region);
            frames++;
            if (r.Found) hits++;
            Console.Write("\r[" + sw.Elapsed.ToString(@"mm\:ss") + "] " +
                          (r.Found ? "✅ 命中" : "❌ 未中") + $" {r.Confidence,5:F3}" +
                          (r.Found ? $" @({r.CenterX},{r.CenterY})" : "        ") +
                          $"   命中率 {hits * 100.0 / Math.Max(1, frames):F0}%  已测 {frames} 帧 ");
            Thread.Sleep(400);
        }
        Console.WriteLine($"\n🏁 探针结束：{frames} 帧，命中 {hits}（命中率 {hits * 100.0 / Math.Max(1, frames):F0}%）");
    }
}
