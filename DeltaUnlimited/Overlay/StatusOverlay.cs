using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeltaUnlimited.Overlay;

/// <summary>
/// 游戏画面上的状态悬浮面板：分层窗口（置顶、鼠标穿透、不抢焦点），覆盖在游戏客户区上，
/// 左上角半透明面板显示状态文本（数据来自 logs/status.log 的尾部）。后台线程渲染。
/// </summary>
public static class StatusOverlay
{
    private static readonly object Sync = new();
    private static Thread? _thread;
    private static LoopState? _loop; // 每代渲染线程自己的停止标志（评审 P2-8①：共享 _stop 在 Join 超时后被 Start 复位会复活旧渲染线程）

    private sealed class LoopState { public volatile bool Stop; }

    private static string _header = "DeltaUnlimited 状态";
    private static string[] _lines = Array.Empty<string>();
    private static bool _dirty = true;
    private static IntPtr _hwnd;
    private static IntPtr _insertAfter = IntPtr.Zero;
    private static volatile int _panelW = 430;
    private static volatile int _panelH = 100;

    /// <summary>在指定屏幕矩形上启动悬浮面板（建议传游戏客户区矩形）。重复调用会先关闭旧的。</summary>
    public static void Start(int x, int y, int w, int h)
    {
        Stop();
        var loop = new LoopState();
        _loop = loop;
        _thread = new Thread(() => RenderLoop(loop, x, y, w, h)) { IsBackground = true, Name = "StatusOverlay" };
        _thread.Start();
    }

    /// <summary>更新面板文本（线程安全，只置脏标记，渲染线程读取）。</summary>
    public static void SetStatus(string header, string[] lines)
    {
        lock (Sync)
        {
            _header = header;
            _lines = (string[])lines.Clone();
            _dirty = true;
        }
    }

    public static void Stop()
    {
        var loop = _loop;
        if (loop != null) loop.Stop = true; // 只停当前代线程：旧线程持有自己的状态，不会被新一次 Start 复活（评审 P2-8①）
        _thread?.Join(600);
        _thread = null;
    }

    /// <summary>把面板平移到新位置（游戏窗口被拖动后跟踪用）；面板未创建返回 false。</summary>
    public static bool TryMoveTo(int x, int y)
    {
        IntPtr h = _hwnd;
        if (h == IntPtr.Zero) return false;
        return SetWindowPos(h, _insertAfter, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>把面板钉在指定窗口的上方一层（其他窗口盖过游戏时会自然遮住面板）。</summary>
    public static void SetInsertAfter(IntPtr hwndAfter)
    {
        _insertAfter = hwndAfter;
        if (_hwnd != IntPtr.Zero)
            _ = SetWindowPos(_hwnd, _insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static void RenderLoop(LoopState loop, int x, int y, int w, int h)
    {
        IntPtr hwnd = IntPtr.Zero, memDc = IntPtr.Zero, dib = IntPtr.Zero;
        IntPtr font = IntPtr.Zero, oldFont = IntPtr.Zero, oldBmp = IntPtr.Zero;
        string lastHeader = "";
        string[] lastLines = Array.Empty<string>();
        try
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Proc,
                hInstance = GetModuleHandleW(null),
                lpszClassName = "DU_StatusOverlay",
            };
            _ = RegisterClassExW(ref wc);

            hwnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
                "DU_StatusOverlay", "DUStatusOverlay", WS_POPUP,
                x, y, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "创建悬浮窗口失败");
            _hwnd = hwnd;
            _ = ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);

            // 32bpp 自上而下 DIB
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = 40,
                    biWidth = w,
                    biHeight = -h,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                }
            };
            IntPtr hdc0 = GetDC(IntPtr.Zero);
            memDc = CreateCompatibleDC(hdc0);
            dib = CreateDIBSection(hdc0, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            _ = ReleaseDC(IntPtr.Zero, hdc0);
            if (memDc == IntPtr.Zero || dib == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "创建悬浮面板画布失败");

            oldBmp = SelectObject(memDc, dib);
            font = CreateFontW(16, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Microsoft YaHei");
            oldFont = SelectObject(memDc, font);

            while (!loop.Stop)
            {
                // 泵消息：WM_NCHITTEST 与拖动依赖消息循环
                while (PeekMessageW(out MSG m, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
                {
                    _ = TranslateMessage(ref m);
                    _ = DispatchMessageW(ref m);
                }

                string header;
                string[] lines;
                bool dirty;
                lock (Sync)
                {
                    header = _header;
                    lines = _lines;
                    dirty = _dirty;
                    _dirty = false;
                }
                if (dirty || header != lastHeader || !lines.SequenceEqual(lastLines))
                {
                    Render(memDc, bits, w, h, header, lines);
                    var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 170, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
                    var dstPt = new POINT { X = x, Y = y };
                    if (GetWindowRect(hwnd, out RECT wr))
                    {
                        dstPt.X = wr.Left; // 尊重用户拖动后的位置
                        dstPt.Y = wr.Top;
                    }
                    var srcPt = new POINT { X = 0, Y = 0 };   // 源 DC 的起始点（必须 0,0）
                    var size = new SIZE { Width = w, Height = h };
                    _ = UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dstPt, ref size, memDc, ref srcPt, 0, ref blend, 2 /* ULW_ALPHA */);
                    lastHeader = header;
                    lastLines = lines;
                }
                Thread.Sleep(20);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠️ 悬浮面板渲染线程异常（面板可能不可见）: {ex.Message}");
        }
        finally
        {
            // 选入 DC 的对象先还原，字体再 DeleteObject（选中状态下删除会失败继续泄漏）（评审 P2-8②）
            if (memDc != IntPtr.Zero)
            {
                if (oldFont != IntPtr.Zero) _ = SelectObject(memDc, oldFont);
                if (oldBmp != IntPtr.Zero) _ = SelectObject(memDc, oldBmp);
            }
            if (font != IntPtr.Zero) _ = DeleteObject(font); // GDI 字体句柄每次 Start 补删，不再泄漏
            if (_hwnd == hwnd) _hwnd = IntPtr.Zero; // 只有还是自己的窗口才清（防 Join 超时后旧线程收尾清掉新线程的句柄）
            if (memDc != IntPtr.Zero) _ = DeleteDC(memDc);
            if (dib != IntPtr.Zero) _ = DeleteObject(dib);
            if (hwnd != IntPtr.Zero) _ = DestroyWindow(hwnd);
        }
    }

    /// <summary>渲染一帧：左下角面板（不透明深色，整体透明度由 UpdateLayeredWindow 的常量 Alpha 控制）+ GDI 文字。</summary>
    private static void Render(IntPtr memDc, IntPtr bits, int w, int h, string header, string[] lines)
    {
        int lineH = 21;
        int pad = 8;
        int panelH = pad + 24 + lines.Length * lineH + pad;
        int panelW = Math.Min(w, 430);
        int baseY = h - panelH - pad; // 锚定左下角
        int n = w * h * 4;
        byte[] buf = new byte[n];

        // 面板区域：不透明深色底（BGRA），面板外保持全 0（透明）
        for (int yy = baseY; yy < baseY + panelH; yy++)
        {
            for (int xx = pad; xx < pad + panelW; xx++)
            {
                int idx = (yy * w + xx) * 4;
                buf[idx] = 34;     // B
                buf[idx + 1] = 30; // G
                buf[idx + 2] = 26; // R
                buf[idx + 3] = 255;
            }
        }
        Marshal.Copy(buf, 0, bits, n);

        // 文字：标题（暖黄）+ 状态行（浅灰）
        _ = SetBkMode(memDc, 1 /* TRANSPARENT */);
        var rect = new RECT { Left = pad + 4, Top = baseY + pad + 2, Right = pad + panelW - 4, Bottom = baseY + panelH };
        _ = SetTextColor(memDc, (int)RGB(255, 214, 90));
        _ = DrawTextW(memDc, header, -1, ref rect, 0);
        rect.Top += 24;
        _ = SetTextColor(memDc, (int)RGB(228, 228, 228));
        var sb = new System.Text.StringBuilder();
        foreach (var l in lines) sb.AppendLine(l);
        _ = DrawTextW(memDc, sb.ToString(), -1, ref rect, 0);

        // GDI 在 32bpp DIB 上写字时 alpha 通道写 0：把非黑像素的 alpha 补成 255
        byte[] fix = new byte[n];
        Marshal.Copy(bits, fix, 0, n);
        for (int i = 0; i < n; i += 4)
        {
            if (fix[i + 3] == 0 && (fix[i] != 0 || fix[i + 1] != 0 || fix[i + 2] != 0))
                fix[i + 3] = 255;
        }
        Marshal.Copy(fix, 0, bits, n);

        // 记录面板几何，供命中测试（拖动手柄区域）使用
        _panelW = panelW;
        _panelH = panelH;
    }

    private static uint RGB(int r, int g, int b) => (uint)(r | (g << 8) | (b << 16));

    // ===== Win32 =====

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static readonly WndProc Proc = WndProcImpl;

    /// <summary>命中测试：面板区域=拖动手柄（HTCAPTION），其余区域穿透（HTTRANSPARENT）。</summary>
    private static IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCHITTEST)
        {
            int sx = unchecked((short)((long)lParam & 0xFFFF));
            int sy = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
            if (GetWindowRect(hWnd, out RECT wr))
            {
                const int pad = 8;
                int left = wr.Left + pad;
                int top = wr.Bottom - _panelH - pad; // 面板锚定左下角
                int right = left + _panelW;
                int bottom = top + _panelH;
                if (sx >= left && sx < right && sy >= top && sy < bottom)
                    return new IntPtr(HTCAPTION); // 按住面板可拖动
            }
            return new IntPtr(HTTRANSPARENT); // 其余区域鼠标穿透
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const int HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint WM_NCHITTEST = 0x0084;
    private const int HTCAPTION = 2;
    private const int HTTRANSPARENT = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public UIntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
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
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet, uint iOutPrecision, uint iClipPrecision,
        uint iQuality, uint iPitchAndFamily, string pszFaceName);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr hdc, int color);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);
}
