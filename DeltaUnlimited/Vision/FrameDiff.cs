using OpenCvSharp;

namespace DeltaUnlimited.Vision;

/// <summary>
/// 帧差检测：两帧的平均绝对像素差（0–255），用于判断"画面是否发生了变化"。
/// 用途：验证移动/转向等动作是否生效；后续可升级为状态切换检测（阈值可调）。
/// </summary>
public static class FrameDiff
{
    /// <summary>两帧平均绝对差（BGR 三通道平均）。尺寸不一致时先把 b 缩放到 a 的尺寸。</summary>
    public static double Score(Mat a, Mat b)
    {
        if (a.Empty() || b.Empty()) throw new ArgumentException("帧差检测需要两张非空帧");

        Mat? resized = null;
        Mat bb = b;
        if (a.Width != b.Width || a.Height != b.Height)
        {
            resized = new Mat();
            Cv2.Resize(b, resized, new Size(a.Width, a.Height));
            bb = resized;
        }

        try
        {
            using var diff = new Mat();
            Cv2.Absdiff(a, bb, diff);
            Scalar m = Cv2.Mean(diff);
            return (m[0] + m[1] + m[2]) / 3.0;
        }
        finally
        {
            resized?.Dispose();
        }
    }
}
