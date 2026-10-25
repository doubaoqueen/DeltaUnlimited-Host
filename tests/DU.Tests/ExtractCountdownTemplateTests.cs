using DeltaUnlimited.Vision;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace DU.Tests;

/// <summary>撤离倒计时到达模板的真实匹配器回归（2026-10-24 定标复查）：
/// 走运行时同一条路（TemplateMatcher.Match + TM_CCOEFF_NORMED + searchRegion），
/// 不用模拟——钉住"模板 + 区域 [850,90,230,100] + 阈值 0.80"这套配置的正负样本表现。
/// 素材为 K0 实帧（tests/fixtures/，gitignore 不出库）：缺任一素材整组跳过（fixtures 管线约定）。</summary>
public class ExtractCountdownTemplateTests
{
    private readonly ITestOutputHelper _out;
    private static readonly int[] Region = { 850, 90, 230, 100 };

    private static readonly string[] Positives =
        { "extract_countdown_pos1", "extract_countdown_pos2", "extract_countdown_pos3", "extract_countdown_pos4" };

    private static readonly string[] Negatives =
        { "extract_countdown_pre", "extract_neg1", "extract_neg2", "extract_neg3", "extract_neg4" };

    public ExtractCountdownTemplateTests(ITestOutputHelper outHelper) => _out = outHelper;

    // 注意：不 ClearCache——静态模板缓存与 MatchScreensTests 等并行测试共享，
    // 中途清缓存会释放别人正在用的 Mat（0xC0000005 竞态，实测踩过）。缓存随进程退出回收。

    /// <summary>从测试 bin 向上找仓库根（.git 所在层），再拼模板路径；找不到返回 null（跳过）。</summary>
    private static string? FindTemplate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                var p = Path.Combine(dir.FullName, "assets", "templates", "extract_countdown_corner.png");
                return File.Exists(p) ? p : null;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static bool FixturesReady(string[] names)
        => names.All(n => FixtureFrames.Has(n));

    [Fact]
    public void InZone_Frames_HitAt_ProductionThreshold()
    {
        var tpl = FindTemplate();
        if (tpl is null || !FixturesReady(Positives)) { _out.WriteLine("⚠ 缺模板或 fixtures，跳过"); return; }

        foreach (var name in Positives)
        {
            using var frame = FixtureFrames.Load(name)!;
            var m = TemplateMatcher.Match(frame, tpl!, 0.80, Region);
            _out.WriteLine($"{name}: score={m.Confidence:F3} found={m.Found} @({m.CenterX},{m.CenterY})");
            Assert.True(m.Found, $"{name} 应在阈值 0.80 命中（实测 {m.Confidence:F3}）");
            // 位置钉死在卡片左上角标附近（防"高分但匹到区域里别的东西"）
            Assert.InRange(m.CenterX, 880, 1000);
            Assert.InRange(m.CenterY, 100, 190);
        }
    }

    [Fact]
    public void PreZone_AndNormalFrames_DoNotHit()
    {
        var tpl = FindTemplate();
        if (tpl is null || !FixturesReady(Negatives)) { _out.WriteLine("⚠ 缺模板或 fixtures，跳过"); return; }

        foreach (var name in Negatives)
        {
            using var frame = FixtureFrames.Load(name)!;
            var m = TemplateMatcher.Match(frame, tpl!, 0.80, Region);
            _out.WriteLine($"{name}: score={m.Confidence:F3} found={m.Found}");
            Assert.False(m.Found, $"{name} 不应命中（实测区域内最高 {m.Confidence:F3} ≥ 0.80）");
        }
    }
}
