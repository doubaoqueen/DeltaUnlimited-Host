using DeltaUnlimited.Vision.Localization;
using Xunit;

namespace DU.Tests;

/// <summary>楼层状态机（债4 2026-10）：首锚确认、滞回换层、邻接约束（隔层不跳）、失联置 Unknown、复位。</summary>
public class FloorTrackerTests
{
    private static FloorConfig Cfg() => new()
    {
        Adjacency = new HashSet<string> { "outdoor|1f", "1f|2f", "1f|b1" },
        SwitchMargin = 0.05,
        SwitchConfirmFrames = 3,
        MaxMissFrames = 8,
    };

    private static Dictionary<string, double> Scores(params (string, double)[] kv)
        => kv.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void FirstAnchor_RequiresConfirmFrames()
    {
        var t = new FloorTracker(Cfg());
        Assert.Null(t.Update(Scores(("1f", 0.7)), 0.5));   // 第 1 票
        Assert.Null(t.Update(Scores(("1f", 0.7)), 0.5));   // 第 2 票
        Assert.Equal("1f", t.Update(Scores(("1f", 0.7)), 0.5));   // 第 3 票 → 首锚
    }

    [Fact]
    public void Switch_NeedsMargin_AndConfirm()
    {
        var t = new FloorTracker(Cfg());
        for (int i = 0; i < 3; i++) t.Update(Scores(("outdoor", 0.7)), 0.5);
        Assert.Equal("outdoor", t.Current);

        // 1f 分数只高 0.02（< Margin 0.05）：不算挑战票
        Assert.Equal("outdoor", t.Update(Scores(("outdoor", 0.70), ("1f", 0.72)), 0.5));
        Assert.Equal("outdoor", t.Update(Scores(("outdoor", 0.70), ("1f", 0.72)), 0.5));

        // 高出 0.08 且邻接：连续 3 帧才切
        Assert.Equal("outdoor", t.Update(Scores(("outdoor", 0.70), ("1f", 0.78)), 0.5));  // 票 1
        Assert.Equal("outdoor", t.Update(Scores(("outdoor", 0.70), ("1f", 0.78)), 0.5));  // 票 2
        Assert.Equal("1f", t.Update(Scores(("outdoor", 0.70), ("1f", 0.78)), 0.5));       // 票 3 → 换层
    }

    [Fact]
    public void NonAdjacent_NeverSwitches_EvenWithHigherScore()
    {
        // outdoor → 2f 不邻接（没有直通楼梯）：分数再高也不跳，层只能沿邻接边走
        var t = new FloorTracker(Cfg());
        for (int i = 0; i < 3; i++) t.Update(Scores(("outdoor", 0.6)), 0.5);
        for (int i = 0; i < 10; i++)
            Assert.Equal("outdoor", t.Update(Scores(("outdoor", 0.60), ("2f", 0.95)), 0.5));
    }

    [Fact]
    public void AllMiss_Repeatedly_GoesUnknown()
    {
        var t = new FloorTracker(Cfg());
        for (int i = 0; i < 3; i++) t.Update(Scores(("1f", 0.7)), 0.5);
        for (int i = 0; i < 7; i++)
            Assert.Equal("1f", t.Update(Scores(("1f", 0.3)), 0.5));   // 容忍窗口内维持
        Assert.Null(t.Update(Scores(("1f", 0.3)), 0.5));              // 第 8 帧 → 失联置 Unknown
        Assert.Null(t.Update(Scores(("2f", 0.8)), 0.5));              // 重新首锚同样要 3 帧确认
        Assert.Null(t.Update(Scores(("2f", 0.8)), 0.5));
        Assert.Equal("2f", t.Update(Scores(("2f", 0.8)), 0.5));       // 第 3 票 → 锚定新层
    }

    [Fact]
    public void ChallengerRun_Breaks_WhenMarginDrops()
    {
        // 挑战票进行到一半分数回落 → 计数清零，下次从头数（防断续抖动攒够票）
        var t = new FloorTracker(Cfg());
        for (int i = 0; i < 3; i++) t.Update(Scores(("outdoor", 0.7)), 0.5);
        Assert.Equal("outdoor", t.Update(Scores(("outdoor", 0.70), ("1f", 0.78)), 0.5));  // 票 1
        Assert.Equal("outdoor", t.Update(Scores(("outdoor", 0.75), ("1f", 0.76)), 0.5));  // 差距消失 → 清零
        Assert.Equal("outdoor", t.Update(Scores(("outdoor", 0.70), ("1f", 0.78)), 0.5));  // 重新票 1
        Assert.Equal("outdoor", t.Update(Scores(("outdoor", 0.70), ("1f", 0.78)), 0.5));  // 票 2
        Assert.Equal("1f", t.Update(Scores(("outdoor", 0.70), ("1f", 0.78)), 0.5));       // 票 3 → 换层（清零后从头数）
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var t = new FloorTracker(Cfg());
        for (int i = 0; i < 3; i++) t.Update(Scores(("b1", 0.7)), 0.5);
        t.Reset();
        Assert.Null(t.Current);
    }
}
