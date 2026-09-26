using DeltaUnlimited.Capture;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
using DeltaUnlimited.Vision;
using DeltaUnlimited.Vision.Ocr;
using OpenCvSharp;

namespace DeltaUnlimited.Cli;

/// <summary>干员选择执行器：运行时 OCR 定位类型标签 → 相对偏移计算头像点 → 点击。
/// 决策链：① 目标类型标签 OCR 命中 → 直接定位；② 目标艺术字读不出但其他类型标签可见 →
/// 从可见标签出发按"类型人数×同类间距+组间空隙"推算目标位置（间距推算法，更新友好）；③ 全部不可见或推算越界 → 滚轮翻动重识别。</summary>
public static class OperatorPicker
{
    /// <summary>尝试选择干员（type + index）。成功返回 true；失败（找不到/OCR 不可用）保持当前干员。</summary>
    public static bool TryPick(IntPtr hwnd, string winKeyword, RuntimeConfig runtime, OperatorPresetTable table, OperatorPick pick)
    {
        var anchor = table.Layout.TypeLabels.GetValueOrDefault(pick.Type);
        if (anchor is null)
        {
            Console.WriteLine($"  ⚠️ operator_presets 无类型锚点 “{pick.Type}”");
            return false;
        }
        if (anchor.Count > 0 && pick.Index >= anchor.Count)
        {
            Console.WriteLine($"  ⚠️ 类型 “{pick.Type}” 只有 {anchor.Count} 位干员，序号 {pick.Index} 越界");
            return false;
        }
        if (Ocr.Engine is not { IsAvailable: true })
        {
            Console.WriteLine("  ⚠️ OCR 不可用，无法定位类型标签（保持当前干员）");
            return false;
        }

        // 滚轮只在头像区域生效：光标先就位
        var area = table.Layout.AvatarArea is { Count: 4 }
            ? table.Layout.AvatarArea
            : new List<int> { 79, 787, 1734, 152 };

        var strip = table.Layout.LabelStrip is { Count: 4 }
            ? table.Layout.LabelStrip.ToArray()
            : new[] { 30, 730, 1860, 90 };

        CaptureService.LogWindowAnchor(hwnd, runtime.DesignWidth, runtime.DesignHeight);

        int maxTries = 12;
        int direction = -120; // 全部标签不可见时才滚轮；过半未果自动反向
        string lastDump = "";
        for (int t = 0; t < maxTries; t++)
        {
            var client = CaptureService.GetClientScreenRect(hwnd);
            if (client is null)
            {
                Console.WriteLine("  ⚠️ 干员选择中窗口不可用（最小化？），保持当前干员");
                return false;
            }
            // 设计坐标 → 屏幕绝对坐标（窗口位置/DPI 缩放随时可能变化，每次循环重取客户区）
            (int X, int Y) ToScreen(double dx, double dy) =>
                CaptureService.MapDesignToScreen(client.Value, runtime.DesignWidth, runtime.DesignHeight, dx, dy);

            // 滚轮就位：光标移到头像区中心（屏幕坐标）
            var (acx, acy) = ToScreen(area[0] + area[2] / 2.0, area[1] + area[3] / 2.0);
            InputService.MoveHumanized(acx, acy);

            using var frame = CaptureService.CaptureWindowMat(winKeyword);
            using var norm = FrameTools.Normalize(frame, runtime.DesignWidth, runtime.DesignHeight);

            var (labels, words) = RecognizeStripLabels(norm, strip, table.Layout, pick.Type);

            int? labelX = null;
            string how = "";
            if (labels.TryGetValue(pick.Type, out int lx))
            {
                labelX = lx;
                how = $"OCR 命中 “{pick.Type}” @ x={lx}";
            }
            else if (labels.Count > 0)
            {
                // 目标艺术字读不出（如“突击”被误读），但其他类型标签可见 →
                // 从可见标签出发，按“左侧类型人数×同类间距+组间空隙”逐组推算目标标签位置（间距推算法，数据驱动；
                // 游戏加新干员只需改 operator_presets 的 count，推算自动跟着走）
                int? pred = ComputeTargetLabelX(pick.Type, labels, table.Layout, out string? why);
                if (pred is int px)
                {
                    labelX = px;
                    how = $"目标标签未识别，但可见 [{string.Join(" ", labels.Select(kv => $"{kv.Key}@{kv.Value}"))}] → 间距推算 x={px}";
                }
                else if (why is not null)
                {
                    Console.WriteLine($"  🔍 第 {t + 1} 次：{why}，继续翻动");
                }
            }

            if (labelX is int L)
            {
                Console.WriteLine($"  🔍 第 {t + 1} 次：{how}");
                var p = new OperatorLabelAnchor { LabelX = L, Count = anchor.Count };
                var (ax, ay) = table.Layout.ComputeAvatarPoint(p, pick.Index);
                if (ax >= 60 && ax <= 1900)
                {
                    var (sx, sy) = ToScreen(ax, ay);
                    if (!InputService.ClickAt(sx, sy))
                    {
                        Console.WriteLine($"  ⚠️ 干员 “{pick.Type}” 点击被安全网拦截（目标设计 ({ax},{ay}) → 屏幕 ({sx},{sy})），保持当前干员");
                        return false;
                    }
                    string method = labels.ContainsKey(pick.Type) ? "OCR 定位" : "间距推算";
                    Console.WriteLine($"  ✅ 干员 “{pick.Type}” 第 {pick.Index + 1} 位：头像设计 ({ax},{ay}) → 屏幕 ({sx},{sy}) 已点击（{method}）");
                    return true;
                }
                Console.WriteLine($"  🔄 定位 x={L} 但目标头像越界（{ax}），继续翻动");
            }
            else if (labels.Count == 0)
            {
                string dump = string.Join(" ", words.Take(24).Select(w => w.Text));
                if (dump != lastDump)
                {
                    lastDump = dump;
                    Console.WriteLine($"  🔍 第 {t + 1} 次：无任何类型标签可见，词表: {dump}");
                }
            }

            InputService.ScrollWheel(t < maxTries / 2 ? direction : -direction);
            Thread.Sleep(200);
        }

        Console.WriteLine("  ⚠️ 多次翻动仍未定位到目标干员（保持当前干员）");
        return false;
    }

    /// <summary>间距推算法（纯函数，可单测）：从任意一个可见标签出发，按“左侧类型人数×同类间距+组间空隙”
    /// 逐组推算目标类型标签位置。类型顺序按锚点 x 升序；组间空隙用相邻锚点实测平均值自校准；
    /// 多人推算取中位数；分歧 >40px 拒绝；推算位置超出可视区 [60,1900] 返回 null 并给原因（该滚轮了）。
    /// 更新友好：游戏加新干员 → 改 operator_presets 的 count + 重标定锚点，推算自动跟着走。</summary>
    public static int? ComputeTargetLabelX(string targetType, IReadOnlyDictionary<string, int> labels, OperatorLayout layout, out string? reason)
    {
        reason = null;
        if (!layout.TypeLabels.ContainsKey(targetType)) { reason = $"类型 “{targetType}” 无锚点"; return null; }

        // 类型顺序：按锚点 x 升序（数据驱动，与屏幕上从左到右一致）
        var order = layout.TypeLabels
            .OrderBy(kv => kv.Value.LabelX)
            .Select(kv => kv.Key)
            .ToList();
        int targetIdx = order.IndexOf(targetType);

        // 相邻类型标签间距 = 左侧类型干员数 × 同类头像间距 + 组间空隙；空隙用锚点实测平均（自校准）
        int spacing = layout.AvatarSpacing;
        var gaps = new List<int>();
        for (int i = 0; i + 1 < order.Count; i++)
        {
            var a = layout.TypeLabels[order[i]];
            var b = layout.TypeLabels[order[i + 1]];
            gaps.Add(b.LabelX - a.LabelX - a.Count * spacing);
        }
        int gap = gaps.Count > 0 ? (int)Math.Round(gaps.Average()) : 20;

        var preds = new List<int>();
        foreach (var (type, x) in labels)
        {
            int li = order.IndexOf(type);
            if (li < 0 || !layout.TypeLabels.TryGetValue(type, out var a)) continue;
            int sum = 0;
            if (li < targetIdx)
                for (int i = li; i < targetIdx; i++)
                    sum += layout.TypeLabels[order[i]].Count * spacing + gap;
            else if (li > targetIdx)
                for (int i = targetIdx; i < li; i++)
                    sum -= layout.TypeLabels[order[i]].Count * spacing + gap;
            preds.Add(x + sum);
        }
        if (preds.Count == 0) { reason = "可见标签全部无锚点，无法推算"; return null; }

        int spread = preds.Max() - preds.Min();
        if (spread > 40) { reason = $"多标签推算分歧 {spread}px > 40px，疑似误识别"; return null; }

        preds.Sort();
        int pred = preds[preds.Count / 2]; // 中位数
        if (pred < 60 || pred > 1900)
        {
            reason = $"推算位置 x={pred} 超出可视区（{(pred < 60 ? "目标在屏幕左侧外，需反向滚回" : "目标在屏幕右侧外，需继续滚动")}）";
            return null;
        }
        return pred;
    }

    /// <summary>标签带分段 OCR 全部类型标签：每段 ≤760px（1.5x 放大后 ≤1140px，满足引擎 ≤1700 的放大条件——
    /// 整带 1860px 吃不到放大，艺术字标签在 1x 下识别时灵时不灵）。窗口按与目标锚点的距离排序，目标命中即停；
    /// 未命中则读完所有段（供锚点兜底推算）。返回 (类型→命中x, 全部词表)。</summary>
    private static (Dictionary<string, int> Labels, List<OcrWord> Words) RecognizeStripLabels(Mat norm, int[] strip, OperatorLayout layout, string targetType)
    {
        int x0 = strip[0], y = strip[1], h = strip[3];
        int end = x0 + strip[2];
        var windows = new List<int[]>();
        for (int wx = x0; wx < end; wx += 530)
        {
            int ww = Math.Min(760, end - wx);
            windows.Add(new[] { wx, y, ww, h });
        }
        int targetAnchor = layout.TypeLabels.GetValueOrDefault(targetType)?.LabelX ?? x0;
        windows.Sort((a, b) => Math.Abs(a[0] + a[2] / 2 - targetAnchor).CompareTo(Math.Abs(b[0] + b[2] / 2 - targetAnchor)));

        var words = new List<OcrWord>();
        var seen = new HashSet<string>();
        var labels = new Dictionary<string, int>();
        foreach (var win in windows)
        {
            if (Ocr.Engine is not { IsAvailable: true }) break;
            foreach (var wd in Ocr.Engine.Recognize(norm, win))
            {
                string key = $"{wd.X},{wd.Y},{wd.W},{wd.H},{wd.Text}";
                if (seen.Add(key)) words.Add(wd);
            }
            foreach (var (type, _) in layout.TypeLabels)
            {
                if (labels.ContainsKey(type)) continue;
                var f = Ocr.FindStrict(words, new[] { type });
                if (f is { Found: true }) labels[type] = f.CenterX;
            }
            if (labels.ContainsKey(targetType)) break; // 目标命中即停（省 OCR）
        }
        return (labels, words);
    }
}
