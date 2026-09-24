using DeltaUnlimited.Data;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>smoke：Phase 0 数据层冒烟。</summary>
public static class SmokeCommand
{
    public static void Run(DataStore data)
    {
        Console.WriteLine("✅ DeltaUnlimited BotHost 启动");

        var elements = data.LoadElements();
        Console.WriteLine($"[elements.json] 元素识别表: {elements.Elements.Count} 个");
        foreach (var (name, def) in elements.Elements)
            Console.WriteLine($"  - {name}: strategy={def.Strategy}");

        var runtime = data.LoadRuntime();
        Console.WriteLine($"[runtime.json] 90°px={runtime.PxPer90Deg} 窗口关键字={runtime.WindowKeyword}");

        var ops = data.LoadGameOps();
        Console.WriteLine($"[game_ops.json] 按键 {ops.KeyMap.Count} 个, 复合动作 {ops.Actions.Count} 个");
        Console.WriteLine($"  interact -> {ops.KeyMap.GetValueOrDefault("interact", "?")}");

        var items = data.LoadItemValues();
        Console.WriteLine($"[item_values.json] 材料 {items.Materials.Count} 种");

        var catalog = data.LoadItemCatalog();
        Console.WriteLine($"[item_catalog.json] 物品 {catalog.Items.Count} 种");

        var presets = data.LoadLoadoutPresets();
        Console.WriteLine($"[loadout_preset.json] 预设 {presets.Presets.Count} 套, 策略 source={presets.Policy.Source} max_tier={presets.Policy.MaxTier}");

        var points = data.LoadZeroDamPoints();
        Console.WriteLine($"[zero_dam_points.json] 撤离点 {points.ExtractPoints.Count}, 搜刮点 {points.LootPoints.Count}");

        var wf = data.LoadWorkflow("demo_smoke.json");
        Console.WriteLine($"[demo_smoke.json] '{wf.Name}': {wf.Nodes.Count} 节点, {wf.Edges.Count} 边");

        Console.WriteLine("\nPhase 0 数据层验证完成 ✅");
        Console.WriteLine("提示：dotnet run -- annotate depart_button  可跑静态视觉冒烟");
    }
}
