using DeltaUnlimited.Vision.Ai;
using Xunit;

namespace DU.Tests;

/// <summary>可通行性滤波与漂移监测（纯逻辑，无 IO/GPU）。</summary>
public class PassabilityFilterTests
{
    private static PassabilityResult Res(PassabilityClass c, double conf = 0.9)
        => new(c, new float[3], conf, 1.0);

    private static PassabilityFilter Make(int window = 5, int k = 3)
        => new(window, k, motionDiffThreshold: 12.0, minConfidence: 0.70);

    [Fact]
    public void 窗口内不足k帧blocked_不升级()
    {
        var f = Make();
        Assert.Equal(PathWarningLevel.None, f.Push(Res(PassabilityClass.Blocked), 5).Level);
        Assert.Equal(PathWarningLevel.None, f.Push(Res(PassabilityClass.Blocked), 5).Level);
        Assert.Equal(PathWarningLevel.Blocked, f.Push(Res(PassabilityClass.Blocked), 5).Level); // 第 3 帧
    }

    [Fact]
    public void 窗口滑动后警告解除()
    {
        var f = Make(window: 5, k: 3);
        f.Push(Res(PassabilityClass.Blocked), 5);
        f.Push(Res(PassabilityClass.Blocked), 5);
        f.Push(Res(PassabilityClass.Blocked), 5);
        Assert.Equal(PathWarningLevel.Blocked, f.Current);
        f.Push(Res(PassabilityClass.Passable), 5);  // 滑出最旧 1 帧 blocked
        f.Push(Res(PassabilityClass.Passable), 5);  // 再滑出 1 帧 → 窗口内 blocked 只剩 1
        Assert.Equal(PathWarningLevel.None, f.Push(Res(PassabilityClass.Passable), 5).Level);
    }

    [Fact]
    public void 弃权帧NoGround_不推进窗口()
    {
        var f = Make();
        f.Push(Res(PassabilityClass.Blocked), 5);
        f.Push(Res(PassabilityClass.Blocked), 5);
        f.Push(Res(PassabilityClass.NoGround), 5);  // 天空/遮挡：不计数
        Assert.Equal(PathWarningLevel.None, f.Current);
        Assert.Equal(PathWarningLevel.Blocked, f.Push(Res(PassabilityClass.Blocked), 5).Level);
    }

    [Fact]
    public void 高帧差_移动物体帧被压制()
    {
        var f = Make();
        f.Push(Res(PassabilityClass.Blocked), 5);
        f.Push(Res(PassabilityClass.Blocked), 5);
        Assert.Equal(PathWarningLevel.None, f.Push(Res(PassabilityClass.Blocked), frameDiff: 20).Level); // 敌人穿过
        Assert.Equal(PathWarningLevel.Blocked, f.Push(Res(PassabilityClass.Blocked), 5).Level);
    }

    [Fact]
    public void 低置信帧按弃权处理()
    {
        var f = Make();
        f.Push(Res(PassabilityClass.Blocked), 5);
        f.Push(Res(PassabilityClass.Blocked), 5);
        Assert.Equal(PathWarningLevel.None, f.Push(Res(PassabilityClass.Blocked, conf: 0.5), 5).Level);
    }

    [Fact]
    public void Reset_清空窗口与当前级别()
    {
        var f = Make();
        f.Push(Res(PassabilityClass.Blocked), 5);
        f.Push(Res(PassabilityClass.Blocked), 5);
        f.Push(Res(PassabilityClass.Blocked), 5);
        f.Reset();
        Assert.Equal(PathWarningLevel.None, f.Current);
        Assert.Equal(PathWarningLevel.None, f.Push(Res(PassabilityClass.Passable), 5).Level);
    }
}

public class DriftMonitorTests
{
    private static DateTimeOffset _t = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 矛盾率超阈值且样本足够_应禁用()
    {
        var m = new DriftMonitor(rateLimit: 0.25, window: TimeSpan.FromMinutes(10), minSamples: 20, clock: () => _t);
        for (int i = 0; i < 10; i++) m.Record(contradiction: true);
        for (int i = 0; i < 10; i++) m.Record(contradiction: false);
        Assert.Equal(0.5, m.ContradictionRate, 3);
        Assert.True(m.ShouldDisable);
    }

    [Fact]
    public void 样本不足_不触发禁用()
    {
        var m = new DriftMonitor(0.25, TimeSpan.FromMinutes(10), minSamples: 20, clock: () => _t);
        for (int i = 0; i < 5; i++) m.Record(contradiction: true);
        Assert.False(m.ShouldDisable); // 矛盾率 100% 但样本只有 5
    }

    [Fact]
    public void 窗口滑出后旧事件不再计数()
    {
        var m = new DriftMonitor(0.25, TimeSpan.FromMinutes(10), minSamples: 2, clock: () => _t);
        m.Record(contradiction: true);
        _t += TimeSpan.FromMinutes(11); // 超出 10min 窗口
        Assert.Equal(0, m.Count);
        Assert.False(m.ShouldDisable);
    }

    [Fact]
    public void 无矛盾_不禁用()
    {
        var m = new DriftMonitor(0.25, TimeSpan.FromMinutes(10), minSamples: 2, clock: () => _t);
        for (int i = 0; i < 50; i++) m.Record(contradiction: false);
        Assert.False(m.ShouldDisable);
    }
}
