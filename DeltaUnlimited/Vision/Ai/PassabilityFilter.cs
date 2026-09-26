namespace DeltaUnlimited.Vision.Ai;

/// <summary>可通行性时域滤波（纯函数式状态机，无 IO，可直接单测）：
/// ① 弃权帧（no_ground / 低置信）不推进窗口；② 动静分离——ROI 内帧差超阈值 = 移动物体进入，该帧压制；
/// ③ 最近 window 帧有效结果中 ≥k 帧 blocked 才升级预警（单帧误识别不触发）。</summary>
public sealed class PassabilityFilter
{
    private readonly Queue<bool> _window = new();
    private readonly int _windowSize;
    private readonly int _k;
    private readonly double _motionDiffThreshold;
    private readonly double _minConfidence;

    public PassabilityFilter(int window, int k, double motionDiffThreshold, double minConfidence)
    {
        if (window < 1 || k < 1) throw new ArgumentException("window/k 必须 ≥1");
        _windowSize = window;
        _k = Math.Min(k, window);
        _motionDiffThreshold = motionDiffThreshold;
        _minConfidence = minConfidence;
    }

    /// <summary>当前预警级别（Push 之间保持不变）。</summary>
    public PathWarningLevel Current { get; private set; } = PathWarningLevel.None;

    /// <summary>推进一帧并返回最新预警级别。</summary>
    public PathWarning Push(PassabilityResult result, double frameDiff)
    {
        // 弃权帧：不动窗口状态（天空/遮挡/低置信/移动物体都不给地形下结论）
        if (result.Class == PassabilityClass.NoGround) return new PathWarning(Current, 0);
        if (frameDiff > _motionDiffThreshold) return new PathWarning(Current, 0);
        if (result.Confidence < _minConfidence) return new PathWarning(Current, 0);

        _window.Enqueue(result.Class == PassabilityClass.Blocked);
        while (_window.Count > _windowSize) _window.Dequeue();

        Current = _window.Count(b => b) >= _k ? PathWarningLevel.Blocked : PathWarningLevel.None;
        return new PathWarning(Current, result.Confidence);
    }

    /// <summary>重置（守卫触发/Escalate/传送后调用，避免旧状态污染）。</summary>
    public void Reset()
    {
        _window.Clear();
        Current = PathWarningLevel.None;
    }
}
