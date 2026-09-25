using DeltaUnlimited.Capture;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
using Xunit;

namespace DU.Tests;

/// <summary>输入层纯逻辑测试：按键名 → 虚拟键码映射（不涉及任何系统调用）。</summary>
public class InputServiceTests
{
    [Theory]
    [InlineData("w", 0x57)]
    [InlineData("W", 0x57)]
    [InlineData("3", 0x33)]
    [InlineData("space", 0x20)]
    [InlineData("shift", 0x10)]
    [InlineData("ctrl", 0x11)]
    [InlineData("alt", 0x12)]
    [InlineData("capslock", 0x14)]
    [InlineData("=", 0xBB)]
    [InlineData("[", 0xDB)]
    [InlineData("f1", 0x70)]
    [InlineData("f12", 0x7B)]
    [InlineData("enter", 0x0D)]
    [InlineData("esc", 0x1B)]
    [InlineData("tab", 0x09)]
    public void MapKeyName_MapsKnownKeys(string key, ushort expectedVk)
        => Assert.Equal(expectedVk, InputService.MapKeyName(key));

    [Fact]
    public void MapKeyName_UnknownKey_ReturnsZero()
        => Assert.Equal((ushort)0, InputService.MapKeyName("不存在的键名"));

    [Theory]
    [InlineData("ctrl+left", 2)]
    [InlineData("w", 1)]
    [InlineData("w+shift", 2)]
    [InlineData("", 0)]
    public void SplitCombo_SplitsByPlus(string combo, int expectedCount)
        => Assert.Equal(expectedCount, InputService.SplitCombo(combo).Count);

    [Fact]
    public void SplitCombo_TrimsWhitespace()
    {
        var tokens = InputService.SplitCombo(" ctrl + left ");
        Assert.Equal(new[] { "ctrl", "left" }, tokens);
    }

    // ===== 拟人鼠标路径规划（纯逻辑） =====

    [Fact]
    public void MousePathPlanner_EndsExactlyAtTarget()
    {
        var rng = new Random(42);
        for (int i = 0; i < 20; i++)
        {
            var path = MousePathPlanner.Plan(new(100, 100), new(1500, 800), new MousePathConfig(), rng);
            Assert.Equal(1500, path[^1].X);
            Assert.Equal(800, path[^1].Y);
        }
    }

    [Fact]
    public void MousePathPlanner_ShortDistance_SingleWaypoint()
    {
        var path = MousePathPlanner.Plan(new(500, 500), new(503, 502), new MousePathConfig(), new Random(1));
        Assert.Single(path);
        Assert.Equal(new ScreenPoint(503, 502), path[0]);
    }

    [Fact]
    public void MousePathPlanner_CurvedPath_DeviatesFromStraightLine()
    {
        // 弧线特征：随机路径应出现明显偏离中线的点（证明不是直线瞬移）
        var rng = new Random(7);
        bool deviated = false;
        for (int i = 0; i < 10 && !deviated; i++)
        {
            var path = MousePathPlanner.Plan(new(400, 400), new(1400, 400), new MousePathConfig(), rng);
            deviated = path.Any(p => Math.Abs(p.Y - 400) > 10);
        }
        Assert.True(deviated, "路径应出现弧线偏离（否则轨迹是直线）");
    }

    [Fact]
    public void MousePathPlanner_Overshoot_WhenForced_ExtendsBeyondTarget()
    {
        var cfg = new MousePathConfig { OvershootProbability = 1.0, OvershootPxMin = 10, OvershootPxMax = 10 };
        var path = MousePathPlanner.Plan(new(0, 0), new(1000, 0), cfg, new Random(3));
        Assert.True(path.Any(p => p.X > 1000), "应存在冲过目标的点");
        Assert.Equal(1000, path[^1].X); // 末点仍精确回到目标
        Assert.Equal(0, path[^1].Y);
    }

    [Fact]
    public void MousePathPlanner_Steps_ScaleWithDistance()
    {
        var rng = new Random(11);
        var shortPath = MousePathPlanner.Plan(new(0, 0), new(60, 0), new MousePathConfig(), rng);
        var longPath = MousePathPlanner.Plan(new(0, 0), new(1500, 0), new MousePathConfig(), rng);
        Assert.True(longPath.Count > shortPath.Count, "远距离应比近距离步数多（轨迹更细腻）");
    }

    [Fact]
    public void MousePathPlanner_AllPoints_WithinBoundingBox()
    {
        // 所有点（含过冲）应落在起点/终点外扩 (曲率+抖动+过冲) 的包围盒内
        var cfg = new MousePathConfig { OvershootProbability = 1.0, OvershootPxMax = 22 };
        var path = MousePathPlanner.Plan(new(500, 500), new(1200, 700), cfg, new Random(5));
        const int margin = 100;
        foreach (var p in path)
        {
            Assert.InRange(p.X, 500 - margin, 1200 + margin);
            Assert.InRange(p.Y, 500 - margin, 700 + margin);
        }
    }

    // ===== 设计坐标 → 屏幕坐标换算（纯函数，窗口化/DPI 缩放点击定位的核心） =====

    [Fact]
    public void MapDesignToScreen_ClientMatchesDesign_IdentityPlusOrigin()
    {
        // 客户区与设计尺寸一致（100% 缩放）：只加窗口客户区原点
        var p = CaptureService.MapDesignToScreen((100, 50, 1920, 1080), 1920, 1080, 243, 873);
        Assert.Equal((343, 923), p);
    }

    [Fact]
    public void MapDesignToScreen_StretchedClient_ScalesByRatio()
    {
        // 4K@150% DPI 拉伸：客户区 2880×1620 = 设计 1920×1080 × 1.5
        var p = CaptureService.MapDesignToScreen((300, 270, 2880, 1620), 1920, 1080, 240, 870);
        Assert.Equal((660, 1575), p); // 300+240*1.5, 270+870*1.5
    }

    [Fact]
    public void MousePathConfig_VerifyTolerance_DefaultsToSix()
    {
        Assert.Equal(6, new MousePathConfig().VerifyTolerancePx);
    }
}
