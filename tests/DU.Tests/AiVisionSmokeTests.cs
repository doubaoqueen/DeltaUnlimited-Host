using DeltaUnlimited.Data;
using DeltaUnlimited.Vision.Ai;
using OpenCvSharp;
using Xunit;

namespace DU.Tests;

/// <summary>AI 感知管线冒烟：真实 ONNX 会话（placebo 模型，随机权重）走 通 会话创建→GPU/CPU EP→推理→softmax；
/// 传感器走通 归一化→ROI 裁剪→预处理→推理全链路。模型文件已入库，测试稳定。</summary>
public class AiVisionSmokeTests
{
    private static string ModelPath()
    {
        string root = DataStore.FindRoot();
        string path = Path.Combine(root, "assets", "ai", "models", "passability_v0_placebo.onnx");
        Assert.True(File.Exists(path), $"placebo 模型缺失: {path}");
        return path;
    }

    [Fact]
    public void OnnxRunner_会话创建_推理_softmax归一()
    {
        using var runner = new OnnxRunner(ModelPath(), ep: "auto", threads: 2, inputH: 112, inputW: 224);
        runner.Warmup();

        var (probs, ms) = runner.Run(buf =>
        {
            for (int i = 0; i < buf.Length; i++) buf[i] = 0.01f * (i % 251);
        });

        Assert.Equal(3, probs.Length);
        Assert.Equal(1f, probs.Sum(), precision: 2);
        Assert.All(probs, p => Assert.True(p is >= 0f and <= 1f));
        Assert.True(ms < 1000, $"单帧推理过慢: {ms}ms（EP={runner.Provider}）");
    }

    [Fact]
    public void Sensor_合成帧全链路_ROI裁剪与三分类()
    {
        string root = DataStore.FindRoot();
        var data = new DataStore(root);
        var cfg = data.LoadAiVision();
        using var runner = new OnnxRunner(ModelPath(), "auto", 2, cfg.InputSize[0], cfg.InputSize[1]);
        runner.Warmup();
        var sensor = new PassabilitySensor(runner, cfg, designW: 1920, designH: 1080);

        using var frame = new Mat(1080, 1920, MatType.CV_8UC3);
        Cv2.Randu(frame, 0, 255); // 随机噪声帧：placebo 模型输出无意义，只验管线

        var (result, roi) = sensor.PredictFull(frame, wantRoi: true);
        using (roi)
        {
            Assert.NotNull(roi);
            Assert.Equal(cfg.Roi[2], roi!.Width);  // 设计分辨率 ROI 拷贝（800×400）
            Assert.Equal(cfg.Roi[3], roi.Height);
            Assert.Equal(3, result.Probs.Length);
            Assert.Equal(1f, result.Probs.Sum(), precision: 2);
            Assert.InRange(result.Class, PassabilityClass.Passable, PassabilityClass.NoGround);
        }
    }
}
