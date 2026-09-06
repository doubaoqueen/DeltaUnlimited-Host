using DeltaUnlimited.Cli;
using DeltaUnlimited.Input;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>turn：视角转动测试（帧差验证）。</summary>
public static class TurnCommand
{
    public static void Run(string repoRoot, string[] cmdArgs)
    {
        if (cmdArgs.Length < 2) throw new ArgumentException("用法: turn <dx> [dy] [窗口关键字(默认 三角洲行动)]");
        int dx = int.Parse(cmdArgs[1]);
        int dy = 0;
        int idx = 2;
        if (cmdArgs.Length > 2 && int.TryParse(cmdArgs[2], out int parsedDy))
        {
            dy = parsedDy;
            idx = 3;
        }
        string winKeyword = cmdArgs.Length > idx ? cmdArgs[idx] : "三角洲行动";
        InputProbe.Run(repoRoot, winKeyword, $"视角转动 ({dx}, {dy})", () => InputService.MoveRelative(dx, dy));
    }
}
