using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>GUI 本地设置 data/gui_settings.json（记住上次用的工作流/模式/窗口位置）。
/// 属**本机个人设置**，已 gitignore——不入库、不参与发布数据；缺失或损坏时一律回退默认值。</summary>
public sealed class GuiSettings
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("last_workflow")]
    public string? LastWorkflow { get; set; }

    /// <summary>true = 全自动（跳过暂停点），false = 半自动（默认）。</summary>
    [JsonPropertyName("auto_mode")]
    public bool AutoMode { get; set; }

    [JsonPropertyName("window_x")]
    public int? WindowX { get; set; }

    [JsonPropertyName("window_y")]
    public int? WindowY { get; set; }

    [JsonPropertyName("window_w")]
    public int? WindowW { get; set; }

    [JsonPropertyName("window_h")]
    public int? WindowH { get; set; }
}
