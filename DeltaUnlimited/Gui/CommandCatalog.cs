using System.Text.Json;
using DeltaUnlimited.Data;

namespace DeltaUnlimited.Gui;

/// <summary>GUI 命令台目录：加载/校验 data/gui_commands.json（纯逻辑，无 UI 依赖，可单测）。
/// BuildArgs/BuildPreview 是纯函数——把表单值变成命令行 token，运行与预览共用同一份构建，
/// 保证"预览的就是会跑的"。</summary>
public static class CommandCatalog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly string[] ValidKinds = { "arg", "argq", "flag", "switch" };

    public static GuiCommandCatalog Load(string repoRoot)
    {
        string path = Path.Combine(repoRoot, "data", "gui_commands.json");
        if (!File.Exists(path)) throw new FileNotFoundException("找不到命令台目录 data/gui_commands.json", path);
        var cat = JsonSerializer.Deserialize<GuiCommandCatalog>(File.ReadAllText(path), JsonOpts)
                  ?? throw new InvalidOperationException("gui_commands.json 解析为空");
        Validate(cat);
        return cat;
    }

    /// <summary>结构性校验：schema/分组引用/动词与参数合法/(动词+固定参数)不重复。
    /// 动词是否真实存在于 CliDispatcher.KnownVerbs 属于"数据与代码同步"，由测试钉住，不在运行时校验。</summary>
    public static void Validate(GuiCommandCatalog cat)
    {
        if (cat.SchemaVersion != 1) throw new InvalidOperationException($"不支持的 gui_commands schema 版本: {cat.SchemaVersion}");
        if (cat.Groups.Count == 0) throw new InvalidOperationException("gui_commands.json 没有任何分组");
        var groupIds = new HashSet<string>();
        foreach (var g in cat.Groups)
        {
            if (string.IsNullOrWhiteSpace(g.Id) || string.IsNullOrWhiteSpace(g.Title))
                throw new InvalidOperationException("分组存在空的 id/title");
            if (!groupIds.Add(g.Id)) throw new InvalidOperationException($"分组 id 重复: {g.Id}");
        }
        var seen = new HashSet<string>();
        foreach (var c in cat.Commands)
        {
            if (string.IsNullOrWhiteSpace(c.Verb)) throw new InvalidOperationException("存在 verb 为空的命令");
            if (!groupIds.Contains(c.Group)) throw new InvalidOperationException($"命令 {c.Verb} 引用了不存在的分组 {c.Group}");
            if (string.IsNullOrWhiteSpace(c.Desc)) throw new InvalidOperationException($"命令 {c.Verb} 缺少说明");
            string key = c.Verb + "|" + string.Join(' ', c.FixedArgs);
            if (!seen.Add(key)) throw new InvalidOperationException($"命令重复: {key}");
            foreach (var a in c.Args)
            {
                if (string.IsNullOrWhiteSpace(a.Name)) throw new InvalidOperationException($"命令 {c.Verb} 存在无名参数");
                if (!ValidKinds.Contains(a.Kind)) throw new InvalidOperationException($"命令 {c.Verb} 参数 {a.Name} 种类非法: {a.Kind}");
            }
        }
        if (cat.Commands.Count == 0) throw new InvalidOperationException("gui_commands.json 没有任何命令");
    }

    /// <summary>把表单值构建成命令行 token（不含 exe 本身）。values/switches 与 def.Args 按顺序一一对应
    /// （UI 侧按控件类型分流收集）。留空取 Default；Required 且最终仍为空 → 抛 ArgumentException。</summary>
    public static string[] BuildArgs(GuiCommandDef def, IReadOnlyList<string> values, IReadOnlyList<bool> switches)
    {
        var tokens = new List<string> { def.Verb };
        tokens.AddRange(def.FixedArgs);
        int vi = 0, si = 0;
        foreach (var a in def.Args)
        {
            if (a.Kind == "switch")
            {
                if (si < switches.Count && switches[si++]) tokens.Add("--" + a.Name);
                continue;
            }
            string v = vi < values.Count ? values[vi++].Trim() : "";
            if (v.Length == 0) v = (a.Default ?? "").Trim();
            if (v.Length == 0)
            {
                if (a.Required) throw new ArgumentException($"参数「{a.Name}」必填——填上再运行");
                continue;
            }
            if (a.Kind == "flag") tokens.Add("--" + a.Name);
            // arg：允许多 token（如 ocr 的多个关键词）；argq：整体一个 token（时间戳）
            if (a.Kind == "argq") tokens.Add(v);
            else tokens.AddRange(v.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        return tokens.ToArray();
    }

    /// <summary>命令行预览（含空格的 token 加引号）——"预览的就是会跑的"。</summary>
    public static string BuildPreview(IReadOnlyList<string> tokens)
        => "DeltaUnlimited.exe " + string.Join(' ', tokens.Select(Quote));

    private static string Quote(string token) => token.Contains(' ') ? $"\"{token}\"" : token;
}
