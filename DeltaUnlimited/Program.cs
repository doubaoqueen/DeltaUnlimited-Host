using DeltaUnlimited.Data;
using Vanara.PInvoke;

Console.WriteLine("✅ DeltaUnlimited BotHost 启动");
var desktopHwnd = User32.GetDesktopWindow();
Console.WriteLine($"桌面句柄：{desktopHwnd}");

// ===== Phase 0: 数据层冒烟（验证 JSON schema 反序列化，schema 与 Python 工作区一致）=====
var store = new DataStore(DataStore.FindRoot());

var elements = store.LoadElements();
Console.WriteLine($"[elements.json] 元素识别表: {elements.Elements.Count} 个");
foreach (var (name, def) in elements.Elements)
    Console.WriteLine($"  - {name}: strategy={def.Strategy}");

var ops = store.LoadGameOps();
Console.WriteLine($"[game_ops.json] 按键 {ops.KeyMap.Count} 个, 复合动作 {ops.Actions.Count} 个");
Console.WriteLine($"  interact -> {ops.KeyMap.GetValueOrDefault("interact", "?")}");

var items = store.LoadItemValues();
Console.WriteLine($"[item_values.json] 材料 {items.Materials.Count} 种");

var points = store.LoadZeroDamPoints();
Console.WriteLine($"[zero_dam_points.json] 撤离点 {points.ExtractPoints.Count}, 搜刮点 {points.LootPoints.Count}");

var wf = store.LoadWorkflow("demo_smoke.json");
Console.WriteLine($"[demo_smoke.json] '{wf.Name}': {wf.Nodes.Count} 节点, {wf.Edges.Count} 边");

Console.WriteLine("\nPhase 0 数据层验证完成 ✅  按回车退出...");
_ = Console.ReadLine();
