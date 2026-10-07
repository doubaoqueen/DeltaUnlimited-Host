using OpenCvSharp;

namespace DeltaUnlimited.Vision.Ai;

/// <summary>模型评估器：对带 labels.csv 的图片目录跑三分类，输出混淆矩阵与 macro-F1（金标集门禁与 aieval 命令共用）。</summary>
public static class AiEvaluator
{
    public static readonly string[] ClassNames = { "passable", "blocked", "no_ground" };

    public sealed record EvalReport(
        int Total,                 // 实际评估成功的样本数（不含缺失/无法解码的行）
        int Skipped,               // 标注行存在但图片缺失/解码失败的数量（>0 时报告会提示）
        int[,] Confusion,          // [真实, 预测]
        double[] PerClassF1,
        double MacroF1,
        double MeanMs,
        double P99Ms);

    /// <summary>目录须含 labels.csv，每行：文件名,label[,地图,赛季,来源,created]；label ∈ passable/blocked/no_ground。
    /// 图片按尺寸自动分流：≥设计分辨率 = 全帧（走 PredictFull：归一化→裁 ROI→推理，与运行时同链路）；
    /// 小图 = aicollect 时代的预裁 ROI（直接缩放推理）。</summary>
    public static EvalReport Evaluate(PassabilitySensor sensor, string datasetDir, int designW, int designH)
    {
        string csvPath = Path.Combine(datasetDir, "labels.csv");
        if (!File.Exists(csvPath))
            throw new FileNotFoundException($"目录缺少 labels.csv: {datasetDir}");

        var rows = File.ReadAllLines(csvPath)
            .Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split(','))
            .Select(p => (File: p[0].Trim(), Label: p[1].Trim().ToLowerInvariant()))
            .Where(r => ClassNames.Contains(r.Label))
            .ToList();
        if (rows.Count == 0)
            throw new InvalidDataException($"labels.csv 无有效标注行: {csvPath}");

        int n = ClassNames.Length;
        var confusion = new int[n, n];
        var latencies = new List<double>();
        int evaluated = 0, skipped = 0;
        foreach (var (file, label) in rows)
        {
            string imagePath = Path.Combine(datasetDir, file);
            if (!File.Exists(imagePath)) imagePath = Path.Combine(datasetDir, label, file); // 兼容 子目录/label/文件名 布局
            using var img = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (img.Empty()) { skipped++; continue; }
            // 全帧必须走与运行时一致的 预处理→裁ROI 链路；直接 Predict 会把整屏压扁成模型输入，领域完全错位
            PassabilityResult result;
            if (img.Width >= designW && img.Height >= designH)
                result = sensor.PredictFull(img).Result;
            else
                result = sensor.Predict(img);
            latencies.Add(result.LatencyMs);
            confusion[Array.IndexOf(ClassNames, label), (int)result.Class]++;
            evaluated++;
        }
        if (evaluated == 0)
            throw new InvalidDataException($"labels.csv 中的图片全部缺失或无法解码: {datasetDir}");

        var perClassF1 = new double[n];
        for (int c = 0; c < n; c++)
        {
            double tp = confusion[c, c];
            double fp = 0, fn = 0;
            for (int i = 0; i < n; i++)
            {
                if (i != c) { fp += confusion[i, c]; fn += confusion[c, i]; }
            }
            perClassF1[c] = tp + fp + fn == 0 ? 1.0 : tp == 0 ? 0 : 2 * tp / (2 * tp + fp + fn);
        }

        latencies.Sort();
        double p99 = latencies.Count > 0 ? latencies[(int)Math.Min(latencies.Count - 1, latencies.Count * 0.99)] : 0;
        return new EvalReport(evaluated, skipped, confusion, perClassF1, perClassF1.Average(), latencies.Average(), p99);
    }

    /// <summary>控制台友好输出（aieval / 金标集门禁共用）。</summary>
    public static string FormatReport(EvalReport r)
    {
        var lines = new List<string>
        {
            $"样本 {r.Total} | macro-F1 {r.MacroF1:F3} | 平均延迟 {r.MeanMs:F2}ms | p99 {r.P99Ms:F2}ms",
            "混淆矩阵（行=真实, 列=预测 | " + string.Join(", ", ClassNames) + "）:",
        };
        for (int i = 0; i < ClassNames.Length; i++)
        {
            var row = Enumerable.Range(0, ClassNames.Length).Select(j => r.Confusion[i, j].ToString().PadLeft(6));
            lines.Add($"  {ClassNames[i],-10} {string.Join(' ', row)}   F1={r.PerClassF1[i]:F3}");
        }
        if (r.Skipped > 0)
            lines.Add($"  ⚠️ {r.Skipped} 行标注对应的图片缺失/无法解码，已排除在样本量之外（清理 labels.csv 或补回图片）");
        return string.Join(Environment.NewLine, lines);
    }
}
