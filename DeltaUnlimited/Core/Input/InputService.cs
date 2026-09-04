using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeltaUnlimited.Input;

/// <summary>
/// 键鼠输出（Helmsman 雏形）：SendInput 模拟真实输入，绝对屏幕坐标。
/// 元素坐标（游戏画面内）→ 屏幕坐标的换算由调用方完成（客户区偏移 + 元素坐标）。
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

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    /// <summary>把鼠标绝对移动到屏幕坐标 (x, y) 并左键单击。</summary>
    public static void ClickAt(int screenX, int screenY)
    {
        MoveTo(screenX, screenY);
        Thread.Sleep(80);
        SendMouse(MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(50);
        SendMouse(MOUSEEVENTF_LEFTUP);
        Console.WriteLine($"[Input] 左键单击 屏幕({screenX}, {screenY})");
    }

    /// <summary>绝对移动鼠标（SendInput 使用 0–65535 归一化坐标）。</summary>
    public static void MoveTo(int screenX, int screenY)
    {
        int w = GetSystemMetrics(SM_CXSCREEN);
        int h = GetSystemMetrics(SM_CYSCREEN);
        uint nx = (uint)(screenX * 65535 / Math.Max(1, w - 1));
        uint ny = (uint)(screenY * 65535 / Math.Max(1, h - 1));

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

    /// <summary>按下并松开一个键。支持单字符（字母/数字，自动转大写）及常用名（enter/space/esc/f1-f12 等）。</summary>
    public static void PressKey(string key)
    {
        ushort vk = MapKeyName(key);
        if (vk == 0) throw new ArgumentException($"不认识的按键名: {key}");

        var down = new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = 0, time = 0, dwExtraInfo = IntPtr.Zero }
        };
        Send(ref down);
        Thread.Sleep(40);
        down.ki.dwFlags = KEYEVENTF_KEYUP;
        Send(ref down);
        Console.WriteLine($"[Input] 按键 {key} (VK=0x{vk:X2})");
    }

    /// <summary>按键名 → 虚拟键码。</summary>
    private static ushort MapKeyName(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
            return c switch
            {
                ' ' => 0x20,
                '\r' or '\n' => 0x0D,
                (char)0x1B => 0x1B,
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
            "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73,
            "f5" => 0x74, "f6" => 0x75, "f7" => 0x76, "f8" => 0x77,
            "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
            _ => 0,
        };
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
