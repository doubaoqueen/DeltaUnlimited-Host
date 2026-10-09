using DeltaUnlimited.Vision;
using Xunit;

namespace DU.Tests;

/// <summary>识别变更门（纯逻辑）：没变化就跳过整轮 Detect，但三种兜底必须保证不漏界面切换。</summary>
public class ChangeGateTests
{
    private const long Ms = TimeSpan.TicksPerMillisecond;

    [Fact]
    public void FirstCall_AlwaysDetects()
    {
        var gate = new ChangeGate();
        Assert.True(gate.ShouldDetect(frameDiff: 0, nowTicks: 0));
    }

    [Fact]
    public void NoChange_Skips_ThenForcedByCount()
    {
        var gate = new ChangeGate(diffThreshold: 1.2, forceEveryN: 5, maxStaleMs: 100_000);
        Assert.True(gate.ShouldDetect(0, 0));                       // 首轮识别
        for (int i = 0; i < 5; i++)
            Assert.False(gate.ShouldDetect(0.1, (i + 1) * 100 * Ms)); // 画面无变化 → 连跳 5 次
        Assert.True(gate.ShouldDetect(0.1, 600 * Ms));              // 第 6 次按轮数兜底强制识别
        Assert.Equal(5, gate.Skipped);
    }

    [Fact]
    public void ChangedFrame_DetectsImmediately()
    {
        var gate = new ChangeGate(diffThreshold: 1.2, forceEveryN: 100, maxStaleMs: 100_000);
        Assert.True(gate.ShouldDetect(0, 0));
        Assert.True(gate.ShouldDetect(5.0, 100 * Ms));              // 帧差 ≥ 阈值（界面切换量级）
        Assert.Equal(0, gate.Skipped);
    }

    [Fact]
    public void Stale_ForcedByAge()
    {
        var gate = new ChangeGate(diffThreshold: 1.2, forceEveryN: 100, maxStaleMs: 2000);
        Assert.True(gate.ShouldDetect(0, 0));
        Assert.False(gate.ShouldDetect(0, 1000 * Ms));              // 1s：还不到兜底时限
        Assert.True(gate.ShouldDetect(0, 2100 * Ms));               // 2.1s：超时强制识别
    }

    [Fact]
    public void ThresholdBoundary_TreatedAsChanged()
    {
        var gate = new ChangeGate(diffThreshold: 1.2, forceEveryN: 100, maxStaleMs: 100_000);
        Assert.True(gate.ShouldDetect(0, 0));
        Assert.True(gate.ShouldDetect(1.2, 50 * Ms));               // 等于阈值算"有变化"（保守）
    }
}
