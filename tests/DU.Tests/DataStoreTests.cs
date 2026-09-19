using DeltaUnlimited.Data;
using Xunit;

namespace DU.Tests;

/// <summary>数据层测试：验证 data/ 与 workflows/ 下 JSON 能被正确反序列化（schema 冻结）。</summary>
public class DataStoreTests
{
    private static DataStore CreateStore() => new(DataStore.FindRoot());

    [Fact]
    public void Elements_Contains_DepartButton_WithDesignCoords()
    {
        var table = CreateStore().LoadElements();
        Assert.True(table.Elements.ContainsKey("depart_button"));
        var def = table.Elements["depart_button"];
        Assert.Equal("ocr", def.Strategy); // P0 起按钮以 OCR 为主定位
        Assert.Contains("出发", def.Params.Keywords!);
        Assert.Equal(1738, def.Params.X); // 坐标兜底仍在
        Assert.Equal(966, def.Params.Y);
        Assert.Equal("zone_bottom_right", def.Params.RegionName);
    }

    [Fact]
    public void Runtime_Loads_CalibrationValues()
    {
        var runtime = CreateStore().LoadRuntime();
        Assert.Equal(4500, runtime.PxPer90Deg);
        Assert.Equal(1920, runtime.DesignWidth);
        Assert.Equal(1080, runtime.DesignHeight);
        Assert.False(string.IsNullOrEmpty(runtime.WindowKeyword));
    }

    [Fact]
    public void Screens_PlazaReady_HasOcrAndTemplateMarkers()
    {
        var table = CreateStore().LoadScreens();
        Assert.True(table.Screens.ContainsKey("plaza_ready"));
        var screen = table.Screens["plaza_ready"];
        Assert.Contains(screen.Markers, m => m.Template == "assets/templates/depart_button.png");
        Assert.Contains(screen.Markers, m => m.Type == "ocr");
        Assert.NotEmpty(screen.Actions);
    }

    [Fact]
    public void Screens_PlazaReady_RequiresAllKeywords_ToAvoidLoadoutCrossTalk()
    {
        // 防串台回归：'配装' 会严格命中配装界面的 '确认配装' 按钮，
        // 所以广场准备状态必须 require_all 同时要求 '出发' 在场，否则会在配装界面误判为广场。
        var table = CreateStore().LoadScreens();
        var marker = table.Screens["plaza_ready"].Markers.First(m => m.Type == "ocr");
        Assert.True(marker.RequireAll == true, "plaza_ready 的 ocr 标记必须 require_all=true");
        Assert.Contains("配装", marker.Keywords!);
        Assert.Contains("出发", marker.Keywords!);
    }

    [Fact]
    public void Screens_MatchingAndCharSelect_UseCurrentButtonTexts()
    {
        // 实测校准（2026-09 游戏内 OCR）：匹配中按钮是“取消行动”（非旧文档“取消匹配”）；
        // 干员选择界面是“请选择干员”标题 + “当前出战”标签（非旧文档“当前干员”）。
        var table = CreateStore().LoadScreens();
        var matching = table.Screens["matching"].Markers.First(m => m.Type == "ocr");
        Assert.Contains("取消行动", matching.Keywords!);
        var charSelect = table.Screens["char_select"];
        Assert.Contains(charSelect.Markers, m => m.Keywords!.Contains("当前出战"));
        Assert.Contains(charSelect.Markers, m => m.Keywords!.Contains("请选择干员"));
    }

    [Fact]
    public void Screens_MapPoolMapSelectLoadoutReminder_PresentAndEnabled()
    {
        // 初进场链路 v1 依赖的四个界面必须存在且启用（map_select 曾被禁用，现已按实测重新启用）
        var table = CreateStore().LoadScreens();
        foreach (var name in new[] { "map_pool", "map_select", "loadout", "deploy_reminder" })
        {
            Assert.True(table.Screens.ContainsKey(name), $"缺少界面 {name}");
            Assert.True(table.Screens[name].Enabled != false, $"界面 {name} 被禁用");
            Assert.NotEmpty(table.Screens[name].Markers);
        }
    }

    [Fact]
    public void Elements_EntryChainButtons_Present()
    {
        var table = CreateStore().LoadElements();
        foreach (var name in new[] { "start_action_button", "confirm_loadout_button", "continue_anyway_button" })
        {
            Assert.True(table.Elements.ContainsKey(name), $"缺少元素 {name}");
            Assert.Equal("ocr", table.Elements[name].Strategy);
            Assert.NotEmpty(table.Elements[name].Params.Keywords!);
        }
    }

    [Fact]
    public void Chain_EnterMatchFirst_HasAutomatedSteps()
    {
        var chain = CreateStore().LoadChain("enter_match_first.json");
        Assert.Contains(chain.Steps, s => s.Op == "click_element" && s.Element == "start_action_button");
        Assert.Contains(chain.Steps, s => s.Op == "click_element" && s.Element == "confirm_loadout_button");
        Assert.Contains(chain.Steps, s => s.Op == "click_element" && s.Element == "continue_anyway_button");
        Assert.Contains(chain.Steps, s => s.Op == "if_screen" && s.Screen == "map_pool");
        Assert.Contains(chain.Steps, s => s.Op == "wait_screen" && s.Screen == "char_select");
    }

    [Fact]
    public void Zones_ContainsBottomRight()
    {
        var zones = CreateStore().LoadZones();
        Assert.True(zones.Zones.ContainsKey("zone_bottom_right"));
        Assert.True(zones.Zones.ContainsKey("zone_center_bottom"));
        Assert.True(zones.Zones.ContainsKey("zone_bottom_left"));
        Assert.True(zones.Zones.ContainsKey("zone_top"));
        Assert.True(zones.Zones.ContainsKey("zone_right_mid"));
        Assert.True(zones.Zones.ContainsKey("zone_left_mid"));
    }

    [Fact]
    public void Chain_EnterMatch_HasBranchSteps()
    {
        var chain = CreateStore().LoadChain("enter_match.json");
        Assert.True(chain.Steps.Count >= 10);
        Assert.Contains(chain.Steps, s => s.Id == "depart" && s.Op == "click_element" && s.Element == "depart_button");
        Assert.Contains(chain.Steps, s => s.Op == "if_screen" && s.Screen == "plaza_ready");
        Assert.Contains(chain.Steps, s => s.Op == "wait_screen" && s.Screen == "char_select");
    }

    [Fact]
    public void GameOps_MoveForward_IsW()
    {
        var ops = CreateStore().LoadGameOps();
        Assert.Equal("w", ops.KeyMap["move_forward"]);
        Assert.Equal("shift", ops.KeyMap["sprint"]);
    }
}
