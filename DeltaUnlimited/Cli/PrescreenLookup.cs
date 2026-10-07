using System.Text;

namespace DeltaUnlimited.Cli;

/// <summary>VLM 初筛结果一行（prescreen.csv 的投影）。红线：这是**参考信息**，永远不是训练标签；
/// 训练标签只由人工在 ailabel 里打（见 docs/AI初筛操作手册.md §0）。</summary>
public sealed record PrescreenRow(
    string Relpath,
    string Scene,
    string RoiUsable,
    string Occlusion,
    string TimeWeather,
    string HasEnemy,
    string HasLootSignal,
    string HasPrompt,
    string Quality,
    string Confidence,
    string Error)
{
    /// <summary>是否成功判定（error 为空）。失败行不显示判定结论，只提示失败原因。</summary>
    public bool Judged => string.IsNullOrWhiteSpace(Error);

    /// <summary>roi_usable 是否为真（CSV 里是 Python 的 True/False 文本）。</summary>
    public bool RoiUsableTrue => RoiUsable.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);

    public bool IsGoldFrame =>
        HasEnemy.Trim().Equals("True", StringComparison.OrdinalIgnoreCase) ||
        HasLootSignal.Trim().Equals("True", StringComparison.OrdinalIgnoreCase) ||
        HasPrompt.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
}

/// <summary>初筛结果查询（record/prescreen.csv → 按 relpath 索引）与标注界面展示文本（纯函数，可单测）。</summary>
public static class PrescreenLookup
{
    public const string CsvRelativePath = "ai-training/datasets/record/prescreen.csv";

    /// <summary>relpath 归一化：分隔符统一为 /、去首尾空白（Windows 上 record_manifest 写的是反斜杠）。</summary>
    public static string Normalize(string? relpath)
        => (relpath ?? "").Trim().Replace('\\', '/');

    /// <summary>加载 prescreen.csv（不存在/读取失败返回空表，绝不影响标注流程）。</summary>
    public static Dictionary<string, PrescreenRow> Load(string repoRoot)
    {
        var map = new Dictionary<string, PrescreenRow>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(repoRoot, CsvRelativePath);
        if (!File.Exists(path)) return map;
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("relpath,", StringComparison.Ordinal)) continue;
                var p = line.Split(',');
                if (p.Length < 11) continue; // 残行跳过（error 里已把逗号替换为中文分号）
                var row = new PrescreenRow(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim(), p[4].Trim(),
                    p[5].Trim(), p[6].Trim(), p[7].Trim(), p[8].Trim(), p[9].Trim(), p[10].Trim());
                map[Normalize(row.Relpath)] = row; // 同 relpath 后行覆盖前行（重筛语义）
            }
        }
        catch
        {
            // 初筛文件坏了不该挡住人工标注
        }
        return map;
    }

    /// <summary>标注界面展示文本（多行）：标题行明确"参考、非标签"，正文给出分流结论与环境元数据。</summary>
    public static string FormatForLabel(PrescreenRow? row)
    {
        var sb = new StringBuilder();
        sb.AppendLine("🤖 VLM 初筛（参考，不是训练标签）");
        if (row is null)
        {
            sb.Append("无初筛记录：该帧未跑 prescreen.py，或结果表里没有它。");
            return sb.ToString();
        }
        if (!row.Judged)
        {
            sb.Append($"初筛失败（error）：{row.Error}").Append('\n');
            sb.Append("→ 下次跑 prescreen.py 会自动重试该帧。");
            return sb.ToString();
        }

        sb.Append(row.RoiUsableTrue ? "roi_usable=✅可用（值得人工看）" : "roi_usable=⛔淘汰（大概率 no_ground 或可直接 X 丢弃）").Append('\n');
        sb.Append($"scene={row.Scene}｜occlusion={row.Occlusion}｜quality={row.Quality}").Append('\n');
        sb.Append($"时段天气={row.TimeWeather}｜conf={row.Confidence}");
        if (row.IsGoldFrame)
        {
            var gold = new List<string>();
            if (row.HasEnemy.Trim().Equals("True", StringComparison.OrdinalIgnoreCase)) gold.Add("敌人");
            if (row.HasLootSignal.Trim().Equals("True", StringComparison.OrdinalIgnoreCase)) gold.Add("物资信号");
            if (row.HasPrompt.Trim().Equals("True", StringComparison.OrdinalIgnoreCase)) gold.Add("交互提示");
            sb.Append($"｜金帧: {string.Join(",", gold)}");
        }
        sb.Append('\n').Append("（VLM 会一本正经地胡说，按自己的判断打标；分歧正是 audit.py 要统计的）");
        return sb.ToString();
    }
}
