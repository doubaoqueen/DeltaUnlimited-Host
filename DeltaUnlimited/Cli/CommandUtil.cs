using DeltaUnlimited.Data;
using DeltaUnlimited.Input;

namespace DeltaUnlimited.Cli;

/// <summary>CLI 公共工具：急停处理、键位解析。</summary>
public static class CommandUtil
{
    /// <summary>注册 Ctrl+C 急停：先释放所有按键再退出（防止按键卡死）。</summary>
    public static void InstallEmergencyStop()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            InputService.ReleaseAllHeldKeys();
            Console.WriteLine("\n⛔ 手动急停，已释放所有按键");
            Environment.Exit(130);
        };
    }

    /// <summary>语义键名 → 实际按键（查 game_ops 的按键映射；查不到则按原样返回）。</summary>
    public static string ResolveKey(DataStore data, string semantic)
        => data.LoadGameOps().KeyMap.GetValueOrDefault(semantic, semantic);
}
