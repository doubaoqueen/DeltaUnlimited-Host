using OpenCvSharp;

namespace DeltaUnlimited.Vision.Ocr;

/// <summary>OCR 全局管理器：启动时初始化引擎，识别层统一从此取用。</summary>
public static class Ocr
{
    public static IOcrEngine? Engine { get; private set; }

    public static void Initialize()
    {
        var engine = new WindowsOcrEngine();
        Engine = engine;
        if (!engine.IsAvailable)
            Console.WriteLine($"⚠️ OCR 不可用: {engine.FailureReason}");
        else
            Console.WriteLine("✅ OCR 引擎就绪（Windows.Media.Ocr）");
    }

    /// <summary>在区域内严格匹配关键词（任一命中），返回第一个命中；无严格命中返回 null。</summary>
    public static OcrFindResult? FindStrict(Mat frame, int[]? region, IReadOnlyList<string> keywords)
    {
        if (Engine is null || !Engine.IsAvailable || keywords is not { Count: > 0 })
            return null;

        var words = Engine.Recognize(frame, region);
        foreach (var kw in keywords)
        {
            var m = OcrTextMatcher.Match(words, kw);
            if (m.StrictHit)
                return new OcrFindResult(true, kw, m.MatchedText,
                    m.X + m.W / 2, m.Y + m.H / 2, m.X, m.Y, m.W, m.H);
        }
        return null;
    }
}
