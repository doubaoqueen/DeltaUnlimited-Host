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
    private readonly GuiSettings _settings; // 本机设置（上次工作流/模式/窗口位置）

    // ---- 命令台（G3）：代替 cmd 的可视化命令启动器（数据驱动：data/gui_commands.json）----
    private readonly GuiCommandCatalog _cmdCatalog;
    private ComboBox _cmdGroupBox = null!;
    private ComboBox _cmdBox = null!;
    private TextBox _cmdDescBox = null!;
    private FlowLayoutPanel _cmdArgsPanel = null!;
    private TextBox _cmdPreviewBox = null!;
    private Button _cmdRunButton = null!;
    private Button _cmdStopButton = null!;
    private readonly ToolTip _cmdTips = new();
    private readonly List<GuiCommandDef> _cmdVisible = new();
    private GuiCommandDef? _cmdCurrent;
    private volatile bool _cmdRunning;
    private string _cmdOutcome = "";
    private Task? _cmdTask;

    public MainForm(DataStore store, string repoRoot)
    {
        _store = store;
        _repoRoot = repoRoot;
        _settings = store.LoadGuiSettings();

        // 命令台目录加载（加载失败降级为空目录，面板照常构建，不让 GUI 起不来）
        GuiCommandCatalog? catalog = null;
        try { catalog = CommandCatalog.Load(_repoRoot); }
        catch (Exception ex) { Console.Error.WriteLine($"⚠ 命令台目录加载失败（命令台为空）：{ex.Message}"); }
        _cmdCatalog = catalog ?? new GuiCommandCatalog { Groups = { new GuiCommandGroup { Id = "_none", Title = "（目录加载失败）" } } };

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
            BackColor = Color.FromArgb(24, 28, 34),
            ForeColor = Color.FromArgb(228, 231, 236),
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

        // ---- 应用上次设置（本机记忆：工作流/模式/窗口位置；越界或已删除则回退默认）----
        if (!string.IsNullOrEmpty(_settings.LastWorkflow) && _workflowBox.Items.Contains(_settings.LastWorkflow))
            _workflowBox.SelectedItem = _settings.LastWorkflow;
        _modeBox.SelectedIndex = _settings.AutoMode ? 1 : 0;
        if (_settings is { WindowW: > 0, WindowH: > 0 })
        {
            var b = new Rectangle(_settings.WindowX ?? 0, _settings.WindowY ?? 0,
                _settings.WindowW.Value, _settings.WindowH.Value);
            bool visible = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(b));
            if (b.Width >= MinimumSize.Width && b.Height >= MinimumSize.Height && visible)
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = b;
            }
        }

        _startButton = new Button { Text = "▶ 开始", Width = 140, Height = 42, Margin = new Padding(24, 0, 0, 0), Font = new Font(Font.FontFamily, 11f, FontStyle.Bold) };
        _startButton.Click += (_, _) => StartChain();
        _stopButton = new Button { Text = "⛔ 急停", Width = 140, Height = 42, Enabled = false, ForeColor = Color.Red, Margin = new Padding(10, 0, 0, 0), Font = new Font(Font.FontFamily, 11f, FontStyle.Bold) };
        _stopButton.Click += (_, _) => CommandUtil.RequestStop();
        var clearButton = new Button { Text = "清空日志", Width = 110, Height = 42, Margin = new Padding(10, 0, 0, 0), Font = new Font(Font.FontFamily, 10f) };
        clearButton.Click += (_, _) => { _logBox.Clear(); };
        top.Controls.Add(_startButton);
        top.Controls.Add(_stopButton);
        top.Controls.Add(clearButton);

        // ---- 命令台（G3）：停靠在顶栏之下、暂停面板之上——后加入者靠上，故在暂停面板之后、顶栏之前加入 ----
        var cmdPanel = BuildCommandPanel();
        Controls.Add(cmdPanel);

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
        if (_cmdRunning) { _statusLabel.Text = "⚠ 命令还在运行——等它结束或先停止，再开始链路"; return; }
        if (_workflowBox.SelectedItem is not string wf || wf.Length == 0)
        {
            _statusLabel.Text = "⚠️ 请先选择工作流";
            return;
        }
        bool auto = _modeBox.SelectedIndex == 1;

        // 记住本次选择（本机设置；写失败不影响运行）
        _settings.LastWorkflow = wf;
        _settings.AutoMode = auto;
        _store.SaveGuiSettings(_settings);

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
        UpdateBusyUi(); // 与命令台状态调和：开始按钮的可用性由"链路 + 命令"两者共同决定
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

    // ===== 命令台（G3）：代替 cmd 的可视化命令启动器 =====

    /// <summary>构建命令台面板：分类/命令下拉 + 说明 + 动态参数表单 + 命令行预览。
    /// 注意控件创建顺序：先建全部字段再 PopulateCommands（事件链会用到它们）。</summary>
    private Panel BuildCommandPanel()
    {
        var f = new Font(Font.FontFamily, 10f);
        var panel = new Panel { Dock = DockStyle.Top, Height = 244, BackColor = Color.FromArgb(248, 249, 251), Padding = new Padding(10, 4, 10, 6) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        // r0：标题 + 分类 + 命令 + 运行/停止（默认分类先于事件接线设置，避免链式触发时字段未就绪）
        var row0 = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), BackColor = Color.FromArgb(248, 249, 251) };
        row0.Controls.Add(new Label { Text = "命令台", AutoSize = true, Margin = new Padding(0, 9, 12, 0), Font = new Font(Font.FontFamily, 11f, FontStyle.Bold), ForeColor = Color.FromArgb(0, 106, 74) });
        row0.Controls.Add(new Label { Text = "分类", AutoSize = true, Margin = new Padding(0, 10, 6, 0), Font = f });
        _cmdGroupBox = new ComboBox { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList, Font = f };
        foreach (var g in _cmdCatalog.Groups) _cmdGroupBox.Items.Add(g.Title);
        if (_cmdGroupBox.Items.Count > 0) _cmdGroupBox.SelectedIndex = 0;
        _cmdGroupBox.SelectedIndexChanged += (_, _) => PopulateCommands();
        row0.Controls.Add(_cmdGroupBox);
        row0.Controls.Add(new Label { Text = "命令", AutoSize = true, Margin = new Padding(14, 10, 6, 0), Font = f });
        _cmdBox = new ComboBox { Width = 240, DropDownStyle = ComboBoxStyle.DropDownList, Font = f };
        _cmdBox.SelectedIndexChanged += (_, _) => OnCommandSelected();
        row0.Controls.Add(_cmdBox);
        _cmdRunButton = new Button { Text = "▶ 运行", Width = 96, Height = 32, Margin = new Padding(14, 3, 0, 0), Font = new Font(Font.FontFamily, 10f, FontStyle.Bold) };
        _cmdRunButton.Click += (_, _) => RunCommand();
        row0.Controls.Add(_cmdRunButton);
        _cmdStopButton = new Button { Text = "⛔ 停止", Width = 86, Height = 32, Enabled = false, ForeColor = Color.Red, Margin = new Padding(8, 3, 0, 0), Font = f };
        _cmdStopButton.Click += (_, _) => StopCommand();
        row0.Controls.Add(_cmdStopButton);
        grid.Controls.Add(row0, 0, 0);

        // r1：说明（人话描述 + 用法提示 + 风险警示）——浅底深字，看得清优先
        _cmdDescBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BackColor = Color.FromArgb(255, 255, 255), ForeColor = Color.FromArgb(52, 58, 64), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Microsoft YaHei UI", 9.75f) };
        grid.Controls.Add(_cmdDescBox, 0, 1);

        // r2：参数表单（按所选命令动态生成）
        _cmdArgsPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(255, 255, 255) };
        grid.Controls.Add(_cmdArgsPanel, 0, 2);

        // r3：命令行预览——预览的就是会跑的
        _cmdPreviewBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9.75f), BackColor = Color.FromArgb(250, 251, 252), ForeColor = Color.FromArgb(0, 106, 78), BorderStyle = BorderStyle.FixedSingle };
        grid.Controls.Add(_cmdPreviewBox, 0, 3);

        panel.Controls.Add(grid);
        PopulateCommands();
        return panel;
    }

    private void PopulateCommands()
    {
        _cmdVisible.Clear();
        _cmdBox.Items.Clear();
        string? title = _cmdGroupBox.SelectedItem as string;
        var groupId = _cmdCatalog.Groups.FirstOrDefault(g => g.Title == title)?.Id ?? "";
        _cmdVisible.AddRange(_cmdCatalog.Commands.Where(c => c.Group == groupId));
        foreach (var d in _cmdVisible) _cmdBox.Items.Add(DisplayName(d));
        if (_cmdBox.Items.Count > 0) _cmdBox.SelectedIndex = 0;
    }

    private static string DisplayName(GuiCommandDef d)
        => d.Verb + (d.FixedArgs.Count > 0 ? " " + string.Join(' ', d.FixedArgs) : "");

    private void OnCommandSelected()
    {
        _cmdCurrent = _cmdBox.SelectedIndex >= 0 && _cmdBox.SelectedIndex < _cmdVisible.Count ? _cmdVisible[_cmdBox.SelectedIndex] : null;
        _cmdArgsPanel.Controls.Clear();
        _cmdTips.RemoveAll();
        if (_cmdCurrent is null)
        {
            _cmdDescBox.Text = "（该分组没有命令）";
            UpdateCmdPreview();
            UpdateBusyUi();
            return;
        }
        var def = _cmdCurrent;
        string warn = def.Exclusive ? "\r\n⚠ 会动键鼠/抢占游戏：链路运行期间禁止调起。" : "";
        string nostop = def.Stoppable ? "" : "\r\n⚠ 常驻逻辑：无急停检查点，GUI 内不可停止。";
        _cmdDescBox.Text = def.Desc + (def.Note.Length > 0 ? "\r\nℹ " + def.Note : "") + warn + nostop;

        foreach (var a in def.Args)
        {
            if (a.Kind == "switch")
            {
                var ck = new CheckBox { Text = "--" + a.Name, AutoSize = true, Margin = new Padding(4, 10, 12, 0), Font = new Font(Font.FontFamily, 9.75f), ForeColor = Color.FromArgb(52, 58, 64) };
                ck.CheckedChanged += (_, _) => UpdateCmdPreview();
                _cmdTips.SetToolTip(ck, a.Desc);
                _cmdArgsPanel.Controls.Add(ck);
            }
            else
            {
                string label = (a.Kind == "flag" ? "--" : "") + a.Name + (a.Required ? "*" : "");
                var lb = new Label { Text = label, AutoSize = true, Margin = new Padding(4, 9, 4, 0), Font = new Font(Font.FontFamily, 9.75f), ForeColor = Color.FromArgb(52, 58, 64) };
                var tb = new TextBox { Width = 150, PlaceholderText = a.Default, Margin = new Padding(0, 6, 0, 0), Font = new Font(Font.FontFamily, 9.75f) };
                tb.TextChanged += (_, _) => UpdateCmdPreview();
                _cmdTips.SetToolTip(tb, a.Desc + (a.Required ? "（必填）" : "") + (a.Default.Length > 0 ? $"（留空 = {a.Default}）" : ""));
                _cmdArgsPanel.Controls.Add(lb);
                _cmdArgsPanel.Controls.Add(tb);
            }
        }
        UpdateCmdPreview();
        UpdateBusyUi();
    }

    /// <summary>按控件类型分流收集当前表单值（与 def.Args 顺序一一对应）。</summary>
    private (List<string> Values, List<bool> Switches) CollectCurrentValues()
    {
        var values = new List<string>();
        var switches = new List<bool>();
        foreach (var c in _cmdArgsPanel.Controls)
        {
            if (c is TextBox tb) values.Add(tb.Text);
            else if (c is CheckBox ck) switches.Add(ck.Checked);
        }
        return (values, switches);
    }

    private void UpdateCmdPreview()
    {
        if (_cmdCurrent is null) { _cmdPreviewBox.Text = ""; return; }
        try
        {
            var (v, s) = CollectCurrentValues();
            _cmdPreviewBox.Text = CommandCatalog.BuildPreview(CommandCatalog.BuildArgs(_cmdCurrent, v, s));
        }
        catch (ArgumentException ex)
        {
            _cmdPreviewBox.Text = "（参数未填全：" + ex.Message + "）";
        }
    }

    private void RunCommand()
    {
        var def = _cmdCurrent;
        if (def is null || _cmdRunning) return;
        if (_running && def.Exclusive)
        {
            _statusLabel.Text = "⚠ 链路运行中，不能调起会动键鼠的命令——先急停链路";
            return;
        }
        string[] tokens;
        try
        {
            var (v, s) = CollectCurrentValues();
            tokens = CommandCatalog.BuildArgs(def, v, s);
        }
        catch (ArgumentException ex)
        {
            _statusLabel.Text = "⚠ " + ex.Message;
            return;
        }
        if (!_running) CommandUtil.ResetStop(); // 链路没跑时独占急停标志；链路在跑（非独占诊断）时不能动它
        _cmdRunning = true;
        _cmdOutcome = "";
        string verb = def.Verb;
        _statusLabel.Text = $"▶ 命令运行中：{verb} —— 输出见下方日志" + (_running ? "（注意：此刻停止会同时急停链路）" : "");
        Console.WriteLine($"—— 命令台运行：{CommandCatalog.BuildPreview(tokens)}");
        _cmdTask = Task.Run(() =>
        {
            try
            {
                CliDispatcher.Dispatch(verb, _store, _repoRoot, tokens);
                _cmdOutcome = CommandUtil.StopRequested ? "已停止" : "完成";
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                _cmdOutcome = "失败";
            }
        });
        _cmdTask.ContinueWith(_ => OnCommandEnded(verb), TaskScheduler.FromCurrentSynchronizationContext());
        UpdateBusyUi();
    }

    private void OnCommandEnded(string verb)
    {
        if (IsDisposed || Disposing) return; // 命令结束恰逢窗口退出
        _cmdRunning = false;
        string msg = _cmdOutcome switch
        {
            "完成" => $"✓ 命令 {verb} 完成",
            "已停止" => $"⛔ 命令 {verb} 已停止（所有按键已释放）",
            "失败" => $"❌ 命令 {verb} 失败（异常见日志）",
            _ => $"命令 {verb} 结束",
        };
        _statusLabel.Text = msg;
        Console.WriteLine($"—— {msg}");
        UpdateBusyUi();
    }

    private void StopCommand()
    {
        CommandUtil.RequestStop();
        _statusLabel.Text = _running
            ? "⛔ 已请求停止（急停同时作用于运行中的链路；所有按键已释放）"
            : "⛔ 已请求停止（正在收尾…）";
    }

    /// <summary>链路（_running）与命令（_cmdRunning）双状态的按钮可用性调和：
    /// 开始 vs 运行命令互斥（共用急停标志与键鼠）；独占命令在链路运行期间禁用。</summary>
    private void UpdateBusyUi()
    {
        bool exclusive = _cmdCurrent?.Exclusive == true;
        _startButton.Enabled = !_running && !_cmdRunning;
        _cmdRunButton.Enabled = _cmdCurrent is not null && !_cmdRunning && (!_running || !exclusive);
        _cmdStopButton.Enabled = _cmdRunning;
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
        // 真正退出：记住窗口位置（最大化/最小化时不覆盖上次的普通态位置）
        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowX = Bounds.X;
            _settings.WindowY = Bounds.Y;
            _settings.WindowW = Bounds.Width;
            _settings.WindowH = Bounds.Height;
            _store.SaveGuiSettings(_settings);
        }
        // 急停并给链路线程 2s 收尾窗口（评审 P3-7：否则按键释放存在微小竞态窗口）
        if (_running)
        {
            CommandUtil.RequestStop();
            try { _runTask?.Wait(2000); } catch { }
        }
        if (_cmdRunning)
        {
            CommandUtil.RequestStop();
            try { _cmdTask?.Wait(1500); } catch { }
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
            _cmdTips.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
