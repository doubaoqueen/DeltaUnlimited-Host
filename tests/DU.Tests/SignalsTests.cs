using DeltaUnlimited.Vision.Signals;
using Xunit;

namespace DU.Tests;

/// <summary>廉价档感知信号纯逻辑（提案 §2）：阈值随机范围 + 连续 N 帧确认窗口。</summary>
public class SignalsTests
{
    private static SignalSpec Spec(double min, double max, int confirm = 3) => new()
    {
        Name = "test", ThresholdMin = min, ThresholdMax = max, ConfirmFrames = confirm,
    };

    [Fact]
    public void Threshold_AlwaysWithinRandomRange()
    {
        var spec = Spec(0.20, 0.30);
        for (int seed = 0; seed < 30; seed++)
        {
            var d = new RatioDetector(spec, new Random(seed));
            Assert.InRange(d.CurrentThreshold, 0.20, 0.30); // 铁律：取样永远在范围内
        }
    }

    [Fact]
    public void Trigger_RequiresConsecutiveConfirmFrames()
    {
        var d = new RatioDetector(Spec(0.2, 0.2, confirm: 3), new Random(1));
        Assert.False(d.Update(0.25));   // 1/3
        Assert.False(d.Update(0.25));   // 2/3
        Assert.True(d.Update(0.25));    // 3/3 → 触发
    }

    [Fact]
    public void SingleSpike_DoesNotTrigger()
    {
        var d = new RatioDetector(Spec(0.2, 0.2, confirm: 3), new Random(1));
        Assert.False(d.Update(0.99));   // 单帧尖峰
        Assert.False(d.Update(0.01));   // 断掉 → 计数清零
        Assert.False(d.Update(0.25));
        Assert.False(d.Update(0.25));   // 才 2/3，仍不触发
    }

    [Fact]
    public void ThresholdResamples_AfterTrigger()
    {
        var spec = Spec(0.1, 0.3, confirm: 1);
        var rng = new Random(42);
        var d = new RatioDetector(spec, rng);
        var seen = new HashSet<double>();
        for (int i = 0; i < 12; i++)
        {
            Assert.True(d.Update(0.99)); // 每帧都超阈值 → 每次触发后重取样
            seen.Add(d.CurrentThreshold);
        }
        Assert.True(seen.Count > 1, "触发后阈值必须重新取样（随机范围铁律 + 防同阈值反复误触）");
    }

    [Fact]
    public void LootGlow_TriggersOnEitherChannel()
    {
        var gold = new RatioDetector(Spec(0.10, 0.10, confirm: 2), new Random(1));
        var purple = new RatioDetector(Spec(0.05, 0.05, confirm: 2), new Random(1));
        var glow = new LootGlow(gold, purple);

        Assert.False(glow.Update(0.0, 0.0));
        Assert.False(glow.Update(0.0, 0.20));   // 紫通道 1/2
        Assert.True(glow.Update(0.0, 0.20));    // 紫通道 2/2 → 触发（金通道全程安静）
    }

    [Fact]
    public void Reset_ClearsProgress_AndResamples()
    {
        var d = new RatioDetector(Spec(0.2, 0.2, confirm: 3), new Random(1));
        d.Update(0.99);
        d.Update(0.99);
        d.Reset();
        Assert.False(d.Update(0.99)); // 计数已清零，重新从 1/3 计
    }
}
