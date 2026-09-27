using DeltaUnlimited.Capture;
using DeltaUnlimited.Cli;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
using DeltaUnlimited.Vision;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>airecord：可通行性素材录制器（纯截图，不注入任何输入，零键位冲突）。
/// 采集与标注分离：正常打游戏，工具按固定节奏自动存全帧，标签留空，事后用 ailabel 图形标注。
/// 全帧归档同时是未来 YOLO 避战层的素材底库（一次录制喂所有模型）；FrameDiff 跳过静止重复帧省盘。
/// 与 aicollect（边玩边标）互补：本工具不抢焦点不打断游戏，画面分布 100% 真实。</summary>
public static class AiRecordCommand
{
    public static void Run(DataStore data, string repoRoot, string[] cmdArgs)
    {
        var runtime = data.LoadRuntime();
        var cfg = data.LoadAiVision();
        string map = GetOpt(cmdArgs, "--map") ?? "零号大坝";
        string season = GetOpt(cmdArgs, "--season") ?? "unknown";
        string winKeyword = GetOpt(cmdArgs, "--win") ?? runtime.WindowKeyword;
        double minDiff = ParseOpt(cmdArgs, "--diff", 2.0);
        int intervalMs = ParseOpt(cmdArgs, "--interval", 250);
        double maxGb = ParseOpt(cmdArgs, "--max-gb", 50.0);

        string recDir = Path.Combine(repoRoot, "ai-training", "datasets", "record");
        string fullDir = Path.Combine(recDir, "full");
        Directory.CreateDirectory(fullDir);
        string manifestPath = Path.Combine(recDir, "record_manifest.csv");
        if (!File.Exists(manifestPath))
            File.AppendAllText(manifestPath, "relpath,label,map,season,created\n");

        IntPtr hwnd = CaptureService.FindWindowByTitle(winKeyword);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{winKeyword}” 的窗口");
        CaptureService.LogWindowAnchor(hwnd, runtime.DesignWidth, runtime.DesignHeight);
        InputService.EnsureForeground(hwnd);

        Console.WriteLine($"AI 录制器：窗口 “{winKeyword}” | 地图 {map} | 赛季 {season}");
        Console.WriteLine($"每 {intervalMs}ms 截一帧（帧差 <{minDiff} 跳过）| 容量上限 {maxGb}GB | Ctrl+C 或控制台 q 结束");
        Console.WriteLine("全程零按键：正常打游戏即可，无需 alt-tab。输出: " + recDir);

        long saved = 0, skipped = 0;
        long bytes = 0;
        long maxBytes = (long)(maxGb * 1024L * 1024L * 1024L);
        using var prev = new Mat();
        bool hasPrev = false;
        bool quit = false;

        try
        {
            while (!quit && !CommandUtil.StopRequested)
            {
                quit |= Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q;
                if (quit) break;

                using Mat raw = CaptureService.CaptureWindowMat(hwnd, raiseAndWait: false);
                using var norm = FrameTools.Normalize(raw, runtime.DesignWidth, runtime.DesignHeight);
                bool changed = !hasPrev || FrameDiff.Score(prev, norm) >= minDiff;
                if (changed)
                {
                    string file = $"full_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
                    string relPath = Path.Combine("full", file);
                    string absPath = Path.Combine(recDir, relPath);
                    if (!norm.ImWrite(absPath, new[] { (int)ImwriteFlags.JpegQuality, cfg.Log.RoiJpegQuality }))
                        throw new IOException($"全帧保存失败: {absPath}");
                    File.AppendAllText(manifestPath,
                        $"{relPath},,{map},{season},{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                    norm.CopyTo(prev);
                    hasPrev = true;
                    saved++;
                    bytes += new FileInfo(absPath).Length;
                    if (bytes > maxBytes)
                    {
                        Console.WriteLine($"\n⛔ 已达容量上限 {maxGb}GB，自动停止（已存内容安全落盘）");
                        break;
                    }
                    if (saved % 40 == 0)
                        Console.Write($"  ✅ 已存 {saved} 帧（跳过静止 {skipped}，{bytes / 1048576f:F0}MB）\r");
                }
                else
                {
                    skipped++;
                }

                // 分片睡眠：Ctrl+C 急停 / q 退出最快 50ms 内响应
                for (int waited = 0; waited < intervalMs && !quit && !CommandUtil.StopRequested; waited += 50)
                {
                    quit |= Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q;
                    if (!quit) Thread.Sleep(50);
                }
            }
        }
        finally
        {
            Console.WriteLine($"\n🏁 录制结束：存 {saved} 帧 / 跳过静止 {skipped} 帧（{bytes / 1048576f:F0}MB）→ {manifestPath}");
            Console.WriteLine("   下一步: dotnet run -- ailabel  （图形界面事后标注，标签写回 passability manifest）");
        }
    }

    internal static string? GetOpt(string[] args, string name)
    {
        int idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    private static T ParseOpt<T>(string[] args, string name, T fallback) where T : IParsable<T>
        => GetOpt(args, name) is { } v && T.TryParse(v, null, out var parsed) ? parsed : fallback;
}
