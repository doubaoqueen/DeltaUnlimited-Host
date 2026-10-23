using OpenCvSharp;

namespace DeltaUnlimited.Vision.Signals;

/// <summary>受击红屏检测（提案 §2 廉价档，零训练）：屏幕两侧边缘条的"红色主导像素"占比。
/// 公式与 K0 离线标定严格一致（2026-10-24，704 帧）：r≥90 且 r-g≥35 且 r-b≥35；
/// 两侧边缘条各宽 W/16，y 取 25%-75%（避开顶栏 HUD 与底部状态条）。
/// 标定分布：正常帧 p90≈0.10，受击尖峰 0.63-0.80；阈值范围落在间隙中带（见 DefaultSignals.HitFlash）。</summary>
public static class RedFlashDetector
{
    /// <summary>输入设计分辨率整帧（BGR，任意尺寸按比例取条），返回红色主导像素占比 0-1。纯读图，无状态。</summary>
    public static double EdgeRedRatio(Mat bgr)
    {
        int stripW = Math.Max(8, bgr.Width / 16);
        int y0 = bgr.Height / 4, y1 = bgr.Height * 3 / 4;
        long total = 0, red = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = 0; x < stripW; x++)
            {
                Count(bgr.At<Vec3b>(y, x));
                Count(bgr.At<Vec3b>(y, bgr.Width - 1 - x));
            }
        }
        return total == 0 ? 0 : red / (double)total;

        void Count(Vec3b px)   // BGR：px[2]=R
        {
            total++;
            int r = px[2], g = px[1], b = px[0];
            if (r >= 90 && r - g >= 35 && r - b >= 35) red++;
        }
    }
}

/// <summary>受击红屏监视器：占比统计（RedFlashDetector）+ 防抖判定（RatioDetector，连续 N 帧且阈值随机取样）。
/// 伺服循环每周期喂一帧设计分辨率图，触发即应走 Danger 结局（红线：受击交人工，不自动逃跑）。</summary>
public sealed class HitFlashMonitor
{
    private readonly RatioDetector _detector;

    public HitFlashMonitor(SignalSpec spec, Random rng) => _detector = new(spec, rng);

    public bool Update(Mat designFrame) => _detector.Update(RedFlashDetector.EdgeRedRatio(designFrame));

    public void Reset() => _detector.Reset();
}
