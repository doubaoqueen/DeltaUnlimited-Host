using DeltaUnlimited.Cli.Commands;
using DeltaUnlimited.Data;
using Xunit;

namespace DU.Tests;

/// <summary>链路校验测试：死步骤可达性分析（评审 P1-1 同步建议）+ enter_match 修复后无死分支钉住。</summary>
public class ChainValidationTests
{
    private static ChainStep Step(string? id, string op, string? jumpTo = null) => new() { Id = id, Op = op, JumpTo = jumpTo };

    [Fact]
    public void JumpOverStep_IsReportedUnreachable()
    {
        // P1-1 死分支形态：jump 越过的步骤既无跳转指向、顺序执行也到不了（原 match_fail/end 即此形态）
        var steps = new List<ChainStep>
        {
            Step("start", "jump", "end"),
            Step("dead", "pause"),
            Step("end", "detect"),
        };
        Assert.Equal(new[] { 1 }, ChainCommand.FindUnreachableSteps(steps));
    }

    [Fact]
    public void FallThroughSteps_AreReachable()
    {
        // v7 汇聚模式回归钉住：分支动作完成后顺序落到 jump 回主循环——不得误报为死步骤
        var steps = new List<ChainStep>
        {
            new() { Id = "start", Op = "switch_screen", Default = "a",
                    Branches = new Dictionary<string, string> { ["some_screen"] = "b" } },
            Step("a", "key"),             // default 分支落到这里（顺序执行）
            Step(null, "jump", "start"),  // a 完成后顺序落到这里
            Step("b", "key"),             // some_screen 分支落到这里
            Step(null, "jump", "start"),  // b 完成后顺序落到这里
            Step("never", "pause"),       // 无人引用、无处顺序可达 → 唯一死步骤
        };
        Assert.Equal(new[] { 5 }, ChainCommand.FindUnreachableSteps(steps));
    }

    [Fact]
    public void EnterMatch_NoDeadSteps_AllTargetsExist()
    {
        // P1-1 修复钉住（2026-09-26 用户确认）：匹配超时人工确认点 match_fail 必须可达，
        // 且所有 jump_to / branches / default 目标必须存在（防再次出现 default→done 之类的死分支）。
        var chain = new DataStore(DataStore.FindRoot()).LoadChain("enter_match.json");
        Assert.Empty(ChainCommand.FindUnreachableSteps(chain.Steps));
        foreach (var s in chain.Steps)
        {
            if (!string.IsNullOrEmpty(s.JumpTo))
                Assert.Contains(chain.Steps, x => x.Id == s.JumpTo);
            if (s.Branches != null)
                foreach (var t in s.Branches.Values)
                    Assert.Contains(chain.Steps, x => x.Id == t);
            if (!string.IsNullOrEmpty(s.Default))
                Assert.Contains(chain.Steps, x => x.Id == s.Default);
        }
    }
}
