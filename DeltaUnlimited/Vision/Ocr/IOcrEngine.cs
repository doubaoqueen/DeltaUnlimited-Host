using OpenCvSharp;

namespace DeltaUnlimited.Vision.Ocr;

/// <summary>OCR 识别出的单个词（文本框为设计分辨率基准坐标）。</summary>
public sealed record OcrWord(string Text, int X, int Y, int W, int H);

/// <summary>严格命中结果（供"检测即点击"与界面识别裁决）。</summary>
public sealed record OcrFindResult(bool Found, string Keyword, string MatchedText, int CenterX, int CenterY, int X, int Y, int W, int H);

/// <summary>OCR 引擎抽象（ADR #10：Windows OCR 先行，Paddle 后补，数据表不动）。</summary>
public interface IOcrEngine
{
    bool IsAvailable { get; }
    string? FailureReason { get; }
    IReadOnlyList<OcrWord> Recognize(Mat frame, int[]? region);
}
