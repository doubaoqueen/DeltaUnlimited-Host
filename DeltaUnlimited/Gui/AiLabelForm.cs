using System.Drawing;
using System.Windows.Forms;
using DeltaUnlimited.Cli;
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
    private readonly Label _lblVlm = new();
    private readonly Label _lblInfo = new();
    private readonly Label _lblStats = new();
    private readonly Label _lblKeys = new();
    private int _index;

    internal AiLabelForm(AiLabelStore store, int startIndex)
    {
        _store = store;

        Text = "AI 素材标注器（airecord 事后标注）";
        KeyPreview = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1476, 724);
        MinimumSize = new Size(1100, 688);
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

        _pbFull = NewBox(12, 12, 960, 596);        // 1920×1080 全帧 2:1，随窗口上下伸缩
        _pbFull.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        _pbRoi = NewBox(984, 12, 480, 240);        // ROI 800×400 2:1，贴右缘
        _pbRoi.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        NewLabel(984, 256, 480, 172, out _lblVlm); // VLM 初筛判断（参考）：紧跟 ROI 图下方，打标时正眼看得到
        _lblVlm.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _lblVlm.BackColor = Color.FromArgb(16, 42, 58);
        _lblVlm.ForeColor = Color.FromArgb(150, 220, 255);
        _lblVlm.Font = new Font("Consolas", 9.5f);
        NewLabel(984, 432, 480, 120, out _lblInfo);
        _lblInfo.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        NewLabel(984, 556, 480, 66, out _lblStats);
        _lblStats.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        NewLabel(12, 618, 1452, 94, out _lblKeys);
        _lblKeys.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _lblKeys.Font = new Font(Font.FontFamily, 9f);
        _lblKeys.Text = "左手打标：A=可通行(passable)  S=不可通行(blocked)  D=无地面(no_ground)  F=背景(background)  X=丢弃(discard)   右手翻页：←/→  N=下一个未标  M=清理缺失帧  Esc=退出\n" +
                        "G=跳转（输行号或文件名片段）  PgUp/PgDn=快翻±100  数字键 1/2/0/B 兼容 aicollect。即时落盘；回翻按键即可复标。启动默认第一个未标帧（命令行 --start first/行号 可改）。";

        // 启动行号由命令行解析（缺省=第一个未标帧；全标完=第一个文件完好的帧进入复览）
        MoveTo(Math.Clamp(startIndex, 0, Math.Max(0, _store.RowCount - 1)));
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
            case Keys.A or Keys.D1: Apply("passable"); break;
            case Keys.S or Keys.D2: Apply("blocked"); break;
            case Keys.D or Keys.D0: Apply("no_ground"); break;
            case Keys.B or Keys.F: Apply("background"); break;
            case Keys.X: Apply("discard"); break;
            case Keys.M: ApplyDiscardMissing(); break;
            case Keys.Right: MoveTo(_index + 1); break;
            case Keys.Left: MoveTo(_index - 1); break;
            case Keys.PageDown: MoveTo(_index + 100); break;
            case Keys.PageUp: MoveTo(_index - 100); break;
            case Keys.G: JumpDialog(); break;
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
            _lblInfo.Text = "⚠ 全帧文件缺失，无法裁 ROI——按 M 一键清理所有缺失帧，或按 X 丢弃此帧，或 → 跳过";
            return;
        }

        bool wasUnlabeled = row.Label.Length == 0;
        _store.SetLabel(_index, label);

        // 新标 → 跳下一个未标（流水线节奏）；复标 → 原地停留（复盘修正节奏）
        if (wasUnlabeled) NextUnlabeled();
        else MoveTo(_index);
    }

    private void ApplyDiscardMissing()
    {
        int n = _store.DiscardMissing();
        if (n > 0) NextUnlabeled(); // 丢弃后已无缺失未标帧，回到可标注流
        else MoveTo(_index);
    }

    private void NextUnlabeled()
    {
        int n = _store.RowCount;
        if (n == 0) return;
        // 两轮扫描：优先未标且文件完好的（能裁 ROI 的）；没有才落回"未标但文件缺失"（只能 X/M 处理）
        for (int pass = 0; pass < 2; pass++)
        {
            for (int step = 1; step <= n; step++)
            {
                int i = (_index + step) % n;
                if (_store.Rows[i].Label.Length != 0) continue;
                if (pass == 0 ? _store.FileExists(i) : !_store.FileExists(i))
                {
                    MoveTo(i);
                    return;
                }
            }
        }
        MoveTo(_index); // 全部标完，停在原地
    }

    /// <summary>G 键：弹窗跳转到任意一张——输入行号（1 基，同 [N/M] 显示）或文件名片段（如 232739）。</summary>
    private void JumpDialog()
    {
        var input = new TextBox { Left = 12, Top = 26, Width = 320 };
        var ok = new Button { Text = "跳转", DialogResult = DialogResult.OK, Left = 12, Top = 58, Width = 90 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 110, Top = 58, Width = 90 };
        using var dlg = new Form
        {
            Text = "跳转到…",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(344, 98),
            ShowInTaskbar = false,
        };
        dlg.Controls.Add(new Label { Text = "行号（如 500）或文件名片段（如 232739）：", Left = 12, Top = 4, AutoSize = true, ForeColor = Color.FromArgb(224, 224, 224) });
        dlg.Controls.Add(input);
        dlg.Controls.Add(ok);
        dlg.Controls.Add(cancel);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        JumpTo(input.Text.Trim());
    }

    private void JumpTo(string query)
    {
        if (query.Length == 0) return;
        if (int.TryParse(query, out int row))
        {
            MoveTo(row - 1); // 窗口显示 1 基
            return;
        }
        int n = _store.RowCount;
        for (int step = 1; step <= n; step++) // 文件名片段：从当前往后找，绕一圈
        {
            int i = (_index + step) % n;
            if (_store.Rows[i].RelPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                MoveTo(i);
                return;
            }
        }
        _lblInfo.Text = $"⚠ 没找到文件名含 “{query}” 的帧（当前 {_index + 1}/{n}）";
    }

    private void MoveTo(int index)
    {
        int n = _store.RowCount;
        if (n == 0)
        {
            _index = 0;
            _lblInfo.Text = "没有可标注素材——先跑 airecord 录制几帧再来。";
            _lblStats.Text = "";
            _lblVlm.Text = "";
            RefreshImages(null, null);
            return;
        }

        _index = Math.Clamp(index, 0, n - 1);
        var row = _store.Rows[_index];
        bool loaded = _store.Open(_index);

        RefreshImages(loaded ? _store.RenderFull() : null, loaded ? _store.RenderRoi() : null);
        _lblVlm.Text = PrescreenLookup.FormatForLabel(_store.PrescreenFor(_index));
        _lblInfo.Text = $"[{_index + 1}/{n}]  {row.RelPath}\n" +
                        $"地图 {row.Map}｜赛季 {row.Season}｜采集于 {row.Created}\n" +
                        "状态: " + (!loaded ? "⚠ 全帧文件缺失（按 M 一键清理缺失帧 / X 丢弃此帧 / → 跳过）"
                                           : row.Label.Length == 0 ? "未标注" : row.Label + "（回翻可复标）");
        Text = $"AI 素材标注器 —— 未标 {_store.UnlabeledCount}/{_store.RowCount}" +
               (_store.UnlabeledCount == 0 ? " ✅全部标完" : "");
    }

    private void RefreshImages(Bitmap? full, Bitmap? roi)
    {
        SetImage(_pbFull, full);
        SetImage(_pbRoi, roi);
        int missing = _store.MissingCount;
        _lblStats.Text = $"未标 {_store.UnlabeledCount} / 共 {_store.RowCount}" +
                         (missing > 0 ? $"｜⚠ 文件缺失 {missing}" : "") + "\n" +
                         "训练清单: " + (_store.TrainCounts.Count == 0
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
