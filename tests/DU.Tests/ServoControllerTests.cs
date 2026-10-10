using DeltaUnlimited.Vision.Servo;
using Xunit;

namespace DU.Tests;

/// <summary>伺服控制与循环骨架（纯逻辑，假时钟/假感知，无需游戏）。</summary>
public class ServoControllerTests
{
    private static ServoConfig Cfg() => new()
    {
        TurnGainMin = 0.5, TurnGainMax = 0.5,   // 固定增益便于断言（随机范围在生产里必须保留）
        DeadzonePx = 40,
        MaxTurnPerCycle = 200,
        TurnJitterPx = 0,
        ArriveMeters = 12,
        SprintUntilMeters = 60,
        SearchForwardRatio = 1.0,
    };

    [Fact]
    public void Deadzone_NoTurn()
    {
        var a = ServoController.Decide(new ServoFrame(true, 30, 100, 10, false), Cfg(), new Random(1));
        Assert.Equal(0, a.TurnDx);
        Assert.True(a.HoldForward);
    }

    [Fact]
    public void OffsetBeyondDeadzone_TurnsTowardMarker_AndClamps()
    {
        var a = ServoController.Decide(new ServoFrame(true, 500, 100, 10, false), Cfg(), new Random(1));
        Assert.Equal(200, a.TurnDx);                 // 500*0.5=250 → 钳到 200
        var b = ServoController.Decide(new ServoFrame(true, -100, 100, 10, false), Cfg(), new Random(1));
        Assert.Equal(-50, b.TurnDx);                 // -100*0.5=-50，反向
    }

    [Fact]
    public void MarkerInvisible_SearchForward_NoTurn()
    {
        var a = ServoController.Decide(new ServoFrame(false, 0, -1, 10, false), Cfg(), new Random(7));
        Assert.Equal(0, a.TurnDx);                   // 不瞎转
        Assert.True(a.HoldForward);                  // 搜索式前进（比例 1.0）
        Assert.False(a.Sprint);
    }

    [Fact]
    public void SprintOnlyWhenFarAway()
    {
        var far = ServoController.Decide(new ServoFrame(true, 0, 200, 10, false), Cfg(), new Random(1));
        Assert.True(far.Sprint);
        var near = ServoController.Decide(new ServoFrame(true, 0, 30, 10, false), Cfg(), new Random(1));
        Assert.False(near.Sprint);                   // 近了不冲，防冲过头
    }

    [Fact]
    public void BlockedWarning_ReleasesForwardKey()
    {
        var a = ServoController.Decide(new ServoFrame(true, 0, 200, 10, HitBlocked: true), Cfg(), new Random(1));
        Assert.False(a.HoldForward);
        Assert.False(a.Sprint);
        Assert.Contains("预警", a.Note);
    }

    [Fact]
    public void TurnJitter_WithinConfiguredRange_AndNeverZeroWhenBeyondDeadzone()
    {
        var cfg = Cfg();
        cfg.TurnJitterPx = 12;
        var rng = new Random(42);
        int expectedBase = (int)Math.Round(41 * 0.5); // 与实现同口径：20.5 → 20（.NET 银行家舍入）
        for (int i = 0; i < 50; i++)
        {
            int turn = ServoController.Decide(new ServoFrame(true, 41, 100, 10, false), cfg, rng).TurnDx;
            Assert.InRange(turn, expectedBase - 12, expectedBase + 12);
            Assert.NotEqual(0, turn);
        }
    }

    [Fact]
    public void IsArrived_OnlyWhenDistanceReadable()
    {
        Assert.True(ServoController.IsArrived(new ServoFrame(true, 0, 10, 10, false), Cfg()));
        Assert.False(ServoController.IsArrived(new ServoFrame(true, 0, 20, 10, false), Cfg()));
        Assert.False(ServoController.IsArrived(new ServoFrame(true, 0, -1, 10, false), Cfg())); // 距离未知不算到达
    }

    [Fact]
    public void IsStuck_RequiresConsecutiveLowDiffs()
    {
        Assert.True(ServoController.IsStuck(new[] { 20.0, 1.0, 1.0, 1.0 }, 4.0, 3));
        Assert.False(ServoController.IsStuck(new[] { 1.0, 1.0, 9.0, 1.0 }, 4.0, 3));
        Assert.False(ServoController.IsStuck(new[] { 1.0 }, 4.0, 3));
    }

    // ===== ServoLoop 编排（假时钟/假感知/假动作）=====

    private sealed class FakeClock
    {
        public double Now;
        public double Advance(double ms) => Now += ms;
    }

    private static ServoLoop.Options FastOpts() => new()
    {
        CycleHz = 1000,      // 1ms/周期，测试里几乎不睡
        TimeoutMs = 10_000,
        MaxCycles = 50,
        StuckNeedLow = 3,
        StuckDiffThreshold = 4.0,
        BlockedGiveUpCycles = 3,
    };

    [Fact]
    public void Loop_StopsImmediately_OnEmergencyStop()
    {
        var r = ServoLoop.Run(
            perceive: () => ServoFrame.None,
            act: _ => { },
            cfg: Cfg(),
            opt: FastOpts(),
            rng: new Random(1),
            clock: () => 0,
            sleep: _ => { },
            stopped: () => true);

        Assert.Equal(ServoOutcome.Stopped, r.Outcome);
    }

    [Fact]
    public void Loop_Succeeds_WhenArrived()
    {
        var moves = new List<ServoAction>();
        var r = ServoLoop.Run(
            perceive: () => new ServoFrame(true, 0, 8, 20, false),   // 8m ≤ 12m 到达
            act: moves.Add,
            cfg: Cfg(),
            opt: FastOpts(),
            rng: new Random(1),
            clock: () => 0,
            sleep: _ => { },
            stopped: () => false);

        Assert.Equal(ServoOutcome.Success, r.Outcome);
        Assert.Contains(moves, m => m == ServoAction.Idle); // 到达时松手
    }

    [Fact]
    public void Loop_ReportsStuck_AfterConsecutiveLowDiffs()
    {
        var r = ServoLoop.Run(
            perceive: () => new ServoFrame(true, 100, 200, 0.5, false), // 画面几乎不变
            act: _ => { },
            cfg: Cfg(),
            opt: FastOpts(),
            rng: new Random(1),
            clock: () => 0,
            sleep: _ => { },
            stopped: () => false);

        Assert.Equal(ServoOutcome.Stuck, r.Outcome);
    }

    [Fact]
    public void Loop_GivesUp_WhenBlockedPersists()
    {
        var r = ServoLoop.Run(
            perceive: () => new ServoFrame(true, 0, 200, 20, HitBlocked: true),
            act: _ => { },
            cfg: Cfg(),
            opt: FastOpts(),
            rng: new Random(1),
            clock: () => 0,
            sleep: _ => { },
            stopped: () => false);

        Assert.Equal(ServoOutcome.Danger, r.Outcome);
    }

    [Fact]
    public void Loop_TimesOut_ByClock()
    {
        var clk = new FakeClock();
        var r = ServoLoop.Run(
            perceive: () => new ServoFrame(true, 0, 200, 20, false),
            act: _ => { },
            cfg: Cfg(),
            opt: FastOpts() with { TimeoutMs = 5 },
            rng: new Random(1),
            clock: () => clk.Advance(2),   // 每查一次前进 2ms
            sleep: _ => { },
            stopped: () => false);

        Assert.Equal(ServoOutcome.Timeout, r.Outcome);
    }

    [Fact]
    public void Loop_ReturnsError_WhenPerceiveThrows()
    {
        var r = ServoLoop.Run(
            perceive: () => throw new InvalidOperationException("截图失败"),
            act: _ => { },
            cfg: Cfg(),
            opt: FastOpts(),
            rng: new Random(1),
            clock: () => 0,
            sleep: _ => { },
            stopped: () => false);

        Assert.Equal(ServoOutcome.Error, r.Outcome);
        Assert.Contains("截图失败", r.Detail);
    }

    [Fact]
    public void Loop_OnlyEmitsMovementActions()
    {
        // 红线回归：伺服动作类型里根本没有点击字段——用反射钉住，防以后有人加"点击"进去
        var props = typeof(ServoAction).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Click", props);
        Assert.DoesNotContain("ClickX", props);
        Assert.DoesNotContain("ClickY", props);
        Assert.Contains("TurnDx", props);
        Assert.Contains("HoldForward", props);
    }
}
