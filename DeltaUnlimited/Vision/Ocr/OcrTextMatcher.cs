using System.Text;

namespace DeltaUnlimited.Vision.Ocr;

/// <summary>OCR 文本匹配（纯函数，可单测）。
/// 严格层（可以裁决：触发点击/界面命中）：
///   1) 单词包含；2) 同一行内"最小连续词窗口"拼接包含（框只覆盖构成关键词的词）。
/// 候选层（只报警不裁决）：全帧文本出现关键词任一字符。
/// ADR #11：宽松候选永远不作为点击依据。</summary>
public static class OcrTextMatcher
{
    public sealed record OcrMatch(bool StrictHit, bool CandidateHit, string MatchedText, int X, int Y, int W, int H);

    private static string Norm(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        int n = 0;
        foreach (var c in s)
            if (!char.IsWhiteSpace(c))
                buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }

    public static OcrMatch Match(IReadOnlyList<OcrWord> words, string keyword)
    {
        string kw = Norm(keyword);
        if (kw.Length == 0 || words.Count == 0)
            return new OcrMatch(false, false, keyword, 0, 0, 0, 0);

        // 1) 单词严格命中
        foreach (var w in words)
        {
            string t = Norm(w.Text);
            if (t.Contains(kw))
                return new OcrMatch(true, true, w.Text, w.X, w.Y, w.W, w.H);
        }

        // 2) 行内最小连续词窗口严格命中（如 "配"+"装" 拆词、"Tab"+"开始游戏" 多词）
        foreach (var line in GroupLines(words))
        {
            var ordered = line.OrderBy(w => w.X).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var sb = new StringBuilder();
                for (int j = i; j < ordered.Count; j++)
                {
                    sb.Append(Norm(ordered[j].Text));
                    if (!sb.ToString().Contains(kw)) continue;

                    // 从左侧收缩窗口，让框只覆盖关键词对应的词
                    while (i < j)
                    {
                        var rest = new StringBuilder();
                        for (int k = i + 1; k <= j; k++) rest.Append(Norm(ordered[k].Text));
                        if (rest.ToString().Contains(kw)) i++;
                        else break;
                    }

                    int x = ordered[i].X;
                    int y = ordered[i].Y;
                    int x2 = Math.Max(ordered[i].X + ordered[i].W, ordered[j].X + ordered[j].W);
                    int y2 = Math.Max(ordered[i].Y + ordered[i].H, ordered[j].Y + ordered[j].H);
                    return new OcrMatch(true, true, kw, x, y, x2 - x, y2 - y);
                }
            }
        }

        // 3) 候选层：全帧文本出现任一字符（不裁决）
        string all = string.Concat(words.Select(w => Norm(w.Text)));
        bool candidate = kw.Any(c => all.Contains(c));
        return new OcrMatch(false, candidate, kw, 0, 0, 0, 0);
    }

    /// <summary>按垂直重叠分行：重叠 ≥ 较高词高度的一半才视为同一行（防止密集多行 HUD 串行）。</summary>
    internal static List<List<OcrWord>> GroupLines(IReadOnlyList<OcrWord> words)
    {
        var lines = new List<List<OcrWord>>();
        foreach (var w in words.OrderBy(w => w.Y).ThenBy(w => w.X))
        {
            var line = lines.FirstOrDefault(l => l.Any(prev => VerticalOverlap(prev, w)));
            if (line is null)
                lines.Add(new List<OcrWord> { w });
            else
                line.Add(w);
        }
        return lines;
    }

    private static bool VerticalOverlap(OcrWord a, OcrWord b)
    {
        int overlap = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
        return overlap * 2 >= Math.Max(a.H, b.H);
    }
}
