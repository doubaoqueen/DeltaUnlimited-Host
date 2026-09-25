using DeltaUnlimited.Data;
using DeltaUnlimited.Vision;
using DeltaUnlimited.Vision.Ocr;
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

    /// <summary>本地压缩 fixture（ADR #8：基准整图 gitignore + 本地压缩 fixture）。缺失时测试自动跳过。</summary>
    private static string? FixturePath(string name)
    {
        string path = Path.Combine(RepoRoot, "tests", "fixtures", name);
        if (File.Exists(path)) return path;
        Console.WriteLine($"⚠️ 跳过：本地 fixture 缺失 {path}（用真实截图压缩后放入 tests/fixtures/）");
        return null;
    }

    [Fact]
    public void TemplateMatcher_FindsDepartButton_InPlazaReadyFixture()
    {
        string root = RepoRoot;
        string? fixture = FixturePath("plaza_ready.jpg");
        if (fixture is null) return;

        using var frame = Cv2.ImRead(fixture, ImreadModes.Color);
        Assert.False(frame.Empty());

        var r = TemplateMatcher.Match(frame, Path.Combine(root, "assets", "templates", "depart_button.png"), 0.9);
        Assert.True(r.Found);
        Assert.True(r.Confidence > 0.9, $"置信度过低: {r.Confidence}");
        Assert.InRange(r.CenterX, 1680, 1800); // 设计中心 1738
        Assert.InRange(r.CenterY, 940, 990);   // 设计中心 966
    }

    [Fact]
    public void ScreenDetector_DetectsPlazaReady_OnPlazaReadyFixture()
    {
        string root = RepoRoot;
        string? fixture = FixturePath("plaza_ready.jpg");
        if (fixture is null) return;

        using var frame = Cv2.ImRead(fixture, ImreadModes.Color);
        var table = new DataStore(root).LoadScreens();

        var guess = ScreenDetector.Detect(frame, table, root);
        Assert.NotNull(guess);
        Assert.Equal("plaza_ready", guess!.Name);
    }

    [Fact]
    public void Ocr_RegionPath_ReturnsUnscaledCoordinates()
    {
        // 回归：区域路径 1.5x 放大后，OCR 框必须除回原坐标系——
        // 否则 ElementClicker 会把点击坐标放大 1.5 倍点偏（“出发”≈(1739,966) 会偏到 ≈(1958,1009)）。
        try { Ocr.Initialize(); } catch { return; } // 测试宿主不可用 OCR 时跳过
        if (Ocr.Engine is null || !Ocr.Engine.IsAvailable) return;

        string? fixture = FixturePath("plaza_ready.jpg");
        if (fixture is null) return;

        using var frame = Cv2.ImRead(fixture, ImreadModes.Color);
        var zones = new DataStore(RepoRoot).LoadZones();
        var region = zones.Zones["zone_bottom_right"].ToArray();

        var found = Ocr.FindStrict(frame, region, new[] { "出发" });
        Assert.NotNull(found);
        Assert.True(found!.Found, "区域路径应严格命中“出发”");
        Assert.InRange(found.CenterX, 1680, 1780); // 设计中心 1739（放大未除回会 ≈1958）
        Assert.InRange(found.CenterY, 930, 1000);  // 设计中心 966
    }

    [Fact]
    public void TemplateMatcher_FindsZerodamTitle_InMapPoolFixture()
    {
        // 自匹配回归：模板从地图池截图 (770,200,113,30) 裁出，应回到中心 (826,215)
        string root = RepoRoot;
        string? fixture = FixturePath("map_pool.png");
        string tpl = Path.Combine(root, "assets", "templates", "zerodam_title.png");
        if (fixture is null || !File.Exists(tpl)) return;

        using var frame = Cv2.ImRead(fixture, ImreadModes.Color);
        var r = TemplateMatcher.Match(frame, tpl, 0.9);
        Assert.True(r.Found);
        Assert.InRange(r.CenterX, 796, 856); // 设计中心 826
        Assert.InRange(r.CenterY, 200, 230); // 设计中心 215
    }

    [Fact]
    public void TemplateMatcher_ZerodamTitle_NotOn_MapSelectFixture()
    {
        // 防串台：零号大坝卡片标题只在地图池出现；已选地图部署面板上不应命中，
        // 否则地图池的模板标记会与 map_select 串台（若将来发现真机误报，给模板标记加区域限制）。
        string root = RepoRoot;
        string? fixture = FixturePath("map_select.png");
        string tpl = Path.Combine(root, "assets", "templates", "zerodam_title.png");
        if (fixture is null || !File.Exists(tpl)) return;

        using var frame = Cv2.ImRead(fixture, ImreadModes.Color);
        var r = TemplateMatcher.Match(frame, tpl, 0.85);
        Assert.False(r.Found, $"置信度 {r.Confidence:F3} —— 零号大坝标题模板在部署面板上误命中");
    }

    /// <summary>多态门控回归（2026-09 实测截图）：各界面唯一命中且不串台。
    /// fixture 用无损 PNG（JPEG 压缩会让小字误读，如“战”→“摅”）。</summary>
    [Theory]
    [InlineData("plaza_first.png", "plaza_first")]
    [InlineData("matching.png", "matching")]
    [InlineData("char_select.png", "char_select")]
    [InlineData("map_pool.png", "map_pool")]
    [InlineData("map_pool_live.png", "map_pool")]
    [InlineData("map_select.png", "map_select")]
    [InlineData("loadout.png", "loadout")]
    [InlineData("deploy_reminder.png", "deploy_reminder")]
    [InlineData("space_continue.png", "space_continue")]
    [InlineData("mode_select.png", "mode_select")]
    [InlineData("mode_select_launch.png", "mode_select")]
    public void ScreenDetector_DetectsStateUniquely_OnStateFixtures(string fixtureName, string expected)
    {
        try { Ocr.Initialize(); } catch { return; } // 测试宿主不可用 OCR 时跳过
        if (Ocr.Engine is null || !Ocr.Engine.IsAvailable) return;

        string? fixture = FixturePath(fixtureName);
        if (fixture is null) return;

        using var frame = Cv2.ImRead(fixture, ImreadModes.Color);
        var table = new DataStore(RepoRoot).LoadScreens();

        var guess = ScreenDetector.Detect(frame, table, RepoRoot);
        Assert.NotNull(guess);
        Assert.Equal(expected, guess!.Name);
        Assert.Empty(guess.Alternatives); // 唯一命中：多界面同时命中=串台，必须告警整改
    }

    /// <summary>真机现场截图（screenshots/ 下，gitignore 不进仓库）。缺失时测试自动跳过。</summary>
    private static string? LivePath(string rel)
    {
        string path = Path.Combine(RepoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path)) return path;
        Console.WriteLine($"⚠️ 跳过：现场截图缺失 {path}");
        return null;
    }

    [Fact]
    public void TemplateMatcher_LobbyTabHint_MatchesLobbyCaptures_NotPlaza()
    {
        // 特勤处主标记=底部“Tab 开始游戏”提示条模板：两张特勤处现场截图必须命中；
        // Tab 后到达的备战截图不得命中（防串台，否则 Tab 会在特勤处/备战之间来回切）。
        string root = RepoRoot;
        string tpl = Path.Combine(root, "assets", "templates", "lobby_tab_hint.png");
        if (!File.Exists(tpl)) return;
        foreach (var rel in new[]
                 {
                     "screenshots/unknown/unknown_20260921_225832.png",
                     "screenshots/unknown/unknown_20260921_225937.png"
                 })
        {
            string? live = LivePath(rel);
            if (live is null) continue;
            using var frame = Cv2.ImRead(live, ImreadModes.Color);
            var r = TemplateMatcher.Match(frame, tpl, 0.85);
            Assert.True(r.Found, $"{rel} 应命中 Tab 提示条，置信度 {r.Confidence:F3}");
        }

        string? plaza = LivePath("screenshots/captured/chain_6_after_tab_225735.png");
        if (plaza is null) return;
        using var pf = Cv2.ImRead(plaza, ImreadModes.Color);
        var pr = TemplateMatcher.Match(pf, tpl, 0.85);
        Assert.False(pr.Found, $"Tab 提示条模板在备战截图上误命中（{pr.Confidence:F3}）—— 特勤处标记会串台");
    }

    [Fact]
    public void TemplateMatcher_ConfirmLoadoutButton_MatchesLiveAndFixture()
    {
        // 确认配装按钮模板：今天现场截图必须命中（自匹配），历史 fixture（另一晚截图）也应命中（跨时刻稳定）。
        string root = RepoRoot;
        string tpl = Path.Combine(root, "assets", "templates", "confirm_loadout_button.png");
        if (!File.Exists(tpl)) return;

        string? live = LivePath("screenshots/unknown/unknown_20260921_225900.png");
        if (live is not null)
        {
            using var frame = Cv2.ImRead(live, ImreadModes.Color);
            var r = TemplateMatcher.Match(frame, tpl, 0.85);
            Assert.True(r.Found, $"现场配装截图应命中确认配装模板，置信度 {r.Confidence:F3}");
        }

        string? fixture = FixturePath("loadout.png");
        if (fixture is null) return;
        using var ff = Cv2.ImRead(fixture, ImreadModes.Color);
        var fr = TemplateMatcher.Match(ff, tpl, 0.85);
        Assert.True(fr.Found, $"历史配装 fixture 应命中确认配装模板，置信度 {fr.Confidence:F3}");
    }

    [Theory]
    [InlineData("screenshots/unknown/unknown_20260921_225832.png", "lobby")]
    [InlineData("screenshots/unknown/unknown_20260921_225937.png", "lobby")]
    [InlineData("screenshots/unknown/unknown_20260921_225900.png", "loadout")]
    [InlineData("screenshots/captured/chain_6_after_tab_225735.png", "plaza_first")]
    [InlineData("screenshots/captured/chain_fail_20260921_225808.png", "settlement")]
    [InlineData("screenshots/captured/capture_20260922_184254.png", "preset_select")]
    [InlineData("screenshots/captured/OperatorBeginTemp.png", "char_select")]
    public void ScreenDetector_DetectsStateUniquely_OnLiveCaptures(string rel, string expected)
    {
        try { Ocr.Initialize(); } catch { return; } // 测试宿主不可用 OCR 时跳过
        if (Ocr.Engine is null || !Ocr.Engine.IsAvailable) return;

        string? live = LivePath(rel);
        if (live is null) return;

        using var frame = Cv2.ImRead(live, ImreadModes.Color);
        var table = new DataStore(RepoRoot).LoadScreens();

        var guess = ScreenDetector.Detect(frame, table, RepoRoot);
        Assert.NotNull(guess);
        Assert.Equal(expected, guess!.Name);
        Assert.Empty(guess.Alternatives); // 唯一命中：特勤处提示条/确认配装模板不得与其他界面串台
    }

    [Fact]
    public void TemplateMatcher_StandardSetCard_MatchesLoadoutCaptures()
    {
        // “制式套装”卡片模板（用户标定 (1660,880)-(1730,898)，OCR 读不出的美术字）：现场配装截图必须命中且回到卡片中心。
        string root = RepoRoot;
        string tpl = Path.Combine(root, "assets", "templates", "standard_set_card.png");
        if (!File.Exists(tpl)) return;

        string? live = LivePath("screenshots/unknown/unknown_20260921_225900.png");
        if (live is null) return;
        using var frame = Cv2.ImRead(live, ImreadModes.Color);
        var r = TemplateMatcher.Match(frame, tpl, 0.85);
        Assert.True(r.Found, $"现场配装截图应命中制式套装卡片模板，置信度 {r.Confidence:F3}");
        Assert.InRange(r.CenterX, 1660, 1730);
        Assert.InRange(r.CenterY, 880, 898);
    }

    [Fact]
    public void Ocr_FindAllStrict_CountsUnequipped_OnLoadoutCapture()
    {
        // 裸装鉴别（check_loadout）依据：裸装配装界面应至少读到 4 处“未装配”（实测 7 处）
        try { Ocr.Initialize(); } catch { return; } // 测试宿主不可用 OCR 时跳过
        if (Ocr.Engine is null || !Ocr.Engine.IsAvailable) return;

        string? live = LivePath("screenshots/unknown/unknown_20260921_225900.png");
        if (live is null) return;
        using var frame = Cv2.ImRead(live, ImreadModes.Color);
        int count = Ocr.CountStrict(frame, null, new[] { "未装配" });
        Assert.True(count >= 4, $"裸装配装界面应至少读到 4 处“未装配”，实际 {count}");
    }

    [Fact]
    public void OcrTextMatcher_CountStrict_CountsSeparateAndMergedWords()
    {
        // 计数语义：拆词行（未+装+配）与合并词（未装配）都应计数，且同一行多次出现分别计数
        var words = new List<OcrWord>
        {
            new("未", 0, 0, 10, 10), new("装", 12, 0, 10, 10), new("配", 24, 0, 10, 10),   // 拆词行 1
            new("未装配", 0, 20, 30, 10),                                                  // 合并词行 2
            new("未", 0, 40, 10, 10), new("装", 12, 40, 10, 10), new("配", 24, 40, 10, 10), // 拆词行 3
        };
        Assert.Equal(3, OcrTextMatcher.CountStrict(words, "未装配"));
    }

    [Fact]
    public void Ocr_WordsOverloads_MatchCachedWordLists()
    {
        // 同帧词表缓存路径（ScreenDetector.Scan）：FindStrict/FindAllStrict/CountStrict 的词表重载应与帧路径同语义
        var words = new List<OcrWord>
        {
            new("未", 0, 0, 10, 10), new("装", 12, 0, 10, 10), new("配", 24, 0, 10, 10),
            new("出发", 100, 100, 40, 20),
        };
        Assert.True(Ocr.FindStrict(words, new[] { "未装配" }) is { Found: true });
        Assert.True(Ocr.FindStrict(words, new[] { "出发" }) is { Found: true });
        Assert.Equal(2, Ocr.FindAllStrict(words, new[] { "未装配", "出发" }).Count);
        Assert.Equal(1, Ocr.CountStrict(words, new[] { "未装配" }));
        Assert.Equal(1, Ocr.CountStrict(words, new[] { "出发" }));
    }
}
