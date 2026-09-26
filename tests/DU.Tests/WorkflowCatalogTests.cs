using DeltaUnlimited.Data;
using DeltaUnlimited.Gui;
using Xunit;

namespace DU.Tests;

/// <summary>GUI 纯逻辑测试：工作流目录扫描（不涉及 WinForms 消息循环）。</summary>
public class WorkflowCatalogTests
{
    [Fact]
    public void List_NonexistentRoot_ReturnsEmpty()
        => Assert.Empty(WorkflowCatalog.List(Path.Combine(Path.GetTempPath(), "du_no_such_root_" + Guid.NewGuid().ToString("N"))));

    [Fact]
    public void List_OnlyJsonSorted_IgnoresOtherFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "du_wf_root_" + Guid.NewGuid().ToString("N"));
        string wfDir = Path.Combine(root, "workflows");
        Directory.CreateDirectory(wfDir);
        try
        {
            File.WriteAllText(Path.Combine(wfDir, "b.json"), """{ "name": "b", "steps": [ { "op": "wait" } ] }""");
            File.WriteAllText(Path.Combine(wfDir, "a.json"), """{ "name": "a", "steps": [ { "op": "wait" } ] }""");
            File.WriteAllText(Path.Combine(wfDir, "note.txt"), "x");
            var list = WorkflowCatalog.List(root);
            Assert.Equal(new[] { "a.json", "b.json" }, list);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void List_FiltersOutNodeGraphFormat_OnlyChainFormatListed()
    {
        // 评审 P1-2 回归钉住：节点图格式（无 steps 数组）的 demo_smoke.json 不得进清单——
        // 否则 GUI 选中后 Chain.Steps 反序列化为空列表，ValidateChain 空转通过，主循环直接跳过还打印"链路执行完成"（假成功）。
        string root = Path.Combine(Path.GetTempPath(), "du_wf_root_" + Guid.NewGuid().ToString("N"));
        string wfDir = Path.Combine(root, "workflows");
        Directory.CreateDirectory(wfDir);
        try
        {
            File.WriteAllText(Path.Combine(wfDir, "demo_smoke.json"),
                """{ "name": "引擎冒烟演示", "nodes": { "n1": { "type": "Log" } }, "edges": [ { "from": "n1", "to": "n1" } ] }""");
            File.WriteAllText(Path.Combine(wfDir, "broken.json"), "{ not valid json");
            File.WriteAllText(Path.Combine(wfDir, "steps_not_array.json"), """{ "name": "x", "steps": "oops" }""");
            File.WriteAllText(Path.Combine(wfDir, "enter_match.json"),
                """{ "name": "进场链路", "steps": [ { "op": "wait" } ] }""");
            var list = WorkflowCatalog.List(root);
            Assert.Equal(new[] { "enter_match.json" }, list); // 只留链路格式，节点图/坏 JSON/steps 非数组一律过滤
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void List_RealRepo_DemoSmokeNotListed()
    {
        // 真实仓库钉住（评审 P1-2 + P3-9）：仓库 workflows/ 里的 demo_smoke.json（节点图格式）不应出现在 GUI 清单，
        // 而真正的链路 enter_match.json 必须仍在。
        var list = WorkflowCatalog.List(DataStore.FindRoot());
        Assert.DoesNotContain("demo_smoke.json", list);
        Assert.Contains("enter_match.json", list);
    }
}
