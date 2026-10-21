using OpenCvSharp;

namespace DeltaUnlimited.Vision.Localization;

/// <summary>定位候选：官方底图坐标 + 尺度 + NCC 分数。</summary>
public sealed record LocalizeCandidate(double Score, int ScalePx, int CenterX, int CenterY);

/// <summary>定位结果：是否命中 + 最佳候选 + 全部候选。</summary>
public sealed record LocalizeResult(bool Found, LocalizeCandidate? Best, IReadOnlyList<LocalizeCandidate> Candidates)
{
    public static readonly LocalizeResult Miss = new(false, null, Array.Empty<LocalizeCandidate>());
}

/// <summary>小地图定位器配置（预留 runtime.json 接入；当前由 CLI/测试直接构造）。
/// 小地图面板圆心/半径为用户 2026-10-21 口述四点标定（设计分辨率 1920×1080 坐标）。</summary>
public sealed class LocalizerConfig
{
    /// <summary>官方大地图路径（相对项目根；本机素材，不入库）。</summary>
    public string MapImage { get; set; } = "screenshots/mapoverview/zero_dam_tiles_z4.png";

    /// <summary>搜索时底图缩小倍数（2 = 2048x1536，兼顾速度与精度）。</summary>
    public int Downscale { get; set; } = 2;

    /// <summary>候选尺度：小地图内容对应到底图上的正方形边长（像素）。小地图缩放未知，故多尺度搜索。</summary>
    public int[] ScalesPx { get; set; } = { 150, 180, 210, 245, 285, 330, 380, 440 };

    /// <summary>最低有效分（CCOEFF_NORMED）。2026-10-21 校准：用户确认命中的最弱样本 0.515（m1a，scale 380），
    /// 故取 0.50。⚠️ 注意：重复地形的高分误报（实测 0.746）无法靠阈值区分——单帧分数不可当置信度。</summary>
    public double MinScore { get; set; } = 0.50;

    /// <summary>小地图圆形面板：圆心 X（设计分辨率坐标）。</summary>
    public int MiniCenterX { get; set; } = 165;

    /// <summary>小地图圆形面板：圆心 Y。</summary>
    public int MiniCenterY { get; set; } = 185;

    /// <summary>小地图圆形面板：半径。</summary>
    public int MiniRadius { get; set; } = 120;

    /// <summary>返回候选数量。</summary>
    public int TopK { get; set; } = 3;
}

/// <summary>小地图定位器：局内帧的圆形小地图 ↔ 官方大地图 地形匹配，还原玩家在地图上的位置（离线/半实时）。
/// 算法：圆形面板裁剪 → 灰度 → 多尺度模板匹配（CCOEFF_NORMED；圆外填模板均值以弱化 HUD 干扰）→ 取最高分尺度。
/// 已知局限（2026-10-21 P0 实测）：**NCC 高分 ≠ 正确**——集装箱堆场等重复地形会制造高分误报（实测 0.904 误报）；
/// 因此真实导航时必须叠加跨帧连续性/尺度先验约束，不可单帧独立采信。
/// 红线：纯离线图像分析（读图、匹配、输出坐标），不读内存、不注入进程、不碰游戏运行时。</summary>
public sealed class MinimapLocalizer : IDisposable
{
    private readonly LocalizerConfig _cfg;
    private readonly int _f;
    private readonly Mat _mapGray;      // 缩小灰度底图（搜索用）
    private readonly Mat _mapColor;     // 原尺寸彩色底图（预览用）

    public MinimapLocalizer(LocalizerConfig cfg, string root)
    {
        _cfg = cfg;
        _f = Math.Max(1, cfg.Downscale);

        string mapPath = Path.IsPathRooted(cfg.MapImage) ? cfg.MapImage : Path.Combine(root, cfg.MapImage);
        if (!File.Exists(mapPath))
            throw new FileNotFoundException($"定位底图不存在: {mapPath}（需先准备官方底图，见 docs/小地图定位P0验证.md）");

        _mapColor = Cv2.ImRead(mapPath, ImreadModes.Color);
        if (_mapColor.Empty()) throw new InvalidDataException($"底图读取失败: {mapPath}");

        using var gray = new Mat();
        Cv2.CvtColor(_mapColor, gray, ColorConversionCodes.BGR2GRAY);
        _mapGray = new Mat();
        Cv2.Resize(gray, _mapGray, new Size(_mapColor.Width / _f, _mapColor.Height / _f),
            interpolation: InterpolationFlags.Area);
    }

    public int MapWidth => _mapColor.Width;
    public int MapHeight => _mapColor.Height;
    public LocalizerConfig Config => _cfg;

    /// <summary>对一帧（已归一化到设计分辨率）定位。永不抛异常：异常时返回 Miss（上层按"未定位"处理）。</summary>
    public LocalizeResult Localize(Mat frame)
    {
        try
        {
            using var mini = CropMinimap(frame);
            if (mini.Empty()) return LocalizeResult.Miss;

            using var miniGray = new Mat();
            Cv2.CvtColor(mini, miniGray, ColorConversionCodes.BGR2GRAY);

            var hits = new List<LocalizeCandidate>();
            using var result = new Mat();
            foreach (int scale in _cfg.ScalesPx)
            {
                int small = Math.Max(40, scale / _f);
                using var tpl = BuildTemplate(miniGray, small);
                if (tpl.Width >= _mapGray.Width || tpl.Height >= _mapGray.Height) continue;

                Cv2.MatchTemplate(_mapGray, tpl, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                if (double.IsNaN(maxVal) || maxVal < _cfg.MinScore) continue;

                hits.Add(new LocalizeCandidate(maxVal, scale,
                    maxLoc.X * _f + scale / 2, maxLoc.Y * _f + scale / 2));
            }

            hits.Sort((a, b) => b.Score.CompareTo(a.Score));
            var top = hits.Take(Math.Max(1, _cfg.TopK)).ToArray();
            return top.Length == 0 ? LocalizeResult.Miss : new LocalizeResult(true, top[0], top);
        }
        catch
        {
            return LocalizeResult.Miss; // fail-open：定位失败不打断上层
        }
    }

    /// <summary>裁出圆形小地图面板（越界自动夹取）。</summary>
    public Mat CropMinimap(Mat frame)
    {
        int x = Math.Clamp(_cfg.MiniCenterX - _cfg.MiniRadius, 0, Math.Max(0, frame.Width - 1));
        int y = Math.Clamp(_cfg.MiniCenterY - _cfg.MiniRadius, 0, Math.Max(0, frame.Height - 1));
        int w = Math.Min(_cfg.MiniRadius * 2, frame.Width - x);
        int h = Math.Min(_cfg.MiniRadius * 2, frame.Height - y);
        if (w <= 0 || h <= 0) return new Mat();
        return new Mat(frame, new Rect(x, y, w, h)).Clone();
    }

    /// <summary>构造模板：缩放到 small×small，圆外像素填均值（模拟圆形掩膜，弱化 HUD 边角干扰）。</summary>
    private static Mat BuildTemplate(Mat miniGray, int small)
    {
        var tpl = new Mat();
        Cv2.Resize(miniGray, tpl, new Size(small, small), interpolation: InterpolationFlags.Linear);

        Scalar mean = Cv2.Mean(tpl);
        using var mask = new Mat(small, small, MatType.CV_8UC1, Scalar.All(255));
        Cv2.Circle(mask, new Point(small / 2, small / 2), small / 2 - 1, Scalar.All(0), -1);
        tpl.SetTo(mean, mask);   // 圆外填均值 = 在 CCOEFF 里贡献接近 0
        return tpl;
    }

    /// <summary>把定位结果画到底图副本上（预览/人工核验用）。</summary>
    public Mat RenderPreview(LocalizeResult result)
    {
        var canvas = _mapColor.Clone();
        int k = 1;
        foreach (var c in result.Candidates.Take(3))
        {
            var color = k == 1 ? new Scalar(0, 0, 255) : new Scalar(0, 255, 255);
            Cv2.Rectangle(canvas,
                new Rect(c.CenterX - c.ScalePx / 2, c.CenterY - c.ScalePx / 2, c.ScalePx, c.ScalePx),
                color, Math.Max(2, _mapColor.Width / 1000));
            Cv2.PutText(canvas, $"#{k} {c.Score:F2}", new Point(c.CenterX, c.CenterY),
                HersheyFonts.HersheySimplex, 1.2, color, 2);
            k++;
        }
        return canvas;
    }

    public void Dispose()
    {
        _mapGray.Dispose();
        _mapColor.Dispose();
    }
}
