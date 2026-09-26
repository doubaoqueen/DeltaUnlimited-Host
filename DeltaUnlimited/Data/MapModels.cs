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

/// <summary>零号大坝点位表 data/zero_dam_points.json。</summary>
public sealed class ZeroDamPoints
{
    [JsonPropertyName("extract_points")]
    public List<MapPoint> ExtractPoints { get; set; } = new();

    [JsonPropertyName("loot_points")]
    public List<MapPoint> LootPoints { get; set; } = new();
}

public sealed class MapPoint
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
}
