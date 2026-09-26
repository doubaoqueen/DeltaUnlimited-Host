using DeltaUnlimited.Cli;
using DeltaUnlimited.Data;
using Xunit;

namespace DU.Tests;

/// <summary>干员选择锚点兜底推算（纯函数）：目标艺术字读不出时，用其他可见标签推算滚动偏移。</summary>
public class OperatorPickerTests
{
    // 锚点：突击98/支援563/工程1027/侦察1600（operator_presets.json 实测）
    private static Dictionary<string, OperatorLabelAnchor> Anchors() => new()
    {
        ["突击"] = new OperatorLabelAnchor { LabelX = 98, Count = 4 },
        ["支援"] = new OperatorLabelAnchor { LabelX = 563, Count = 4 },
        ["工程"] = new OperatorLabelAnchor { LabelX = 1027, Count = 5 },
        ["侦察"] = new OperatorLabelAnchor { LabelX = 1600, Count = 4 },
    };

    [Fact]
    public void Predict_SupportVisibleAtAnchor_InfersInitialState()
    {
        // 支援@563 = 锚点 → 偏移 0 → 突击应在 98（初始态锚点兜底）
        var labels = new Dictionary<string, int> { ["支援"] = 563 };
        Assert.Equal(98, OperatorPicker.PredictTargetLabelX("突击", labels, Anchors(), out var why));
        Assert.Null(why);
    }

    [Fact]
    public void Predict_MultipleLabelsAgree_MedOffset()
    {
        // 支援@533、工程@997：偏移均 -30 → 突击预测 98-30=68
        var labels = new Dictionary<string, int> { ["支援"] = 533, ["工程"] = 997 };
        Assert.Equal(68, OperatorPicker.PredictTargetLabelX("突击", labels, Anchors(), out _));
    }

    [Fact]
    public void Predict_ScrolledFarLeft_TargetOffscreen_ReturnsNull()
    {
        // 滚到底：侦察@1393（偏移 -207）→ 突击预测 -109 越界 → 不预测（走滚轮找回）
        var labels = new Dictionary<string, int> { ["侦察"] = 1393 };
        Assert.Null(OperatorPicker.PredictTargetLabelX("突击", labels, Anchors(), out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Predict_ConflictingOffsets_ReturnsNull()
    {
        // 支援@563（偏移 0）与工程@877（偏移 -150）分歧 >40px → 不信任，不预测
        var labels = new Dictionary<string, int> { ["支援"] = 563, ["工程"] = 877 };
        Assert.Null(OperatorPicker.PredictTargetLabelX("突击", labels, Anchors(), out _));
    }

    [Fact]
    public void Predict_PositiveOffset_ReturnsNull()
    {
        // 标签不会右移：支援@663（偏移 +100）→ 疑似误识别，不预测
        var labels = new Dictionary<string, int> { ["支援"] = 663 };
        Assert.Null(OperatorPicker.PredictTargetLabelX("突击", labels, Anchors(), out _));
    }
}
