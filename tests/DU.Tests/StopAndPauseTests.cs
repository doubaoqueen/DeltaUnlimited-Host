using DeltaUnlimited.Cli;
using Xunit;

namespace DU.Tests;

/// <summary>协作式急停与暂停点闸门（GUI 与 CLI 共用的纯逻辑，不涉及 WinForms 消息循环）。</summary>
public class StopAndPauseTests
{
    [Fact]
    public async Task PauseGate_Wait_Resume_ReturnsTrue()
    {
        CommandUtil.ResetStop();
        var t = Task.Run(() => PauseGate.Wait("测试暂停"));
        await Task.Delay(200);
        Assert.False(t.IsCompleted, "未放行前应阻塞");
        PauseGate.Resume();
        await t.WaitAsync(TimeSpan.FromSeconds(2)); // 超时会抛 TimeoutException
        Assert.True(await t);
    }

    [Fact]
    public async Task PauseGate_Wait_Interrupt_ReturnsFalse()
    {
        CommandUtil.ResetStop();
        var t = Task.Run(() => PauseGate.Wait("测试暂停"));
        await Task.Delay(200);
        Assert.False(t.IsCompleted, "未放行前应阻塞");
        PauseGate.Interrupt();
        await t.WaitAsync(TimeSpan.FromSeconds(2)); // 超时会抛 TimeoutException
        Assert.False(await t);
    }

    [Fact]
    public void PauseGate_Wait_StopRequested_ShortCircuitsFalse()
    {
        CommandUtil.RequestStop();
        try
        {
            Assert.False(PauseGate.Wait("不应阻塞"));
        }
        finally
        {
            CommandUtil.ResetStop();
        }
    }

    [Fact]
    public void AbortIfStopped_ThrowsChainStoppedException_WhenRequested()
    {
        CommandUtil.ResetStop();
        CommandUtil.RequestStop();
        try
        {
            Assert.Throws<ChainStoppedException>(CommandUtil.AbortIfStopped);
        }
        finally
        {
            CommandUtil.ResetStop();
        }
    }

    [Fact]
    public void AbortIfStopped_NoThrow_WhenNotRequested()
    {
        CommandUtil.ResetStop();
        CommandUtil.AbortIfStopped(); // 不应抛
    }
}
