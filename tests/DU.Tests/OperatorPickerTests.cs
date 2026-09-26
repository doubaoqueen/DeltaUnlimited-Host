using DeltaUnlimited.Cli;
using DeltaUnlimited.Data;
using Xunit;

namespace DU.Tests;

/// <summary>干员选择间距推算法（纯函数）：目标艺术字读不出时，从任意可见标签按类型间距推算目标位置。
/// 核心价值：游戏加新干员 → 改 count + 重标定锚点，推算自动跟着走。</summary>
public class OperatorPickerTests
{
    // 实测布局（operator_presets.json 2026-09-24）：锚点 突击98/支援563/工程1027/侦察1600，同类间距 111
    private static OperatorLayout Layout() => new()
    {
        AvatarFirstY = 873,
        AvatarSpacing = 111,
        FirstAvatarOffset = 33,
        TypeLabels = new Dictionary<string, OperatorLabelAnchor>
        {
            ["突击"] = new OperatorLabelAnchor { LabelX = 98, Count = 4 },
            ["支援"] = new OperatorLabelAnchor { LabelX = 563, Count = 4 },
            ["工程"] = new OperatorLabelAnchor { LabelX = 1027, Count = 5 },
            ["侦察"] = new OperatorLabelAnchor { LabelX = 1600, Count = 4 },
        },
    };

    [Fact]
    public void Compute_SupportVisible_InfersAssaultPosition()
    {
        // 支援@563（锚点处）→ 突击 = 563 - (4×111 + 空隙20) = 99（与实测锚点 98 差 1px，头像宽度内）
        var labels = new Dictionary<string, int> { ["支援"] = 563 };
        int? pred = OperatorPicker.ComputeTargetLabelX("突击", labels, Layout(), out var why);
        Assert.NotNull(pred);
        Assert.InRange(pred!.Value, 93, 103);
        Assert.Null(why);
    }

    [Fact]
    public void Compute_ScrolledState_StillAbsolute()
    {
        // 列表滚过：支援在 350 处被识别 → 突击 = 350 - 464 = -114 超出可视区 → 拒绝推算（该滚轮找回）
        var labels = new Dictionary<string, int> { ["支援"] = 350 };
        int? pred = OperatorPicker.ComputeTargetLabelX("突击", labels, Layout(), out var why);
        Assert.Null(pred);
        Assert.NotNull(why);
    }

    [Fact]
    public void Compute_ReconVisible_ComputesRightward()
    {
        // 目标在右侧：侦察@1600 被识别 → 工程 = 1600 - (5×111+20) = 1025（锚点 1027，差 2px）
        var labels = new Dictionary<string, int> { ["侦察"] = 1600 };
        int? pred = OperatorPicker.ComputeTargetLabelX("工程", labels, Layout(), out _);
        Assert.NotNull(pred);
        Assert.InRange(pred!.Value, 1020, 1032);
    }

    [Fact]
    public void Compute_MultipleLabels_MedianOfPredictions()
    {
        // 两个标签同时可见，推算应彼此印证（分歧 >40px 才拒绝）
        var labels = new Dictionary<string, int> { ["支援"] = 563, ["工程"] = 1027 };
        int? pred = OperatorPicker.ComputeTargetLabelX("突击", labels, Layout(), out _);
        Assert.NotNull(pred);
        Assert.InRange(pred!.Value, 93, 103);
    }

    [Fact]
    public void Compute_NewOperatorAdded_CountDrivenFollows()
    {
        // 更新友好场景：突击新增第 5 位干员 → 后续全部类型锚点整体右移 111（重标定后）：
        // 支援 563→674、工程 1027→1138、侦察 1600→1711；
        // 识别到支援@674 → 突击 = 674 - (5×111+20) = 99 ✓ 只改 count 与锚点，代码不动
        var layout = Layout();
        layout.TypeLabels["突击"].Count = 5;
        layout.TypeLabels["支援"].LabelX = 674;
        layout.TypeLabels["工程"].LabelX = 1138;
        layout.TypeLabels["侦察"].LabelX = 1711;
        var labels = new Dictionary<string, int> { ["支援"] = 674 };
        int? pred = OperatorPicker.ComputeTargetLabelX("突击", labels, layout, out _);
        Assert.NotNull(pred);
        Assert.InRange(pred!.Value, 93, 103);
    }

    [Fact]
    public void Compute_ConflictingLabels_Rejected()
    {
        // 支援@563 与 工程@877 推算分歧 >40px → 疑似误识别，拒绝
        var labels = new Dictionary<string, int> { ["支援"] = 563, ["工程"] = 877 };
        Assert.Null(OperatorPicker.ComputeTargetLabelX("突击", labels, Layout(), out _));
    }

    [Fact]
    public void Compute_NoAnchoredLabels_Rejected()
    {
        // 可见标签没有任何锚点（未知类型）→ 无法推算
        var labels = new Dictionary<string, int> { ["未知类型"] = 500 };
        Assert.Null(OperatorPicker.ComputeTargetLabelX("突击", labels, Layout(), out var why));
        Assert.NotNull(why);
    }
}
