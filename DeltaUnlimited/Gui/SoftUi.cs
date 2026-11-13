using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeltaUnlimited.Gui;

/// <summary>软边 UI 小件：圆角卡片与圆角按钮——WinForms 默认直角太硬朗，统一做圆润。
/// 走自绘而非系统渲染，颜色全部由 ApplyTheme 按主题下发。</summary>
public static class SoftUi
{
    /// <summary>统一圆角半径（px）。</summary>
    public const int Radius = 10;

    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (r.Width < d || r.Height < d || radius <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>圆角卡片容器：Region 裁圆角 + 自绘柔边。子控件（无边框 TextBox 等）放里面，
/// 视觉上是一张完整的圆角卡。</summary>
public class SoftPanel : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.FromArgb(210, 212, 216);

    public SoftPanel()
    {
        ResizeRedraw = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = SoftUi.RoundedPath(ClientRectangle, SoftUi.Radius);
        Region?.Dispose();
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var path = SoftUi.RoundedPath(ClientRectangle, SoftUi.Radius);
        using var pen = new Pen(BorderColor, 1f);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>圆角参数区：FlowLayoutPanel 版圆角卡（Region 裁切 + 自绘柔边）。</summary>
public class SoftFlowPanel : FlowLayoutPanel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.FromArgb(210, 212, 216);

    public SoftFlowPanel()
    {
        ResizeRedraw = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = SoftUi.RoundedPath(ClientRectangle, SoftUi.Radius);
        Region?.Dispose();
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var path = SoftUi.RoundedPath(ClientRectangle, SoftUi.Radius);
        using var pen = new Pen(BorderColor, 1f);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>圆角按钮：自绘填充 + 柔边 + 居中文字（悬停微亮、禁用文字减淡）。圆角外的四角透出父容器底色。</summary>
public class SoftButton : Button
{
    private bool _hover;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.FromArgb(210, 212, 216);

    public SoftButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 留空：圆角外的四角透出父容器底色，不能刷成方块
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = _hover ? ControlPaint.Light(BackColor, 0.10f) : BackColor;
        using var path = SoftUi.RoundedPath(ClientRectangle, SoftUi.Radius);
        using (var brush = new SolidBrush(BackColor.IsEmpty ? SystemColors.Control : fill))
            g.FillPath(brush, path);
        using var pen = new Pen(BorderColor, 1f);
        g.DrawPath(pen, path);
        var textColor = Enabled ? ForeColor : Color.FromArgb(130, ForeColor);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}
