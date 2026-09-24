using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>自动配装预设表 data/loadout_preset.json（预留，未启用）。
/// 等待攻略/建议确定方案后填充：workflows/enter_match.json 中“确认配装”点击前为插入点。</summary>
public sealed class LoadoutPresetTable
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("说明")]
    public string? Note { get; set; }

    [JsonPropertyName("presets")]
    public List<LoadoutPreset> Presets { get; set; } = new();

    /// <summary>配装策略（GUI 预留）：优先预设装配 or 优先从仓库携带 + 最高携带品质档位。</summary>
    [JsonPropertyName("policy")]
    public LoadoutPolicy Policy { get; set; } = new();
}

/// <summary>配装策略（GUI 预留）：数据槽，正式启用前字段可改。</summary>
public sealed class LoadoutPolicy
{
    /// <summary>preset=优先使用预设装配（制式套装等）；warehouse=优先从仓库携带道具（缺则跳过不补，规则见 docs/仓库交互手册.md）。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "preset";

    /// <summary>最高携带品质档位：白1/绿2/蓝3/紫4/金5/红6；0=不限。</summary>
    [JsonPropertyName("max_tier")]
    public int MaxTier { get; set; } = 3;
}

/// <summary>一套配装方案（草案字段，正式启用前可改）。</summary>
public sealed class LoadoutPreset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("budget_cap")]
    public int? BudgetCap { get; set; } // 预算上限（哈夫币）

    [JsonPropertyName("slots")]
    public List<LoadoutSlot> Slots { get; set; } = new();
}

/// <summary>单个槽位（主武器/副武器/护甲/头盔/背包/药品等）。</summary>
public sealed class LoadoutSlot
{
    [JsonPropertyName("slot")]
    public string Slot { get; set; } = "";

    [JsonPropertyName("item")]
    public string Item { get; set; } = ""; // 装备名（预设/搜索关键词）

    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = "ocr"; // 预留：预设装备名 / 模板 / ocr

    [JsonPropertyName("required")]
    public bool? Required { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}
