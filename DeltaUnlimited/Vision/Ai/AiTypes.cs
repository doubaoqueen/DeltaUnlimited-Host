namespace DeltaUnlimited.Vision.Ai;

/// <summary>可通行性三分类：blocked=前方有障碍/无法通过，no_ground=模型弃权（天空/贴脸遮挡/UI 覆盖/低置信）。</summary>
public enum PassabilityClass
{
    Passable = 0,
    Blocked = 1,
    NoGround = 2,
}

/// <summary>单帧感知结果（纯传感器输出，无决策语义）。</summary>
public sealed record PassabilityResult(
    PassabilityClass Class,
    float[] Probs,
    double Confidence,
    double LatencyMs);

/// <summary>滤波后的预警级别：Blocked=近端阻挡（v1 单 ROI 只有这两级；Warn(远端) 预留给 v2 双带 ROI）。</summary>
public enum PathWarningLevel
{
    None = 0,
    Blocked = 1,
}

/// <summary>时域滤波输出的预警信号（消费方：状态机；模型永不直接驱动动作）。</summary>
public sealed record PathWarning(PathWarningLevel Level, double Confidence)
{
    public static readonly PathWarning None = new(PathWarningLevel.None, 0);
}
