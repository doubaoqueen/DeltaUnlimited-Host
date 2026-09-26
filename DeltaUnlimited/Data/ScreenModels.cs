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

/// <summary>单个界面定义：任一标记命中即判定为该界面（置信度取命中标记的最高值，见 ScreenDetector）。
/// 这使"主标记 + 兜底标记"分层自然成立；与 screens.json 的"说明"一致。</summary>
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

    /// <summary>自动关闭键：检测到该界面（弹层类）时自动按下该键关闭并重新识别（如 space_continue → "space"）。
    /// 用于"空格继续"类通用弹层（仓库升级完成/广告/任务领取等），避免链路被非主线弹层卡死。</summary>
    [JsonPropertyName("dismiss")]
    public string? Dismiss { get; set; }
}

/// <summary>识别标记：type = template / ocr（后续扩展 color）。
/// region = [x, y, w, h] 可选，只在该区域搜索；region_name = 引用 data/zones.json 的具名区。
/// require_all：ocr 标记默认任一关键词命中即可；设为 true 时区域内所有关键词都必须命中（防止关键词子串串台，
/// 如 "配装" 会命中配装界面的 "确认配装" 按钮，必须搭配 "出发" 一起要求才能锁定广场准备状态）。</summary>
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

    [JsonPropertyName("require_all")]
    public bool? RequireAll { get; set; }

    [JsonPropertyName("region")]
    public List<int>? Region { get; set; }

    [JsonPropertyName("region_name")]
    public string? RegionName { get; set; }
}
