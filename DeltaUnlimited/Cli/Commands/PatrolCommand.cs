using DeltaUnlimited.Capture;
using DeltaUnlimited.Cli;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
using DeltaUnlimited.Overlay;
using DeltaUnlimited.Vision;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>patrol：反应式巡逻 v2 —— 双守卫（贴墙停 + 横移低效），转向量可标定。</summary>
public static class PatrolCommand
{
    public static void Run(DataStore data, string repoRoot, string[] cmdArgs)
    {
        var runtime = data.LoadRuntime();
        double maxSeconds = cmdArgs.Length > 1 && double.TryParse(cmdArgs[1], out double ms1) ? ms1 : 60;
        double stuckThreshold = cmdArgs.Length > 2 && double.TryParse(cmdArgs[2], out double st) ? st : 4.0;
        int needLow = cmdArgs.Length > 3 && int.TryParse(cmdArgs[3], out int nl) ? nl : 3;
        int turnPx = cmdArgs.Length > 4 && int.TryParse(cmdArgs[4], out int tp) ? tp : (int)Math.Round(runtime.PxPer90Deg);
        if (turnPx <= 0)
            throw new InvalidOperationException("转向参数未标定（data/runtime.json 的 px_per_90deg ≤ 0）：请先标定或用第 4 参数临时指定。");
        string winKeyword = cmdArgs.Length > 5 ? cmdArgs[5] : runtime.WindowKeyword;

        const double sampleEveryMs = 350;
        const double highScore = 8.0;     // ≥ 此值 = 明确的高速前进（开阔地冲刺实测 ~27）
        const double slowDriftSec = 6.0;  // 这么久没有高速样本 → 疑似贴墙横移/低效前进
        const int maxEvents = 20;         // 事件过多 → 场地过小/打转，中止

        var ops = data.LoadGameOps();
        string fwdKey = ops.KeyMap.GetValueOrDefault("move_forward", "w");
        string sprintKey = ops.KeyMap.GetValueOrDefault("sprint", "shift");

        IntPtr hwnd = CaptureService.FindWindowByTitle(winKeyword);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{winKeyword}” 的窗口");

        CommandUtil.InstallEmergencyStop();

        Console.WriteLine($"反应式巡逻 v2：最长 {maxSeconds}s | 守卫1 贴墙: 连续 {needLow} 次差 < {stuckThreshold} | 守卫2 横移: {slowDriftSec}s 无 ≥{highScore} 样本 | 转向 {turnPx}px(标定)");
        Console.WriteLine("开始后角色将持续冲刺前进；受阻自动转向。Ctrl+C 急停。");
        Console.WriteLine("2 秒后开始，请盯紧屏幕！");
        Thread.Sleep(2000);

        var start = DateTime.Now;
        int samples = 0, lowRun = 0, stuckEvents = 0, driftEvents = 0;
        double diffSum = 0, lastHighAt = 0;
        Mat? prev = null;

        void Escalate(double t, string reason)
        {
            stuckEvents++;
            int total = stuckEvents + driftEvents;
            Console.WriteLine($"  ⚠️ [t={t:F1}s] {reason}（贴墙#{stuckEvents} / 横移#{driftEvents}）→ 松开前进键");
            Logger.Warn($"{reason}（贴墙#{stuckEvents}/横移#{driftEvents}）→ 右转");
            InputService.ReleaseAllHeldKeys();
            Thread.Sleep(400);
            Console.WriteLine("  ↻ 右转中...");
            InputService.MoveRelative(turnPx, 0);
            Thread.Sleep(400);
            InputService.PressKeys(new[] { fwdKey, sprintKey });
            lowRun = 0;
            lastHighAt = t;
            prev?.Dispose();
            prev = null; // 重置参考帧，避免起步阶段误判
            Console.WriteLine("  ▶ 继续冲刺前进");
            if (total >= maxEvents)
                throw new InvalidOperationException($"受阻事件已达 {total} 次仍反复卡住——场地可能过小/在角落打转，请换开阔区域后重试");
        }

        try
        {
            InputService.EnsureForeground(hwnd);
            InputService.PressKeys(new[] { fwdKey, sprintKey });
            Console.WriteLine("▶ 开始冲刺前进...");
            Logger.Info("▶ 巡逻开始：持续冲刺前进，受阻自动转向");

            while ((DateTime.Now - start).TotalSeconds < maxSeconds)
            {
                Thread.Sleep((int)sampleEveryMs);
                // 首个采样带置顶等待；后续窗口已在前台，跳过置顶提速
                Mat cur = CaptureService.CaptureWindowMat(winKeyword, prev is null);
                if (prev is not null)
                {
                    double d = FrameDiff.Score(prev, cur);
                    samples++;
                    diffSum += d;
                    double t = (DateTime.Now - start).TotalSeconds;
                    if (d < stuckThreshold)
                    {
                        lowRun++;
                        Console.WriteLine($"  [t={t:F1}s] 运动差 {d:F2}（低变化 {lowRun}/{needLow}）");
                    }
                    else
                    {
                        lowRun = 0;
                        if (d >= highScore) lastHighAt = t;
                        if (samples % 8 == 0)
                        {
                            Console.WriteLine($"  [t={t:F1}s] 运动差 {d:F2}（移动中·心跳）");
                            Logger.Info($"巡逻 t={t:F1}s 运动差 {d:F2}");
                        }
                    }

                    if (lowRun >= needLow)
                        Escalate(t, "判定贴墙卡住（画面连续低变化）");
                    else if (samples > 8 && t - lastHighAt >= slowDriftSec)
                        Escalate(t, "疑似贴墙横移/低效前进（长时间无高速运动样本）");
                }
                prev?.Dispose();
                prev = cur;
            }
        }
        finally
        {
            prev?.Dispose();
            InputService.ReleaseAllHeldKeys();
        }

        double avg = samples > 0 ? diffSum / samples : 0;
        Console.WriteLine($"\n🏁 巡逻结束：运行 {(DateTime.Now - start):hh\\:mm\\:ss} | 采样 {samples} 次 | 平均运动差 {avg:F2} | 贴墙事件 {stuckEvents} | 横移/低效事件 {driftEvents}");
        Logger.Info($"🏁 巡逻结束：采样 {samples} 平均差 {avg:F2} 贴墙 {stuckEvents} 横移 {driftEvents}");
        Console.WriteLine("转角标定（与 EDPI=DPI×游戏灵敏度 相关，换设置需重标）:");
        Console.WriteLine("  面朝固定参照物 → 重复执行 turn 1000 0 直到回到原方向，记次数 n（转一圈约需 n 次）");
        Console.WriteLine($"  → 90° 的 px ≈ 250×n，当前标定值 {runtime.PxPer90Deg} 已存 data/runtime.json（EDPI 变化时更新它，或用第 4 参临时覆盖）");
        Console.WriteLine("校准提示: 贴墙不识别→阈值调大; 开阔地假贴墙→调小/加严: patrol 60 2.5 4");
    }
}
