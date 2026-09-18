using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>具名区域表 data/zones.json：UI 惯例区域（设计分辨率 1920×1080 基准），elements/screens 按名引用。</summary>
public sealed class ZoneTable
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("说明")]
    public string? Note { get; set; }

    [JsonPropertyName("zones")]
    public Dictionary<string, List<int>> Zones { get; set; } = new();
}
