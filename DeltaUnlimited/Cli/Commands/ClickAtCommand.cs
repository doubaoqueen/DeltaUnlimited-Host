using DeltaUnlimited.Input;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>clickat：对指定屏幕坐标做拟人化点击（干员头像等无文字元素的点击测试用）。</summary>
public static class ClickAtCommand
{
    public static void Run(string[] cmdArgs)
    {
        if (cmdArgs.Length < 3) throw new ArgumentException("用法: clickat <x> <y>（屏幕坐标，拟人化点击）");
        int x = int.Parse(cmdArgs[1]);
        int y = int.Parse(cmdArgs[2]);
        bool ok = InputService.ClickAt(x, y);
        Console.WriteLine(ok ? "✅ 已点击" : "❌ 点击被安全网拦截（落点校验失败，未点击）");
    }
}
