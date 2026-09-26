using System.Threading;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;

namespace DeltaUnlimited.Cli;

/// <summary>CLI 公共工具：协作式急停、暂停点闸门、键位解析。</summary>
public static class CommandUtil
{
    /// <summary>急停标志（volatile 跨线程可见）。链路/巡逻/训练循环在每个步骤边界与轮询点检查。</summary>
    public static volatile bool StopRequested;

    /// <summary>请求急停：置标志 + 释放所有按键 + 打断暂停点等待（GUI 急停按钮/Ctrl+C 共用）。</summary>
    public static void RequestStop()
    {
        StopRequested = true;
        InputService.ReleaseAllHeldKeys();
        PauseGate.Interrupt();
    }

    /// <summary>清急停标志（链路启动前调用）。</summary>
    public static void ResetStop() => StopRequested = false;

    /// <summary>急停检查点：已请求急停则抛 ChainStoppedException（由命令层统一收尾）。</summary>
    public static void AbortIfStopped()
    {
        if (StopRequested) throw new ChainStoppedException();
    }

    /// <summary>注册 Ctrl+C 急停：释放所有按键 + 置标志（不再 Environment.Exit——让链路/循环优雅收尾，进程自然退出）。</summary>
    public static void InstallEmergencyStop()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            RequestStop();
            Console.WriteLine("\n⛔ 手动急停：已释放所有按键，正在收尾…");
        };
    }

    /// <summary>暂停点处理器：非空时（GUI 注入 PauseGate.Wait）由 GUI 负责放行；空则用控制台回车轮询（CLI 默认）。</summary>
    public static Func<string, bool>? PauseHandler { get; set; }

    /// <summary>人工确认点：返回 true=继续，false=急停。CLI：回车继续、Ctrl+C 急停（轮询式，可被急停打断）。</summary>
    public static bool WaitForResume(string message)
    {
        if (PauseHandler is not null) return PauseHandler(message);
        Console.WriteLine($"  ⏸ {message} —— 按回车继续（Ctrl+C 急停）");
        while (!StopRequested)
        {
            bool enter = false;
            try
            {
                enter = Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Enter;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                // stdin 重定向/无控制台环境（如 GUI 未注入 PauseHandler）没有可读键盘：只能等急停（评审 P3）
            }
            if (enter) return true;
            Thread.Sleep(60);
        }
        return false;
    }

    /// <summary>语义键名 → 实际按键（查 game_ops 的按键映射；查不到则按原样返回）。</summary>
    public static string ResolveKey(DataStore data, string semantic)
        => data.LoadGameOps().KeyMap.GetValueOrDefault(semantic, semantic);
}

/// <summary>GUI 暂停点闸门：链路线程阻塞等待，UI 线程点"继续/中止"放行。
/// 事件 PauseRequested 在链路线程上触发（GUI 订阅后转到 UI 线程显示暂停面板）。</summary>
public static class PauseGate
{
    private static readonly object Lock = new();
    private static ManualResetEventSlim? _resume;
    private static bool _resumeResult;

    /// <summary>链路线程请求暂停（message=暂停原因），GUI 订阅后显示暂停面板。</summary>
    public static event Action<string>? PauseRequested;

    /// <summary>链路线程调用：阻塞直到 Resume()（继续）或 Interrupt()（中止/急停）；返回是否继续。</summary>
    public static bool Wait(string message)
    {
        lock (Lock)
        {
            if (CommandUtil.StopRequested) return false; // 已在等待前被急停
            _resume = new ManualResetEventSlim(false);
        }
        PauseRequested?.Invoke(message);
        _resume.Wait();
        return _resumeResult;
    }

    /// <summary>放行：继续执行。</summary>
    public static void Resume()
    {
        lock (Lock)
        {
            _resumeResult = true;
            _resume?.Set();
        }
    }

    /// <summary>放行：中止（急停路径也调用）。</summary>
    public static void Interrupt()
    {
        lock (Lock)
        {
            _resumeResult = false;
            _resume?.Set();
        }
    }
}
