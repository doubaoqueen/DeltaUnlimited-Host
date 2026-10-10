using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>物品价值表 data/item_values.json。</summary>
public sealed class ItemValuesTable
{
    [JsonPropertyName("说明")]
    public string? Note { get; set; }

    [JsonPropertyName("materials")]
    public List<ItemValue> Materials { get; set; } = new();

    /// <summary>未映射字段兜底收纳，不再被 System.Text.Json 静默丢弃（评审 P3-8）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ItemValue
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("value")] public int Value { get; set; }
    [JsonPropertyName("slots")] public int Slots { get; set; }
}

/// <summary>零号大坝撤离点表 data/zero_dam_points.json。
/// ⚠️ 只保留撤离点**元数据**：按跑刀方案 §1 的强制约束，搜刮点坐标表（loot_points）已废弃，
/// 生产链路禁止引用任何预设路线/点位表——搜刮靠现场感知，撤离靠地图标点 + HUD 标记跟随。</summary>
public sealed class ZeroDamPoints
{
    [JsonPropertyName("说明")]
    public string? Note { get; set; }

    [JsonPropertyName("extract_points")]
    public List<MapPoint> ExtractPoints { get; set; } = new();

    /// <summary>未映射字段兜底收纳（防静默丢弃，评审 P3-8 同类问题）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MapPoint
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }

    [JsonPropertyName("note")] public string? Note { get; set; }
}
