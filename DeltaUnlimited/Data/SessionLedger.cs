using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>会话限额账本（技术债修复 2026-10）：让 runtime.json 的 session_limits 从"愿望"变成"执法"。
/// 语义：每日累计自动化时长上限（MaxMinutesPerDay，0=不限）；状态持久化 data/session_state.json，跨重启生效。
/// 崩溃安全：Begin 时把上一次未 Complete 的 OpenStart 一并计入（进程被杀不白给额度）。
/// 用法：链路启动前 <see cref="TryBegin"/>（null=已超额，拒绝启动）；运行中 <see cref="ExceededNow"/> 作急停信号之一；
/// 结束 <see cref="Complete"/>（或 using 出界自动 Complete）。纯时间参数注入，无隐藏时钟，可单测。</summary>
public sealed class SessionLedger : IDisposable
{
    public sealed class State
    {
        [JsonPropertyName("date")] public string? Date { get; set; }              // yyyy-MM-dd（换日重置）
        [JsonPropertyName("accumulated_ms")] public long AccumulatedMs { get; set; }
        [JsonPropertyName("open_start")] public string? OpenStart { get; set; }   // ISO 8601；非空=上次未收尾
    }

    private readonly string _path;
    private readonly long _budgetMs;      // 0 = 不限
    private readonly DateTime _openStart;
    private long _accumulatedMs;
    private bool _closed;

    private SessionLedger(string path, long budgetMs, long accumulatedMs, DateTime openStart)
    {
        _path = path;
        _budgetMs = budgetMs;
        _accumulatedMs = accumulatedMs;
        _openStart = openStart;
    }

    public static string DefaultPath(string repoRoot) => Path.Combine(repoRoot, "data", "session_state.json");

    /// <summary>尝试开工：超额返回 null（denyReason 给出人话），否则返回在账账本并落盘 OpenStart。
    /// 状态文件日期早于今天 → 视为新的一天，累计清零。</summary>
    public static SessionLedger? TryBegin(
        string statePath, SessionLimits limits, DateTime now, out string denyReason)
    {
        long budgetMs = Math.Max(0, limits.MaxMinutesPerDay) * 60_000L;

        var st = Load(statePath);
        if (st.Date != now.ToString("yyyy-MM-dd"))
        {
            st = new State { Date = now.ToString("yyyy-MM-dd"), AccumulatedMs = 0 };
        }

        // 崩溃回收：上一次没 Complete 的时长照样计入（防"杀进程白嫖额度"）
        if (st.OpenStart is { } openStr && DateTime.TryParse(openStr, out var prevOpen))
        {
            st.AccumulatedMs += Math.Max(0, (long)(now - prevOpen).TotalMilliseconds);
            st.OpenStart = null;
        }

        if (budgetMs > 0 && st.AccumulatedMs >= budgetMs)
        {
            denyReason = $"今日自动化时长已达上限（累计 {st.AccumulatedMs / 60_000.0:F0} 分钟 ≥ "
                         + $"{limits.MaxMinutesPerDay} 分钟，账本 {statePath}）。明天再来，或手动改 runtime.json 的 "
                         + "session_limits.max_minutes_per_day（风险自担）。";
            return null;
        }

        st.OpenStart = now.ToString("o");
        Save(statePath, st);
        denyReason = "";
        return new SessionLedger(statePath, budgetMs, st.AccumulatedMs, now);
    }

    /// <summary>运行中闸：本段运行时长叠加累计后是否已超额（不限额恒 false）。接进急停检查即可优雅收尾。</summary>
    public bool ExceededNow(DateTime now)
        => _budgetMs > 0 && _accumulatedMs + ElapsedSinceOpen(now) >= _budgetMs;

    /// <summary>收尾：本段时长入账并清 OpenStart。幂等（重复调用只记一次）。</summary>
    public void Complete(DateTime now)
    {
        if (_closed) return;
        _closed = true;
        var st = Load(_path);
        if (st.OpenStart is { } openStr && DateTime.TryParse(openStr, out var open))
            st.AccumulatedMs += Math.Max(0, (long)(now - open).TotalMilliseconds);
        st.OpenStart = null;
        Save(_path, st);
    }

    public void Dispose() => Complete(DateTime.Now);

    private long ElapsedSinceOpen(DateTime now) => Math.Max(0, (long)(now - _openStart).TotalMilliseconds);

    private static State Load(string path)
    {
        if (!File.Exists(path)) return new State();
        try
        {
            return JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State();
        }
        catch (JsonException)
        {
            return new State();   // 状态文件损坏 → 当作新账本（宁可少记不可挡死）
        }
    }

    private static void Save(string path, State st)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));
    }
}
