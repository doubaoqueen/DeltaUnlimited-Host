using System.Runtime.InteropServices;

namespace DeltaUnlimited.Cli;

/// <summary>WinExe 发布版（release 的双击日常版）从 cmd 调起 CLI 命令时没有控制台——
/// 接回父进程控制台让输出可见。Exe 开发构建（dotnet run）已附着控制台，再调用会失败但无害；
/// 输出被重定向（管道）时句柄已有效，不碰。</summary>
public static class ConsoleAttach
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static void TryAttachParent()
    {
        if (Console.IsOutputRedirected) return; // 管道/重定向：句柄已有效
        try { AttachConsole(AttachParentProcess); } catch { /* 已附着/无父控制台：忽略 */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);
}
