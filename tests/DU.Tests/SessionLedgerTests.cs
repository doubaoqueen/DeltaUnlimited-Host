using DeltaUnlimited.Data;
using Xunit;

namespace DU.Tests;

/// <summary>会话限额账本（债1 2026-10）：启动闸/运行闸/崩溃回收/换日重置/不限额语义。</summary>
public class SessionLedgerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"du_session_{Guid.NewGuid():N}.json");

    private static SessionLimits Limits(int minutes) => new() { MaxMinutesPerDay = minutes };

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Begin_Allowed_WhenUnderBudget()
    {
        var t0 = new DateTime(2026, 10, 24, 10, 0, 0);
        using (var l = SessionLedger.TryBegin(_path, Limits(240), t0, out var deny))
        {
            Assert.NotNull(l);
            Assert.Equal("", deny);
            Assert.False(l!.ExceededNow(t0.AddMinutes(10)));
        }
    }

    [Fact]
    public void Complete_Accumulates_AndNextDayResets()
    {
        var t0 = new DateTime(2026, 10, 24, 10, 0, 0);
        using (var l = SessionLedger.TryBegin(_path, Limits(240), t0, out _))
        {
            l!.Complete(t0.AddMinutes(30));
        }

        // 同日再开工：剩余额度变少，但仍在限内
        var t1 = t0.AddHours(2);
        using (var l2 = SessionLedger.TryBegin(_path, Limits(240), t1, out _))
        {
            Assert.NotNull(l2);
            // 30 分钟已累计 + 本段再跑 3.5 小时会爆 → 运行闸触发
            Assert.True(l2!.ExceededNow(t1.AddMinutes(210)));
        }

        // 次日：账本自动清零
        var t2 = new DateTime(2026, 10, 25, 9, 0, 0);
        using (var l3 = SessionLedger.TryBegin(_path, Limits(240), t2, out _))
        {
            Assert.NotNull(l3);
            Assert.False(l3!.ExceededNow(t2.AddMinutes(239)));
        }
    }

    [Fact]
    public void OverBudget_DeniesBegin_WithHumanReason()
    {
        var t0 = new DateTime(2026, 10, 24, 10, 0, 0);
        using (var l = SessionLedger.TryBegin(_path, Limits(60), t0, out _))
        {
            l!.Complete(t0.AddMinutes(61));   // 超额收工
        }

        var denied = SessionLedger.TryBegin(_path, Limits(60), t0.AddMinutes(5), out var deny);
        Assert.Null(denied);
        Assert.Contains("上限", deny);
        Assert.Contains("max_minutes_per_day", deny);   // 告诉用户去哪改
    }

    [Fact]
    public void CrashRecovery_DanglingOpenCounted()
    {
        // 模拟进程被杀：账本里留着未收尾的 OpenStart——下次开工必须把这段计入，不白给额度
        var t0 = new DateTime(2026, 10, 24, 10, 0, 0);
        File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(
            new SessionLedger.State
            {
                Date = "2026-10-24",
                AccumulatedMs = 0,
                OpenStart = t0.ToString("o"),
            }));

        var t1 = t0.AddMinutes(59);
        using (var l2 = SessionLedger.TryBegin(_path, Limits(60), t1, out _))
        {
            Assert.NotNull(l2);                                    // 59 < 60，还能开工
            // 悬账 59 分钟 + 本段 2 分钟 = 爆 60 分钟限额 → 运行闸触发
            Assert.True(l2!.ExceededNow(t1.AddMinutes(2)));
        }
    }

    [Fact]
    public void ZeroLimit_NeverDenies()
    {
        var t0 = new DateTime(2026, 10, 24, 10, 0, 0);
        using (var l = SessionLedger.TryBegin(_path, Limits(0), t0, out _))
        {
            Assert.NotNull(l);
            Assert.False(l!.ExceededNow(t0.AddHours(48)));   // 0 = 不限，永不到闸
        }
    }

    [Fact]
    public void CorruptStateFile_TreatedAsFreshLedger()
    {
        File.WriteAllText(_path, "{ 这不是合法 json");
        var t0 = new DateTime(2026, 10, 24, 10, 0, 0);
        using (var l = SessionLedger.TryBegin(_path, Limits(240), t0, out _))
        {
            Assert.NotNull(l);   // 状态坏了不挡死：当新账本开工
        }
    }
}
