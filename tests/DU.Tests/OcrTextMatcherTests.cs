using DeltaUnlimited.Vision.Ocr;
using Xunit;

namespace DU.Tests;

/// <summary>OCR 文本匹配（ADR #11：严格层裁决、候选层只报警）。</summary>
public class OcrTextMatcherTests
{
    [Fact]
    public void Strict_SingleWord()
    {
        var m = OcrTextMatcher.Match(new[] { new OcrWord("出发", 0, 0, 40, 20) }, "出发");
        Assert.True(m.StrictHit);
        Assert.True(m.CandidateHit);
    }

    [Fact]
    public void Strict_ConcatenatedMultiWord()
    {
        var words = new[]
        {
            new OcrWord("Tab", 0, 0, 60, 30),
            new OcrWord("开始游戏", 70, 0, 160, 30),
        };
        var m = OcrTextMatcher.Match(words, "Tab 开始游戏");
        Assert.True(m.StrictHit);
        Assert.Equal(0, m.X);
        Assert.Equal(230, m.W); // 并集包围盒
    }

    [Fact]
    public void Candidate_Only_NotStrict()
    {
        // ADR #11 真例：“出发”的 1 字候选同样命中“退出”，候选不得裁决
        var m = OcrTextMatcher.Match(new[] { new OcrWord("退出游戏", 0, 0, 100, 30) }, "出发");
        Assert.False(m.StrictHit);
        Assert.True(m.CandidateHit);
    }

    [Fact]
    public void Strict_SubstringWithinWord_CrossTalkTrap()
    {
        // 串台陷阱（require_all 的存在理由）：关键词“配装”会严格命中配装界面的“确认配装”按钮，
        // 因此界面标记若只用“配装”，会在配装界面误判为广场 → 必须连“出发”一起要求（见 screens.json require_all）。
        var m = OcrTextMatcher.Match(new[] { new OcrWord("确认配装", 1600, 950, 120, 30) }, "配装");
        Assert.True(m.StrictHit);
    }

    [Fact]
    public void NoHit()
    {
        var m = OcrTextMatcher.Match(new[] { new OcrWord("配装", 0, 0, 60, 30) }, "出发");
        Assert.False(m.StrictHit);
        Assert.False(m.CandidateHit);
    }

    [Fact]
    public void Strict_SameLine_SplitCharacters()
    {
        // 真例：“配装”被 OCR 拆成“配”+“装”两个词（同一行）——行级拼接必须命中且框限该行
        var words = new[]
        {
            new OcrWord("配", 1532, 956, 20, 20),
            new OcrWord("装", 1553, 956, 20, 20),
        };
        var m = OcrTextMatcher.Match(words, "配装");
        Assert.True(m.StrictHit);
        Assert.Equal(1532, m.X);
        Assert.InRange(m.W, 41, 60);
        Assert.InRange(m.H, 20, 24);
    }

    [Fact]
    public void FarApartWords_NotStrict_OnlyCandidate()
    {
        // 全帧拼接只配做候选：远距两字不得裁决（否则会点到屏幕中心）
        var words = new[]
        {
            new OcrWord("配", 100, 100, 20, 20),
            new OcrWord("装", 1500, 900, 20, 20),
        };
        var m = OcrTextMatcher.Match(words, "配装");
        Assert.False(m.StrictHit);
        Assert.True(m.CandidateHit);
    }

    [Fact]
    public void Strict_MinimalWindow_ExcludesLeadingGarbage()
    {
        // 同行有前缀噪音词时，框只覆盖关键词窗口
        var words = new[]
        {
            new OcrWord("菜单", 0, 0, 60, 20),
            new OcrWord("配", 70, 0, 20, 20),
            new OcrWord("装", 92, 0, 20, 20),
        };
        var m = OcrTextMatcher.Match(words, "配装");
        Assert.True(m.StrictHit);
        Assert.Equal(70, m.X);
        Assert.Equal(42, m.W);
    }

    [Fact]
    public void TwoCloseRows_DoNotMerge()
    {
        // 密集两行 HUD（行距 ~20px）不得串行：关键词"配装"只框自己的行
        var words = new[]
        {
            new OcrWord("爱", 136, 975, 14, 15),
            new OcrWord("吃", 152, 975, 15, 14),
            new OcrWord("配", 1532, 957, 19, 18),
            new OcrWord("装", 1553, 956, 19, 20),
        };
        var m = OcrTextMatcher.Match(words, "配装");
        Assert.True(m.StrictHit);
        Assert.Equal(1532, m.X);
        Assert.InRange(m.W, 40, 60);
        Assert.InRange(m.Y, 950, 960);
    }

    [Fact]
    public void EmptyWords_NoHit()
    {
        var m = OcrTextMatcher.Match(Array.Empty<OcrWord>(), "出发");
        Assert.False(m.StrictHit);
        Assert.False(m.CandidateHit);
    }
}
