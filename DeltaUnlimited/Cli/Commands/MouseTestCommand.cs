using DeltaUnlimited.Input;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>mousetest：从当前光标拟人化移动到指定屏幕坐标（不点击），供肉眼验收轨迹用。可多次运行观察随机性。</summary>
public static class MouseTestCommand
{
    public static void Run(string[] cmdArgs)
    {
        if (cmdArgs.Length < 3) throw new ArgumentException("用法: mousetest <x> <y>（屏幕坐标，不点击）");
        int x = int.Parse(cmdArgs[1]);
        int y = int.Parse(cmdArgs[2]);
        InputService.MoveHumanized(x, y);
    }
}
