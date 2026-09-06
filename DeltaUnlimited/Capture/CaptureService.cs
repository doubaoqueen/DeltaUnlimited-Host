using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using OpenCvSharp;

namespace DeltaUnlimited.Capture;

/// <summary>
/// 窗口定位 + GDI 截屏（开发期方案，适用窗口化游戏）。
/// 思路：运行时按标题关键字找到游戏窗口 → 取客户区矩形 → 只截该区域。
/// 后期要换 Windows.Graphics.Capture 时，保持本类公开方法不变即可。
/// </summary>
public static class CaptureService
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const uint DIB_RGB_COLORS = 0;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int HWND_TOPMOST = -1;
    private const int HWND_NOTOPMOST = -2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private static bool _dpiDone;

    /// <summary>设置 Per-Monitor V2 DPI 感知，避免缩放下坐标错位。失败则忽略（非致命）。</summary>
    public static void EnsureDpiAwareness()
    {
        if (_dpiDone) return;
        _dpiDone = true;
        try
        {
            _ = SetProcessDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        }
        catch
        {
            // 系统太老或已被宿主设置过，忽略
        }
    }

    /// <summary>枚举所有可见顶层窗口（标题非空）。</summary>
    public static List<(IntPtr Hwnd, string Title)> ListTopLevelWindows()
    {
        var list = new List<(IntPtr, string)>();
        EnumWindows((hWnd, _) =>
        {
            if (IsWindowVisible(hWnd))
            {
                var sb = new StringBuilder(1024);
                if (GetWindowText(hWnd, sb, sb.Capacity) > 0)
                    list.Add((hWnd, sb.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>判断窗口标题是否属于控制台宿主（cmd/PowerShell 标题会包含命令行，必须排除）。</summary>
    private static bool IsConsoleHostTitle(string title) =>
        title.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase)
        || title.Contains("powershell", StringComparison.OrdinalIgnoreCase)
        || title.Contains("windowsterminal", StringComparison.OrdinalIgnoreCase);

    /// <summary>按标题关键字（忽略大小写）找第一个匹配窗口，找不到返回 IntPtr.Zero。
    /// 排除自己的控制台窗口与命令行走廊窗口（标题会显示命令行，容易自匹配）。</summary>
    public static IntPtr FindWindowByTitle(string keyword)
    {
        var hit = ListTopLevelWindows().FirstOrDefault(w => w.Hwnd != GetConsoleWindow() && !IsConsoleHostTitle(w.Title) && w.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return hit.Hwnd;
    }

    /// <summary>客户区在屏幕上的矩形 (X, Y, W, H)。窗口最小化/无效时返回 null。</summary>
    public static (int X, int Y, int W, int H)? GetClientScreenRect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        if (!GetClientRect(hwnd, out RECT client)) return null;
        int w = client.Right - client.Left;
        int h = client.Bottom - client.Top;
        if (w <= 0 || h <= 0) return null; // 最小化

        var origin = new POINT(0, 0);
        if (!ClientToScreen(hwnd, ref origin)) return null;
        return (origin.X, origin.Y, w, h);
    }

    public sealed record CaptureOutcome(string Path, int X, int Y, int W, int H, double MeanR, double MeanG, double MeanB)
    {
        /// <summary>平均亮度太低 → 疑似黑屏（全屏独占/被遮挡/无桌面会话）。</summary>
        public bool LooksBlack => (MeanR + MeanG + MeanB) / 3.0 < 8.0;
        public override string ToString() => $"已保存: {Path}\n  区域: ({X}, {Y}) {W}x{H}  平均色: BGR({MeanB:F1}, {MeanG:F1}, {MeanR:F1}){(LooksBlack ? "  ⚠️ 疑似黑屏！" : "")}";
    }

    /// <summary>截整个桌面。用于截屏冒烟验证（无需进游戏）。</summary>
    public static CaptureOutcome CaptureDesktop(string pngPath)
    {
        EnsureDpiAwareness();
        int w = GetSystemMetrics(SM_CXSCREEN);
        int h = GetSystemMetrics(SM_CYSCREEN);
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            return CaptureRegion(hdc, 0, 0, w, h, pngPath);
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <summary>窗口是否处于最小化状态（GetClientRect 对最小化窗口仍返回正常尺寸，必须用 IsIconic 判断）。</summary>
    public static bool IsMinimized(IntPtr hwnd) => hwnd != IntPtr.Zero && IsIconic(hwnd);

    /// <summary>窗口在屏幕上的矩形 (X, Y, W, H)（含边框标题栏，调试用）。</summary>
    public static (int X, int Y, int W, int H)? GetWindowScreenRect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        if (!GetWindowRect(hwnd, out RECT r)) return null;
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>把窗口提到最上层（不影响键盘焦点，供截图/点击前使用）。</summary>
    public static void RaiseWindow(IntPtr hwnd) =>
        _ = SetWindowPos(hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

    /// <summary>取消窗口的最上层状态，还原普通层级。</summary>
    public static void UnraiseWindow(IntPtr hwnd) =>
        _ = SetWindowPos(hwnd, new IntPtr(HWND_NOTOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);

    /// <summary>按标题关键字找窗口并截其客户区。</summary>
    public static CaptureOutcome CaptureWindowClient(string titleKeyword, string pngPath)
    {
        EnsureDpiAwareness();
        // 排除自己的控制台窗口（cmd 标题含命令行，会把自己匹配进来）
        IntPtr ownConsole = GetConsoleWindow();
        var wins = ListTopLevelWindows();
        var hit = wins.FirstOrDefault(w => w.Hwnd != ownConsole && !IsConsoleHostTitle(w.Title) && w.Title.Contains(titleKeyword, StringComparison.OrdinalIgnoreCase));
        if (hit.Hwnd == IntPtr.Zero)
        {
            var names = string.Join(", ", ListTopLevelWindows().Take(12).Select(w => $"“{w.Title}”"));
            throw new InvalidOperationException($"没找到标题含 “{titleKeyword}” 的窗口。当前可见窗口: {names}");
        }
        IntPtr hwnd = hit.Hwnd;

        var client = GetClientScreenRect(hwnd)
            ?? throw new InvalidOperationException("窗口无效或最小化，请还原窗口后重试");
        var wnd = GetWindowScreenRect(hwnd);
        Console.WriteLine($"[debug] 命中窗口: “{hit.Title}”  hwnd=0x{hwnd.ToInt64():X} 窗口矩形={(wnd.HasValue ? $"({wnd.Value.X},{wnd.Value.Y}) {wnd.Value.W}x{wnd.Value.H}" : "?")} 客户区=({client.X},{client.Y}) {client.W}x{client.H}");

        // 截图前把目标窗口提到最上层（否则会被 cmd 等窗口遮挡，截到别人的像素），截完还原
        Console.WriteLine("[debug] 已把目标窗口提到最上层...");
        _ = SetWindowPos(hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        System.Threading.Thread.Sleep(300);
        try
        {
            // 源用桌面 DC + 客户区屏幕坐标（不用窗口 DC：部分环境下窗口 DC 拷贝会全黑）
            IntPtr hdc = GetDC(IntPtr.Zero);
            try
            {
                return CaptureRegion(hdc, client.X, client.Y, client.W, client.H, pngPath);
            }
            finally
            {
                _ = ReleaseDC(IntPtr.Zero, hdc);
            }
        }
        finally
        {
            _ = SetWindowPos(hwnd, new IntPtr(HWND_NOTOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        }
    }

    /// <summary>从屏幕 DC 拷一块矩形区域 → 保存为 PNG。</summary>
    private static CaptureOutcome CaptureRegion(IntPtr hdcScreen, int srcX, int srcY, int w, int h, string pngPath)
    {
        var dir = Path.GetDirectoryName(pngPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var mat = CapturePixels(hdcScreen, srcX, srcY, w, h);
        if (!Cv2.ImWrite(pngPath, mat))
            throw new IOException($"保存截图失败: {pngPath}");
        Scalar mean = Cv2.Mean(mat);
        return new CaptureOutcome(pngPath, srcX, srcY, w, h, mean[0], mean[1], mean[2]);
    }

    /// <summary>截指定窗口客户区，返回内存中的 BGRA Mat（调用方负责 Dispose）——不落盘，供帧差/识别用。
    /// raiseAndWait=false 时跳过"置顶+等待"（连续采样提速用，要求窗口已在前台/最上层）。</summary>
    public static Mat CaptureWindowMat(string titleKeyword, bool raiseAndWait = true)
    {
        EnsureDpiAwareness();
        IntPtr hwnd = FindWindowByTitle(titleKeyword);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"没找到标题含 “{titleKeyword}” 的窗口");

        var client = GetClientScreenRect(hwnd)
            ?? throw new InvalidOperationException("窗口无效或最小化，请还原窗口后重试");

        if (raiseAndWait)
        {
            RaiseWindow(hwnd);
            System.Threading.Thread.Sleep(150);
        }
        try
        {
            IntPtr hdc = GetDC(IntPtr.Zero);
            try
            {
                return CapturePixels(hdc, client.X, client.Y, client.W, client.H);
            }
            finally
            {
                _ = ReleaseDC(IntPtr.Zero, hdc);
            }
        }
        finally
        {
            if (raiseAndWait) UnraiseWindow(hwnd);
        }
    }

    /// <summary>从屏幕 DC 拷一块矩形区域 → 独立内存的 BGRA Mat（深拷贝，调用方负责 Dispose）。</summary>
    private static Mat CapturePixels(IntPtr hdcScreen, int srcX, int srcY, int w, int h)
    {
        if (w <= 0 || h <= 0) throw new InvalidOperationException("截图区域尺寸无效");

        IntPtr memDc = CreateCompatibleDC(hdcScreen);
        if (memDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateCompatibleDC 失败");
        try
        {
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = 40,
                    biWidth = w,
                    biHeight = -h, // 负值 = 自上而下的行序，直接对应 Mat
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                }
            };

            IntPtr hBitmap = CreateDIBSection(hdcScreen, ref bmi, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDIBSection 失败");

            IntPtr old = SelectObject(memDc, hBitmap);
            try
            {
                bool ok = BitBlt(memDc, 0, 0, w, h, hdcScreen, srcX, srcY, SRCCOPY | CAPTUREBLT);
                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "BitBlt 失败");

                // DIB 像素 → Mat(CV_8UC4, BGRA)，Clone 深拷贝后即可释放 GCHandle
                // 关键：必须在 DeleteObject(hBitmap) 之前拷贝，否则 bits 指向的内存已被释放
                byte[] buf = new byte[w * h * 4];
                Marshal.Copy(bits, buf, 0, buf.Length);
                var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try
                {
                    using var view = Mat.FromPixelData(h, w, MatType.CV_8UC4, handle.AddrOfPinnedObject(), 0);
                    return view.Clone();
                }
                finally
                {
                    handle.Free();
                }
            }
            finally
            {
                _ = SelectObject(memDc, old);
                _ = DeleteObject(hBitmap);
            }
        }
        finally
        {
            _ = DeleteDC(memDc);
        }
    }

    // ===== Win32 P/Invoke =====

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public POINT(int x, int y) { X = x; Y = y; }
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, int rop);
}
