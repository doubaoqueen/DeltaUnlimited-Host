using OpenCvSharp;

namespace DeltaUnlimited.Vision;

/// <summary>
/// 静态图像标注工具（开发期验证 data/elements.json 识别参数用）。
/// 输入真实截图 PNG，输出带标记的新 PNG —— “看得见的识别冒烟”。
/// 支持两种策略的验证：
///   coord    —— 在 (x, y) 画十字/圆圈（确认坐标是否落在元素上）；
///   template —— 模板匹配找最佳位置，画绿框 + 中心十字（确认模板与阈值）。
/// </summary>
public static class ImageAnnotator
{
    private static readonly Scalar Red = new(0, 0, 255);
    private static readonly Scalar Green = new(0, 255, 0);

    /// <summary>coord 策略验证：在指定点画标记并保存。</summary>
    public static void AnnotatePoint(string imagePath, string outPath, int x, int y, string label)
    {
        using var img = LoadImage(imagePath);
        var p = new Point(x, y);
        Cv2.Circle(img, p, 20, Red, 2);
        Cv2.Line(img, new Point(x - 32, y), new Point(x + 32, y), Red, 2);
        Cv2.Line(img, new Point(x, y - 32), new Point(x, y + 32), Red, 2);
        PutLabel(img, label, x + 14, y - 16);
        Save(img, outPath);
    }

    public sealed record MatchResult(bool Found, double Confidence, int CenterX, int CenterY, int W, int H);

    /// <summary>template 策略验证：模板匹配 + 画框，返回最佳位置（中心点用于后续填 coord）。</summary>
    public static MatchResult AnnotateTemplate(string imagePath, string outPath, string templatePath, double threshold, string label)
    {
        using var img = LoadImage(imagePath);
        using var tpl = LoadImage(templatePath);
        if (tpl.Width >= img.Width || tpl.Height >= img.Height)
            throw new InvalidDataException($"模板 ({tpl.Width}x{tpl.Height}) 不小于截图 ({img.Width}x{img.Height})，无法匹配");

        using var result = new Mat();
        Cv2.MatchTemplate(img, tpl, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);

        int cx = 0, cy = 0;
        var w = tpl.Width;
        var h = tpl.Height;

        if (maxVal >= threshold)
        {
            cx = maxLoc.X + w / 2;
            cy = maxLoc.Y + h / 2;
            Cv2.Rectangle(img, new Rect(maxLoc, new Size(w, h)), Green, 2);
            Cv2.Circle(img, new Point(cx, cy), 10, Red, 2);
            Cv2.Line(img, new Point(cx - 20, cy), new Point(cx + 20, cy), Red, 2);
            Cv2.Line(img, new Point(cx, cy - 20), new Point(cx, cy + 20), Red, 2);
            PutLabel(img, label, maxLoc.X, Math.Max(0, maxLoc.Y - 10));
        }

        Save(img, outPath);
        return new MatchResult(maxVal >= threshold, maxVal, cx, cy, w, h);
    }
    // <summary>加载图片，确保文件存在且可解码。</summary>
    private static Mat LoadImage(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到图片: {path}");
        var img = Cv2.ImRead(path, ImreadModes.Color);
        if (img.Empty())
            throw new InvalidDataException($"无法解码图片: {path}");
        return img;
    }
    // <summary>在图片上画文字标签，自动收敛到图片内。</summary>
    private static void PutLabel(Mat img, string label, int x, int y)
    {
        // 文字画不下时收敛到图片内
        var org = new Point(Math.Clamp(x, 4, Math.Max(4, img.Width - 220)), Math.Clamp(y, 16, Math.Max(16, img.Height - 8)));
        Cv2.PutText(img, label, org, HersheyFonts.HersheySimplex, 0.6, Red, 2, LineTypes.AntiAlias);
    }
    // <summary>保存图片，确保目录存在。</summary>
    private static void Save(Mat img, string outPath)
    {
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (!Cv2.ImWrite(outPath, img))
            throw new IOException($"保存图片失败: {outPath}");
    }
}
