using DeltaUnlimited.Cli;
using DeltaUnlimited.Data;
using DeltaUnlimited.Input;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>hold：长按测试（帧差验证）。</summary>
public static class HoldCommand
{
    public static void Run(DataStore data, string repoRoot, string[] cmdArgs)
    {
        if (cmdArgs.Length < 3) throw new ArgumentException("用法: hold <键名或语义键> <毫秒> [窗口关键字(默认 三角洲行动)]");
        string keyArg = cmdArgs[1];
        if (!int.TryParse(cmdArgs[2], out int ms) || ms <= 0) throw new ArgumentException("时长需为正整数毫秒");
        string winKeyword = cmdArgs.Length > 3 ? cmdArgs[3] : "三角洲行动";

        string key = CommandUtil.ResolveKey(data, keyArg);
        InputProbe.Run(repoRoot, winKeyword, $"长按 {keyArg} → {key} {ms}ms", () => InputService.HoldKey(key, ms));
    }
}
