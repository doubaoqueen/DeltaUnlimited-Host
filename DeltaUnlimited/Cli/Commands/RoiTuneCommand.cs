using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Forms;
using DeltaUnlimited.Capture;
using DeltaUnlimited.Data;
using DeltaUnlimited.Gui;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>roitune：ROI 调参器——可通行性绿框"由谁定"的答案：由人看着定。
/// 实时模式（默认）：对着游戏画面移动/缩放绿框，所见即模型所得；静态模式：传一张截图离线调。
/// 回车保存到 data/ai_vision.json（采集/标注/运行三端共用同一配置，下次启动生效）。
/// 锁定 2:1 时自动联动 input_size（保持宽 224）；自由比例只改框不改输入尺寸并提示拉伸风险。
/// 红线：只写配置文件，不注入任何输入。</summary>
public static class RoiTuneCommand
{
    public static void Run(DataStore data, string repoRoot, string[] cmdArgs)
    {
        var runtime = data.LoadRuntime();
        var cfg = data.LoadAiVision();
        string? imageRel = cmdArgs.Length > 1 && !cmdArgs[1].StartsWith("--") ? cmdArgs[1] : null;

        IntPtr hwnd = IntPtr.Zero;
        Mat? staticFrame = null;
        if (imageRel is null)
        {
            hwnd = CaptureService.FindWindowByTitle(runtime.WindowKeyword);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"没找到标题含 “{runtime.WindowKeyword}” 的窗口（或传一张截图走静态模式: roitune <截图路径>）");
            Console.WriteLine($"ROI 调参器（实时）：窗口 “{runtime.WindowKeyword}” ｜ 当前 ROI [{string.Join(',', cfg.Roi)}]");
        }
        else
        {
            staticFrame = AiLabelImaging.LoadFrame(Path.Combine(repoRoot, imageRel));
            if (staticFrame is null) throw new FileNotFoundException($"截图不存在或解码失败: {imageRel}");
            Console.WriteLine($"ROI 调参器（静态图）：{imageRel} ｜ 当前 ROI [{string.Join(',', cfg.Roi)}]");
        }

        Console.WriteLine("操作：WASD/方向键=移动  Q/E=宽±  Z/C=高±  F=锁2:1  回车=保存到 ai_vision.json  R=还原  Esc=退出");

        var ui = new Thread(() =>
        {
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); }
            catch { /* 已设置则忽略 */ }
            Application.Run(new RoiTuneForm(cfg, repoRoot, runtime.DesignWidth, runtime.DesignHeight, hwnd, staticFrame));
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
    }

    /// <summary>写回 data/ai_vision.json（与 AiVision.Disable 同一写法）。返回保存结果描述。</summary>
    internal static string Save(string repoRoot, AiVisionConfig cfg)
    {
        string path = Path.Combine(repoRoot, "data", "ai_vision.json");
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, opts));
        return $"roi [{string.Join(',', cfg.Roi)}]  input_size [{string.Join(',', cfg.InputSize)}]";
    }
}
