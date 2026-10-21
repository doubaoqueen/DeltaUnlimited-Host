using DeltaUnlimited.Data;
using DeltaUnlimited.Vision;
using DeltaUnlimited.Vision.Localization;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>localize：小地图定位诊断——对一张局内截图做"小地图↔官方底图"匹配，输出玩家在地图上的位置。</summary>
public static class LocalizeCommand
{
    public static void Run(string repoRoot, string[] cmdArgs)
    {
        if (cmdArgs.Length < 2)
            throw new ArgumentException(
                "用法: localize <截图相对路径> [--map 底图相对路径] [--scales 150,180,...] [--topk 3]");

        string imageRel = cmdArgs[1];
        string? mapRel = null;
        int[]? scales = null;
        int topk = 3;
        double? minScore = null;
        for (int i = 2; i < cmdArgs.Length; i++)
        {
            switch (cmdArgs[i])
            {
                case "--map" when i + 1 < cmdArgs.Length: mapRel = cmdArgs[++i]; break;
                case "--scales" when i + 1 < cmdArgs.Length:
                    scales = cmdArgs[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.TryParse(s.Trim(), out int v) ? v : 0).Where(v => v > 0).ToArray();
                    break;
                case "--topk" when i + 1 < cmdArgs.Length: int.TryParse(cmdArgs[++i], out topk); break;
                case "--minscore" when i + 1 < cmdArgs.Length:
                    if (double.TryParse(cmdArgs[++i], out double ms)) minScore = ms;
                    break;
            }
        }

        using var img = Cv2.ImRead(Path.Combine(repoRoot, imageRel), ImreadModes.Color);
        if (img.Empty()) throw new FileNotFoundException($"图片读取失败: {imageRel}");
        var runtime = new DataStore(repoRoot).LoadRuntime();
        using var norm = FrameTools.Normalize(img, runtime.DesignWidth, runtime.DesignHeight);

        var cfg = new LocalizerConfig { TopK = Math.Max(1, topk) };
        if (mapRel is not null) cfg.MapImage = mapRel;
        if (scales is { Length: > 0 }) cfg.ScalesPx = scales;
        if (minScore is { } msv) cfg.MinScore = msv;

        using var loc = new MinimapLocalizer(cfg, repoRoot);
        var r = loc.Localize(norm);

        Console.WriteLine($"小地图面板: 圆心({cfg.MiniCenterX},{cfg.MiniCenterY}) r={cfg.MiniRadius} | 底图 {loc.MapWidth}x{loc.MapHeight}");
        if (!r.Found)
        {
            Console.WriteLine("未定位 ❌（没有任何尺度达到最低分）");
            return;
        }

        Console.WriteLine("定位候选（按分数）:");
        int k = 1;
        foreach (var c in r.Candidates)
            Console.WriteLine($"  #{k++} score={c.Score:F3} scale={c.ScalePx}px 中心=({c.CenterX},{c.CenterY})");

        // 预览图：底图上画候选框
        using var preview = loc.RenderPreview(r);
        string outDir = Path.Combine(repoRoot, "screenshots", "captured");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, "localize_" + Path.GetFileNameWithoutExtension(imageRel) + ".png");
        Cv2.ImWrite(outPath, preview);
        Console.WriteLine($"预览已保存: {outPath}");

        if (r.Candidates.Count > 1)
            Console.WriteLine("⚠️ 多候选分数接近时勿单帧采信（重复地形会制造高分误报，见 docs/小地图定位P0验证.md §4）");
    }
}
