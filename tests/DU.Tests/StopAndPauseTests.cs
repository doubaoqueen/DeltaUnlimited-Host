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
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPauseRequested(string message) => paused.TrySetResult();
        PauseGate.PauseRequested += OnPauseRequested;
        try
        {
            var t = Task.Run(() => PauseGate.Wait("测试暂停"));
            // 等 Wait 真正建好闸门再放行：原 Task.Delay(200) 在线程池饥饿时会与 Resume 竞态（评审 P2-7）
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(t.IsCompleted, "未放行前应阻塞");
            PauseGate.Resume();
            await t.WaitAsync(TimeSpan.FromSeconds(2)); // 超时会抛 TimeoutException
            Assert.True(await t);
        }
        finally
        {
            PauseGate.PauseRequested -= OnPauseRequested;
        }
    }

    [Fact]
    public async Task PauseGate_Wait_Interrupt_ReturnsFalse()
    {
        CommandUtil.ResetStop();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPauseRequested(string message) => paused.TrySetResult();
        PauseGate.PauseRequested += OnPauseRequested;
        try
        {
            var t = Task.Run(() => PauseGate.Wait("测试暂停"));
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(2)); // 同上：事件同步代替延时（评审 P2-7）
            Assert.False(t.IsCompleted, "未放行前应阻塞");
            PauseGate.Interrupt();
            await t.WaitAsync(TimeSpan.FromSeconds(2)); // 超时会抛 TimeoutException
            Assert.False(await t);
        }
        finally
        {
            PauseGate.PauseRequested -= OnPauseRequested;
        }
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
