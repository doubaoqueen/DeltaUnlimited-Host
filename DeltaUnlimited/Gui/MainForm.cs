using System.Collections.Concurrent;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DeltaUnlimited.Cli;
using DeltaUnlimited.Cli.Commands;
using DeltaUnlimited.Data;

namespace DeltaUnlimited.Gui;

/// <summary>主控制面板（G2）：工作流/模式选择、开始/急停（后台线程执行链路 + 协作式急停）、
/// 暂停面板（半自动人工确认点：继续/中止）、实时日志窗、系统托盘（收托盘/双击恢复/菜单/气泡通知）。
/// 红线：急停永远优先——急停按钮/Ctrl+C/暂停面板"中止"都会释放所有按键并优雅收尾。</summary>
public sealed class MainForm : Form
{
    private const int LogMaxLines = 3000;

    private readonly DataStore _store;
    private readonly string _repoRoot;
    private readonly Action<string> _logHandler;
    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly System.Windows.Forms.Timer _logTimer;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _trayStart;
    private readonly ToolStripMenuItem _trayStop;
    private readonly ComboBox _workflowBox;
    private readonly ComboBox _modeBox;
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly TextBox _logBox;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly Panel _pausePanel;
    private readonly Label _pauseLabel;
    private bool _reallyExit;
    private bool _trayHintShown;
    private bool _running;
    private Task? _runTask; // 链路线程句柄（退出收尾时等它停稳，评审 P3-7）
    private volatile string _lastOutcome = "";

    public MainForm(DataStore store, string repoRoot)
    {
        _store = store;
        _repoRoot = repoRoot;

        Text = "DeltaUnlimited 控制面板";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1180, 760);
        MinimumSize = new Size(940, 600);
        Icon = AppIconFactory.Create();

        // ---- 日志窗（先加入，Dock.Fill 占剩余空间）----
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
        _statusLabel = new ToolStripStatusLabel("就绪 —— 选择工作流后点开始；急停随时有效");
        status.Items.Add(_statusLabel);
        Controls.Add(status);

        // ---- 暂停面板（半自动人工确认点，非模态：急停按钮始终可用）----
        _pausePanel = new Panel { Dock = DockStyle.Top, Height = 60, Visible = false, BackColor = Color.FromArgb(58, 48, 18) };
        var abortButton = new Button { Text = "⛔ 中止链路", Dock = DockStyle.Right, Width = 130, ForeColor = Color.Red, Font = new Font(Font.FontFamily, 10f) };
        abortButton.Click += (_, _) => AbortFromPause();
        var contButton = new Button { Text = "▶ 继续", Dock = DockStyle.Right, Width = 110, Font = new Font(Font.FontFamily, 10f) };
        contButton.Click += (_, _) => { PauseGate.Resume(); _pausePanel.Visible = false; };
        _pauseLabel = new Label { Text = "", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(255, 220, 120), TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font.FontFamily, 10f) };
        _pausePanel.Controls.Add(_pauseLabel);
        _pausePanel.Controls.Add(abortButton);
        _pausePanel.Controls.Add(contButton);
        Controls.Add(_pausePanel);

        // ---- 顶部控制区（最后加入 → 停靠最顶部；后续新按钮直接往里加，宽度自动流式排列）----
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(12, 10, 12, 0), WrapContents = false };
        top.Controls.Add(new Label { Text = "工作流", AutoSize = true, Margin = new Padding(0, 8, 6, 0), Font = new Font(Font.FontFamily, 10f) });
        _workflowBox = new ComboBox { Width = 240, DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font(Font.FontFamily, 10f) };
        foreach (var wf in WorkflowCatalog.List(repoRoot)) _workflowBox.Items.Add(wf);
        int defIdx = _workflowBox.Items?.IndexOf("enter_match.json") ?? -1;
        if (defIdx >= 0) _workflowBox.SelectedIndex = defIdx;
        else if ((_workflowBox.Items?.Count ?? 0) > 0) _workflowBox.SelectedIndex = 0;
        top.Controls.Add(_workflowBox);

        top.Controls.Add(new Label { Text = "模式", AutoSize = true, Margin = new Padding(18, 8, 6, 0), Font = new Font(Font.FontFamily, 10f) });
        _modeBox = new ComboBox { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font(Font.FontFamily, 10f) };
        _modeBox.Items.AddRange(new object[] { "半自动（暂停点人工确认）", "全自动（跳过暂停点）" });
        _modeBox.SelectedIndex = 0;
        top.Controls.Add(_modeBox);

        _startButton = new Button { Text = "▶ 开始", Width = 140, Height = 42, Margin = new Padding(24, 0, 0, 0), Font = new Font(Font.FontFamily, 11f, FontStyle.Bold) };
        _startButton.Click += (_, _) => StartChain();
        _stopButton = new Button { Text = "⛔ 急停", Width = 140, Height = 42, Enabled = false, ForeColor = Color.Red, Margin = new Padding(10, 0, 0, 0), Font = new Font(Font.FontFamily, 11f, FontStyle.Bold) };
        _stopButton.Click += (_, _) => CommandUtil.RequestStop();
        var clearButton = new Button { Text = "清空日志", Width = 110, Height = 42, Margin = new Padding(10, 0, 0, 0), Font = new Font(Font.FontFamily, 10f) };
        clearButton.Click += (_, _) => { _logBox.Clear(); };
        top.Controls.Add(_startButton);
        top.Controls.Add(_stopButton);
        top.Controls.Add(clearButton);
        Controls.Add(top);

        // ---- 系统托盘 ----
        _tray = new NotifyIcon { Icon = Icon, Text = "DeltaUnlimited", Visible = true };
        var menu = new ContextMenuStrip();
        var trayShow = new ToolStripMenuItem("显示面板");
        trayShow.Click += (_, _) => RestoreWindow();
        _trayStart = new ToolStripMenuItem("开始 enter_match");
        _trayStart.Click += (_, _) => { _workflowBox.SelectedItem = "enter_match.json"; StartChain(); };
        _trayStop = new ToolStripMenuItem("急停");
        _trayStop.Enabled = false;
        _trayStop.Click += (_, _) => CommandUtil.RequestStop();
        var trayExit = new ToolStripMenuItem("退出");
        trayExit.Click += (_, _) => { _reallyExit = true; Close(); };
        menu.Items.Add(trayShow);
        menu.Items.Add(_trayStart);
        menu.Items.Add(_trayStop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(trayExit);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreWindow();

        // ---- 关闭/最小化 → 托盘 ----
        FormClosing += OnFormClosing;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };

        // ---- 暂停点桥接：链路线程阻塞在 PauseGate.Wait，本窗口按钮放行 ----
        CommandUtil.PauseHandler = PauseGate.Wait;
        PauseGate.PauseRequested += OnPauseRequested;

        // ---- 日志订阅（启动缓冲补发）与批量刷新定时器 ----
        _logHandler = line => _logQueue.Enqueue(line);
        ConsoleRelay.Subscribe(_logHandler);
        _logTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _logTimer.Tick += (_, _) => FlushLogQueue();
        _logTimer.Start();
    }

    // ===== 链路执行 =====

    private void StartChain()
    {
        if (_running) return;
        if (_workflowBox.SelectedItem is not string wf || wf.Length == 0)
        {
            _statusLabel.Text = "⚠️ 请先选择工作流";
            return;
        }
        bool auto = _modeBox.SelectedIndex == 1;

        CommandUtil.ResetStop();
        SetRunning(true);
        _statusLabel.Text = $"▶ 运行中：{wf}（{(auto ? "全自动" : "半自动")}）—— 急停随时有效";
        _lastOutcome = "";

        _runTask = Task.Run(() =>
        {
            try
            {
                // 链路输出经 ConsoleRelay 进入日志窗；ChainCommand 内部处理急停与暂停点
                ChainCommand.Run(_store, _repoRoot, new[] { "chain", wf, auto ? "--auto" : "" });
                _lastOutcome = CommandUtil.StopRequested ? "已停止" : "完成";
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                _lastOutcome = "失败";
            }
        });
        _runTask.ContinueWith(_ => OnChainEnded(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnChainEnded()
    {
        if (IsDisposed || Disposing) return; // 链路结束恰逢窗口退出（评审 P3-5：防 ObjectDisposedException）
        SetRunning(false);
        string msg = _lastOutcome switch
        {
            "完成" => "✅ 链路完成",
            "已停止" => "⛔ 已急停（所有按键已释放）",
            "失败" => "❌ 链路失败（详见日志）",
            _ => "链路结束",
        };
        _statusLabel.Text = msg;
        try { _tray.ShowBalloonTip(3000, "DeltaUnlimited", msg, ToolTipIcon.Info); } catch { }
    }

    private void AbortFromPause()
    {
        PauseGate.Interrupt();
        CommandUtil.RequestStop();
        _pausePanel.Visible = false;
    }

    /// <summary>暂停点事件在链路线程触发 → 转到 UI 线程显示暂停面板。</summary>
    private void OnPauseRequested(string message)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            PauseGate.Interrupt(); // 窗口已不在，放行中止，避免链路线程永久阻塞
            return;
        }
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            _pauseLabel.Text = message;
            _pausePanel.Visible = true;
            _statusLabel.Text = "⏸ 暂停点：请确认后继续或中止";
        }));
    }

    private void SetRunning(bool running)
    {
        _running = running;
        _startButton.Enabled = !running;
        _stopButton.Enabled = running;
        _workflowBox.Enabled = !running;
        _modeBox.Enabled = !running;
        _trayStart.Enabled = !running;
        _trayStop.Enabled = running;
        if (!running) _pausePanel.Visible = false;
    }

    // ===== 日志 =====

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

    // ===== 托盘 =====

    private void HideToTray()
    {
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(3000, "DeltaUnlimited 仍在托盘运行",
                "双击托盘图标恢复窗口；右键菜单可开始/急停/退出。", ToolTipIcon.Info);
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
        if (!_reallyExit)
        {
            e.Cancel = true; // 关闭 = 收进托盘（真正退出走托盘菜单）
            HideToTray();
            return;
        }
        // 真正退出：急停并给链路线程 2s 收尾窗口（评审 P3-7：否则按键释放存在微小竞态窗口）
        if (_running)
        {
            CommandUtil.RequestStop();
            try { _runTask?.Wait(2000); } catch { }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_running) CommandUtil.RequestStop(); // 退出前释放一切按键
            PauseGate.PauseRequested -= OnPauseRequested;
            if (CommandUtil.PauseHandler == PauseGate.Wait) CommandUtil.PauseHandler = null;
            ConsoleRelay.Unsubscribe(_logHandler);
            _logTimer.Stop();
            _logTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
