using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeltaUnlimited.Input;

/// <summary>
/// 键鼠输出（Helmsman）：SendInput 模拟真实输入。
/// 绝对移动（定位点击）用屏幕坐标；相对移动（视角转动）用增量。
///
/// 拟人化 v1（内置于所有动作，调用方无需关心）：
///   - 鼠标移动：正弦缓动分段 + 每步随机抖动/间隔，不再瞬移（贝塞尔曲线留待 M2 升级）；
///   - 点击：到达后随机停顿、按下与抬起之间随机间隔；
///   - 按键：按下持续时间加入随机抖动。
/// 说明：拟人化降低"机械感"，不等于免疫风控；实战频率与行为仍需自行控制（见大纲 §7）。
/// </summary>
public static class InputService
{
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const ushort VK_ALT = 0x12;

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private static readonly Random Rng = new();

    /// <summary>把鼠标移动到屏幕坐标 (x, y) 并左键单击（移动为分段缓动）。</summary>
    public static void ClickAt(int screenX, int screenY)
    {
        MoveTo(screenX, screenY);
        Thread.Sleep(Rng.Next(120, 280)); // 到达后的自然停顿
        SendMouse(MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(Rng.Next(35, 90));   // 按下到抬起的自然间隔
        SendMouse(MOUSEEVENTF_LEFTUP);
        Console.WriteLine($"[Input] 左键单击 屏幕({screenX}, {screenY})");
    }

    /// <summary>平滑移动鼠标到屏幕坐标（从当前位置按缓动曲线分步移动，精确落在目标点）。</summary>
    public static void MoveTo(int screenX, int screenY)
    {
        GetCursorPos(out POINT cur);
        int dx = screenX - cur.X;
        int dy = screenY - cur.Y;
        MoveRelative(dx, dy);
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
        int steps = Math.Clamp(dist / 70 + Rng.Next(3, 8), 4, 28);

        // 正弦缓动权重（起步慢→中途快→收尾慢）× 随机抖动，重归一化保证总和精确等于 (dx, dy)
        var weights = new double[steps];
        double wsum = 0;
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            double s = Math.Sin(Math.PI * t);            // 0→1→0 缓动
            double jitter = 0.8 + Rng.NextDouble() * 0.4; // 0.8–1.2 抖动
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
            Thread.Sleep(Rng.Next(3, 11)); // 每步随机间隔，模拟手部抖动轨迹
        }

        Console.WriteLine($"[Input] 鼠标相对移动 ({dx}, {dy})，{steps} 步缓动完成");
    }

    /// <summary>按下并松开一个键（短按，按下时长随机）。</summary>
    public static void PressKey(string key)
    {
        ushort vk = MapKeyName(key);
        if (vk == 0) throw new ArgumentException($"不认识的按键名: {key}");
        KeyDown(vk);
        Thread.Sleep(Rng.Next(35, 90));
        KeyUp(vk);
        Console.WriteLine($"[Input] 按键 {key} (VK=0x{vk:X2})");
    }

    /// <summary>按住一个键约 durationMs 后松开（长按，移动用；时长 ±10% 抖动）。</summary>
    public static void HoldKey(string key, int durationMs)
    {
        ushort vk = MapKeyName(key);
        if (vk == 0) throw new ArgumentException($"不认识的按键名: {key}");
        int hold = (int)(durationMs * (0.92 + Rng.NextDouble() * 0.16));
        Console.WriteLine($"[Input] 长按 {key} (VK=0x{vk:X2}) 请求 {durationMs}ms → 实际 {hold}ms");
        KeyDown(vk);
        Thread.Sleep(Math.Max(30, hold));
        KeyUp(vk);
    }

    private static void KeyDown(ushort vk) => SendKeyEvent(vk, 0);
    private static void KeyUp(ushort vk) => SendKeyEvent(vk, KEYEVENTF_KEYUP);

    private static void SendKeyEvent(ushort vk, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero }
        };
        Send(ref input);
    }

    /// <summary>
    /// 确保窗口持有键盘焦点（FPS 需要焦点才能收到移动/鼠标输入）。
    /// 经典做法：先模拟一次 Alt 键让本进程暂时获得前台权限，再 SetForegroundWindow。
    /// </summary>
    public static bool EnsureForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (GetForegroundWindow() == hwnd) return true;

        TapKey(VK_ALT);
        bool ok = SetForegroundWindow(hwnd);
        if (!ok)
        {
            TapKey(VK_ALT);
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

    /// <summary>按键名/字符 → 虚拟键码。支持单字符（字母/数字/常用符号）及常用名。</summary>
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
}
