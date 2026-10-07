using System.Text.Encodings.Web;
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

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文说明字段不转义
    };

    public const string GuiSettingsPath = "data/gui_settings.json";

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

    /// <summary>宽容加载（本机设置类）：文件缺失/损坏/json 为空都返回 fallback，绝不打断启动流程。</summary>
    public T LoadJsonOrDefault<T>(string relativePath, T fallback) where T : class
    {
        try
        {
            var path = Path.Combine(_root, relativePath);
            if (!File.Exists(path)) return fallback;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>写入 data/ 下的 JSON（本机设置类；失败不抛——设置写不进不该影响使用）。</summary>
    public bool SaveJson<T>(string relativePath, T value)
    {
        try
        {
            var path = Path.Combine(_root, relativePath);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(value, WriteOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>GUI 本地设置（缺失/损坏 → 默认值）。</summary>
    public GuiSettings LoadGuiSettings() => LoadJsonOrDefault(GuiSettingsPath, new GuiSettings());

    /// <summary>保存 GUI 本地设置（失败静默）。</summary>
    public bool SaveGuiSettings(GuiSettings settings) => SaveJson(GuiSettingsPath, settings);

    public ElementsTable LoadElements() => LoadJson<ElementsTable>("data/elements.json");
    public GameOpsTable LoadGameOps() => LoadJson<GameOpsTable>("data/game_ops.json");
    public ItemValuesTable LoadItemValues() => LoadJson<ItemValuesTable>("data/item_values.json");
    public ZeroDamPoints LoadZeroDamPoints() => LoadJson<ZeroDamPoints>("data/zero_dam_points.json");
    public LoadoutPresetTable LoadLoadoutPresets() => LoadJson<LoadoutPresetTable>("data/loadout_preset.json");
    public ItemCatalogTable LoadItemCatalog() => LoadJson<ItemCatalogTable>("data/item_catalog.json");
    public OperatorPresetTable LoadOperatorPresets() => LoadJson<OperatorPresetTable>("data/operator_presets.json");
    public AiVisionConfig LoadAiVision() => LoadJson<AiVisionConfig>("data/ai_vision.json");
    public Workflow LoadWorkflow(string file) => LoadJson<Workflow>($"workflows/{file}");
    public RuntimeConfig LoadRuntime() => LoadJson<RuntimeConfig>("data/runtime.json");
    public Chain LoadChain(string file) => LoadJson<Chain>($"workflows/{file}");
    public ScreenTable LoadScreens() => LoadJson<ScreenTable>("data/screens.json");
    public ZoneTable LoadZones() => LoadJson<ZoneTable>("data/zones.json");
}
