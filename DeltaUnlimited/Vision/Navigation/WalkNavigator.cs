namespace DeltaUnlimited.Vision.Navigation;

/// <summary>自动跑图实验（v1 虫子走路 + 倒车请注意）的纯导航库：方位数学、航向伺服、脱困策略、周期决策。
/// 全部纯函数/纯状态机（随机源注入），无 IO 无游戏依赖——单测即真机。
/// 坐标/方位约定：底图 4096×3072 像素空间（x 右 y 下），方位角 0°=北（底图上方）、顺时针、
/// 东=90°——与"小地图北朝上静态图"（定位 P0 已证）一致。鼠标 +dx = 右转 = 方位角增大。</summary>
public static class NavMath
{
    /// <summary>两点方位角（0=北，顺时针，0-360）。</summary>
    public static double BearingDeg(double fromX, double fromY, double toX, double toY)
        => Norm(Math.Atan2(toX - fromX, -(toY - fromY)) * 180.0 / Math.PI);

    /// <summary>位移向量的航向角（同口径）。</summary>
    public static double CourseDeg(double dx, double dy)
        => Norm(Math.Atan2(dx, -dy) * 180.0 / Math.PI);

    /// <summary>角差 to-from，归一到 [-180, 180)。正值 = 目标在顺时针方向（该右转）。</summary>
    public static double AngleDelta(double from, double to)
    {
        double d = (to - from) % 360.0;
        if (d >= 180.0) d -= 360.0;
        if (d < -180.0) d += 360.0;
        return d;
    }

    private static double Norm(double deg) => ((deg % 360.0) + 360.0) % 360.0;

    /// <summary>航向误差 → 本周期鼠标 dx（px）。死区 6° 内不动（防抖）；增益/抖动随机（拟人铁律）；
    /// 单周期钳制 ±260px（大误差由调用方分多次转向）。</summary>
    public static int TurnDx(double errDeg, double pxPer90Deg, Random rng)
    {
        if (Math.Abs(errDeg) < 6.0) return 0;
        double gain = 0.35 + rng.NextDouble() * 0.20;                     // 0.35-0.55
        int jitter = rng.Next(-15, 16);
        int dx = (int)Math.Round(errDeg / 90.0 * pxPer90Deg * gain) + jitter;
        return Math.Clamp(dx, -260, 260);
    }
}

/// <summary>"倒车请注意"脱困策略（用户 2026-10-25 提案）：撞墙先按 S 退出接触再转向——
/// 比原地转向好在①脱离障碍后转向弧不再二次碰撞；②后退改变视角，给定位和航迹留出恢复窗口。
/// 边偏置滞回：记住上次绕行方向（左/右），成功就沿用，失败就翻转——贴着障碍同侧绕行
/// 才是"虫子走路"能绕出简单凹地形的关键（左右随机横跳会原地打转）。
/// 连续 4 次脱困失败 → 放弃交人（不无限挣扎）。</summary>
public sealed class RecoveryPolicy
{
    private int _sideBias;               // 0=未定，+1=右，-1=左
    private int _consecutiveFails;

    public int ConsecutiveFails => _consecutiveFails;
    public bool ShouldGiveUp => _consecutiveFails >= GiveUpAfter;
    public int SideBias => _sideBias;

    /// <summary>连续失败多少次后放弃（交人工）。</summary>
    public int GiveUpAfter { get; init; } = 4;

    /// <summary>生成一次脱困动作：后退 400-800ms + 转向 60-120°（方向=边偏置）。</summary>
    public (int BackMs, int TurnDeg) Plan(Random rng)
    {
        if (_sideBias == 0)
            _sideBias = rng.Next(2) == 0 ? -1 : 1;
        int turn = _sideBias * (60 + rng.Next(0, 61));       // ±60-120°
        int back = 400 + rng.Next(0, 401);                   // 400-800ms
        return (back, turn);
    }

    /// <summary>脱困结果回报：成功清零计数并沿用当前边；失败计数+1 并翻转偏置（换边重试）。</summary>
    public void ReportOutcome(bool escaped)
    {
        if (escaped)
        {
            _consecutiveFails = 0;
            return;
        }
        _consecutiveFails++;
        _sideBias = -_sideBias;
    }
}

/// <summary>一个导航周期的决策（纯数据）：转向 / 前进 / 探索步 / 脱困 / 终态。</summary>
public enum WalkPhase { Turn, Walk, Explore, Recover, Arrived, GiveUp }

public sealed record WalkPlan(WalkPhase Phase, int TurnDx, bool Sprint, string Note)
{
    public static WalkPlan Arrived => new(WalkPhase.Arrived, 0, false, "到达目标点半径内");
    public static WalkPlan GiveUp => new(WalkPhase.GiveUp, 0, false, "连续脱困失败——交人工");
}

/// <summary>周期决策器：输入感知（航迹/距离/卡死），输出本周期动作。状态只有 RecoveryPolicy。
/// 裁判是物理航迹（LocalizerTrack 位移方向），可通行性模型只观察不参与（红线：enforce=false）。</summary>
public static class WalkNavigator
{
    /// <summary>航向死区（度）：航迹方向与目标方位差在此区内 → 前进，否则先转向。</summary>
    public const double CourseDeadzoneDeg = 12.0;

    /// <summary>冲刺距离（底图像素）：比这远就冲刺，近了改普通走（防冲过头）。</summary>
    public const double SprintDistPx = 2000.0;

    /// <summary>建立航迹的最小位移（px，相对航迹锚点而非相邻两帧）：零号大坝整图 4096px，
    /// 走一步只挪 ~2px，相邻帧比较永远凑不齐门槛（首跑实测踩过）——锚点基线才是比例尺适配的比法。</summary>
    public const double MinTrackDispPx = 8.0;

    /// <summary>贴墙判据（进度版）：连续走 WallStuckCycles 个周期、地图位移仍 &lt; 此值 = 抵墙。
    /// 帧差判据在"抵墙走"时会失明（视角晃动给 6-8 差分）——位移不撒谎。</summary>
    public const double WallStuckDispPx = 15.0;

    /// <summary>贴墙判据需要的连续前进周期数。</summary>
    public const int WallStuckCycles = 4;

    /// <summary>进度版贴墙判定：连续走了 n 个周期但地图位移不足 → 抵墙。</summary>
    public static bool IsWallStuck(int walkCycles, double dispPx)
        => walkCycles >= WallStuckCycles && dispPx < WallStuckDispPx;

    public static WalkPlan Decide(
        double? courseDeg, double goalBearingDeg, double distPx, double arrivePx,
        bool stuck, RecoveryPolicy recovery, Random rng)
    {
        if (distPx <= arrivePx) return WalkPlan.Arrived;
        if (recovery.ShouldGiveUp) return WalkPlan.GiveUp;
        if (stuck)
        {
            var (backMs, turnDeg) = recovery.Plan(rng);
            return new WalkPlan(WalkPhase.Recover, turnDeg, false,
                $"倒车请注意：S {backMs}ms + 转 {turnDeg}°（边偏置 {recovery.SideBias:+0;-0}）");
        }
        if (courseDeg is null)
            return new WalkPlan(WalkPhase.Explore, 0, false, "无航迹：向前探索建立位移（0.6s 不冲刺）");

        double err = NavMath.AngleDelta(courseDeg.Value, goalBearingDeg);
        if (Math.Abs(err) <= CourseDeadzoneDeg)
            return new WalkPlan(WalkPhase.Walk, 0, distPx > SprintDistPx, $"航迹对准目标（偏差 {err:F0}°），前进");
        int dx = NavMath.TurnDx(err, 4500, rng);
        return new WalkPlan(WalkPhase.Turn, dx, false, $"航向误差 {err:F0}°，转向修正");
    }
}
