using DeltaUnlimited.Data;
using DeltaUnlimited.Vision.Ai;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>aieval：对带 labels.csv 的图片目录跑三分类评估（混淆矩阵/macro-F1/延迟）。
/// 用法: aieval [模型路径] [数据集目录]；缺省 = ai_vision.json 的模型 + 金标集目录。
/// 金标集目录跑分即"赛季门禁"离线版：macro-F1 < min_macro_f1 → 建议重训（运行期门禁在 AiVision.EnsureStarted 自动执行）。</summary>
public static class AiEvalCommand
{
    public static void Run(DataStore data, string repoRoot, string[] cmdArgs)
    {
        var cfg = data.LoadAiVision();
        var runtime = data.LoadRuntime();
        string modelRel = cmdArgs.Length > 1 ? cmdArgs[1] : cfg.Model;
        string dirRel = cmdArgs.Length > 2 ? cmdArgs[2] : cfg.Drift.GoldenSetDir;
        string modelPath = Path.Combine(repoRoot, modelRel);
        string datasetDir = Path.Combine(repoRoot, dirRel);

        if (!File.Exists(modelPath)) throw new FileNotFoundException($"模型不存在: {modelPath}");
        if (!Directory.Exists(datasetDir)) throw new DirectoryNotFoundException($"数据集目录不存在: {datasetDir}");

        Console.WriteLine($"AI 评估：模型 {modelRel} | 数据集 {dirRel}");
        using var runner = new OnnxRunner(modelPath, cfg.Ep, cfg.RuntimeThreads, cfg.InputSize[0], cfg.InputSize[1]);
        runner.Warmup();
        Console.WriteLine($"  执行提供者: {runner.Provider} | 输出类别数 {runner.OutputCount}");

        var sensor = new PassabilitySensor(runner, cfg, runtime.DesignWidth, runtime.DesignHeight);
        var report = AiEvaluator.Evaluate(sensor, datasetDir, runtime.DesignWidth, runtime.DesignHeight);
        Console.WriteLine(AiEvaluator.FormatReport(report));

        string verdict = report.MacroF1 >= cfg.Drift.MinMacroF1 ? "✅ PASS（可执行）"
            : report.MacroF1 >= cfg.Drift.MinMacroF1Observe ? "⚠️ OBSERVE-ONLY（降级仅观察）"
            : "❌ FAIL（禁用）";
        Console.WriteLine($"门禁判定: {verdict}（macro-F1 {report.MacroF1:F3} | 观察线 {cfg.Drift.MinMacroF1Observe} | 执行线 {cfg.Drift.MinMacroF1}）");
    }
}
