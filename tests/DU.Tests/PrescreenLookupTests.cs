using DeltaUnlimited.Cli;
using Xunit;

namespace DU.Tests;

/// <summary>VLM 初筛结果查询与标注界面展示文本（纯逻辑）。红线：初筛只是参考，不是训练标签。</summary>
public class PrescreenLookupTests
{
    [Fact]
    public void Normalize_ConvertsBackslashes()
        => Assert.Equal("full/full_1.jpg", PrescreenLookup.Normalize(@"full\full_1.jpg"));

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
        => Assert.Empty(PrescreenLookup.Load(Path.Combine(Path.GetTempPath(), "du_no_root_" + Guid.NewGuid().ToString("N"))));

    [Fact]
    public void Load_ParsesRows_SkipsMalformed_LastWins()
    {
        string root = Path.Combine(Path.GetTempPath(), "du_pre_" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(root, "ai-training", "datasets", "record");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "prescreen.csv"), new[]
            {
                "relpath,scene,roi_usable,occlusion,time_weather,has_enemy,has_loot_signal,has_prompt,quality,confidence,error",
                @"full\a.jpg,对局内,True,无,白天,False,True,False,清晰,0.83,",
                "残行,缺列",
                @"full\a.jpg,对局内,False,贴脸遮挡,夜战,False,False,False,清晰,0.71,", // 重筛：后行覆盖前行
                @"full\b.jpg,结算,,无,未知,False,False,False,清晰,0.5,无法解析模型输出",
            });

            var map = PrescreenLookup.Load(root);
            Assert.Equal(2, map.Count);

            var a = map[PrescreenLookup.Normalize(@"full\a.jpg")];
            Assert.True(a.Judged);
            Assert.False(a.RoiUsableTrue);      // 后行覆盖：淘汰
            Assert.Equal("夜战", a.TimeWeather);
            Assert.True(a.IsGoldFrame == false);

            var b = map[PrescreenLookup.Normalize(@"full\b.jpg")];
            Assert.False(b.Judged);             // error 非空 = 未判定
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void FormatForLabel_Null_ShowsNoRecord()
    {
        string text = PrescreenLookup.FormatForLabel(null);
        Assert.Contains("VLM 初筛", text);
        Assert.Contains("不是训练标签", text);
        Assert.Contains("无初筛记录", text);
    }

    [Fact]
    public void FormatForLabel_JudgedUsable_ListsMetadataAndGoldFrames()
    {
        var row = new PrescreenRow(@"full\a.jpg", "对局内", "True", "无", "白天", "True", "False", "True", "清晰", "0.83", "");
        string text = PrescreenLookup.FormatForLabel(row);
        Assert.Contains("✅可用", text);
        Assert.Contains("scene=对局内", text);
        Assert.Contains("金帧", text);
        Assert.Contains("敌人", text);
        Assert.Contains("交互提示", text);
    }

    [Fact]
    public void FormatForLabel_NotUsable_HintsDiscard()
    {
        var row = new PrescreenRow(@"full\a.jpg", "对局内", "False", "天空", "白天", "False", "False", "False", "清晰", "0.9", "");
        string text = PrescreenLookup.FormatForLabel(row);
        Assert.Contains("⛔淘汰", text);
        Assert.Contains("X 丢弃", text);
    }

    [Fact]
    public void FormatForLabel_FailedRow_ShowsErrorAndRetryHint()
    {
        var row = new PrescreenRow(@"full\a.jpg", "", "", "", "", "", "", "", "", "", "接口超时");
        string text = PrescreenLookup.FormatForLabel(row);
        Assert.Contains("接口超时", text);
        Assert.Contains("自动重试", text);
    }
}
