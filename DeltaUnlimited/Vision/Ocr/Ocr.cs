using OpenCvSharp;

namespace DeltaUnlimited.Vision.Ocr;

/// <summary>OCR 全局管理器：启动时初始化引擎，识别层统一从此取用。</summary>
public static class Ocr
{
    public static IOcrEngine? Engine { get; private set; }

    /// <summary>初始化引擎（runtime.json 的 ocr_engine 可切换；paddle 待门控失败后接入）。</summary>
    public static void Initialize(string enginePreference = "windows")
    {
        if (enginePreference.Equals("paddle", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("⚠️ runtime.json 指定 paddle 引擎，但 Paddle 尚未接入（Windows OCR 门控未失败，按 ADR #10 暂缓）；本次回退 windows 引擎。");
        }
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
        if (Engine is null || !Engine.IsAvailable) return null;
        return FindStrict(Engine.Recognize(frame, region), keywords);
    }

    /// <summary>对已识别词表做严格匹配（同帧词表缓存用：单次扫描内同 region 只识别一次）。</summary>
    public static OcrFindResult? FindStrict(IReadOnlyList<OcrWord> words, IReadOnlyList<string> keywords)
        => MatchAll(words, keywords).FirstOrDefault();

    /// <summary>一次识别内统计哪些关键词严格命中（require_all 标记用，避免重复 OCR 同区域）。</summary>
    public static IReadOnlyList<string> FindAllStrict(Mat frame, int[]? region, IReadOnlyList<string> keywords)
    {
        if (Engine is null || !Engine.IsAvailable) return Array.Empty<string>();
        return FindAllStrict(Engine.Recognize(frame, region), keywords);
    }

    /// <summary>对已识别词表统计命中的关键词（同帧词表缓存用）。</summary>
    public static IReadOnlyList<string> FindAllStrict(IReadOnlyList<OcrWord> words, IReadOnlyList<string> keywords)
        => MatchAll(words, keywords).Select(r => r.Keyword).ToList();

    /// <summary>一次识别内统计关键词的严格命中总次数（裸装鉴别等计数场景用；与 FindStrict 同源严格层）。</summary>
    public static int CountStrict(Mat frame, int[]? region, IReadOnlyList<string> keywords)
    {
        if (Engine is null || !Engine.IsAvailable || keywords is not { Count: > 0 })
            return 0;
        return CountStrict(Engine.Recognize(frame, region), keywords);
    }

    /// <summary>对已识别词表统计命中次数（同帧词表缓存用）。</summary>
    public static int CountStrict(IReadOnlyList<OcrWord> words, IReadOnlyList<string> keywords)
        => keywords.Sum(k => OcrTextMatcher.CountStrict(words, k));

    private static List<OcrFindResult> MatchAll(Mat frame, int[]? region, IReadOnlyList<string> keywords)
    {
        if (Engine is null || !Engine.IsAvailable) return new List<OcrFindResult>();
        return MatchAll(Engine.Recognize(frame, region), keywords);
    }

    private static List<OcrFindResult> MatchAll(IReadOnlyList<OcrWord> words, IReadOnlyList<string> keywords)
    {
        var results = new List<OcrFindResult>();
        if (keywords is not { Count: > 0 })
            return results;

        foreach (var kw in keywords)
        {
            var m = OcrTextMatcher.Match(words, kw);
            if (m.StrictHit)
                results.Add(new OcrFindResult(true, kw, m.MatchedText,
                    m.X + m.W / 2, m.Y + m.H / 2, m.X, m.Y, m.W, m.H));
        }
        return results;
    }

    /// <summary>严格命中失败时的候选提示（诊断/报警：指出可能与哪个词混淆，不作裁决依据）。</summary>
    public static string? FindCandidateHint(Mat frame, int[]? region, IReadOnlyList<string> keywords)
    {
        if (Engine is null || !Engine.IsAvailable || keywords is not { Count: > 0 })
            return null;

        var words = Engine.Recognize(frame, region);
        foreach (var kw in keywords)
        {
            var m = OcrTextMatcher.Match(words, kw);
            if (m.CandidateHit && !string.IsNullOrEmpty(m.CandidateHint))
                return $"关键词 “{kw}” 可能与 “{m.CandidateHint}” 混淆，建议人工确认";
        }
        return null;
    }
}
