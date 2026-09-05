using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>运行时标定参数 data/runtime.json：按本机环境与游戏内设置标定。</summary>
public sealed class RuntimeConfig
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("说明")]
    public string? Note { get; set; }

    /// <summary>游戏窗口标题关键字（所有命令默认使用）。</summary>
    [JsonPropertyName("window_keyword")]
    public string WindowKeyword { get; set; } = "三角洲行动";

    /// <summary>转 90° 需要发送的鼠标相对移动像素（EDPI = 鼠标DPI × 游戏灵敏度 决定，需标定）。</summary>
    [JsonPropertyName("px_per_90deg")]
    public double PxPer90Deg { get; set; } = 800;

    /// <summary>设计分辨率（元素坐标/模板/素材的统一基准），运行时按实际分辨率缩放。</summary>
    [JsonPropertyName("design_width")]
    public int DesignWidth { get; set; } = 1920;

    [JsonPropertyName("design_height")]
    public int DesignHeight { get; set; } = 1080;
}
