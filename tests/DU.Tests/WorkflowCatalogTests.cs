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
            File.WriteAllText(Path.Combine(wfDir, "b.json"), "{}");
            File.WriteAllText(Path.Combine(wfDir, "a.json"), "{}");
            File.WriteAllText(Path.Combine(wfDir, "note.txt"), "x");
            var list = WorkflowCatalog.List(root);
            Assert.Equal(new[] { "a.json", "b.json" }, list);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
