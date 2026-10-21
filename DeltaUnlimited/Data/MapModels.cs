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

/// <summary>零号大坝点位表 data/zero_dam_points.json（2026-10-21 起为路线规划坐标底库）。
/// 背景：2026-09-26 曾禁止一切预设点位表；2026-10-21 用户决策变更——允许「出生点→撤离点」计划路线，
/// 本表提供官方坐标换算后的点位（来源：官网地图工具截图标注 + 自动提取，见 docs/小地图定位P0验证.md §8）。
/// 约束仍在：**路线是计划，感知是裁判**——执行时走位必须由实时感知兜底，点位表不得当作盲执行脚本；
/// 物资点（loot_points）仍不恢复。</summary>
public sealed class ZeroDamPoints
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 2;

    [JsonPropertyName("说明")]
    public string? Note { get; set; }

    /// <summary>出生点（14 个，2026-10-21 从用户标注的官网截图自动提取并人工核对）。</summary>
    [JsonPropertyName("spawn_points")]
    public List<MapPoint> SpawnPoints { get; set; } = new();

    /// <summary>撤离点（用户只标了常规 + 条件：随机撤离点费时间、电梯撤离点可能被堵且需开门）。
    /// 坐标为世界坐标；map_px 为官方底图（4096×3072）像素坐标，便于画图与路线对齐。</summary>
    [JsonPropertyName("extract_points")]
    public List<MapPoint> ExtractPoints { get; set; } = new();

    /// <summary>未映射字段兜底收纳（防静默丢弃，评审 P3-8 同类问题）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MapPoint
{
    /// <summary>短 id（如 S1 / E1），路线表引用用。</summary>
    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>点位类型：出生点 / 常规 / 条件 / 丢包 / 随机 / 电梯（撤离点用）。</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }

    /// <summary>世界坐标 X（与官网地图工具一致）。</summary>
    [JsonPropertyName("x")] public int X { get; set; }

    /// <summary>世界坐标 Y。</summary>
    [JsonPropertyName("y")] public int Y { get; set; }

    /// <summary>官方底图（4096×3072）像素坐标 [x, y]，供画图/路线对齐。</summary>
    [JsonPropertyName("map_px")] public List<int>? MapPx { get; set; }

    [JsonPropertyName("note")] public string? Note { get; set; }
}
