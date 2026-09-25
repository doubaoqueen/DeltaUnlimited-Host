using DeltaUnlimited.Capture;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
using DeltaUnlimited.Vision;
using DeltaUnlimited.Vision.Ocr;
using OpenCvSharp;

namespace DeltaUnlimited.Cli;

/// <summary>干员选择执行器：运行时 OCR 定位类型标签 → 相对偏移计算头像点 → 点击；
/// 标签不可见或目标越界 → 光标移到头像区滚轮翻动 → 重识别（方向自动翻转）。</summary>
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
        int direction = -120; // 先向一个方向翻；过半未果自动反向（侦察在右侧，滚动状态未知时双保险）
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

            // 标签带分段识别：每段 ≤760px → 全部吃 1.5x 放大（艺术字“突击/支援/工程/侦察”在 1x 下时灵时不灵）；
            // 按与锚点 x 的距离排序，目标类型所在段先识别，命中即停（省 OCR 时间）
            var (hit, labelX, words) = FindStripLabel(norm, strip, anchor.LabelX, pick.Type);
            if (hit)
            {
                Console.WriteLine($"  🔍 第 {t + 1} 次识别命中 “{pick.Type}” @ x={labelX}");
                var p = new OperatorLabelAnchor { LabelX = labelX, Count = anchor.Count };
                var (ax, ay) = table.Layout.ComputeAvatarPoint(p, pick.Index);
                if (ax >= 60 && ax <= 1900)
                {
                    var (sx, sy) = ToScreen(ax, ay);
                    if (!InputService.ClickAt(sx, sy))
                    {
                        Console.WriteLine($"  ⚠️ 干员 “{pick.Type}” 点击被安全网拦截（目标设计 ({ax},{ay}) → 屏幕 ({sx},{sy})），保持当前干员");
                        return false;
                    }
                    Console.WriteLine($"  ✅ 干员 “{pick.Type}” 第 {pick.Index + 1} 位：标签 x={labelX} → 头像设计 ({ax},{ay}) → 屏幕 ({sx},{sy}) 已点击");
                    return true;
                }
                Console.WriteLine($"  🔄 标签已见（x={labelX}）但目标头像越界（{ax}），继续翻动");
            }
            else
            {
                // 未命中：词表变化才打日志（诊断现场：下次卡壳直接能看到 OCR 读出了什么）
                string dump = string.Join(" ", words.Take(24).Select(w => w.Text));
                if (dump != lastDump)
                {
                    lastDump = dump;
                    Console.WriteLine($"  🔍 第 {t + 1} 次未命中 “{pick.Type}”，标签带词表: {dump}");
                }
            }

            InputService.ScrollWheel(t < maxTries / 2 ? direction : -direction);
            Thread.Sleep(200);
        }

        Console.WriteLine("  ⚠️ 多次翻动仍未定位到目标干员（保持当前干员）");
        return false;
    }

    /// <summary>标签带分段 OCR：每段 ≤760px（1.5x 放大后 ≤1140px，满足引擎 ≤1700 的放大条件——整带 1860px 吃不到放大，
    /// 艺术字标签在 1x 下识别时灵时不灵）。窗口按与锚点 x 的距离排序，目标类型所在段先识别、命中即停（省时间）。
    /// 返回 (是否命中, 命中中心x, 已识别词表——未命中时供诊断打印)。</summary>
    private static (bool Hit, int CenterX, List<OcrWord> Words) FindStripLabel(Mat norm, int[] strip, int anchorX, string keyword)
    {
        int x0 = strip[0], y = strip[1], h = strip[3];
        int end = x0 + strip[2];
        var windows = new List<int[]>();
        for (int wx = x0; wx < end; wx += 530)
        {
            int ww = Math.Min(760, end - wx);
            windows.Add(new[] { wx, y, ww, h });
        }
        windows.Sort((a, b) => Math.Abs(a[0] + a[2] / 2 - anchorX).CompareTo(Math.Abs(b[0] + b[2] / 2 - anchorX)));

        var words = new List<OcrWord>();
        var seen = new HashSet<string>();
        foreach (var win in windows)
        {
            if (Ocr.Engine is not { IsAvailable: true }) return (false, -1, words);
            foreach (var wd in Ocr.Engine.Recognize(norm, win))
            {
                string key = $"{wd.X},{wd.Y},{wd.W},{wd.H},{wd.Text}";
                if (seen.Add(key)) words.Add(wd);
            }
            var f = Ocr.FindStrict(words, new[] { keyword });
            if (f is { Found: true }) return (true, f.CenterX, words);
        }
        return (false, -1, words);
    }
}
