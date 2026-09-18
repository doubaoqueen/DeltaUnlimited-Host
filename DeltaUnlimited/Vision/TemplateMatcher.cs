using System.Collections.Concurrent;
using OpenCvSharp;

namespace DeltaUnlimited.Vision;

/// <summary>内存帧模板匹配（不落盘）：链路的 if_template 步骤与后续状态检测用。
/// 模板按路径缓存于内存，避免循环中反复读盘（#4）。</summary>
public static class TemplateMatcher
{
    private static readonly ConcurrentDictionary<string, Mat> TemplateCache = new();

    /// <summary>清空模板缓存并释放内存（程序退出/测试清理用）。</summary>
    public static void ClearCache()
    {
        foreach (var kv in TemplateCache)
            kv.Value.Dispose();
        TemplateCache.Clear();
    }

    /// <summary>在帧内找模板的最佳位置，返回是否命中（置信度 ≥ threshold）及中心点。
    /// region = [x, y, w, h] 可选：只在区域内搜索（更快、更抗干扰）。</summary>
    public static MatchResult Match(Mat frame, string templatePath, double threshold, int[]? region = null)
    {
        Mat tpl = LoadTemplate(templatePath);
        Mat? roi = null;
        Mat src = frame;
        int offX = 0, offY = 0;
        if (region is { Length: 4 })
        {
            int rx = Math.Clamp(region[0], 0, frame.Width - 1);
            int ry = Math.Clamp(region[1], 0, frame.Height - 1);
            int rw = Math.Clamp(region[2], 1, frame.Width - rx);
            int rh = Math.Clamp(region[3], 1, frame.Height - ry);
            roi = new Mat(frame, new Rect(rx, ry, rw, rh));
            src = roi;
            offX = rx;
            offY = ry;
        }

        if (tpl.Width >= src.Width || tpl.Height >= src.Height)
        {
            roi?.Dispose();
            throw new InvalidDataException($"模板 ({tpl.Width}x{tpl.Height}) 不小于搜索区域 ({src.Width}x{src.Height})，无法匹配");
        }

        // 实时帧是 BGRA(4通道)，模板是 BGR(3通道)：统一转成 BGR 再匹配
        Mat? conv = null;
        if (src.Channels() == 4)
        {
            conv = new Mat();
            Cv2.CvtColor(src, conv, ColorConversionCodes.BGRA2BGR);
            src = conv;
        }

        try
        {
            using var result = new Mat();
            Cv2.MatchTemplate(src, tpl, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);

            bool found = maxVal >= threshold;
            return new MatchResult(
                found,
                maxVal,
                found ? offX + maxLoc.X + tpl.Width / 2 : 0,
                found ? offY + maxLoc.Y + tpl.Height / 2 : 0,
                tpl.Width,
                tpl.Height);
        }
        finally
        {
            conv?.Dispose();
            roi?.Dispose();
        }
    }

    private static Mat LoadTemplate(string path)
    {
        return TemplateCache.GetOrAdd(path, p =>
        {
            var tpl = Cv2.ImRead(p, ImreadModes.Color);
            if (tpl.Empty())
                throw new FileNotFoundException($"无法读取模板图片: {p}");
            return tpl;
        });
    }
}
