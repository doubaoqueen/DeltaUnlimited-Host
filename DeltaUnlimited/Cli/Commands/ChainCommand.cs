using DeltaUnlimited.Capture;
using DeltaUnlimited.Cli;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;
using DeltaUnlimited.Overlay;
using DeltaUnlimited.Vision;
using DeltaUnlimited.Vision.Ocr;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>chain：按 JSON 步骤顺序执行链路（半自动默认带人工确认点，--auto 全自动；支持 detect/if_screen/if_template 分支与 jump）。
/// click_element 支持 expect_screen/timeout_ms/retries：点击后轮询验证目标界面，失败自动重试，仍失败留证据；配置 jump_to 时失败软跳转兜底（重判/人工确认），否则中止（#7）。</summary>
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

        ValidateChain(chain, elements, screens, repoRoot); // 加载期交叉校验：启动即报错，而非运行时盲等

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
                        ClickElementStep(hwnd, elements, elName, runtime.DesignWidth, runtime.DesignHeight, repoRoot);
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
                        if (!string.IsNullOrEmpty(s.JumpTo))
                        {
                            Logger.Warn($"点击 {elName} 后未进入 {s.ExpectScreen}（证据帧已存），按 jump_to 跳转 “{s.JumpTo}”");
                            Console.WriteLine($"  ⚠ 点击未达预期界面，跳转到 “{s.JumpTo}”");
                            i = FindStep(s.JumpTo);
                            continue;
                        }
                        throw new InvalidOperationException($"点击 {elName} 后 {retries + 1} 次均未进入预期界面 {s.ExpectScreen}，链路中止");
                    }
                    break;
                }

                case "wait":
                    break; // 统一在步骤末尾按 wait_ms 等待

                case "wait_screen":
                {
                    string want = s.Screen ?? throw new InvalidDataException("wait_screen 步骤缺少 screen 字段");
                    int timeoutMs = s.TimeoutMs ?? 120000;
                    bool absent = s.Absent == true;
                    Console.WriteLine($"  ⏳ 等待界面 {want} {(absent ? "消失" : "出现")}（最长 {timeoutMs}ms）...");
                    bool reached = WaitForScreenState(runtime, screens, repoRoot, want, timeoutMs, absent);
                    Logger.Info($"等待界面 {want}{(absent ? "消失" : "出现")}: {(reached ? "达成" : "超时")}");
                    if (!reached)
                    {
                        if (!string.IsNullOrEmpty(s.JumpTo))
                        {
                            Console.WriteLine($"  ⏱ 超时，跳转到 “{s.JumpTo}”");
                            i = FindStep(s.JumpTo);
                            continue;
                        }
                        throw new InvalidOperationException($"等待界面 {want} {(absent ? "消失" : "出现")} 超时（{timeoutMs}ms）");
                    }
                    break;
                }

                case "detect":
                {
                    using var frame = CaptureService.CaptureWindowMat(winKeyword);
                    using var norm = FrameTools.Normalize(frame, runtime.DesignWidth, runtime.DesignHeight);
                    var guess = ScreenDetector.Detect(norm, screens, repoRoot);
                    string msg;
                    if (guess is not null)
                    {
                        msg = $"识别界面: {guess.Name}（置信度 {guess.Confidence:F3}）可用操作: {string.Join(" / ", guess.Actions)}";
                        if (guess.Alternatives.Count > 0)
                            msg += $"；⚠️ 同时命中: {string.Join(", ", guess.Alternatives)}（已按置信度取优，若误判请收紧该界面关键词）";
                    }
                    else
                    {
                        var scan = ScreenDetector.Scan(norm, screens, repoRoot);
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
                    string amb = guess is { Alternatives.Count: > 0 } ? $"（同时命中: {string.Join(", ", guess.Alternatives)}）" : "";
                    Console.WriteLine($"  👁 界面判断: 当前 {got}{amb} / 期望 {want} → {(got == want ? "命中 ✅" : "未命中 ❌")}");
                    Logger.Info($"界面判断: 当前 {got}{amb} / 期望 {want} {(got == want ? "命中" : "未命中")}");
                    if (got == want && !string.IsNullOrEmpty(s.JumpTo))
                    {
                        Console.WriteLine($"  ↪ 跳转到 “{s.JumpTo}”");
                        i = FindStep(s.JumpTo);
                        continue;
                    }
                    break;
                }

                case "mark_unknown":
                {
                    // 未知界面标记：截图 + 界面候选 + 全帧 OCR 词表存盘，供后续补标记（screenshots/unknown/）
                    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string dir = Path.Combine(repoRoot, "screenshots", "unknown");
                    Directory.CreateDirectory(dir);
                    string png = Path.Combine(dir, $"unknown_{stamp}.png");
                    string txt = Path.Combine(dir, $"unknown_{stamp}.txt");

                    using var frame = CaptureService.CaptureWindowMat(winKeyword);
                    using var norm = FrameTools.Normalize(frame, runtime.DesignWidth, runtime.DesignHeight);
                    Cv2.ImWrite(png, norm);

                    var scan = ScreenDetector.Scan(norm, screens, repoRoot);
                    var lines = new List<string>
                    {
                        $"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                        $"链路: {chainFile} 步骤 {i + 1}",
                        $"窗口: {winKeyword}  设计分辨率: {runtime.DesignWidth}x{runtime.DesignHeight}",
                        "",
                        "界面候选（按置信度，✓=命中）:"
                    };
                    foreach (var c in scan.Take(5))
                        lines.Add($"  {c.Name}: 置信度 {c.Confidence:F3}  命中标记 {c.MarkerHits}  {(c.Matched ? "✓" : "")}");
                    lines.Add("");
                    lines.Add("全帧 OCR 词表（供后续加标记用）:");
                    if (Ocr.Engine is { IsAvailable: true })
                        foreach (var ow in Ocr.Engine.Recognize(norm, null))
                            lines.Add($"  “{ow.Text}” @ ({ow.X},{ow.Y}) {ow.W}x{ow.H}");
                    File.WriteAllLines(txt, lines);

                    Logger.Warn($"未知界面已标记: {png}（词表 {txt}）");
                    Console.WriteLine($"  🏷 未知界面已标记: {png}");
                    Console.WriteLine($"     候选与词表: {txt}");
                    if (!auto)
                    {
                        Console.WriteLine($"  ⏸ {s.Message ?? "人工处理后回车继续（部分界面需长按空格跳过）"} —— 回车继续");
                        Console.ReadLine();
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

                case "if_ocr":
                {
                    // 关键词条件分支：当前帧 OCR 严格命中任一关键词则跳转（如入局提醒弹窗内的“未转移”行检查）
                    var keywords = s.Keywords ?? throw new InvalidDataException("if_ocr 步骤缺少 keywords");
                    if (Ocr.Engine is not { IsAvailable: true })
                    {
                        Console.WriteLine("  ⚠️ OCR 不可用，if_ocr 判为未命中（fail-open）");
                        Logger.Warn("if_ocr: OCR 不可用，判为未命中");
                        break;
                    }
                    using var frame = CaptureService.CaptureWindowMat(winKeyword);
                    using var norm = FrameTools.Normalize(frame, runtime.DesignWidth, runtime.DesignHeight);
                    int need = Math.Max(1, s.MinCount ?? 1);
                    int count = Ocr.CountStrict(norm, null, keywords);
                    bool hit = count >= need;
                    Console.WriteLine($"  关键词判断: [{string.Join("/", keywords)}] 命中 {count} 处（需 ≥{need}）→ {(hit ? "命中 ✅" : "未命中 ❌")}");
                    Logger.Info($"if_ocr [{string.Join("/", keywords)}]: {count}/{need} {(hit ? "命中" : "未命中")}");
                    if (hit && !string.IsNullOrEmpty(s.JumpTo))
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

                case "pick_operator":
                {
                    // 干员选择（15s 倒计时内）：按 operator_presets.map_operators 选择；未配置则跳过保持当前
                    var opTable = new DataStore(repoRoot).LoadOperatorPresets();
                    OperatorPick? pick = null;
                    if (opTable.MapOperators.TryGetValue("零号大坝", out var p)) pick = p;
                    else if (opTable.MapOperators.TryGetValue("default", out p)) pick = p;
                    if (pick is null)
                    {
                        Console.WriteLine("  ℹ 未配置干员选择（operator_presets.map_operators），保持当前干员，等待倒计时自动开局");
                        Logger.Info("pick_operator: 未配置，跳过");
                        break;
                    }
                    InputService.EnsureForeground(hwnd);
                    bool ok = OperatorPicker.TryPick(hwnd, winKeyword, runtime, opTable, pick);
                    Logger.Info($"pick_operator: {pick.Type} #{pick.Index} {(ok ? "点击完成" : "未找到目标（保持当前干员）")}");
                    break;
                }

                case "switch_screen":
                {
                    // 界面分流：一次截图+识别，按 branches 表跳转；未匹配走 default 兜底
                    using var frame = CaptureService.CaptureWindowMat(winKeyword);
                    using var norm = FrameTools.Normalize(frame, runtime.DesignWidth, runtime.DesignHeight);
                    var guess = ScreenDetector.Detect(norm, screens, repoRoot);
                    string got = guess?.Name ?? "";
                    string amb = guess is { Alternatives.Count: > 0 } ? $"（同时命中: {string.Join(", ", guess.Alternatives)}）" : "";
                    Console.WriteLine($"  🔀 界面分流: 当前 {(got == "" ? "unknown" : got)}{amb}（置信度 {guess?.Confidence:F3}）");
                    Logger.Info($"switch_screen: 当前 {(got == "" ? "unknown" : got)}{amb}");

                    string? target = null;
                    if (got != "" && s.Branches is not null && s.Branches.TryGetValue(got, out var b))
                        target = b;
                    target ??= s.Default;
                    if (string.IsNullOrEmpty(target))
                        throw new InvalidDataException("switch_screen 无匹配分支且缺少 default 兜底目标");

                    Console.WriteLine($"  ↪ 跳转到 “{target}”");
                    i = FindStep(target);
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
        => WaitForScreenState(runtime, screens, repoRoot, want, timeoutMs, wantAbsent: false);

    /// <summary>轮询等待目标界面出现/消失（wait_screen 用），超时返回 false。</summary>
    private static bool WaitForScreenState(RuntimeConfig runtime, ScreenTable screens, string repoRoot, string want, int timeoutMs, bool wantAbsent)
    {
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        string lastGot = "";
        while (DateTime.Now < deadline)
        {
            Thread.Sleep(800);
            using var frame = CaptureService.CaptureWindowMat(runtime.WindowKeyword, raiseAndWait: false);
            using var norm = FrameTools.Normalize(frame, runtime.DesignWidth, runtime.DesignHeight);
            var guess = ScreenDetector.Detect(norm, screens, repoRoot);
            string got = guess?.Name ?? "unknown";
            if (got != lastGot) // 界面变化才打日志（可见的轮询心跳，排查"等待期间发生了什么"）
            {
                Console.WriteLine($"  ⏳ 等待界面 {want}{(wantAbsent ? " 消失" : " 出现")}… 当前 {got}");
                Logger.Info($"等待 {want}{(wantAbsent ? "消失" : "出现")}: 当前 {got}");
                lastGot = got;
            }
            bool hit = guess?.Name == want;
            if (hit != wantAbsent) return true; // 出现且要出现 / 消失且要消失
        }
        return false;
    }

    /// <summary>点击一个元素：委托 ElementClicker（coord 兜底 / ocr 主力，降级链见其实现）。</summary>
    private static void ClickElementStep(IntPtr hwnd, ElementsTable table, string elementName, int designW, int designH, string repoRoot)
        => ElementClicker.Click(hwnd, table, elementName, designW, designH, repoRoot);

    /// <summary>加载期交叉校验（P0）：引用不存在的元素/无标记界面/缺失模板/非法按键 → 启动即报错。</summary>
    private static void ValidateChain(Chain chain, ElementsTable elements, ScreenTable screens, string repoRoot)
    {
        var errors = new List<string>();
        foreach (var (s, idx) in chain.Steps.Select((s, i) => (s, i + 1)))
        {
            switch (s.Op)
            {
                case "click_element":
                    if (string.IsNullOrEmpty(s.Element))
                        errors.Add($"步骤{idx}: click_element 缺少 element");
                    else if (!elements.Elements.ContainsKey(s.Element))
                        errors.Add($"步骤{idx}: 元素 “{s.Element}” 不存在于 elements.json");
                    else
                    {
                        var def = elements.Elements[s.Element];
                        if (def.Strategy == "ocr" && def.Params.Keywords is not { Count: > 0 })
                            errors.Add($"步骤{idx}: 元素 “{s.Element}” 用 ocr 策略但缺少 keywords");
                        else if (def.Strategy == "coord" && (def.Params.X is null || def.Params.Y is null))
                            errors.Add($"步骤{idx}: 元素 “{s.Element}” 用 coord 策略但缺少 x/y");
                        else if (def.Strategy == "template" && !File.Exists(Path.Combine(repoRoot, def.Params.Template ?? "")))
                            errors.Add($"步骤{idx}: 元素 “{s.Element}” 模板文件不存在: {def.Params.Template}");
                    }
                    break;

                case "if_screen":
                case "wait_screen":
                    if (string.IsNullOrEmpty(s.Screen))
                        errors.Add($"步骤{idx}: {s.Op} 缺少 screen");
                    else if (!screens.Screens.ContainsKey(s.Screen))
                        errors.Add($"步骤{idx}: 界面 “{s.Screen}” 不存在于 screens.json");
                    else if (screens.Screens[s.Screen].Markers.Count == 0)
                        errors.Add($"步骤{idx}: 界面 “{s.Screen}” 没有任何识别标记（引用无标记界面）");
                    break;

                case "if_ocr":
                    if (s.Keywords is not { Count: > 0 })
                        errors.Add($"步骤{idx}: if_ocr 缺少 keywords");
                    else if (s.MinCount is < 1)
                        errors.Add($"步骤{idx}: if_ocr 的 min_count 必须 ≥1");
                    break;

                case "if_template":
                    if (string.IsNullOrEmpty(s.Template))
                        errors.Add($"步骤{idx}: if_template 缺少 template");
                    else if (!File.Exists(Path.Combine(repoRoot, s.Template)))
                        errors.Add($"步骤{idx}: 模板文件不存在: {s.Template}");
                    break;

                case "key":
                    if (string.IsNullOrEmpty(s.Key))
                        errors.Add($"步骤{idx}: key 缺少 key");
                    else
                    {
                        foreach (var t in InputService.SplitCombo(s.Key))
                        {
                            bool mouse = t.Equals("left", StringComparison.OrdinalIgnoreCase)
                                         || t.Equals("right", StringComparison.OrdinalIgnoreCase)
                                         || t.Equals("middle", StringComparison.OrdinalIgnoreCase);
                            if (!mouse && InputService.MapKeyName(t) == 0)
                                errors.Add($"步骤{idx}: 不认识的按键名 “{t}”（组合 {s.Key}）");
                        }
                    }
                    break;

                case "switch_screen":
                    if (s.Branches is not { Count: > 0 })
                        errors.Add($"步骤{idx}: switch_screen 缺少 branches");
                    else
                    {
                        foreach (var (sn, tgt) in s.Branches)
                        {
                            if (!screens.Screens.ContainsKey(sn))
                                errors.Add($"步骤{idx}: switch_screen 分支界面 “{sn}” 不存在于 screens.json");
                            if (string.IsNullOrEmpty(tgt) || chain.Steps.All(x => x.Id != tgt))
                                errors.Add($"步骤{idx}: switch_screen 分支 “{sn}” 的跳转目标 “{tgt}” 不存在");
                        }
                    }
                    if (string.IsNullOrEmpty(s.Default) || chain.Steps.All(x => x.Id != s.Default))
                        errors.Add($"步骤{idx}: switch_screen 的 default 兜底目标不存在");
                    else if (s.MaxLoops is < 1)
                        errors.Add($"步骤{idx}: switch_screen 的 max_loops 必须 ≥1");
                    break;

                case "pick_operator":
                    break; // 无必填字段（读 operator_presets 配置，未配置则跳过）
            }

            if (!string.IsNullOrEmpty(s.JumpTo) && chain.Steps.All(x => x.Id != s.JumpTo))
                errors.Add($"步骤{idx}: jump_to “{s.JumpTo}” 指向不存在的步骤 id");
            if (!string.IsNullOrEmpty(s.ExpectScreen))
            {
                if (!screens.Screens.ContainsKey(s.ExpectScreen))
                    errors.Add($"步骤{idx}: expect_screen “{s.ExpectScreen}” 不存在于 screens.json");
                else if (screens.Screens[s.ExpectScreen].Markers.Count == 0)
                    errors.Add($"步骤{idx}: expect_screen “{s.ExpectScreen}” 无识别标记");
            }
        }

        if (errors.Count > 0)
            throw new InvalidDataException("链路加载校验失败:\n  " + string.Join("\n  ", errors));
    }
}
