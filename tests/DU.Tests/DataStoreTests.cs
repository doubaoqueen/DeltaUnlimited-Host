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
        Assert.Equal("coord", def.Strategy);
        Assert.Equal(1738, def.Params.X);
        Assert.Equal(966, def.Params.Y);
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
    public void Screens_PlazaReady_HasTemplateMarker()
    {
        var table = CreateStore().LoadScreens();
        Assert.True(table.Screens.ContainsKey("plaza_ready"));
        var screen = table.Screens["plaza_ready"];
        Assert.Single(screen.Markers);
        Assert.Equal("assets/templates/depart_button.png", screen.Markers[0].Template);
        Assert.NotEmpty(screen.Actions);
    }

    [Fact]
    public void Chain_EnterMatch_HasBranchSteps()
    {
        var chain = CreateStore().LoadChain("enter_match.json");
        Assert.True(chain.Steps.Count >= 8);
        Assert.Contains(chain.Steps, s => s.Id == "normal_ready" && s.Op == "click_element" && s.Element == "depart_button");
        Assert.Contains(chain.Steps, s => s.Op == "if_screen" && s.Screen == "plaza_ready");
    }

    [Fact]
    public void GameOps_MoveForward_IsW()
    {
        var ops = CreateStore().LoadGameOps();
        Assert.Equal("w", ops.KeyMap["move_forward"]);
        Assert.Equal("shift", ops.KeyMap["sprint"]);
    }
}
