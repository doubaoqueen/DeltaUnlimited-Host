using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>干员选择预设 data/operator_presets.json：地图 -> 干员（类型+类型内序号）+ 布局标定。
/// 选择方式：运行时 OCR 标签带找类型标签中心 → 头像点 = (标签x + first_avatar_offset + index*avatar_spacing, avatar_first_y)。
/// ⚠️ 无出击按钮：右上角 15s 倒计时自动开局。</summary>
public sealed class OperatorPresetTable
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("说明")]
    public string? Note { get; set; }

    /// <summary>地图名（或 "default"）-> 干员选择（type=突击/支援/工程/侦察，index=类型内序号 0 起）。</summary>
    [JsonPropertyName("map_operators")]
    public Dictionary<string, OperatorPick> MapOperators { get; set; } = new();

    /// <summary>布局标定：类型标签/头像网格/滚轮区。</summary>
    [JsonPropertyName("layout")]
    public OperatorLayout Layout { get; set; } = new();
}

/// <summary>一次干员选择。</summary>
public sealed class OperatorPick
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>干员选择界面布局标定（2026-09-24 用户实测；滚动位移无需标定——靠标签定位）。</summary>
public sealed class OperatorLayout
{
    /// <summary>每列第一个头像中心 y（实测 873）。</summary>
    [JsonPropertyName("avatar_first_y")]
    public int AvatarFirstY { get; set; } = 873;

    /// <summary>同类型头像横向间距（固定，实测 111）。</summary>
    [JsonPropertyName("avatar_spacing")]
    public int AvatarSpacing { get; set; } = 111;

    /// <summary>类型标签中心 x → 该类型第一个头像中心 x 的偏移（实测 ≈33）。</summary>
    [JsonPropertyName("first_avatar_offset")]
    public int FirstAvatarOffset { get; set; } = 33;

    /// <summary>滚轮生效区域（光标必须在此区域内）：[x,y,w,h]。</summary>
    [JsonPropertyName("avatar_area")]
    public List<int> AvatarArea { get; set; } = new() { 79, 787, 1734, 152 };

    /// <summary>标签带 OCR 识别区 [x,y,w,h]（覆盖滚动后的全部标签位置）。</summary>
    [JsonPropertyName("label_strip")]
    public List<int> LabelStrip { get; set; } = new() { 30, 730, 1860, 90 };

    /// <summary>类型名 -> 标签锚点（label_x=初始标签中心 x，供越界判断/兜底；count=该类型干员数）。</summary>
    [JsonPropertyName("type_labels")]
    public Dictionary<string, OperatorLabelAnchor> TypeLabels { get; set; } = new();

    /// <summary>按标签锚点计算头像点击点（纯函数，可单测）。</summary>
    public (int X, int Y) ComputeAvatarPoint(OperatorLabelAnchor anchor, int index)
        => (anchor.LabelX + FirstAvatarOffset + index * AvatarSpacing, AvatarFirstY);
}

/// <summary>类型标签锚点。</summary>
public sealed class OperatorLabelAnchor
{
    /// <summary>OCR 识别区 [x,y,w,h]（初始位置参考；实际用 label_strip 全带识别）。</summary>
    [JsonPropertyName("region")]
    public List<int> Region { get; set; } = new();

    /// <summary>初始标签中心 x（该类型头像列的点击 x 基准）。</summary>
    [JsonPropertyName("label_x")]
    public int LabelX { get; set; }

    /// <summary>该类型干员总数（序号越界检查用）。</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }
}
