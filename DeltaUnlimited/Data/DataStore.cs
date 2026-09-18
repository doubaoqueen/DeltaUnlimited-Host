using System.Text.Json;

namespace DeltaUnlimited.Data;

/// <summary>数据加载器：读取 data/ 与 workflows/ 下的 JSON（UTF-8）。</summary>
public sealed class DataStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _root;

    public DataStore(string root) => _root = root;

    /// <summary>从启动目录向上找项目根。开发期优先找 .git（仓库根）；发布场景回退到随程序复制的 data/elements.json。</summary>
    public static string FindRoot(string? start = null)
    {
        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);

        // 1) 开发期：git 仓库根（避免命中 bin 下复制出来的 data/，保证 screenshots/logs 路径落在仓库内）
        var probe = dir;
        while (probe is not null)
        {
            if (Directory.Exists(Path.Combine(probe.FullName, ".git")))
                return probe.FullName;
            probe = probe.Parent;
        }

        // 2) 发布场景：随程序复制的 data/ 所在目录
        probe = dir;
        while (probe is not null)
        {
            if (File.Exists(Path.Combine(probe.FullName, "data", "elements.json")))
                return probe.FullName;
            probe = probe.Parent;
        }
        throw new DirectoryNotFoundException("找不到 data/elements.json，请从项目根目录运行");
    }

    public T LoadJson<T>(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidDataException($"JSON 为空或解析失败: {relativePath}");
    }

    public ElementsTable LoadElements() => LoadJson<ElementsTable>("data/elements.json");
    public GameOpsTable LoadGameOps() => LoadJson<GameOpsTable>("data/game_ops.json");
    public ItemValuesTable LoadItemValues() => LoadJson<ItemValuesTable>("data/item_values.json");
    public ZeroDamPoints LoadZeroDamPoints() => LoadJson<ZeroDamPoints>("data/zero_dam_points.json");
    public Workflow LoadWorkflow(string file) => LoadJson<Workflow>($"workflows/{file}");
    public RuntimeConfig LoadRuntime() => LoadJson<RuntimeConfig>("data/runtime.json");
    public Chain LoadChain(string file) => LoadJson<Chain>($"workflows/{file}");
    public ScreenTable LoadScreens() => LoadJson<ScreenTable>("data/screens.json");
    public ZoneTable LoadZones() => LoadJson<ZoneTable>("data/zones.json");
}
