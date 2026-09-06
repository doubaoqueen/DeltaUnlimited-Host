using DeltaUnlimited.Data;
using DeltaUnlimited.Vision;
using OpenCvSharp;
using Xunit;

namespace DU.Tests;

/// <summary>视觉层测试：帧归一化、帧差、模板匹配、界面识别（用仓库内真实素材做 self-match）。</summary>
public class VisionTests
{
    private static string RepoRoot => DataStore.FindRoot();

    [Fact]
    public void FrameTools_Normalize_UpscalesToDesignResolution()
    {
        using var frame = new Mat(360, 640, MatType.CV_8UC3);
        using var norm = FrameTools.Normalize(frame, 1920, 1080);
        Assert.Equal(1920, norm.Width);
        Assert.Equal(1080, norm.Height);
    }

    [Fact]
    public void FrameTools_Normalize_SameSize_ReturnsSameDimensions()
    {
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3);
        using var norm = FrameTools.Normalize(frame, 1920, 1080);
        Assert.Equal(1920, norm.Width);
        Assert.Equal(1080, norm.Height);
    }

    [Fact]
    public void FrameDiff_IdenticalFrames_ScoresZero()
    {
        using var a = Mat.Zeros(100, 100, MatType.CV_8UC3);
        using var b = Mat.Zeros(100, 100, MatType.CV_8UC3); // 内容相同的两帧
        Assert.True(FrameDiff.Score(a, b) < 0.001);
    }

    [Fact]
    public void FrameDiff_OppositeFrames_ScoresHigh()
    {
        using var a = Mat.Zeros(100, 100, MatType.CV_8UC3);
        using var b = new Mat(100, 100, MatType.CV_8UC3, Scalar.All(255));
        Assert.True(FrameDiff.Score(a, b) > 200);
    }

    [Fact]
    public void TemplateMatcher_FindsPlazaPrepButton_InGamePlazaScreenshot()
    {
        string root = RepoRoot;
        using var frame = Cv2.ImRead(Path.Combine(root, "screenshots", "game-plaza.png"), ImreadModes.Color);
        Assert.False(frame.Empty());

        var r = TemplateMatcher.Match(frame, Path.Combine(root, "assets", "templates", "plaza_prep_button.png"), 0.9);
        Assert.True(r.Found);
        Assert.True(r.Confidence > 0.95, $"置信度过低: {r.Confidence}");
        Assert.InRange(r.CenterX, 1600, 1700); // 设计中心 1645
        Assert.InRange(r.CenterY, 950, 980);   // 设计中心 966
    }

    [Fact]
    public void ScreenDetector_DetectsPlazaFirst_OnGamePlazaScreenshot()
    {
        string root = RepoRoot;
        using var frame = Cv2.ImRead(Path.Combine(root, "screenshots", "game-plaza.png"), ImreadModes.Color);
        var table = new DataStore(root).LoadScreens();

        var guess = ScreenDetector.Detect(frame, table, root);
        Assert.NotNull(guess);
        Assert.Equal("plaza_first", guess!.Name);
    }
}
