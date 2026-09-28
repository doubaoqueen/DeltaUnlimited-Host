using System.Drawing;
using System.Windows.Forms;
using DeltaUnlimited.Capture;
using DeltaUnlimited.Cli.Commands;
using DeltaUnlimited.Data;
using DeltaUnlimited.Vision;
using OpenCvSharp;

namespace DeltaUnlimited.Gui;

/// <summary>ROI 调参窗体：绿框即可通行性模型的全部视野，所见即所得。
/// 实时模式用 Timer 抓游戏窗口；静态模式画一张截图。所有修改只在按回车时写盘。
/// 注意：本文件同时使用 OpenCvSharp 与 WinForms——OpenCV 类型裸用，System.Drawing.Point/Size 需全限定（避二义性）。</summary>
public sealed class RoiTuneForm : Form
{
    private readonly AiVisionConfig _cfg;
    private readonly string _repoRoot;
    private readonly int _designW, _designH;
    private readonly IntPtr _hwnd;                  // Zero = 静态图模式
    private readonly Mat? _staticFrame;
    private readonly int[] _initial;
    private readonly PictureBox _pb = new();
    private readonly Label _lbl = new();
    private readonly System.Windows.Forms.Timer? _timer;
    private Rect _roi;
    private Mat? _lastNorm;                         // 实时模式最近一帧（设计分辨率）
    private bool _aspectLock = true;

    internal RoiTuneForm(AiVisionConfig cfg, string repoRoot, int designW, int designH, IntPtr hwnd, Mat? staticFrame)
    {
        _cfg = cfg;
        _repoRoot = repoRoot;
        _designW = designW;
        _designH = designH;
        _hwnd = hwnd;
        _staticFrame = staticFrame;
        _initial = cfg.Roi.ToArray();
        _roi = new Rect(_initial[0], _initial[1], _initial[2], _initial[3]);

        Text = "ROI 调参器——绿框 = 可通行性模型的全部视野";
        KeyPreview = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new System.Drawing.Size(1280, 700);
        BackColor = Color.FromArgb(30, 30, 30);

        _pb.Bounds = new Rectangle(8, 8, 1264, 640);
        _pb.SizeMode = PictureBoxSizeMode.Zoom;
        _pb.BackColor = Color.FromArgb(42, 42, 42);
        _lbl.Bounds = new Rectangle(8, 654, 1264, 40);
        _lbl.ForeColor = Color.FromArgb(224, 224, 224);
        Controls.Add(_pb);
        Controls.Add(_lbl);

        if (_hwnd != IntPtr.Zero)
        {
            _timer = new System.Windows.Forms.Timer { Interval = 150 };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }
        else if (_staticFrame is not null)
        {
            RefreshView();
        }
        RefreshText();
    }

    private void Tick()
    {
        try
        {
            using Mat raw = CaptureService.CaptureWindowMat(_hwnd, raiseAndWait: false);
            _lastNorm?.Dispose();
            _lastNorm = FrameTools.Normalize(raw, _designW, _designH);
        }
        catch { /* 单帧失败忽略（窗口最小化等），下一帧再来 */ }
        RefreshView();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        const int s = 8;
        switch (e.KeyCode)
        {
            case Keys.A or Keys.Left: MoveBy(-s, 0); break;
            case Keys.D or Keys.Right: MoveBy(s, 0); break;
            case Keys.W or Keys.Up: MoveBy(0, -s); break;
            case Keys.S or Keys.Down: MoveBy(0, s); break;
            case Keys.Q: ResizeW(s); break;
            case Keys.E: ResizeW(-s); break;
            case Keys.Z: ResizeH(s); break;
            case Keys.C: ResizeH(-s); break;
            case Keys.F:
                _aspectLock = !_aspectLock;
                if (_aspectLock) _roi.Height = Math.Max(16, _roi.Width / 2);
                break;
            case Keys.Enter: Save(); break;
            case Keys.R:
                _roi = new Rect(_initial[0], _initial[1], _initial[2], _initial[3]);
                break;
            case Keys.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
        RefreshView();
    }

    private void MoveBy(int dx, int dy)
    {
        _roi.X = Math.Clamp(_roi.X + dx, 0, _designW - 16);
        _roi.Y = Math.Clamp(_roi.Y + dy, 0, _designH - 16);
    }

    private void ResizeW(int dw)
    {
        _roi.Width = Math.Clamp(_roi.Width + dw, 16, _designW - _roi.X);
        if (_aspectLock) _roi.Height = Math.Clamp(_roi.Width / 2, 16, _designH - _roi.Y);
    }

    private void ResizeH(int dh)
    {
        _roi.Height = Math.Clamp(_roi.Height + dh, 16, _designH - _roi.Y);
        if (_aspectLock) _roi.Width = Math.Clamp(_roi.Height * 2, 16, _designW - _roi.X);
    }

    private Rect Clamped() => new(
        Math.Clamp(_roi.X, 0, _designW - 16),
        Math.Clamp(_roi.Y, 0, _designH - 16),
        Math.Clamp(_roi.Width, 16, _designW - Math.Clamp(_roi.X, 0, _designW - 16)),
        Math.Clamp(_roi.Height, 16, _designH - Math.Clamp(_roi.Y, 0, _designH - 16)));

    private void Save()
    {
        var r = Clamped();
        _cfg.Roi = new List<int> { r.X, r.Y, r.Width, r.Height };
        string note = "";
        if (_aspectLock)
        {
            int hIn = Math.Max(32, 2 * (int)Math.Round(224.0 * r.Height / r.Width / 2));
            _cfg.InputSize = new List<int> { hIn, 224 };
        }
        else if (Math.Abs(r.Width / (double)r.Height - 2.0) > 0.05)
        {
            note = "  ⚠ 自由比例与输入 2:1 不一致——训练/推理会拉伸变形（改 input_size 可对齐）";
        }
        string saved = RoiTuneCommand.Save(_repoRoot, _cfg);
        Console.WriteLine($"💾 已保存 {saved}{note}");
        Text = $"ROI 调参器——已保存 ✅";
    }

    private void RefreshView()
    {
        var src = _lastNorm ?? _staticFrame;
        if (src is null)
        {
            RefreshText();
            return;
        }
        var r = Clamped();
        using var m = src.Clone();
        Cv2.Rectangle(m, r, new Scalar(80, 255, 80), 2);
        Cv2.PutText(m, $"{r.Width}x{r.Height} @({r.X},{r.Y})",
            new OpenCvSharp.Point(r.X + 4, Math.Max(24, r.Y - 8)),
            HersheyFonts.HersheySimplex, 0.9, new Scalar(80, 255, 80), 2);
        SetImage(AiLabelImaging.ToBitmap(m));
        RefreshText();
    }

    private void RefreshText()
    {
        var r = Clamped();
        Text = $"ROI 调参器  [{r.X},{r.Y},{r.Width},{r.Height}]{(_aspectLock ? "  [2:1锁]" : "  [自由比例]")}";
        _lbl.Text = "WASD/方向键=移动   Q/E=宽±   Z/C=高±   F=锁2:1(自动联动 input_size)   回车=保存到 ai_vision.json   R=还原   Esc=退出";
    }

    private void SetImage(Bitmap? img)
    {
        var old = _pb.Image;
        _pb.Image = img;
        old?.Dispose();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer?.Stop();
        SetImage(null);
        _lastNorm?.Dispose();
        base.OnFormClosed(e);
    }
}
