using OpenCvSharp;

namespace DeltaUnlimited.Vision;

/// <summary>帧工具：把运行时帧归一化到设计分辨率（模板/识别统一在 1920×1080 基准上进行）。</summary>
public static class FrameTools
{
    /// <summary>返回新 Mat（调用方负责 Dispose）：尺寸与设计分辨率一致则拷贝，否则缩放。</summary>
    public static Mat Normalize(Mat frame, int designW, int designH)
    {
        var m = new Mat();
        if (frame.Width == designW && frame.Height == designH)
        {
            frame.CopyTo(m);
        }
        else
        {
            Cv2.Resize(frame, m, new Size(designW, designH));
        }
        return m;
    }
}
