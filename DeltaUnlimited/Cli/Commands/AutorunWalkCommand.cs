using System.Text;
using System.Text.Json;
using DeltaUnlimited.Capture;
using DeltaUnlimited.Cli;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
using DeltaUnlimited.Vision;
using DeltaUnlimited.Vision.Ai;
using DeltaUnlimited.Vision.Localization;
using DeltaUnlimited.Vision.Navigation;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>autorun_walk：自动跑图首版实验（虫子走路 + 倒车请注意）。
/// 循环：HUD 门 → 小地图定位 → 方位角（零号大坝点位表）→ 航迹伺服（转向/前进）→
/// 帧差卡死检测 → 倒车脱困（S 后退 + 边偏置转向，连续 4 败交人）。可通行性模型全程只观察记录。
/// 产物：logs/autorun/walk_*.jsonl（逐周期轨迹+决策）+ walk_*_track.png（底图轨迹回放）。
/// 红线：人盯实验、急停常备、会话限额执法、所有时序/角度/键程随机（拟人铁律）。
/// 用法: autorun_walk [--extract E1..E4] [--minutes 10] [--arrive-px 250]</summary>
public static class AutorunWalkCommand
{
    public static void Run(DataStore data, string repoRoot, string[] cmdArgs)
    {
        string extractId = GetOpt(cmdArgs, "--extract") ?? "E2";
        int minutes = int.TryParse(GetOpt(cmdArgs, "--minutes"), out var m) && m > 0 ? m : 10;
        int arrivePx = int.TryParse(GetOpt(cmdArgs, "--arrive-px"), out var a) && a > 0 ? a : 250;

        var runtime = data.LoadRuntime();
        var screens = data.LoadScreens();
        var ops = data.LoadGameOps();
        if (runtime.PxPer90Deg <= 0)
            throw new InvalidOperationException("runtime.json 缺少 px_per_90deg 标定（先用 turn 命令标定）");

        // 目标撤离点（零号大坝点位表，map_px 与定位候选同坐标系）
        var goal = LoadExtractPoint(repoRoot, extractId)
                   ?? throw new ArgumentException($"点位表里没有撤离点 {extractId}（E1-E4）");

        // 会话限额执法（债1）：这是自动化，必须过闸
        var ledger = SessionLedger.TryBegin(
            SessionLedger.DefaultPath(repoRoot), runtime.SessionLimits, DateTime.Now, out var deny);
        if (ledger is null)
        {
            Console.WriteLine($"⛔ {deny}");
            return;
        }
        using var session = ledger;

        IntPtr hwnd = CaptureService.FindWindowByTitle(runtime.WindowKeyword);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{runtime.WindowKeyword}” 的窗口");
        CaptureService.LogWindowAnchor(hwnd, runtime.DesignWidth, runtime.DesignHeight);
        if (!InputService.EnsureForeground(hwnd))
            Console.WriteLine("⚠ 未能把游戏置前台——按键可能落不进游戏，若角色不动请点一下游戏窗口");
        CommandUtil.InstallEmergencyStop();

        // 定位 + 轨迹 + 门
        using var localizer = new MinimapLocalizer(new LocalizerConfig(), repoRoot);
        var track = new LocalizerTrack();

        // 可通行性模型：只观察不决策（observe-only 红线；探针直连，不经 AiVision 运行态）
        OnnxRunner? runner = null;
        PassabilitySensor? sensor = null;
        var aiCfg = data.LoadAiVision();
        string modelPath = Path.Combine(repoRoot, aiCfg.Model);
        if (File.Exists(modelPath))
        {
            runner = new OnnxRunner(modelPath, aiCfg.Ep, aiCfg.RuntimeThreads, aiCfg.InputSize[0], aiCfg.InputSize[1]);
            runner.Warmup();
            sensor = new PassabilitySensor(runner, aiCfg, runtime.DesignWidth, runtime.DesignHeight);
        }
        else
        {
            Console.WriteLine("⚠ 模型文件不在，本实验不记录可通行性观察（不影响行走）");
        }

        string fwd = ops.KeyMap.GetValueOrDefault("move_forward", "w");
        string back = ops.KeyMap.GetValueOrDefault("move_back", "s");
        string sprintK = ops.KeyMap.GetValueOrDefault("sprint", "shift");

        string outDir = Path.Combine(repoRoot, "logs", "autorun");
        Directory.CreateDirectory(outDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string jsonlPath = Path.Combine(outDir, $"walk_{stamp}.jsonl");

        Console.WriteLine($"=== autorun_walk 实验 ===");
        Console.WriteLine($"目标 {extractId}（map_px {goal.Item1},{goal.Item2}）| 到达半径 {arrivePx}px | 时限 {minutes} 分钟 | 模型观察 {(sensor is null ? "关" : "开")}");
        Console.WriteLine("⚠ 人盯实验：随时 Ctrl+C / 急停。开始前 3 秒可以切进游戏…");
        Thread.Sleep(3000);

        var rng = new Random();
        var recovery = new RecoveryPolicy();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var positions = new List<(double x, double y, long ms)>();     // 已接受的轨迹点
        using var prevFrame = new Mat();
        bool hasPrev = false;
        int lowDiffRun = 0, locMissRun = 0, recoveries = 0, cycles = 0;
        bool awaitingEscape = false;          // 脱困后待验证：走出 40px = 成功；再次卡死 = 失败（翻边）
        double escX = 0, escY = 0;
        (double x, double y)? courseAnchor = null;   // 航迹锚点：走出去 ≥8px 才算一次航向（比例尺适配）
        int walkRun = 0;                    // 连续前进周期数（进度版贴墙判据用）
        (double x, double y)? walkStart = null;      // 本轮连续前进的起点
        string outcome = "未知";
        double startDist = -1;

        using (var jsonl = new StreamWriter(jsonlPath, append: false, new UTF8Encoding(false)))
        using (runner)
        {
            try
            {
                while (!CommandUtil.StopRequested && !session.ExceededNow(DateTime.Now))
                {
                    if (sw.Elapsed.TotalMinutes >= minutes) { outcome = "时限到"; break; }
                    cycles++;

                    Mat frame;
                    try
                    {
                        frame = CaptureService.CaptureWindowMat(hwnd, raiseAndWait: cycles == 1);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠ 截图失败（{ex.Message}）——1 秒后重试。游戏窗口被最小化/关闭会一直如此");
                        jsonl.WriteLine(J(cycles, null, null, null, "capture_fail", 0, ex.Message.Replace('"', '\'')));
                        Thread.Sleep(1000);
                        continue;
                    }
                    if (frame.Empty())
                    {
                        Console.WriteLine("⚠ 截到空帧（窗口不可见？）——1 秒后重试");
                        jsonl.WriteLine(J(cycles, null, null, null, "empty_frame", 0, "空帧"));
                        Thread.Sleep(1000);
                        continue;
                    }
                    using var _f = frame;

                    // HUD 门：背包/地图/结算等全屏 UI 时小地图不可用，原地等待
                    if (!HudStateGate.IsNormalHud(frame, screens, repoRoot, out var ui))
                    {
                        InputService.ReleaseAllHeldKeys();
                        Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] 全屏 UI（{ui}）——小地图不可用，原地等待");
                        jsonl.WriteLine(J(cycles, null, null, null, "ui_blocked", 0, $"UI覆盖:{ui}"));
                        Thread.Sleep(1000);
                        continue;
                    }

                    // 定位 + 轨迹
                    var prior = track.Last is { } l
                        ? new LocalizePrior(l.CenterX, l.CenterY, l.ScalePx) : null;
                    var raw = localizer.Localize(frame, prior);
                    var tr = track.Accept(raw, sw.ElapsedMilliseconds);
                    double? px = tr.Accepted && tr.Position is { } lp ? lp.CenterX : (double?)null;
                    double? py = tr.Accepted && tr.Position is { } lp2 ? lp2.CenterY : (double?)null;

                    // 航迹：最近两个已接受位置（位移 ≥ 8px 才算有效航向）
                    if (tr.Accepted && tr.Position is { } now)
                    {
                        positions.Add((now.CenterX, now.CenterY, sw.ElapsedMilliseconds));
                        locMissRun = 0;
                    }
                    else
                    {
                        if (cycles <= 3 || locMissRun == 10)
                            Console.WriteLine($"  ℹ 定位未接受（第 {cycles} 周期）：{tr.Note}");
                        locMissRun++;
                    }
                    if (track.Last is { } first && positions.Count == 1)
                        Console.WriteLine($"  ✅ 首锚成功：map_px ({first.CenterX:F0},{first.CenterY:F0}) 分数 {first.Score:F2}");
                    if (locMissRun >= 20) { outcome = "定位失联（20 周期无有效位置）"; break; }

                    // 航迹（锚点基线）：与上次判向点比位移，≥8px 才产出一个航向值。
                    // 不比相邻两帧——整图 4096px 下一步行只挪 ~2px，相邻比法永远判不出航向（首跑实测）。
                    double? course = null;
                    if (positions.Count > 0)
                    {
                        var (nx, ny, _) = positions[^1];
                        if (courseAnchor is { } anc)
                        {
                            double ddx = nx - anc.x, ddy = ny - anc.y;
                            if (Math.Sqrt(ddx * ddx + ddy * ddy) >= WalkNavigator.MinTrackDispPx)
                            {
                                course = NavMath.CourseDeg(ddx, ddy);
                                courseAnchor = (nx, ny);
                            }
                        }
                        else
                        {
                            courseAnchor = (nx, ny);   // 首个锚点只立不判
                        }
                    }

                    // 帧差（卡死判据）：转向帧天然高差，前进帧差低 = 被挡
                    double diff = hasPrev ? FrameDiff.Score(prevFrame, frame) : 999;
                    frame.CopyTo(prevFrame); hasPrev = true;

                    double cx = px ?? (positions.Count > 0 ? positions[^1].x : goal.Item1);
                    double cy = py ?? (positions.Count > 0 ? positions[^1].y : goal.Item2);
                    double dist = Math.Sqrt(Math.Pow(goal.Item1 - cx, 2) + Math.Pow(goal.Item2 - cy, 2));
                    if (startDist < 0 && px is not null) startDist = dist;
                    double bearing = NavMath.BearingDeg(cx, cy, goal.Item1, goal.Item2);

                    // 卡死：前进期连续 3 周期低帧差（脱困周期自身不判卡死，见下面 stuck 计算）
                    bool walkedRecently = positions.Count >= 2;
                    if (diff < 4.0 && walkedRecently) lowDiffRun++; else lowDiffRun = 0;
                    bool stuck = lowDiffRun >= 3;

                    // 进度版贴墙：连续前进 4 周期、地图位移 <15px = 抵墙（帧差对"抵墙走"失明，位移不撒谎）
                    if (walkRun > 0 && positions.Count > 0 && walkStart is { } ws)
                    {
                        var (nx, ny, _) = positions[^1];
                        double wdisp = Math.Sqrt(Math.Pow(nx - ws.x, 2) + Math.Pow(ny - ws.y, 2));
                        if (WalkNavigator.IsWallStuck(walkRun, wdisp)) stuck = true;
                    }

                    // 脱困效果裁决（先于决策，让策略状态最新）：再次卡死=失败翻边；走出40px=成功沿用边
                    if (awaitingEscape)
                    {
                        if (stuck) { recovery.ReportOutcome(false); awaitingEscape = false; }
                        else if (positions.Count > 0)
                        {
                            var (lx, ly, _) = positions[^1];
                            if (Math.Sqrt(Math.Pow(lx - escX, 2) + Math.Pow(ly - escY, 2)) >= 40)
                            { recovery.ReportOutcome(true); awaitingEscape = false; }
                        }
                    }

                    // 可通行性观察（只记录）
                    string aiNote = "";
                    if (sensor is not null)
                    {
                        var r = sensor.PredictFull(frame).Result;
                        aiNote = $"{r.Class}:{r.Confidence:F2}";
                    }

                    // 决策 + 执行。⚠ 从未有过定位锚点时绝不能拿"目标坐标兜底"去算距离——
                    // 双帧首锚意味着第 1 周期必然无位置，兜底会算出距离 0 → 假"到达"（首跑实测踩过）。
                    // 此时唯一合理动作 = 盲走建立航迹，等定位确认后再导航。
                    WalkPlan plan;
                    if (px is null && positions.Count == 0)
                    {
                        plan = new WalkPlan(WalkPhase.Explore, 0, false,
                            locMissRun == 0 && cycles == 1 ? "首周期双帧锚未立：盲走建立航迹" : "无定位锚点：盲走等待定位（持续失联 20 周期将收工）");
                    }
                    else
                    {
                        plan = WalkNavigator.Decide(course, bearing, dist, arrivePx, stuck, recovery, rng);
                    }
                    switch (plan.Phase)
                    {
                        case WalkPhase.Arrived:
                            outcome = "✅ 到达目标点半径内";
                            break;
                        case WalkPhase.GiveUp:
                            outcome = "⛔ 连续脱困失败交人工";
                            break;
                        case WalkPhase.Recover:
                        {
                            recoveries++;
                            InputService.ReleaseAllHeldKeys();
                            var (backMs, turnDeg) = recovery.Plan(rng);
                            InputService.HoldKeys(new[] { back }, backMs);
                            TurnByDeg(turnDeg, runtime.PxPer90Deg);
                            if (positions.Count > 0) (escX, escY, _) = positions[^1];
                            awaitingEscape = true;
                            walkRun = 0; walkStart = null;             // 脱困打断前进链
                            jsonl.WriteLine(J(cycles, px, py, course, "recover", diff, $"{plan.Note} back={backMs}ms turn={turnDeg}°"));
                            lowDiffRun = 0;
                            continue;   // 脱困周期直接进入下轮感知，不前进
                        }
                        case WalkPhase.Turn:
                            InputService.ReleaseAllHeldKeys();
                            TurnByDeg(plan.TurnDx, runtime.PxPer90Deg);
                            walkRun = 0; walkStart = null;             // 转向打断前进链
                            break;
                        case WalkPhase.Walk:
                        {
                            int walkMs = 500 + rng.Next(0, 400);
                            InputService.HoldKeys(
                                plan.Sprint ? new[] { fwd, sprintK } : new[] { fwd }, walkMs);
                            walkRun++;
                            walkStart ??= (positions.Count > 0 ? (positions[^1].x, positions[^1].y) : ((double)0, 0));
                            break;
                        }
                        case WalkPhase.Explore:
                            InputService.HoldKeys(new[] { fwd }, 600);
                            walkRun++;                                   // 探索步也算前进（贴墙判据同适用）
                            walkStart ??= (positions.Count > 0 ? (positions[^1].x, positions[^1].y) : ((double)0, 0));
                            break;
                    }
                    if (plan.Phase is WalkPhase.Arrived or WalkPhase.GiveUp)
                    {
                        jsonl.WriteLine(J(cycles, px, py, course, plan.Phase.ToString().ToLowerInvariant(), diff,
                            $"{plan.Note} dist={dist:F0}px"));
                        break;
                    }

                    jsonl.WriteLine(J(cycles, px, py, course, plan.Phase.ToString().ToLowerInvariant(), diff,
                        $"{plan.Note} dist={dist:F0}px ai={aiNote}"));
                    Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] c{cycles} {plan.Phase} {plan.Note} | 距目标 {dist:F0}px | {aiNote}");
                }
                if (outcome == "未知") outcome = CommandUtil.StopRequested ? "人工急停" : "会话限额到点";
            }
            finally
            {
                InputService.ReleaseAllHeldKeys();
            }
        }

        // 轨迹回放图：底图 /2 缩放 + 折线 + 起终点
        try
        {
            var cfg = new LocalizerConfig();
            string mapPath = Path.IsPathRooted(cfg.MapImage) ? cfg.MapImage : Path.Combine(repoRoot, cfg.MapImage);
            using var map = Cv2.ImRead(mapPath, ImreadModes.Color);
            if (!map.Empty())
            {
                using var small = new Mat();
                Cv2.Resize(map, small, new Size(map.Width / 2, map.Height / 2));
                var pts = positions
                    .Select(p => new Point((int)(p.x / 2), (int)(p.y / 2)))
                    .ToArray();
                if (pts.Length >= 2)
                    Cv2.Polylines(small, new[] { pts }, false, new Scalar(0, 215, 255), 3);   // 橙色轨迹
                Cv2.Circle(small, new Point(goal.Item1 / 2, goal.Item2 / 2), 14, new Scalar(0, 0, 255), 3);
                Cv2.PutText(small, extractId, new Point(goal.Item1 / 2 - 10, goal.Item2 / 2 - 20),
                    HersheyFonts.HersheySimplex, 0.7, new Scalar(0, 0, 255), 2);
                if (pts.Length > 0)
                    Cv2.Circle(small, pts[0], 8, new Scalar(255, 0, 0), 3);
                string png = Path.Combine(outDir, $"walk_{stamp}_track.png");
                Cv2.ImWrite(png, small);
                Console.WriteLine($"\n轨迹图: {png}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ 轨迹图生成失败: {ex.Message}");
        }

        double netProgress = startDist > 0 && positions.Count > 0
            ? startDist - Math.Sqrt(Math.Pow(goal.Item1 - positions[^1].x, 2) + Math.Pow(goal.Item2 - positions[^1].y, 2))
            : 0;
        Console.WriteLine($"🏁 结局: {outcome}");
        Console.WriteLine($"   周期 {cycles} | 有效轨迹点 {positions.Count} | 脱困 {recoveries} 次 | 净推进 {netProgress:F0}px | 时长 {sw.Elapsed:mm\\:ss}");
        Console.WriteLine($"   轨迹数据: {jsonlPath}（失败在哪卡住、为什么卡——下一版路线图的原料）");
    }

    /// <summary>分块转向（单次 MoveRelative ≤260px，块间小睡），全程序随时可被急停打断。</summary>
    private static void TurnByDeg(double deg, double pxPer90)
    {
        int total = (int)Math.Round(deg / 90.0 * pxPer90);
        int sign = Math.Sign(total);
        int remaining = Math.Abs(total);
        while (remaining > 0 && !CommandUtil.StopRequested)
        {
            int chunk = Math.Min(260, remaining);
            InputService.MoveRelative(sign * chunk, 0);
            remaining -= chunk;
            Thread.Sleep(60 + Random.Shared.Next(0, 60));
        }
    }

    private static (int, int)? LoadExtractPoint(string repoRoot, string id)
    {
        string path = Path.Combine(repoRoot, "data", "zero_dam_points.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var e in doc.RootElement.GetProperty("extract_points").EnumerateArray())
        {
            if (e.GetProperty("id").GetString() != id) continue;
            var mp = e.GetProperty("map_px");
            return (mp[0].GetInt32(), mp[1].GetInt32());
        }
        return null;
    }

    private static string J(long cycle, double? x, double? y, double? course, string phase, double diff, string note)
        => $"{{\"c\":{cycle},\"ts\":\"{DateTime.Now:HH:mm:ss.fff}\",\"x\":{Fmt(x)},\"y\":{Fmt(y)},"
           + $"\"course\":{Fmt(course)},\"phase\":\"{phase}\",\"fdiff\":{diff:F1},\"note\":\"{note.Replace("\"", "'")}\"}}";

    private static string Fmt(double? v) => v is null ? "null" : v.Value.ToString("F1");

    internal static string? GetOpt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
