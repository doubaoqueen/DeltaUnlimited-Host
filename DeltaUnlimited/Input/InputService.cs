using System.ComponentModel;
using System.Runtime.InteropServices;
using DeltaUnlimited.Capture;
using DeltaUnlimited.Data;

namespace DeltaUnlimited.Input;

/// <summary>
/// 键鼠输出（Helmsman）：SendInput 模拟真实输入。
/// 绝对移动（定位点击）用屏幕坐标；相对移动（视角转动）用增量。
/// 支持组合键（"ctrl+left" 等 "+" 分隔串）与鼠标键 token（left/right/middle）。
///
/// 拟人化参数全部来自 HumanizerConfig（runtime.json 的 humanizer 段），调参不改代码。
/// 说明：拟人化降低"机械感"，不等于免疫风控；实战频率与行为仍需自行控制（见大纲 §7）。
/// </summary>
public static class InputService
{
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const ushort VK_LMENU = 0xA4; // 左 Alt（前置切换用）

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private static readonly Random Rng = new();
    private static readonly object HeldLock = new();
    private static readonly HashSet<ushort> HeldKeys = new();
    private static readonly HashSet<uint> HeldMouseButtons = new();

    private static HumanizerConfig Config = new();

    /// <summary>注入拟人化参数（启动时从 runtime.json 读取后调用）。</summary>
    public static void Configure(HumanizerConfig config)
    {
        Config = config ?? new HumanizerConfig();
        CaptureService.EnsureDpiAwareness(); // 绝对定位/系统光标读回必须在物理像素坐标系下一致
    }

    private static int Rand(int min, int max) => Rng.Next(min, max + 1);

    /// <summary>把鼠标移动到屏幕坐标 (x, y) 并左键单击。
    /// 移动=拟人弧线路径（贝塞尔+缓动+过冲+抖动），全程绝对坐标不受系统指针加速影响。
    /// 点击前校验光标实际落点：偏差&gt;容差 → 直接绝对钉正一次 → 再校验；仍偏 → 放弃点击并返回 false（fail-open，绝不盲点）。
    /// 点击永远只发生在校验通过的光标位置。容差 = humanizer.mouse.verify_tolerance_px。</summary>
    public static bool ClickAt(int screenX, int screenY)
    {
        int tol = Math.Clamp((Config.Mouse ?? new MousePathConfig()).VerifyTolerancePx, 2, 30);
        MoveHumanized(screenX, screenY); // 拟人弧线路径，末点即目标
        Thread.Sleep(Rand(Config.ClickPauseMin, Config.ClickPauseMax)); // 到达后的自然停顿

        if (!VerifyLanding(screenX, screenY, tol, out POINT actual))
        {
            MoveAbsoluteTo(screenX, screenY); // 直接绝对钉正（无拟人/无过冲），排除末点丢失/轻微扰动
            Thread.Sleep(10);
            if (!VerifyLanding(screenX, screenY, tol, out actual))
            {
                Console.WriteLine($"[Input] ⛔ 点击被安全网拦截：光标实际 ({actual.X},{actual.Y}) 偏离目标 ({screenX},{screenY}) 超 {tol}px（疑似游戏抢光标/移动末点丢失）。未点击，保持当前状态。");
                return false;
            }
            Console.WriteLine($"[Input] 首次落点偏离，已直接钉正: 光标 ({actual.X},{actual.Y})");
        }

        // P2-10：DOWN 先登记进急停释放清单，DOWN…UP 包 try/finally——中间异常（如 SendInput 被拒/焦点突变）
        // 也会补发 UP，急停 ReleaseAllHeldKeys 才放得掉这颗键，防左键卡死
        lock (HeldLock) _ = HeldMouseButtons.Add(MOUSEEVENTF_LEFTDOWN);
        try
        {
            SendMouse(MOUSEEVENTF_LEFTDOWN);
            Thread.Sleep(Rand(Config.PressHoldMin, Config.PressHoldMax)); // 按下到抬起的按压时长
        }
        finally
        {
            SendMouse(MOUSEEVENTF_LEFTUP);
            lock (HeldLock) _ = HeldMouseButtons.Remove(MOUSEEVENTF_LEFTDOWN);
        }
        Console.WriteLine($"[Input] 左键单击 屏幕({screenX}, {screenY})，落点校验通过（实际 {actual.X},{actual.Y}）");
        return true;
    }

    private static bool VerifyLanding(int screenX, int screenY, int tol, out POINT actual)
    {
        GetCursorPos(out actual);
        Console.WriteLine($"[Input] 光标落点校验: ({actual.X}, {actual.Y})，目标 ({screenX}, {screenY})，容差 {tol}px");
        return Math.Abs(actual.X - screenX) <= tol && Math.Abs(actual.Y - screenY) <= tol;
    }

    /// <summary>绝对定位：SendInput 归一化坐标（0-65535），不受鼠标加速影响。</summary>
    public static void MoveAbsoluteTo(int screenX, int screenY) => SendAbsolute(screenX, screenY);

    /// <summary>滚轮滚动：delta 为 ±120 的整数倍（正=向上，负=向下；滚轮作用于光标所在区域）。</summary>
    public static void ScrollWheel(int delta)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { dx = 0, dy = 0, mouseData = (uint)delta, dwFlags = MOUSEEVENTF_WHEEL, time = 0, dwExtraInfo = IntPtr.Zero }
        };
        Send(ref input);
        Console.WriteLine($"[Input] 滚轮 {delta}");
    }

    /// <summary>拟人化移动鼠标到屏幕坐标：随机弧线 + 缓动 + 过冲 + 抖动，全程绝对坐标（不受系统指针加速影响），
    /// 路径末点精确等于目标（无需再“钉”一次）。参数来自 runtime.json 的 humanizer.mouse。</summary>
    public static void MoveHumanized(int screenX, int screenY)
    {
        GetCursorPos(out POINT cur);
        var cfg = Config.Mouse ?? new MousePathConfig();
        var path = MousePathPlanner.Plan(new ScreenPoint(cur.X, cur.Y), new ScreenPoint(screenX, screenY), cfg, Rng);
        int lo = Math.Max(1, (int)(cfg.StepIntervalMs * 0.7));
        int hi = Math.Max(1, (int)(cfg.StepIntervalMs * 1.3));
        foreach (var p in path)
        {
            SendAbsolute(p.X, p.Y);
            Thread.Sleep(Rand(lo, hi));
        }
        Console.WriteLine($"[Input] 拟人鼠标移动 ({cur.X},{cur.Y}) → ({screenX},{screenY})，{path.Count} 步");
    }

    private static void SendAbsolute(int screenX, int screenY)
    {
        int w = GetSystemMetrics(SM_CXSCREEN);
        int h = GetSystemMetrics(SM_CYSCREEN);
        // P3：负坐标（游戏窗口在主屏左侧/上方的多显示器布局）经 (uint) 强转会回绕成垃圾归一化值——
        // 钳回主屏范围并告警。完整多屏支持（MOUSEEVENTF_VIRTUALDESK + 虚拟屏幕归一化）属行为变更，需主力拍板。
        int cx = Math.Clamp(screenX, 0, Math.Max(0, w - 1));
        int cy = Math.Clamp(screenY, 0, Math.Max(0, h - 1));
        if (cx != screenX || cy != screenY)
            Console.WriteLine($"[Input] ⚠️ 目标 ({screenX},{screenY}) 超出主屏 {w}x{h}，已钳到 ({cx},{cy})（多显示器越屏定位未支持）");
        uint nx = (uint)(cx * 65535 / Math.Max(1, w - 1));
        uint ny = (uint)(cy * 65535 / Math.Max(1, h - 1));
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dx = (int)nx,
                dy = (int)ny,
                mouseData = 0,
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            }
        };
        Send(ref input);
    }

    /// <summary>平滑移动鼠标到屏幕坐标（从当前位置按缓动曲线分步移动，精确落在目标点）。</summary>
    public static void MoveTo(int screenX, int screenY)
    {
        GetCursorPos(out POINT cur);
        MoveRelative(screenX - cur.X, screenY - cur.Y);
    }

    /// <summary>相对移动鼠标（dx/dy 像素增量）——视角转动用。自动分段缓动 + 抖动。</summary>
    public static void MoveRelative(int dx, int dy)
    {
        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (dist <= 2)
        {
            SendRelative(dx, dy);
            Console.WriteLine($"[Input] 鼠标微调 ({dx}, {dy})");
            return;
        }

        // 分段数：距离越大步数越多，加随机扰动避免机械规律
        int steps = Math.Clamp(dist / 70 + Rand(Config.StepExtraMin, Config.StepExtraMax), 4, 28);

        // 正弦缓动权重（起步慢→中途快→收尾慢）× 随机抖动，重归一化保证总和精确等于 (dx, dy)
        var weights = new double[steps];
        double wsum = 0;
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            double s = Math.Sin(Math.PI * t);
            double jitter = Config.StepJitterMin + Rng.NextDouble() * (Config.StepJitterMax - Config.StepJitterMin);
            weights[i - 1] = s * jitter;
            wsum += weights[i - 1];
        }

        double accX = 0, accY = 0;
        long lastX = 0, lastY = 0;
        for (int i = 0; i < steps; i++)
        {
            accX += dx * weights[i] / wsum;
            accY += dy * weights[i] / wsum;
            long ix = (long)Math.Round(accX);
            long iy = (long)Math.Round(accY);
            int sx = (int)(ix - lastX);
            int sy = (int)(iy - lastY);
            lastX = ix;
            lastY = iy;
            if (sx == 0 && sy == 0) continue;
            SendRelative(sx, sy);
            Thread.Sleep(Rand(Config.MouseStepDelayMin, Config.MouseStepDelayMax)); // 每步随机间隔
        }

        Console.WriteLine($"[Input] 鼠标相对移动 ({dx}, {dy})，{steps} 步缓动完成");
    }

    /// <summary>按下并松开一个键（短按，按下时长随机）。支持 "a+b" 组合串与鼠标键 token。
    /// finally 只释放本次按下的键（逆序）——与 PressKeys 持续按住混用时不误放移动键（评审 P2-10 附带）。</summary>
    public static void PressKey(string key)
    {
        var tokens = SplitCombo(key);
        if (tokens.Count == 0) return;
        var pressedVks = new List<ushort>();
        var pressedMouse = new List<uint>();
        try
        {
            foreach (var t in tokens)
            {
                if (TokenDown(t, pressedVks, pressedMouse)) // 已在按下状态则不算本次按下
                    Thread.Sleep(Rand(Config.KeyGapMin, Config.KeyGapMax));
            }
            Thread.Sleep(Rand(Config.KeyTapMin, Config.KeyTapMax));
        }
        finally
        {
            foreach (var down in pressedMouse.AsEnumerable().Reverse())
                SendMouse(down << 1); // UP = DOWN << 1
            foreach (var vk in pressedVks.AsEnumerable().Reverse())
                SendKeyEvent(vk, KEYEVENTF_KEYUP);
            lock (HeldLock)
            {
                foreach (var vk in pressedVks) HeldKeys.Remove(vk);
                foreach (var down in pressedMouse) HeldMouseButtons.Remove(down);
            }
        }
        Console.WriteLine($"[Input] 按键 {key}");
    }

    /// <summary>按住一组键约 durationMs 后松开（长按，移动用；时长按配置抖动）。</summary>
    public static void HoldKey(string key, int durationMs) => HoldKeys(new[] { key }, durationMs);

    /// <summary>同时按住多个键/组合串（如 sprint 前进 = ["move_forward", "sprint"]，或 "ctrl+left"），
    /// 依次按下 → 保持 → 兜底全释放。任何异常都会确保全部松开。</summary>
    public static void HoldKeys(IReadOnlyList<string> keys, int durationMs)
    {
        var tokens = FlattenTokens(keys);
        if (tokens.Count == 0) return;

        double jitter = Config.HoldJitterMin + Rng.NextDouble() * (Config.HoldJitterMax - Config.HoldJitterMin);
        int hold = (int)(durationMs * jitter);
        Console.WriteLine($"[Input] 组合长按 [{string.Join("+", tokens)}] 请求 {durationMs}ms → 实际 {hold}ms");
        try
        {
            foreach (var t in tokens)
            {
                TokenDown(t);
                Thread.Sleep(Rand(Config.KeyGapMin, Config.KeyGapMax)); // 依次按下，模拟人手顺序
            }
            Thread.Sleep(Math.Max(30, hold));
        }
        finally
        {
            ReleaseAllHeldKeys(); // 兜底全释放
        }
    }

    /// <summary>同时按住多个键并保持（不自动松开），供"持续移动"类场景使用；
    /// 结束前必须调用 <see cref="ReleaseAllHeldKeys"/>（建议配 try/finally 或急停处理）。</summary>
    public static void PressKeys(IReadOnlyList<string> keys)
    {
        var tokens = FlattenTokens(keys);
        if (tokens.Count == 0) return;
        foreach (var t in tokens)
        {
            TokenDown(t);
            Thread.Sleep(Rand(Config.KeyGapMin, Config.KeyGapMax));
        }
        Console.WriteLine($"[Input] 持续按住 [{string.Join("+", tokens)}]（需手动释放）");
    }

    /// <summary>释放所有仍处于按下状态的键/鼠标键（急停/异常兜底用，可重复调用）。</summary>
    public static void ReleaseAllHeldKeys()
    {
        ushort[] held;
        uint[] heldMouse;
        lock (HeldLock)
        {
            held = HeldKeys.ToArray();
            heldMouse = HeldMouseButtons.ToArray();
        }
        foreach (var vk in held.Reverse())
            SendKeyEvent(vk, KEYEVENTF_KEYUP);
        foreach (var down in heldMouse.Reverse())
            SendMouse(down << 1); // UP = DOWN << 1（left/right/middle 均如此）
        lock (HeldLock)
        {
            HeldKeys.Clear();
            HeldMouseButtons.Clear();
        }
    }

    private static void TokenDown(string token)
    {
        // 长按/持续按住路径用（HoldKeys/PressKeys）：由调用方的 finally 统一 ReleaseAllHeldKeys 兜底
        var vks = new List<ushort>();
        var mouse = new List<uint>();
        _ = TokenDown(token, vks, mouse);
    }

    /// <summary>按下单个 token 并登记到本次按下清单（PressKey 用：finally 只释放清单内的键）。
    /// 已在按下状态（重复 down）返回 false。</summary>
    private static bool TokenDown(string token, List<ushort> pressedVks, List<uint> pressedMouse)
    {
        if (IsMouseToken(token))
        {
            uint down = MouseDownFlag(token);
            lock (HeldLock)
            {
                if (!HeldMouseButtons.Add(down)) return false; // 已在按下状态
            }
            SendMouse(down);
            pressedMouse.Add(down);
            return true;
        }

        ushort vk = MapKeyName(token);
        if (vk == 0) throw new ArgumentException($"不认识的按键名: {token}");
        lock (HeldLock)
        {
            if (!HeldKeys.Add(vk)) return false; // 已在按下状态，避免重复 down
        }
        SendKeyEvent(vk, 0);
        pressedVks.Add(vk);
        return true;
    }

    private static bool IsMouseToken(string token) =>
        token.Equals("left", StringComparison.OrdinalIgnoreCase)
        || token.Equals("right", StringComparison.OrdinalIgnoreCase)
        || token.Equals("middle", StringComparison.OrdinalIgnoreCase);

    private static uint MouseDownFlag(string token) => token.ToLowerInvariant() switch
    {
        "left" => MOUSEEVENTF_LEFTDOWN,
        "right" => MOUSEEVENTF_RIGHTDOWN,
        "middle" => MOUSEEVENTF_MIDDLEDOWN,
        _ => 0,
    };

    private static List<string> FlattenTokens(IReadOnlyList<string> keys)
    {
        var tokens = new List<string>();
        foreach (var k in keys)
            tokens.AddRange(SplitCombo(k));
        return tokens;
    }

    /// <summary>把 "ctrl+left" 之类的组合串按 '+' 拆成 token 列表（供组合键与测试用）。</summary>
    public static IReadOnlyList<string> SplitCombo(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Array.Empty<string>();
        return key.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>修饰键用左侧具体键码（VK_LSHIFT/LCONTROL/LMENU）。
    /// 部分游戏引擎不认通用 VK_SHIFT(0x10)/VK_CONTROL(0x11)/VK_MENU(0x12)，会导致修饰键无效。</summary>
    private static ushort NormalizeVk(ushort vk) => vk switch
    {
        0x10 => 0xA0, // VK_SHIFT   → VK_LSHIFT
        0x11 => 0xA2, // VK_CONTROL → VK_LCONTROL
        0x12 => 0xA4, // VK_MENU    → VK_LMENU
        _ => vk,
    };

    private static void SendKeyEvent(ushort vk, uint flags)
    {
        vk = NormalizeVk(vk);
        ushort scan = (ushort)MapVirtualKey(vk, 0); // 自动补扫描码，兼容要求硬件级输入的引擎
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero }
        };
        Send(ref input);
    }

    /// <summary>指定窗口是否为当前前台窗口。</summary>
    public static bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    /// <summary>
    /// 确保窗口持有键盘焦点（FPS 需要焦点才能收到移动/鼠标输入）。
    /// 经典做法：先模拟一次 Alt 键让本进程暂时获得前台权限，再 SetForegroundWindow。
    /// </summary>
    public static bool EnsureForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (GetForegroundWindow() == hwnd) return true;

        TapKey(VK_LMENU);
        bool ok = SetForegroundWindow(hwnd);
        if (!ok)
        {
            TapKey(VK_LMENU);
            ok = SetForegroundWindow(hwnd);
        }
        Thread.Sleep(150);
        bool success = GetForegroundWindow() == hwnd;
        Console.WriteLine(success
            ? $"[Input] 焦点已切到目标窗口 0x{hwnd.ToInt64():X}"
            : $"[Input] ⚠️ 焦点切换失败（当前前台 0x{GetForegroundWindow().ToInt64():X}），按键可能不会到达游戏！");
        return success;
    }

    private static void TapKey(ushort vk)
    {
        SendKeyEvent(vk, 0);
        Thread.Sleep(20);
        SendKeyEvent(vk, KEYEVENTF_KEYUP);
        Thread.Sleep(20);
    }

    /// <summary>按键名/字符 → 虚拟键码。支持单字符（字母/数字/常用符号）及常用名（组合串请先 SplitCombo）。</summary>
    public static ushort MapKeyName(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
            return c switch
            {
                ' ' => 0x20,
                '=' or '+' => 0xBB,
                '-' or '_' => 0xBD,
                '[' => 0xDB,
                ']' => 0xDD,
                ';' or ':' => 0xBA,
                '\'' or '"' => 0xDE,
                ',' or '<' => 0xBC,
                '.' or '>' => 0xBE,
                '/' or '?' => 0xBF,
                '`' or '~' => 0xC0,
                '\\' or '|' => 0xDC,
                _ => 0,
            };
        }

        return key.ToLowerInvariant() switch
        {
            "enter" or "return" => 0x0D,
            "space" => 0x20,
            "esc" or "escape" => 0x1B,
            "tab" => 0x09,
            "shift" => 0x10,
            "ctrl" or "control" => 0x11,
            "alt" => 0x12,
            "capslock" => 0x14,
            "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73,
            "f5" => 0x74, "f6" => 0x75, "f7" => 0x76, "f8" => 0x77,
            "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
            _ => 0,
        };
    }

    private static void SendRelative(int dx, int dy)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                mouseData = 0,
                dwFlags = MOUSEEVENTF_MOVE,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            }
        };
        Send(ref input);
    }

    private static void SendMouse(uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { dx = 0, dy = 0, mouseData = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero }
        };
        Send(ref input);
    }

    private static void Send(ref INPUT input)
    {
        if (SendInput(1, ref input, Marshal.SizeOf<INPUT>()) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput 失败");
    }

    // ===== Win32 结构 =====
    // INPUT = type(4) + 对齐填充(4) + 联合(32) = 40 字节 (x64)

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
