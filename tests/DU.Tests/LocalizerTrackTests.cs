using DeltaUnlimited.Vision.Localization;
using Xunit;

namespace DU.Tests;

/// <summary>定位轨迹过滤器：首锚双帧确认 / 速度门 / 重锚定。</summary>
public class LocalizerTrackTests
{
    private static LocalizeResult R(params (double s, int x, int y)[] cands)
    {
        var list = cands.Select(c => new LocalizeCandidate(c.s, 210, c.x, c.y)).ToArray();
        return new LocalizeResult(true, list[0], list);
    }

    [Fact]
    public void FirstFix_RequiresTwoConsistentFrames()
    {
        var t = new LocalizerTrack();
        var r1 = t.Accept(R((0.5, 1000, 1000)), 0);
        Assert.False(r1.Accepted);              // 首帧只挂起
        Assert.Null(r1.Position);

        var r2 = t.Accept(R((0.6, 1030, 1010)), 2000);
        Assert.True(r2.Accepted);               // 两帧一致 → 锁定
        Assert.Equal(1030, r2.Position!.CenterX);
    }

    [Fact]
    public void FirstFix_InconsistentCandidates_DoNotLock()
    {
        var t = new LocalizerTrack();
        t.Accept(R((0.5, 1000, 1000)), 0);
        var r = t.Accept(R((0.6, 1600, 1600)), 2000);   // 相距 848px → 不一致
        Assert.False(r.Accepted);
        Assert.True(r.Held == false);                    // 尚未有锚点，不算"沿用"
    }

    [Fact]
    public void SmoothMovement_Accepted()
    {
        var t = new LocalizerTrack();
        t.Accept(R((0.8, 1000, 1000)), 0);
        t.Accept(R((0.8, 1000, 1000)), 1000);
        var r = t.Accept(R((0.8, 1030, 1000)), 3000);    // 30px / 2s = 15px/s
        Assert.True(r.Accepted);
        Assert.Equal(1030, r.Position!.CenterX);
    }

    [Fact]
    public void Teleport_Rejected_AndHoldsLastPosition()
    {
        var t = new LocalizerTrack();
        t.Accept(R((0.8, 1000, 1000)), 0);
        t.Accept(R((0.8, 1000, 1000)), 1000);
        var r = t.Accept(R((0.85, 1101, 2519)), 3000);   // 2 秒瞬移 1394px
        Assert.False(r.Accepted);
        Assert.True(r.Held);
        Assert.Equal(1000, r.Position!.CenterX);
        Assert.Contains("超速", r.Note);
    }

    [Fact]
    public void JumpThenBackOnTrack_ResumesAccepting()
    {
        var t = new LocalizerTrack();
        t.Accept(R((0.8, 1000, 1000)), 0);
        t.Accept(R((0.8, 1000, 1000)), 1000);
        t.Accept(R((0.9, 1101, 2519)), 3000);            // 跳变被拒
        var r = t.Accept(R((0.7, 1010, 1000)), 5000);    // 回到轨迹
        Assert.True(r.Accepted);
        Assert.Equal(1010, r.Position!.CenterX);
    }

    [Fact]
    public void SustainedConsistentNewPosition_Reanchors()
    {
        // 场景：初始锚点错误（首次两帧都错到同一处），随后连续多帧一致指向真位置
        var t = new LocalizerTrack();
        t.Accept(R((0.5, 1729, 2659)), 0);
        t.Accept(R((0.5, 1729, 2659)), 2000);            // 错误锚点锁定

        // 之后每帧候选都在 (1500,1300) 附近（相互一致，但与锚点相距 1380px → 逐帧被拒）
        var r1 = t.Accept(R((0.62, 1525, 1283)), 4000);
        var r2 = t.Accept(R((0.64, 1523, 1293)), 6000);
        Assert.False(r1.Accepted);
        Assert.False(r2.Accepted);

        var r3 = t.Accept(R((0.65, 1521, 1297)), 8000);  // 第 3 帧一致 → 重锚定
        Assert.True(r3.Accepted);
        Assert.Equal(1521, r3.Position!.CenterX);
        Assert.Contains("重锚定", r3.Note);
    }

    [Fact]
    public void InconsistentRejectedCandidates_DoNotReanchor()
    {
        var t = new LocalizerTrack();
        t.Accept(R((0.8, 1000, 1000)), 0);
        t.Accept(R((0.8, 1000, 1000)), 1000);

        t.Accept(R((0.9, 3000, 500)), 3000);             // 远处候选 A
        t.Accept(R((0.9, 500, 2500)), 5000);             // 远处候选 B（与 A 相距远）
        var r = t.Accept(R((0.9, 3500, 2000)), 7000);    // 远处候选 C
        Assert.False(r.Accepted);                        // 三者不聚成一团 → 不重锚定
        Assert.Equal(1000, r.Position!.CenterX);
    }

    [Fact]
    public void NoCandidates_HeldWithLastPosition()
    {
        var t = new LocalizerTrack();
        t.Accept(R((0.8, 500, 500)), 0);
        t.Accept(R((0.8, 500, 500)), 1000);
        var r = t.Accept(LocalizeResult.Miss, 3000);
        Assert.False(r.Accepted);
        Assert.True(r.Held);
        Assert.Equal(500, r.Position!.CenterX);
    }

    [Fact]
    public void LowerScoreCandidateNearby_IsChosenOverFarHighScore()
    {
        var t = new LocalizerTrack();
        t.Accept(R((0.8, 1000, 1000)), 0);
        t.Accept(R((0.8, 1000, 1000)), 1000);
        var r = t.Accept(R((0.9, 3000, 500), (0.6, 1015, 1002)), 3000);
        Assert.True(r.Accepted);
        Assert.Equal(1015, r.Position!.CenterX);
    }
}
