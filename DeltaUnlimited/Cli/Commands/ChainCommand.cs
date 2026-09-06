using DeltaUnlimited.Capture;
using DeltaUnlimited.Cli;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
using DeltaUnlimited.Overlay;
using DeltaUnlimited.Vision;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>chain：按 JSON 步骤顺序执行链路（半自动默认带人工确认点，--auto 全自动；支持 detect/if_screen/if_template 分支与 jump）。
/// click_element 支持 expect_screen/timeout_ms/retries：点击后轮询验证目标界面，失败自动重试，仍失败留证据并中止（#7）。</summary>
public static class ChainCommand
{
    public static void Run(DataStore data, string repoRoot, string[] cmdArgs)
    {
        if (cmdArgs.Length < 2) throw new ArgumentException("用法: chain <workflow文件> [--auto]");
        string chainFile = cmdArgs[1];
        bool auto = cmdArgs.Any(a => a.Equals("--auto", StringComparison.OrdinalIgnoreCase));

        var chain = data.LoadChain(chainFile);
        var runtime = data.LoadRuntime();
        var elements = data.LoadElements();
        var screens = data.LoadScreens();
        string winKeyword = runtime.WindowKeyword;

        IntPtr hwnd = CaptureService.FindWindowByTitle(winKeyword);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"没找到标题含 “{winKeyword}” 的窗口");

        CommandUtil.InstallEmergencyStop();

        Console.WriteLine($"执行链路: {chain.Name}（{chain.Steps.Count} 步） 模式: {(auto ? "全自动" : "半自动（pause 步骤回车继续）")}");
        Console.WriteLine("Ctrl+C 随时急停。");

        int FindStep(string id)
        {
            int idx = chain.Steps.FindIndex(x => x.Id == id);
            if (idx < 0) throw new InvalidDataException($"链路中不存在步骤 id: “{id}”");
            return idx;
        }

        int i = 0;
        while (i < chain.Steps.Count)
        {
            var s = chain.Steps[i];
            Console.WriteLine($"\n[步骤 {i + 1}/{chain.Steps.Count}] {s.Op}");
            Logger.Info($"链路 {chainFile} 步骤 {i + 1}/{chain.Steps.Count} {s.Op}");
            switch (s.Op)
            {
                case "key":
                    InputService.EnsureForeground(hwnd);
                    InputService.PressKey(s.Key ?? throw new InvalidDataException("key 步骤缺少 key 字段"));
                    Logger.Info($"按键 {s.Key}");
                    break;

                case "click_element":
                {
                    string elName = s.Element ?? throw new InvalidDataException("click_element 步骤缺少 element 字段");
                    int retries = s.Retries ?? 0;
                    int timeoutMs = s.TimeoutMs ?? 8000;

                    bool ok = false;
                    for (int attempt = 0; attempt <= retries; attempt++)
                    {
                        ClickElementStep(hwnd, elements, elName, runtime.DesignWidth, runtime.DesignHeight);
                        Logger.Info($"点击元素 {elName}（第 {attempt + 1}/{retries + 1} 次）");

                        if (string.IsNullOrEmpty(s.ExpectScreen))
                        {
                            ok = true; // 未配置 expect_screen：按旧行为（点了即继续）
                            break;
                        }

                        ok = WaitForScreen(runtime, screens, repoRoot, s.ExpectScreen, timeoutMs);
                        if (ok)
                        {
                            Logger.Info($"✅ 已进入预期界面 {s.ExpectScreen}");
                            break;
                        }
                        Logger.Warn($"点击 {elName} 后未进入 {s.ExpectScreen}（第 {attempt + 1} 次，超时 {timeoutMs}ms）");
                        Thread.Sleep(800);
                    }

                    if (!ok)
                    {
                        try
                        {
                            string ev = Path.Combine(repoRoot, "screenshots", "captured", $"chain_fail_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                            var d = Path.GetDirectoryName(ev);
                            if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                            using var f = CaptureService.CaptureWindowMat(winKeyword);
                            if (Cv2.ImWrite(ev, f)) Logger.Warn($"证据帧已保存: {ev}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"证据保存失败: {ex.Message}");
                        }
                        throw new InvalidOperationException($"点击 {elName} 后 {retries + 1} 次均未进入预期界面 {s.ExpectScreen}，链路中止");
                    }
                    break;
                }

                case "wait":
                    break; // 统一在步骤末尾按 wait_ms 等待

                case "detect":
                {
                    using var frame = CaptureService.CaptureWindowMat(winKeyword);
                    using var norm = FrameTools.Normalize(frame, runtime.DesignWidth, runtime.DesignHeight);
                    var scan = ScreenDetector.Scan(norm, screens, repoRoot);
                    var hit = scan.FirstOrDefault(c => c.Matched);
                    string msg;
                    if (hit is not null)
                    {
                        var actions = screens.Screens[hit.Name].Actions;
                        msg = $"识别界面: {hit.Name}（置信度 {hit.Confidence:F3}）可用操作: {string.Join(" / ", actions)}";
                    }
                    else
                    {
                        var top = string.Join("  ", scan.Take(3).Select(c => $"{c.Name}={c.Confidence:F2}"));
                        msg = $"识别界面: 未知（最接近: {top}）";
                        var uncfg = ScreenDetector.UnconfiguredScreens(screens);
                        if (uncfg.Count > 0)
                            msg += $"；未配置标记的界面: {string.Join(", ", uncfg)}";
                    }
                    Console.WriteLine($"  👁 {msg}");
                    Logger.Info(msg);
                    break;
                }

                case "if_screen":
                {
                    string want = s.Screen ?? throw new InvalidDataException("if_screen 步骤缺少 screen 字段");
                    using var frame = CaptureService.CaptureWindowMat(winKeyword);
                    using var norm = FrameTools.Normalize(frame, runtime.DesignWidth, runtime.DesignHeight);
                    var guess = ScreenDetector.Detect(norm, screens, repoRoot);
                    string got = guess?.Name ?? "unknown";
                    Console.WriteLine($"  👁 界面判断: 当前 {got} / 期望 {want} → {(got == want ? "命中 ✅" : "未命中 ❌")}");
                    Logger.Info($"界面判断: 当前 {got} / 期望 {want} {(got == want ? "命中" : "未命中")}");
                    if (got == want && !string.IsNullOrEmpty(s.JumpTo))
                    {
                        Console.WriteLine($"  ↪ 跳转到 “{s.JumpTo}”");
                        i = FindStep(s.JumpTo);
                        continue;
                    }
                    break;
                }

                case "pause":
                    if (!auto)
                    {
                        Console.WriteLine($"  ⏸ {s.Message ?? "人工确认点"} —— 按回车继续（Ctrl+C 中止）");
                        Console.ReadLine();
                    }
                    break;

                case "if_template":
                {
                    string tpl = s.Template ?? throw new InvalidDataException("if_template 步骤缺少 template 字段");
                    string tplPath = Path.Combine(repoRoot, tpl);
                    using var frame = CaptureService.CaptureWindowMat(winKeyword);
                    using var norm = FrameTools.Normalize(frame, runtime.DesignWidth, runtime.DesignHeight);
                    var mr = TemplateMatcher.Match(norm, tplPath, s.Threshold ?? 0.85);
                    Console.WriteLine($"  模板检测: {tpl} 置信度 {mr.Confidence:F3} → {(mr.Found ? "命中 ✅" : "未命中 ❌")}");
                    Logger.Info($"模板检测 {tpl}: {mr.Confidence:F3} {(mr.Found ? "命中" : "未命中")}");
                    if (mr.Found && !string.IsNullOrEmpty(s.JumpTo))
                    {
                        Console.WriteLine($"  ↪ 跳转到 “{s.JumpTo}”");
                        i = FindStep(s.JumpTo);
                        continue;
                    }
                    break;
                }

                case "jump":
                {
                    string to = s.JumpTo ?? throw new InvalidDataException("jump 步骤缺少 jump_to 字段");
                    Console.WriteLine($"  ↪ 无条件跳转到 “{to}”");
                    i = FindStep(to);
                    continue;
                }

                default:
                    throw new InvalidDataException($"未知步骤 op: {s.Op}");
            }

            if (s.WaitMs is int w && w > 0) Thread.Sleep(w);
            if (!string.IsNullOrEmpty(s.Capture))
            {
                string p = Path.Combine(repoRoot, "screenshots", "captured", $"chain_{i + 1}_{s.Capture}_{DateTime.Now:HHmmss}.png");
                Console.WriteLine(CaptureService.CaptureWindowClient(winKeyword, p));
            }
            i++;
        }
        Console.WriteLine($"\n🎉 链路 {chain.Name} 执行完成");
    }

    /// <summary>轮询等待目标界面出现（expect_screen 用），超时返回 false。</summary>
    private static bool WaitForScreen(RuntimeConfig runtime, ScreenTable screens, string repoRoot, string want, int timeoutMs)
    {
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < deadline)
        {
            Thread.Sleep(800);
            using var frame = CaptureService.CaptureWindowMat(runtime.WindowKeyword, raiseAndWait: false);
            using var norm = FrameTools.Normalize(frame, runtime.DesignWidth, runtime.DesignHeight);
            var guess = ScreenDetector.Detect(norm, screens, repoRoot);
            if (guess?.Name == want) return true;
        }
        return false;
    }

    /// <summary>点击一个 coord 策略元素：窗口置顶 → 抢焦点 → 设计分辨率坐标缩放 → 拟人化点击。</summary>
    private static void ClickElementStep(IntPtr hwnd, ElementsTable table, string elementName, int designW, int designH)
    {
        if (!table.Elements.TryGetValue(elementName, out var def))
            throw new ArgumentException($"元素表里没有 “{elementName}”");
        if (def.Strategy != "coord" || def.Params.X is null || def.Params.Y is null)
            throw new InvalidDataException($"元素 {elementName} 需要 coord 策略且 x/y 已填写");

        CaptureService.RaiseWindow(hwnd);
        Thread.Sleep(250);
        InputService.EnsureForeground(hwnd); // 部分游戏非前台时忽略鼠标点击
        Thread.Sleep(200);
        var r = CaptureService.GetClientScreenRect(hwnd);
        if (r is null)
        {
            CaptureService.UnraiseWindow(hwnd);
            throw new InvalidOperationException("点击前窗口不可用（最小化？）");
        }
        int absX = (int)Math.Round(r.Value.X + def.Params.X.Value * (r.Value.W / (double)designW));
        int absY = (int)Math.Round(r.Value.Y + def.Params.Y.Value * (r.Value.H / (double)designH));
        InputService.ClickAt(absX, absY);
        CaptureService.UnraiseWindow(hwnd);
    }
}
