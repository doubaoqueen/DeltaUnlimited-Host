using DeltaUnlimited.Capture;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
using DeltaUnlimited.Vision;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>aicollect：可通行性数据集采集器（纯截图，不注入任何输入）。
/// 边看边标：1=可通行 2=不可通行 0=无地面 b=背景负样本 q=退出；按下的瞬间保存当前帧 ROI 并追加 manifest.csv。
/// 另按 background_every 自动存背景帧（负样本池）。产物直接落在 ai-training/datasets/passability/，训练端即取即用。</summary>
public static class AiCollectCommand
{
    private static readonly (ConsoleKey Key, string Label)[] LabeledKeys =
    {
        (ConsoleKey.D1, "passable"),
        (ConsoleKey.D2, "blocked"),
        (ConsoleKey.D0, "no_ground"),
        (ConsoleKey.B, "background"),
    };

    public static void Run(DataStore data, string repoRoot, string[] cmdArgs)
    {
        var runtime = data.LoadRuntime();
        var cfg = data.LoadAiVision();
        string map = GetOpt(cmdArgs, "--map") ?? "零号大坝";
        string season = GetOpt(cmdArgs, "--season") ?? "unknown";
        string winKeyword = GetOpt(cmdArgs, "--win") ?? runtime.WindowKeyword;

        string dsDir = Path.Combine(repoRoot, "ai-training", "datasets", "passability");
        string manifestPath = Path.Combine(dsDir, "manifest.csv");
        Directory.CreateDirectory(Path.Combine(dsDir, "images"));
        if (!File.Exists(manifestPath))
            File.AppendAllText(manifestPath, "relpath,label,map,season,source,created\n");

        IntPtr hwnd = CaptureService.FindWindowByTitle(winKeyword);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{winKeyword}” 的窗口");
        CaptureService.LogWindowAnchor(hwnd, runtime.DesignWidth, runtime.DesignHeight);

        Console.WriteLine($"AI 采集器：窗口 “{winKeyword}” | 地图 {map} | 赛季 {season}");
        Console.WriteLine("操作（边玩边标，按键保存当前画面 ROI）： 1=可通行  2=不可通行  0=无地面  b=背景  q=退出");
        Console.WriteLine($"背景帧每 {cfg.Log.BackgroundEvery} 帧自动存一张。输出: {Path.GetRelativePath(repoRoot, dsDir)}");
        InputService.EnsureForeground(hwnd);

        var counts = new Dictionary<string, int>();
        long frameIndex = 0;
        try
        {
            while (true)
            {
                string? label = null;
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    if (key == ConsoleKey.Q) break;
                    label = LabeledKeys.FirstOrDefault(k => k.Key == key).Label;
                }

                Mat raw = CaptureService.CaptureWindowMat(winKeyword, raiseAndWait: false);
                using (raw)
                using (var norm = FrameTools.Normalize(raw, runtime.DesignWidth, runtime.DesignHeight))
                {
                    var (x, y, w, h) = ClampRoi(cfg.Roi, runtime.DesignWidth, runtime.DesignHeight);
                    using var roi = new Mat(norm, new Rect(x, y, w, h));
                    frameIndex++;

                    bool autoBackground = cfg.Log.BackgroundEvery > 0 && frameIndex % cfg.Log.BackgroundEvery == 0;
                    string? toSave = label ?? (autoBackground ? "background" : null);
                    if (toSave is not null)
                    {
                        string file = $"roi_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
                        string relDir = Path.Combine("images", toSave);
                        Directory.CreateDirectory(Path.Combine(dsDir, relDir));
                        roi.ImWrite(Path.Combine(dsDir, relDir, file),
                            new[] { (int)ImwriteFlags.JpegQuality, cfg.Log.RoiJpegQuality });
                        File.AppendAllText(manifestPath,
                            $"{Path.Combine(relDir, file)},{toSave},{map},{season},collect,{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                        counts[toSave] = counts.GetValueOrDefault(toSave) + 1;
                        Console.Write($"  ✅ 已存 {toSave}（共 {string.Join(" ", counts.Select(kv => $"{kv.Key}:{kv.Value}"))}）\r");
                    }
                }
                Thread.Sleep(250); // ~4Hz，与人类标注节奏匹配
            }
        }
        finally
        {
            Console.WriteLine($"\n🏁 采集结束：{string.Join(" ", counts.Select(kv => $"{kv.Key}:{kv.Value}"))}（manifest: {manifestPath}）");
        }
    }

    internal static (int X, int Y, int W, int H) ClampRoi(List<int> roi, int designW, int designH)
    {
        int x = Math.Clamp(roi[0], 0, designW - 1);
        int y = Math.Clamp(roi[1], 0, designH - 1);
        int w = Math.Clamp(roi[2], 1, designW - x);
        int h = Math.Clamp(roi[3], 1, designH - y);
        return (x, y, w, h);
    }

    private static string? GetOpt(string[] args, string name)
    {
        int idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
