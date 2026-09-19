using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using OpenCvSharp;

namespace DeltaUnlimited.Vision.Ocr;

/// <summary>Windows.Media.Ocr 实现（零额外依赖）。
/// 预处理（MAA 惯例）：灰度 → 深底浅字反色 → Otsu 二值化 → 送引擎，显著提升游戏文字识别率。</summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly OcrEngine? _engine;

    public WindowsOcrEngine()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? TryCreateChinese();
        if (_engine is null)
        {
            FailureReason = "未能创建 OCR 引擎：请确认系统已安装中文语言包及其“光学字符识别”组件（设置→时间和语言→语言→中文(简体)→选项）。";
        }
    }

    public bool IsAvailable => _engine is not null;
    public string? FailureReason { get; }

    private static OcrEngine? TryCreateChinese()
    {
        try
        {
            return OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN"))
                ?? OcrEngine.TryCreateFromLanguage(new Language("zh-CN"));
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<OcrWord> Recognize(Mat frame, int[]? region)
    {
        if (_engine is null)
            throw new InvalidOperationException(FailureReason ?? "OCR 引擎不可用");

        Mat src = frame;
        Mat? roi = null;
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

        try
        {
            // 预处理：灰度 → 反色（深底浅字）→ Otsu 二值化
            using var gray = new Mat();
            Cv2.CvtColor(src, gray, src.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
            using var prepared = new Mat();
            if (Cv2.Mean(gray).Val0 < 128)
                Cv2.BitwiseNot(gray, prepared);
            else
                gray.CopyTo(prepared);
            Cv2.Threshold(prepared, prepared, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

            // 轻微放大（仅区域裁切时：全帧放大 2880px 会超过 OcrEngine 2600px 上限）
            // 放大后 OCR 返回的框是放大坐标系的，必须除回 scale 才能映射回原图（否则点击/区域判定偏移 1.5x）。
            double scale = 1.0;
            if (prepared.Width <= 1700 && prepared.Height <= 1700)
            {
                Cv2.Resize(prepared, prepared, new Size(0, 0), 1.5, 1.5, InterpolationFlags.Linear);
                scale = 1.5;
            }

            using var bgra = new Mat();
            Cv2.CvtColor(prepared, bgra, ColorConversionCodes.GRAY2BGRA);
            Cv2.ImEncode(".bmp", bgra, out byte[] bmp);

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bmp);
                _ = writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                _ = writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            }
            stream.Seek(0);

            var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            using var bitmap = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();

            var result = _engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
            var words = new List<OcrWord>();
            foreach (var line in result.Lines)
            {
                foreach (var w in line.Words)
                {
                    var r = w.BoundingRect;
                    words.Add(new OcrWord(w.Text,
                        (int)(offX + r.X / scale), (int)(offY + r.Y / scale),
                        (int)(r.Width / scale), (int)(r.Height / scale)));
                }
            }
            return words;
        }
        finally
        {
            roi?.Dispose();
        }
    }
}
