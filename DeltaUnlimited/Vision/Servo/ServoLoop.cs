using DeltaUnlimited.Cli;

namespace DeltaUnlimited.Vision.Servo;

/// <summary>伺服循环骨架（编排层，感知/动作以委托注入 → 可用假实现单测，无需游戏）。
/// 职责：按固定频率跑"感知 → 决策（纯函数）→ 动作"，并内置四道收尾判据：
/// 急停（CommandUtil.StopRequested）、到达、卡死（帧差窗口）、超时/周期上限、可通行性预警持续过久。
/// 红线：① 每周期都查急停；② 只执行移动类动作（点击类动作由 chain 走安全网）；③ 任何异常都返回 Error 而不是抛出后失控。</summary>
public static class ServoLoop
{
    public sealed record Options
    {
        /// <summary>循环频率（Hz）。8Hz = 125ms/周期，足够跟标记且不显得机械。</summary>
        public double CycleHz { get; init; } = 8;

        /// <summary>最长运行时长（ms）。</summary>
        public int TimeoutMs { get; init; } = 180_000;

        /// <summary>周期数上限（保险丝，防时钟异常导致死循环）。</summary>
        public int MaxCycles { get; init; } = 10_000;

        /// <summary>卡死判定：连续 N 个周期帧差低于阈值。</summary>
        public int StuckNeedLow { get; init; } = 3;
        public double StuckDiffThreshold { get; init; } = 4.0;

        /// <summary>可通行性预警连续 N 个周期仍为 blocked → 交人工（不自动绕行）。</summary>
        public int BlockedGiveUpCycles { get; init; } = 10;
    }

    /// <summary>跑一轮伺服。
    /// <paramref name="perceive"/> 返回当前帧感知；<paramref name="act"/> 执行动作；
    /// <paramref name="clock"/> 返回毫秒时间（默认真实时钟，可注入）；<paramref name="sleep"/> 周期节流（可注入为空实现以便单测）。</summary>
    public static ServoResult Run(
        Func<ServoFrame> perceive,
        Action<ServoAction> act,
        ServoConfig cfg,
        Options opt,
        Random rng,
        Func<double>? clock = null,
        Action<int>? sleep = null,
        Func<bool>? stopped = null)
    {
        clock ??= () => Environment.TickCount64;
        sleep ??= Thread.Sleep;
        stopped ??= () => CommandUtil.StopRequested;

        double start = clock();
        int cycleMs = Math.Max(10, (int)Math.Round(1000.0 / Math.Max(0.5, opt.CycleHz)));
        var recentDiffs = new List<double>(opt.StuckNeedLow * 2);
        int blockedRun = 0;

        for (int cycle = 0; cycle < opt.MaxCycles; cycle++)
        {
            if (stopped())
                return new ServoResult(ServoOutcome.Stopped, "急停/人工中止", clock() - start);

            ServoFrame frame;
            try
            {
                frame = perceive();
            }
            catch (Exception ex)
            {
                return new ServoResult(ServoOutcome.Error, $"感知失败: {ex.Message}", clock() - start);
            }

            recentDiffs.Add(frame.FrameDiff);
            if (recentDiffs.Count > opt.StuckNeedLow * 2) recentDiffs.RemoveAt(0);

            if (ServoController.IsArrived(frame, cfg))
            {
                act(ServoAction.Idle);
                return new ServoResult(ServoOutcome.Success, $"已到达目标点（{frame.DistanceMeters:F0}m）", clock() - start);
            }

            if (ServoController.IsStuck(recentDiffs.ToArray(), opt.StuckDiffThreshold, opt.StuckNeedLow))
            {
                act(ServoAction.Idle); // 松手，交守卫/人工处理
                return new ServoResult(ServoOutcome.Stuck, $"连续 {opt.StuckNeedLow} 周期画面无变化（贴墙？）", clock() - start);
            }

            if (frame.HitBlocked)
            {
                blockedRun++;
                if (blockedRun >= opt.BlockedGiveUpCycles)
                {
                    act(ServoAction.Idle);
                    return new ServoResult(ServoOutcome.Danger, $"可通行性预警持续 {blockedRun} 周期——交人工", clock() - start);
                }
            }
            else
            {
                blockedRun = 0;
            }

            try
            {
                act(ServoController.Decide(frame, cfg, rng));
            }
            catch (Exception ex)
            {
                return new ServoResult(ServoOutcome.Error, $"动作失败: {ex.Message}", clock() - start);
            }

            if (clock() - start >= opt.TimeoutMs)
            {
                act(ServoAction.Idle);
                return new ServoResult(ServoOutcome.Timeout, $"超过时限 {opt.TimeoutMs}ms", clock() - start);
            }

            sleep(cycleMs);
        }

        act(ServoAction.Idle);
        return new ServoResult(ServoOutcome.Timeout, $"达到周期上限 {opt.MaxCycles}", clock() - start);
    }
}
