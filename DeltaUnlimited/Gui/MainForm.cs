using System.Collections.Concurrent;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DeltaUnlimited.Cli;
using DeltaUnlimited.Cli.Commands;
using DeltaUnlimited.Data;

namespace DeltaUnlimited.Gui;

/// <summary>主控制面板（G3）：**左右主从布局**——左侧命令侧栏（分类/命令/说明/参数/运行），右侧日志窗占主视野；
/// 顶部全局栏（标题/主题切换/工作流/模式/开始/急停），底部状态栏。深浅两套主题可切换（记忆在 gui_settings）。
/// 红线不变：急停永远优先——急停按钮/Ctrl+C/暂停面板"中止"都会释放所有按键并优雅收尾。</summary>
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
    private readonly StatusStrip _statusStrip;
    private readonly Panel _pausePanel;
    private readonly Label _pauseLabel;
    private bool _reallyExit;
    private bool _trayHintShown;
    private bool _running;
    private Task? _runTask; // 链路线程句柄（退出收尾时等它停稳，评审 P3-7）
    private volatile string _lastOutcome = "";
    private readonly GuiSettings _settings; // 本机设置（上次工作流/模式/窗口位置/主题）
    private Theme _theme;                   // 当前主题（切换按钮换调色板，结构不动）

    // ---- 主题相关的结构引用（ApplyTheme 按"角色"上色）----
    private FlowLayoutPanel _topBar = null!;
    private Label _titleLabel = null!;
    private Button _themeButton = null!;
    private SplitContainer _split = null!;
    private TableLayoutPanel _cmdSidebar = null!;

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
        _theme = _settings.DarkTheme ? Theme.Dark : Theme.Light;

        // 命令台目录加载（加载失败降级为空目录，面板照常构建，不让 GUI 起不来）
        GuiCommandCatalog? catalog = null;
        try { catalog = CommandCatalog.Load(_repoRoot); }
        catch (Exception ex) { Console.Error.WriteLine($"⚠ 命令台目录加载失败（命令台为空）：{ex.Message}"); }
        _cmdCatalog = catalog ?? new GuiCommandCatalog { Groups = { new GuiCommandGroup { Id = "_none", Title = "（目录加载失败）" } } };

        Text = "DeltaUnlimited 控制面板";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1240, 780);
        MinimumSize = new Size(1020, 640);
        Icon = AppIconFactory.Create();

        // ---- 应用上次窗口位置（越界或过小则回退默认居中）----
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

        // ---- 日志窗（右侧主区，Dock.Fill 在 SplitContainer.Panel2）----
        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9.75f),
            BorderStyle = BorderStyle.None,
        };
        _split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        _split.Panel1MinSize = 280;
        _split.Panel2.Controls.Add(_logBox);

        // ---- 状态栏 ----
        _statusStrip = new StatusStrip { SizingGrip = false };
        _statusLabel = new ToolStripStatusLabel("就绪 —— 选择工作流后点开始；急停随时有效");
        _statusStrip.Items.Add(_statusLabel);

        // ---- 暂停面板（半自动人工确认点，非模态：急停按钮始终可用）----
        _pausePanel = new Panel { Dock = DockStyle.Top, Height = 60, Visible = false };
        var abortButton = new Button { Text = "⛔ 中止链路", Dock = DockStyle.Right, Width = 130, Font = new Font(Font.FontFamily, 10f) };
        abortButton.Click += (_, _) => AbortFromPause();
        var contButton = new Button { Text = "▶ 继续", Dock = DockStyle.Right, Width = 110, Font = new Font(Font.FontFamily, 10f) };
        contButton.Click += (_, _) => { PauseGate.Resume(); _pausePanel.Visible = false; };
        _pauseLabel = new Label { Text = "", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font.FontFamily, 10f) };
        _pausePanel.Controls.Add(_pauseLabel);
        _pausePanel.Controls.Add(abortButton);
        _pausePanel.Controls.Add(contButton);

        // ---- 顶部全局栏（最后加入 → 停靠最顶部）----
        _topBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(14, 11, 14, 0), WrapContents = false };
        _titleLabel = new Label { Text = "DeltaUnlimited", AutoSize = true, Margin = new Padding(0, 8, 18, 0), Font = new Font(Font.FontFamily, 12.5f, FontStyle.Bold) };
        _topBar.Controls.Add(_titleLabel);

        _themeButton = new Button { Text = "🌙 深色", Width = 92, Height = 34, Margin = new Padding(0, 1, 14, 0) };
        _themeButton.Click += (_, _) => ToggleTheme();
        _topBar.Controls.Add(_themeButton);

        _topBar.Controls.Add(new Label { Text = "工作流", AutoSize = true, Margin = new Padding(0, 10, 6, 0), Font = new Font(Font.FontFamily, 10f) });
        _workflowBox = new ComboBox { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font(Font.FontFamily, 10f) };
        foreach (var wf in WorkflowCatalog.List(repoRoot)) _workflowBox.Items.Add(wf);
        int defIdx = _workflowBox.Items?.IndexOf("enter_match.json") ?? -1;
        if (defIdx >= 0) _workflowBox.SelectedIndex = defIdx;
        else if ((_workflowBox.Items?.Count ?? 0) > 0) _workflowBox.SelectedIndex = 0;
        if (!string.IsNullOrEmpty(_settings.LastWorkflow) && _workflowBox.Items.Contains(_settings.LastWorkflow))
            _workflowBox.SelectedItem = _settings.LastWorkflow;
        _topBar.Controls.Add(_workflowBox);

        _topBar.Controls.Add(new Label { Text = "模式", AutoSize = true, Margin = new Padding(16, 10, 6, 0), Font = new Font(Font.FontFamily, 10f) });
        _modeBox = new ComboBox { Width = 190, DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font(Font.FontFamily, 10f) };
        _modeBox.Items.AddRange(new object[] { "半自动（暂停点人工确认）", "全自动（跳过暂停点）" });
        _modeBox.SelectedIndex = _settings.AutoMode ? 1 : 0;
        _topBar.Controls.Add(_modeBox);

        _startButton = new Button { Text = "▶ 开始", Width = 120, Height = 34, Margin = new Padding(20, 1, 0, 0), Font = new Font(Font.FontFamily, 10f, FontStyle.Bold) };
        _startButton.Click += (_, _) => StartChain();
        _stopButton = new Button { Text = "⛔ 急停", Width = 104, Height = 34, Enabled = false, Margin = new Padding(8, 1, 0, 0), Font = new Font(Font.FontFamily, 10f, FontStyle.Bold) };
        _stopButton.Click += (_, _) => CommandUtil.RequestStop();
        var clearButton = new Button { Text = "清空日志", Width = 92, Height = 34, Margin = new Padding(8, 1, 0, 0), Font = new Font(Font.FontFamily, 10f) };
        clearButton.Click += (_, _) => { _logBox.Clear(); };
        _topBar.Controls.Add(_startButton);
        _topBar.Controls.Add(_stopButton);
        _topBar.Controls.Add(clearButton);

        // ---- 命令侧栏（左侧）——必须最后构建：PopulateCommands 的事件链会触及顶栏按钮与状态栏 ----
        var cmdSidebar = BuildCommandSidebar();
        _split.Panel1.Controls.Add(cmdSidebar);

        // ---- 加入窗体（停靠处理顺序 = 后加者靠上：顶栏 → 暂停面板 → 主分栏 → 状态栏）----
        Controls.Add(_split);
        Controls.Add(_pausePanel);
        Controls.Add(_topBar);
        Controls.Add(_statusStrip);
        _split.SplitterDistance = 310;

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

        ApplyTheme();
    }

    // ===== 命令侧栏（左右主从的左栏）=====

    /// <summary>构建命令侧栏：分类/命令下拉 → 运行/停止 → 说明 → 参数表单（吃掉剩余高度）→ 命令行预览。
    /// 注意控件创建顺序：先建全部字段再 PopulateCommands（事件链会用到它们）。</summary>
    private TableLayoutPanel BuildCommandSidebar()
    {
        var f = new Font(Font.FontFamily, 10f);
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0) };
        _cmdSidebar = sidebar;
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 分类
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 命令
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));   // 运行/停止
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));   // 说明
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 参数（吃掉剩余高度）
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 命令行预览

        _cmdGroupBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Font = f };
        foreach (var g in _cmdCatalog.Groups) _cmdGroupBox.Items.Add(g.Title);
        if (_cmdGroupBox.Items.Count > 0) _cmdGroupBox.SelectedIndex = 0;
        _cmdGroupBox.SelectedIndexChanged += (_, _) => PopulateCommands();
        sidebar.Controls.Add(_cmdGroupBox, 0, 0);

        _cmdBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Font = f };
        _cmdBox.SelectedIndexChanged += (_, _) => OnCommandSelected();
        sidebar.Controls.Add(_cmdBox, 0, 1);

        var rowBtn = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        _cmdRunButton = new Button { Text = "▶ 运行命令", Width = 128, Height = 34, Margin = new Padding(0, 4, 8, 0), Font = new Font(Font.FontFamily, 10f, FontStyle.Bold) };
        _cmdRunButton.Click += (_, _) => RunCommand();
        rowBtn.Controls.Add(_cmdRunButton);
        _cmdStopButton = new Button { Text = "⛔ 停止", Width = 108, Height = 34, Enabled = false, Margin = new Padding(0, 4, 0, 0), Font = f };
        _cmdStopButton.Click += (_, _) => StopCommand();
        rowBtn.Controls.Add(_cmdStopButton);
        sidebar.Controls.Add(rowBtn, 0, 2);

        // 说明（人话描述 + 用法提示 + 风险警示）
        _cmdDescBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Microsoft YaHei UI", 9.75f) };
        sidebar.Controls.Add(_cmdDescBox, 0, 3);

        // 参数表单（按所选命令动态生成）
        _cmdArgsPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true, BorderStyle = BorderStyle.FixedSingle };
        sidebar.Controls.Add(_cmdArgsPanel, 0, 4);

        // 命令行预览——预览的就是会跑的
        _cmdPreviewBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9.75f), BorderStyle = BorderStyle.FixedSingle };
        sidebar.Controls.Add(_cmdPreviewBox, 0, 5);

        PopulateCommands();
        return sidebar;
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
                var ck = new CheckBox { Text = "--" + a.Name, AutoSize = true, Margin = new Padding(4, 10, 12, 0), Font = new Font(Font.FontFamily, 9.75f) };
                ck.CheckedChanged += (_, _) => UpdateCmdPreview();
                _cmdTips.SetToolTip(ck, a.Desc);
                _cmdArgsPanel.Controls.Add(ck);
            }
            else
            {
                string label = (a.Kind == "flag" ? "--" : "") + a.Name + (a.Required ? "*" : "");
                var lb = new Label { Text = label, AutoSize = true, Margin = new Padding(4, 9, 4, 0), Font = new Font(Font.FontFamily, 9.75f) };
                var tb = new TextBox { Width = 168, PlaceholderText = a.Default, Margin = new Padding(0, 6, 0, 0), Font = new Font(Font.FontFamily, 9.75f) };
                tb.TextChanged += (_, _) => UpdateCmdPreview();
                _cmdTips.SetToolTip(tb, a.Desc + (a.Required ? "（必填）" : "") + (a.Default.Length > 0 ? $"（留空 = {a.Default}）" : ""));
                _cmdArgsPanel.Controls.Add(lb);
                _cmdArgsPanel.Controls.Add(tb);
            }
        }
        UpdateCmdPreview();
        UpdateBusyUi();
        ApplyTheme(); // 动态控件按当前主题上色
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
        _statusLabel.Text = $"▶ 命令运行中：{verb} —— 输出见右侧日志" + (_running ? "（注意：此刻停止会同时急停链路）" : "");
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

    // ===== 主题 =====

    private void ToggleTheme()
    {
        _theme = _theme.IsDark ? Theme.Light : Theme.Dark;
        _settings.DarkTheme = _theme.IsDark;
        _store.SaveGuiSettings(_settings); // 与既有设置写入一致：失败不影响运行
        ApplyTheme();
    }

    private void StyleButton(Button b, bool accent = false, bool danger = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = _theme.Border;
        b.FlatAppearance.MouseOverBackColor = _theme.IsDark ? _theme.Card : Color.FromArgb(233, 237, 242);
        b.BackColor = accent ? _theme.Accent : _theme.Card;
        b.ForeColor = danger ? _theme.Danger : accent ? _theme.AccentText : _theme.Text;
    }

    /// <summary>按"角色"给全部控件上色——换主题只换调色板，结构不动。</summary>
    private void ApplyTheme()
    {
        var t = _theme;
        BackColor = t.Window;

        // 顶栏
        _topBar.BackColor = t.Window;
        foreach (Control c in _topBar.Controls)
        {
            switch (c)
            {
                case Label l: l.ForeColor = l == _titleLabel ? t.Accent : t.Text; break;
                case ComboBox cb: cb.BackColor = t.FieldBack; cb.ForeColor = t.Text; break;
                case Button b: StyleButton(b, accent: b == _startButton, danger: b == _stopButton); break;
            }
        }
        _themeButton.Text = t.IsDark ? "☀ 浅色" : "🌙 深色";

        // 日志窗
        _logBox.BackColor = t.LogBack;
        _logBox.ForeColor = t.LogText;

        // 暂停面板（暖色警示条，深浅各一版）
        _pausePanel.BackColor = t.IsDark ? Color.FromArgb(74, 58, 16) : Color.FromArgb(255, 244, 214);
        _pauseLabel.ForeColor = t.IsDark ? Color.FromArgb(255, 220, 120) : Color.FromArgb(110, 78, 0);
        foreach (Control c in _pausePanel.Controls)
            if (c is Button b) StyleButton(b);

        // 命令侧栏
        _cmdSidebar.BackColor = t.Panel;
        _cmdGroupBox.BackColor = t.FieldBack; _cmdGroupBox.ForeColor = t.Text;
        _cmdBox.BackColor = t.FieldBack; _cmdBox.ForeColor = t.Text;
        _cmdDescBox.BackColor = t.Card; _cmdDescBox.ForeColor = t.Text;
        _cmdArgsPanel.BackColor = t.Card;
        _cmdPreviewBox.BackColor = t.LogBack; _cmdPreviewBox.ForeColor = t.LogText;
        foreach (Control c in _cmdArgsPanel.Controls)
        {
            switch (c)
            {
                case Label l: l.ForeColor = t.Text; break;
                case CheckBox ck: ck.ForeColor = t.Text; break;
                case TextBox tb: tb.BackColor = t.FieldBack; tb.ForeColor = t.Text; break;
            }
        }
        StyleButton(_cmdRunButton, accent: true);
        StyleButton(_cmdStopButton, danger: true);

        // 状态栏
        _statusStrip.BackColor = t.Window;
        _statusLabel.ForeColor = t.Text;
    }

    // ===== 链路执行 =====

    private void StartChain()
    {
        if (_running) return;
        if (_cmdRunning) { _statusLabel.Text = "⚠ 命令还在运行——等它结束或先停止，再开始链路"; return; }
        if (_workflowBox.SelectedItem is not string wf || wf.Length == 0)
        {
            _statusLabel.Text = "⚠ 请先选择工作流";
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
        // 急停并给链路/命令线程 2s 收尾窗口（评审 P3-7：否则按键释放存在微小竞态窗口）
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
