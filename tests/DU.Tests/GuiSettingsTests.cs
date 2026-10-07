using DeltaUnlimited.Data;
using Xunit;

namespace DU.Tests;

/// <summary>GUI 本机设置（data/gui_settings.json）的宽容加载与往返：缺失/损坏一律回退默认，不影响启动。</summary>
public class GuiSettingsTests
{
    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "du_gui_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        string root = NewRoot();
        try
        {
            var s = new DataStore(root).LoadGuiSettings();
            Assert.Null(s.LastWorkflow);
            Assert.False(s.AutoMode);
            Assert.Equal(1, s.SchemaVersion);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        string root = NewRoot();
        try
        {
            var store = new DataStore(root);
            var s = new GuiSettings
            {
                LastWorkflow = "enter_match.json",
                AutoMode = true,
                WindowX = 120, WindowY = 80, WindowW = 1180, WindowH = 760,
            };
            Assert.True(store.SaveGuiSettings(s));

            var back = store.LoadGuiSettings();
            Assert.Equal("enter_match.json", back.LastWorkflow);
            Assert.True(back.AutoMode);
            Assert.Equal(120, back.WindowX);
            Assert.Equal(760, back.WindowH);
            Assert.Contains("last_workflow", File.ReadAllText(Path.Combine(root, "data", "gui_settings.json")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        string root = NewRoot();
        try
        {
            string dir = Path.Combine(root, "data");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "gui_settings.json"), "{ 这不是合法 JSON ");

            var s = new DataStore(root).LoadGuiSettings();
            Assert.Null(s.LastWorkflow);   // 损坏 → 默认值，不抛异常
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
