using System.Text.Encodings.Web;
using System.Text.Json;
using DeltaUnlimited.Data;
using DeltaUnlimited.Overlay;
using OpenCvSharp;

namespace DeltaUnlimited.Vision.Ai;

/// <summary>AI 视觉感知门面（阶段1：地面可通行性）。
/// 生命周期：EnsureStarted（懒加载、幂等）→ Observe（巡逻/链路每采样帧喂一帧）→ RecordGuardFired（守卫触发时回填地面真值）。
/// 红线：本模块只产出预警信号（PathWarning），永不直接驱动移动；enforce 语义 = 预测层提前拉动既有守卫，而非新决策逻辑。
/// 降级：金标集门禁失败 / 运行时矛盾率超限 / enabled=false → 完全回到原始守卫，零代码改动。</summary>
public static class AiVision
{
    private static readonly object Gate = new();
    private static bool _tried;                     // 初始化只尝试一次（失败不反复重建会话）
    private static AiVisionConfig _cfg = new();
    private static string _repoRoot = "";

    private static OnnxRunner? _runner;
    private static PassabilitySensor? _sensor;
    private static PassabilityFilter? _filter;
    private static DriftMonitor? _drift;
    private static AiLogSink? _sink;

    private static long _frames, _blockedWarnings, _contradictions;

    /// <summary>模块是否已就绪（初始化成功且未被自动禁用）。所有消费方据此判空。</summary>
    public static bool Available { get; private set; }

    /// <summary>预警是否作用于动作（false = 只记录不动动作，observe-only）。</summary>
    private static bool _observeOnly;   // 门禁降级标记：本会话强制仅观察

    /// <summary>enforce 总开关：可用 && 配置允许 && 门禁未降级。</summary>
    public static bool Enforce => Available && _cfg.Avoid.Enforce && !_observeOnly;

    public static string StatusText { get; private set; } = "AI 感知：未初始化";

    /// <summary>懒加载入口（幂等；失败后本进程内不再重试，避免每帧重建会话）。</summary>
    public static void EnsureStarted(DataStore data, string repoRoot)
    {
        if (Available || _tried) return;
        lock (Gate)
        {
            if (Available || _tried) return;
            _tried = true;
            _repoRoot = repoRoot;
            try
            {
                Start(data, repoRoot);
            }
            catch (Exception ex)
            {
                StatusText = $"AI 感知：初始化失败（{ex.Message}）——已回退原始守卫";
                Logger.Warn(StatusText);
                Console.WriteLine($"  ⚠️ {StatusText}");
            }
        }
    }

    private static void Start(DataStore data, string repoRoot)
    {
        var cfg = data.LoadAiVision();
        _cfg = cfg;
        if (!cfg.Enabled)
        {
            StatusText = "AI 感知：已关闭（ai_vision.enabled=false）";
            Console.WriteLine($"  ℹ {StatusText}");
            return;
        }

        string modelPath = Path.Combine(repoRoot, cfg.Model);
        if (!File.Exists(modelPath))
        {
            StatusText = $"AI 感知：模型文件不存在（{cfg.Model}），模块停用";
            Logger.Warn(StatusText);
            Console.WriteLine($"  ⚠️ {StatusText}");
            return;
        }

        var runtime = data.LoadRuntime();
        var runner = new OnnxRunner(modelPath, cfg.Ep, cfg.RuntimeThreads, cfg.InputSize[0], cfg.InputSize[1]);
        runner.Warmup();
        var sensor = new PassabilitySensor(runner, cfg, runtime.DesignWidth, runtime.DesignHeight);

        // 金标集门禁：赛季更新后模型失能的确定性检测（金标集为空则跳过并提示）
        var goldenDir = Path.Combine(repoRoot, cfg.Drift.GoldenSetDir);
        if (File.Exists(Path.Combine(goldenDir, "labels.csv")))
        {
            var report = AiEvaluator.Evaluate(sensor, goldenDir, runtime.DesignWidth, runtime.DesignHeight);
            Logger.Info($"金标集回放：{AiEvaluator.FormatReport(report)}");
            if (report.Total >= 10)
            {
                if (report.MacroF1 < cfg.Drift.MinMacroF1Observe)
                {
                    runner.Dispose();
                    StatusText = $"AI 感知：金标集 macro-F1 {report.MacroF1:F3} < 观察线 {cfg.Drift.MinMacroF1Observe}（疑似赛季漂移），自动禁用。请用新素材重训";
                    Logger.Warn(StatusText);
                    Console.WriteLine($"  ⛔ {StatusText}");
                    return;
                }
                if (report.MacroF1 < cfg.Drift.MinMacroF1)
                {
                    _observeOnly = true;
                    Console.WriteLine($"  ⚠️ 金标集 macro-F1 {report.MacroF1:F3} 达观察线但未达执行线 {cfg.Drift.MinMacroF1}：本会话强制仅观察（enforce 失效）");
                    Logger.Warn("金标门禁降级：仅观察模式");
                }
            }
        }
        else
        {
            Console.WriteLine("  ℹ AI 感知：金标集为空，赛季门禁跳过（用 aicollect 采集后建金标集）");
        }

        _runner = runner;
        _sensor = sensor;
        if (_observeOnly)
            StatusText += " | ⚠️ 仅观察（门禁降级）";
        _filter = new PassabilityFilter(cfg.Filter.Window, cfg.Filter.K, cfg.Filter.MotionDiffThreshold, cfg.MinConfidence);
        _drift = new DriftMonitor(cfg.Drift.ContradictionRateLimit, TimeSpan.FromMinutes(cfg.Drift.WindowMinutes), cfg.Drift.MinSamples);
        _sink = new AiLogSink(repoRoot, cfg.Log, cfg.Model, runner.Provider);
        Available = true;
        StatusText = $"AI 感知：已启用（{runner.Provider} | {Path.GetFileName(cfg.Model)} | ROI [{string.Join(',', cfg.Roi)}] | 滤波 {cfg.Filter.K}/{cfg.Filter.Window}）";
        Console.WriteLine($"  🤖 {StatusText}");
        Logger.Info(StatusText);
    }

    /// <summary>喂一帧（巡逻/链路的采样帧，原始分辨率即可），返回滤波后的预警。绝不抛异常，绝不返回 null 语义。</summary>
    public static PathWarning Observe(Mat rawFrame, double frameDiff)
    {
        if (!Available || _sensor is null || _filter is null || _sink is null) return PathWarning.None;
        try
        {
            var (result, roi) = _sensor.PredictFull(rawFrame, wantRoi: true);
            using (roi)
            {
                var warn = _filter.Push(result, frameDiff);
                long frame = Interlocked.Increment(ref _frames);
                if (warn.Level == PathWarningLevel.Blocked) Interlocked.Increment(ref _blockedWarnings);
                _sink.Log(result, frameDiff, warn.Level, roi, frame);
                return warn;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"AI 感知单帧失败（已跳过）: {ex.Message}");
            return PathWarning.None;
        }
    }

    /// <summary>守卫触发（贴墙/横移 Escalate）时回填地面真值：模型平静但守卫触发 = 假阴性矛盾（模型变瞎的主要漂移信号）。</summary>
    public static void RecordGuardFired()
    {
        if (!Available || _filter is null || _drift is null) return;
        bool modelCalm = _filter.Current == PathWarningLevel.None;
        _drift.Record(modelCalm);
        if (modelCalm) Interlocked.Increment(ref _contradictions);
        _filter.Reset(); // 守卫已处理地形，旧窗口状态作废
        if (_drift.ShouldDisable)
            Disable($"运行时矛盾率 {_drift.ContradictionRate:P0} 超阈值（窗口 {_cfg.Drift.WindowMinutes}min）——疑似赛季漂移，自动禁用");
    }

    /// <summary>自动禁用：内存停用 + 把 enabled=false 写回 ai_vision.json（与手动一键关闭同一开关，重启后保持关闭直到重训后人工恢复）。</summary>
    private static void Disable(string reason)
    {
        Available = false;
        StatusText = $"AI 感知：{reason}";
        Logger.Warn(StatusText);
        Console.WriteLine($"  ⛔ {StatusText}");
        try
        {
            _cfg.Enabled = false;
            string path = Path.Combine(_repoRoot, "data", "ai_vision.json");
            var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(path, JsonSerializer.Serialize(_cfg, opts));
        }
        catch (Exception ex)
        {
            Logger.Warn($"ai_vision.json 写回失败（内存中已禁用）: {ex.Message}");
        }
    }

    /// <summary>运行统计（巡逻收尾/状态栏展示用）。</summary>
    public static (long Frames, long BlockedWarnings, long Contradictions) SnapshotStats()
        => (Interlocked.Read(ref _frames), Interlocked.Read(ref _blockedWarnings), Interlocked.Read(ref _contradictions));
}
