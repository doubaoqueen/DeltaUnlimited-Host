using OpenCvSharp;

namespace DeltaUnlimited.Vision.Ai;

/// <summary>模型评估器：对带 labels.csv 的图片目录跑三分类，输出混淆矩阵与 macro-F1（金标集门禁与 aieval 命令共用）。</summary>
public static class AiEvaluator
{
    public static readonly string[] ClassNames = { "passable", "blocked", "no_ground" };

    public sealed record EvalReport(
        int Total,
        int[,] Confusion,          // [真实, 预测]
        double[] PerClassF1,
        double MacroF1,
        double MeanMs,
        double P99Ms);

    /// <summary>目录须含 labels.csv，每行：文件名,label[,地图,赛季,来源,created]；label ∈ passable/blocked/no_ground。</summary>
    public static EvalReport Evaluate(PassabilitySensor sensor, string datasetDir)
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
        foreach (var (file, label) in rows)
        {
            string imagePath = Path.Combine(datasetDir, file);
            if (!File.Exists(imagePath)) imagePath = Path.Combine(datasetDir, label, file); // 兼容 子目录/label/文件名 布局
            using var img = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (img.Empty()) continue;
            var result = sensor.Predict(img);
            latencies.Add(result.LatencyMs);
            confusion[Array.IndexOf(ClassNames, label), (int)result.Class]++;
        }

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
        return new EvalReport(rows.Count, confusion, perClassF1, perClassF1.Average(), latencies.Average(), p99);
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
        return string.Join(Environment.NewLine, lines);
    }
}
