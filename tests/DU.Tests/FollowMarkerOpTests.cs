using DeltaUnlimited.Vision.Servo;
using Xunit;

namespace DU.Tests;

/// <summary>FollowMarkerOp 薄壳（提案 §1/§3）：三件套注入 + 四结局 + 松键兜底，无需游戏。</summary>
public class FollowMarkerOpTests
{
    private sealed class FakePerceiver : IServoPerceiver
    {
        private readonly ServoFrame _frame;
        public FakePerceiver(ServoFrame frame) => _frame = frame;
        public ServoFrame Observe() => _frame;             // 无限重复同一帧
    }

    private sealed class ThrowingPerceiver : IServoPerceiver
    {
        public ServoFrame Observe() => throw new InvalidOperationException("截图失败");
    }

    private sealed class FakeActor : IServoActor
    {
        public readonly List<ServoAction> Performed = new();
        public int ReleaseCount;
        public void Perform(ServoAction action) => Performed.Add(action);
        public void Release() => ReleaseCount++;
    }

    private static ServoConfig Cfg() => new()
    {
        TurnGainMin = 0.5, TurnGainMax = 0.5,
        DeadzonePx = 40, MaxTurnPerCycle = 200, TurnJitterPx = 0,
        ArriveMeters = 12, SprintUntilMeters = 60, SearchForwardRatio = 1.0,
    };

    private static ServoLoop.Options FastOpts() => new()
    {
        CycleHz = 1000, TimeoutMs = 10_000, MaxCycles = 50,
        StuckNeedLow = 3, StuckDiffThreshold = 4.0, BlockedGiveUpCycles = 3,
    };

    [Fact]
    public void Success_ReleasesKeys()
    {
        var actor = new FakeActor();
        var r = FollowMarkerOp.Run(
            new FakePerceiver(new ServoFrame(true, 0, 8, 20, false)),
            actor, Cfg(), FastOpts(), new Random(1), stopped: () => false);

        Assert.Equal(ServoOutcome.Success, r.Outcome);
        Assert.Equal(1, actor.ReleaseCount);                 // 成功也要松键
        Assert.Contains(actor.Performed, a => a == ServoAction.Idle);
    }

    /// <summary>前 N 帧可见、之后丢失的有状态假感知器（markerlost 用例）。</summary>
    private sealed class VanishingMarkerPerceiver : IServoPerceiver
    {
        private int _visibleLeft;
        public VanishingMarkerPerceiver(int visibleLeft) => _visibleLeft = visibleLeft;
        public ServoFrame Observe()
        {
            if (_visibleLeft > 0) { _visibleLeft--; return new ServoFrame(true, 0, -1, 20, false); }
            return new ServoFrame(false, 0, -1, 20, false);
        }
    }

    [Fact]
    public void ArrivalPrompt_ReachesSuccess_EvenWithUnknownDistance()
    {
        // 债2回归核心：此前 DistanceMeters 恒 -1 → Success 永远不可达；提示出现即到达
        var actor = new FakeActor();
        var r = FollowMarkerOp.Run(
            new FakePerceiver(new ServoFrame(true, 0, -1, 20, false, ArrivalPrompt: true)),
            actor, Cfg(), FastOpts(), new Random(1), stopped: () => false);

        Assert.Equal(ServoOutcome.Success, r.Outcome);
        Assert.Contains("交互提示", r.Detail);
        Assert.Equal(1, actor.ReleaseCount);
    }

    [Fact]
    public void MarkerLost_AfterSeen_ReleasesKeys()
    {
        // 标记见过后持续消失 → MarkerLost（疑似到点上方），不是无脑超时
        var actor = new FakeActor();
        var r = FollowMarkerOp.Run(
            new VanishingMarkerPerceiver(visibleLeft: 2),
            actor, Cfg(), FastOpts() with { MarkerLostGiveUpCycles = 5 }, new Random(1), stopped: () => false);

        Assert.Equal(ServoOutcome.MarkerLost, r.Outcome);
        Assert.Equal(1, actor.ReleaseCount);
    }

    [Fact]
    public void MarkerNeverSeen_DoesNotFireMarkerLost()
    {
        // 开局从未见过标记：那是感知问题（模板不对/没标点），交超时，不误报"疑似到达"
        var actor = new FakeActor();
        var r = FollowMarkerOp.Run(
            new FakePerceiver(new ServoFrame(false, 0, -1, 20, false)),
            actor, Cfg(), FastOpts() with { MarkerLostGiveUpCycles = 5, TimeoutMs = 5 }, new Random(1), stopped: () => false);

        Assert.Equal(ServoOutcome.Timeout, r.Outcome);
    }

    [Fact]
    public void HitFlash_ReturnsDanger_AndReleasesKeys()
    {
        // 债3：受击红屏信号（防抖已过）→ Danger 立即交人工，不自动逃跑
        var actor = new FakeActor();
        var r = FollowMarkerOp.Run(
            new FakePerceiver(new ServoFrame(true, 0, 200, 20, false, ArrivalPrompt: false, HitFlash: true)),
            actor, Cfg(), FastOpts(), new Random(1), stopped: () => false);

        Assert.Equal(ServoOutcome.Danger, r.Outcome);
        Assert.Contains("受击", r.Detail);
        Assert.Equal(1, actor.ReleaseCount);
    }

    [Fact]
    public void EmergencyStop_ReleasesKeys_AndReportsStopped()
    {
        var actor = new FakeActor();
        var r = FollowMarkerOp.Run(
            new FakePerceiver(new ServoFrame(true, 100, 200, 20, false)),
            actor, Cfg(), FastOpts(), new Random(1),
            stopped: () => true);

        Assert.Equal(ServoOutcome.Stopped, r.Outcome);
        Assert.Equal(1, actor.ReleaseCount);
        Assert.Empty(actor.Performed);                       // 急停时一个动作都不该发出
    }

    [Fact]
    public void PerceiveThrows_ReturnsError_AndStillReleases()
    {
        var actor = new FakeActor();
        var r = FollowMarkerOp.Run(
            new ThrowingPerceiver(),
            actor, Cfg(), FastOpts(), new Random(1), stopped: () => false);

        Assert.Equal(ServoOutcome.Error, r.Outcome);
        Assert.Equal(1, actor.ReleaseCount);                 // 异常路径也必须松键（提案 §3 输出兜底）
    }

    [Fact]
    public void Stuck_ReleasesKeys()
    {
        var actor = new FakeActor();
        var r = FollowMarkerOp.Run(
            new FakePerceiver(new ServoFrame(true, 100, 200, 0.5, false)),  // 画面持续低变化
            actor, Cfg(), FastOpts(), new Random(1), stopped: () => false);

        Assert.Equal(ServoOutcome.Stuck, r.Outcome);
        Assert.Equal(1, actor.ReleaseCount);
    }

    [Fact]
    public void Timeout_ReleasesKeys()
    {
        var actor = new FakeActor();
        var r = FollowMarkerOp.Run(
            new FakePerceiver(new ServoFrame(true, 0, 200, 20, false)),
            actor, Cfg(), FastOpts() with { TimeoutMs = 5 }, new Random(1), stopped: () => false);

        Assert.Equal(ServoOutcome.Timeout, r.Outcome);
        Assert.Equal(1, actor.ReleaseCount);
    }

    [Fact]
    public void Actor_OnlyReceivesMovementActions()
    {
        // 薄壳只把 ServoAction（转向/前进/冲刺）交给执行器——点击类动作在类型层面就不存在
        // （ServoAction 反射测试已在 ServoControllerTests.Loop_OnlyEmitsMovementActions 钉住）
        var actor = new FakeActor();
        FollowMarkerOp.Run(
            new FakePerceiver(new ServoFrame(true, 100, 200, 20, false)),
            actor, Cfg(), FastOpts(), new Random(1), stopped: () => false);

        foreach (var a in actor.Performed)
        {
            Assert.True(a.TurnDx is >= -200 and <= 200, $"转向越界: {a.TurnDx}");
            Assert.NotNull(a.Note);
        }
    }
}

