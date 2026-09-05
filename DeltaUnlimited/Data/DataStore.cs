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

    /// <summary>从启动目录向上找项目根（含 data/elements.json 的目录）。</summary>
    public static string FindRoot(string? start = null)
    {
        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "data", "elements.json")))
                return dir.FullName;
            dir = dir.Parent;
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
}
