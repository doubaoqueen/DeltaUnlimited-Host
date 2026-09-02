using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>元素识别表 data/elements.json（schema 与 Python 工作区一致，见 docs/C#重构指南.md）。</summary>
public sealed class ElementsTable
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("说明")]
    public string? Note { get; set; }

    [JsonPropertyName("elements")]
    public Dictionary<string, ElementDefinition> Elements { get; set; } = new();
}

/// <summary>单个界面元素定义：识别方式（策略）在此切换，流程不用改。</summary>
public sealed class ElementDefinition
{
    /// <summary>coord | template | ocr | color</summary>
    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = "coord";

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("params")]
    public ElementParams Params { get; set; } = new();
}

/// <summary>各策略的参数：coord(x/y)、template(template/threshold)、ocr(keywords)、color(r/g/b)。</summary>
public sealed class ElementParams
{
    [JsonPropertyName("x")] public int? X { get; set; }
    [JsonPropertyName("y")] public int? Y { get; set; }
    [JsonPropertyName("template")] public string? Template { get; set; }
    [JsonPropertyName("threshold")] public double? Threshold { get; set; }
    [JsonPropertyName("keywords")] public List<string>? Keywords { get; set; }
    [JsonPropertyName("region")] public List<int>? Region { get; set; }
    [JsonPropertyName("r")] public int? R { get; set; }
    [JsonPropertyName("g")] public int? G { get; set; }
    [JsonPropertyName("b")] public int? B { get; set; }
    [JsonPropertyName("tolerance")] public int? Tolerance { get; set; }
    [JsonPropertyName("min_ratio")] public double? MinRatio { get; set; }
}
