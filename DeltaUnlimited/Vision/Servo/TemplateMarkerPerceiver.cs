using DeltaUnlimited.Vision;
using OpenCvSharp;

namespace DeltaUnlimited.Vision.Servo;

/// <summary>导航标记感知器（follow_marker 的真实实现，K1 用）：
/// 每周期抓帧 → 归一化到设计分辨率 → 导航标记模板匹配 → 输出 ServoFrame。
/// 到达判据权威 = ArrivalPrompt：准星区 [F] 交互提示模板匹配（2026-10 技术债修复——
/// 之前 DistanceMeters 恒 -1 而 IsArrived 要求距离可读，Success 结局永远不可达）。
/// 距离仍不可读（DistanceMeters=-1），留作 K0 标定后的进度显示（不参与到达判定）。
/// 卡墙守卫用的 FrameDiff 在同一帧算好，无需另起采样。</summary>
public sealed class TemplateMarkerPerceiver : IServoPerceiver
{
    private readonly Func<Mat> _capture;      // 抓帧委托（真实=CaptureService；测试=假帧）
    private readonly string _templatePath;    // 导航标记模板（K0 标定产物）
    private readonly string? _promptTemplatePath;  // [F] 交互提示模板（到达判据权威；null=该判据关闭）
    private readonly int _designW;
    private readonly int _designH;
    private readonly double _threshold;
    private readonly int[]? _searchRegion;    // 标记搜索区域 [x,y,w,h]（K0 标定后可限定，省时抗干扰）
    private readonly int[]? _promptRegion;    // 提示搜索区域（准星附近小 ROI，K0 标定）
    private readonly Func<Mat, bool>? _dangerProbe;  // 危险信号探针（如 HitFlashMonitor.Update；null=无危险信号）
    private Mat? _prev;

    public TemplateMarkerPerceiver(
        Func<Mat> capture,
        string templatePath,
        int designW,
        int designH,
        double threshold = 0.8,
        int[]? searchRegion = null,
        string? promptTemplatePath = null,
        int[]? promptRegion = null,
        Func<Mat, bool>? dangerProbe = null)
    {
        _capture = capture;
        _templatePath = templatePath;
        _designW = designW;
        _designH = designH;
        _threshold = threshold;
        _searchRegion = searchRegion;
        _promptTemplatePath = promptTemplatePath;
        _promptRegion = promptRegion;
        _dangerProbe = dangerProbe;
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
        bool prompt = _promptTemplatePath is not null
                      && TemplateMatcher.Match(norm, _promptTemplatePath, _threshold, _promptRegion).Found;
        bool danger = _dangerProbe is not null && _dangerProbe(norm);   // 设计分辨率帧喂给信号探针
        return new ServoFrame(
            MarkerVisible: m.Found,
            MarkerOffsetPx: offsetPx,
            DistanceMeters: -1,                // 距离未标定前不可读（只影响进度显示，不影响到达）
            FrameDiff: diff,
            HitBlocked: false,                 // AI 可通行性预警 K1 后接（observe-only 起步）
            ArrivalPrompt: prompt,
            HitFlash: danger);
    }
}
