using DeltaUnlimited.Cli;
using DeltaUnlimited.Data;
using DeltaUnlimited.Gui;
using Xunit;

namespace DU.Tests;

/// <summary>GUI 命令台测试：目录数据契约（data/gui_commands.json）与命令行构建纯逻辑。
/// 关键防线：目录里的动词必须真实存在于 CliDispatcher.KnownVerbs（数据与代码同步）。</summary>
public class CommandCatalogTests
{
    private static GuiCommandCatalog LoadReal() => CommandCatalog.Load(DataStore.FindRoot());

    [Fact]
    public void Load_RealRepo_ParsesAndValidates()
    {
        var cat = LoadReal();
        Assert.Equal(1, cat.SchemaVersion);
        Assert.True(cat.Groups.Count >= 5);
        Assert.True(cat.Commands.Count >= 20);
    }

    [Fact]
    public void Load_RealRepo_AllVerbsExistInDispatcher()
    {
        var cat = LoadReal();
        foreach (var c in cat.Commands)
        {
            Assert.Contains(c.Verb, CliDispatcher.KnownVerbs);
            Assert.Contains(c.Group, cat.Groups.Select(g => g.Id));
            Assert.False(string.IsNullOrWhiteSpace(c.Desc));
        }
    }

    [Fact]
    public void Load_RealRepo_InputCommandsAreExclusive()
    {
        // 会动键鼠的命令必须标 Exclusive——否则链路运行期间可被调起，键鼠打架
        var cat = LoadReal();
        foreach (var verb in new[] { "click", "hold", "turn", "drill", "patrol", "autorun_walk", "airecord", "aicollect", "clickat", "mousetest", "click_operator" })
        {
            var def = cat.Commands.First(c => c.Verb == verb);
            Assert.True(def.Exclusive, $"{verb} 应标记 Exclusive");
        }
    }

    [Fact]
    public void Load_RealRepo_ChainOverlayGuiExcluded()
    {
        // chain 有 GUI 顶栏专属入口；overlay 是无急停检查点的常驻逻辑；gui 是面板自身——均不进命令台
        var verbs = LoadReal().Commands.Select(c => c.Verb).ToList();
        Assert.DoesNotContain("chain", verbs);
        Assert.DoesNotContain("overlay", verbs);
        Assert.DoesNotContain("gui", verbs);
    }

    [Fact]
    public void Load_RealRepo_SmartArgsWired()
    {
        // 参数智能化：元素名=联想下拉、图片路径=文件选择器、干员类型=固定下拉
        var cat = LoadReal();
        var annotate = cat.Commands.First(c => c.Verb == "annotate");
        Assert.Equal("elements", annotate.Args.First(a => a.Name == "元素名").Source);
        Assert.Equal("file", annotate.Args.First(a => a.Name == "截图路径").Source);
        var clickOperator = cat.Commands.First(c => c.Verb == "click_operator");
        Assert.Contains("突击", clickOperator.Args.First(a => a.Name == "类型").Options);
        var autorun = cat.Commands.First(c => c.Verb == "autorun_walk");
        Assert.Equal("extracts", autorun.Args.First(a => a.Name == "extract").Source);
    }

    [Fact]
    public void Validate_BadSource_Throws()
    {
        var cat = new GuiCommandCatalog
        {
            Groups = { new GuiCommandGroup { Id = "g", Title = "G" } },
            Commands = { new GuiCommandDef { Verb = "v", Group = "g", Desc = "d",
                Args = { new GuiCommandArg { Name = "a", Kind = "arg", Source = "bogus" } } } },
        };
        Assert.Throws<InvalidOperationException>(() => CommandCatalog.Validate(cat));
    }

    [Fact]
    public void BuildArgs_PositionalSkipEmpty_UsesDefault_RequiredThrows()
    {
        var def = new GuiCommandDef
        {
            Verb = "v", Group = "g", Desc = "d",
            Args =
            {
                new GuiCommandArg { Name = "a", Kind = "arg", Required = true },
                new GuiCommandArg { Name = "b", Kind = "arg", Default = "B" },
                new GuiCommandArg { Name = "c", Kind = "arg" },
            },
        };
        Assert.Equal(new[] { "v", "X", "B" }, CommandCatalog.BuildArgs(def, new[] { "X", "", "" }, Array.Empty<bool>()));
        Assert.Throws<ArgumentException>(() => CommandCatalog.BuildArgs(def, new[] { "", "", "" }, Array.Empty<bool>()));
    }

    [Fact]
    public void BuildArgs_FlagSplitsValue_SwitchAddsBare()
    {
        var def = new GuiCommandDef
        {
            Verb = "v", Group = "g", Desc = "d",
            Args =
            {
                new GuiCommandArg { Name = "region", Kind = "flag" },
                new GuiCommandArg { Name = "only-usable", Kind = "switch" },
            },
        };
        Assert.Equal(new[] { "v", "--region", "10", "20" }, CommandCatalog.BuildArgs(def, new[] { "10 20" }, new[] { false }));
        Assert.Equal(new[] { "v", "--only-usable" }, CommandCatalog.BuildArgs(def, new[] { "" }, new[] { true }));
        Assert.Equal(new[] { "v" }, CommandCatalog.BuildArgs(def, new[] { "" }, new[] { false }));
    }

    [Fact]
    public void BuildArgs_Argq_KeepsSingleToken()
    {
        var def = new GuiCommandDef
        {
            Verb = "localizetrack", Group = "g", Desc = "d",
            Args = { new GuiCommandArg { Name = "t1", Kind = "argq", Required = true } },
        };
        Assert.Equal(new[] { "localizetrack", "2026-10-12 00:28:20" },
            CommandCatalog.BuildArgs(def, new[] { "2026-10-12 00:28:20" }, Array.Empty<bool>()));
    }

    [Fact]
    public void BuildPreview_QuotesTokensWithSpaces()
        => Assert.Equal("DeltaUnlimited.exe v \"a b\"", CommandCatalog.BuildPreview(new[] { "v", "a b" }));

    [Fact]
    public void KnownVerbs_ExactSyncWithSwitch()
    {
        // 与 CliDispatcher 的 switch 同步防线：26 个动词不多不少（新增命令必须同时补两处）
        string[] expected =
        {
            "smoke", "annotate", "capture", "click", "hold", "turn", "drill", "patrol",
            "aicollect", "airecord", "ailabel", "roitune", "aiprobe", "autorun_walk", "aieval",
            "chain", "crop", "ocr", "tplprobe", "localize", "localizetrack", "overlay",
            "mousetest", "clickat", "click_operator", "windows",
        };
        Assert.Equal(expected.OrderBy(x => x), CliDispatcher.KnownVerbs.OrderBy(x => x));
    }
}
