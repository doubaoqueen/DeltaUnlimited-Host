using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>干员选择预设 data/operator_presets.json（预留）：地图 -> 干员（类型+类型内序号）+ 布局锚点标定。
/// 选择方式：类型标签 OCR 锚定列 x，同类头像固定间距 → 点击 (label_x, first_y + index*spacing)。</summary>
public sealed class OperatorPresetTable
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("说明")]
    public string? Note { get; set; }

    /// <summary>地图名 -> 干员选择（type=突击/支援/工程/侦察，index=类型内序号 0 起）。</summary>
    [JsonPropertyName("map_operators")]
    public Dictionary<string, OperatorPick> MapOperators { get; set; } = new();

    /// <summary>布局标定：类型标签识别区/头像网格/出击按钮。</summary>
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

/// <summary>干员选择界面布局标定（待用户真机标定后填充；新增干员=类型内追加，已有序号不变）。</summary>
public sealed class OperatorLayout
{
    /// <summary>每列第一个头像中心 y。</summary>
    [JsonPropertyName("avatar_first_y")]
    public int AvatarFirstY { get; set; }

    /// <summary>同类型头像纵向间距（固定）。</summary>
    [JsonPropertyName("avatar_spacing")]
    public int AvatarSpacing { get; set; }

    /// <summary>出击按钮坐标。</summary>
    [JsonPropertyName("depart_button")]
    public OperatorPoint DepartButton { get; set; } = new();

    /// <summary>类型名 -> 标签锚点（ocr 区域 + 标签中心 x，头像列按 label_x 对齐）。</summary>
    [JsonPropertyName("type_labels")]
    public Dictionary<string, OperatorLabelAnchor> TypeLabels { get; set; } = new();
}

/// <summary>类型标签锚点。</summary>
public sealed class OperatorLabelAnchor
{
    /// <summary>OCR 识别区 [x,y,w,h]（区域路径，全帧会丢字）。</summary>
    [JsonPropertyName("region")]
    public List<int> Region { get; set; } = new();

    /// <summary>标签中心 x（该类型头像列的点击 x）。</summary>
    [JsonPropertyName("label_x")]
    public int LabelX { get; set; }
}

/// <summary>屏幕坐标点。</summary>
public sealed class OperatorPoint
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }
}
