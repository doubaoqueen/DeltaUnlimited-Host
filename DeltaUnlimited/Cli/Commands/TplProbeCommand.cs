using DeltaUnlimited.Data;
using DeltaUnlimited.Vision;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>tplprobe：对一张截图做模板匹配诊断（输出最佳匹配位置与置信度，供标定/验证模板用）。</summary>
public static class TplProbeCommand
{
    public static void Run(string repoRoot, string[] cmdArgs)
    {
        if (cmdArgs.Length < 3) throw new ArgumentException("用法: tplprobe <截图相对路径> <模板相对路径> [阈值(默认0.85)]");
        string imageRel = cmdArgs[1];
        string tplRel = cmdArgs[2];
        double threshold = cmdArgs.Length > 3 && double.TryParse(cmdArgs[3], out double t) ? t : 0.85;

        using var img = Cv2.ImRead(Path.Combine(repoRoot, imageRel), ImreadModes.Color);
        if (img.Empty()) throw new FileNotFoundException($"图片读取失败: {imageRel}");
        var runtime = new DataStore(repoRoot).LoadRuntime(); // 探针与真实点击路径用同一设计分辨率，避免结论分叉（评审 P2-12）
        using var norm = FrameTools.Normalize(img, runtime.DesignWidth, runtime.DesignHeight);

        var r = TemplateMatcher.Match(norm, Path.Combine(repoRoot, tplRel), threshold);
        Console.WriteLine($"模板 {tplRel} 在 {imageRel} 上（阈值 {threshold:F2}）:");
        Console.WriteLine($"  置信度 {r.Confidence:F3} → {(r.Found ? "命中 ✅" : "未命中 ❌")}  中心 @ ({r.CenterX},{r.CenterY})  模板 {r.W}x{r.H}");
    }
}
