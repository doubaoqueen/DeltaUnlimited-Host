namespace DeltaUnlimited.Vision.Servo;

/// <summary>伺服控制参数（全部随机范围——项目铁律：宁可慢，不可机械）。
/// 参数最终由 data/runtime.json 的 servo 段提供；本类给出安全默认值。</summary>
public sealed class ServoConfig
{
    /// <summary>比例控制增益范围（像素偏差 → 相对移动像素）。</summary>
    public double TurnGainMin { get; set; } = 0.35;
    public double TurnGainMax { get; set; } = 0.55;

    /// <summary>死区（px）：偏差在死区内不转向，避免抖动。</summary>
    public int DeadzonePx { get; set; } = 40;

    /// <summary>每周期最大转向量（px，硬钳制防失控）。</summary>
    public int MaxTurnPerCycle { get; set; } = 260;

    /// <summary>转向随机抖动（px，叠加在计算量上）。</summary>
    public int TurnJitterPx { get; set; } = 12;

    /// <summary>到达判定距离（米）：≤ 此值视为到达标记点。</summary>
    public double ArriveMeters { get; set; } = 12;

    /// <summary>减速距离（米）：进入该距离后不再冲刺。</summary>
    public double SprintUntilMeters { get; set; } = 60;

    /// <summary>标记不可见时的前进时长占比（0-1）：不盲目狂奔，走走停停等标记重现。</summary>
    public double SearchForwardRatio { get; set; } = 0.6;
}

/// <summary>伺服控制器（**纯函数**，无 IO、无输入注入，可单测）：
/// 输入一帧感知状态 + 随机源 → 输出本周期动作（视角/前进/冲刺）。
/// 红线：只产出移动类动作；不点击、不按键、不做决策裁决（决策在 chain 状态机）。
/// 与 AI 预警的关系：HitBlocked 只是"提前拉动既有守卫"的信号，控制器据此松前进键，不自行绕行。</summary>
public static class ServoController
{
    /// <summary>决定本周期动作。<paramref name="rng"/> 用于随机范围（增益/抖动/搜索节奏），不得用固定值。</summary>
    public static ServoAction Decide(ServoFrame frame, ServoConfig cfg, Random rng, bool arrived = false)
    {
        if (arrived)
            return new ServoAction(0, false, false, "已到达目标点");

        // ① 转向：比例控制 + 死区 + 随机抖动 + 硬钳制
        double gain = cfg.TurnGainMin + rng.NextDouble() * (cfg.TurnGainMax - cfg.TurnGainMin);
        int turn = 0;
        if (frame.MarkerVisible && Math.Abs(frame.MarkerOffsetPx) > cfg.DeadzonePx)
        {
            double raw = frame.MarkerOffsetPx * gain;
            int jitter = rng.Next(-cfg.TurnJitterPx, cfg.TurnJitterPx + 1);
            turn = (int)Math.Round(raw) + jitter;
            turn = Math.Clamp(turn, -cfg.MaxTurnPerCycle, cfg.MaxTurnPerCycle);
            if (turn == 0) turn = Math.Sign(frame.MarkerOffsetPx); // 钳到 0 也要给一个最小方向量，避免僵住
        }

        // ② 前进：标记可见就前进；不可见时按比例"走走停停"（避免一路狂奔错过标记）
        bool forward;
        string note;
        if (frame.MarkerVisible)
        {
            forward = true;
            note = frame.DistanceMeters >= 0 ? $"跟随标记（{frame.DistanceMeters:F0}m）" : "跟随标记（距离未知）";
        }
        else
        {
            forward = rng.NextDouble() < cfg.SearchForwardRatio;
            note = forward ? "标记不可见：搜索式前进" : "标记不可见：原地观察";
        }

        // ③ 冲刺：距离远才冲；AI 预警 blocked 时立刻松前进键（安全优先）
        bool sprint = forward && frame.MarkerVisible
                      && (frame.DistanceMeters < 0 || frame.DistanceMeters > cfg.SprintUntilMeters);
        if (frame.HitBlocked)
        {
            forward = false;
            sprint = false;
            note = "可通行性预警：松前进键（等守卫/人工）";
        }

        return new ServoAction(turn, forward, sprint, note);
    }

    /// <summary>是否已到达（距离可读且 ≤ 到达阈值）。距离不可读时返回 false（交由上层用其它判据）。</summary>
    public static bool IsArrived(ServoFrame frame, ServoConfig cfg)
        => frame.DistanceMeters >= 0 && frame.DistanceMeters <= cfg.ArriveMeters;

    /// <summary>卡死判据（守卫兜底）：连续 <paramref name="needLow"/> 个周期帧差低于阈值即判定卡住。
    /// 只做判定，不做动作——动作仍走既有 Escalate 路径（与 patrol 一致）。</summary>
    public static bool IsStuck(double[] recentFrameDiffs, double threshold, int needLow)
    {
        if (recentFrameDiffs.Length < needLow) return false;
        int low = 0;
        for (int i = recentFrameDiffs.Length - needLow; i < recentFrameDiffs.Length; i++)
            if (recentFrameDiffs[i] < threshold) low++;
        return low >= needLow;
    }
}
