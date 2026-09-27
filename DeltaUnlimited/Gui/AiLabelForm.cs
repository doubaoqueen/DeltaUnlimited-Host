using System.Drawing;
using System.Windows.Forms;
using DeltaUnlimited.Cli.Commands;

namespace DeltaUnlimited.Gui;

/// <summary>AI 素材标注窗体（ailabel 命令）：左=全帧+ROI 绿框，右=ROI 裁剪（模型实际视野）+信息+统计。
/// 键位与 aicollect 一致（1/2/0/b），另加 X 丢弃、←→ 翻张、N 跳到下一个未标。打标即时落盘。
/// 红线同采集器：纯看图打标，不做任何输入注入。</summary>
public sealed class AiLabelForm : Form
{
    private readonly AiLabelStore _store;
    private readonly PictureBox _pbFull = new();
    private readonly PictureBox _pbRoi = new();
    private readonly Label _lblInfo = new();
    private readonly Label _lblStats = new();
    private readonly Label _lblKeys = new();
    private int _index;

    internal AiLabelForm(AiLabelStore store)
    {
        _store = store;

        Text = "AI 素材标注器（airecord 事后标注）";
        KeyPreview = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1476, 640);
        MinimumSize = new Size(1100, 620);
        BackColor = Color.FromArgb(30, 30, 30);

        PictureBox NewBox(int x, int y, int w, int h)
        {
            var pb = new PictureBox
            {
                Bounds = new Rectangle(x, y, w, h),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(42, 42, 42),
            };
            Controls.Add(pb);
            return pb;
        }

        _pbFull = NewBox(12, 12, 960, 540);        // 1920×1080 全帧 2:1
        _pbRoi = NewBox(984, 12, 480, 240);        // ROI 800×400 2:1
        NewLabel(984, 262, 480, 150, out _lblInfo);
        NewLabel(984, 420, 480, 96, out _lblStats);
        NewLabel(12, 566, 1452, 60, out _lblKeys);
        _lblKeys.Text = "1=可通行(passable)   2=不可通行(blocked)   0=无地面(no_ground)   B=背景(background)   X=丢弃(discard)\n" +
                        "←/→=上一张/下一张   N=下一个未标   Esc=退出。每次打标即时写盘，无需保存；回翻旧图按键即可复标。";

        MoveTo(0);
    }

    private void NewLabel(int x, int y, int w, int h, out Label lbl)
    {
        lbl = new Label
        {
            Bounds = new Rectangle(x, y, w, h),
            ForeColor = Color.FromArgb(224, 224, 224),
            BackColor = Color.Transparent,
            Font = new Font(Font.FontFamily, 10f),
        };
        Controls.Add(lbl);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.D1: Apply("passable"); break;
            case Keys.D2: Apply("blocked"); break;
            case Keys.D0: Apply("no_ground"); break;
            case Keys.B: Apply("background"); break;
            case Keys.X: Apply("discard"); break;
            case Keys.Right: MoveTo(_index + 1); break;
            case Keys.Left: MoveTo(_index - 1); break;
            case Keys.N: NextUnlabeled(); break;
            case Keys.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Apply(string label)
    {
        if (_index < 0 || _index >= _store.RowCount) return;
        var row = _store.Rows[_index];
        if (row.Label == label) return; // 重复按键无操作
        if (AiLabelStore.TrainLabels.Contains(label) && !_store.HasFrame)
        {
            _lblInfo.Text = "⚠ 全帧文件缺失，无法裁 ROI——可按 X 丢弃此帧，或 → 跳过";
            return;
        }

        bool wasUnlabeled = row.Label.Length == 0;
        _store.SetLabel(_index, label);

        // 新标 → 跳下一个未标（流水线节奏）；复标 → 原地停留（复盘修正节奏）
        if (wasUnlabeled) NextUnlabeled();
        else MoveTo(_index);
    }

    private void NextUnlabeled()
    {
        int n = _store.RowCount;
        for (int step = 1; step <= n; step++)
        {
            int i = (_index + step) % n;
            if (_store.Rows[i].Label.Length == 0)
            {
                MoveTo(i);
                return;
            }
        }
        MoveTo(_index); // 全部标完，停在原地
    }

    private void MoveTo(int index)
    {
        int n = _store.RowCount;
        if (n == 0)
        {
            _index = 0;
            _lblInfo.Text = "没有可标注素材——先跑 airecord 录制几帧再来。";
            _lblStats.Text = "";
            RefreshImages(null, null);
            return;
        }

        _index = Math.Clamp(index, 0, n - 1);
        var row = _store.Rows[_index];
        bool loaded = _store.Open(_index);

        RefreshImages(loaded ? _store.RenderFull() : null, loaded ? _store.RenderRoi() : null);
        _lblInfo.Text = $"[{_index + 1}/{n}]  {row.RelPath}\n" +
                        $"地图 {row.Map}｜赛季 {row.Season}｜采集于 {row.Created}\n" +
                        "状态: " + (!loaded ? "⚠ 全帧文件缺失（可按 X 丢弃，→ 跳过）"
                                           : row.Label.Length == 0 ? "未标注" : row.Label + "（回翻可复标）");
        Text = $"AI 素材标注器 —— 未标 {_store.UnlabeledCount}/{_store.RowCount}" +
               (_store.UnlabeledCount == 0 ? " ✅全部标完" : "");
    }

    private void RefreshImages(Bitmap? full, Bitmap? roi)
    {
        SetImage(_pbFull, full);
        SetImage(_pbRoi, roi);
        _lblStats.Text = $"未标 {_store.UnlabeledCount} / 共 {_store.RowCount}\n" +
                         "训练清单: " + ( _store.TrainCounts.Count == 0
                             ? "（空）"
                             : string.Join("  ", _store.TrainCounts.Select(kv => $"{kv.Key}:{kv.Value}")));
    }

    private static void SetImage(PictureBox pb, Bitmap? img)
    {
        var old = pb.Image;
        pb.Image = img;
        old?.Dispose();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SetImage(_pbFull, null);
        SetImage(_pbRoi, null);
        _store.Dispose();
        base.OnFormClosed(e);
    }
}
