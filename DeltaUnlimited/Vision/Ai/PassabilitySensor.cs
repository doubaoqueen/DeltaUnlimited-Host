using DeltaUnlimited.Data;
using OpenCvSharp;

namespace DeltaUnlimited.Vision.Ai;

/// <summary>可通行性传感器：原始帧 → 设计分辨率归一化 → 裁 ROI → 缩放 → RGB/CHW/ImageNet 归一化 → ONNX 三分类。
/// 纯感知：不做任何决策，输出交 PassabilityFilter 滤波后由状态机消费。</summary>
public sealed class PassabilitySensor
{
    // torchvision ImageNet 均值/方差（RGB 通道序）；训练端 dataset.py 必须使用同一组常量
    public static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    public static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    private readonly OnnxRunner _runner;
    private readonly Rect _roiRect;      // 设计坐标 ROI
    private readonly int _designW, _designH;
    private readonly int _inputH, _inputW;

    public PassabilitySensor(OnnxRunner runner, AiVisionConfig cfg, int designW, int designH)
    {
        _runner = runner;
        _designW = designW;
        _designH = designH;
        _inputH = cfg.InputSize[0];
        _inputW = cfg.InputSize[1];
        int x = cfg.Roi[0], y = cfg.Roi[1], w = cfg.Roi[2], h = cfg.Roi[3];
        // ROI 越界裁剪（设计分辨率边界内），避免 SubMat 越界异常
        x = Math.Clamp(x, 0, designW - 1);
        y = Math.Clamp(y, 0, designH - 1);
        w = Math.Clamp(w, 1, designW - x);
        h = Math.Clamp(h, 1, designH - y);
        _roiRect = new Rect(x, y, w, h);
    }

    /// <summary>直接推理一张类 ROI 图（任意尺寸 BGR，如金标集/数据集图片），内部缩放到模型输入。</summary>
    public PassabilityResult Predict(Mat bgrImage)
    {
        var (probs, ms) = _runner.Run(buf => FillChw(buf, bgrImage));
        int cls = ArgMax(probs);
        return new PassabilityResult((PassabilityClass)cls, probs, probs[cls], ms);
    }

    /// <summary>全流程：原始窗口帧 → 归一化 → 裁 ROI → 推理。wantRoi=true 时返回设计分辨率 ROI 拷贝（供日志留证，调用方 Dispose）。</summary>
    public (PassabilityResult Result, Mat? Roi) PredictFull(Mat rawFrame, bool wantRoi = false)
    {
        using var norm = FrameTools.Normalize(rawFrame, _designW, _designH);
        using var roiCrop = new Mat(norm, _roiRect);
        Mat? roiCopy = wantRoi ? roiCrop.Clone() : null;
        try
        {
            return (Predict(roiCrop), roiCopy);
        }
        catch
        {
            roiCopy?.Dispose();
            throw;
        }
    }

    /// <summary>BGR 任意尺寸 → 缩放到模型输入 → RGB/CHW/ImageNet 归一化填入缓冲。</summary>
    private void FillChw(float[] buffer, Mat bgr)
    {
        int plane = _inputH * _inputW;
        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size(_inputW, _inputH));
        using var rgb = new Mat();
        Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);
        Mat[] channels = Cv2.Split(rgb);
        try
        {
            for (int c = 0; c < 3; c++)
            {
                using var f32 = new Mat();
                channels[c].ConvertTo(f32, MatType.CV_32F);
                f32.GetArray(out float[] data);
                int offset = c * plane;
                for (int i = 0; i < plane; i++)
                    buffer[offset + i] = (data[i] / 255f - Mean[c]) / Std[c];
            }
        }
        finally
        {
            foreach (var m in channels) m.Dispose();
        }
    }

    private static int ArgMax(float[] probs)
    {
        int best = 0;
        for (int i = 1; i < probs.Length; i++)
            if (probs[i] > probs[best]) best = i;
        return best;
    }
}
