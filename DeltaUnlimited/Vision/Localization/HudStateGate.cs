using DeltaUnlimited.Data;
using OpenCvSharp;

namespace DeltaUnlimited.Vision.Localization;

/// <summary>HUD 状态门：判断当前帧是否处于"正常对局 HUD"（此时才有小地图）。
///
/// 背景（2026-10-21 实测）：按 Tab 打开背包、开地图、结算界面时，左上角区域不是小地图
/// （是背包 UI/面板），此时定位器会把 UI 纹理当小地图去匹配 → 得到高分但完全错误的位置。
/// 因此定位前必须先过这道门：用对局内界面表（screens.json，group=match）识别当前界面，
/// 命中"背包/地图/结算"等全屏 UI 则判定无小地图。未识别出界面时按正常 HUD 处理（fail-open，
/// 由轨迹过滤兜底）。</summary>
public static class HudStateGate
{
    /// <summary>对局内的全屏 UI 界面对应界面名（此时小地图不可用）。</summary>
    private static readonly HashSet<string> UiScreens = new(StringComparer.Ordinal)
    {
        "match_backpack",
        "match_map",
        "match_settlement_success",
        "match_settlement_fail",
    };

    /// <summary>是否处于正常对局 HUD（可定位）。<paramref name="uiScreen"/> 输出命中的 UI 界面名（无则 null）。</summary>
    public static bool IsNormalHud(OpenCvSharp.Mat frame, ScreenTable screens, string root, out string? uiScreen)
    {
        uiScreen = null;
        try
        {
            var guess = ScreenDetector.Detect(frame, screens, root, group: "match");
            if (guess is not null && UiScreens.Contains(guess.Name))
            {
                uiScreen = guess.Name;
                return false;
            }
        }
        catch
        {
            // fail-open：识别异常不影响定位（轨迹过滤会兜底）
        }
        return true;
    }
}
