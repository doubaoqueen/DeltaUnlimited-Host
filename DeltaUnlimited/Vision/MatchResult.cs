namespace DeltaUnlimited.Vision;

/// <summary>模板匹配结果（TemplateMatcher / ImageAnnotator 共用）。</summary>
public sealed record MatchResult(bool Found, double Confidence, int CenterX, int CenterY, int W, int H);
