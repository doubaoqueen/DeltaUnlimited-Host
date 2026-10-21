using DeltaUnlimited.Data;
using DeltaUnlimited.Vision;
using DeltaUnlimited.Vision.Localization;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>localizetrack：按时间段批量定位（带运动学过滤），输出轨迹 CSV + 跳变统计。
/// 用途：离线验证定位稳定性、给路线标定提供轨迹、排查误定位。
/// 用法: localizetrack &lt;起始时间&gt; &lt;结束时间&gt; [--maxspeed 30] [--map 底图] [--scales ...]</summary>
public static class LocalizeTrackCommand
{
    public static void Run(string repoRoot, string[] cmdArgs)
    {
        if (cmdArgs.Length < 3)
            throw new ArgumentException(
                "用法: localizetrack <起始时间> <结束时间> [--maxspeed 30] [--map 底图] [--scales 150,180,...]\n" +
                "  时间格式: \"2026-10-12 00:28:20\"（与 record_manifest.csv 的 created 一致）");

        string from = cmdArgs[1];
        string to = cmdArgs[2];
        double maxSpeed = 30;
        string? mapRel = null;
        int[]? scales = null;
        for (int i = 3; i < cmdArgs.Length; i++)
        {
            switch (cmdArgs[i])
            {
                case "--maxspeed" when i + 1 < cmdArgs.Length:
                    if (double.TryParse(cmdArgs[++i], out double v)) maxSpeed = v;
                    break;
                case "--map" when i + 1 < cmdArgs.Length: mapRel = cmdArgs[++i]; break;
                case "--scales" when i + 1 < cmdArgs.Length:
                    scales = cmdArgs[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.TryParse(s.Trim(), out int v) ? v : 0).Where(v => v > 0).ToArray();
                    break;
            }
        }

        string manifest = Path.Combine(repoRoot, "ai-training", "datasets", "record", "record_manifest.csv");
        if (!File.Exists(manifest)) throw new FileNotFoundException($"找不到录制清单: {manifest}");
        var rows = File.ReadLines(manifest)
            .Select(l => l.Split(','))
            .Where(cols => cols.Length >= 5 && cols[4].Trim() is { Length: > 10 } ts && string.CompareOrdinal(ts, from) >= 0 && string.CompareOrdinal(ts, to) <= 0)
            .ToList();
        if (rows.Count == 0) throw new InvalidOperationException("该时间段没有录制帧");

        var cfg = new LocalizerConfig { TopK = 3 };
        if (mapRel is not null) cfg.MapImage = mapRel;
        if (scales is { Length: > 0 }) cfg.ScalesPx = scales;

        var runtime = new DataStore(repoRoot).LoadRuntime();
        var screens = new DataStore(repoRoot).LoadScreens();
        using var loc = new MinimapLocalizer(cfg, repoRoot);
        var track = new LocalizerTrack(new LocalizerTrack.Options { MaxSpeedPxPerSec = maxSpeed });

        string outDir = Path.Combine(repoRoot, "logs");
        Directory.CreateDirectory(outDir);
        string outCsv = Path.Combine(outDir,
            $"localizetrack_{from.Replace(":", "").Replace(" ", "_")}_{to.Replace(":", "").Replace(" ", "_")}.csv");
        int accepted = 0, held = 0, total = 0, uiSkipped = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using (var w = new StreamWriter(outCsv))
        {
            w.WriteLine("created,accepted,score,scale,cx,cy,note");
            foreach (var cols in rows)
            {
                string rel = cols[0].Replace('\\', '/');
                string created = cols[4].Trim();
                string path = Path.Combine(repoRoot, "ai-training", "datasets", "record", rel);
                total++;
                if (!File.Exists(path)) continue;

                using var img = Cv2.ImRead(path, ImreadModes.Color);
                if (img.Empty()) continue;
                using var norm = FrameTools.Normalize(img, runtime.DesignWidth, runtime.DesignHeight);

                // HUD 状态门：背包/地图/结算等全屏 UI 下没有小地图（2026-10-21 实测：Tab 背包被误当小地图）
                if (!HudStateGate.IsNormalHud(norm, screens, repoRoot, out string? uiScreen))
                {
                    uiSkipped++;
                    held++;
                    var holdPos = track.Last;
                    w.WriteLine($"\"{created}\",0,,,,,UI 界面（{uiScreen}）无小地图，跳过");
                    continue;
                }

                var raw = loc.Localize(norm);
                long nowMs = new DateTimeOffset(DateTime.Parse(created)).ToUnixTimeMilliseconds();
                var res = track.Accept(raw, nowMs);

                if (res.Accepted) accepted++; else if (res.Held) held++;
                var pos = res.Position;
                w.WriteLine($"\"{created}\",{(res.Accepted ? 1 : 0)},{(pos is null ? "" : pos.Score.ToString("F3"))}," +
                            $"{(pos is null ? "" : pos.ScalePx.ToString())},{(pos is null ? "" : pos.CenterX.ToString())}," +
                            $"{(pos is null ? "" : pos.CenterY.ToString())},\"{res.Note}\"");
                if (!res.Accepted)
                    Console.WriteLine($"  ⚠ {created} 拒绝：{res.Note}");
            }
        }

        sw.Stop();
        Console.WriteLine($"\n轨迹完成：{total} 帧 | 接受 {accepted} | 沿用 {held}（其中 UI 界面跳过 {uiSkipped}）| 耗时 {sw.Elapsed.TotalSeconds:F1}s（{sw.Elapsed.TotalMilliseconds / Math.Max(1, total):F0}ms/帧）");
        Console.WriteLine($"CSV: {outCsv}");
        if (held - uiSkipped > 0)
            Console.WriteLine($"提示：{held - uiSkipped} 帧被判超速（重复地形误定位/UI 遮挡漏网）——运动学过滤在起作用");
        if (uiSkipped > 0)
            Console.WriteLine($"提示：{uiSkipped} 帧因处于对局内 UI（背包/地图/结算）被跳过——HUD 状态门在起作用");
    }
}
