using DeltaUnlimited.Data;
using Xunit;

namespace DU.Tests;

/// <summary>经济决策纯函数（跑刀工程提案 §5）：白名单起步，全部规则一行数据 + 一个断言。</summary>
public class EconomyAgentTests
{
    private static ItemCatalogTable Catalog() => new()
    {
        MinTierToPick = 2,
        Items =
        {
            new ItemCatalogEntry { Name = "军用背包", Type = "背包", Tier = "紫", Pick = true,
                                    OcrKeywords = { "军包", "战术背包" } },
            new ItemCatalogEntry { Name = "止痛药", Type = "药品", Tier = "绿", Pick = true },
            new ItemCatalogEntry { Name = "9mm弹药", Type = "弹药", Tier = "白", Pick = false },
            new ItemCatalogEntry { Name = "军用头盔", Type = "头盔", Tier = "金", Pick = true },
        },
    };

    [Theory]
    [InlineData("白", 1)]
    [InlineData("绿", 2)]
    [InlineData("蓝", 3)]
    [InlineData("紫", 4)]
    [InlineData("金", 5)]
    [InlineData("红", 6)]
    [InlineData(" 绿 ", 2)]      // 容空格
    [InlineData("未知", 0)]
    [InlineData(null, 0)]
    public void TierValue_MapsKnownTiers(string? tier, int expected)
        => Assert.Equal(expected, EconomyAgent.TierValue(tier));

    [Fact]
    public void MatchItem_HitsName_OrKeywordVariant_CaseInsensitive()
    {
        var c = Catalog();
        Assert.Equal("军用背包", EconomyAgent.MatchItem(c, "军用背包")!.Name);
        Assert.Equal("军用背包", EconomyAgent.MatchItem(c, "军包")!.Name);          // 关键词变体
        Assert.Equal("军用背包", EconomyAgent.MatchItem(c, "战术背包x1")!.Name);    // 包含匹配
        Assert.Equal("9mm弹药", EconomyAgent.MatchItem(c, "9MM弹药")!.Name);        // 大小写不敏感
        Assert.Null(EconomyAgent.MatchItem(c, "不存在的东西"));
        Assert.Null(EconomyAgent.MatchItem(c, ""));
        Assert.Null(EconomyAgent.MatchItem(c, "   "));
    }

    [Fact]
    public void ShouldPick_RequiresWhitelist_Tier_AndSpace()
    {
        var c = Catalog();
        Assert.True(EconomyAgent.ShouldPick(c, "军用背包", 4, backpackFull: false));
        Assert.False(EconomyAgent.ShouldPick(c, "军用背包", 4, backpackFull: true));  // 背包满
        Assert.False(EconomyAgent.ShouldPick(c, "军用背包", 1, backpackFull: false)); // 品质低于 min_tier
        Assert.False(EconomyAgent.ShouldPick(c, "9mm弹药", 2, backpackFull: false));  // pick=false 白名单外
        Assert.False(EconomyAgent.ShouldPick(c, "路边石块", 6, backpackFull: false)); // 无条目 → fail-open 不捡
    }

    [Fact]
    public void ShouldDiscard_KeepsOnlyAboveKeepTier()
    {
        Assert.True(EconomyAgent.ShouldDiscard(1, 2));
        Assert.False(EconomyAgent.ShouldDiscard(2, 2));
        Assert.False(EconomyAgent.ShouldDiscard(5, 3));
    }

    [Fact]
    public void TimeToExtract_ByDeadline_OrFullBackpack()
    {
        Assert.True(EconomyAgent.TimeToExtract(3, backpackFull: false, extractDeadlineMin: 5));  // 撞红线
        Assert.False(EconomyAgent.TimeToExtract(8, backpackFull: false, extractDeadlineMin: 5)); // 未到红线
        Assert.True(EconomyAgent.TimeToExtract(99, backpackFull: true, extractDeadlineMin: 5));   // 背包满即撤
        Assert.False(EconomyAgent.TimeToExtract(99, backpackFull: false, extractDeadlineMin: 0)); // 红线禁用且背包没满
    }

    [Fact]
    public void ItemCatalog_Loads_WithDefaultPickRules()
    {
        // 数据冻结：min_tier_to_pick 默认 2=绿（白名单起步），extract_deadline_min 默认 0=禁用
        var table = new DataStore(DataStore.FindRoot()).LoadItemCatalog();
        Assert.Equal(2, table.MinTierToPick);
        Assert.Equal(0, table.ExtractDeadlineMin);
        Assert.NotNull(table.Items);
    }
}
