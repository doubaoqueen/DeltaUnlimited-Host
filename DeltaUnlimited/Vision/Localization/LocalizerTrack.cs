namespace DeltaUnlimited.Vision.Localization;

/// <summary>定位轨迹过滤器：用"人不可能瞬移"+"真位置会连续重复出现"两条物理/统计约束剔除误定位。
///
/// 背景（2026-10-21 实测）：
///  - 小地图会被 UI 面板遮挡（如行动接取站面板），NCC 仍可能给 0.6+ 分但位置全错（实测 2 秒瞬移 1394px）；
///  - 单帧低分也可能先被接受，成为错误锚点，随后把正确位置全部判为"超速"（实测首次锚点 0.520 分即错）。
///
/// 规则：
///  1. **首锚需确认**：首帧候选先挂起，第二帧候选与之一致（≤ InitTolerancePx）才锁定位置；
///  2. **速度门**：只接受从上次确认位置出发、速度 ≤ MaxSpeedPxPerSec 的候选；
///  3. **重锚定**：连续 N 帧被拒但候选彼此一致（≤ ReanchorTolerancePx）→ 认定锚点漂了，重新锚定（并报出）。
/// 纯逻辑（无 IO、无外部状态），可单测。</summary>
public sealed class LocalizerTrack
{
    public sealed class Options
    {
        /// <summary>允许的最大移动速度（底图像素/秒）。冲刺约 18px/s（≈7m/s，按 0.4m/px），取 30 留余量。</summary>
        public double MaxSpeedPxPerSec { get; init; } = 30;

        /// <summary>时间上限（ms）：两帧间隔超过此值时按此值计算允许位移（防长空档放大瞬移）。</summary>
        public long MaxGapMs { get; init; } = 30_000;

        /// <summary>首锚确认容差（px）：第二帧候选需落在首帧候选附近。</summary>
        public double InitTolerancePx { get; init; } = 120;

        /// <summary>重锚定需要的连续一致帧数。</summary>
        public int ReanchorAfter { get; init; } = 3;

        /// <summary>重锚定一致性容差（px）。</summary>
        public double ReanchorTolerancePx { get; init; } = 100;
    }

    /// <summary>过滤结果：Accepted=本帧可信；Held=本帧被拒（Position 为沿用位置，可能为 null）。</summary>
    public sealed record TrackResult(bool Accepted, bool Held, LocalizeCandidate? Position, string Note);

    private readonly Options _opt;
    private LocalizeCandidate? _last;
    private long _lastMs = long.MinValue;
    private LocalizeCandidate? _seed;                 // 首锚确认用
    private readonly List<LocalizeCandidate> _pending = new();   // 被拒候选（重锚定用）

    public LocalizerTrack(Options? opt = null) => _opt = opt ?? new Options();

    public LocalizeCandidate? Last => _last;

    public void Reset()
    {
        _last = null;
        _lastMs = long.MinValue;
        _seed = null;
        _pending.Clear();
    }

    public TrackResult Accept(LocalizeResult raw, long nowMs)
    {
        if (!raw.Found || raw.Best is null)
            return new TrackResult(false, _last is not null, _last, "无定位结果，沿用上次位置");

        // ① 尚无锚点：需要两帧一致才锁定
        if (_last is null)
        {
            if (_seed is null)
            {
                _seed = raw.Best;
                return new TrackResult(false, false, null, "首帧候选，等待第二帧确认");
            }
            double ds = Dist(_seed, raw.Best);
            if (ds <= _opt.InitTolerancePx)
            {
                _last = raw.Best;
                _lastMs = nowMs;
                _seed = null;
                _pending.Clear();
                return new TrackResult(true, false, _last, $"首锚确认（两帧相距 {ds:F0}px）");
            }
            _seed = raw.Best;                        // 不一致：改用新候选重新等确认
            return new TrackResult(false, false, null, "首帧候选不一致，重新等待确认");
        }

        // ② 速度门
        long gap = Math.Clamp(nowMs - _lastMs, 1, _opt.MaxGapMs);
        double maxDist = _opt.MaxSpeedPxPerSec * gap / 1000.0;
        foreach (var c in raw.Candidates)
        {
            double d = Dist(_last, c);
            if (d <= maxDist)
            {
                _last = c;
                _lastMs = nowMs;
                _pending.Clear();
                return new TrackResult(true, false, c, $"接受（位移 {d:F0}px ≤ 上限 {maxDist:F0}px）");
            }
        }

        // ③ 全部超速：累积候选，若连续一致则重锚定
        _pending.Add(raw.Best);
        if (_pending.Count > _opt.ReanchorAfter) _pending.RemoveAt(0);
        if (_pending.Count >= _opt.ReanchorAfter && ClusterConsistent())
        {
            var re = _pending[^1];
            _last = re;
            _lastMs = nowMs;
            _pending.Clear();
            return new TrackResult(true, false, re, $"重锚定（连续 {_opt.ReanchorAfter} 帧一致指向新位置）");
        }

        double nearest = raw.Candidates.Min(c => Dist(_last, c));
        return new TrackResult(false, true, _last,
            $"候选超速（最近 {nearest:F0}px > 上限 {maxDist:F0}px），沿用上次位置");
    }

    private bool ClusterConsistent()
    {
        for (int i = 0; i < _pending.Count; i++)
            for (int j = i + 1; j < _pending.Count; j++)
                if (Dist(_pending[i], _pending[j]) > _opt.ReanchorTolerancePx)
                    return false;
        return true;
    }

    private static double Dist(LocalizeCandidate a, LocalizeCandidate b)
        => Math.Sqrt(Math.Pow(a.CenterX - b.CenterX, 2) + Math.Pow(a.CenterY - b.CenterY, 2));
}
