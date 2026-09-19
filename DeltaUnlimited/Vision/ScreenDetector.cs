using System.Collections.Concurrent;
using DeltaUnlimited.Data;
using DeltaUnlimited.Vision.Ocr;
using OpenCvSharp;
using OcrSvc = DeltaUnlimited.Vision.Ocr.Ocr; // 与子命名空间 Ocr 同名的类，用别名避免遮蔽

namespace DeltaUnlimited.Vision;

/// <summary>
/// 界面识别器：用 screens.json 的标记判断当前帧属于哪个界面。
/// 语义：任一标记命中即判定为该界面（置信度取命中标记的最高值）——使"主标记 + 兜底标记"分层自然成立。
/// 标记类型：template（纯图标/兜底）与 ocr（关键词严格命中，主力）。
/// </summary>
public static class ScreenDetector
{
    private static readonly ConcurrentDictionary<string, ZoneTable> ZoneCache = new();

    public sealed record ScreenGuess(string Name, double Confidence, IReadOnlyList<string> Actions, IReadOnlyList<string> Alternatives)
    {
        public override string ToString() => $"{Name}（置信度 {Confidence:F3}）可用操作: {string.Join(" / ", Actions)}";
    }

    public sealed record Candidate(string Name, double Confidence, bool Matched, int MarkerHits);

    /// <summary>逐界面扫描所有标记，返回按置信度降序的候选（含未命中的），用于校准阈值与诊断。</summary>
    public static IReadOnlyList<Candidate> Scan(Mat frame, ScreenTable table, string repoRoot)
    {
        var list = new List<Candidate>();
        foreach (var (name, def) in table.Screens)
        {
            if (def.Enabled == false) continue; // 显式禁用
            if (def.Markers.Count == 0) continue; // 无标记的界面不可检测（等待补标记）

            bool hit = false;
            double best = 0;
            int markerHits = 0;
            foreach (var m in def.Markers)
            {
                if (m.Type == "template" && !string.IsNullOrEmpty(m.Template))
                {
                    var region = ResolveRegion(m.Region, m.RegionName, repoRoot);
                    var mr = TemplateMatcher.Match(frame, Path.Combine(repoRoot, m.Template), m.Threshold ?? 0.85, region);
                    if (mr.Found)
                    {
                        hit = true;
                        markerHits++;
                        best = Math.Max(best, mr.Confidence);
                    }
                }
                else if (m.Type == "ocr" && m.Keywords is { Count: > 0 })
                {
                    var region = ResolveRegion(m.Region, m.RegionName, repoRoot);
                    bool markerHit = m.RequireAll == true
                        ? OcrSvc.FindAllStrict(frame, region, m.Keywords).Count == m.Keywords.Count // 区域内全部关键词命中
                        : OcrSvc.FindStrict(frame, region, m.Keywords) is { Found: true };          // 任一关键词命中
                    if (markerHit)
                    {
                        hit = true;
                        markerHits++;
                        best = Math.Max(best, 1.0);
                    }
                }
                // 其他类型（color 等）待实现
            }
            list.Add(new Candidate(name, best, hit, markerHits));
        }
        return list.OrderByDescending(c => c.Confidence).ToList();
    }

    /// <summary>列出"未配置任何标记"的界面名（诊断提示用，避免静默失败）。</summary>
    public static IReadOnlyList<string> UnconfiguredScreens(ScreenTable table)
        => table.Screens
            .Where(kv => kv.Value.Enabled != false && kv.Value.Markers.Count == 0)
            .Select(kv => kv.Key)
            .OrderBy(x => x)
            .ToList();

    /// <summary>检测当前界面；无任何屏幕命中返回 null（未知界面）。
    /// 多界面同时命中时按（置信度, 命中标记数）取最优，其余界面放入 Alternatives 供调用方告警。</summary>
    public static ScreenGuess? Detect(Mat frame, ScreenTable table, string repoRoot)
    {
        var matched = Scan(frame, table, repoRoot).Where(c => c.Matched).ToList();
        if (matched.Count == 0) return null;

        var best = matched.OrderByDescending(c => c.Confidence).ThenByDescending(c => c.MarkerHits).First();
        var alts = matched.Where(c => c.Name != best.Name).Select(c => c.Name).ToList();
        return new ScreenGuess(best.Name, best.Confidence, table.Screens[best.Name].Actions, alts);
    }

    /// <summary>解析标记区域：显式 region 数组优先，其次按名查 zones 表。</summary>
    private static int[]? ResolveRegion(List<int>? region, string? regionName, string repoRoot)
    {
        if (region is { Count: 4 }) return region.ToArray();
        if (!string.IsNullOrEmpty(regionName))
        {
            var zones = ZoneCache.GetOrAdd(repoRoot, r => new DataStore(r).LoadZones());
            if (zones.Zones.TryGetValue(regionName, out var z) && z is { Count: 4 })
                return z.ToArray();
        }
        return null;
    }
}
