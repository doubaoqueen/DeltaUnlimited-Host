using System.Drawing;
using System.Runtime.InteropServices;

namespace DeltaUnlimited.Gui;

/// <summary>运行时绘制应用图标（托盘+窗口共用），无外部素材依赖。</summary>
public static class AppIconFactory
{
    /// <summary>32×32 圆底 + “DU” 字样的图标。</summary>
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var back = new SolidBrush(Color.FromArgb(24, 42, 58));
            g.FillEllipse(back, 1, 1, 30, 30);
            using var ring = new Pen(Color.FromArgb(0, 170, 220), 2f);
            g.DrawEllipse(ring, 2, 2, 28, 28);
            using var font = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.White);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("DU", font, text, new RectangleF(2, 2, 28, 28), sf);
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
