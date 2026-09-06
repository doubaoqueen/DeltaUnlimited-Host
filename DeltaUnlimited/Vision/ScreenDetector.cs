using DeltaUnlimited.Data;
using OpenCvSharp;

namespace DeltaUnlimited.Vision;

/// <summary>界面识别器：用 screens.json 的标记判断当前帧属于哪个界面（返回最佳匹配与可用操作）。</summary>
public static class ScreenDetector
{
    public sealed record ScreenGuess(string Name, double Confidence, IReadOnlyList<string> Actions)
    {
        public override string ToString() => $"{Name}（置信度 {Confidence:F3}）可用操作: {string.Join(" / ", Actions)}";
    }

    public sealed record Candidate(string Name, double Confidence, bool Matched);

    /// <summary>逐界面扫描所有标记，返回按置信度降序的候选（含未完全命中的），用于校准阈值与诊断。</summary>
    public static IReadOnlyList<Candidate> Scan(Mat frame, ScreenTable table, string repoRoot)
    {
        var list = new List<Candidate>();
        foreach (var (name, def) in table.Screens)
        {
            if (def.Markers.Count == 0) continue; // 无标记的界面不可检测（等待补模板）

            bool all = true;
            double worst = 1.0;
            foreach (var m in def.Markers)
            {
                if (m.Type == "template" && !string.IsNullOrEmpty(m.Template))
                {
                    var mr = TemplateMatcher.Match(frame, Path.Combine(repoRoot, m.Template), m.Threshold ?? 0.85);
                    worst = Math.Min(worst, mr.Confidence);
                    if (!mr.Found) all = false;
                }
                else
                {
                    all = false; // 其他标记类型待实现
                }
            }
            list.Add(new Candidate(name, worst, all));
        }
        return list.OrderByDescending(c => c.Confidence).ToList();
    }

    /// <summary>检测当前界面；无任何屏幕完全命中返回 null（未知界面）。</summary>
    public static ScreenGuess? Detect(Mat frame, ScreenTable table, string repoRoot)
    {
        var hit = Scan(frame, table, repoRoot).FirstOrDefault(c => c.Matched);
        return hit is null
            ? null
            : new ScreenGuess(hit.Name, hit.Confidence, table.Screens[hit.Name].Actions);
    }
}
