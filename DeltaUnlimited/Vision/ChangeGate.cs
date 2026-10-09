namespace DeltaUnlimited.Vision;

/// <summary>识别变更门（纯逻辑，可单测）：等待界面时的"要不要真扫一遍"决策。
/// 背景：单次全界面 Detect ≈ 600-700ms（约 7 次 OCR），而等待期间画面多数时候毫无变化——
/// 每轮都全量重扫纯属浪费。策略：先看轻量帧差，画面没动就跳过识别；同时保证
/// ① 首轮必扫 ② 兜底每 N 轮必扫 ③ 距上次识别超过 maxStaleMs 必扫——避免"锚在静止画面"上漏掉变化。
/// 语义保守：宁可多扫一次，不可漏掉界面切换。</summary>
public sealed class ChangeGate
{
    private readonly double _diffThreshold;
    private readonly int _forceEveryN;
    private readonly int _maxStaleMs;
    private int _sinceDetect;
    private long _lastDetectTicks = -1; // -1 = 还没识别过（不能用 0 当哨兵：它是合法 ticks 值）
    private bool _first = true;

    /// <param name="diffThreshold">帧差低于此值视为"画面没动"（FrameDiff.Score 语义：≥3 明显变化、≥0.8 轻微）。</param>
    /// <param name="forceEveryN">连续跳过 N 轮后强制扫一次（兜底，防缓慢渐变被一直跳过）。</param>
    /// <param name="maxStaleMs">距上次识别的最大间隔（ms），超过则强制扫。</param>
    public ChangeGate(double diffThreshold = 1.2, int forceEveryN = 5, int maxStaleMs = 3000)
    {
        _diffThreshold = diffThreshold;
        _forceEveryN = Math.Max(1, forceEveryN);
        _maxStaleMs = Math.Max(1, maxStaleMs);
    }

    /// <summary>已跳过的轮数（诊断用）。</summary>
    public int Skipped { get; private set; }

    /// <summary>是否应该执行完整识别。</summary>
    public bool ShouldDetect(double frameDiff, long nowTicks)
    {
        if (_first)
        {
            _first = false;
            MarkDetected(nowTicks);
            return true;
        }
        bool changed = frameDiff >= _diffThreshold;
        bool forceByCount = _sinceDetect >= _forceEveryN;
        bool forceByAge = _lastDetectTicks >= 0 && (nowTicks - _lastDetectTicks) / TimeSpan.TicksPerMillisecond >= _maxStaleMs;

        if (changed || forceByCount || forceByAge)
        {
            MarkDetected(nowTicks);
            return true;
        }
        _sinceDetect++;
        Skipped++;
        return false;
    }

    private void MarkDetected(long nowTicks)
    {
        _sinceDetect = 0;
        _lastDetectTicks = nowTicks;
    }
}
