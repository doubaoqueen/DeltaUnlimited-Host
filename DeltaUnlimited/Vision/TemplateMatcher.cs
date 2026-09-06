using OpenCvSharp;

namespace DeltaUnlimited.Vision;

/// <summary>内存帧模板匹配（不落盘）：链路的 if_template 步骤与后续状态检测用。</summary>
public static class TemplateMatcher
{
    public sealed record MatchResult(bool Found, double Confidence, int CenterX, int CenterY, int W, int H);

    /// <summary>在帧内找模板的最佳位置，返回是否命中（置信度 ≥ threshold）及中心点。</summary>
    public static MatchResult Match(Mat frame, string templatePath, double threshold)
    {
        using var tpl = Cv2.ImRead(templatePath, ImreadModes.Color);
        if (tpl.Empty())
            throw new FileNotFoundException($"无法读取模板图片: {templatePath}");
        if (tpl.Width >= frame.Width || tpl.Height >= frame.Height)
            throw new InvalidDataException($"模板 ({tpl.Width}x{tpl.Height}) 不小于画面 ({frame.Width}x{frame.Height})，无法匹配");

        // 实时帧是 BGRA(4通道)，模板是 BGR(3通道)：统一转成 BGR 再匹配
        Mat? conv = null;
        Mat src = frame;
        if (frame.Channels() == 4)
        {
            conv = new Mat();
            Cv2.CvtColor(frame, conv, ColorConversionCodes.BGRA2BGR);
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
                found ? maxLoc.X + tpl.Width / 2 : 0,
                found ? maxLoc.Y + tpl.Height / 2 : 0,
                tpl.Width,
                tpl.Height);
        }
        finally
        {
            conv?.Dispose();
        }
    }
}
