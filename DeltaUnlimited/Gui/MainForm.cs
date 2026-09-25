using System.Collections.Concurrent;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DeltaUnlimited.Data;

namespace DeltaUnlimited.Gui;

/// <summary>主控制面板（G1 骨架）：工作流/模式选择、开始/急停（G2 接线，当前禁用并提示）、状态栏、
/// 实时日志窗（封顶+自动滚屏+清空）与系统托盘（最小化/关闭收进托盘、双击恢复、右键菜单）。
/// 红线：急停永远优先于一切自动化行为；日志窗只做展示，不阻塞链路线程。</summary>
public sealed class MainForm : Form
{
    private const int LogMaxLines = 3000;

    private readonly DataStore _store;
    private readonly string _repoRoot;
    private readonly Action<string> _logHandler;
    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly System.Windows.Forms.Timer _logTimer;
    private readonly NotifyIcon _tray;
    private readonly ComboBox _workflowBox;
    private readonly ComboBox _modeBox;
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly TextBox _logBox;
    private readonly ToolStripStatusLabel _statusLabel;
    private bool _reallyExit;
    private bool _trayHintShown;

    public MainForm(DataStore store, string repoRoot)
    {
        _store = store;
        _repoRoot = repoRoot;

        Text = "DeltaUnlimited 控制面板";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(840, 580);
        MinimumSize = new Size(640, 420);
        Icon = AppIconFactory.Create();

        // ---- 顶部控制区 ----
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 7, 8, 0), WrapContents = false };
        top.Controls.Add(new Label { Text = "工作流", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        _workflowBox = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var wf in WorkflowCatalog.List(repoRoot)) _workflowBox.Items.Add(wf);
        int defIdx = _workflowBox.Items?.IndexOf("enter_match.json") ?? -1;
        if (defIdx >= 0) _workflowBox.SelectedIndex = defIdx;
        else if ((_workflowBox.Items?.Count ?? 0) > 0) _workflowBox.SelectedIndex = 0;
        top.Controls.Add(_workflowBox);

        top.Controls.Add(new Label { Text = "模式", AutoSize = true, Margin = new Padding(14, 6, 4, 0) });
        _modeBox = new ComboBox { Width = 176, DropDownStyle = ComboBoxStyle.DropDownList };
        _modeBox.Items.AddRange(new object[] { "半自动（暂停点人工确认）", "全自动（跳过暂停点）" });
        _modeBox.SelectedIndex = 0;
        top.Controls.Add(_modeBox);

        _startButton = new Button { Text = "▶ 开始", Width = 96, Height = 30, Enabled = false, Margin = new Padding(14, 0, 0, 0) };
        _stopButton = new Button { Text = "⛔ 急停", Width = 96, Height = 30, Enabled = false, ForeColor = Color.Red, Margin = new Padding(8, 0, 0, 0) };
        var clearButton = new Button { Text = "清空日志", Width = 96, Height = 30, Margin = new Padding(8, 0, 0, 0) };
        clearButton.Click += (_, _) => { _logBox.Clear(); _statusLabel.Text = "日志已清空"; };
        var g2Tip = new ToolTip();
        g2Tip.SetToolTip(_startButton, "G2 接线：后台线程执行链路 + 暂停点桥接（开发中）");
        g2Tip.SetToolTip(_stopButton, "G2 接线：协作式停止标志（替代 Ctrl+C，释放所有按键）");
        top.Controls.Add(_startButton);
        top.Controls.Add(_stopButton);
        top.Controls.Add(clearButton);
        Controls.Add(top);

        // ---- 日志窗 ----
        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9f),
            BackColor = Color.FromArgb(18, 22, 28),
            ForeColor = Color.FromArgb(208, 212, 220),
            BorderStyle = BorderStyle.None,
        };
        Controls.Add(_logBox);

        // ---- 状态栏 ----
        var status = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel("就绪 — 开始/急停在 G2 接线中；双击托盘图标恢复窗口");
        status.Items.Add(_statusLabel);
        Controls.Add(status);

        // ---- 系统托盘 ----
        _tray = new NotifyIcon { Icon = Icon, Text = "DeltaUnlimited", Visible = true };
        var menu = new ContextMenuStrip();
        var trayShow = new ToolStripMenuItem("显示面板");
        trayShow.Click += (_, _) => RestoreWindow();
        var trayStart = new ToolStripMenuItem("开始 enter_match");
        trayStart.Enabled = false; // G2 接线
        var trayExit = new ToolStripMenuItem("退出");
        trayExit.Click += (_, _) => { _reallyExit = true; Close(); };
        menu.Items.Add(trayShow);
        menu.Items.Add(trayStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(trayExit);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreWindow();

        // ---- 关闭/最小化 → 托盘 ----
        FormClosing += OnFormClosing;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };

        // ---- 日志订阅（启动缓冲补发）与批量刷新定时器 ----
        _logHandler = line => _logQueue.Enqueue(line);
        ConsoleRelay.Subscribe(_logHandler);
        _logTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _logTimer.Tick += (_, _) => FlushLogQueue();
        _logTimer.Start();
    }

    private void FlushLogQueue()
    {
        if (_logQueue.IsEmpty) return;
        var sb = new StringBuilder();
        while (_logQueue.TryDequeue(out var line))
        {
            // Logger 行自带 [HH:mm:ss]，避免双重时间戳
            if (!line.StartsWith('[')) sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ");
            sb.Append(line).AppendLine();
        }
        if (sb.Length == 0) return;
        _logBox.AppendText(sb.ToString());
        if (_logBox.Lines.Length > LogMaxLines)
        {
            var lines = _logBox.Lines;
            _logBox.Text = string.Join(Environment.NewLine, lines, lines.Length - LogMaxLines, LogMaxLines) + Environment.NewLine;
        }
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void HideToTray()
    {
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(3000, "DeltaUnlimited 仍在托盘运行",
                "双击托盘图标恢复窗口；右键菜单可退出。", ToolTipIcon.Info);
        }
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_reallyExit) return;
        e.Cancel = true; // 关闭 = 收进托盘（真正退出走托盘菜单）
        HideToTray();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ConsoleRelay.Unsubscribe(_logHandler);
            _logTimer.Stop();
            _logTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
