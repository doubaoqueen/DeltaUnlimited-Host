using DeltaUnlimited.Vision.Signals;
using OpenCvSharp;
using Xunit;

namespace DU.Tests;

/// <summary>受击红屏提取器/监视器（债3 2026-10）：数学性质用合成图钉死（全红≈1、全绿≈0、半红≈0.5），
/// 防公式漂移导致与 K0 离线标定（704 帧）失配。</summary>
public class RedFlashDetectorTests
{
    private static Mat Solid(int w, int h, Vec3b px)
    {
        var m = new Mat(h, w, MatType.CV_8UC3, Scalar.All(0));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                m.At<Vec3b>(y, x) = px;
        return m;
    }

    [Fact]
    public void AllRed_EdgeRatioNearOne()
    {
        using var m = Solid(1920, 1080, new Vec3b(20, 20, 200));   // BGR：R=200 主导
        Assert.True(RedFlashDetector.EdgeRedRatio(m) > 0.99);
    }

    [Fact]
    public void AllGreen_EdgeRatioZero()
    {
        using var m = Solid(1920, 1080, new Vec3b(20, 200, 20));
        Assert.Equal(0.0, RedFlashDetector.EdgeRedRatio(m), 5);
    }

    [Fact]
    public void DimRed_BelowFloorNotCounted()
    {
        // R<90 的暗红不算（夜战场景的深红环境光不该误报）
        using var m = Solid(1920, 1080, new Vec3b(10, 10, 85));
        Assert.Equal(0.0, RedFlashDetector.EdgeRedRatio(m), 5);
    }

    [Fact]
    public void HitFlashMonitor_ConfirmFramesGate()
    {
        // 防抖：单帧红屏不触发（RATIO 检测器的确认窗口在监视器里同样生效）
        using var red = Solid(1920, 1080, new Vec3b(20, 20, 200));
        var baseSpec = DefaultSignals.HitFlash();
        var spec = new SignalSpec
        {
            Name = baseSpec.Name, Region = baseSpec.Region,
            ThresholdMin = baseSpec.ThresholdMin, ThresholdMax = baseSpec.ThresholdMax,
            ConfirmFrames = 3, Fallback = baseSpec.Fallback, Source = baseSpec.Source,
        };
        var mon = new HitFlashMonitor(spec, new Random(1));
        Assert.False(mon.Update(red));   // 1/3
        Assert.False(mon.Update(red));   // 2/3
        Assert.True(mon.Update(red));    // 3/3 → 触发
    }
}
