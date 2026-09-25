namespace DeltaUnlimited.Gui;

/// <summary>工作流清单：扫描 workflows/*.json（纯函数，无 UI 依赖，可单测）。</summary>
public static class WorkflowCatalog
{
    /// <summary>返回工作流文件名（按名称排序）；目录不存在/无文件返回空列表。</summary>
    public static IReadOnlyList<string> List(string repoRoot)
    {
        var dir = Path.Combine(repoRoot, "workflows");
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.json")
            .Select(Path.GetFileName)
            .Where(f => !string.IsNullOrEmpty(f))
            .Select(f => f!)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
