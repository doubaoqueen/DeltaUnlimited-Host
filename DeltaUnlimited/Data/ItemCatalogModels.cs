using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>物品库 data/item_catalog.json（预留，未启用）：战前携带/局内拾取决策的知识底座。
/// 增量录入：打游戏看到什么录什么。识别管线：OCR 名称为主 → 格子背景色判品质 → 模板/ORB 兜底。</summary>
public sealed class ItemCatalogTable
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("说明")]
    public string? Note { get; set; }

    [JsonPropertyName("items")]
    public List<ItemCatalogEntry> Items { get; set; } = new();
}

/// <summary>单条物品定义（草案字段，正式启用前可改）。</summary>
public sealed class ItemCatalogEntry
{
    /// <summary>物品名（OCR 主关键词）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>识别关键词变体（OCR 读数不稳定时补充）。</summary>
    [JsonPropertyName("ocr_keywords")]
    public List<string> OcrKeywords { get; set; } = new();

    /// <summary>类型：长枪/手枪/头盔/护甲/胸挂/背包/药品/弹药/材料等。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>品质档位：白/绿/蓝/紫/金/红（对应格子背景色）。</summary>
    [JsonPropertyName("tier")]
    public string Tier { get; set; } = "";

    /// <summary>占格数：宽。</summary>
    [JsonPropertyName("grid_w")]
    public int? GridW { get; set; }

    /// <summary>占格数：高。</summary>
    [JsonPropertyName("grid_h")]
    public int? GridH { get; set; }

    /// <summary>参考价值（哈夫币，供预算与将来出售自动化用）。</summary>
    [JsonPropertyName("value")]
    public int? Value { get; set; }

    /// <summary>局内是否值得拾取（拾取决策用）。</summary>
    [JsonPropertyName("pick")]
    public bool? Pick { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}
