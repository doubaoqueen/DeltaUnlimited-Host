namespace DeltaUnlimited.Vision.Ai;

/// <summary>分布漂移监测（纯逻辑，时钟可注入以便单测）：
/// 以贴墙守卫的触发作为免费的"地面真值"——模型平静但守卫触发 = 假阴性矛盾（模型变瞎的主要信号）。
/// 时间滑窗内矛盾率超过阈值（且样本量足够）→ 建议自动禁用模块（翻转 enabled，与手动一键关闭同一开关语义）。</summary>
public sealed class DriftMonitor
{
    private readonly double _rateLimit;
    private readonly TimeSpan _window;
    private readonly int _minSamples;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Queue<(DateTimeOffset Ts, bool Contradiction)> _events = new();

    public DriftMonitor(double rateLimit, TimeSpan window, int minSamples, Func<DateTimeOffset>? clock = null)
    {
        if (rateLimit <= 0 || rateLimit > 1) throw new ArgumentException("rateLimit 应在 (0,1]");
        _rateLimit = rateLimit;
        _window = window;
        _minSamples = minSamples;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>记录一次"守卫事实 vs 模型预测"对照。</summary>
    public void Record(bool contradiction)
    {
        var now = _clock();
        _events.Enqueue((now, contradiction));
        Prune(now);
    }

    /// <summary>窗口内样本数。</summary>
    public int Count
    {
        get
        {
            Prune(_clock());
            return _events.Count;
        }
    }

    /// <summary>窗口内矛盾率（样本数不足时无意义，先看 ShouldDisable）。</summary>
    public double ContradictionRate
    {
        get
        {
            Prune(_clock());
            if (_events.Count == 0) return 0;
            return _events.Count(e => e.Contradiction) / (double)_events.Count;
        }
    }

    /// <summary>是否应自动禁用（样本量足够 且 矛盾率超限）。</summary>
    public bool ShouldDisable
    {
        get
        {
            Prune(_clock());
            return _events.Count >= _minSamples
                   && _events.Count(e => e.Contradiction) / (double)_events.Count >= _rateLimit;
        }
    }

    public void Reset() => _events.Clear();

    private void Prune(DateTimeOffset now)
    {
        while (_events.Count > 0 && now - _events.Peek().Ts > _window) _events.Dequeue();
    }
}
