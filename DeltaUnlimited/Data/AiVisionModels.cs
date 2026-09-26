using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>AI 视觉感知模块配置 data/ai_vision.json。
/// 红线：模型仅作为视觉感知传感器，不持有决策权——所有移动/路径/安全控制权仍在 C# 状态机与人工。
/// 降级：enabled=false（或 DriftMonitor/金标集门禁自动翻转）即完全回退原始导航+贴墙守卫，无需改代码。</summary>
public sealed class AiVisionConfig
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("说明")]
    public string? Note { get; set; }

    /// <summary>总开关：一键关闭 = false（自动禁用机制翻转的也是它）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>ONNX 模型路径（相对仓库根）。</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "assets/ai/models/passability_v0_placebo.onnx";

    /// <summary>执行提供者：auto = 先试 DirectML(GPU) 失败回退 CPU；dml = 仅 DirectML；cpu = 仅 CPU。</summary>
    [JsonPropertyName("ep")]
    public string Ep { get; set; } = "auto";

    /// <summary>推理线程数（0 = ORT 默认）。实测台式机 4 最快且 p99 最稳；小模型别用默认全核。</summary>
    [JsonPropertyName("runtime_threads")]
    public int RuntimeThreads { get; set; } = 4;

    /// <summary>地面 ROI（设计坐标 1920×1080 基准 [x,y,w,h]）：x 560–1360 避开左右 HUD，y 640–1040 避开底部提示条。</summary>
    [JsonPropertyName("roi")]
    public List<int> Roi { get; set; } = new() { 560, 640, 800, 400 };

    /// <summary>模型输入尺寸 [height,width]（ROI 缩放目标），须与模型/训练一致。</summary>
    [JsonPropertyName("input_size")]
    public List<int> InputSize { get; set; } = new() { 112, 224 };

    /// <summary>置信度下限：低于它的帧滤波层按"弃权"处理（不推进窗口）。</summary>
    [JsonPropertyName("min_confidence")]
    public double MinConfidence { get; set; } = 0.70;

    [JsonPropertyName("filter")]
    public AiFilterConfig Filter { get; set; } = new();

    /// <summary>预警触发的避让限制（硬上限，防 AI 抖动）。enforce=false 时仅记录不动动作。</summary>
    [JsonPropertyName("avoid")]
    public AiAvoidConfig Avoid { get; set; } = new();

    [JsonPropertyName("drift")]
    public AiDriftConfig Drift { get; set; } = new();

    [JsonPropertyName("log")]
    public AiLogConfig Log { get; set; } = new();
}

/// <summary>时域滤波参数：window 帧内 ≥k 帧判 blocked 才升级预警（单帧误识别不触发）。</summary>
public sealed class AiFilterConfig
{
    [JsonPropertyName("window")]
    public int Window { get; set; } = 5;

    [JsonPropertyName("k")]
    public int K { get; set; } = 3;

    /// <summary>ROI 内帧差高于此值 = 移动物体（敌人/投掷物）进入 → 该帧压制不计入（动静分离）。</summary>
    [JsonPropertyName("motion_diff_threshold")]
    public double MotionDiffThreshold { get; set; } = 12.0;
}

/// <summary>避让动作硬上限：预警触发后只允许"松 W + 横移试探"，禁止大转向/跳/冲刺绕行。
/// enforce=true 时预警走既有 Escalate 路径（预测层提前拉动反应层守卫），false = 只记录不动动作。</summary>
public sealed class AiAvoidConfig
{
    [JsonPropertyName("enforce")]
    public bool Enforce { get; set; } = false;

    [JsonPropertyName("max_strafe_steps")]
    public int MaxStrafeSteps { get; set; } = 2;

    [JsonPropertyName("max_deviate_ms")]
    public int MaxDeviateMs { get; set; } = 1500;
}

/// <summary>分布漂移防线：金标集回放（赛季门禁）+ 运行时矛盾率监测（贴墙守卫 = 免费噪声标注员）。</summary>
public sealed class AiDriftConfig
{
    [JsonPropertyName("golden_set_dir")]
    public string GoldenSetDir { get; set; } = "assets/ai/golden";

    /// <summary>金标集 macro-F1 低于此值 → 启动期自动禁用（金标集为空则跳过门禁并告警）。</summary>
    [JsonPropertyName("min_macro_f1")]
    public double MinMacroF1 { get; set; } = 0.85;

    /// <summary>滑窗内矛盾率（预测 vs 守卫事实）超过此值 → 自动禁用。</summary>
    [JsonPropertyName("contradiction_rate_limit")]
    public double ContradictionRateLimit { get; set; } = 0.25;

    /// <summary>矛盾率统计窗口（分钟）与最少样本数（防小样本误触发）。</summary>
    [JsonPropertyName("window_minutes")]
    public int WindowMinutes { get; set; } = 10;

    [JsonPropertyName("min_samples")]
    public int MinSamples { get; set; } = 20;
}

/// <summary>双层日志：JSONL 永久层（无图）+ ROI 图像采样层（环形淘汰）。</summary>
public sealed class AiLogConfig
{
    /// <summary>背景采样频率：每 N 帧存一张未标注 ROI 作负样本池（0 = 关闭）。</summary>
    [JsonPropertyName("background_every")]
    public int BackgroundEvery { get; set; } = 50;

    [JsonPropertyName("roi_jpeg_quality")]
    public int RoiJpegQuality { get; set; } = 80;

    /// <summary>图像环形缓冲上限（MB），超限淘汰最旧（JSONL 不受限）。</summary>
    [JsonPropertyName("ring_mb")]
    public long RingMb { get; set; } = 2048;
}
