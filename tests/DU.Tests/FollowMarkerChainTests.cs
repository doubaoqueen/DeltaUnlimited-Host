using DeltaUnlimited.Cli.Commands;
using DeltaUnlimited.Data;
using DeltaUnlimited.Vision;
using DeltaUnlimited.Vision.Servo;
using OpenCvSharp;
using Xunit;

namespace DU.Tests;

/// <summary>follow_marker 链步骤（可达性分析 + 感知器模板匹配）。</summary>
public class FollowMarkerChainTests
{
    private static ChainStep Step(string? id, string op, string? jumpTo = null) => new() { Id = id, Op = op, JumpTo = jumpTo };

    [Fact]
    public void FollowMarker_SuccessFallsThrough_BranchesAndDefaultReachable()
    {
        // follow_marker：Success 顺序落下一步；其余结局走 branches/default——所有边都计入可达
        var steps = new List<ChainStep>
        {
            new()
            {
                Id = "follow", Op = "follow_marker", Default = "bail",
                Branches = new Dictionary<string, string>
                {
                    ["timeout"] = "retry", ["stuck"] = "bail", ["danger"] = "bail",
                },
            },
            Step("next", "key"),               // Success 落到这里
            Step("bail", "pause"),             // default/stuck/danger 分支
            Step("retry", "jump", "follow"),   // timeout 分支
        };
        Assert.Empty(ChainCommand.FindUnreachableSteps(steps));
    }

    [Fact]
    public void FollowMarker_SuccessFallThroughEdge_KeepsNextStepReachable_EvenWithoutBranches()
    {
        // 回归钉住：follow_marker 的 Success 必须计入"顺序落到下一步"这条边——
        // 否则无 branches/default 时，紧跟其后的步骤会被误报为死步骤
        var steps = new List<ChainStep>
        {
            new() { Id = "follow", Op = "follow_marker" },
            Step("next", "key"),
        };
        Assert.Empty(ChainCommand.FindUnreachableSteps(steps));
    }

    // ===== TemplateMarkerPerceiver（真实模板匹配路径，假抓帧）=====

    private static string WriteTemplate(Mat tpl)
    {
        string dir = Path.Combine(Path.GetTempPath(), "du_tpl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "marker.png");
        Cv2.ImWrite(path, tpl);
        return path;
    }

    /// <summary>带纹理的假帧（彩色噪点）——真实游戏帧永远有纹理；
    /// 纯色帧会让 CCoeffNormed 在零方差窗口上产生假匹配（测试合成帧必须造得真）。</summary>
    private static Mat TexturedFrame(int w, int h, int seed = 7)
    {
        var m = new Mat(h, w, MatType.CV_8UC3, Scalar.All(80));
        var rng = new Random(seed);
        for (int i = 0; i < 600; i++)
            Cv2.Circle(m, new Point(rng.Next(w), rng.Next(h)), rng.Next(2, 7),
                       new Scalar(rng.Next(256), rng.Next(256), rng.Next(256)), -1);
        return m;
    }

    /// <summary>画一个"标记"：20×20 白块 + 左上角 6×6 黑角块。
    /// 纯色模板零方差会让 CCoeffNormed 分母为零、假匹配到处 1.0——模板必须带特征（角块），
    /// 与真实模板（游戏图标永远有内部纹理）同理。</summary>
    private static void DrawMarker(Mat m, int x, int y, int scale = 1)
    {
        Cv2.Rectangle(m, new Rect(x, y, 20 * scale, 20 * scale), Scalar.All(255), -1);
        Cv2.Rectangle(m, new Rect(x, y, 6 * scale, 6 * scale), Scalar.All(0), -1);
    }

    private static (string path, string dir) MakeMarkerFixture()
    {
        using var tpl = new Mat(20, 20, MatType.CV_8UC3, Scalar.All(255));
        DrawMarker(tpl, 0, 0);
        string path = WriteTemplate(tpl);
        string dir = Path.GetDirectoryName(path)!;
        return (path, dir);
    }

    [Fact]
    public void Perceiver_FindsMarker_AndComputesCenterOffset()
    {
        var (path, dir) = MakeMarkerFixture();
        try
        {
            using var frame = TexturedFrame(200, 200);
            DrawMarker(frame, 40, 30);                   // 中心 (50,40)，帧中心 (100,100)
            var p = new TemplateMarkerPerceiver(() => frame.Clone(), path, 200, 200, threshold: 0.95);

            var f = p.Observe();
            Assert.True(f.MarkerVisible);
            Assert.Equal(-50.0, f.MarkerOffsetPx, 1); // 50 - 200/2 = -50（偏左）
            Assert.Equal(-1, f.DistanceMeters);       // 距离未标定前不可读
        }
        finally
        {
            TemplateMatcher.ClearCache();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Perceiver_MarkerAbsent_ReportsInvisible_AndFrameDiffOfStaticSceneIsZero()
    {
        var (path, dir) = MakeMarkerFixture();
        try
        {
            using var empty = TexturedFrame(200, 200);  // 无标记的纹理帧
            var p = new TemplateMarkerPerceiver(() => empty.Clone(), path, 200, 200, threshold: 0.95);

            var f1 = p.Observe();
            Assert.False(f1.MarkerVisible);
            var f2 = p.Observe();                    // 画面完全静止
            Assert.Equal(0.0, f2.FrameDiff, 2);      // 帧差≈0 → 卡墙守卫会靠这个判死
        }
        finally
        {
            TemplateMatcher.ClearCache();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Perceiver_KeepsDesignResolution_ByNormalizing()
    {
        var (path, dir) = MakeMarkerFixture();
        try
        {
            // 抓到的帧是 400×400（2 倍设计分辨率），中心 (50,40) 归一化后仍在 (50,40)
            using var frame = TexturedFrame(400, 400);
            DrawMarker(frame, 80, 60, scale: 2);         // 2 倍分辨率下画 2 倍大的标记，归一化后回到模板尺寸
            var p = new TemplateMarkerPerceiver(() => frame.Clone(), path, 200, 200, threshold: 0.95);

            var f = p.Observe();
            Assert.True(f.MarkerVisible);
            Assert.Equal(-50.0, f.MarkerOffsetPx, 1);
        }
        finally
        {
            TemplateMatcher.ClearCache();
            Directory.Delete(dir, recursive: true);
        }
    }
}
