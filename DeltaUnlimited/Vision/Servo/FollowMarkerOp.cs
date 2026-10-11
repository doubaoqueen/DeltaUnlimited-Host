namespace DeltaUnlimited.Vision.Servo;

/// <summary>follow_marker（撤离跟随伺服）的 chain 薄壳（跑刀工程提案 §1）：
/// chain 里一个 op = 同步阻塞跑一轮伺服，返回 ServoResult 供 chain 按 JSON 分支走结局。
/// 薄壳职责只有三件：接线（感知/动作/停止检查注入）、try/finally 兜底松键、把 ServoResult 交给 chain。
/// 感知实现（导航标记模板匹配）K0 标定后填；本壳与 ServoLoop 完全可用假实现单测。</summary>
public static class FollowMarkerOp
{
    /// <summary>跑一轮"跟随导航标记"。<paramref name="stopped"/> 默认接全局急停标记。
    /// 任何结局（成功/超时/急停/卡死/危险/异常）都保证 <see cref="IServoActor.Release"/>（松键兜底）。</summary>
    public static ServoResult Run(
        IServoPerceiver perceive,
        IServoActor act,
        ServoConfig cfg,
        ServoLoop.Options opt,
        Random rng,
        Func<bool>? stopped = null)
    {
        try
        {
            return ServoLoop.Run(perceive.Observe, act.Perform, cfg, opt, rng, stopped: stopped);
        }
        finally
        {
            act.Release();   // 提案 §3：每个动作块 try/finally ReleaseAllHeldKeys
        }
    }
}
