using System.Text.Json;

namespace DeltaUnlimited.Gui;

/// <summary>工作流清单：扫描 workflows/*.json（纯函数，无 UI 依赖，可单测）。</summary>
public static class WorkflowCatalog
{
    /// <summary>返回链路格式的工作流文件名（按名称排序）；目录不存在/无文件返回空列表。
    /// 只列出顶层含 steps 数组的文件：节点图格式（如 demo_smoke.json）进 chain 引擎会空跑成功、
    /// 解析失败的文件也无法执行，一律不进清单（评审 P1-2）。</summary>
    public static IReadOnlyList<string> List(string repoRoot)
    {
        var dir = Path.Combine(repoRoot, "workflows");
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.json")
            .Where(IsChainFormat)
            .Select(Path.GetFileName)
            .Where(f => !string.IsNullOrEmpty(f))
            .Select(f => f!)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>文件是否可被 chain 引擎执行：合法 JSON 且顶层含 steps 数组成员。</summary>
    private static bool IsChainFormat(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("steps", out var steps)
                && steps.ValueKind == JsonValueKind.Array;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false; // 读不了/解析不了的文件不可能被 chain 加载执行，不进清单
        }
    }
}
