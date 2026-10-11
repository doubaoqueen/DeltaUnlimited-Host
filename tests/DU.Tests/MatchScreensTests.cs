using DeltaUnlimited.Data;
using DeltaUnlimited.Vision;
using Xunit;

namespace DU.Tests;

/// <summary>match 组界面分表（K0 标定钉住）：分组正确、标记齐备、防串台 require_all。</summary>
public class MatchScreensTests
{
    private static readonly string[] Names =
        { "match_backpack", "match_map", "match_settlement_success", "match_settlement_fail" };

    [Fact]
    public void MatchScreens_Exist_WithGroupMatch_AndMarkers()
    {
        var table = new DataStore(DataStore.FindRoot()).LoadScreens();
        foreach (var name in Names)
        {
            Assert.True(table.Screens.ContainsKey(name), $"缺少界面 {name}");
            var s = table.Screens[name];
            Assert.Equal("match", s.Group);
            Assert.NotEmpty(s.Markers);
        }
    }

    [Fact]
    public void MatchScreens_RequireAll_ToAvoidSettlementCrossTalk()
    {
        // 防串台：'本局收获' 在背包/成功结算/失败结算都出现——每个 match 界面都必须 require_all 组合词
        var table = new DataStore(DataStore.FindRoot()).LoadScreens();
        foreach (var name in Names)
        {
            var marker = table.Screens[name].Markers.First(m => m.Type == "ocr");
            Assert.True(marker.RequireAll == true, $"{name} 的 ocr 标记必须 require_all=true");
            Assert.True(marker.Keywords is { Count: >= 2 }, $"{name} 至少两个关键词");
        }
    }

    [Fact]
    public void LobbyGroupFilter_ExcludesMatchScreens()
    {
        // 大厅链路不得扫到 match 组（省一半 OCR + 局内 unknown 兜底语义与大厅不同）
        var table = new DataStore(DataStore.FindRoot()).LoadScreens();
        var backpack = table.Screens["match_backpack"];
        Assert.False(ScreenDetector.InGroup(backpack, "lobby"));
        Assert.True(ScreenDetector.InGroup(backpack, "match"));
    }
}
