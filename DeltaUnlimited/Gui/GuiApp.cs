using System.Threading;
using System.Windows.Forms;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
using DeltaUnlimited.Vision.Ocr;

namespace DeltaUnlimited.Gui;

/// <summary>GUI 入口：控制台分流 → 拟人化参数/OCR 初始化 → 专用 STA 线程跑 WinForms 消息循环。
/// 主线程只做 Join；所有窗口代码都在 STA 线程上（控制台入口线程是 MTA，不能直接跑 WinForms）。</summary>
public static class GuiApp
{
    public static void Run(DataStore store, string repoRoot, RuntimeConfig runtime, string version)
    {
        ConsoleRelay.Attach();
        Console.WriteLine($"DeltaUnlimited v{version}（图形控制面板）");

        InputService.Configure(runtime.Humanizer ?? new HumanizerConfig());
        Ocr.Initialize(runtime.OcrEngine);
        if (runtime.PxPer90Deg <= 0)
            Console.WriteLine("⚠️ 转向参数未标定（data/runtime.json 的 px_per_90deg ≤ 0）：请在 CLI 运行 turn 命令标定，或手动编辑该值。");

        var ui = new Thread(() =>
        {
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); }
            catch { /* 已被其它组件设置则忽略 */ }
            Application.Run(new MainForm(store, repoRoot));
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
    }
}
