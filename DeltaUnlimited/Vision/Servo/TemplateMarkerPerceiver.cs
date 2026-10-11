using DeltaUnlimited.Vision;
using OpenCvSharp;

namespace DeltaUnlimited.Vision.Servo;

/// <summary>导航标记感知器（follow_marker 的真实实现，K1 用）：
/// 每周期抓帧 → 归一化到设计分辨率 → 导航标记模板匹配 → 输出 ServoFrame。
/// 距离暂不可读（DistanceMeters=-1）：到达判定 K0 标定后按
/// "距离数字可 OCR → 数字阈值；不可 → 模板尺寸阈值"二选一（提案 §3/§9-4）。
/// 卡墙守卫用的 FrameDiff 在同一帧算好，无需另起采样。</summary>
public sealed class TemplateMarkerPerceiver : IServoPerceiver
{
    private readonly Func<Mat> _capture;      // 抓帧委托（真实=CaptureService；测试=假帧）
    private readonly string _templatePath;    // 导航标记模板（K0 标定产物）
    private readonly int _designW;
    private readonly int _designH;
    private readonly double _threshold;
    private readonly int[]? _searchRegion;    // 标记搜索区域 [x,y,w,h]（K0 标定后可限定，省时抗干扰）
    private Mat? _prev;

    public TemplateMarkerPerceiver(
        Func<Mat> capture,
        string templatePath,
        int designW,
        int designH,
        double threshold = 0.8,
        int[]? searchRegion = null)
    {
        _capture = capture;
        _templatePath = templatePath;
        _designW = designW;
        _designH = designH;
        _threshold = threshold;
        _searchRegion = searchRegion;
    }

    public ServoFrame Observe()
    {
        using var raw = _capture();
        using var norm = FrameTools.Normalize(raw, _designW, _designH);

        double diff = _prev is null ? 0 : FrameDiff.Score(_prev, norm);
        _prev?.Dispose();
        _prev = norm.Clone();                  // norm 随 using 释放，帧差参考帧单独持有

        var m = TemplateMatcher.Match(norm, _templatePath, _threshold, _searchRegion);
        double offsetPx = m.Found ? m.CenterX - _designW / 2.0 : 0;
        return new ServoFrame(
            MarkerVisible: m.Found,
            MarkerOffsetPx: offsetPx,
            DistanceMeters: -1,                // 距离未标定前不可读
            FrameDiff: diff,
            HitBlocked: false);                // AI 可通行性预警 K1 后接（observe-only 起步）
    }
}
