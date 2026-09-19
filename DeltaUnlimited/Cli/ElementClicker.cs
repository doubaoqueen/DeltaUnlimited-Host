using DeltaUnlimited.Capture;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
using DeltaUnlimited.Overlay;
using DeltaUnlimited.Vision;
using DeltaUnlimited.Vision.Ocr;

namespace DeltaUnlimited.Cli;

/// <summary>元素点击器（click 命令与 chain 的 click_element 共用）。
/// 策略：ocr（关键词→文字中心→点击，主力）/ template（模板→中心→点击，美术字/图标用）/ coord（坐标直点，兜底）。
/// 降级链：ocr/模板未命中 → 有坐标则坐标兜底（告警）→ 无坐标则报错（人工出口在链路层）。</summary>
public static class ElementClicker
{
    public static void Click(IntPtr hwnd, ElementsTable table, string elementName, int designW, int designH, string repoRoot)
    {
        if (!table.Elements.TryGetValue(elementName, out var def))
            throw new ArgumentException($"元素表里没有 “{elementName}”");

        CaptureService.RaiseWindow(hwnd);
        Thread.Sleep(250);
        InputService.EnsureForeground(hwnd); // 部分游戏非前台时忽略鼠标点击
        Thread.Sleep(200);

        var rect = CaptureService.GetClientScreenRect(hwnd);
        if (rect is null)
        {
            CaptureService.UnraiseWindow(hwnd);
            throw new InvalidOperationException("点击前窗口不可用（最小化？）");
        }
        double fx = rect.Value.W / (double)designW;
        double fy = rect.Value.H / (double)designH;

        int? clickX = null, clickY = null;

        // 视觉定位统一先截一帧（ocr/template 共用），策略切换不重复截图
        if (def.Strategy == "ocr" || def.Strategy == "template")
        {
            string winKeyword = new DataStore(DataStore.FindRoot()).LoadRuntime().WindowKeyword;
            using var frame = CaptureService.CaptureWindowMat(winKeyword, raiseAndWait: false);
            using var norm = FrameTools.Normalize(frame, designW, designH);
            var region = ResolveRegion(def.Params.Region, def.Params.RegionName, repoRoot);

            if (def.Strategy == "ocr")
            {
                if (def.Params.Keywords is not { Count: > 0 })
                    throw new InvalidDataException($"元素 {elementName} 的 ocr 策略缺少 keywords");

                var found = Ocr.FindStrict(norm, region, def.Params.Keywords);
                if (found is { Found: true })
                {
                    clickX = (int)Math.Round(rect.Value.X + found.CenterX * fx);
                    clickY = (int)Math.Round(rect.Value.Y + found.CenterY * fy);
                    Logger.Info($"元素 {elementName}: OCR 命中 “{found.MatchedText}” @ ({found.CenterX},{found.CenterY})");
                }
                else
                {
                    var hint = Ocr.FindCandidateHint(norm, region, def.Params.Keywords);
                    Logger.Warn($"元素 {elementName}: OCR 未命中 [{string.Join("/", def.Params.Keywords)}]{(hint is null ? "" : "；" + hint)}，尝试坐标兜底");
                }
            }
            else // template：模板匹配中心（美术字/图标专用，如“零号大坝”卡片标题）
            {
                string tpl = def.Params.Template ?? throw new InvalidDataException($"元素 {elementName} 的 template 策略缺少 template");
                var mr = TemplateMatcher.Match(norm, Path.Combine(repoRoot, tpl), def.Params.Threshold ?? 0.85, region);
                if (mr.Found)
                {
                    clickX = (int)Math.Round(rect.Value.X + mr.CenterX * fx);
                    clickY = (int)Math.Round(rect.Value.Y + mr.CenterY * fy);
                    Logger.Info($"元素 {elementName}: 模板命中 @ ({mr.CenterX},{mr.CenterY}) 置信度 {mr.Confidence:F3}");
                }
                else
                {
                    Logger.Warn($"元素 {elementName}: 模板未命中 {tpl}（置信度 {mr.Confidence:F3}），尝试坐标兜底");
                }
            }
        }

        // 兜底：coord 坐标
        if (clickX is null && def.Params.X is not null && def.Params.Y is not null)
        {
            clickX = (int)Math.Round(rect.Value.X + def.Params.X.Value * fx);
            clickY = (int)Math.Round(rect.Value.Y + def.Params.Y.Value * fy);
            if (def.Strategy == "ocr" || def.Strategy == "template")
                Logger.Warn($"元素 {elementName}: 使用坐标兜底 ({def.Params.X}, {def.Params.Y})");
        }

        if (clickX is null || clickY is null)
        {
            CaptureService.UnraiseWindow(hwnd);
            throw new InvalidOperationException($"元素 {elementName} 定位失败：OCR 未命中且无坐标兜底（strategy={def.Strategy}）");
        }

        InputService.ClickAt(clickX.Value, clickY.Value);
        CaptureService.UnraiseWindow(hwnd);
    }

    private static int[]? ResolveRegion(List<int>? region, string? regionName, string repoRoot)
    {
        if (region is { Count: 4 }) return region.ToArray();
        if (!string.IsNullOrEmpty(regionName))
        {
            var zones = new DataStore(repoRoot).LoadZones();
            if (zones.Zones.TryGetValue(regionName, out var z) && z is { Count: 4 })
                return z.ToArray();
        }
        return null;
    }
}
