using DeltaUnlimited.Vision.Navigation;
using Xunit;

namespace DU.Tests;

/// <summary>自动跑图 v1 纯导航库（2026-10-25）：方位数学、航向伺服、倒车请注意策略、周期决策。
/// 方位约定：0°=北（底图上方），顺时针；+dx = 右转 = 方位角增大。</summary>
public class WalkNavigatorTests
{
    // ---------- NavMath ----------

    [Fact]
    public void Bearing_CardinalDirections()
    {
        // 从原点出发：正右方=东(90)，正上方=北(0)，正左=西(270)，正下=南(180)
        Assert.Equal(90.0, NavMath.BearingDeg(0, 0, 100, 0), 3);
        Assert.Equal(0.0, NavMath.BearingDeg(0, 0, 0, -100), 3);
        Assert.Equal(270.0, NavMath.BearingDeg(0, 0, -100, 0), 3);
        Assert.Equal(180.0, NavMath.BearingDeg(0, 0, 0, 100), 3);
        // 东北 45°
        Assert.Equal(45.0, NavMath.BearingDeg(0, 0, 100, -100), 3);
    }

    [Fact]
    public void AngleDelta_NormalizesAndSigns()
    {
        Assert.Equal(90.0, NavMath.AngleDelta(0, 90), 3);        // 目标在东，该右转
        Assert.Equal(-90.0, NavMath.AngleDelta(90, 0), 3);       // 该左转
        Assert.Equal(180.0, Math.Abs(NavMath.AngleDelta(0, 180)), 3);   // 正后方 ±180
        Assert.Equal(10.0, NavMath.AngleDelta(350, 360), 3);     // 跨 0 归一
        Assert.Equal(-20.0, NavMath.AngleDelta(10, 350), 3);     // 跨 0 反向
    }

    [Fact]
    public void TurnDx_DeadzoneZero_Randomized_Clamped()
    {
        var rng = new Random(7);
        Assert.Equal(0, NavMath.TurnDx(3.0, 4500, rng));         // 死区内不动
        Assert.Equal(0, NavMath.TurnDx(-5.9, 4500, rng));

        int right = 0, left = 0;
        for (int i = 0; i < 50; i++)
        {
            int dx = NavMath.TurnDx(90, 4500, rng);              // 90° 误差 → 平均 ~2250px
            if (dx > 0) right++; else left++;
            Assert.InRange(dx, 0, 260);
        }
        Assert.True(right > 40, "右转误差几乎全应给正 dx");
        for (int i = 0; i < 50; i++)
        {
            int dx = NavMath.TurnDx(-179, 4500, rng);
            Assert.InRange(dx, -260, 0);
        }
    }

    // ---------- RecoveryPolicy（倒车请注意） ----------

    [Fact]
    public void Recovery_FirstSideRandom_ThenSticksOnSuccess()
    {
        var rec = new RecoveryPolicy();
        var (b1, t1) = rec.Plan(new Random(1));
        Assert.InRange(b1, 400, 800);
        Assert.True(Math.Abs(t1) is >= 60 and <= 120);
        int side = Math.Sign(t1);

        rec.ReportOutcome(true);      // 成功沿用同边
        var (_, t2) = rec.Plan(new Random(2));
        Assert.Equal(side, Math.Sign(t2));
        Assert.False(rec.ShouldGiveUp);
    }

    [Fact]
    public void Recovery_FailureFlipsSide_AndGivesUpAfterFour()
    {
        var rec = new RecoveryPolicy();
        var (_, t1) = rec.Plan(new Random(3));
        int side = Math.Sign(t1);

        rec.ReportOutcome(false);
        Assert.Equal(1, rec.ConsecutiveFails);
        var (_, t2) = rec.Plan(new Random(4));
        Assert.Equal(-side, Math.Sign(t2));     // 失败翻边

        rec.ReportOutcome(false);
        rec.ReportOutcome(false);
        rec.ReportOutcome(false);               // 第 4 次 → 放弃
        Assert.True(rec.ShouldGiveUp);

        rec.ReportOutcome(true);                // 成功复活（清零）
        Assert.False(rec.ShouldGiveUp);
    }

    // ---------- WalkNavigator.Decide ----------

    [Fact]
    public void Decide_Arrived_WhenWithinRadius()
    {
        var rec = new RecoveryPolicy();
        var plan = WalkNavigator.Decide(0, 0, 100, 250, stuck: false, recovery: rec, rng: new Random(1));
        Assert.Equal(WalkPhase.Arrived, plan.Phase);
    }

    [Fact]
    public void Decide_Stuck_GoesRecover_EvenWithoutCourse()
    {
        var rec = new RecoveryPolicy();
        var plan = WalkNavigator.Decide(null, 90, 5000, 250, stuck: true, recovery: rec, rng: new Random(1));
        Assert.Equal(WalkPhase.Recover, plan.Phase);
        Assert.Contains("倒车", plan.Note);
    }

    [Fact]
    public void Decide_GiveUp_AfterPolicyExhausted()
    {
        var rec = new RecoveryPolicy();
        for (int i = 0; i < 4; i++) rec.ReportOutcome(false);
        var plan = WalkNavigator.Decide(0, 90, 5000, 250, stuck: false, recovery: rec, rng: new Random(1));
        Assert.Equal(WalkPhase.GiveUp, plan.Phase);
    }

    [Fact]
    public void Decide_NoCourse_ExploresForward()
    {
        var rec = new RecoveryPolicy();
        var plan = WalkNavigator.Decide(null, 90, 5000, 250, stuck: false, recovery: rec, rng: new Random(1));
        Assert.Equal(WalkPhase.Explore, plan.Phase);
    }

    [Fact]
    public void Decide_Aligned_Walks_SprintsWhenFar()
    {
        var rec = new RecoveryPolicy();
        var near = WalkNavigator.Decide(88, 90, 800, 250, false, rec, new Random(1));    // 2° 偏差近处
        Assert.Equal(WalkPhase.Walk, near.Phase);
        Assert.False(near.Sprint);

        var far = WalkNavigator.Decide(88, 90, 3000, 250, false, rec, new Random(1));    // 远处冲刺
        Assert.Equal(WalkPhase.Walk, far.Phase);
        Assert.True(far.Sprint);
    }

    [Fact]
    public void Decide_OffCourse_TurnsTowardGoal()
    {
        var rec = new RecoveryPolicy();
        // 航迹向北(0)，目标在东(90)：误差 +90 → 右转正 dx
        var plan = WalkNavigator.Decide(0, 90, 5000, 250, false, rec, new Random(1));
        Assert.Equal(WalkPhase.Turn, plan.Phase);
        Assert.InRange(plan.TurnDx, 1, 260);

        // 航迹向东(90)，目标在北(0)：误差 -90 → 左转负 dx
        var plan2 = WalkNavigator.Decide(90, 0, 5000, 250, false, rec, new Random(1));
        Assert.Equal(WalkPhase.Turn, plan2.Phase);
        Assert.InRange(plan2.TurnDx, -260, -1);
    }
}
