using DeltaUnlimited.Capture;
using DeltaUnlimited.Data;
using DeltaUnlimited.Overlay;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>overlay：悬浮状态面板（把 logs/status.log 尾部渲染到游戏画面上，常驻进程）。</summary>
public static class OverlayCommand
{
    public static void Run(DataStore data, string repoRoot)
    {
        var runtime = data.LoadRuntime();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            StatusOverlay.Stop();
            Environment.Exit(0);
        };

        Console.WriteLine($"等待游戏窗口（标题含 “{runtime.WindowKeyword}”）... 游戏没开时每 2 秒重试；Ctrl+C 退出");
        (int X, int Y, int W, int H)? rect = null;
        while (rect is null)
        {
            IntPtr gameHwnd = CaptureService.FindWindowByTitle(runtime.WindowKeyword);
            rect = CaptureService.GetClientScreenRect(gameHwnd);
            if (rect is null)
            {
                Console.WriteLine("  未找到游戏窗口，2 秒后重试...");
                Thread.Sleep(2000);
            }
        }

        Console.WriteLine($"✅ 悬浮面板启动于游戏客户区 ({rect.Value.X},{rect.Value.Y}) {rect.Value.W}x{rect.Value.H}（置顶+鼠标穿透，只覆盖游戏）");
        Console.WriteLine("状态来源: logs/status.log —— 运行 chain/patrol/click 等命令会实时刷新。Ctrl+C 关闭。");
        StatusOverlay.Start(rect.Value.X, rect.Value.Y, rect.Value.W, rect.Value.H);

        while (true)
        {
            try
            {
                var tail = new List<string>();
                string logPath = Logger.LatestLogPath(); // 最新按日日志
                if (logPath != "" && File.Exists(logPath))
                {
                    string[] all = File.ReadAllLines(logPath);
                    int start = Math.Max(0, all.Length - 12);
                    for (int i = start; i < all.Length; i++) tail.Add(all[i]);
                }
                if (tail.Count == 0) tail.Add("（暂无日志 —— 运行 chain/patrol 后这里会实时显示）");
                StatusOverlay.SetStatus("DeltaUnlimited 状态", tail.ToArray());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [overlay] 读取日志失败: {ex.Message}");
            }
            Thread.Sleep(500);
        }
    }
}
