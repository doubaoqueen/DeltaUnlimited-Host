using DeltaUnlimited.Data;
using DeltaUnlimited.Vision.Localization;
using OpenCvSharp;
using Xunit;

namespace DU.Tests;

/// <summary>小地图定位器（Vision/Localization/MinimapLocalizer）：合成图自检 + 真机帧回归（素材缺失自动跳过）。</summary>
public class LocalizerTests
{
    // ---------- 合成图自检（不依赖任何游戏素材）----------

    private static (string root, string mapRel, Point expect, LocalizerConfig cfg) MakeSynthetic()
    {
        string root = Path.Combine(Path.GetTempPath(), "du_loc_" + Guid.NewGuid().ToString("N"));
        string mapDir = Path.Combine(root, "map");
        Directory.CreateDirectory(mapDir);

        // 1200x900 噪声底图（有纹理，避免零方差）
        var rng = new Random(7);
        using var map = new Mat(900, 1200, MatType.CV_8UC3, Scalar.All(60));
        for (int i = 0; i < 4000; i++)
        {
            Cv2.Rectangle(map,
                new Rect(rng.Next(1200), rng.Next(900), rng.Next(3, 25), rng.Next(3, 25)),
                new Scalar(rng.Next(256), rng.Next(256), rng.Next(256)), -1);
        }

        // 在 (400,300)-(700,600) 放一块可辨认的图案，作为"小地图内容"
        Cv2.Rectangle(map, new Rect(400, 300, 300, 300), new Scalar(0, 0, 255), -1);
        Cv2.Circle(map, new Point(550, 450), 90, new Scalar(0, 255, 0), -1);
        Cv2.PutText(map, "ZONE", new Point(430, 520), HersheyFonts.HersheySimplex, 1.5, Scalar.White, 3);
        string mapPath = Path.Combine(mapDir, "synthetic.png");
        Cv2.ImWrite(mapPath, map);

        var cfg = new LocalizerConfig
        {
            MapImage = mapPath,
            Downscale = 2,
            ScalesPx = new[] { 300 },
            MinScore = 0.5,
            MiniCenterX = 165, MiniCenterY = 185, MiniRadius = 120,
            TopK = 3,
        };

        // 帧：1920x1080，把小地图内容画进圆形面板区域（240x240 → 缩放后放进去）
        using var patch = new Mat(map, new Rect(400, 300, 300, 300)).Clone();
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(20));
        using var scaled = new Mat();
        Cv2.Resize(patch, scaled, new Size(240, 240), interpolation: InterpolationFlags.Area);
        scaled.CopyTo(new Mat(frame, new Rect(45, 65, 240, 240)));

        string framePath = Path.Combine(root, "frame.png");
        Cv2.ImWrite(framePath, frame);
        return (root, mapPath, new Point(550, 450), cfg);
    }

    [Fact]
    public void Synthetic_FindsEmbeddedPatch()
    {
        var (root, _, expect, cfg) = MakeSynthetic();
        try
        {
            string framePath = Path.Combine(root, "frame.png");
            using var frame = Cv2.ImRead(framePath, ImreadModes.Color);
            using var loc = new MinimapLocalizer(cfg, root);
            var r = loc.Localize(frame);

            Assert.True(r.Found, "应命中");
            Assert.NotNull(r.Best);
            Assert.InRange(Math.Abs(r.Best!.CenterX - expect.X), 0, 25);
            Assert.InRange(Math.Abs(r.Best.CenterY - expect.Y), 0, 25);
            Assert.Equal(300, r.Best.ScalePx);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingMap_ThrowsFileNotFound()
    {
        var cfg = new LocalizerConfig { MapImage = "no/such/map.png" };
        Assert.Throws<FileNotFoundException>(() => new MinimapLocalizer(cfg, Path.GetTempPath()));
    }

    [Fact]
    public void Localize_EmptyFrame_ReturnsMiss_NoThrow()
    {
        var (root, _, _, cfg) = MakeSynthetic();
        try
        {
            using var loc = new MinimapLocalizer(cfg, root);
            var r = loc.Localize(new Mat());     // 空帧
            Assert.False(r.Found);               // fail-open：不抛异常
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------- 真机帧回归（用户 2026-10-21 肉眼标注；素材在 tests/fixtures/localizer/，缺失跳过）----------

    private static readonly (string Id, string Frame, int X, int Y, string Verdict)[] Labels =
    {
        ("m1b", "full_20261012_000848_109.jpg", 2357, 1067, "good"),
        ("m1c", "full_20261012_001113_824.jpg", 2474, 1110, "good"),
        ("m1d", "full_20261012_001457_708.jpg", 1176, 1540, "good"),
        ("m2b", "full_20261012_003010_816.jpg", 1577, 1307, "good"),
        ("m1a", "full_20261012_000707_949.jpg", 2496, 1362, "good"),   // 2026-10-21 用户复核：全部对得上
        ("m2a", "full_20261012_002950_974.jpg", 1473, 1441, "good"),   // 2026-10-21 复核改判：正确
    };

    [Fact]
    public void Fixture_Regression_MatchesUserLabels()
    {
        string dir = Path.Combine(FixtureFrames.Dir, "localizer");
        string mapPath = Path.Combine(dir, "zero_dam_z4.png");
        if (!File.Exists(mapPath))
            return; // 素材未入库 → 跳过（本地放好后再跑）

        var cfg = new LocalizerConfig
        {
            MapImage = mapPath,
            Downscale = 2,
            ScalesPx = new[] { 150, 180, 210, 245, 285, 330, 380, 440 },
            MinScore = 0.45,
            MiniCenterX = 165, MiniCenterY = 185, MiniRadius = 120,
            TopK = 3,
        };

        using var loc = new MinimapLocalizer(cfg, dir);
        int goodHit = 0, goodTotal = 0;
        foreach (var (id, frameName, x, y, verdict) in Labels)
        {
            string framePath = Path.Combine(dir, frameName);
            if (!File.Exists(framePath)) continue;

            using var frame = Cv2.ImRead(framePath, ImreadModes.Color);
            using var norm = DeltaUnlimited.Vision.FrameTools.Normalize(frame, 1920, 1080);
            var r = loc.Localize(norm);
            Assert.True(r.Found, $"{id}: 应有候选");

            double d = Math.Sqrt(Math.Pow(r.Best!.CenterX - x, 2) + Math.Pow(r.Best.CenterY - y, 2));
            if (verdict == "good")
            {
                goodTotal++;
                if (d < 80) goodHit++;
                Assert.True(d < 80, $"{id}: 距标注点 {d:F0}px（应 <80），实际 ({r.Best.CenterX},{r.Best.CenterY})");
            }
            // verdict == "bad"：当前已知会误报（0.746），此处不断言，作为问题记录（见 docs §4）
        }
        Assert.True(goodTotal == 0 || goodHit == goodTotal);
    }

    [Fact]
    public void Synthetic_PriorSearch_HitsWithinWindow_MissesOutside()
    {
        // 先验搜索（实时伺服提速用）：只在上一帧位置附近搜索；窗口外不应产生命中
        var (root, _, expect, cfg) = MakeSynthetic();
        try
        {
            string framePath = Path.Combine(root, "frame.png");
            using var frame = Cv2.ImRead(framePath, ImreadModes.Color);
            using var loc = new MinimapLocalizer(cfg, root);

            var near = loc.Localize(frame, new LocalizePrior(expect.X + 100, expect.Y - 80, 300, 300));
            Assert.True(near.Found, "先验附近应命中");
            Assert.InRange(Math.Abs(near.Best!.CenterX - expect.X), 0, 25);

            var far = loc.Localize(frame, new LocalizePrior(1050, 800, 300, 150)); // 远离目标
            Assert.False(far.Found, "目标不在搜索窗口内时不应命中");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_BackpackFrame_IsGatedByHudStateGate()
    {
        // 2026-10-21 实测：背包（Tab）全屏时左上角不是小地图 —— 定位前必须被 HUD 状态门挡下
        string dir = Path.Combine(FixtureFrames.Dir, "localizer");
        string framePath = Path.Combine(dir, "full_20261012_003431_486.jpg");
        string mapPath = Path.Combine(dir, "zero_dam_z4.png");
        if (!File.Exists(framePath) || !File.Exists(mapPath)) return;

        try { DeltaUnlimited.Vision.Ocr.Ocr.Initialize(); }
        catch { return; } // 测试宿主不可用 OCR 时跳过

        var screens = new DataStore(DataStore.FindRoot()).LoadScreens();
        using var frame = Cv2.ImRead(framePath, ImreadModes.Color);
        using var norm = DeltaUnlimited.Vision.FrameTools.Normalize(frame, 1920, 1080);
        bool normalHud = HudStateGate.IsNormalHud(norm, screens, DataStore.FindRoot(), out string? uiScreen);
        Assert.False(normalHud, "背包帧不应被判为正常 HUD");
        Assert.Equal("match_backpack", uiScreen);
    }
}

