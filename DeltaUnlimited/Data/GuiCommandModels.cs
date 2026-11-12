namespace DeltaUnlimited.Data;

/// <summary>GUI 命令台的数据契约（data/gui_commands.json）：把 CLI 动词包装成"人话分类 + 参数表单"，
/// 让不懂命令行的人也能安全调起诊断与工具。纯数据模型，加载/校验在 Gui.CommandCatalog。</summary>
public sealed class GuiCommandCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<GuiCommandGroup> Groups { get; set; } = new();
    public List<GuiCommandDef> Commands { get; set; } = new();
}

/// <summary>命令分组（功能划分的展示单元）：Id 供命令引用，Title 是界面上显示的人话名。</summary>
public sealed class GuiCommandGroup
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>单条命令定义：Verb 必须是 CliDispatcher.KnownVerbs 之一；Exclusive=true 表示会动键鼠/抢占游戏，
/// 链路运行期间禁止调起（避免键鼠打架）；Stoppable=false 表示无急停检查点的常驻逻辑。</summary>
public sealed class GuiCommandDef
{
    public string Verb { get; set; } = "";
    public string Group { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Note { get; set; } = "";
    public bool Exclusive { get; set; }
    public bool Stoppable { get; set; } = true;
    /// <summary>固定前缀参数（如 tplprobe --live 实时模式的 --live），始终出现在动词之后。</summary>
    public List<string> FixedArgs { get; set; } = new();
    public List<GuiCommandArg> Args { get; set; } = new();
}

/// <summary>单个参数：Kind=arg（位置参数，按空格可拆多 token）/ argq（单 token 位置参数，含空格时整体引号包裹，
/// 用于时间戳这类"一段是一个值"的输入）/ flag（--名称 值）/ switch（布尔开关，无值）。
/// Default 是留空时的取值；Required=true 且留空会在运行前被拦下。</summary>
public sealed class GuiCommandArg
{
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Default { get; set; } = "";
    public string Kind { get; set; } = "arg";
    public bool Required { get; set; }
}
