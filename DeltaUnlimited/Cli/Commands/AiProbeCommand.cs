using DeltaUnlimited.Capture;
using DeltaUnlimited.Cli;
using DeltaUnlimited.Data;
using DeltaUnlimited.Vision;
using DeltaUnlimited.Vision.Ai;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>aiprobe：可通行性模型的"亲眼验证"探针。
/// 实时模式（默认，需游戏窗口）：抓画面逐帧判定——准星指平地/怼墙/看天，判定实时刷新；
/// 静态模式：`aiprobe <图片路径...>` 对整帧截图判定（自动裁 ROI，要求整帧为设计分辨率基准）。
/// 纯感知只读：不注入输入、不写运行日志、不影响 AiVision 的运行状态。</summary>
public static class AiProbeCommand
{
    public static void Run(DataStore data, string repoRoot, string[] cmdArgs)
    {
        var runtime = data.LoadRuntime();
        var cfg = data.LoadAiVision();
        string modelRel = cfg.Model;
        var images = new List<string>();
        for (int i = 1; i < cmdArgs.Length; i++)
        {
            if (cmdArgs[i] == "--model" && i + 1 < cmdArgs.Length) { modelRel = cmdArgs[++i]; continue; }
            if (!cmdArgs[i].StartsWith("--")) images.Add(cmdArgs[i]);   // 其余非开关参数 = 待判定图片
        }
        string modelPath = Path.IsPathRooted(modelRel) ? modelRel : Path.Combine(repoRoot, modelRel);
        if (!File.Exists(modelPath)) throw new FileNotFoundException($"模型不存在: {modelPath}");

        using var runner = new OnnxRunner(modelPath, cfg.Ep, cfg.RuntimeThreads, cfg.InputSize[0], cfg.InputSize[1]);
        runner.Warmup();
        var sensor = new PassabilitySensor(runner, cfg, runtime.DesignWidth, runtime.DesignHeight);
        Console.WriteLine($"AI 探针：{modelRel} | EP {runner.Provider} | ROI [{string.Join(',', cfg.Roi)}]");

        var images2 = images.ToArray();
        if (images2.Length > 0)
        {
            foreach (var rel in images2)
            {
                string abs = Path.IsPathRooted(rel) ? rel : Path.Combine(repoRoot, rel);
                using var frame = AiLabelImaging.LoadFrame(abs)
                    ?? throw new FileNotFoundException($"无法读取图片: {abs}");
                PrintVerdict(sensor, frame, Path.GetFileName(abs));
            }
            return;
        }

        IntPtr hwnd = CaptureService.FindWindowByTitle(runtime.WindowKeyword);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"没找到标题含 “{runtime.WindowKeyword}” 的窗口（或用 aiprobe <图片路径> 走静态模式）");
        CaptureService.LogWindowAnchor(hwnd, runtime.DesignWidth, runtime.DesignHeight);
        Console.WriteLine("实时判定中：把准星指向平地/墙/天空，看判定变化。Ctrl+C 或 q 结束");

        int frames = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!CommandUtil.StopRequested)
        {
            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q) break;
            using Mat raw = CaptureService.CaptureWindowMat(hwnd, raiseAndWait: false);
            var result = sensor.PredictFull(raw).Result; // PredictFull 内部已做 设计分辨率归一化→裁ROI（勿再自己 Normalize）
            frames++;
            Console.Write($"\r[{sw.Elapsed:mm\\:ss}] {result.Class} {result.Confidence * 100,5:0.0}%  " +
                          $"(blocked {result.Probs[1] * 100:0.0}% / no_ground {result.Probs[2] * 100:0.0}%)  " +
                          $"{result.LatencyMs:0.0}ms  已测 {frames} 帧 ");
            Thread.Sleep(400);
        }
        Console.WriteLine($"\n🏁 探针结束，共 {frames} 帧");
    }

    private static void PrintVerdict(PassabilitySensor sensor, Mat frame, string name)
    {
        var result = sensor.PredictFull(frame).Result;
        Console.WriteLine($"  {name,-46} → {result.Class} {result.Confidence * 100,5:0.0}%  " +
                          $"(blocked {result.Probs[1] * 100:0.0}% / no_ground {result.Probs[2] * 100:0.0}%)");
    }
}
