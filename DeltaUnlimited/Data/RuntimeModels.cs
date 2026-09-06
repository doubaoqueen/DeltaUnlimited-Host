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

    /// <summary>转 90° 需要发送的鼠标相对移动像素（EDPI = 鼠标DPI × 游戏灵敏度 决定，需标定）。
    /// 0 = 未标定（启动时警告）。</summary>
    [JsonPropertyName("px_per_90deg")]
    public double PxPer90Deg { get; set; } = 0;

    /// <summary>设计分辨率（元素坐标/模板/素材的统一基准），运行时按实际分辨率缩放。</summary>
    [JsonPropertyName("design_width")]
    public int DesignWidth { get; set; } = 1920;

    [JsonPropertyName("design_height")]
    public int DesignHeight { get; set; } = 1080;

    /// <summary>拟人化参数（缺失时使用默认值）。</summary>
    [JsonPropertyName("humanizer")]
    public HumanizerConfig? Humanizer { get; set; }
}

/// <summary>拟人化参数：输入层所有随机范围的配置（调参不改代码）。</summary>
public sealed class HumanizerConfig
{
    // 点击：到达后的停顿
    [JsonPropertyName("click_pause_min")] public int ClickPauseMin { get; set; } = 150;
    [JsonPropertyName("click_pause_max")] public int ClickPauseMax { get; set; } = 320;
    // 点击：按下到抬起
    [JsonPropertyName("press_hold_min")] public int PressHoldMin { get; set; } = 100;
    [JsonPropertyName("press_hold_max")] public int PressHoldMax { get; set; } = 220;
    // 按键：短按按下时长
    [JsonPropertyName("key_tap_min")] public int KeyTapMin { get; set; } = 35;
    [JsonPropertyName("key_tap_max")] public int KeyTapMax { get; set; } = 90;
    // 组合键：依次按下的间隔
    [JsonPropertyName("key_gap_min")] public int KeyGapMin { get; set; } = 15;
    [JsonPropertyName("key_gap_max")] public int KeyGapMax { get; set; } = 30;
    // 鼠标缓动：每步间隔
    [JsonPropertyName("mouse_step_delay_min")] public int MouseStepDelayMin { get; set; } = 3;
    [JsonPropertyName("mouse_step_delay_max")] public int MouseStepDelayMax { get; set; } = 11;
    // 鼠标缓动：额外分段数范围
    [JsonPropertyName("step_extra_min")] public int StepExtraMin { get; set; } = 3;
    [JsonPropertyName("step_extra_max")] public int StepExtraMax { get; set; } = 8;
    // 鼠标缓动：每步幅度抖动系数
    [JsonPropertyName("step_jitter_min")] public double StepJitterMin { get; set; } = 0.8;
    [JsonPropertyName("step_jitter_max")] public double StepJitterMax { get; set; } = 1.2;
    // 长按：时长抖动系数
    [JsonPropertyName("hold_jitter_min")] public double HoldJitterMin { get; set; } = 0.92;
    [JsonPropertyName("hold_jitter_max")] public double HoldJitterMax { get; set; } = 1.08;
}
