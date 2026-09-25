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

    /// <summary>等待类步骤的轮询间隔（ms）。优化阶段先保持 600；真机量到单次识别 &lt;1.5s 后再降到 300-400。</summary>
    [JsonPropertyName("poll_interval_ms")]
    public int PollIntervalMs { get; set; } = 600;

    /// <summary>设计分辨率（元素坐标/模板/素材的统一基准），运行时按实际分辨率缩放。</summary>
    [JsonPropertyName("design_width")]
    public int DesignWidth { get; set; } = 1920;

    [JsonPropertyName("design_height")]
    public int DesignHeight { get; set; } = 1080;

    /// <summary>OCR 引擎选择：windows（默认）/ paddle（门控失败后接入）。</summary>
    [JsonPropertyName("ocr_engine")]
    public string OcrEngine { get; set; } = "windows";

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
    // 鼠标轨迹拟人化（弧线/缓动/过冲/抖动）
    [JsonPropertyName("mouse")] public MousePathConfig? Mouse { get; set; }
}

/// <summary>拟人鼠标路径参数（humanizer.mouse）：缺失时用默认值。</summary>
public sealed class MousePathConfig
{
    // 移动总时长上下限（ms，按距离缩放后夹取）
    [JsonPropertyName("min_duration_ms")] public int MinDurationMs { get; set; } = 250;
    [JsonPropertyName("max_duration_ms")] public int MaxDurationMs { get; set; } = 650;
    // 每像素追加时长（ms）：距离越远移动越慢，符合人手习惯
    [JsonPropertyName("per_pixel_ms")] public double PerPixelMs { get; set; } = 0.5;
    // 每步间隔（ms，实际按 ±30% 随机）
    [JsonPropertyName("step_interval_ms")] public int StepIntervalMs { get; set; } = 12;
    // 弧线最大曲率（法向偏移上限 px）
    [JsonPropertyName("curvature_max_px")] public int CurvatureMaxPx { get; set; } = 90;
    // 途中逐点抖动（px）
    [JsonPropertyName("jitter_px")] public int JitterPx { get; set; } = 2;
    // 过冲概率与幅度（冲过头再收回，px）
    [JsonPropertyName("overshoot_probability")] public double OvershootProbability { get; set; } = 0.18;
    [JsonPropertyName("overshoot_px_min")] public int OvershootPxMin { get; set; } = 5;
    [JsonPropertyName("overshoot_px_max")] public int OvershootPxMax { get; set; } = 22;
}
