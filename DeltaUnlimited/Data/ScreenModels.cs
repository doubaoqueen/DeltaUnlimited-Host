using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>界面识别表 data/screens.json：每个界面的识别标记与可用操作（视觉世界观）。</summary>
public sealed class ScreenTable
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("说明")]
    public string? Note { get; set; }

    [JsonPropertyName("screens")]
    public Dictionary<string, ScreenDef> Screens { get; set; } = new();
}

/// <summary>单个界面定义：markers 全部命中即判定为该界面（分数取最差命中置信度）。</summary>
public sealed class ScreenDef
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; } // false = 显式禁用（识别器跳过，避免静默失败）

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("markers")]
    public List<ScreenMarker> Markers { get; set; } = new();

    [JsonPropertyName("actions")]
    public List<string> Actions { get; set; } = new();
}

/// <summary>识别标记：type = template / ocr（后续扩展 color）。
/// region = [x, y, w, h] 可选，只在该区域搜索；region_name = 引用 data/zones.json 的具名区。</summary>
public sealed class ScreenMarker
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "template";

    [JsonPropertyName("template")]
    public string? Template { get; set; }

    [JsonPropertyName("threshold")]
    public double? Threshold { get; set; }

    [JsonPropertyName("keywords")]
    public List<string>? Keywords { get; set; }

    [JsonPropertyName("region")]
    public List<int>? Region { get; set; }

    [JsonPropertyName("region_name")]
    public string? RegionName { get; set; }
}
