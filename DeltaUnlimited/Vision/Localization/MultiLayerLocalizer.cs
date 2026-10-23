using OpenCvSharp;

namespace DeltaUnlimited.Vision.Localization;

/// <summary>多图层定位器（债4 2026-10）：FloorTracker 状态机 + 每层一个 MinimapLocalizer 的薄组合。
/// 搜索策略（省时关键）：非 Unknown 时只搜"当前层 + 邻接层"（通常 2-3 层，且带先验窗口）；
/// Unknown 时全层搜索做首锚。每层各自维护先验（换层互不污染坐标）。
/// 红线同 MinimapLocalizer：纯离线图像分析。</summary>
public sealed class MultiLayerLocalizer : IDisposable
{
    private readonly FloorConfig _floor;
    private readonly Dictionary<string, MinimapLocalizer> _loc;
    private readonly Dictionary<string, LocalizePrior?> _priors = new();
    private readonly FloorTracker _tracker;

    public MultiLayerLocalizer(FloorConfig floor, Func<string, LocalizerConfig> cfgOf, string root)
    {
        _floor = floor;
        _tracker = new FloorTracker(floor);
        _loc = floor.Layers.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var c = cfgOf(kv.Key);          // 每次返回新实例（或可变独占实例），只覆写底图路径
                c.MapImage = kv.Value;
                return new MinimapLocalizer(c, root);
            });
    }

    public string? CurrentFloor => _tracker.Current;

    /// <summary>对一帧局内图定位：逐候选层匹配 → 状态机判层 → 返回判定层的定位结果。
    /// minScore 语义同单层（LocalizerConfig.MinScore）。</summary>
    public (string? Floor, LocalizeResult Result) Localize(Mat inGameFrame, double minScore = 0.50)
    {
        string[] candidates = _tracker.Current is { } cur
            ? _floor.Layers.Keys.Where(l => FloorTracker.Adjacent(_floor, cur, l)).ToArray()
            : _floor.Layers.Keys.ToArray();

        var scores = new Dictionary<string, double>();
        var results = new Dictionary<string, LocalizeResult>();
        foreach (var layer in candidates)
        {
            var r = _loc[layer].Localize(inGameFrame, _priors.GetValueOrDefault(layer));
            results[layer] = r;
            scores[layer] = r.Best?.Score ?? 0.0;
            if (r.Found && r.Best is { } b)
                _priors[layer] = new LocalizePrior(b.CenterX, b.CenterY, b.ScalePx);
        }

        string? chosen = _tracker.Update(scores, minScore);
        if (chosen is null || !results.TryGetValue(chosen, out var res))
            return (chosen, LocalizeResult.Miss);
        // 判定层的候选分数若未达线，状态机可能仍维持旧层（滞回）——返回该层真实结果，由上层决定采信度
        return (chosen, res);
    }

    /// <summary>换局/换图复位（层态与各层先验全清）。</summary>
    public void Reset()
    {
        _tracker.Reset();
        _priors.Clear();
    }

    public void Dispose()
    {
        foreach (var l in _loc.Values) l.Dispose();
    }
}
