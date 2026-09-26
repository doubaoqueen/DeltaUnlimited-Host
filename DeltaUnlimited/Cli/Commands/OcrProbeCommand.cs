using DeltaUnlimited.Data;
using DeltaUnlimited.Vision;
using DeltaUnlimited.Vision.Ocr;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>ocr：对一张截图做 OCR 诊断（引擎门控测试 + 关键词严格/候选匹配验证，ADR #10 的 4/5 验收工具）。</summary>
public static class OcrProbeCommand
{
    public static void Run(string repoRoot, string[] cmdArgs)
    {
        if (cmdArgs.Length < 2) throw new ArgumentException("用法: ocr <截图相对路径> [--region x,y,w,h] [关键词...]");
        string imageRel = cmdArgs[1];

        // 可选区域参数：与 ScreenDetector 真实路径一致（引擎内部裁剪 → 预处理 → 1.5x 放大）
        int[]? region = null;
        int argIdx = 2;
        if (argIdx < cmdArgs.Length && cmdArgs[argIdx].Equals("--region", StringComparison.OrdinalIgnoreCase))
        {
            if (cmdArgs.Length < argIdx + 5)
                throw new ArgumentException("--region 需要 4 个整数: --region x y w h");
            region = new[] { int.Parse(cmdArgs[argIdx + 1]), int.Parse(cmdArgs[argIdx + 2]), int.Parse(cmdArgs[argIdx + 3]), int.Parse(cmdArgs[argIdx + 4]) };
            argIdx += 5;
        }
        var keywords = cmdArgs.Skip(argIdx).ToList();

        if (Ocr.Engine is null || !Ocr.Engine.IsAvailable)
            throw new InvalidOperationException(Ocr.Engine?.FailureReason ?? "OCR 引擎未初始化");

        using var img = Cv2.ImRead(Path.Combine(repoRoot, imageRel), ImreadModes.Color);
        if (img.Empty()) throw new FileNotFoundException($"图片读取失败: {imageRel}");
        var runtime = new DataStore(repoRoot).LoadRuntime(); // 探针与真实点击路径用同一设计分辨率，避免结论分叉（评审 P2-12）
        using var norm = FrameTools.Normalize(img, runtime.DesignWidth, runtime.DesignHeight);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var words = Ocr.Engine.Recognize(norm, region);
        sw.Stop();

        string regionDesc = region is null ? "全帧" : $"区域[{region[0]},{region[1]},{region[2]},{region[3]}]";
        Console.WriteLine($"OCR 耗时 {sw.ElapsedMilliseconds}ms（{regionDesc}），识别 {words.Count} 词:");
        foreach (var w in words)
            Console.WriteLine($"  “{w.Text}” @ ({w.X},{w.Y}) {w.W}x{w.H}");

        foreach (var kw in keywords)
        {
            var m = OcrTextMatcher.Match(words, kw);
            string hint = m.CandidateHit && !string.IsNullOrEmpty(m.CandidateHint)
                ? $"  混淆提示: 可能与 “{m.CandidateHint}” 混淆，建议人工确认"
                : "";
            Console.WriteLine($"关键词 “{kw}”: 严格={(m.StrictHit ? "命中 ✅" : "未命中")} 候选={(m.CandidateHit ? "出现" : "无")}{(m.StrictHit ? $" @ ({m.X},{m.Y}) {m.W}x{m.H}" : "")}{hint}");
        }
    }
}
